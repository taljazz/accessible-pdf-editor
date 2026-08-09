using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Rendering.Skia;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace AccessiblePdfEditor.Rendering;

// =====================================================================================
//  PageRenderer.cs
//
//  Turns a page into a picture, for the people who can see one.
//
//  This is a SUPPLEMENT and must stay one. The text view is what gives a screen reader its
//  review cursor, Say All, braille tracking and text selection; a rendered page gives
//  assistive technology precisely nothing — it is a rectangle of pixels. If this ever
//  became the primary surface the whole product would regress into an ordinary PDF viewer
//  with worse accessibility than the thing it replaced.
//
//  What it is genuinely for:
//
//  1. A SIGHTED PERSON HELPING OUT can see the page as it really is, rather than reading a
//     linearised transcript of it.
//  2. PARTIAL SIGHT is common among screen-reader users. Being able to zoom into the actual
//     page, not just enlarge the extracted text, is a real capability for them.
//  3. DESCRIBING AN IMAGE. This is the one that matters most. The remediation workflow asks
//     for alt text, and until now nobody using this program could see the image they were
//     being asked to describe — not the blind user, obviously, but not a sighted helper
//     either. Cropping the render to a figure's bounds fixes that, and it is why this class
//     exposes a crop as well as a whole page.
// =====================================================================================

#region PageRenderer

/// <summary>Renders document pages to images, with caching.</summary>
public sealed class PageRenderer : IDisposable
{
    #region State
    // Its own document handle, deliberately. The loader closes its handle as soon as the model has
    // been built, because holding a file open for the life of a document would lock it against the
    // save that has to replace it. Rendering needs a live handle, so it opens its own and closes it
    // when the document does.

    private readonly Lock _gate = new();
    private readonly Dictionary<(int Page, int Scale), byte[]> _cache = [];

    private PigDocument? _document;
    private string? _path;
    private bool _disposed;

    /// <summary>Whether a document is open and can be rendered.</summary>
    public bool IsAvailable => _document is not null;

    /// <summary>Why rendering is unavailable, when it is.</summary>
    public string? UnavailableReason { get; private set; }

    #endregion

    #region Opening and closing

    /// <summary>
    /// Opens a file for rendering. Failure is not fatal: the editor works entirely without a
    /// rendered view, so a renderer that cannot start simply reports why and stays quiet.
    /// </summary>
    public void Open(string filePath)
    {
        lock (_gate)
        {
            CloseInternal();

            try
            {
                var document = PigDocument.Open(filePath, new ParsingOptions
                {
                    UseLenientParsing = true,
                    SkipMissingFonts = true,
                });

                document.AddSkiaPageFactory();

                _document = document;
                _path = filePath;
                UnavailableReason = null;
            }
            catch (Exception ex)
            {
                _document = null;
                UnavailableReason = $"The page picture could not be prepared: {ex.Message}";
            }
        }
    }

    /// <summary>Closes the render handle and forgets every cached page.</summary>
    public void Close()
    {
        lock (_gate)
            CloseInternal();
    }

    private void CloseInternal()
    {
        _cache.Clear();

        try { _document?.Dispose(); }
        catch { /* Closing a handle that is already gone is not worth reporting. */ }

        _document = null;
        _path = null;
    }

    #endregion

    #region Rendering

