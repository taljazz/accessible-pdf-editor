using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Model;

namespace AccessiblePdfEditor.UI;

// =====================================================================================
//  SignaturePadControl.cs
//
//  A drawing surface a person who cannot see it can still sign on.
//
//  THE PROBLEM. Signing by hand is one of the few genuinely visual acts left in document
//  work. Plenty of blind people can write their own signature perfectly well — it is
//  muscle memory, not something you look at while doing — but a mouse gives none of the
//  proprioceptive feedback a pen on paper does. You do not know where the pointer is, you
//  do not know when you have left the box, and you do not know whether you have drawn
//  anything at all.
//
//  WHAT THIS DOES ABOUT IT, in the order the problems occur:
//
//  1. THE POINTER IS PUT WHERE THE SIGNATURE STARTS. On opening, the physical mouse cursor
//     is moved to the left-centre of the pad — where a signature begins on paper. The user
//     does not have to find the surface; they are already on it, and they are told so.
//
//  2. POSITION IS AUDIBLE WHILE DRAWING. Pitch rises as the pointer moves right and as it
//     moves up, so the user hears where they are in real time. Speech cannot do this: by
//     the time a coordinate had been read out the pointer would have moved.
//
//  3. LEAVING THE SURFACE SOUNDS DIFFERENT. A distinct low tone at the edge, so running
//     off the pad is immediately obvious rather than being discovered afterwards.
//
//  4. THERE IS A MOUSE-FREE ROUTE. Arrow keys move the pen, Space puts it down and lifts
//     it. Slower, but it works with no pointing device at all — and for someone who cannot
//     use a mouse, "draw your signature" is otherwise simply impossible.
//
//  5. IT REFUSES A SIGNATURE THAT IS NOT ONE. A stray click leaves a dot. A sighted user
//     sees that instantly; a blind user would sign a document with it. The pad measures
//     what was drawn and says so.
// =====================================================================================

#region SignaturePadControl

/// <summary>A drawing surface for capturing a signature, usable with a mouse or the keyboard.</summary>
public sealed class SignaturePadControl : Panel
{
    #region Construction and state

    private readonly IAudioCueService _cues;
    private readonly Action<string, AnnouncementPriority> _announce;

    private readonly List<SignatureStroke> _strokes = [];
    private readonly List<List<PointF>> _screenStrokes = [];

    private SignatureStroke? _currentStroke;
    private List<PointF>? _currentScreenStroke;

    private bool _penIsDown;
    private DateTime _lastToneAt = DateTime.MinValue;
    private PointF _keyboardPen;

    /// <summary>How often a position tone may play. Faster than this and they overlap into a drone.</summary>
    private static readonly TimeSpan ToneInterval = TimeSpan.FromMilliseconds(45);

    /// <summary>How far one arrow-key press moves the pen, as a fraction of the pad.</summary>
    private const double KeyboardStep = 0.03;

    public SignaturePadControl(IAudioCueService cues, Action<string, AnnouncementPriority> announce)
    {
        _cues = cues;
        _announce = announce;

        DoubleBuffered = true;
        BorderStyle = BorderStyle.FixedSingle;

        // System colours, not white and black. Plenty of the people who use a screen reader also
        // have some usable sight and run Windows in a high-contrast theme; a hard-coded white pad
        // with black ink would be a glaring white box in the middle of a dark screen, and the ink
        // could vanish entirely. What is drawn INTO the PDF stays dark ink on the page — that is a
        // separate thing from how the pad looks while drawing on it.
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;

        // Must be focusable, or the keyboard route does not exist and neither do the announcements
        // a screen reader makes on arrival.
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;

        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Signature area";
        AccessibleDescription =
            "Draw your signature by holding the left mouse button, or press Space to put the pen " +
            "down and use the arrow keys. The pitch tells you where you are: higher to the right " +
            "and higher up.";

        _keyboardPen = new PointF(0.1f, 0.5f);
    }

    /// <summary>Whether anything has been drawn.</summary>
    public bool HasStrokes => _strokes.Count > 0 || _currentStroke is not null;

    /// <summary>The strokes drawn so far, in coordinates from 0 to 1.</summary>
    public IReadOnlyList<SignatureStroke> Strokes => _strokes;

    /// <summary>Raised whenever the drawing changes, so the dialog can update its buttons.</summary>
    public event Action? DrawingChanged;

    #endregion

    #region Getting the user onto the surface
    // The single most requested thing about this feature: put the pointer where the signature
    // starts, so the user does not have to hunt for a surface they cannot see.

