using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Model.Forms;
using AccessiblePdfEditor.Rendering;

namespace AccessiblePdfEditor.Tests;

// =====================================================================================
//  BrowseViewTests.cs
//
//  Tests for the HTML the browse view is built from.
//
//  These matter more than most tests in this suite, because the markup IS the
//  accessibility here. A <td> where a <th scope="row"> belonged is not a cosmetic
//  slip — it is the difference between a screen reader saying "February, Amount,
//  398.50" and saying "398.50", and there is no way to tell the two apart by looking
//  at the screen. The document model is the same either way; only the markup differs.
//
//  Two of these tests exist to stop a specific regression rather than to check a
//  feature:
//
//    "no role labels are written into the text" — the text box needed the word
//    "Heading" written into it because a text box has no other way to convey a role.
//    Carrying that habit into HTML would make the reader say "heading level 2,
//    Heading 2: Introduction". The markup already says it; the text must not.
//
//    "the page never claims to be an application" — NVDA switches browse mode OFF
//    for anything with role="application" (ia2Web.Application.shouldCreateTreeInterceptor
//    is False). One stray attribute would silently undo the entire point of this view,
//    and nothing on screen would look any different.
// =====================================================================================

internal static class BrowseViewTests
{
    public static void Register(TestRunner t)
    {
        RegisterStructure(t);
        RegisterTables(t);
        RegisterForms(t);
        RegisterFigures(t);
        RegisterSafety(t);
    }

    #region Structure

