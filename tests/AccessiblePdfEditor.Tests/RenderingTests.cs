using AccessiblePdfEditor.Ingestion;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Rendering;

namespace AccessiblePdfEditor.Tests;

// =====================================================================================
//  RenderingTests.cs
//
//  Tests for the page picture, and above all for the coordinate flip.
//
//  The flip is the part most likely to be silently wrong. PDF measures upwards from the
//  bottom of a page; an image measures downwards from the top. Get it backwards and the
//  crop comes out mirrored about the middle of the page — which is plausible enough to
//  ship, because it still returns a picture of *something*.
//
//  That matters more here than it usually would: the crop is what a sighted helper is
//  shown when asked to describe an image. Handing them the wrong part of the page would
//  produce a confidently written, completely wrong alt text, and neither they nor the blind
//  user would have any way to notice.
// =====================================================================================

internal static class RenderingTests
{
    public static void Register(TestRunner t)
    {
        RegisterCoordinates(t);
        RegisterRendering(t);
    }

    #region The coordinate flip

    private static void RegisterCoordinates(TestRunner t)
    {
        t.Group("page picture — coordinates");

        t.Test("an element at the top of the page crops from the top of the image", () =>
        {
            // The test that catches an inverted flip. On an A4 page 842 points tall, something
            // sitting at the top has a HIGH PDF y, and must map to a LOW image y.
            var atTop = new PageRegion(60, 780, 300, 820);

            var rect = PageRenderer.ToImageRectangle(
                atTop, pageHeight: 842, scale: 1f, padding: 0, imageSize: new Size(595, 842));

            t.IsTrue(rect.Top < 100,
                $"an element near the top of the page should crop near the top of the image, but y was {rect.Top}");
        });

        t.Test("an element at the bottom of the page crops from the bottom of the image", () =>
        {
            var atBottom = new PageRegion(60, 20, 300, 60);

            var rect = PageRenderer.ToImageRectangle(
                atBottom, pageHeight: 842, scale: 1f, padding: 0, imageSize: new Size(595, 842));

            t.IsTrue(rect.Top > 740,
                $"an element near the bottom should crop near the bottom, but y was {rect.Top}");
        });

        t.Test("the crop is the right size", () =>
        {
            var region = new PageRegion(100, 400, 300, 500);

            var rect = PageRenderer.ToImageRectangle(
                region, pageHeight: 842, scale: 1f, padding: 0, imageSize: new Size(595, 842));

            t.AreEqual(200, rect.Width, "200 points wide at scale 1 is 200 pixels");
            t.AreEqual(100, rect.Height, "100 points tall at scale 1 is 100 pixels");
        });

        t.Test("scale multiplies both position and size", () =>
        {
            var region = new PageRegion(100, 400, 300, 500);

            var rect = PageRenderer.ToImageRectangle(
                region, pageHeight: 842, scale: 2f, padding: 0, imageSize: new Size(1190, 1684));

            t.AreEqual(200, rect.Left, "the left edge should double");
            t.AreEqual(400, rect.Width, "the width should double");
            t.AreEqual(200, rect.Height, "the height should double");
        });

        t.Test("padding grows the crop on every side", () =>
        {
            var region = new PageRegion(100, 400, 300, 500);

            var without = PageRenderer.ToImageRectangle(
                region, 842, 1f, padding: 0, new Size(595, 842));

            var with = PageRenderer.ToImageRectangle(
                region, 842, 1f, padding: 10, new Size(595, 842));

            t.AreEqual(without.Left - 10, with.Left, "padding should move the left edge out");
            t.AreEqual(without.Width + 20, with.Width, "and add to both sides of the width");
        });

        t.Test("a crop is clamped to the image rather than running off it", () =>
        {
            // An element flush against the page edge, plus padding, would otherwise ask for pixels
            // that do not exist and throw when the crop is drawn.
            var atEdge = new PageRegion(0, 0, 595, 842);

            var rect = PageRenderer.ToImageRectangle(
                atEdge, pageHeight: 842, scale: 1f, padding: 20, imageSize: new Size(595, 842));

            t.IsTrue(rect.Left >= 0 && rect.Top >= 0, "the crop must start inside the image");
            t.IsTrue(rect.Right <= 595, $"the crop must not run off the right edge, but ended at {rect.Right}");
            t.IsTrue(rect.Bottom <= 842, $"the crop must not run off the bottom, but ended at {rect.Bottom}");
        });
    }

    #endregion

    #region Rendering itself

