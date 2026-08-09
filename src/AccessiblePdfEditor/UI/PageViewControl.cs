using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Rendering;

namespace AccessiblePdfEditor.UI;

// =====================================================================================
//  PageViewControl.cs
//
//  Shows the page as it actually looks, beside the text.
//
//  THE RULE THIS CONTROL LIVES BY: it never takes focus by surprise, and it is never
//  required for anything. Focus belongs to the text view, which is where a screen reader
//  does its work. This pane follows along — when the reading position moves, the page
//  scrolls and the current element is outlined — but it does not lead, and every command
//  in the program works with it closed.
//
//  It is still a real, named, keyboard-reachable control rather than a decorative surface.
//  Someone with partial sight may want to Tab into it and scroll around, and someone using
//  a screen reader is entitled to be told what it is rather than meeting an unlabelled
//  rectangle. What it announces is the page number and the fact that this is a picture —
//  because a picture of a page is the one thing in this application a screen reader cannot
//  read, and saying so is more honest than letting it appear to be empty.
// =====================================================================================

#region PageViewControl

/// <summary>Displays a rendered page, following the reading position.</summary>
public sealed class PageViewControl : Panel
{
    #region State

    private readonly PageRenderer _renderer;

    private Bitmap? _rendered;
    private int _pageNumber;
    private float _scale = 1.5f;
    private Rectangle _highlight = Rectangle.Empty;
    private double _pageHeightPoints;
    private string _status = "No page to show.";

    /// <summary>The zoom levels offered, as pixels per PDF point.</summary>
    private static readonly float[] ScaleChoices = [0.75f, 1.0f, 1.5f, 2.0f, 3.0f, 4.0f];

    public PageViewControl(PageRenderer renderer)
    {
        _renderer = renderer;

        DoubleBuffered = true;
        AutoScroll = true;
        BackColor = SystemColors.AppWorkspace;
        BorderStyle = BorderStyle.FixedSingle;

        TabStop = true;

        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Page picture";
        AccessibleDescription =
            "A picture of the page as it is printed. It is here for people who can see it; " +
            "a screen reader cannot read a picture. Everything in the document is available as " +
            "text in the document view. Use Ctrl+plus and Ctrl+minus to zoom.";
    }

    /// <summary>
    /// The zoom level, as pixels per PDF point. Named ZoomLevel rather than Scale because Control
    /// already has a Scale method, and a name that shadows it is a trap for the next reader.
    /// </summary>
    public float ZoomLevel => _scale;

    /// <summary>The page currently shown.</summary>
    public int PageNumber => _pageNumber;

    #endregion

    #region Showing a page

    /// <summary>Shows a page, and outlines an element on it when one is given.</summary>
    public void ShowPage(int pageNumber, DocumentElement? highlight = null)
    {
        if (!_renderer.IsAvailable)
        {
            _status = _renderer.UnavailableReason ?? "The page picture is not available.";
            ClearRendered();
            return;
        }

        if (pageNumber < 1)
            return;

        bool pageChanged = pageNumber != _pageNumber || _rendered is null;

        if (pageChanged)
        {
            byte[]? bytes = _renderer.RenderPage(pageNumber, _scale);

            if (bytes is null)
            {
                _status = _renderer.UnavailableReason ?? $"Page {pageNumber} could not be drawn.";
                ClearRendered();
                return;
            }

            var size = _renderer.GetPageSize(pageNumber);
            _pageHeightPoints = size?.Height ?? 842;

            try
            {
                using var stream = new MemoryStream(bytes);
                var bitmap = new Bitmap(stream);

                _rendered?.Dispose();
                _rendered = bitmap;
            }
            catch (Exception ex)
            {
                _status = $"Page {pageNumber} could not be shown: {ex.Message}";
                ClearRendered();
                return;
            }

            _pageNumber = pageNumber;
            AutoScrollMinSize = _rendered.Size;
            _status = $"Page {pageNumber}.";
        }

        SetHighlight(highlight);
        Invalidate();
    }

    /// <summary>Outlines an element and scrolls it into view.</summary>
    private void SetHighlight(DocumentElement? element)
    {
        if (element is null || element.Bounds.IsEmpty || _rendered is null)
        {
            _highlight = Rectangle.Empty;
            return;
        }

        _highlight = PageRenderer.ToImageRectangle(
            element.Bounds, _pageHeightPoints, _scale, padding: 2, _rendered.Size);

        ScrollHighlightIntoView();
    }

