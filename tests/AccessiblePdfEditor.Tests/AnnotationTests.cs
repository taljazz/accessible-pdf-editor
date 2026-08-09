using AccessiblePdfEditor.Editing;
using AccessiblePdfEditor.Ingestion;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Persistence;
using AccessiblePdfEditor.Rendering;

namespace AccessiblePdfEditor.Tests;

// =====================================================================================
//  AnnotationTests.cs
//
//  Tests for writing comments: adding, editing, replying, deleting — and getting them
//  into and back out of a real file.
//
//  The round-trip tests at the end are the ones that matter. A comment that exists only
//  in this program's memory is not a comment; it is a note that vanishes when the document
//  is closed. So those tests save to a real PDF, re-open it with the real loader, and
//  check the text came back — because every failure mode worth catching here (a wrong
//  dictionary key, a rectangle in the wrong coordinate space, an annotation appended to
//  the wrong page) produces a file that saves without complaint and is silently empty.
//
//  One test exists purely to protect the save's own safety net. Deleting a comment reduces
//  the annotation count, and the fingerprint check treats an unexplained reduction as
//  damage and rolls the whole save back. So a deliberate deletion has to be declared to it.
//  Without that, deleting a comment would appear to work, then silently undo itself along
//  with everything else in the same save.
// =====================================================================================

internal static class AnnotationTests
{
    public static void Register(TestRunner t)
    {
        RegisterAdding(t);
        RegisterEditing(t);
        RegisterDeleting(t);
        RegisterBrowseView(t);
        RegisterRoundTrip(t);
        RegisterTagging(t);
    }

    #region Adding

    private static void RegisterAdding(TestRunner t)
    {
        t.Group("comments — adding");

        t.Test("a comment attaches to the element the user is on", () =>
        {
            var (document, paragraph) = BuildDocument();

            var command = new AddAnnotationCommand(paragraph, "Check this figure.", "Denise");
            t.IsTrue(command.Apply(document).Succeeded, "the comment should be added");

            t.AreEqual(1, document.Annotations.Count, "the document should now have one comment");
            t.AreEqual("Check this figure.", document.Annotations[0].Contents, "with the text given");
            t.AreEqual("Denise", document.Annotations[0].Author, "signed by its author");
        });

        t.Test("a comment takes its position from what it is about", () =>
        {
            // The whole point of anchoring to an element: the user never states coordinates.
            var (document, paragraph) = BuildDocument();

            new AddAnnotationCommand(paragraph, "Note.", "Denise").Apply(document);

            var annotation = document.Annotations[0];

            t.AreEqual(paragraph.PageNumber, annotation.PageNumber, "on the same page as its anchor");
            t.AreEqual(paragraph.Bounds.Left, annotation.Bounds.Left, "and at its anchor's position");
        });

        t.Test("a comment records what it is about, in words", () =>
        {
            // So it reads as "comment on the paragraph beginning ..." rather than "comment at 380, 512".
            var (document, paragraph) = BuildDocument();

            new AddAnnotationCommand(paragraph, "Note.", "Denise").Apply(document);

            t.Says(document.Annotations[0].AnchoredText ?? string.Empty, "Payments are due");
        });

        t.Test("a comment reads immediately after the thing it is about", () =>
        {
            // Appended to the end of the page, it would be heard minutes after the sentence it
            // refers to, which for a listener makes it useless.
            var (document, paragraph) = BuildDocument();

            new AddAnnotationCommand(paragraph, "Note.", "Denise").Apply(document);

            var order = document.ReadingOrder;
            int paragraphAt = order.ToList().FindIndex(e => ReferenceEquals(e, paragraph));
            int commentAt = order.ToList().FindIndex(e => e is AnnotationElement);

            t.IsTrue(commentAt == paragraphAt + 1,
                $"the comment should read straight after its anchor, but was at {commentAt} and the anchor at {paragraphAt}");
        });

        t.Test("undoing an added comment removes it", () =>
        {
            var (document, paragraph) = BuildDocument();
            var command = new AddAnnotationCommand(paragraph, "Note.", "Denise");

            command.Apply(document);
            t.AreEqual(1, document.Annotations.Count, "added");

            t.IsTrue(command.Revert(document).Succeeded, "undo should succeed");
            t.AreEqual(0, document.Annotations.Count, "and the comment should be gone");
        });

        t.Test("the undo announcement says which comment it was", () =>
        {
            var (document, paragraph) = BuildDocument();
            var command = new AddAnnotationCommand(paragraph, "Check the total.", "Denise");

            t.Says(command.Description, "Check the total");
            t.Says(command.Description, "paragraph");
        });

        t.Test("a reply is attached to the comment it answers", () =>
        {
            var (document, paragraph) = BuildDocument();
            new AddAnnotationCommand(paragraph, "Is this right?", "Denise").Apply(document);

            var original = document.Annotations[0];
            var reply = new ReplyToAnnotationCommand(original, "Yes, checked it.", "Thomas");

            t.IsTrue(reply.Apply(document).Succeeded, "the reply should be added");
            t.AreEqual(1, original.Replies.Count, "and hang off the comment it answers");
            t.IsTrue(ReferenceEquals(reply.Reply.InReplyTo, original), "with its parent recorded");
        });
    }

