using AccessiblePdfEditor.Ingestion;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Navigation;
using AccessiblePdfEditor.Persistence;
using AccessiblePdfEditor.UI;

namespace AccessiblePdfEditor.Tests;

// =====================================================================================
//  NavigationIntegrationTests.cs
//
//  Tests that navigation actually MOVES THE CARET, for every key.
//
//  Written after a real bug that every existing test missed. Pressing T found the table
//  and announced it correctly — the navigation service was working perfectly, and the tests
//  for it all passed — but the caret never moved, because the document text renderer
//  deliberately gives containers no text of their own and the caret lookup returned
//  nothing. The screen reader's review cursor stayed exactly where it was.
//
//  So from the user's side the key did nothing, while from the model's side everything was
//  correct. The gap was between two components that were each individually right.
//
//  That is the lesson these tests encode: for this application, navigation is not "the
//  service returned an element". It is "the caret is now somewhere the screen reader will
//  read from". Nothing short of that is navigation, and every granularity is checked,
//  because the bug was never specific to tables — it was waiting in lists and sections too.
// =====================================================================================

internal static class NavigationIntegrationTests
{
    public static void Register(TestRunner t)
    {
        RegisterCaretTargets(t);
        RegisterContainerRendering(t);
    }

    #region Every granularity must land the caret somewhere

    private static void RegisterCaretTargets(TestRunner t)
    {
        t.Group("navigation moves the caret");

        t.Test("every structural granularity that finds something can place the caret", () =>
        {
            // The test that would have caught the reported bug. It walks EVERY navigation key, and
            // for each one that finds an element, insists the renderer can say where to put the
            // caret. An element you can reach but cannot land on is not reachable.
            var document = BuildRichDocument();
            var rendered = DocumentTextRenderer.Render(document, ReadingMode.Structured, true);

            NavigationGranularity[] granularities =
            [
                NavigationGranularity.Element,
                NavigationGranularity.Paragraph,
                NavigationGranularity.Heading,
                NavigationGranularity.List,
                NavigationGranularity.ListItem,
                NavigationGranularity.Table,
                NavigationGranularity.TableCell,
                NavigationGranularity.Figure,
                NavigationGranularity.Link,
                NavigationGranularity.FormField,
                NavigationGranularity.Annotation,
            ];

            foreach (var granularity in granularities)
            {
                var navigation = new NavigationService();
                navigation.Attach(document);

                var result = navigation.Move(granularity, MoveDirection.First);

                if (!result.Moved || result.Element is null)
                    continue;

                t.IsNotNull(rendered.CaretTargetFor(result.Element),
                    $"pressing the key for {granularity} finds \"{result.Element.Kind}\" but the " +
                    "caret has nowhere to go, so the review cursor would not move");
            }
        });

        t.Test("moving to a table lands the caret inside it", () =>
        {
            // The reported symptom, pinned exactly.
            var document = BuildRichDocument();
            var rendered = DocumentTextRenderer.Render(document, ReadingMode.Structured, true);

            var navigation = new NavigationService();
            navigation.Attach(document);

            var result = navigation.Move(NavigationGranularity.Table, MoveDirection.First);

            t.IsTrue(result.Moved, "the table should be found");

            var target = rendered.CaretTargetFor(result.Element!);
            t.IsNotNull(target, "the caret must have somewhere to go");

            // And that somewhere must actually be the table's text, not a coincidence elsewhere.
            string at = rendered.Text.Substring(target!.Value.Start, target.Value.Length);

            t.IsTrue(at.Contains("Table", StringComparison.OrdinalIgnoreCase)
                     || at.Contains("Month", StringComparison.OrdinalIgnoreCase),
                $"the caret should land on the table, but landed on \"{at}\"");
        });

        t.Test("moving to a list lands the caret inside it", () =>
        {
            // The same bug was waiting here. Lists were excluded from the rendered text for exactly
            // the same reason tables were.
            var document = BuildRichDocument();
            var rendered = DocumentTextRenderer.Render(document, ReadingMode.Structured, true);

            var navigation = new NavigationService();
            navigation.Attach(document);

            var result = navigation.Move(NavigationGranularity.List, MoveDirection.First);

            t.IsTrue(result.Moved, "the list should be found");
            t.IsNotNull(rendered.CaretTargetFor(result.Element!),
                "the caret must be able to land on a list");
        });

        t.Test("a container with no rendered text falls back to its first child", () =>
        {
            // Sections are still not rendered, deliberately — they have no content of their own to
            // announce. Landing on one must still work.
            var root = new DocumentRootElement("Doc");
            var document = new PdfDocumentModel("C:\\x.pdf", root);

            var page = new PageElement(1, 595, 842);
            root.AddChild(page);

            var section = new SectionElement(1);
            page.AddChild(section);
            section.AddChild(new ParagraphElement(1, "Text inside the section."));

            document.RebuildReadingOrder();

            var rendered = DocumentTextRenderer.Render(document, ReadingMode.Structured, true);

            t.IsNull(rendered.SpanOf(section), "a section has no text of its own");
            t.IsNotNull(rendered.CaretTargetFor(section),
                "but the caret should fall back to the paragraph inside it");
        });

        t.Test("every element in the real sample can be landed on", () =>
        {
            // Against a real document rather than a constructed one, because the bug arose from the
            // interaction of real extraction with real rendering.
            string sample = FindSample();

            if (sample.Length == 0)
                return;

            var result = new PdfPigDocumentLoader().Load(sample);
            t.IsTrue(result.IsSuccess, "the sample should load");

            var document = result.Document!;
            var rendered = DocumentTextRenderer.Render(document, ReadingMode.Structured, true);

            foreach (var granularity in new[]
                     {
                         NavigationGranularity.Heading, NavigationGranularity.Table,
                         NavigationGranularity.Figure, NavigationGranularity.Link,
                         NavigationGranularity.FormField, NavigationGranularity.Paragraph,
                     })
            {
                var navigation = new NavigationService();
                navigation.Attach(document);

                var moved = navigation.Move(granularity, MoveDirection.First);

                if (!moved.Moved || moved.Element is null)
                    continue;

                t.IsNotNull(rendered.CaretTargetFor(moved.Element),
                    $"{granularity} finds something in the sample that the caret cannot reach");
            }
        });
    }