    /// <summary>
    /// Scrolls so the outlined element is visible, without moving if it already is. Constant
    /// scrolling is disorienting for anyone watching the pane while someone else drives the
    /// keyboard, which is exactly the shared-screen case this view exists for.
    /// </summary>
    private void ScrollHighlightIntoView()
    {
        if (_highlight.IsEmpty || _rendered is null)
            return;

        var visible = new Rectangle(
            -AutoScrollPosition.X, -AutoScrollPosition.Y, ClientSize.Width, ClientSize.Height);

        if (visible.Contains(_highlight))
            return;

        int targetY = Math.Max(0, _highlight.Top - ClientSize.Height / 3);
        int targetX = Math.Max(0, _highlight.Left - ClientSize.Width / 4);

        AutoScrollPosition = new Point(targetX, targetY);
    }

    private void ClearRendered()
    {
        _rendered?.Dispose();
        _rendered = null;
        _highlight = Rectangle.Empty;
        AutoScrollMinSize = Size.Empty;
        Invalidate();
    }

    #endregion

    #region Zoom

    /// <summary>Steps the zoom up or down. Returns the new level described in words.</summary>
    public string AdjustZoom(bool larger)
    {
        int current = Array.FindIndex(ScaleChoices, s => Math.Abs(s - _scale) < 0.01f);
        if (current < 0) current = 2;

        int next = Math.Clamp(current + (larger ? 1 : -1), 0, ScaleChoices.Length - 1);

        if (next == current)
            return larger ? "Already at the largest zoom." : "Already at the smallest zoom.";

        _scale = ScaleChoices[next];

        // Forced to re-render at the new size.
        int page = _pageNumber;
        _pageNumber = 0;
        ShowPage(page);

        return $"Page picture at {_scale * 100:0} percent.";
    }

    #endregion

    #region Painting

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var graphics = e.Graphics;

        if (_rendered is null)
        {
            // Drawn as text rather than left blank, so a sighted user is told why there is no
            // picture instead of wondering whether the program has hung.
            using var brush = new SolidBrush(SystemColors.ControlText);
            using var font = new Font(Font.FontFamily, 10);

            graphics.DrawString(_status, font, brush,
                new RectangleF(12, 12, Math.Max(40, ClientSize.Width - 24), ClientSize.Height - 24));

            return;
        }

        graphics.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
        graphics.DrawImage(_rendered, 0, 0, _rendered.Width, _rendered.Height);

        if (_highlight.IsEmpty)
            return;

        // The current element, outlined. System highlight colour so it stays visible in a
        // high-contrast theme, and thick enough to find at a glance.
        using var pen = new Pen(SystemColors.Highlight, 2.5f);
        graphics.DrawRectangle(pen, _highlight);
    }

    #endregion

    #region Keyboard
    // Scrolling only. Nothing here changes the document, because this pane is a view of it and a
    // keystroke that edited from a picture would be unreachable for the primary user.

    protected override bool IsInputKey(Keys keyData) => keyData switch
    {
        Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown
            or Keys.Home or Keys.End => true,
        _ => base.IsInputKey(keyData),
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (_rendered is null)
            return;

        int step = 60;
        var position = AutoScrollPosition;

        // AutoScrollPosition reports negative values but is set with positive ones, which is a
        // long-standing WinForms oddity and the source of a great many inverted-scrolling bugs.
        int x = -position.X;
        int y = -position.Y;

        switch (e.KeyCode)
        {
            case Keys.Left: x -= step; break;
            case Keys.Right: x += step; break;
            case Keys.Up: y -= step; break;
            case Keys.Down: y += step; break;
            case Keys.PageUp: y -= ClientSize.Height; break;
            case Keys.PageDown: y += ClientSize.Height; break;
            case Keys.Home: x = 0; y = 0; break;
            case Keys.End: y = _rendered.Height; break;
            default: return;
        }

        AutoScrollPosition = new Point(Math.Max(0, x), Math.Max(0, y));
        e.Handled = e.SuppressKeyPress = true;
    }

    #endregion

    #region Disposal

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _rendered?.Dispose();

        base.Dispose(disposing);
    }

    #endregion
}

#endregion
