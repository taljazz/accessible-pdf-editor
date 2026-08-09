using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Model;

namespace AccessiblePdfEditor.UI;

// =====================================================================================
//  TextPromptDialog.cs
//
//  A dialog that asks the user for one piece of text.
//
//  Used for every "type something" moment in the application: describing an image, naming
//  a form field, entering a page number, giving a document a title. One dialog rather than
//  eight, because a user who has learned how this one behaves has learned all of them —
//  and a consistent dialog is worth far more without sight than a bespoke one.
//
//  The guidance line is the part that earns its place. When the user is asked to describe
//  an image, the prompt does not just say "Description:" — it explains what a good
//  description is, and it is spoken when the dialog opens. Writing alt text is a skill, and
//  most people have never been told what it involves.
// =====================================================================================

#region TextPromptDialog

/// <summary>Asks the user for a single piece of text.</summary>
public sealed class TextPromptDialog : AccessibleFormBase
{
    #region State

    private readonly string _title;
    private readonly string _prompt;
    private readonly string? _guidance;
    private readonly bool _multiline;
    private readonly string _initialValue;
    private readonly Func<string, string?>? _validate;
    private readonly Bitmap? _picture;

    private TextBox _input = null!;
    private Label _errorLabel = null!;

    /// <summary>What the user typed. Null when they cancelled.</summary>
    public string? Value { get; private set; }

    public TextPromptDialog(
        ISpeechService speech,
        IAudioCueService cues,
        string title,
        string prompt,
        string? guidance = null,
        string initialValue = "",
        bool multiline = false,
        Func<string, string?>? validate = null,
        Bitmap? picture = null)
        : base(speech, cues)
    {
        _title = title;
        _prompt = prompt;
        _guidance = guidance;
        _initialValue = initialValue;
        _multiline = multiline;
        _validate = validate;
        _picture = picture;

        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    #endregion

    #region Identity

    protected override string WindowTitle => _title;

    protected override string WindowPurpose => _guidance ?? _prompt;

    protected override string BuildOpeningAnnouncement()
    {
        var parts = new List<string>(4) { _title, _prompt };

        if (_guidance is { Length: > 0 })
            parts.Add(_guidance);

        if (_initialValue.Length > 0)
            parts.Add($"Currently: {_initialValue}");

        parts.Add(_multiline
            ? "Type your answer. Press Control plus Enter to accept, or Escape to cancel."
            : "Type your answer. Press Enter to accept, or Escape to cancel.");

        return string.Join(" ", parts);
    }

    #endregion

    #region Layout

    protected override void BuildContent()
    {
        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
        };

        var promptLabel = CreateLabel(_prompt, tabIndex: 0);
        promptLabel.MaximumSize = new Size(560, 0);
        layout.Controls.Add(promptLabel);

        if (_guidance is { Length: > 0 })
        {
            var guidanceLabel = CreateLabel(_guidance, tabIndex: 1);
            guidanceLabel.MaximumSize = new Size(560, 0);
            guidanceLabel.ForeColor = SystemColors.GrayText;
            layout.Controls.Add(guidanceLabel);
        }

        AddPicture(layout);

        _input = CreateTextBox(
            accessibleName: _prompt.TrimEnd(':', ' '),
            description: _guidance,
            multiline: _multiline,
            tabIndex: 2);

        _input.Text = _initialValue;
        _input.Width = 560;

        // Selecting the existing value means typing replaces it, which is what someone correcting
        // an entry expects, while leaving it available to arrow through if they only want to amend.
        _input.SelectAll();

        layout.Controls.Add(_input);

        _errorLabel = CreateLabel(string.Empty, tabIndex: 3);

        // A hard-coded dark red is close to unreadable on a dark high-contrast theme, which is
        // exactly the theme someone with low vision is likely to be running. The system's own
        // highlight colour is guaranteed legible against the window background in every theme.
        _errorLabel.ForeColor = SystemInformation.HighContrast
            ? SystemColors.HotTrack
            : Color.FromArgb(180, 0, 0);
        _errorLabel.MaximumSize = new Size(560, 0);
        _errorLabel.AccessibleName = "Validation message";
        _errorLabel.Visible = false;
        layout.Controls.Add(_errorLabel);

        var accept = CreateButton("&OK", (_, _) => Accept(), "Accept what you have typed", tabIndex: 4);
        var cancel = CreateButton("&Cancel", (_, _) => Cancel(), "Close without changing anything", tabIndex: 5);

        Controls.Add(layout);
        Controls.Add(CreateButtonRow(accept, cancel));

        SetCancelButton(cancel);

        // A multi-line box needs Enter for new lines, so it cannot also be the accept key. Control
        // plus Enter accepts instead, which is the Windows convention.
        if (!_multiline)
            SetDefaultButton(accept);
    }