    /// <summary>
    /// Renders a page at a scale, in PNG bytes. Returns null when rendering is unavailable or the
    /// page cannot be drawn.
    /// </summary>
    /// <param name="scale">
    /// Pixels per PDF point. 1 gives 72 dots per inch, which is too coarse to read; 2 is a
    /// comfortable default on an ordinary screen and renders a page in well under a fifth of a
    /// second.
    /// </param>
    public byte[]? RenderPage(int pageNumber, float scale = 2.0f)
    {
        lock (_gate)
        {
            if (_document is null || pageNumber < 1 || pageNumber > _document.NumberOfPages)
                return null;

            // Cached by tenths of a scale step, so nudging the zoom does not re-render every time
            // while still distinguishing genuinely different sizes.
            var key = (pageNumber, (int)Math.Round(scale * 10));

            if (_cache.TryGetValue(key, out byte[]? cached))
                return cached;

            try
            {
                using var stream = _document.GetPageAsPng(pageNumber, scale, 90);
                byte[] bytes = stream.ToArray();

                // Bounded: a long document at high zoom would otherwise grow without limit. Pages
                // are cheap to re-render, so the cache exists for scrolling smoothness rather than
                // to avoid expense.
                if (_cache.Count > 12)
                    _cache.Clear();

                _cache[key] = bytes;
                return bytes;
            }
            catch (Exception ex)
            {
                UnavailableReason = $"Page {pageNumber} could not be drawn: {ex.Message}";
                return null;
            }
        }
    }

    /// <summary>The size of a page in points, for laying out the view before anything is rendered.</summary>
    public (double Width, double Height)? GetPageSize(int pageNumber)
    {
        lock (_gate)
        {
            if (_document is null || pageNumber < 1 || pageNumber > _document.NumberOfPages)
                return null;

            try
            {
                var page = _document.GetPage(pageNumber);
                return (page.Width, page.Height);
            }
            catch
            {
                return null;
            }
        }
    }

    #endregion

    #region Cropping to an element
    // The part that changes what the remediation workflow can do. Being asked to describe an image
    // you cannot see is an impossible request; being shown it makes the task ordinary.

    /// <summary>
    /// Renders just the part of a page occupied by an element, with a little context around it.
    /// Returns null when it cannot be produced.
    /// </summary>
    /// <param name="padding">Extra points to include around the element, for context.</param>
    public Bitmap? RenderElement(DocumentElement element, float scale = 2.0f, double padding = 6)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (element.PageNumber < 1 || element.Bounds.IsEmpty)
            return null;

        var size = GetPageSize(element.PageNumber);
        if (size is not { } page)
            return null;

        byte[]? pageBytes = RenderPage(element.PageNumber, scale);
        if (pageBytes is null)
            return null;

        try
        {
            using var stream = new MemoryStream(pageBytes);
            using var rendered = new Bitmap(stream);

            var crop = ToImageRectangle(element.Bounds, page.Height, scale, padding, rendered.Size);

            if (crop.Width <= 0 || crop.Height <= 0)
                return null;

            // Copied into a new bitmap rather than returned as a reference into the source, because
            // the source is disposed the moment this method returns.
            var result = new Bitmap(crop.Width, crop.Height);

            using (var graphics = Graphics.FromImage(result))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(rendered, new Rectangle(0, 0, crop.Width, crop.Height),
                    crop, GraphicsUnit.Pixel);
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Converts a region in PDF user space into a rectangle in the rendered image.
    ///
    /// The flip is the whole of it: PDF measures upwards from the bottom of the page, an image
    /// measures downwards from the top. Getting this backwards produces a crop of the wrong part of
    /// the page, mirrored about the middle — which looks plausible enough to ship and is completely
    /// wrong.
    /// </summary>
    public static Rectangle ToImageRectangle(
        PageRegion region, double pageHeight, float scale, double padding, Size imageSize)
    {
        double left = (region.Left - padding) * scale;
        double top = (pageHeight - region.Top - padding) * scale;
        double width = (region.Width + padding * 2) * scale;
        double height = (region.Height + padding * 2) * scale;

        int x = (int)Math.Max(0, Math.Round(left));
        int y = (int)Math.Max(0, Math.Round(top));
        int w = (int)Math.Round(width);
        int h = (int)Math.Round(height);

        // Clamped to the image, so an element whose bounds run off the page does not ask for
        // pixels that are not there.
        w = Math.Min(w, imageSize.Width - x);
        h = Math.Min(h, imageSize.Height - y);

        return new Rectangle(x, y, Math.Max(0, w), Math.Max(0, h));
    }

    #endregion

    #region Disposal

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Close();
    }

    #endregion
}

#endregion
