using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Model;

namespace AccessiblePdfEditor.UI;

// =====================================================================================
//  SignaturePadDialog.cs
//
//  The window that hosts the signature pad.
//
//  Its job beyond holding the control: make sure the user is never in the position of
//  having drawn something they cannot judge. Before anything is accepted, the drawing is
//  described out loud — how many strokes, how much of the area they cover, and whether
//  that looks like a signature or like a slip of the mouse — and a drawing that is too
//  small has to be confirmed a second time.
//
//  A sighted user gets all of that from a glance. This is the substitute, and it is not
//  optional: signing a document with an accidental dot is a worse outcome than any amount
//  of extra confirmation.
// =====================================================================================

#region SignaturePadDialog

/// <summary>Captures a hand-drawn signature.</summary>
public sealed class SignaturePadDialog : AccessibleFormBase
{
    #region State

    private readonly string _signerName;

    private SignaturePadControl _pad = null!;
    private Button _apply = null!;
    private Label _statusLabel = null!;

    /// <summary>The captured signature. Null when the user cancelled.</summary>
    public SignatureMark? Result { get; private set; }

    public SignaturePadDialog(ISpeechService speech, IAudioCueService cues, string signerName)
        : base(speech, cues)
    {
        _signerName = signerName;

        Size = new Size(720, 480);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(520, 380);
    }

    #endregion

    #region Identity

    protected override string WindowTitle => "Draw your signature";

    protected override string WindowPurpose =>
        "Draw your signature with the mouse, or with the arrow keys if you would rather not use one.";

    protected override string BuildOpeningAnnouncement() =>
        "Draw your signature. " +
        "The mouse pointer will be moved to the left of the drawing area, where a signature " +
        "usually starts. Hold the left mouse button and draw. " +
        "If you would rather not use the mouse, press Space to put the pen down and draw with the " +
        "arrow keys. " +
        "As you move, the pitch tells you where you are: higher towards the right, and higher up " +
        "the area. " +
        "Press Control plus Enter when you have finished, Control plus Delete to start again, or " +
        "Escape to cancel.";

    #endregion

    #region Layout

    protected override void BuildContent()
    {
        _pad = new SignaturePadControl(Cues, (message, priority) => Announce(message, priority))
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(12),
        };

        _pad.DrawingChanged += OnDrawingChanged;

        var padHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            AccessibleRole = AccessibleRole.Grouping,
            AccessibleName = "Signature drawing area",
        };

        padHost.Controls.Add(_pad);

        _statusLabel = CreateLabel("Nothing drawn yet.", tabIndex: 1);
        _statusLabel.Dock = DockStyle.Bottom;
        _statusLabel.Padding = new Padding(16, 4, 16, 8);
        _statusLabel.AccessibleName = "What you have drawn";

        _apply = CreateButton("&Use this signature", (_, _) => Accept(),
            "Accept what you have drawn", tabIndex: 2);
        _apply.Enabled = false;

        var clear = CreateButton("Start &again", (_, _) => ClearDrawing(),
            "Rub out everything and start over", tabIndex: 3);

        var describe = CreateButton("&Describe what I have drawn", (_, _) => DescribeDrawing(),
            "Say how many strokes there are and how much of the area they cover", tabIndex: 4);

        var cancel = CreateButton("&Cancel", (_, _) => Cancel(),
            "Close without signing", tabIndex: 5);

        Controls.Add(padHost);
        Controls.Add(_statusLabel);
        Controls.Add(CreateButtonRow(_apply, clear, describe, cancel));

        SetCancelButton(cancel);
    }

    /// <summary>
    /// Focus goes to the pad, and the pointer is moved onto it. Both matter: focus is what makes
    /// the keyboard route work, and the pointer move is what makes the mouse route findable.
    /// </summary>
    protected override void FocusFirstControl()
    {
        _pad.Focus();
        BeginInvoke(() => _pad.PlacePointerAtStart());
    }

    #endregion

    #region Reacting to the drawing

    private void OnDrawingChanged()
    {
        _apply.Enabled = _pad.HasStrokes;
        _statusLabel.Text = _pad.DescribeDrawing();
    }

    private void DescribeDrawing()
    {
        Speech.BeginNewAnnouncement();
        Announce(_pad.DescribeDrawing(), AnnouncementPriority.Assertive);
    }

    private void ClearDrawing()
    {
        _pad.ClearDrawing();
        Play(AudioCue.Undone);
        Announce("Rubbed out. The drawing area is empty again.", AnnouncementPriority.Assertive);
        _pad.Focus();
    }

    #endregion

    #region Accepting

    private void Accept()
    {
        if (!_pad.HasStrokes)
        {
            Play(AudioCue.Boundary);
            Announce("Nothing has been drawn yet.", AnnouncementPriority.Assertive);
            _pad.Focus();
            return;
        }

        var mark = SignatureMark.FromStrokes(_pad.Strokes, _signerName);

        // A drawing this small is far more likely to be a slip than a signature, and the user has
        // no way to see that for themselves. Confirmed a second time rather than assumed.
        if (mark.IsSuspiciouslySmall)
        {
            bool sure = Confirm(
                $"{_pad.DescribeDrawing()} Do you want to use it anyway?",
                "That looks very small");

            if (!sure)
            {
                _pad.Focus();
                return;
            }
        }

        Result = mark;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Cancel()
    {
        Result = null;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    protected override bool HandleShortcut(Keys keyData)
    {
        switch (keyData)
        {
            // Not plain Enter: the pad uses Space for the pen, and Enter would be too easy to press
            // by accident part-way through drawing.
            case Keys.Control | Keys.Enter:
                Accept();
                return true;

            case Keys.Control | Keys.Delete:
                ClearDrawing();
                return true;

            case Keys.Control | Keys.D:
                DescribeDrawing();
                return true;

            default:
                return false;
        }
    }

    protected override string BuildKeyHelp() =>
        "Hold the left mouse button and move to draw. " +
        "Or press Space to put the pen down, then use the arrow keys; Shift with an arrow moves " +
        "further. Space again lifts the pen. " +
        "The pitch tells you where you are: higher towards the right, and higher up. A low tone " +
        "means you have reached the edge. " +
        "Control plus D describes what you have drawn. " +
        "Control plus Delete starts again. " +
        "Control plus Enter accepts it. Escape cancels.";

    #endregion

    #region Convenience

    /// <summary>
    /// Shows the pad and returns the drawn signature, or null.
    ///
    /// Named CaptureDrawing rather than Capture because Control already has a Capture property for
    /// mouse capture, and a name that shadows it would be a trap for anyone reading this later.
    /// </summary>
    public static SignatureMark? CaptureDrawing(
        IWin32Window owner, ISpeechService speech, IAudioCueService cues, string signerName)
    {
        using var dialog = new SignaturePadDialog(speech, cues, signerName);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Result : null;
    }

    #endregion
}

#endregion
