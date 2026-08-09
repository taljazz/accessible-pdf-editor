using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Model.Forms;
using AccessiblePdfEditor.Rendering;

namespace AccessiblePdfEditor.UI;

// =====================================================================================
//  MainForm.Browse.cs
//
//  The browse view: the document as a web page, read by the screen reader in BROWSE MODE.
//
//  WHY THERE ARE TWO READING SURFACES
//
//  The text box was built first, and it earns its place: it needs nothing installed, it
//  gives the screen reader's review cursor a continuous piece of text to walk, and it is
//  what the program falls back to when the WebView2 runtime is missing. What it cannot do
//  is carry STRUCTURE. To a screen reader a text box is one flat string, so every piece of
//  navigation had to be reimplemented here — H for a heading, T for a table — using keys
//  this program chose, in a program the user had to learn separately from every other
//  program they own. And table navigation could not be reimplemented at all, because
//  NVDA's Control+Alt+arrow commands need a document, not a control.
//
//  The browse view has the structure and gives the commands back. So:
//
//     browse view    the reading surface. The user's own screen reader drives it.
//     text box       the fallback, and the surface this program's own caret commands act on.
//
//  WHAT THIS PROGRAM DOES NOT DO IN THE BROWSE VIEW
//
//  It does not track the reading cursor, because it cannot. In browse mode the screen
//  reader moves a cursor through a copy of the page that it holds itself, and nothing is
//  reported back to the application. That is not a defect to work around — it is what makes
//  browse mode fast and what keeps the reader in control.
//
//  So the commands that act on "the thing you are on" are answered differently here: every
//  fault the editor can repair is written into the page AS A BUTTON. NVDA's B key finds the
//  next button, Enter activates it, and the activation arrives at OnBrowsePageEvent naming
//  the element it happened to. The faults become the navigation, which is a better fit for
//  repair work than a cursor was.
// =====================================================================================

public sealed partial class MainForm
{
    #region State

    private BrowseModeView _browseView = null!;
    private Panel _readingHost = null!;
    private DocumentHtml? _html;

    /// <summary>
    /// Whether the browse view is the one on screen. False when the user has chosen the text box,
    /// and always false when the WebView2 runtime is missing.
    /// </summary>
    private bool BrowseViewActive => _browseView.IsAvailable && _settings.UseBrowseView;

    /// <summary>The control the reader should be in, whichever surface is showing.</summary>
    private Control ReadingSurface => BrowseViewActive ? _browseView : _documentView;

    #endregion

    #region Layout

    /// <summary>
    /// Builds the pane that holds both reading surfaces, one visible at a time.
    ///
    /// Both are built even though only one is shown, so that switching between them is instant and
    /// does not lose the document. Only the visible one is in the tab order — an invisible control
    /// is out of the accessibility tree entirely, so there is no hidden pane for a screen reader
    /// user to fall into.
    /// </summary>
    private Panel BuildReadingHost()
    {
        _browseView = new BrowseModeView { Visible = false };
        _browseView.PageEvent += OnBrowsePageEvent;
        _browseView.HostKeyPressed += OnBrowseHostKey;

        _readingHost = new Panel { Dock = DockStyle.Fill, AccessibleName = "Document" };

        _readingHost.Controls.Add(_documentView);
        _readingHost.Controls.Add(_browseView);

        ApplyReadingSurfaceVisibility();

        return _readingHost;
    }

    private void ApplyReadingSurfaceVisibility()
    {
        bool browse = BrowseViewActive;

        _browseView.Visible = browse;
        _documentView.Visible = !browse;
    }

    #endregion

    #region Rendering

    /// <summary>
    /// Rebuilds the web page from the model. Called from RenderDocument, so the browse view is
    /// refreshed by exactly the same events that refresh the text: loading, and every edit.
    /// </summary>
    private void RenderBrowseView()
    {
        if (!_browseView.IsAvailable)
            return;

        if (_document is null)
        {
            _html = null;
            return;
        }

        _html = DocumentHtmlWriter.Write(_document);

        if (BrowseViewActive)
            _browseView.Refresh(_html);
    }

