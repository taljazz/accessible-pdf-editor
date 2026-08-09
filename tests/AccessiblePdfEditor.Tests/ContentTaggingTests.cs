using System.Text;
using AccessiblePdfEditor.Ingestion;
using AccessiblePdfEditor.Model;
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
        RegisterWholeDocument(t);
    }

    #region Reading operators

    private static void RegisterReading(TestRunner t)
    {
        t.Group("content tagging — reading the stream");

        t.Test("text is found and marked", () =>
        {
            byte[] content = Encoding.Latin1.GetBytes(
                "BT /F1 12 Tf 72 700 Td (Hello) Tj ET");

            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => new ContentTag("P", 1));

            t.AreEqual(1, tagged.Runs.Count, "the one show operator should be marked");
            t.AreEqual(72.0, tagged.Runs[0].X, "at the x the operators put it");
            t.AreEqual(700.0, tagged.Runs[0].Y, "and the y");
        });

        t.Test("marks are opened and closed around the text", () =>
        {
            byte[] content = Encoding.Latin1.GetBytes("BT 72 700 Td (Hello) Tj ET");
            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => new ContentTag("P", 1));

            string result = Encoding.Latin1.GetString(tagged.Content);

            t.Says(result, "/P <</MCID 0>> BDC");
            t.Says(result, "EMC");

            // Before the OPERAND, not merely before the operator. This distinction is not
            // pedantry: a mark inserted between (Hello) and Tj leaves Tj with nothing to show, and
            // the text silently disappears from the page while the file still opens. The weaker
            // form of this assertion — that BDC precedes Tj — passed while exactly that was
            // happening, and a third of the sample's text was being destroyed.
            t.IsTrue(result.IndexOf("BDC", StringComparison.Ordinal) < result.IndexOf("(Hello)", StringComparison.Ordinal),
                "the mark must open before the text's own operand, not between it and the operator");
        });

        t.Test("a string operand is not separated from its operator", () =>
        {
            // The same fault as above, checked directly on the shape of the output rather than on
            // an index, because this is the one that costs a document its text.
            byte[] content = Encoding.Latin1.GetBytes("BT 72 700 Td <00410042> Tj ET");
            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => new ContentTag("P", 1));

            string result = Encoding.Latin1.GetString(tagged.Content);
            int operand = result.IndexOf("<00410042>", StringComparison.Ordinal);
            int show = result.IndexOf("Tj", StringComparison.Ordinal);

            t.IsTrue(operand >= 0 && show > operand, "the operand should still precede its operator");
            t.IsFalse(result[operand..show].Contains("BDC", StringComparison.Ordinal),
                "nothing may be inserted between an operand and the operator that consumes it");
        });

        t.Test("text the classifier rejects is left unmarked", () =>
        {
            // Page furniture — running heads, folios — is an artifact, not content, and marking it
            // would put a page number into the reading order between two sentences.
            byte[] content = Encoding.Latin1.GetBytes("BT 72 700 Td (Hello) Tj ET");
            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => (ContentTag?)null);

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

            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => new ContentTag("P", 1));

            t.AreEqual(1, tagged.Runs.Count, "three shows in one paragraph should be one mark");
        });

        t.Test("two different paragraphs are not run together", () =>
        {
            // The reason the classifier reports an element key rather than just a tag name. Both of
            // these are "P", and comparing names alone would fuse them into one paragraph — so the
            // document would have one where it should have two, and a reader moving by paragraph
            // would sail straight past the boundary.
            byte[] content = Encoding.Latin1.GetBytes(
                "BT 72 700 Td (First para) Tj 0 -40 Td (Second para) Tj ET");

            int call = 0;
            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => new ContentTag("P", ++call));

            t.AreEqual(2, tagged.Runs.Count, "two paragraphs should be two marks");
            t.IsTrue(tagged.Runs[0].MarkedContentId != tagged.Runs[1].MarkedContentId,
                "and they must have different identifiers");
        });

        t.Test("identifiers continue from where the caller says", () =>
        {
            // A page that already carries marks must not have them duplicated.
            byte[] content = Encoding.Latin1.GetBytes("BT 72 700 Td (A) Tj 0 -20 Td (B) Tj ET");

            int call = 0;
            var tagged = ContentStreamTagger.Tag(content, 7, (_, _) => new ContentTag(call++ == 0 ? "H1" : "P", call));

            t.AreEqual(7, tagged.Runs[0].MarkedContentId, "the first mark takes the given number");
            t.IsTrue(tagged.Runs.Count > 1 && tagged.Runs[1].MarkedContentId == 8, "and the next follows it");
        });

        t.Test("a string containing operator-like text does not confuse the reader", () =>
        {
            // A literal string can contain anything, including "ET" or "Tj". Parsing it as
            // operators would close the text object early and put marks in absurd places.
            byte[] content = Encoding.Latin1.GetBytes(
                "BT 72 700 Td (ET Tj BT nonsense) Tj ET");

            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => new ContentTag("P", 1));

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
            ContentStreamTagger.Tag(content, 0, (x, y) => { seen.Add((x, y)); return new ContentTag("P", seen.Count); });

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
            ContentStreamTagger.Tag(content, 0, (x, y) => { seen.Add((x, y)); return new ContentTag("P", 1); });

            t.AreEqual(1, seen.Count, "one position");
            t.AreEqual(110.0, seen[0].X, "translated by the matrix");
            t.AreEqual(60.0, seen[0].Y, "in both directions");
        });

        t.Test("Q restores the matrix that q saved", () =>
        {
            byte[] content = Encoding.Latin1.GetBytes(
                "q 1 0 0 1 500 500 cm Q BT 72 700 Td (A) Tj ET");

            var seen = new List<(double X, double Y)>();
            ContentStreamTagger.Tag(content, 0, (x, y) => { seen.Add((x, y)); return new ContentTag("P", 1); });

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
            ContentStreamTagger.Tag(content, 0, (x, y) => { positions.Add((x, y)); return new ContentTag("P", 1); });

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
            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => new ContentTag(call++ == 0 ? "H1" : "P", call));

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
            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => new ContentTag("P", 1));

            string result = Encoding.Latin1.GetString(tagged.Content);

            t.AreEqual(CountOf(result, "BDC"), CountOf(result, "EMC"),
                "there must be exactly as many EMC as BDC");
        });

        t.Test("a stream with no text is returned unchanged", () =>
        {
            byte[] content = Encoding.Latin1.GetBytes("q 1 0 0 1 0 0 cm 100 100 m 200 200 l S Q");
            var tagged = ContentStreamTagger.Tag(content, 0, (_, _) => new ContentTag("P", 1));

            t.IsFalse(tagged.MarkedAnything, "there is nothing to mark");
            t.AreEqual(Encoding.Latin1.GetString(content), Encoding.Latin1.GetString(tagged.Content),
                "and the drawing should be untouched");
        });
    }

    #endregion

    #region Tagging a whole document

    private static void RegisterWholeDocument(TestRunner t)
    {
        t.Group("content tagging — a whole document");

        t.Test("an untagged document comes back tagged, with its text intact", () =>
        {
            // The end of the whole chain: layout analysis, content marking, structure tree. What
            // makes it worth asserting on the FILE rather than on the objects is that every
            // intermediate step can look right while the result is a document no other reader can
            // use — which is the state this program exists to get people out of.
            WithSampleCopy(t, (document, path, saver) =>
            {
                t.IsTrue(document.TaggedStatus is not TaggedStatus.FullyTagged,
                    "the sample is supposed to start untagged");

                string textBefore = AllText(path);

                var result = saver.Save(document, new SaveOptions
                {
                    TargetPath = path,
                    AddStructureTags = true,
                    CreateBackup = false,
                });

                t.IsTrue(result.Outcome is SaveOutcome.Saved, $"the save should succeed: {result.Message}");

                var tagging = saver.LastStructureTreeResult;
                t.IsNotNull(tagging, "the save should report what the tagging achieved");
                t.IsTrue(tagging!.Value.ElementsTagged > 0, "it should have tagged something");

                // The text is the document. Marked content is inserted around the drawing
                // operators, never into them, so not one character may move.
                t.AreEqual(textBefore, AllText(path), "the document's text must be unchanged");

                using var sharp = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
                var root = sharp.Internals.Catalog.Elements.GetDictionary("/StructTreeRoot");

                t.IsNotNull(root, "the file should now carry a structure tree");
                t.IsTrue(root!.Elements.GetArray("/K")?.Elements.Count > 0, "with elements in it");
                t.IsNotNull(root.Elements.GetDictionary("/ParentTree"),
                    "and a parent tree, without which every tag is unreachable");
            });
        });

        t.Test("the reloaded document reports itself as tagged", () =>
        {
            // Read back through the ordinary loader, which is what the user's next session does.
            WithSampleCopy(t, (document, path, saver) =>
            {
                saver.Save(document, new SaveOptions
                {
                    TargetPath = path, AddStructureTags = true, CreateBackup = false,
                });

                var reloaded = new PdfPigDocumentLoader().Load(path).Document;

                t.IsNotNull(reloaded, "the tagged file should re-open");
                t.IsTrue(reloaded!.TaggedStatus is TaggedStatus.FullyTagged or TaggedStatus.PartiallyTagged,
                    $"it should read back as tagged, but was {reloaded.TaggedStatus}");
            });
        });

        t.Test("headings are written as headings, not as paragraphs", () =>
        {
            // The single most valuable thing in the tree. Everything else could be right and the
            // document would still be unnavigable if every block came out as /P.
            WithSampleCopy(t, (document, path, saver) =>
            {
                saver.Save(document, new SaveOptions
                {
                    TargetPath = path, AddStructureTags = true, CreateBackup = false,
                });

                using var sharp = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
                var root = sharp.Internals.Catalog.Elements.GetDictionary("/StructTreeRoot")!;

                var types = new List<string>();
                CollectTypes(root.Elements.GetArray("/K"), types, 0);

                t.IsTrue(types.Any(s => s.StartsWith("/H", StringComparison.Ordinal)),
                    $"there should be heading elements, but found: {string.Join(", ", types.Distinct())}");
            });
        });

        t.Test("a document tagged twice does not accumulate duplicate marks", () =>
        {
            // Running the repair again is a thing people do. Each run rebuilds the tree from
            // scratch, so the second must not leave the first run's marks orphaned inside it.
            WithSampleCopy(t, (document, path, saver) =>
            {
                var options = new SaveOptions
                {
                    TargetPath = path, AddStructureTags = true, CreateBackup = false,
                };

                saver.Save(document, options);
                string textOnce = AllText(path);

                var reloaded = new PdfPigDocumentLoader().Load(path).Document!;
                var second = saver.Save(reloaded, options);

                t.IsTrue(second.Outcome is SaveOutcome.Saved, $"the second save should succeed: {second.Message}");
                t.AreEqual(textOnce, AllText(path), "and the text should still be identical");
            });
        });
    }

    private static void CollectTypes(PdfArray? kids, List<string> types, int depth)
    {
        if (kids is null || depth > 12)
            return;

        for (int i = 0; i < kids.Elements.Count; i++)
        {
            var element = kids.Elements[i] switch
            {
                PdfReference reference => reference.Value as PdfDictionary,
                PdfDictionary dictionary => dictionary,
                _ => null,
            };

            if (element is null)
                continue;

            if (element.Elements.GetName("/S") is { Length: > 0 } type)
                types.Add(type);

            CollectTypes(element.Elements.GetArray("/K"), types, depth + 1);
        }
    }

    private static List<int> TextByPage(string path)
    {
        using var pig = UglyToad.PdfPig.PdfDocument.Open(path,
            new UglyToad.PdfPig.ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });

        var lengths = new List<int>();

        for (int i = 1; i <= pig.NumberOfPages; i++)
        {
            try { lengths.Add(pig.GetPage(i).Text?.Length ?? 0); }
            catch (Exception ex) { Console.WriteLine($"  page {i} threw {ex.GetType().Name}"); lengths.Add(-1); }
        }

        return lengths;
    }

    private static string AllText(string path)
    {
        using var pig = UglyToad.PdfPig.PdfDocument.Open(path,
            new UglyToad.PdfPig.ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });

        var builder = new StringBuilder();

        for (int i = 1; i <= pig.NumberOfPages; i++)
            builder.Append(pig.GetPage(i).Text);

        return builder.ToString();
    }

    private static void WithSampleCopy(
        TestRunner t, Action<Model.PdfDocumentModel, string, IDocumentSaver> test)
    {
        string sample = FindSample();

        if (!File.Exists(sample))
        {
            t.IsTrue(true, "the sample is not present, so this test is skipped");
            return;
        }

        string working = Path.Combine(Path.GetTempPath(), $"ape-tagging-{Guid.NewGuid():N}.pdf");
        File.Copy(sample, working, overwrite: true);

        try
        {
            var loaded = new PdfPigDocumentLoader().Load(working);
            t.IsNotNull(loaded.Document, "the sample should load");

            if (loaded.Document is not null)
                test(loaded.Document, working, new PdfSharpDocumentSaver());
        }
        finally
        {
            try { if (File.Exists(working)) File.Delete(working); } catch { }
        }
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
