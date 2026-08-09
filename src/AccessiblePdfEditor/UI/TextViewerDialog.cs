using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Model;

namespace AccessiblePdfEditor.UI;

// =====================================================================================
//  TextViewerDialog.cs
//
//  A window that shows a block of text in a read-only text box for the user to browse.
//
//  This exists because SPEECH IS TRANSIENT AND A KEY LIST IS NOT SOMETHING YOU HEAR ONCE.
//
//  Reading the help aloud, as F1 does, is right when you want a quick reminder. It is
//  useless when you want to explore it: you cannot go back a line, you cannot skip to the
//  part about forms, you cannot check a shortcut you half-caught while the next sentence
//  was already playing, and you certainly cannot copy it somewhere.
//
//  Putting the same text in a real read-only text box solves all of that at once, and
//  solves it with the user's OWN tools rather than with commands invented here:
//
//    arrow keys and the screen reader's review cursor, line by line or word by word
//    Say All, from wherever they are, at their speed
//    find-in-text with their screen reader's own search
//    their braille display, tracking as they read
//    Ctrl+A and Ctrl+C to copy it into an email or a notes file
//
//  None of that needed writing. It comes free from using a real Windows control, which is
//  the same reasoning behind the document view in the main window.
// =====================================================================================

#region TextViewerDialog

/// <summary>Shows a block of text in a read-only, browsable text box.</summary>
public sealed class TextViewerDialog : AccessibleFormBase
{
    #region State

    private readonly string _title;
    private readonly string _purpose;
    private readonly string _content;

    private TextBox _view = null!;

    public TextViewerDialog(
        ISpeechService speech,
        IAudioCueService cues,
        string title,
        string purpose,
        string content)
        : base(speech, cues)
    {
        _title = title;
        _purpose = purpose;
        _content = NormaliseLineEndings(content);

        Size = new Size(760, 620);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;
    }

    /// <summary>
    /// Converts any mixture of line endings to the Windows form.
    ///
    /// Not a nicety. A text box renders a bare newline as an unprintable box rather than as a line
    /// break, so text arriving with Unix endings would display as one enormous line — and a single
    /// line cannot be browsed line by line, which is the entire purpose of this window. Raw-string
    /// literals in C# use bare newlines, so this is the normal case, not the exotic one.
    /// </summary>
    internal static string NormaliseLineEndings(string? text) =>
        (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);

    #endregion

    #region Identity

    protected override string WindowTitle => _title;

    protected override string WindowPurpose => _purpose;

    /// <summary>
    /// Deliberately short, and deliberately NOT the text itself.
    ///
    /// The user opened this window in order to read at their own pace. Reading the whole thing at
    /// them on arrival would be exactly the behaviour they were trying to escape, and they would
    /// have to sit through it or interrupt it before they could start.
    /// </summary>
    protected override string BuildOpeningAnnouncement()
    {
        int lines = _content.Split("\r\n").Length;

        return $"{_title}. {lines} lines. " +
               "Read it with the arrow keys or your screen reader's own reading commands. " +
               "Control plus A then Control plus C copies it all. Escape closes.";
    }

    #endregion

    #region Layout

    protected override void BuildContent()
    {
        _view = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Text = _content,

            // A real, focusable text box with a visible selection. Read-only, but it must still
            // take focus and carry a caret, or none of the screen reader's review commands work on
            // it — which is the entire reason this window exists.
            TabStop = true,
            HideSelection = false,

            AccessibleName = _title,
            AccessibleDescription = _purpose,
            AccessibleRole = AccessibleRole.Text,

            Font = new Font("Segoe UI", 11f),
            Padding = new Padding(8),
        };

        var copy = CreateButton("&Copy all", (_, _) => CopyAll(),
            "Copy the whole text to the clipboard", tabIndex: 1);

        var close = CreateButton("&Close", (_, _) => Close(),
            "Close this window", tabIndex: 2);

        Controls.Add(_view);
        Controls.Add(CreateButtonRow(copy, close));

        SetCancelButton(close);
    }

    /// <summary>
    /// Focus lands in the text, not on a button. The user came here to read; making them Tab into
    /// the content first would be a small obstacle repeated every single time.
    /// </summary>
    protected override void FocusFirstControl()
    {
        _view.Focus();
        _view.Select(0, 0);
    }

    #endregion

    #region Copying

    private void CopyAll()
    {
        try
        {
            Clipboard.SetText(_content);
            AnnounceOutcome(true, "Copied to the clipboard.");
        }
        catch (Exception ex)
        {
            // The clipboard can genuinely be locked by another program, and a silent failure would
            // leave the user pasting whatever was there before without knowing.
            AnnounceOutcome(false, $"It could not be copied: {ex.Message}");
        }
    }

    #endregion

    #region Keys

    protected override string BuildKeyHelp() =>
        "Arrow keys move through the text a line at a time. Control plus Home and Control plus End " +
        "go to the start and the end. Control plus A selects everything and Control plus C copies " +
        "it. Escape closes this window. Your screen reader's own reading and review commands work " +
        "here as they do in any text box.";

    #endregion

    #region Convenience

    /// <summary>Shows a block of text for browsing.</summary>
    public static void Show(
        IWin32Window owner,
        ISpeechService speech,
        IAudioCueService cues,
        string title,
        string purpose,
        string content)
    {
        using var dialog = new TextViewerDialog(speech, cues, title, purpose, content);
        dialog.ShowDialog(owner);
    }

    #endregion
}

#endregion