    private static void RegisterStructure(TestRunner t)
    {
        t.Group("browse view — structure");

        t.Test("a heading becomes a real heading at its own level", () =>
        {
            var document = BuildDocument(page =>
            {
                page.AddChild(new HeadingElement(1, "Introduction", HeadingLevel.Level2));
            });

            string html = DocumentHtmlWriter.Write(document).Html;

            t.IsTrue(html.Contains("<h2", StringComparison.Ordinal), "a level 2 heading should become an h2");
            t.IsTrue(html.Contains(">Introduction</h2>", StringComparison.Ordinal), "with its text inside");
        });

        t.Test("a heading of unknown level still becomes a heading", () =>
        {
            // Losing the level is a shame. Losing the heading means the user cannot navigate by
            // heading at all, which is far worse, so an unknown level is guessed rather than dropped.
            var document = BuildDocument(page =>
                page.AddChild(new HeadingElement(1, "Somewhere", HeadingLevel.None)));

            string html = DocumentHtmlWriter.Write(document).Html;

            t.IsTrue(html.Contains("<h2", StringComparison.Ordinal),
                "a heading with no level should still be a heading");
        });

        t.Test("no role labels are written into the text", () =>
        {
            // The reader announces the role from the markup. Writing it as well says it twice.
            var document = BuildDocument(page =>
            {
                page.AddChild(new HeadingElement(1, "Introduction", HeadingLevel.Level1));
                page.AddChild(new ParagraphElement(1, "Some words."));
            });

            string html = DocumentHtmlWriter.Write(document).Html;

            t.IsFalse(html.Contains("Heading 1:", StringComparison.Ordinal),
                "the text box's role prefix must not survive into the markup");
            t.IsFalse(html.Contains("Table:", StringComparison.Ordinal),
                "nor the table's dimensions line");
        });

        t.Test("each page becomes a landmark the reader can jump between", () =>
        {
            var document = BuildDocument(page => page.AddChild(new ParagraphElement(1, "One.")));

            string html = DocumentHtmlWriter.Write(document).Html;

            t.IsTrue(html.Contains("<section aria-label=\"Page 1\"", StringComparison.Ordinal),
                "a page should be a labelled region, so D moves a page at a time");
        });

        t.Test("a page says how many there are, because that is when it is worth knowing", () =>
        {
            // The count goes in the page's accessible name rather than into a separate spoken
            // announcement, so the reader delivers it on arrival in the user's own voice.
            var root = new DocumentRootElement("Two pages");

            for (int number = 1; number <= 2; number++)
            {
                var page = new PageElement(number, 612, 792);
                page.AddChild(new ParagraphElement(number, $"Page {number} text."));
                root.AddChild(page);
            }

            var document = new PdfDocumentModel("two.pdf", root);
            document.RebuildReadingOrder();

            t.IsTrue(DocumentHtmlWriter.Write(document).Html
                    .Contains("aria-label=\"Page 2 of 2\"", StringComparison.Ordinal),
                "a page in a multi-page document should name its position");
        });

        t.Test("blocks are focusable so navigation can move the reading cursor", () =>
        {
            // Scrolling does not move a screen reader's browse cursor; focus does. Without this a
            // navigation command announces its destination and leaves the user where they were —
            // the same failure the text view once had with the caret.
            var document = BuildDocument(page =>
            {
                page.AddChild(new HeadingElement(1, "Somewhere", HeadingLevel.Level2));
                page.AddChild(new ParagraphElement(1, "Some words."));
            });

            string html = DocumentHtmlWriter.Write(document).Html;

            t.IsTrue(html.Contains("<h2 id=", StringComparison.Ordinal), "the heading should be anchored");
            t.IsTrue(html.Contains("tabindex=\"-1\"", StringComparison.Ordinal),
                "blocks need tabindex minus one so they can be focused programmatically");
        });

        t.Test("links and buttons are NOT given tabindex, which would unreach them", () =>
        {
            // tabindex="-1" on something already focusable REMOVES it from the Tab order. Applied
            // to a link or a repair button it would make them unreachable by keyboard — the exact
            // opposite of the intent.
            var document = BuildDocument(page =>
            {
                page.AddChild(new LinkElement(1, "Read the terms", LinkTargetKind.WebUrl, "https://example.com"));
                page.AddChild(new FigureElement(1, new PageRegion(72, 500, 300, 700)));
            });

            string html = DocumentHtmlWriter.Write(document).Html;

            foreach (string fragment in html.Split('<'))
            {
                bool isLinkOrButton = fragment.StartsWith("a ", StringComparison.Ordinal)
                                      || fragment.StartsWith("button ", StringComparison.Ordinal);

                if (isLinkOrButton)
                {
                    t.IsFalse(fragment.Contains("tabindex", StringComparison.Ordinal),
                        $"a link or button must stay in the Tab order, but got: <{fragment.Split('>')[0]}>");
                }
            }
        });

        t.Test("a list becomes a real list, ordered when the document numbered it", () =>
        {
            var document = BuildDocument(page =>
            {
                var list = new ListElement(1) { MarkerKind = ListMarkerKind.Decimal };
                list.AddChild(new ListItemElement(1, "First"));
                list.AddChild(new ListItemElement(1, "Second"));
                page.AddChild(list);
            });

            string html = DocumentHtmlWriter.Write(document).Html;

            t.IsTrue(html.Contains("<ol", StringComparison.Ordinal), "a numbered list should be an ol");
            t.IsTrue(html.Contains("<li", StringComparison.Ordinal), "with real list items");
            t.IsFalse(html.Contains("List of 2 items", StringComparison.Ordinal),
                "the reader counts the items itself");
        });

        t.Test("a bulleted list is unordered", () =>
        {
            var document = BuildDocument(page =>
            {
                var list = new ListElement(1) { MarkerKind = ListMarkerKind.Bullet };
                list.AddChild(new ListItemElement(1, "Only"));
                page.AddChild(list);
            });

            t.IsTrue(DocumentHtmlWriter.Write(document).Html.Contains("<ul", StringComparison.Ordinal),
                "a bulleted list should be a ul");
        });

        t.Test("every element that produced markup can be found again", () =>
        {
            // The anchors are how this program scrolls the browse view to whatever its own
            // navigation moved to. An element with no anchor is one the program cannot reach.
            var heading = new HeadingElement(1, "Findable", HeadingLevel.Level1);
            var document = BuildDocument(page => page.AddChild(heading));

            var written = DocumentHtmlWriter.Write(document);

            t.IsNotNull(written.AnchorFor(heading), "the heading should have an anchor");
            t.IsTrue(written.Html.Contains($"id=\"{written.AnchorFor(heading)}\"", StringComparison.Ordinal),
                "and that anchor should appear in the markup");
        });
    }

