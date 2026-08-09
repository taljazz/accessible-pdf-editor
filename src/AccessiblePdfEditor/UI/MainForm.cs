using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Auditing;
using AccessiblePdfEditor.Editing;
using AccessiblePdfEditor.Ingestion;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Navigation;
using AccessiblePdfEditor.Persistence;
using AccessiblePdfEditor.Rendering;

namespace AccessiblePdfEditor.UI;

// =====================================================================================
//  MainForm.cs
//
//  The main window: the menu, the reading surface, and the keyboard.
//
//  The reading surface is a READ-ONLY text box, and that choice does most of the
//  accessibility work in this application. Because it is a real Windows text box, the
//  screen reader's own review cursor, Say All, braille tracking, selection and copy all
//  work without this program doing anything. Because it is read-only, single letters can
//  be claimed as navigation keys without ever swallowing something the user meant to type
//  — which is exactly how browse mode works in NVDA, so the keys are already familiar.
//
//  This file holds the window and its keyboard. The commands the menus and keys invoke
//  live in MainForm.Actions.cs, so that neither file becomes too long to read.
// =====================================================================================

#region MainForm — window, menu and keyboard

/// <summary>The application's main window.</summary>
public sealed partial class MainForm : AccessibleFormBase
{
    #region Services and state

    private readonly IDocumentLoader _loader;
    private readonly IDocumentSaver _saver;
    private readonly NavigationService _navigation;
    private readonly SearchService _search;
    private readonly AccessibilityAuditor _auditor;
    private readonly ReaderSettings _settings;

    private PdfDocumentModel? _document;
    private EditHistory? _history;
    private RenderedDocument? _rendered;
    private AccessibilityReport? _lastReport;

    private TextBox _documentView = null!;
    private StatusStrip _statusStrip = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private MenuStrip _menu = null!;

    private readonly PageRenderer _renderer = new();
    private SplitContainer _split = null!;
    private PageViewControl _pageView = null!;

    /// <summary>Suppresses caret-tracking while this program is the one moving the caret.</summary>
    private bool _movingCaret;

    public MainForm(
        ISpeechService speech,
        IAudioCueService cues,
        IDocumentLoader loader,
        IDocumentSaver saver,
        ReaderSettings settings)
        : base(speech, cues)
    {
        _loader = loader;
        _saver = saver;
        _settings = settings;
        _navigation = new NavigationService();
        _search = new SearchService();
        _auditor = new AccessibilityAuditor();

        Size = new Size(1000, 720);
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = true;
        MaximizeBox = true;
        ShowInTaskbar = true;
    }

    #endregion

    #region Window identity

    protected override string WindowTitle =>
        _document is null
            ? "Accessible PDF Editor"
            : $"{_document.BuildStatusLine()} — Accessible PDF Editor";

    protected override string WindowPurpose =>
        _document is null
            ? "No document is open. Press Control plus O to open one, or F1 for the list of keys."
            : "Press F1 for the list of keys.";

    /// <summary>The main window is not closed by Escape; Escape is for leaving dialogs.</summary>
    protected override bool CloseOnEscape => false;

    protected override string BuildOpeningAnnouncement()
    {
        var parts = new List<string>(4)
        {
            "Accessible PDF Editor.",
        };

        // The user is told at once whether speech and sound are actually working. Silent failure
        // here would leave them unable to tell a broken program from a broken document.
        if (!Speech.IsSpeechAvailable)
        {
            parts.Add("No screen reader or speech engine was found, so nothing will be spoken. " +
                      "Everything is still shown on screen.");
        }
        else if (Speech.DetectedScreenReader is { Length: > 0 } reader)
        {
            parts.Add($"Speaking through {reader}.");
        }

        if (!Cues.IsAvailable)
            parts.Add("No audio device, so there will be no sounds. Everything is still announced.");

        if (!PdfSharpEnvironment.CanDrawText)
            parts.Add(PdfSharpEnvironment.FontFailureReason ?? "New text cannot be added to documents.");

        parts.Add("No document is open. Press Control plus O to open one, or F1 for the list of keys.");

        return string.Join(" ", parts);
    }

    #endregion

    #region Layout
    // Docked so it follows the system DPI and any font size the user has chosen, rather than being
    // laid out at fixed pixel positions that would clip at large text sizes.