    #endregion

    #region Editing

    private static void RegisterEditing(TestRunner t)
    {
        t.Group("comments — editing");

        t.Test("changing a comment replaces its text", () =>
        {
            var (document, paragraph) = BuildDocument();
            new AddAnnotationCommand(paragraph, "Frist draft.", "Denise").Apply(document);

            var annotation = document.Annotations[0];
            var edit = new EditAnnotationCommand(annotation, "First draft.");

            t.IsTrue(edit.Apply(document).Succeeded, "the edit should apply");
            t.AreEqual("First draft.", annotation.Contents, "with the corrected text");
        });

        t.Test("undoing a change puts the old wording back exactly", () =>
        {
            var (document, paragraph) = BuildDocument();
            new AddAnnotationCommand(paragraph, "Original wording.", "Denise").Apply(document);

            var annotation = document.Annotations[0];
            var edit = new EditAnnotationCommand(annotation, "Replaced.");

            edit.Apply(document);
            edit.Revert(document);

            t.AreEqual("Original wording.", annotation.Contents, "the original wording should return");
        });

        t.Test("successive changes to one comment collapse into a single undo", () =>
        {
            // Otherwise correcting a comment twice means pressing Ctrl+Z twice to get back past
            // your own corrections, and hearing two near-identical announcements.
            var (document, paragraph) = BuildDocument();
            new AddAnnotationCommand(paragraph, "One.", "Denise").Apply(document);

            var annotation = document.Annotations[0];
            var first = new EditAnnotationCommand(annotation, "Two.");
            var second = new EditAnnotationCommand(annotation, "Three.");

            t.IsTrue(first.CanMergeWith(second), "two edits of the same comment should merge");
        });

        t.Test("changes to different comments do not merge", () =>
        {
            var (document, paragraph) = BuildDocument();
            new AddAnnotationCommand(paragraph, "One.", "Denise").Apply(document);
            new AddAnnotationCommand(paragraph, "Two.", "Denise").Apply(document);

            var first = new EditAnnotationCommand(document.Annotations[0], "Changed one.");
            var second = new EditAnnotationCommand(document.Annotations[1], "Changed two.");

            t.IsFalse(first.CanMergeWith(second), "separate comments are separate undo steps");
        });

        t.Test("editing an annotation that came from the file marks it for writing", () =>
        {
            // A new comment is built and appended; an edited one has to be found in the file and
            // rewritten. The save distinguishes them by these flags.
            var (document, paragraph) = BuildDocument();

            var existing = new AnnotationElement(1, AnnotationKind.Comment, "From the file")
            {
                SourceObjectId = "EXISTING-1",
            };

            paragraph.Parent!.AddChild(existing);
            document.RebuildReadingOrder();

            new EditAnnotationCommand(existing, "Changed").Apply(document);

            t.IsFalse(existing.IsUnsaved, "it did not become a new annotation");
            t.IsTrue(existing.IsEdited, "but it is marked as needing rewriting");
            t.IsTrue(existing.NeedsWriting, "and so it will be written");
        });
    }

