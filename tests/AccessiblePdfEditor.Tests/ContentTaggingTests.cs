using System.Text;
using AccessiblePdfEditor.Ingestion;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Persistence;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace AccessiblePdfEditor.Tests;

// =====================================================================================
//  ContentTaggingTests.cs
//
//  Tests for the content-stream half of tagging an untagged document.
//
//  The decisive test here is "the computed positions land on the detected elements". The
//  whole approach rests on one claim: that walking the text matrix through a page's
//  operators produces the same coordinates that the layout analysis produced when it read
//  the page. If that is even slightly wrong, every tag is attached to the wrong paragraph
//  and the document ends up worse than untagged — confidently mislabelled instead of
//  honestly unlabelled.
//
//  It is checked against a real file rather than a synthetic one, because the interesting
//  operators are the ones real producers emit: a whole page inside a single BT/ET block,
//  positions built up from successive relative Td moves rather than absolute Tm, and text
//  drawn as glyph indices in a subset font.
// =====================================================================================

internal static class ContentTaggingTests
{
    public static void Register(TestRunner t)
    {
        RegisterReading(t);
        RegisterPositions(t);
        RegisterSafety(t);
    }

    #region Reading operators

    private static void RegisterReading(TestRunner t)
    {
        t.Group("content tagging — reading the stream");

        t.Test("text is found and marked", () =>
        {
            byte[] content = Encoding.Latin1.GetBytes(
                "BT /F1 12 Tf 72 700 Td (Hello) Tj ET");

            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => "P");

            t.AreEqual(1, tagged.Runs.Count, "the one show operator should be marked");
            t.AreEqual(72.0, tagged.Runs[0].X, "at the x the operators put it");
            t.AreEqual(700.0, tagged.Runs[0].Y, "and the y");
        });