    protected override void BuildContent()
    {
        _menu = BuildMenu();

        _documentView = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = true,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", _settings.TextSizePoints),

            // The whole point of the design: a real text box, named so the screen reader
            // introduces it properly when focus arrives.
            AccessibleName = "Document text",
            AccessibleDescription =
                "The document, in reading order. Use your screen reader's usual reading commands, " +
                "or press H for the next heading, F for the next form field, K for the next link.",
            AccessibleRole = AccessibleRole.Text,

            // Read-only, but must still take focus and a caret, or none of the review commands work.
            TabStop = true,
            HideSelection = false,
            Text = "No document is open. Press Control plus O to open one.",
        };

        _documentView.KeyDown += OnDocumentViewKeyDown;
        _documentView.KeyPress += OnDocumentViewKeyPress;
        _documentView.MouseUp += (_, _) => TrackCaretToNavigation();

        _statusLabel = new ToolStripStatusLabel
        {
            Text = "Ready",
            AccessibleName = "Status",
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _statusStrip = new StatusStrip { AccessibleName = "Status bar" };
        _statusStrip.Items.Add(_statusLabel);

        _pageView = new PageViewControl(_renderer) { Dock = DockStyle.Fill };

        // A split, with the text on the left. The picture starts COLLAPSED: the primary user
        // cannot use it, and a pane that is not needed should not be taking up half the window or
        // sitting in the tab order until somebody asks for it.
        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Panel2Collapsed = !_settings.ShowPagePicture,
            AccessibleName = "Document",
        };

        // Panel1 holds BOTH reading surfaces — the browse view and the text box — with one visible
        // at a time. See MainForm.Browse.cs for why there are two.
        _split.Panel1.Controls.Add(BuildReadingHost());
        _split.Panel1.AccessibleName = "Document";

        _split.Panel2.Controls.Add(_pageView);
        _split.Panel2.AccessibleName = "Page picture";

        Controls.Add(_split);
        Controls.Add(_statusStrip);
        Controls.Add(_menu);

        MainMenuStrip = _menu;