    #endregion

    #region Deleting

    private static void RegisterDeleting(TestRunner t)
    {
        t.Group("comments — deleting");

        t.Test("deleting removes the comment from the document", () =>
        {
            var (document, paragraph) = BuildDocument();
            new AddAnnotationCommand(paragraph, "Delete me.", "Denise").Apply(document);

            var command = new DeleteAnnotationCommand(document.Annotations[0]);

            t.IsTrue(command.Apply(document).Succeeded, "the delete should succeed");
            t.AreEqual(0, document.Annotations.Count, "and the comment should be gone");
        });

        t.Test("undo puts the comment back in its own place, not at the end", () =>
        {
            // Reading order is what this program protects. An undo that moved the comment would
            // hand back a different document from the one before it.
            var (document, paragraph) = BuildDocument();

            new AddAnnotationCommand(paragraph, "First.", "Denise").Apply(document);
            new AddAnnotationCommand(paragraph, "Second.", "Denise").Apply(document);

            var first = document.Annotations.First(a => a.Contents == "First.");
            var command = new DeleteAnnotationCommand(first);

            command.Apply(document);
            command.Revert(document);

            var restored = document.Annotations.Select(a => a.Contents).ToList();

            t.AreEqual(2, restored.Count, "both comments should be back");
            t.AreEqual("First.", restored[0], "and in the order they were in before");
        });

        t.Test("deleting one that came from the file records it for removal on save", () =>
        {
            var (document, paragraph) = BuildDocument();

            var existing = new AnnotationElement(1, AnnotationKind.Comment, "In the file")
            {
                SourceObjectId = "EXISTING-2",
            };

            paragraph.Parent!.AddChild(existing);
            document.RebuildReadingOrder();

            new DeleteAnnotationCommand(existing).Apply(document);

            t.AreEqual(1, document.DeletedAnnotations.Count,
                "the save has to know to remove it, and by now it is out of the tree");
        });

        t.Test("deleting one that was never saved records nothing to remove", () =>
        {
            // There is nothing in the file to delete, so recording it would make the save expect a
            // removal that never happens and mis-count the result.
            var (document, paragraph) = BuildDocument();
            new AddAnnotationCommand(paragraph, "Never saved.", "Denise").Apply(document);

            new DeleteAnnotationCommand(document.Annotations[0]).Apply(document);

            t.AreEqual(0, document.DeletedAnnotations.Count, "nothing on disk needs removing");
        });

        t.Test("undoing a delete cancels the recorded removal", () =>
        {
            var (document, paragraph) = BuildDocument();

            var existing = new AnnotationElement(1, AnnotationKind.Comment, "In the file")
            {
                SourceObjectId = "EXISTING-3",
            };

            paragraph.Parent!.AddChild(existing);
            document.RebuildReadingOrder();

            var command = new DeleteAnnotationCommand(existing);
            command.Apply(document);
            command.Revert(document);

            t.AreEqual(0, document.DeletedAnnotations.Count,
                "an undone deletion must not still be pending on the next save");
        });

        t.Test("deleting is flagged as losing information", () =>
        {
            // Which is what makes the save take a backup and say what is about to go.
            var (document, paragraph) = BuildDocument();
            new AddAnnotationCommand(paragraph, "Words nobody can retype.", "Denise").Apply(document);

            var command = new DeleteAnnotationCommand(document.Annotations[0]);

            t.AreEqual(EditConfidence.Lossy, command.Confidence,
                "deleting somebody's comment is not a safe edit");
        });
    }

    #endregion

    #region The browse view