    /// <summary>
    /// Starts the web view and shows the document in it. Deferred until the browse view is first
    /// needed, because starting a browser process costs a second and some memory, and a user who
    /// stays in the text box should never pay for it.
    /// </summary>
    private async Task EnsureBrowseViewReadyAsync()
    {
        if (!_browseView.IsAvailable)
            return;

        await _browseView.InitialiseAsync();

        if (_html is not null)
            _browseView.Show(_html);
    }

    #endregion

    #region Switching between the surfaces

    /// <summary>
    /// Switches between the browse view and the text box, and says what changed and what it means.
    ///
    /// The announcement names the commands that become available or go away, because the difference
    /// between the two surfaces is entirely a difference in which keys work, and a user who is not
    /// told has to discover it by pressing things and hearing nothing happen.
    /// </summary>
    private async void ToggleBrowseView()
    {
        if (!_browseView.IsAvailable)
        {
            Play(AudioCue.Warning);
            Speech.BeginNewAnnouncement();

            Announce(_browseView.UnavailableReason ?? "The browse view is not available on this computer.",
                AnnouncementPriority.Assertive);

            return;
        }

        _settings.UseBrowseView = !_settings.UseBrowseView;
        _settings.Save();

        ApplyReadingSurfaceVisibility();

        Play(AudioCue.Success);
        Speech.BeginNewAnnouncement();

        if (BrowseViewActive)
        {
            await EnsureBrowseViewReadyAsync();

            _browseView.Focus();

            Announce(
                "Browse view. The document is now a web page, so your screen reader's own reading " +
                "commands work here the way they do on any web page: H for the next heading, T for " +
                "the next table, K for a link, F for a form field, D for the next page, and Control " +
                "plus Alt plus the arrow keys to move around inside a table. " +
                "Press Control plus Shift plus B to go back to the text view.",
                AnnouncementPriority.Assertive);
        }
        else
        {
            _documentView.Focus();

            if (_navigation.Current is { } current)
                MoveCaretTo(current);

            Announce(
                "Text view. This program's own navigation keys work here: H for the next heading, " +
                "T for the next table, F for a form field. " +
                "Press Control plus Shift plus B to go back to the browse view.",
                AnnouncementPriority.Assertive);
        }
    }

    /// <summary>
    /// Takes the browse view — and the reader's cursor with it — to whatever this program's own
    /// navigation moved to.
    ///
    /// Only ever called from a deliberate navigation command, never from a background change. That
    /// distinction is the whole rule: when the user asks to go somewhere, going there is the point;
    /// a page that moved the reader on its own would be dragging them away mid-sentence.
    /// </summary>
    private async void SyncBrowseView(DocumentElement element)
    {
        if (!BrowseViewActive || _html is null)
            return;

        // A container — a table, a list, a page — has no markup of its own to land on in some
        // documents, so fall back to the first descendant that does. Exactly the same reasoning as
        // RenderedDocument.CaretTargetFor in the text view, and the same bug if it is missed.
        string? anchor = _html.AnchorFor(element)
                         ?? element.Descendants()
                             .Select(d => _html.AnchorFor(d))
                             .FirstOrDefault(a => a is not null);

        if (anchor is not null)
            await _browseView.MoveToAsync(anchor);
    }

    #endregion

    #region What the page reports back

    /// <summary>
    /// Handles something the user did in the page: activating a link, pressing one of the repair
    /// buttons, or changing a form field.
    ///
    /// Everything here goes through the same commands the rest of the program uses, so an edit made
    /// in the browse view is undoable, is recorded in the change history, and is written by the
    /// same code that has been tested against real documents. The web page is a surface, never a
    /// second implementation.
    /// </summary>
    private void OnBrowsePageEvent(object? sender, BrowsePageEvent e)
    {
        if (_document is null || e.ElementId < 0)
            return;

        if (_document.FindById(e.ElementId) is not { } element)
            return;

        switch (e.Kind)
        {
            // Focus landing on a real control is the one moment the page can tell this program
            // where the user is, so the navigation position is brought into step with it. That is
            // what makes Control+W and the remediation commands work on the field being filled in.
            case "focus":
                _navigation.GoToElement(element, _settings.Verbosity);
                UpdateStatus();
                break;

            case "value":
                ApplyBrowseValue(element, e.Value);
                break;

            case "activate":
                ActivateFromBrowseView(element, e.Action);
                break;
        }
    }