        t.Test("marks are opened and closed around the text", () =>
        {
            byte[] content = Encoding.Latin1.GetBytes("BT 72 700 Td (Hello) Tj ET");
            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => "P");

            string result = Encoding.Latin1.GetString(tagged.Content);

            t.Says(result, "/P <</MCID 0>> BDC");
            t.Says(result, "EMC");
            t.IsTrue(result.IndexOf("BDC", StringComparison.Ordinal) < result.IndexOf("Tj", StringComparison.Ordinal),
                "the mark must open before the text it covers");
        });

        t.Test("text the classifier rejects is left unmarked", () =>
        {
            // Page furniture — running heads, folios — is an artifact, not content, and marking it
            // would put a page number into the reading order between two sentences.
            byte[] content = Encoding.Latin1.GetBytes("BT 72 700 Td (Hello) Tj ET");
            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => null);

            t.AreEqual(0, tagged.Runs.Count, "nothing should be marked");
            t.IsFalse(Encoding.Latin1.GetString(tagged.Content).Contains("BDC", StringComparison.Ordinal),
                "and no sequence should be opened");
        });

        t.Test("consecutive text with the same tag shares one mark", () =>
        {
            // Otherwise a paragraph drawn as eight separate show operators becomes eight
            // paragraphs, and a reader announces "paragraph" eight times inside one sentence.
            byte[] content = Encoding.Latin1.GetBytes(
                "BT 72 700 Td (One) Tj (Two) Tj (Three) Tj ET");

            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => "P");

            t.AreEqual(1, tagged.Runs.Count, "three shows in one paragraph should be one mark");
        });

        t.Test("identifiers continue from where the caller says", () =>
        {
            // A page that already carries marks must not have them duplicated.
            byte[] content = Encoding.Latin1.GetBytes("BT 72 700 Td (A) Tj 0 -20 Td (B) Tj ET");

            int call = 0;
            var tagged = ContentStreamTagger.Tag(content, 7, (_, _) => call++ == 0 ? "H1" : "P");

            t.AreEqual(7, tagged.Runs[0].MarkedContentId, "the first mark takes the given number");
            t.IsTrue(tagged.Runs.Count > 1 && tagged.Runs[1].MarkedContentId == 8, "and the next follows it");
        });

        t.Test("a string containing operator-like text does not confuse the reader", () =>
        {
            // A literal string can contain anything, including "ET" or "Tj". Parsing it as
            // operators would close the text object early and put marks in absurd places.
            byte[] content = Encoding.Latin1.GetBytes(
                "BT 72 700 Td (ET Tj BT nonsense) Tj ET");

            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => "P");

            t.AreEqual(1, tagged.Runs.Count, "there is one show operator, whatever the string says");
        });
    }

    #endregion

    #region Positions — the claim everything rests on

    private static void RegisterPositions(TestRunner t)
    {
        t.Group("content tagging — positions");

        t.Test("relative moves accumulate down the page", () =>
        {
            // Real producers rarely emit an absolute Tm per line; they emit one Td and then
            // successive relative moves. Getting the accumulation wrong drifts further from the
            // truth with every line, so the last line of a page lands nowhere near its paragraph.
            byte[] content = Encoding.Latin1.GetBytes(
                "BT 60 772 Td (A) Tj 0 -35 Td (B) Tj 0 -20 Td (C) Tj ET");

            var seen = new List<(double X, double Y)>();
            ContentStreamTagger.Tag(content, 0, (x, y) => { seen.Add((x, y)); return seen.Count.ToString(); });

            t.AreEqual(3, seen.Count, "three positions");
            t.AreEqual(772.0, seen[0].Y, "the first line");
            t.AreEqual(737.0, seen[1].Y, "35 points below it");
            t.AreEqual(717.0, seen[2].Y, "and 20 below that");
        });

        t.Test("the current transformation matrix is applied", () =>
        {
            byte[] content = Encoding.Latin1.GetBytes(
                "q 1 0 0 1 100 50 cm BT 10 10 Td (A) Tj ET Q");

            var seen = new List<(double X, double Y)>();
            ContentStreamTagger.Tag(content, 0, (x, y) => { seen.Add((x, y)); return "P"; });

            t.AreEqual(1, seen.Count, "one position");
            t.AreEqual(110.0, seen[0].X, "translated by the matrix");
            t.AreEqual(60.0, seen[0].Y, "in both directions");
        });

        t.Test("Q restores the matrix that q saved", () =>
        {
            byte[] content = Encoding.Latin1.GetBytes(
                "q 1 0 0 1 500 500 cm Q BT 72 700 Td (A) Tj ET");

            var seen = new List<(double X, double Y)>();
            ContentStreamTagger.Tag(content, 0, (x, y) => { seen.Add((x, y)); return "P"; });

            t.AreEqual(72.0, seen[0].X, "the translation was undone by Q");
            t.AreEqual(700.0, seen[0].Y, "in both directions");
        });

        t.Test("the computed positions land on the elements found by layout", () =>
        {
            // THE test. Two entirely separate readings of the same page — one by the layout
            // analysis through PdfPig, one by walking the operators here — have to agree about
            // where the text is, or every tag goes on the wrong paragraph.
            string sample = FindSample();

            if (!File.Exists(sample))
            {
                t.IsTrue(true, "the sample is not present, so this test is skipped");
                return;
            }

            var document = new PdfPigDocumentLoader().Load(sample).Document;
            t.IsNotNull(document, "the sample should load");

            var onPageOne = document!.ReadingOrder
                .Where(e => e.PageNumber == 1 && !e.Bounds.IsEmpty && e is not PageElement)
                .ToList();

            t.IsTrue(onPageOne.Count > 0, "the layout analysis should have found elements on page 1");

            using var sharp = PdfReader.Open(sample, PdfDocumentOpenMode.Modify);
            byte[] content = ReadContent(sharp.Pages[0]);

            var positions = new List<(double X, double Y)>();
            ContentStreamTagger.Tag(content, 0, (x, y) => { positions.Add((x, y)); return "P"; });

            t.IsTrue(positions.Count > 0, "the tagger should have found text to mark");

            // A show operator's origin is the BASELINE of the text, while a detected element's
            // bounds are the glyph box, so the baseline sits at or just above the bottom edge.
            // The tolerance covers descenders and nothing more.
            const double Tolerance = 8.0;
            int landed = 0;

            foreach (var (x, y) in positions)
            {
                bool inside = onPageOne.Any(e =>
                    x >= e.Bounds.Left - Tolerance && x <= e.Bounds.Right + Tolerance
                    && y >= e.Bounds.Bottom - Tolerance && y <= e.Bounds.Top + Tolerance);

                if (inside)
                    landed++;
            }

            double proportion = (double)landed / positions.Count;

            t.IsTrue(proportion >= 0.8,
                $"the operator positions should agree with the layout analysis, but only " +
                $"{landed} of {positions.Count} ({proportion:P0}) landed on a detected element");
        });
    }

    #endregion

    #region Safety

    private static void RegisterSafety(TestRunner t)
    {
        t.Group("content tagging — safety");

        t.Test("no operator is ever modified, only insertions are made", () =>
        {
            // The drawing must be bit-identical: the primary user cannot check what the page looks
            // like, so a tagger that could change it would be unverifiable by the person using it.
            byte[] content = Encoding.Latin1.GetBytes(
                "BT /F1 12 Tf 72 700 Td (Hello) Tj 0 -14 Td (World) Tj ET");

            int call = 0;
            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => call++ == 0 ? "H1" : "P");

            string result = Encoding.Latin1.GetString(tagged.Content);

            // Everything that was there is still there, in order, with only marks added between.
            string stripped = result
                .Replace("/H1 <</MCID 0>> BDC\n", string.Empty)
                .Replace("/P <</MCID 1>> BDC\n", string.Empty)
                .Replace("EMC\n", string.Empty)
                .Replace("\nEMC", string.Empty);

            t.AreEqual(Encoding.Latin1.GetString(content), stripped.TrimEnd('\n'),
                "removing the inserted marks should give back exactly the original operators");
        });

        t.Test("every opened sequence is closed", () =>
        {
            // An unclosed BDC swallows everything drawn after it, which in a multi-stream page
            // means the rest of the document's content joins the wrong tag.
            byte[] content = Encoding.Latin1.GetBytes("BT 72 700 Td (A) Tj ET");
            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => "P");

            string result = Encoding.Latin1.GetString(tagged.Content);

            t.AreEqual(CountOf(result, "BDC"), CountOf(result, "EMC"),
                "there must be exactly as many EMC as BDC");
        });

        t.Test("a stream with no text is returned unchanged", () =>
        {
            byte[] content = Encoding.Latin1.GetBytes("q 1 0 0 1 0 0 cm 100 100 m 200 200 l S Q");
            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => "P");

            t.IsFalse(tagged.MarkedAnything, "there is nothing to mark");
            t.AreEqual(Encoding.Latin1.GetString(content), Encoding.Latin1.GetString(tagged.Content),
                "and the drawing should be untouched");
        });
    }

    #endregion

    #region Helpers

    private static int CountOf(string haystack, string needle)
    {
        int count = 0, index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    internal static byte[] ReadContent(PdfPage page)
    {
        using var buffer = new MemoryStream();

        foreach (var item in page.Contents.Elements)
        {
            var stream = item switch
            {
                PdfReference reference => (reference.Value as PdfDictionary)?.Stream,
                PdfDictionary dictionary => dictionary.Stream,
                _ => null,
            };

            if (stream is null)
                continue;

            byte[] bytes = stream.UnfilteredValue;
            buffer.Write(bytes, 0, bytes.Length);
            buffer.WriteByte((byte)'\n');
        }

        return buffer.ToArray();
    }

    private static string FindSample() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "samples", "Sample form (deliberately inaccessible).pdf"));

    #endregion
}