    private static void RegisterBrowseView(TestRunner t)
    {
        t.Group("comments — in the browse view");

        t.Test("a comment carries buttons to change, reply and delete", () =>
        {
            // In browse mode the screen reader keeps its cursor to itself, so "the comment I am on"
            // has nothing to act on. Buttons are how the commands become reachable at all.
            var (document, paragraph) = BuildDocument();
            new AddAnnotationCommand(paragraph, "Look at this.", "Denise").Apply(document);

            string html = DocumentHtmlWriter.Write(document).Html;

            foreach (string action in new[] { "editComment", "replyComment", "deleteComment" })
            {
                t.IsTrue(html.Contains($"data-act=\"{action}\"", StringComparison.Ordinal),
                    $"there should be a {action} button on the comment");
            }
        });

        t.Test("the comment and its author are readable in the page", () =>
        {
            var (document, paragraph) = BuildDocument();
            new AddAnnotationCommand(paragraph, "Look at this.", "Denise").Apply(document);

            string html = DocumentHtmlWriter.Write(document).Html;

            t.Says(html, "Denise");
            t.Says(html, "Look at this.");
        });
    }

    #endregion

    #region Round trip through a real file

    private static void RegisterRoundTrip(TestRunner t)
    {
        t.Group("comments — round trip through a saved file");

        t.Test("a comment survives save and reload", () =>
        {
            WithSampleCopy(t, (document, path, saver) =>
            {
                var anchor = document.ReadingOrder.FirstOrDefault(e => e is ParagraphElement);
                t.IsNotNull(anchor, "the sample should have a paragraph to comment on");

                new AddAnnotationCommand(anchor!, "This total looks wrong.", "Denise").Apply(document);

                var result = saver.Save(document, new SaveOptions { CreateBackup = false, TargetPath = path });
                t.IsTrue(result.Outcome is SaveOutcome.Saved, $"the save should succeed: {result.Message}");

                var reloaded = new PdfPigDocumentLoader().Load(path).Document;
                t.IsNotNull(reloaded, "the saved file should re-open");

                bool found = reloaded!.Annotations.Any(a =>
                    a.Contents.Contains("This total looks wrong", StringComparison.Ordinal));

                t.IsTrue(found, "the comment should be in the file that was written");
            });
        });

        t.Test("the author is written so a reader can say who wrote it", () =>
        {
            WithSampleCopy(t, (document, path, saver) =>
            {
                var anchor = document.ReadingOrder.First(e => e is ParagraphElement);
                new AddAnnotationCommand(anchor, "Checked.", "Denise").Apply(document);

                saver.Save(document, new SaveOptions { CreateBackup = false, TargetPath = path });

                var reloaded = new PdfPigDocumentLoader().Load(path).Document!;
                var written = reloaded.Annotations.FirstOrDefault(a => a.Contents == "Checked.");

                t.IsNotNull(written, "the comment should be there");
                t.AreEqual("Denise", written!.Author, "and carry its author");
            });
        });

        t.Test("deleting a comment does not roll the whole save back", () =>
        {
            // The save compares the file before and after and treats any unexplained reduction as
            // damage. A deliberate deletion looks exactly like that, so it has to be declared —
            // and without this test, deleting a comment would appear to work and then silently
            // undo itself along with every other change in the same save.
            WithSampleCopy(t, (document, path, saver) =>
            {
                var anchor = document.ReadingOrder.First(e => e is ParagraphElement);
                new AddAnnotationCommand(anchor, "Temporary.", "Denise").Apply(document);

                var first = saver.Save(document, new SaveOptions { CreateBackup = false, TargetPath = path });
                t.IsTrue(first.Outcome is SaveOutcome.Saved, $"the first save should succeed: {first.Message}");

                var reloaded = new PdfPigDocumentLoader().Load(path).Document!;
                var toDelete = reloaded.Annotations.First(a => a.Contents == "Temporary.");

                new DeleteAnnotationCommand(toDelete).Apply(reloaded);

                var second = saver.Save(reloaded, new SaveOptions { CreateBackup = false, TargetPath = path });

                t.IsTrue(second.Outcome is SaveOutcome.Saved,
                    $"deleting a comment must not be mistaken for damage: {second.Message}");

                var afterDelete = new PdfPigDocumentLoader().Load(path).Document!;

                t.IsFalse(afterDelete.Annotations.Any(a => a.Contents == "Temporary."),
                    "and the comment should actually be gone from the file");
            });
        });

        t.Test("a changed comment is rewritten rather than duplicated", () =>
        {
            WithSampleCopy(t, (document, path, saver) =>
            {
                var anchor = document.ReadingOrder.First(e => e is ParagraphElement);
                new AddAnnotationCommand(anchor, "Before.", "Denise").Apply(document);
                saver.Save(document, new SaveOptions { CreateBackup = false, TargetPath = path });

                var reloaded = new PdfPigDocumentLoader().Load(path).Document!;
                int countBefore = reloaded.Annotations.Count;

                var existing = reloaded.Annotations.First(a => a.Contents == "Before.");
                new EditAnnotationCommand(existing, "After.").Apply(reloaded);

                var result = saver.Save(reloaded, new SaveOptions { CreateBackup = false, TargetPath = path });
                t.IsTrue(result.Outcome is SaveOutcome.Saved, $"the save should succeed: {result.Message}");

                var final = new PdfPigDocumentLoader().Load(path).Document!;

                t.AreEqual(countBefore, final.Annotations.Count, "editing must not add a second comment");
                t.IsTrue(final.Annotations.Any(a => a.Contents == "After."), "the new wording should be there");
                t.IsFalse(final.Annotations.Any(a => a.Contents == "Before."), "and the old wording gone");
            });
        });

        t.Test("the date is written in the format other editors can read", () =>
        {
            // A comment thread is sorted by date. A plain string here would put the conversation in
            // the wrong order in every other PDF tool.
            string formatted = AnnotationWriter.FormatPdfDate(
                new DateTimeOffset(2026, 8, 9, 14, 30, 15, TimeSpan.FromHours(1)));

            t.IsTrue(formatted.StartsWith("D:20260809143015", StringComparison.Ordinal),
                $"the PDF date should start D:YYYYMMDDHHmmSS, but was {formatted}");

            t.IsTrue(formatted.Contains("+01'00'", StringComparison.Ordinal),
                $"and carry the UTC offset, but was {formatted}");
        });
    }