    #endregion

    #region Tables — the reason the browse view exists

    private static void RegisterTables(TestRunner t)
    {
        t.Group("browse view — tables");

        t.Test("a table becomes a real table with scoped header cells", () =>
        {
            var document = BuildDocument(page => page.AddChild(BuildPaymentsTable()));

            string html = DocumentHtmlWriter.Write(document).Html;

            t.IsTrue(html.Contains("<table", StringComparison.Ordinal), "there should be a table");
            t.IsTrue(html.Contains("<thead>", StringComparison.Ordinal), "with the header row in a thead");
            t.IsTrue(html.Contains("scope=\"col\"", StringComparison.Ordinal), "column headers scoped to their column");
            t.IsTrue(html.Contains("scope=\"row\"", StringComparison.Ordinal), "row headers scoped to their row");
            // Every cell carries an id, so the opening tag is never bare. What matters is that the
            // amount is in a td rather than a th: a data cell wrongly marked as a header would be
            // announced as the heading of everything below it.
            t.IsTrue(html.Contains(">398.50</td>", StringComparison.Ordinal), "and data cells as data cells");
        });

        t.Test("the pipe-separated row text is gone", () =>
        {
            // What the text box had to do, and what the user asked to be rid of.
            var document = BuildDocument(page => page.AddChild(BuildPaymentsTable()));

            string html = DocumentHtmlWriter.Write(document).Html;

            t.IsFalse(html.Contains("  |  ", StringComparison.Ordinal),
                "cells must be table cells, not text with separators between them");
        });

        t.Test("a header cell with no stated scope is still given one", () =>
        {
            // An unscoped header tells the reader the cell is a header but not what it governs, so
            // it cannot announce the cell with its heading — which is the whole point.
            var table = new TableElement(1);
            var header = new TableRowElement(1);
            header.AddChild(new TableCellElement(1, "Month", TableCellRole.Header));
            header.AddChild(new TableCellElement(1, "Amount", TableCellRole.Header));
            table.AddChild(header);

            var row = new TableRowElement(1);
            row.AddChild(new TableCellElement(1, "January", TableCellRole.Header));
            row.AddChild(new TableCellElement(1, "412.00"));
            table.AddChild(row);

            var document = BuildDocument(page => page.AddChild(table));
            string html = DocumentHtmlWriter.Write(document).Html;

            t.IsTrue(html.Contains("scope=\"col\"", StringComparison.Ordinal),
                "an unscoped header in the first row should label its column");
            t.IsTrue(html.Contains("scope=\"row\"", StringComparison.Ordinal),
                "and one further down should label its row");
        });

        t.Test("spans are carried across", () =>
        {
            var table = new TableElement(1);
            var row = new TableRowElement(1);
            row.AddChild(new TableCellElement(1, "Wide") { ColumnSpan = 2 });
            table.AddChild(row);

            var document = BuildDocument(page => page.AddChild(table));

            t.IsTrue(DocumentHtmlWriter.Write(document).Html.Contains("colspan=\"2\"", StringComparison.Ordinal),
                "a spanning cell should keep its span, or the columns stop lining up");
        });

        t.Test("a caption becomes the table's name", () =>
        {
            var table = BuildPaymentsTable();
            table.Summary = "Payments for the year";

            var document = BuildDocument(page => page.AddChild(table));

            t.IsTrue(DocumentHtmlWriter.Write(document).Html
                    .Contains("<caption>Payments for the year</caption>", StringComparison.Ordinal),
                "the summary should become the caption, which is what the reader announces");
        });
    }

    #endregion

    #region Form fields