    /// <summary>
    /// Shows the thing being asked about, when there is a picture of it.
    ///
    /// This is what makes "describe this image" a possible request rather than an impossible one.
    /// Until the page could be rendered, nobody using this program could see the image they were
    /// being asked to describe — not the blind user, obviously, but not a sighted colleague helping
    /// out either, because the document was only ever shown as text. Now the helper can see it.
    ///
    /// It is added AFTER the guidance and BEFORE the input, so tab order still reaches the text box
    /// immediately, and the picture itself is skipped: it is worth nothing to a screen reader and
    /// putting it in the tab order would be an obstacle for the primary user.
    /// </summary>
    private void AddPicture(TableLayoutPanel layout)
    {
        if (_picture is null)
            return;

        // Scaled down to fit, never up. An enlarged low-resolution crop looks like a mistake and
        // tells the viewer nothing extra.
        const int maximumWidth = 560;
        const int maximumHeight = 260;

        double scale = Math.Min(
            Math.Min((double)maximumWidth / _picture.Width, (double)maximumHeight / _picture.Height),
            1.0);

        var box = new PictureBox
        {
            Image = _picture,
            SizeMode = PictureBoxSizeMode.Zoom,
            Width = Math.Max(40, (int)(_picture.Width * scale)),
            Height = Math.Max(40, (int)(_picture.Height * scale)),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 6, 0, 6),

            // Named and described, but kept out of the tab order. A screen reader announces that a
            // picture is here and that it cannot read it, which is honest; stopping on it every
            // time would be an obstacle.
            TabStop = false,
            AccessibleRole = AccessibleRole.Graphic,
            AccessibleName = "The image you are describing",
            AccessibleDescription =
                "A picture of this part of the page, shown for anyone who can see it. " +
                "A screen reader cannot read it — that is exactly why it needs a description.",
        };

        layout.Controls.Add(box);
    }

    protected override void FocusFirstControl() => _input.Focus();

    #endregion

    #region Accepting and cancelling

    protected override bool HandleShortcut(Keys keyData)
    {
        if (_multiline && keyData == (Keys.Control | Keys.Enter))
        {
            Accept();
            return true;
        }

        return false;
    }

    private void Accept()
    {
        string typed = _input.Text.Trim();

        if (_validate?.Invoke(typed) is { Length: > 0 } error)
        {
            // Announced assertively and shown on screen. A validation message that is only visible
            // is no message at all to someone who cannot see it.
            _errorLabel.Text = error;
            _errorLabel.Visible = true;

            Play(AudioCue.Rejected);
            Speech.BeginNewAnnouncement();
            Announce(error, AnnouncementPriority.Assertive);

            _input.Focus();
            _input.SelectAll();
            return;
        }

        Value = typed;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Cancel()
    {
        Value = null;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    protected override string BuildKeyHelp() =>
        _multiline
            ? "Type your answer. Enter starts a new line. Control plus Enter accepts. Escape cancels."
            : "Type your answer. Enter accepts. Escape cancels.";

    #endregion

    #region Convenience

    /// <summary>
    /// Shows the dialog and returns what was typed, or null if cancelled.
    /// </summary>
    public static string? Ask(
        IWin32Window owner,
        ISpeechService speech,
        IAudioCueService cues,
        string title,
        string prompt,
        string? guidance = null,
        string initialValue = "",
        bool multiline = false,
        Func<string, string?>? validate = null,
        Bitmap? picture = null)
    {
        using var dialog = new TextPromptDialog(
            speech, cues, title, prompt, guidance, initialValue, multiline, validate, picture);

        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Value : null;
    }

    #endregion
}

#endregion