    #endregion

    #region Tagging into the structure tree

    private static void RegisterTagging(TestRunner t)
    {
        t.Group("comments — in the structure tree");

        t.Test("a comment in a tagged document joins the structure tree", () =>
        {
            // PDF/UA requires annotations to be tagged. Untagged, a checker reports the comment as
            // a fault and it has no place in the reading order.
            WithTaggedDocument(t, (document, path, saver) =>
            {
                var anchor = document.Pages[0];
                new AddAnnotationCommand(anchor, "Tag me.", "Denise").Apply(document);

                var result = saver.Save(document, new SaveOptions { CreateBackup = false, TargetPath = path });
                t.IsTrue(result.Outcome is SaveOutcome.Saved, $"the save should succeed: {result.Message}");

                using var sharp = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
                var root = sharp.Internals.Catalog.Elements.GetDictionary("/StructTreeRoot");

                t.IsNotNull(root, "the structure tree should still be there");
                t.IsNotNull(FindAnnotStructureElement(root!), "and should now contain an Annot element");
            });
        });

        t.Test("the parent tree entry is a single reference, not an array", () =>
        {
            // The one detail that is silently wrong if guessed. ISO 32000 clause 14.7.5: an object
            // reaching the tree by object reference maps to a single reference; only marked content
            // maps to an array. An array here produces a tag nothing can reach from the annotation,
            // and the file still opens and still validates structurally.
            WithTaggedDocument(t, (document, path, saver) =>
            {
                new AddAnnotationCommand(document.Pages[0], "Reachable?", "Denise").Apply(document);
                saver.Save(document, new SaveOptions { CreateBackup = false, TargetPath = path });

                using var sharp = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
                var root = sharp.Internals.Catalog.Elements.GetDictionary("/StructTreeRoot")!;

                var numbers = root.Elements.GetDictionary("/ParentTree")?.Elements.GetArray("/Nums");
                t.IsNotNull(numbers, "there should be a parent tree");

                var element = FindAnnotStructureElement(root);
                t.IsNotNull(element, "there should be an Annot structure element");

                bool foundAsSingleReference = false;

                for (int i = 0; i + 1 < numbers!.Elements.Count; i += 2)
                {
                    var value = numbers.Elements[i + 1];

                    if (value is PdfSharp.Pdf.PdfArray)
                        continue;

                    if (ReferenceEquals(ResolveDictionary(value), element))
                        foundAsSingleReference = true;
                }

                t.IsTrue(foundAsSingleReference,
                    "the annotation's parent-tree value must be the structure element itself, not an array");
            });
        });

        t.Test("the annotation points back at its parent-tree key", () =>
        {
            WithTaggedDocument(t, (document, path, saver) =>
            {
                new AddAnnotationCommand(document.Pages[0], "Linked.", "Denise").Apply(document);
                saver.Save(document, new SaveOptions { CreateBackup = false, TargetPath = path });

                using var sharp = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
                var annotations = sharp.Pages[0].Elements.GetArray("/Annots");

                t.IsNotNull(annotations, "the page should have annotations");

                bool hasStructParent = false;

                for (int i = 0; i < annotations!.Elements.Count; i++)
                {
                    if (ResolveDictionary(annotations.Elements[i]) is { } annotation
                        && annotation.Elements.ContainsKey("/StructParent"))
                    {
                        hasStructParent = true;
                    }
                }

                t.IsTrue(hasStructParent, "the annotation needs /StructParent or the tag is unreachable");
            });
        });

        t.Test("deleting a tagged comment removes its structure element too", () =>
        {
            // A structure element pointing at an annotation that no longer exists is a broken tree —
            // a worse fault than the untagged annotation it replaced.
            WithTaggedDocument(t, (document, path, saver) =>
            {
                new AddAnnotationCommand(document.Pages[0], "Short lived.", "Denise").Apply(document);
                saver.Save(document, new SaveOptions { CreateBackup = false, TargetPath = path });

                var reloaded = new PdfPigDocumentLoader().Load(path).Document!;
                var comment = reloaded.Annotations.First(a => a.Contents == "Short lived.");

                new DeleteAnnotationCommand(comment).Apply(reloaded);

                var result = saver.Save(reloaded, new SaveOptions { CreateBackup = false, TargetPath = path });
                t.IsTrue(result.Outcome is SaveOutcome.Saved, $"the save should succeed: {result.Message}");

                using var sharp = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
                var root = sharp.Internals.Catalog.Elements.GetDictionary("/StructTreeRoot")!;

                t.IsNull(FindAnnotStructureElement(root),
                    "the tag must go when the annotation it describes goes");
            });
        });

        t.Test("an untagged document is NOT given a structure tree", () =>
        {
            // Deliberate. Creating one means claiming /MarkInfo /Marked true, and a document that
            // says it is tagged while its text is not is worse than an honest untagged one: readers
            // and checkers believe the claim.
            WithSampleCopy(t, (document, path, saver) =>
            {
                var anchor = document.ReadingOrder.First(e => e is ParagraphElement);
                new AddAnnotationCommand(anchor, "No tree here.", "Denise").Apply(document);

                var result = saver.Save(document, new SaveOptions { CreateBackup = false, TargetPath = path });
                t.IsTrue(result.Outcome is SaveOutcome.Saved, $"the save should still succeed: {result.Message}");

                using var sharp = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
                var root = sharp.Internals.Catalog.Elements.GetDictionary("/StructTreeRoot");

                t.IsTrue(root is null || root.Elements.Count == 0,
                    "an untagged document must not be made to claim it is tagged");

                // And the comment itself still works, which is the point: tagging is a bonus, not
                // a precondition.
                var reloaded = new PdfPigDocumentLoader().Load(path).Document!;
                t.IsTrue(reloaded.Annotations.Any(a => a.Contents == "No tree here."),
                    "the comment should be readable regardless");
            });
        });
    }