    private static void RegisterForms(TestRunner t)
    {
        t.Group("browse view — form fields");

        t.Test("a text field becomes a real input with a real label", () =>
        {
            var field = new TextFormField(1, "applicant.name") { ToolTip = "Your full name" };
            var document = BuildDocument(page => page.AddChild(field));

            string html = DocumentHtmlWriter.Write(document).Html;

            t.IsTrue(html.Contains("<input type=\"text\"", StringComparison.Ordinal), "it should be an input");
            t.IsTrue(html.Contains("<label for=", StringComparison.Ordinal), "with a label bound to it");
            t.IsTrue(html.Contains("Your full name", StringComparison.Ordinal), "using the label the document gave");
        });

        t.Test("required and read-only are attributes, not words in the label", () =>
        {
            // So the reader announces them in its own wording, and the user's verbosity setting
            // decides whether they are spoken at all.
            var field = new TextFormField(1, "x") { ToolTip = "Date of birth" };
            field.ApplyLoadedStates(FieldStates.Required);

            var document = BuildDocument(page => page.AddChild(field));
            string html = DocumentHtmlWriter.Write(document).Html;

            t.IsTrue(html.Contains("required", StringComparison.Ordinal), "required should be an attribute");
            t.IsFalse(html.Contains("Date of birth, required", StringComparison.Ordinal),
                "and not spelled into the label as the text view has to");
        });

        t.Test("a checkbox carries its own checked state", () =>
        {
            var field = new CheckBoxFormField(1, "agree") { ToolTip = "I agree" };
            field.ApplyLoadedValue("Yes");

            var document = BuildDocument(page => page.AddChild(field));
            string html = DocumentHtmlWriter.Write(document).Html;

            t.IsTrue(html.Contains("type=\"checkbox\"", StringComparison.Ordinal), "it should be a checkbox");
            t.IsTrue(html.Contains(" checked", StringComparison.Ordinal), "and show as ticked");
        });

        t.Test("a radio group becomes a fieldset with a legend", () =>
        {
            // Without the legend the reader announces each option but never the question.
            var group = new RadioGroupFormField(1, "delivery") { ToolTip = "Delivery method" };
            group.AddOption(new RadioOption("post", "By post", 1));
            group.AddOption(new RadioOption("email", "By email", 1));

            var document = BuildDocument(page => page.AddChild(group));
            string html = DocumentHtmlWriter.Write(document).Html;

            t.IsTrue(html.Contains("<fieldset", StringComparison.Ordinal), "the group should be a fieldset");
            t.IsTrue(html.Contains("<legend>", StringComparison.Ordinal), "with a legend naming it");
            t.IsTrue(html.Contains("Delivery method", StringComparison.Ordinal), "using the group's label");
            t.IsTrue(html.Contains("type=\"radio\"", StringComparison.Ordinal), "and real radio buttons");
        });

        t.Test("an unlabelled field says so where it will be heard", () =>
        {
            var field = new TextFormField(1, "Text1");
            var document = BuildDocument(page => page.AddChild(field));

            t.Says(DocumentHtmlWriter.Write(document).Html, "no label");
        });
    }

    #endregion

    #region Figures

    private static void RegisterFigures(TestRunner t)
    {
        t.Group("browse view — figures");

        t.Test("a described image becomes an image with that description", () =>
        {
            var figure = new FigureElement(1, new PageRegion(72, 500, 300, 700)) { AlternateText = "A bar chart of sales" };
            var document = BuildDocument(page => page.AddChild(figure));

            t.IsTrue(DocumentHtmlWriter.Write(document).Html
                    .Contains("alt=\"A bar chart of sales\"", StringComparison.Ordinal),
                "the description should be the image's alt text");
        });

        t.Test("an undescribed image becomes a button the reader can find", () =>
        {
            // This is how remediation reaches a browse-mode reader: the fault becomes a button, and
            // the screen reader's own B key finds it. There is no cursor for this program to read.
            var figure = new FigureElement(1, new PageRegion(72, 500, 300, 700));
            var document = BuildDocument(page => page.AddChild(figure));

            string html = DocumentHtmlWriter.Write(document).Html;

            t.IsTrue(html.Contains("<button", StringComparison.Ordinal),
                "an image with no description should be actionable, not just lamented");
            t.IsTrue(html.Contains("data-act=\"describe\"", StringComparison.Ordinal),
                "and say what activating it will do");
        });

        t.Test("a decorative image is hidden from the reader", () =>
        {
            var figure = new FigureElement(1, new PageRegion(72, 500, 300, 700)) { IsMarkedDecorative = true };
            var document = BuildDocument(page => page.AddChild(figure));

            string html = DocumentHtmlWriter.Write(document).Html;

            t.IsTrue(html.Contains("alt=\"\"", StringComparison.Ordinal),
                "a decorative image should have empty alt text, which is what hides it");
            t.IsFalse(html.Contains("data-act=\"describe\"", StringComparison.Ordinal),
                "and should not be offered for repair, because there is nothing wrong with it");
        });
    }