        // Set once the window has a real width; setting it before then throws, because a splitter
        // distance has to fit inside a size that does not exist yet.
        Shown += (_, _) => ApplySplitPosition();
    }

    /// <summary>Puts the splitter at a sensible place for the current window size.</summary>
    private void ApplySplitPosition()
    {
        if (_split.Panel2Collapsed || _split.Width <= 0)
            return;

        try
        {
            _split.SplitterDistance = Math.Max(200, (int)(_split.Width * 0.52));
        }
        catch (InvalidOperationException)
        {
            // A window too narrow to split. Not worth reporting; the panes simply stay as they are.
        }
    }

    protected override void FocusFirstControl() => ReadingSurface.Focus();

    #endregion

    #region Menu
    // A real MenuStrip, reachable with Alt and navigable with the arrow keys. Every command in the
    // program is here as well as on a shortcut, because a keyboard shortcut only helps someone who
    // already knows it exists, and a menu is how you find out.

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip { AccessibleName = "Main menu" };

        menu.Items.Add(BuildMenuItem("&File",
            ("&Open…\tCtrl+O", (_, _) => OpenDocument()),
            ("&Save\tCtrl+S", (_, _) => SaveDocument(saveAs: false)),
            ("Save &As…\tCtrl+Shift+S", (_, _) => SaveDocument(saveAs: true)),
            (null, null),
            ("Document &properties…\tCtrl+D", (_, _) => ShowDocumentProperties()),
            (null, null),
            ("E&xit\tAlt+F4", (_, _) => Close())));

        menu.Items.Add(BuildMenuItem("&Edit",
            ("&Undo\tCtrl+Z", (_, _) => Undo()),
            ("&Redo\tCtrl+Y", (_, _) => Redo()),
            (null, null),
            ("List &changes made\tCtrl+H", (_, _) => AnnounceChangeHistory())));

        menu.Items.Add(BuildMenuItem("&Comment",
            ("&New comment here\tCtrl+Shift+M", (_, _) => AddCommentHere()),
            ("&Highlight this, with a note\tCtrl+Shift+K", (_, _) => AddCommentHere(AnnotationKind.Highlight)),
            (null, null),
            ("&Change this comment\tF2", (_, _) => EditCommentHere()),
            ("&Reply to this comment\tCtrl+Shift+Y", (_, _) => ReplyToCommentHere()),
            ("&Delete this comment\tCtrl+Delete", (_, _) => DeleteCommentHere()),
            (null, null),
            ("Next comment\tA", (_, _) => Navigate(NavigationGranularity.Annotation, MoveDirection.Next)),
            ("&List all comments…\tCtrl+M", (_, _) => ShowAnnotations())));

        menu.Items.Add(BuildMenuItem("&Read",
            ("&Where am I\tCtrl+W", (_, _) => AnnouncePosition()),
            ("Read &current item\tCtrl+R", (_, _) => AnnounceCurrent()),
            ("Repeat last announcement\tCtrl+Space", (_, _) => Speech.RepeatLast()),
            (null, null),
            ("&Find…\tCtrl+F", (_, _) => FindText()),
            ("Find &next\tF3", (_, _) => FindNext(forward: true)),
            ("Find &previous\tShift+F3", (_, _) => FindNext(forward: false)),
            (null, null),
            ("&Verbosity: cycle\tCtrl+Shift+V", (_, _) => CycleVerbosity()),
            (null, null),
            ("Switch between &browse view and text view\tCtrl+Shift+B", (_, _) => ToggleBrowseView()),
            ("Show the page &picture\tCtrl+Shift+R", (_, _) => TogglePagePicture()),
            ("Switch between document and picture\tF6", (_, _) => SwitchPane()),
            ("&Bigger\tCtrl+plus", (_, _) => AdjustZoom(larger: true)),
            ("&Smaller\tCtrl+minus", (_, _) => AdjustZoom(larger: false))));

        menu.Items.Add(BuildMenuItem("&Go",
            ("Next &heading\tH", (_, _) => Navigate(NavigationGranularity.Heading, MoveDirection.Next)),
            ("Previous heading\tShift+H", (_, _) => Navigate(NavigationGranularity.Heading, MoveDirection.Previous)),
            ("Next &link\tK", (_, _) => Navigate(NavigationGranularity.Link, MoveDirection.Next)),
            ("Next form &field\tF", (_, _) => Navigate(NavigationGranularity.FormField, MoveDirection.Next)),
            ("Next field needing an answer\tShift+F", (_, _) => Navigate(NavigationGranularity.UnfilledFormField, MoveDirection.Next)),
            ("Next &table\tT", (_, _) => Navigate(NavigationGranularity.Table, MoveDirection.Next)),
            ("&Open this table in a grid…\tCtrl+Shift+T", (_, _) => OpenTableView()),
            ("Next &graphic\tG", (_, _) => Navigate(NavigationGranularity.Figure, MoveDirection.Next)),
            (null, null),
            ("Next pa&ge\tCtrl+PageDown", (_, _) => Navigate(NavigationGranularity.Page, MoveDirection.Next)),
            ("Previous page\tCtrl+PageUp", (_, _) => Navigate(NavigationGranularity.Page, MoveDirection.Previous)),
            ("Go to &page…\tCtrl+G", (_, _) => GoToPagePrompt()),
            ("&Bookmarks…\tCtrl+B", (_, _) => ShowBookmarks()),
            ("List of l&inks…\tCtrl+K", (_, _) => ShowLinks())));

        menu.Items.Add(BuildMenuItem("Fi&x",
            ("Check &accessibility\tCtrl+Shift+A", (_, _) => RunAudit()),
            ("Fix problems &one by one…\tCtrl+Shift+F", (_, _) => StartGuidedRemediation()),
            (null, null),
            ("Describe this &image…\tCtrl+Shift+I", (_, _) => DescribeCurrentFigure()),
            ("&Label this field…\tCtrl+Shift+L", (_, _) => LabelCurrentField()),
            ("Set &heading level…\tCtrl+Shift+H", (_, _) => SetHeadingLevelPrompt()),
            ("Mark as page f&urniture\tCtrl+Shift+U", (_, _) => MarkCurrentAsArtifact()),
            (null, null),
            ("Set document &language…", (_, _) => SetLanguagePrompt()),
            ("Set document &title…", (_, _) => SetTitlePrompt())));

        menu.Items.Add(BuildMenuItem("&Tools",
            ("&Fill in this form…\tCtrl+Shift+P", (_, _) => ShowFormFillDialog()),
            ("&Sign this document…\tCtrl+Shift+G", (_, _) => SignDocument()),
            (null, null),
            ("&Clear this form…", (_, _) => ResetForm()),
            ("Save a f&lattened copy…", (_, _) => SaveFlattenedCopy()),
            (null, null),
            ("List &comments…\tCtrl+M", (_, _) => ShowAnnotations()),
            (null, null),
            ("Se&ttings…", (_, _) => ShowSettings())));

        menu.Items.Add(BuildMenuItem("&Help",
            ("&Read the keys aloud\tF1", (_, _) => ShowHelp()),
            ("&Keys in a window I can browse…\tShift+F1", (_, _) => ShowBrowsableHelp()),
            (null, null),
            ("&About", (_, _) => ShowAbout())));

        return menu;
    }

    private static ToolStripMenuItem BuildMenuItem(
        string text, params (string? Text, EventHandler? Handler)[] entries)
    {
        var item = new ToolStripMenuItem(text)
        {
            AccessibleName = text.Replace("&", string.Empty),
        };

        foreach (var (entryText, handler) in entries)
        {
            if (entryText is null || handler is null)
            {
                item.DropDownItems.Add(new ToolStripSeparator());
                continue;
            }

            var child = new ToolStripMenuItem(entryText)
            {
                AccessibleName = entryText.Replace("&", string.Empty).Split('\t')[0],
            };

            child.Click += handler;
            item.DropDownItems.Add(child);
        }

        return item;
    }

    #endregion

    #region The keyboard
    // Single letters navigate, exactly as they do in NVDA's browse mode. They are safe to claim
    // because the document view is read-only, and they are the SAME LETTERS NVDA uses, so someone
    // who reads web pages already knows them and does not have to learn a second set.

    private void OnDocumentViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_document is null)
            return;

        bool shift = e.Shift;
        var direction = shift ? MoveDirection.Previous : MoveDirection.Next;

        // Anything with Control or Alt belongs to the menu shortcuts, not to quick navigation.
        if (e.Control || e.Alt)
            return;

        NavigationGranularity? granularity = e.KeyCode switch
        {
            Keys.H => NavigationGranularity.Heading,
            Keys.K => NavigationGranularity.Link,
            Keys.F => shift ? NavigationGranularity.UnfilledFormField : NavigationGranularity.FormField,
            Keys.T => NavigationGranularity.Table,
            Keys.G => NavigationGranularity.Figure,
            Keys.L => NavigationGranularity.List,
            Keys.I => NavigationGranularity.ListItem,
            Keys.P => NavigationGranularity.Paragraph,
            Keys.A => NavigationGranularity.Annotation,
            Keys.D => NavigationGranularity.AccessibilityIssue,
            Keys.C => NavigationGranularity.TableCell,
            _ => null,
        };

        // Digits jump to a heading level, matching NVDA. Pressing 2 repeatedly walks the level-two
        // headings, which is how you move through a document's sections.
        if (e.KeyCode is >= Keys.D1 and <= Keys.D6)
        {
            var level = (HeadingLevel)(e.KeyCode - Keys.D0);
            Navigate(NavigationGranularity.HeadingAtLevel, direction, level);
            e.Handled = e.SuppressKeyPress = true;
            return;
        }

        if (granularity is { } unit)
        {
            if (unit == NavigationGranularity.AccessibilityIssue)
                GoToNextIssue(direction);
            else
                Navigate(unit, direction);

            e.Handled = e.SuppressKeyPress = true;
            return;
        }

        switch (e.KeyCode)
        {
            // Enter activates whatever the position is on: follows a link, edits a field, reads a
            // comment. One key, whatever is under it.
            case Keys.Enter:
                ActivateCurrent();
                e.Handled = e.SuppressKeyPress = true;
                break;

            // Space toggles a checkbox, matching Windows convention everywhere else.
            case Keys.Space when _navigation.Current is Model.Forms.CheckBoxFormField:
                ActivateCurrent();
                e.Handled = e.SuppressKeyPress = true;
                break;

            case Keys.PageDown when e.Modifiers == Keys.None:
                Navigate(NavigationGranularity.Page, MoveDirection.Next);
                e.Handled = e.SuppressKeyPress = true;
                break;

            case Keys.PageUp when e.Modifiers == Keys.None:
                Navigate(NavigationGranularity.Page, MoveDirection.Previous);
                e.Handled = e.SuppressKeyPress = true;
                break;
        }
    }

    /// <summary>
    /// Swallows the character that a claimed navigation key would otherwise produce. Without this,
    /// Windows plays the default beep on every keypress in a read-only text box, which over a
    /// reading session is maddening.
    /// </summary>
    private void OnDocumentViewKeyPress(object? sender, KeyPressEventArgs e)
    {
        if (_document is not null && !char.IsControl(e.KeyChar))
            e.Handled = true;
    }

    protected override bool HandleShortcut(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.O: OpenDocument(); return true;
            case Keys.Control | Keys.S: SaveDocument(saveAs: false); return true;
            case Keys.Control | Keys.Shift | Keys.S: SaveDocument(saveAs: true); return true;
            case Keys.Control | Keys.Z: Undo(); return true;
            case Keys.Control | Keys.Y: Redo(); return true;
            case Keys.Control | Keys.W: AnnouncePosition(); return true;
            case Keys.Control | Keys.R: AnnounceCurrent(); return true;
            case Keys.Control | Keys.F: FindText(); return true;
            case Keys.F3: FindNext(forward: true); return true;
            case Keys.Shift | Keys.F3: FindNext(forward: false); return true;
            case Keys.Control | Keys.G: GoToPagePrompt(); return true;
            case Keys.Control | Keys.B: ShowBookmarks(); return true;
            case Keys.Control | Keys.K: ShowLinks(); return true;
            case Keys.Control | Keys.M: ShowAnnotations(); return true;
            case Keys.Control | Keys.D: ShowDocumentProperties(); return true;
            case Keys.Control | Keys.H: AnnounceChangeHistory(); return true;
            case Keys.Control | Keys.Shift | Keys.A: RunAudit(); return true;
            case Keys.Control | Keys.Shift | Keys.F: StartGuidedRemediation(); return true;
            case Keys.Control | Keys.Shift | Keys.I: DescribeCurrentFigure(); return true;
            case Keys.Control | Keys.Shift | Keys.L: LabelCurrentField(); return true;
            case Keys.Control | Keys.Shift | Keys.H: SetHeadingLevelPrompt(); return true;
            case Keys.Control | Keys.Shift | Keys.U: MarkCurrentAsArtifact(); return true;
            case Keys.Control | Keys.Shift | Keys.P: ShowFormFillDialog(); return true;
            case Keys.Control | Keys.Shift | Keys.G: SignDocument(); return true;
            case Keys.Control | Keys.Shift | Keys.T: OpenTableView(); return true;

            // Writing comments. Ctrl+M lists them, so Ctrl+Shift+M writes one — the same
            // relationship the other list-and-act pairs use.
            case Keys.Control | Keys.Shift | Keys.M: AddCommentHere(); return true;
            case Keys.Control | Keys.Shift | Keys.K: AddCommentHere(AnnotationKind.Highlight); return true;
            case Keys.F2: EditCommentHere(); return true;
            case Keys.Control | Keys.Shift | Keys.Y: ReplyToCommentHere(); return true;
            case Keys.Control | Keys.Delete: DeleteCommentHere(); return true;
            case Keys.Control | Keys.Shift | Keys.V: CycleVerbosity(); return true;

            case Keys.Control | Keys.Shift | Keys.R: TogglePagePicture(); return true;
            case Keys.Control | Keys.Shift | Keys.B: ToggleBrowseView(); return true;
            case Keys.F6: SwitchPane(); return true;

            // Previous and next page. Plain Page Up and Page Down do this too, but only where
            // nothing else has claimed them: a screen reader in browse mode binds them to its own
            // cursor and this program never sees them (NVDA's cursorManager.py maps them to
            // moveByPage). The Control versions are bound nowhere in NVDA, so they always arrive,
            // and this is the pair to reach for while reading in the browse view.
            case Keys.Control | Keys.PageDown:
                Navigate(NavigationGranularity.Page, MoveDirection.Next);
                return true;

            case Keys.Control | Keys.PageUp:
                Navigate(NavigationGranularity.Page, MoveDirection.Previous);
                return true;

            // The zoom keys every other program uses. A partially-sighted user reaches for these
            // by reflex, and having them do nothing would send them hunting through menus. They
            // zoom whichever pane has focus, which is what a user expects of a zoom key.
            case Keys.Control | Keys.Oemplus:
            case Keys.Control | Keys.Add:
                AdjustZoom(larger: true);
                return true;

            case Keys.Control | Keys.OemMinus:
            case Keys.Control | Keys.Subtract:
                AdjustZoom(larger: false);
                return true;

            default: return false;
        }
    }

    /// <summary>
    /// The key list, spoken on F1. Grouped by what the user is trying to do rather than by
    /// modifier, because "how do I get to the next heading" is the question, not "what does
    /// Control do".
    /// </summary>
    protected override string BuildKeyHelp() =>
        "Keys. " +
        "There are two reading views, and Control plus Shift plus B switches between them. In the " +
        "browse view the document is a web page, so your screen reader's own commands do the " +
        "moving, including Control plus Alt plus the arrow keys inside a table. In the text view " +
        "the keys below are this program's own. " +
        "Moving through the document in the text view: H for the next heading, Shift H for the previous one, " +
        "1 to 6 for a heading at that level, K for a link, F for a form field, Shift F for the next " +
        "field still needing an answer, T for a table, G for a graphic, L for a list, P for a " +
        "paragraph, A for a comment, D for the next accessibility problem. Add Shift to any of " +
        "those to go backwards. Page Up and Page Down move between pages, and Control plus Page Up " +
        "and Control plus Page Down do the same while reading in the browse view. " +
        "Enter activates whatever you are on. " +
        "Reading: your screen reader's own reading commands work here as they do anywhere else. " +
        "Control W says where you are, Control R re-reads the current item, Control Space repeats " +
        "the last announcement. " +
        "Files: Control O opens, Control S saves, Control Shift S saves as. " +
        "Commenting: Control Shift M writes a comment on whatever you are on, Control Shift K " +
        "highlights it, F2 changes the comment you are on, Control Shift Y replies to it, and " +
        "Control Delete deletes it after reading it back. " +
        "Fixing: Control Shift A checks accessibility, Control Shift F walks through the problems " +
        "one at a time, Control Shift I describes an image, Control Shift L labels a form field. " +
        "Control Z undoes and says what it undid. " +
        "F1 repeats this list, and Shift plus F1 opens it in a window you can read through at your " +
        "own pace.";

    /// <summary>
    /// The same list, laid out to be READ. One key per line, grouped under headings, with the key
    /// first so that arrowing down the list gives "H, next heading" rather than a sentence that
    /// has to be heard to the end before the key arrives.
    ///
    /// The section headings are plain words on their own lines rather than markup: this is a text
    /// box, and anything decorative would be read out as punctuation.
    /// </summary>
    protected override string BuildBrowsableHelp() =>
        """
        ACCESSIBLE PDF EDITOR — KEYS

        Everything here is also on the menus, which you can reach with Alt.


        THE TWO WAYS OF READING

        There are two reading views, and which one you are in decides whose keys do
        the moving.

        Ctrl+Shift+B switch between them

        BROWSE VIEW is the normal one. The document is presented as a web page, so
        your screen reader reads it in browse mode and ALL OF ITS OWN COMMANDS WORK,
        exactly as they do on any web page:

           H, T, K, F, G, L, D and the rest, as your screen reader defines them
           Ctrl+Alt+arrow keys to move around inside a table
           NVDA+F7 for the element list
           Say All, the review cursor, find, and your braille display

        Nothing in this program overrides them, and nothing has to be learned twice.
        Each page of the document is a landmark, so D moves a page at a time.

        Ctrl+Page Down  next page
        Ctrl+Page Up    previous page

        Use the Control versions while reading here. Plain Page Up and Page Down
        also move a page when nothing else has claimed them, but a screen reader
        in browse mode binds them to its own cursor and takes them first, so this
        program never sees them. The Control versions are not bound by NVDA and
        always arrive. Either way the reading cursor is moved to the new page, not
        just the view.

        TEXT VIEW is the fallback. The document is one long piece of text in an
        ordinary text box. Your reading commands still work, but a text box has no
        structure, so this program provides the navigation keys itself — the single
        letters listed below. Use it if you prefer it, or if the browse view is not
        available on this computer.

        The browse view needs the Microsoft Edge WebView2 runtime, which comes with
        Windows 11 and with Edge. If it is missing, the text view is used and the
        program says so.


        MOVING THROUGH THE DOCUMENT — TEXT VIEW

        These keys are this program's own, and they work in the TEXT VIEW. In the
        browse view your screen reader's equivalents do the same job better, so
        these are deliberately not claimed there. Hold Shift with any of them to go
        backwards instead of forwards.

        H            next heading
        1 to 6       next heading at that level
        K            next link
        F            next form field
        Shift+F      next form field that still needs an answer
        T            next table
        G            next graphic or image
        L            next list
        P            next paragraph
        A            next comment
        D            next accessibility problem
        C            next table cell
        I            next list item

        Page Down    next page
        Page Up      previous page
        Ctrl+PageDown / Ctrl+PageUp do the same, and also work in the browse view

        Enter        activate whatever you are on: follow a link, fill in a
                     field, read a comment, or open a table in a grid
        Space        tick or clear a checkbox


        TABLES

        In the BROWSE VIEW a table is a real table, so your screen reader's own
        table commands work on it — Ctrl+Alt+arrow keys to move between cells,
        with each cell announced together with its row and column headings. This
        is the way to read a table, and it needs nothing from this program.

        In the TEXT VIEW a table is laid out as text, because a text box cannot
        express a table. There, open it in a grid instead:

        T            next table
        Ctrl+Shift+T open the table you are in as a real grid
        Enter        the same, when you are on a table or one of its cells

        Inside the grid, the arrow keys move between cells — as in any grid in
        Windows — and Ctrl+R reads the cell you are on with its headings.
        Ctrl+H there marks the first row as headings if the document has none.
        Escape returns you to the document.

        Note: your screen reader's Ctrl+Alt+arrow table commands are a feature
        of documents rather than of grids, so they do not apply inside that grid.
        That is exactly why the browse view exists.


        READING

        Your screen reader's own reading commands work here exactly as they do in
        any other text box, including Say All, the review cursor, and your braille
        display. Nothing below replaces them; they answer questions your screen
        reader cannot.

        Ctrl+W       say where you are: what you are on, which section, which page
        Ctrl+R       read the current item again
        Ctrl+Space   repeat the last thing that was said
        Ctrl+Shift+V change how much detail is announced


        SEEING THE SCREEN

        A picture of the printed page can be shown beside the document, for
        anyone who can see one. A screen reader cannot read a picture, so it
        stays hidden until you ask for it, and everything in the document is
        always available to read either way.

        Ctrl+Shift+R show or hide the page picture
        Ctrl+Shift+B switch between the browse view and the text view
        F6           move between the document and the picture
        Ctrl+plus    bigger — the text, or the picture if that has focus
        Ctrl+minus   smaller

        Text size and whether the picture is shown are both remembered between
        sessions, and are on the Settings dialog under the Tools menu.


        FINDING THINGS

        Ctrl+F       find text
        F3           find the next match
        Shift+F3     find the previous match
        Ctrl+G       go to a page by number
        Ctrl+B       list of bookmarks
        Ctrl+K       list of links
        Ctrl+M       list of comments


        FILES

        Ctrl+O       open a PDF
        Ctrl+S       save
        Ctrl+Shift+S save as a new copy
        Ctrl+D       document properties


        FILLING IN A FORM

        Ctrl+Shift+P list every field with its answer, and fill any of them in
        Shift+F      jump to the next field that still needs an answer
        Enter        fill in the field you are on

        Ctrl+Shift+G sign the document

        The Tools menu also has Clear this form, and Save a flattened copy —
        which turns the answers into ordinary page content so nobody can change
        them, and always writes a new file rather than touching this one.


        COMMENTING

        A comment attaches to whatever you are ON — this paragraph, this cell, this
        field — so you never have to point at a place on the page. The comment
        records what it is about, and is signed with your name from Settings.

        Ctrl+Shift+M new comment on whatever you are on
        Ctrl+Shift+K highlight what you are on, and write a note with it
        F2           change the comment you are on
        Ctrl+Shift+Y reply to the comment you are on
        Ctrl+Delete  delete the comment you are on, after reading it back to you
        A            next comment
        Ctrl+M       list every comment

        In the browse view each comment carries its own Change, Reply and Delete
        buttons, because there your screen reader keeps the cursor to itself and
        this program cannot tell which comment you are on.

        Every one of these can be undone with Ctrl+Z, and the undo says which
        comment it put back.


        REPAIRING ACCESSIBILITY

        Ctrl+Shift+A check the document and summarise what is wrong
        Ctrl+Shift+F go through the problems one at a time, with the repair
                     offered at each
        D            jump to the next problem

        Ctrl+Shift+I describe the image you are on
        Ctrl+Shift+L give the form field you are on a name
        Ctrl+Shift+H set the level of the heading you are on
        Ctrl+Shift+U mark what you are on as page furniture, so it is skipped
                     when reading straight through


        UNDOING

        Ctrl+Z       undo, and say what was undone
        Ctrl+Y       redo, and say what was redone
        Ctrl+H       list everything changed in this session


        HELP

        F1           read this list aloud
        Shift+F1     open this window
        Escape       close a dialog

        """;

    #endregion

    #region Keeping the caret and the navigation position in step
    // The user can move the caret themselves, with their screen reader's review cursor or with the
    // arrow keys. When they do, this program follows them rather than fighting for control of the
    // position — the alternative is the two disagreeing about where "here" is, which makes every
    // subsequent command land somewhere unexpected.

    /// <summary>Moves the caret to an element and scrolls it into view.</summary>
    private void MoveCaretTo(DocumentElement element)
    {
        // CaretTargetFor, not SpanOf: a container has no text of its own, and using SpanOf left the
        // caret motionless on every table and list — the announcement happened, the review cursor
        // did not follow, and the command looked broken.
        if (_rendered?.CaretTargetFor(element) is not { } span)
            return;

        _movingCaret = true;

        try
        {
            _documentView.Select(span.Start, span.Length);
            _documentView.ScrollToCaret();
        }
        finally
        {
            _movingCaret = false;
        }
    }

    /// <summary>Updates the navigation position from wherever the user has put the caret.</summary>
    private void TrackCaretToNavigation()
    {
        if (_movingCaret || _document is null || _rendered is null)
            return;

        if (_rendered.ElementIdAt(_documentView.SelectionStart) is not { } elementId)
            return;

        if (_document.FindById(elementId) is { } element && element.Id != _navigation.Current?.Id)
            _navigation.GoToElement(element, _settings.Verbosity);
    }

    #endregion

    #region Status line

    /// <summary>
    /// Keeps the page picture showing whatever the reading position is on.
    ///
    /// One-way, deliberately: the text leads and the picture follows. Doing it the other way would
    /// mean the primary user's position could be moved by a pane they cannot see.
    /// </summary>
    private void SyncPagePicture(DocumentElement element)
    {
        if (_split.Panel2Collapsed || !_renderer.IsAvailable || element.PageNumber < 1)
            return;

        _pageView.ShowPage(element.PageNumber, element);
    }

    /// <summary>
    /// Shows or hides the page picture. Announced either way, because the pane appearing or
    /// vanishing changes the tab order and that should never be a silent change.
    /// </summary>
    private void TogglePagePicture()
    {
        bool showing = _split.Panel2Collapsed;

        if (showing && _document is not null && !_renderer.IsAvailable)
            _renderer.Open(_document.FilePath);

        _split.Panel2Collapsed = !showing;
        _settings.ShowPagePicture = showing;
        _settings.Save();

        if (showing)
        {
            ApplySplitPosition();

            if (_navigation.Current is { } current)
                SyncPagePicture(current);
            else if (_document is not null)
                _pageView.ShowPage(1);
        }

        Play(AudioCue.Success);
        Speech.BeginNewAnnouncement();

        Announce(showing
            ? "Page picture shown beside the text. It is a picture of the printed page, for people " +
              "who can see it; a screen reader cannot read it. Everything in the document is still " +
              "available as text. Press F6 to move between the two panes."
            : "Page picture hidden.", AnnouncementPriority.Assertive);
    }

    /// <summary>
    /// Moves between the text and the picture, the way F6 moves between panes everywhere else in
    /// Windows.
    /// </summary>
    private void SwitchPane()
    {
        string surface = BrowseViewActive ? "Document, browse view." : "Document text.";

        if (_split.Panel2Collapsed)
        {
            ReadingSurface.Focus();
            Announce($"{surface} The page picture is hidden; press Control plus Shift plus R to show it.",
                AnnouncementPriority.Assertive);
            return;
        }

        if (_pageView.ContainsFocus)
        {
            ReadingSurface.Focus();
            Announce(surface, AnnouncementPriority.Assertive);
        }
        else
        {
            _pageView.Focus();
            Announce($"Page picture, page {_pageView.PageNumber}. " +
                     "Arrow keys scroll it. Press F6 to go back to the document.",
                AnnouncementPriority.Assertive);
        }
    }

    /// <summary>
    /// Applies the chosen text size to the document view.
    ///
    /// Live, without reloading. Someone adjusting the size is doing it because they cannot read
    /// what is there, and making them re-open the document to find out whether it helped would be
    /// a poor way to run a setting they may need to try three times.
    /// </summary>
    private void ApplyTextSize()
    {
        var previous = _documentView.Font;
        _documentView.Font = new Font(previous.FontFamily, _settings.TextSizePoints);
        previous.Dispose();
    }

    /// <summary>
    /// Updates the status bar and window title. Also pushed to a braille display, where a
    /// persistent line of context is more useful than a spoken one that has already gone.
    /// </summary>
    private void UpdateStatus(string? message = null)
    {
        string status = message ?? _document?.BuildStatusLine() ?? "No document open";

        _statusLabel.Text = status;
        Text = WindowTitle;

        if (Speech.IsBrailleAvailable)
            Speech.Braille(status);
    }

    #endregion

    #region Disposal

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderer.Dispose();
            _pageView?.Dispose();
            _split?.Dispose();
            _documentView?.Dispose();
            _statusStrip?.Dispose();
            _menu?.Dispose();
        }

        base.Dispose(disposing);
    }

    #endregion
}

#endregion