    private static PdfSharp.Pdf.PdfDictionary? FindAnnotStructureElement(PdfSharp.Pdf.PdfDictionary root)
    {
        var kids = root.Elements.GetArray("/K");

        if (kids is null)
            return null;

        for (int i = 0; i < kids.Elements.Count; i++)
        {
            if (ResolveDictionary(kids.Elements[i]) is { } element
                && string.Equals(element.Elements.GetName("/S"), "/Annot", StringComparison.Ordinal))
            {
                return element;
            }
        }

        return null;
    }

    private static PdfSharp.Pdf.PdfDictionary? ResolveDictionary(PdfSharp.Pdf.PdfItem? item) => item switch
    {
        PdfSharp.Pdf.Advanced.PdfReference reference => reference.Value as PdfSharp.Pdf.PdfDictionary,
        PdfSharp.Pdf.PdfDictionary dictionary => dictionary,
        _ => null,
    };

    /// <summary>
    /// Runs a test against a minimal document that genuinely has a structure tree.
    ///
    /// Built here rather than found on disk, because the tagging path has to be exercised against a
    /// tree PDFsharp can actually see. A real-world tagged PDF is usually stored in object streams,
    /// which PDFsharp cannot read — that is the case the whole save-safety guard exists for, and a
    /// test that silently landed on one would prove nothing.
    /// </summary>
    private static void WithTaggedDocument(
        TestRunner t, Action<PdfDocumentModel, string, IDocumentSaver> test)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ape-tagged-{Guid.NewGuid():N}.pdf");