    private void ActivateFromBrowseView(DocumentElement element, string? action)
    {
        _navigation.GoToElement(element, _settings.Verbosity);

        switch (action)
        {
            // The repair buttons. These are the whole reason faults are written into the page as
            // buttons rather than as sentences: the user finds one with their screen reader's own
            // B key and fixes it on the spot.
            case "describe":
                DescribeCurrentFigure();
                break;

            case "sign":
                SignDocument();
                break;

            // Comment actions. The navigation position was moved to the comment just above, which
            // is what these commands act on, so they behave identically here and in the text view.
            case "editComment":
                EditCommentHere();
                break;

            case "replyComment":
                ReplyToCommentHere();
                break;

            case "deleteComment":
                DeleteCommentHere();
                break;

            case "link":
            case "button":
            case "attachment":
                ActivateCurrent();
                break;
        }
    }

    /// <summary>
    /// Applies a value the user typed or chose in the page, through the ordinary edit command so it
    /// joins the undo history like any other change.
    /// </summary>
    private void ApplyBrowseValue(DocumentElement element, string? value)
    {
        if (element is not PdfFormField field || value is null)
            return;

        // Nothing to record when the page is only telling us what the field already held, which
        // happens when a control is rebuilt after an edit made somewhere else.
        if (string.Equals(field.ValueForSpeech, value, StringComparison.Ordinal))
            return;

        ApplyEdit(new Editing.SetFieldValueCommand(field, value));
    }

    /// <summary>
    /// Deals with a shortcut pressed while focus was inside the page.
    ///
    /// A web view swallows keystrokes, so without this the program's own shortcuts would stop
    /// working the moment the user started reading — which would make the browse view a room with
    /// no door. Only modified and function keys arrive here; single letters are left to the screen
    /// reader, which is the entire point of the browse view.
    /// </summary>
    private void OnBrowseHostKey(object? sender, KeyEventArgs e)
    {
        // F10 is how a keyboard user reaches a menu bar in Windows, and the menu is where the
        // commands are discoverable. It cannot be forwarded as a shortcut because the menu needs
        // real focus, so it is handled here.
        if (e.KeyCode == Keys.F10)
        {
            _menu.Focus();
            _menu.Items[0].Select();
            return;
        }

        // Closing the window. Windows would normally do this without anyone's help, but focus is
        // inside a web view, and a key pressed there reaches this program rather than the window
        // manager. Handled explicitly rather than relying on it finding its own way out: Alt+F4 is
        // the one keystroke every Windows user knows, and a window that ignores it feels stuck.
        if (e.KeyData == (Keys.Alt | Keys.F4))
        {
            Close();
            return;
        }

        if (HandleShortcut(e.KeyData))
            return;

        switch (e.KeyData)
        {
            // Unmodified Page Up and Page Down mean previous and next page, as they do in every
            // PDF reader. They only reach this program when no screen reader has claimed them —
            // NVDA binds them to its own cursor in browse mode — which is why the Control
            // versions in HandleShortcut exist as the pair that always works.
            case Keys.PageDown:
                Navigate(NavigationGranularity.Page, MoveDirection.Next);
                break;

            case Keys.PageUp:
                Navigate(NavigationGranularity.Page, MoveDirection.Previous);
                break;

            // F1 and Shift+F1 belong to the base window rather than to this one's shortcut table.
            case Keys.F1: ShowHelp(); break;
            case Keys.Shift | Keys.F1: ShowBrowsableHelp(); break;
        }
    }

    #endregion

    #region Telling the user which surface they are in

    /// <summary>
    /// What to say when the document first opens, naming the surface and the commands that go with
    /// it. A user who is not told which of the two surfaces they are in has no way of knowing why
    /// a key did or did not work.
    /// </summary>
    private string DescribeReadingSurface()
    {
        if (!_browseView.IsAvailable)
        {
            return "Reading in the text view. Your screen reader's reading commands work here; " +
                   "this program's own keys move by heading, table and form field. Press F1 for the list.";
        }

        return BrowseViewActive
            ? "Reading in the browse view, so your screen reader's own web-page commands work: " +
              "H for a heading, T for a table, Control plus Alt plus the arrow keys inside a table. " +
              "Press Control plus Shift plus B for the text view instead."
            : "Reading in the text view. Press Control plus Shift plus B for the browse view, where " +
              "your screen reader's own navigation commands work.";
    }

    #endregion
}