    #endregion

    #region Containers announce themselves in the text
    // So that a user reading straight through with their screen reader's own Say All learns a table
    // has started and how big it is, rather than meeting a run of indented cells with no warning.

    private static void RegisterContainerRendering(TestRunner t)
    {
        t.Group("containers appear in the document text");

        t.Test("a table announces itself and its size in the text", () =>
        {
            var document = BuildRichDocument();
            var rendered = DocumentTextRenderer.Render(document, ReadingMode.Structured, true);

            t.Says(rendered.Text, "Table: 2 rows, 2 columns");
        });

        t.Test("a table with no headers says so in the text", () =>
        {
            var document = BuildRichDocument(headerRow: false);
            var rendered = DocumentTextRenderer.Render(document, ReadingMode.Structured, true);

            t.Says(rendered.Text, "no header cells");
        });

        t.Test("a list announces how many items it has", () =>
        {
            var document = BuildRichDocument();
            var rendered = DocumentTextRenderer.Render(document, ReadingMode.Structured, true);

            t.Says(rendered.Text, "List of 3 items");
        });

        t.Test("raw reading mode adds nothing", () =>
        {
            // Raw exists for when any interpretation would mislead, so it must not start
            // interpreting.
            var document = BuildRichDocument();
            var rendered = DocumentTextRenderer.Render(document, ReadingMode.Raw, includeRoleLabels: false);

            t.DoesNotSay(rendered.Text, "Table:");
            t.DoesNotSay(rendered.Text, "List of");
        });
    }

    #endregion

    #region A document with one of everything

    private static PdfDocumentModel BuildRichDocument(bool headerRow = true)
    {
        var root = new DocumentRootElement("Rich");
        var document = new PdfDocumentModel("C:\\rich.pdf", root);

        var page = new PageElement(1, 595, 842);
        root.AddChild(page);

        page.AddChild(new HeadingElement(1, "Introduction", HeadingLevel.Level1));
        page.AddChild(new ParagraphElement(1, "Some body text to read."));

        var list = new ListElement(1, ListMarkerKind.Bullet);
        list.AddChild(new ListItemElement(1, "First point"));
        list.AddChild(new ListItemElement(1, "Second point"));
        list.AddChild(new ListItemElement(1, "Third point"));
        page.AddChild(list);

        var table = new TableElement(1);

        var head = new TableRowElement(1);
        head.AddChild(new TableCellElement(1, "Month",
            headerRow ? TableCellRole.ColumnHeader : TableCellRole.Data));
        head.AddChild(new TableCellElement(1, "Amount",
            headerRow ? TableCellRole.ColumnHeader : TableCellRole.Data));
        table.AddChild(head);

        var data = new TableRowElement(1);
        data.AddChild(new TableCellElement(1, "January"));
        data.AddChild(new TableCellElement(1, "412.00"));
        table.AddChild(data);

        page.AddChild(table);

        page.AddChild(new FigureElement(1, new PageRegion(50, 400, 300, 600)));
        page.AddChild(new LinkElement(1, "the guidance", LinkTargetKind.WebUrl, "https://example.org"));
        page.AddChild(new AnnotationElement(1, AnnotationKind.Comment, "A note about this."));

        var field = new Model.Forms.TextFormField(1, "name") { ToolTip = "Full name" };
        page.AddChild(field);

        document.RebuildReadingOrder();
        return document;
    }

    private static string FindSample()
    {
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "samples", "Sample form (deliberately inaccessible).pdf"));

        return File.Exists(path) ? path : string.Empty;
    }

    #endregion
}