        try
        {
            using (var sharp = new PdfSharp.Pdf.PdfDocument())
            {
                sharp.AddPage();

                var root = new PdfSharp.Pdf.PdfDictionary(sharp);
                root.Elements.SetName("/Type", "StructTreeRoot");
                root.Elements.SetValue("/K", new PdfSharp.Pdf.PdfArray(sharp));
                sharp.Internals.AddObject(root);
                sharp.Internals.Catalog.Elements.SetReference("/StructTreeRoot", root);

                var markInfo = new PdfSharp.Pdf.PdfDictionary(sharp);
                markInfo.Elements.SetBoolean("/Marked", true);
                sharp.Internals.Catalog.Elements.SetValue("/MarkInfo", markInfo);

                sharp.Save(path);
            }

            var loaded = new PdfPigDocumentLoader().Load(path);
            t.IsNotNull(loaded.Document, "the tagged fixture should load");

            if (loaded.Document is not null)
                test(loaded.Document, path, new PdfSharpDocumentSaver());
        }
        finally
        {
            TryDelete(path);
            TryDelete(path + ".bak");
        }
    }

    #endregion

    #region Building documents to work on

    private static (PdfDocumentModel Document, ParagraphElement Paragraph) BuildDocument()
    {
        var root = new DocumentRootElement("Test document");
        var page = new PageElement(1, 612, 792);
        var paragraph = new ParagraphElement(1, "Payments are due on the last day of the month.")
        {
            Bounds = new PageRegion(72, 600, 540, 640),
        };

        page.AddChild(paragraph);
        root.AddChild(page);

        var document = new PdfDocumentModel("test.pdf", root);
        document.RebuildReadingOrder();

        return (document, paragraph);
    }

    /// <summary>
    /// Runs a test against a throwaway copy of the sample, so a failing save can never damage the
    /// file in the repository.
    /// </summary>
    private static void WithSampleCopy(TestRunner t, Action<PdfDocumentModel, string, IDocumentSaver> test)
    {
        string sample = FindSample();

        if (!File.Exists(sample))
        {
            t.IsTrue(true, "the sample document is not present, so this test is skipped");
            return;
        }

        string working = Path.Combine(Path.GetTempPath(),
            $"ape-annotations-{Guid.NewGuid():N}.pdf");

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
            TryDelete(working);
            TryDelete(working + ".bak");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A leftover temporary file is not worth failing a test over.
        }
    }

    private static string FindSample() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "samples", "Sample form (deliberately inaccessible).pdf"));

    #endregion
}