    #endregion

    #region Safety

    private static void RegisterSafety(TestRunner t)
    {
        t.Group("browse view — safety");

        t.Test("the page never claims to be an application", () =>
        {
            // NVDA turns browse mode OFF for role="application". One stray attribute would undo
            // this entire view, and nothing on screen would look any different.
            var document = BuildDocument(page => page.AddChild(BuildPaymentsTable()));

            t.IsFalse(DocumentHtmlWriter.Write(document).Html
                    .Contains("role=\"application\"", StringComparison.Ordinal),
                "role=application disables browse mode and must never be written");
        });

        t.Test("text from the document cannot become markup", () =>
        {
            // A PDF is untrusted input. Its text is text, whatever it looks like.
            var document = BuildDocument(page =>
                page.AddChild(new ParagraphElement(1, "<script>alert('x')</script> & <b>bold</b>")));

            string html = DocumentHtmlWriter.Write(document).Html;

            t.IsFalse(html.Contains("<script>alert", StringComparison.Ordinal),
                "a script in the document's text must not become a script in the page");
            t.IsTrue(html.Contains("&lt;script&gt;", StringComparison.Ordinal),
                "it should be escaped and read out as the text it is");
            t.IsTrue(html.Contains("&amp;", StringComparison.Ordinal), "and an ampersand escaped too");
        });

        t.Test("a quotation mark in a field value cannot break out of its attribute", () =>
        {
            var field = new TextFormField(1, "x") { ToolTip = "Name" };
            field.ApplyLoadedValue("\" onfocus=\"steal()");

            var document = BuildDocument(page => page.AddChild(field));
            string html = DocumentHtmlWriter.Write(document).Html;

            t.IsFalse(html.Contains("onfocus=\"steal", StringComparison.Ordinal),
                "a quote in a value must not escape the attribute it sits in");
        });

        t.Test("the page declares a policy that blocks the network", () =>
        {
            var document = BuildDocument(page => page.AddChild(new ParagraphElement(1, "Text.")));

            t.Says(DocumentHtmlWriter.Write(document).Html, "Content-Security-Policy");
        });

        t.Test("the document's language reaches the page", () =>
        {
            // Which voice the reader uses. Getting it wrong makes a French document unintelligible.
            var document = BuildDocument(page => page.AddChild(new ParagraphElement(1, "Bonjour.")));
            document.Metadata.Language = "fr";

            t.IsTrue(DocumentHtmlWriter.Write(document).Html.Contains("<html lang=\"fr\"", StringComparison.Ordinal),
                "the document language should be on the html element");
        });
    }

    #endregion

    #region Building documents to render

    private static PdfDocumentModel BuildDocument(Action<PageElement> fill)
    {
        var root = new DocumentRootElement("Test document");
        var page = new PageElement(1, 612, 792);

        root.AddChild(page);
        fill(page);

        var document = new PdfDocumentModel("test.pdf", root);
        document.RebuildReadingOrder();

        return document;
    }

    private static TableElement BuildPaymentsTable()
    {
        var table = new TableElement(1);

        var header = new TableRowElement(1);
        header.AddChild(new TableCellElement(1, "Month", TableCellRole.ColumnHeader));
        header.AddChild(new TableCellElement(1, "Amount", TableCellRole.ColumnHeader));
        table.AddChild(header);

        foreach (var (month, amount) in new[] { ("January", "412.00"), ("February", "398.50") })
        {
            var row = new TableRowElement(1);
            row.AddChild(new TableCellElement(1, month, TableCellRole.RowHeader));
            row.AddChild(new TableCellElement(1, amount));
            table.AddChild(row);
        }

        return table;
    }

    #endregion
}