    /// <summary>
    /// Moves the physical mouse cursor to where a signature begins — the left-centre of the pad —
    /// and says so.
    ///
    /// Moving the system cursor is normally a rude thing for a program to do. Here it is the whole
    /// point: without sight there is no way to bring the pointer to a specific place on screen, so
    /// the program has to do it. It happens only when this window opens, and only on request.
    /// </summary>
    public void PlacePointerAtStart()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0)
            return;

        try
        {
            var start = new Point(Width / 8, Height / 2);
            Cursor.Position = PointToScreen(start);

            _keyboardPen = new PointF(0.125f, 0.5f);

            _announce(
                "The mouse pointer has been moved to the left of the signature area, where a " +
                "signature usually starts. Hold the left mouse button down and draw. " +
                "Or press Space to put the pen down and draw with the arrow keys.",
                AnnouncementPriority.Assertive);
        }
        catch
        {
            // Moving the cursor can be refused by the system. The keyboard route still works, and
            // saying nothing is better than an error the user cannot act on.
        }
    }

    #endregion

    #region Drawing with a mouse

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left)
            return;

        Focus();
        BeginStroke();
        AddPoint(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_currentStroke is null)
        {
            // Even with the pen up, the pointer's position is sounded. That is how the user finds
            // the surface and gets a feel for its extent before committing to a stroke.
            if (ClientRectangle.Contains(e.Location))
                SoundPosition(e.Location, quiet: true);

            return;
        }

        AddPoint(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button == MouseButtons.Left)
            EndStroke();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        if (_currentStroke is not null)
        {
            // Running off the edge mid-stroke ends it, and says so. Otherwise the stroke would
            // silently continue from wherever the pointer re-entered, producing a line across the
            // signature that the user never drew.
            EndStroke();
            _cues.Play(AudioCue.Boundary);
            _announce("You left the signature area, so that stroke ended.", AnnouncementPriority.Assertive);
        }
    }

    #endregion

    #region Drawing with the keyboard
    // The route that makes this usable with no pointing device at all. Slower than a mouse, and
    // for someone who cannot use a mouse it is the difference between being able to sign and not.

    protected override bool IsInputKey(Keys keyData) => keyData switch
    {
        Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Space => true,
        _ => base.IsInputKey(keyData),
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.KeyCode)
        {
            case Keys.Space:
                ToggleKeyboardPen();
                e.Handled = e.SuppressKeyPress = true;
                return;

            case Keys.Left:
                MoveKeyboardPen(-KeyboardStep, 0, e.Shift);
                break;

            case Keys.Right:
                MoveKeyboardPen(KeyboardStep, 0, e.Shift);
                break;

            case Keys.Up:
                MoveKeyboardPen(0, -KeyboardStep, e.Shift);
                break;

            case Keys.Down:
                MoveKeyboardPen(0, KeyboardStep, e.Shift);
                break;

            default:
                return;
        }

        e.Handled = e.SuppressKeyPress = true;
    }

    private void ToggleKeyboardPen()
    {
        if (_penIsDown)
        {
            EndStroke();
            _penIsDown = false;
            _cues.Play(AudioCue.ValueAccepted);
            _announce("Pen up.", AnnouncementPriority.Assertive);
            return;
        }

        _penIsDown = true;
        BeginStroke();
        AddNormalisedPoint(_keyboardPen.X, _keyboardPen.Y);

        _cues.Play(AudioCue.FormField);
        _announce("Pen down. Use the arrow keys to draw. Hold Shift to move further each press.",
            AnnouncementPriority.Assertive);
    }

    private void MoveKeyboardPen(double dx, double dy, bool large)
    {
        double step = large ? 3 : 1;

        _keyboardPen = new PointF(
            (float)Math.Clamp(_keyboardPen.X + dx * step, 0, 1),
            (float)Math.Clamp(_keyboardPen.Y + dy * step, 0, 1));

        if (_penIsDown)
            AddNormalisedPoint(_keyboardPen.X, _keyboardPen.Y);

        SoundNormalisedPosition(_keyboardPen.X, _keyboardPen.Y, quiet: !_penIsDown);

        // At the edge, the pen has stopped moving even though the key was pressed. Said out loud,
        // because a key that appears to do nothing is exactly what disorients a keyboard user.
        if (_keyboardPen.X is <= 0.001f or >= 0.999f || _keyboardPen.Y is <= 0.001f or >= 0.999f)
            _cues.Play(AudioCue.Boundary);
    }

    #endregion

    #region Collecting strokes

    private void BeginStroke()
    {
        _currentStroke = new SignatureStroke();
        _currentScreenStroke = [];
    }

    private void EndStroke()
    {
        if (_currentStroke is null)
            return;

        if (_currentStroke.IsDrawable && _currentScreenStroke is not null)
        {
            _strokes.Add(_currentStroke);
            _screenStrokes.Add(_currentScreenStroke);
            DrawingChanged?.Invoke();
        }

        _currentStroke = null;
        _currentScreenStroke = null;
        Invalidate();
    }

    private void AddPoint(Point location)
    {
        if (Width <= 0 || Height <= 0)
            return;

        AddNormalisedPoint((double)location.X / Width, (double)location.Y / Height);
        SoundPosition(location, quiet: false);
    }

    private void AddNormalisedPoint(double x, double y)
    {
        _currentStroke?.Add(x, y);

        _currentScreenStroke?.Add(new PointF(
            (float)(Math.Clamp(x, 0, 1) * Width),
            (float)(Math.Clamp(y, 0, 1) * Height)));

        Invalidate();
    }

    #endregion

    #region Sounding the position
    // Pitch carries the position. Rising to the right and rising upward matches how people
    // instinctively map pitch to space, and it is the only channel fast enough to track a moving
    // pointer — speech would still be reading out the first coordinate when the pointer reached
    // the other side of the pad.

    private void SoundPosition(Point location, bool quiet)
    {
        if (Width <= 0 || Height <= 0)
            return;

        SoundNormalisedPosition((double)location.X / Width, (double)location.Y / Height, quiet);
    }

    private void SoundNormalisedPosition(double x, double y, bool quiet)
    {
        var now = DateTime.UtcNow;

        if (now - _lastToneAt < ToneInterval)
            return;

        _lastToneAt = now;

        // Horizontal position sets the note, vertical position shifts the octave. Two octaves is
        // wide enough to hear a difference across the pad and narrow enough to stay comfortable
        // over a long signature.
        double horizontal = Math.Clamp(x, 0, 1);
        double vertical = 1 - Math.Clamp(y, 0, 1);

        double frequency = 220 * Math.Pow(2, horizontal + vertical);

        _cues.PlayTone(frequency, quiet ? 30 : 45, quiet ? 0.18 : 0.4);
    }

    #endregion

    #region Painting
    // Drawn for the sighted person who may be helping, and for a user with some remaining vision.
    // High contrast and a thick pen, not a hairline.

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var graphics = e.Graphics;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // A baseline, as on a paper signature block, so a sighted helper can see where to aim.
        // In a high-contrast theme a faint grey line would be invisible, so the system's own
        // disabled-text colour is used, which every theme guarantees to be legible.
        using (var guide = new Pen(SystemColors.GrayText, 1))
        {
            int baseline = (int)(Height * 0.72);
            graphics.DrawLine(guide, Width / 12, baseline, Width - Width / 12, baseline);
        }

        using var pen = new Pen(ForeColor, 2.5f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
        };

        foreach (var stroke in _screenStrokes)
        {
            if (stroke.Count >= 2)
                graphics.DrawLines(pen, stroke.ToArray());
        }

        if (_currentScreenStroke is { Count: >= 2 })
            graphics.DrawLines(pen, _currentScreenStroke.ToArray());

        // Where the keyboard pen is sitting, so a helper can see it too.
        if (Focused)
        {
            using var marker = new Pen(SystemColors.Highlight, 2);
            var at = new Point((int)(_keyboardPen.X * Width), (int)(_keyboardPen.Y * Height));
            graphics.DrawEllipse(marker, at.X - 5, at.Y - 5, 10, 10);
        }
    }

    #endregion

    #region Clearing and reporting

    /// <summary>Removes everything drawn.</summary>
    public void ClearDrawing()
    {
        _strokes.Clear();
        _screenStrokes.Clear();
        _currentStroke = null;
        _currentScreenStroke = null;
        _penIsDown = false;
        _keyboardPen = new PointF(0.125f, 0.5f);

        Invalidate();
        DrawingChanged?.Invoke();
    }

    /// <summary>
    /// Describes what has been drawn, so the user can judge it without seeing it. The extent
    /// matters more than the stroke count: three strokes covering a tenth of the pad is a scribble,
    /// not a signature.
    /// </summary>
    public string DescribeDrawing()
    {
        if (_strokes.Count == 0)
            return "Nothing drawn yet.";

        var mark = SignatureMark.FromStrokes(_strokes, string.Empty);
        int percent = (int)Math.Round(mark.DrawnExtent * 100);

        string size = mark.IsSuspiciouslySmall
            ? "That is very small for a signature — it may just be a slip of the mouse."
            : "That looks like a signature.";

        return $"{_strokes.Count} {(_strokes.Count == 1 ? "stroke" : "strokes")}, " +
               $"covering about {percent} percent of the area. {size}";
    }

    #endregion
}

#endregion