    private static void RegisterRendering(TestRunner t)
    {
        t.Group("page picture — rendering");

        string sample = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "samples", "Sample form (deliberately inaccessible).pdf");

        sample = Path.GetFullPath(sample);
        bool haveSample = File.Exists(sample);

        t.Test("a renderer with no document open reports so rather than throwing", () =>
        {
            using var renderer = new PageRenderer();

            t.IsFalse(renderer.IsAvailable, "nothing is open yet");
            t.IsNull(renderer.RenderPage(1), "rendering should return nothing, not throw");
        });

        t.Test("opening a file that is not a PDF fails cleanly", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"not-a-pdf-{Guid.NewGuid():N}.pdf");
            File.WriteAllText(path, "plainly not a PDF");

            try
            {
                using var renderer = new PageRenderer();
                renderer.Open(path);

                t.IsFalse(renderer.IsAvailable, "it should not report itself as ready");
                t.IsNotNull(renderer.UnavailableReason, "and it should say why");
            }
            finally
            {
                File.Delete(path);
            }
        });

        if (!haveSample)
        {
            t.Test("rendering the sample (skipped: sample not built yet)", () => { });
            return;
        }

        t.Test("the sample renders to a real image", () =>
        {
            using var renderer = new PageRenderer();
            renderer.Open(sample);

            t.IsTrue(renderer.IsAvailable, $"the sample should open: {renderer.UnavailableReason}");

            byte[]? png = renderer.RenderPage(1, 1.5f);
            t.IsNotNull(png, "page 1 should render");
            t.IsTrue(png!.Length > 1000, $"the image should have real content, but was {png.Length} bytes");

            using var stream = new MemoryStream(png);
            using var bitmap = new Bitmap(stream);

            t.IsTrue(bitmap.Width > 400 && bitmap.Height > 600,
                $"an A4 page at 1.5x should be roughly 892 by 1263, but was {bitmap.Width} by {bitmap.Height}");
        });

        t.Test("asking for a page that does not exist returns nothing", () =>
        {
            using var renderer = new PageRenderer();
            renderer.Open(sample);

            t.IsNull(renderer.RenderPage(999), "there is no page 999");
            t.IsNull(renderer.RenderPage(0), "there is no page 0");
        });

        t.Test("the figure in the sample can be cropped out for describing", () =>
        {
            // The capability that makes "describe this image" a possible request for a sighted
            // helper. If this returns nothing, the alt-text prompt silently loses its picture.
            var loaded = new PdfPigDocumentLoader().Load(sample);
            t.IsTrue(loaded.IsSuccess, "the sample should load");

            var figure = loaded.Document!.Figures.FirstOrDefault(f => !f.IsLikelyDecorativeBySize);
            t.IsNotNull(figure, "the sample contains a chart");

            using var renderer = new PageRenderer();
            renderer.Open(sample);

            using var crop = renderer.RenderElement(figure!, scale: 2f, padding: 8);

            t.IsNotNull(crop, "the figure should crop out of the page");
            t.IsTrue(crop!.Width > 50 && crop.Height > 50,
                $"the crop should be a real picture, but was {crop.Width} by {crop.Height}");

            // Much smaller than the whole page: proof it cropped rather than returning everything.
            t.IsTrue(crop.Height < 1400, $"the crop should be smaller than the page, but was {crop.Height} tall");
        });

        t.Test("rendering the same page twice is served from the cache", () =>
        {
            using var renderer = new PageRenderer();
            renderer.Open(sample);

            byte[]? first = renderer.RenderPage(2, 1.5f);
            byte[]? second = renderer.RenderPage(2, 1.5f);

            t.IsNotNull(first, "the page should render");
            t.IsTrue(ReferenceEquals(first, second), "the second call should return the cached image");
        });

        t.Test("closing releases the file so it can still be saved over", () =>
        {
            // The renderer holds its own handle on the file. If it did not let go, every save would
            // fail with the file locked by this program itself.
            string copy = Path.Combine(Path.GetTempPath(), $"apde-render-{Guid.NewGuid():N}.pdf");
            File.Copy(sample, copy, overwrite: true);

            try
            {
                var renderer = new PageRenderer();
                renderer.Open(copy);
                renderer.RenderPage(1);
                renderer.Close();

                // Would throw if the handle were still open.
                using var probe = File.Open(copy, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                t.IsTrue(true, "the file should be writable after closing");

                renderer.Dispose();
            }
            finally
            {
                try { File.Delete(copy); } catch { }
            }
        });
    }

    #endregion
}
