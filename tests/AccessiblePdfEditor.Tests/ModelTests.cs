using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Model.Forms;

namespace AccessiblePdfEditor.Tests;

// =====================================================================================
//  ModelTests.cs
//
//  Tests for the document model: the announcement template, the element hierarchy, and
//  form field validation.
//
//  Almost every assertion here is about WHAT THE PROGRAM SAYS. That is deliberate. For a
//  screen-reader application the spoken output is not a presentation detail sitting on top
//  of the real behaviour — it IS the behaviour. A table cell that computes its headers
//  correctly but does not mention them is broken, and only a test that reads the
//  announcement can tell.
// =====================================================================================

internal static class ModelTests
{
    public static void Register(TestRunner t)
    {
        RegisterAnnouncementShape(t);
        RegisterHeadingsAndLists(t);
        RegisterTables(t);
        RegisterFigures(t);
        RegisterLinks(t);
        RegisterFieldNaming(t);
        RegisterFieldValidation(t);
        RegisterFieldStates(t);
    }

    #region The announcement template
    // The template method on DocumentElement fixes the order of every announcement in the program.
    // These tests pin that order, because a subclass that reordered it would still compile and
    // would make the whole application sound inconsistent.

    private static void RegisterAnnouncementShape(TestRunner t)
    {
        t.Group("announcement shape");

        t.Test("role comes before content", () =>
        {
            var heading = new HeadingElement(1, "Introduction", HeadingLevel.Level2);
            string spoken = heading.Describe(VerbosityLevel.Normal);

            int rolePosition = spoken.IndexOf("heading", StringComparison.OrdinalIgnoreCase);
            int contentPosition = spoken.IndexOf("Introduction", StringComparison.Ordinal);

            t.IsTrue(rolePosition >= 0 && contentPosition > rolePosition,
                $"role should precede content, but heard \"{spoken}\"");
        });

        t.Test("terse verbosity drops the role", () =>
        {
            var heading = new HeadingElement(1, "Introduction", HeadingLevel.Level2);
            t.DoesNotSay(heading.Describe(VerbosityLevel.Terse), "heading level");
        });

        t.Test("detailed verbosity adds position", () =>
        {
            var paragraph = new ParagraphElement(7, "Some body text.");
            t.Says(paragraph.Describe(VerbosityLevel.Detailed), "page 7");
        });

        t.Test("an element with no text still says something", () =>
        {
            // A silent element is an arrow-key press that appears to do nothing, which is the most
            // disorienting thing a keyboard-driven reader can do.
            var empty = new ParagraphElement(1, "");
            string spoken = empty.Describe(VerbosityLevel.Terse);
            t.IsTrue(spoken.Length > 0, "an empty element must still announce something");
        });

        t.Test("paragraphs do not say 'paragraph' at normal verbosity", () =>
        {
            // Prose is the default. Saying "paragraph" before every paragraph would double the
            // length of every document without adding anything.
            var paragraph = new ParagraphElement(1, "Body text here.");
            t.DoesNotSay(paragraph.Describe(VerbosityLevel.Normal), "paragraph");
        });

        t.Test("artifacts are skipped in continuous reading but still describable", () =>
        {
            var artifact = new ArtifactElement(3, "Annual Report 2026", "header");
            t.IsFalse(artifact.IsReadInContinuousReading, "artifacts should be skipped when reading straight through");
            t.Says(artifact.Describe(VerbosityLevel.Normal), "Annual Report");
        });
    }

    #endregion

    #region Headings and lists

    private static void RegisterHeadingsAndLists(TestRunner t)
    {
        t.Group("headings and lists");

        t.Test("heading announces its level the way NVDA does", () =>
        {
            var heading = new HeadingElement(1, "Methods", HeadingLevel.Level3);
            t.Says(heading.Describe(VerbosityLevel.Normal), "heading level 3");
        });

        t.Test("an inferred heading admits it at detailed verbosity", () =>
        {
            var heading = new HeadingElement(1, "Methods", HeadingLevel.Level3) { };
            // IsFromRealTags defaults to false, meaning it was inferred from layout.
            t.Says(heading.Describe(VerbosityLevel.Detailed), "inferred");
        });

        t.Test("a tagged heading does not claim to be inferred", () =>
        {
            var heading = new HeadingElement(1, "Methods", HeadingLevel.Level3)
            {
                IsFromRealTags = true,
            };

            t.DoesNotSay(heading.Describe(VerbosityLevel.Detailed), "inferred");
        });

        t.Test("a list says how many items it has", () =>
        {
            // The count is the thing a listener cannot get any other way.
            var list = new ListElement(1, ListMarkerKind.Bullet);
            for (int i = 1; i <= 4; i++)
                list.AddChild(new ListItemElement(1, $"Item {i}"));

            string spoken = list.Describe(VerbosityLevel.Normal);
            t.Says(spoken, "4 items");
            t.Says(spoken, "bulleted list");
        });

        t.Test("a list item says where it sits at normal verbosity", () =>
        {
            var list = new ListElement(1, ListMarkerKind.Decimal);
            for (int i = 1; i <= 5; i++)
                list.AddChild(new ListItemElement(1, $"Item {i}"));

            string spoken = list.Children[2].Describe(VerbosityLevel.Normal);
            t.Says(spoken, "item 3 of 5");
        });

        t.Test("a nested list announces its level", () =>
        {
            var outer = new ListElement(1, ListMarkerKind.Bullet);
            var item = new ListItemElement(1, "Parent item");
            var inner = new ListElement(1, ListMarkerKind.Bullet);

            outer.AddChild(item);
            item.AddChild(inner);
            inner.AddChild(new ListItemElement(1, "Child item"));

            t.Says(inner.Describe(VerbosityLevel.Normal), "level 2");
        });

        t.Test("an item containing a sub-list warns before you arrow past it", () =>
        {
            var outer = new ListElement(1, ListMarkerKind.Bullet);
            var item = new ListItemElement(1, "Parent item");
            var inner = new ListElement(1, ListMarkerKind.Bullet);

            outer.AddChild(item);
            item.AddChild(inner);
            inner.AddChild(new ListItemElement(1, "Child one"));
            inner.AddChild(new ListItemElement(1, "Child two"));

            t.Says(item.Describe(VerbosityLevel.Normal), "contains a list");
        });
    }

    #endregion

    #region Tables
    // The most important tests in this file. A data cell that does not announce its headers gives
    // a listener a bare number with nothing to say what it means.

    private static void RegisterTables(TestRunner t)
    {
        t.Group("tables");

        t.Test("a data cell announces its row and column headers", () =>
        {
            var table = BuildRevenueTable();
            var dataCell = table.Rows[1].Cells[1];

            string spoken = dataCell.Describe(VerbosityLevel.Normal);

            t.Says(spoken, "March");   // row header
            t.Says(spoken, "Revenue"); // column header
            t.Says(spoken, "4200");    // the value itself
        });

        t.Test("headers come before the value", () =>
        {
            var table = BuildRevenueTable();
            string spoken = table.Rows[1].Cells[1].Describe(VerbosityLevel.Normal);

            int headerPosition = spoken.IndexOf("Revenue", StringComparison.Ordinal);
            int valuePosition = spoken.IndexOf("4200", StringComparison.Ordinal);

            t.IsTrue(headerPosition >= 0 && valuePosition > headerPosition,
                $"headers should come before the value, but heard \"{spoken}\"");
        });

        t.Test("a header cell does not repeat headers back at itself", () =>
        {
            var table = BuildRevenueTable();
            var header = table.Rows[0].Cells[1];
            t.Says(header.Describe(VerbosityLevel.Normal), "column header");
        });

        t.Test("a table with no header cells warns the listener on arrival", () =>
        {
            var table = new TableElement(1);
            var row = new TableRowElement(1);
            row.AddChild(new TableCellElement(1, "1"));
            row.AddChild(new TableCellElement(1, "2"));
            table.AddChild(row);

            t.IsFalse(table.HasHeaderCells, "this table has no header cells");
            t.Says(table.Describe(VerbosityLevel.Normal), "no header cells");
        });

        t.Test("a blank cell says 'blank' rather than nothing", () =>
        {
            var table = BuildRevenueTable();
            var blank = new TableCellElement(1, "");
            table.Rows[1].AddChild(blank);

            t.Says(blank.Describe(VerbosityLevel.Normal), "blank");
        });

        t.Test("a spanning cell says so, so the listener keeps their column count", () =>
        {
            var table = new TableElement(1);
            var row = new TableRowElement(1);
            var spanning = new TableCellElement(1, "Total") { ColumnSpan = 3 };
            row.AddChild(spanning);
            table.AddChild(row);

            t.Says(spanning.Describe(VerbosityLevel.Normal), "spans 3 columns");
        });

        t.Test("column index accounts for preceding spans", () =>
        {
            var table = new TableElement(1);
            var row = new TableRowElement(1);
            row.AddChild(new TableCellElement(1, "wide") { ColumnSpan = 2 });
            var third = new TableCellElement(1, "third");
            row.AddChild(third);
            table.AddChild(row);

            t.AreEqual(2, third.ColumnIndex, "a cell after a two-column span starts at index 2");
        });

        static TableElement BuildRevenueTable()
        {
            var table = new TableElement(1);

            var headerRow = new TableRowElement(1);
            headerRow.AddChild(new TableCellElement(1, "Month", TableCellRole.ColumnHeader));
            headerRow.AddChild(new TableCellElement(1, "Revenue", TableCellRole.ColumnHeader));
            table.AddChild(headerRow);

            var dataRow = new TableRowElement(1);
            dataRow.AddChild(new TableCellElement(1, "March", TableCellRole.RowHeader));
            dataRow.AddChild(new TableCellElement(1, "4200"));
            table.AddChild(dataRow);

            return table;
        }
    }

    #endregion

    #region Figures

    private static void RegisterFigures(TestRunner t)
    {
        t.Group("figures");

        t.Test("a described figure reads its description", () =>
        {
            var figure = new FigureElement(1, new PageRegion(0, 0, 200, 150));
            figure.SetAlternateText("A bar chart of quarterly revenue");

            t.Says(figure.Describe(VerbosityLevel.Normal), "bar chart of quarterly revenue");
        });

        t.Test("an undescribed figure says so rather than staying silent", () =>
        {
            // Silence would leave the listener unable to tell whether they missed a decorative
            // flourish or the chart the whole report is about.
            var figure = new FigureElement(1, new PageRegion(0, 0, 200, 150));
            t.Says(figure.Describe(VerbosityLevel.Normal), "no description");
        });

        t.Test("no alt text and marked decorative are different states", () =>
        {
            var undescribed = new FigureElement(1, new PageRegion(0, 0, 200, 150));
            var decorative = new FigureElement(1, new PageRegion(0, 0, 200, 150));
            decorative.MarkDecorative();

            t.IsTrue(undescribed.NeedsAlternateText, "a figure with no alt text needs one");
            t.IsFalse(decorative.NeedsAlternateText, "a figure marked decorative does not need one");
        });

        t.Test("marking decorative clears any description", () =>
        {
            var figure = new FigureElement(1, new PageRegion(0, 0, 200, 150));
            figure.SetAlternateText("Something");
            figure.MarkDecorative();

            t.IsNull(figure.AlternateText, "a decorative figure must not also carry a description");
        });

        t.Test("tiny images are recognised as probably decorative", () =>
        {
            // Otherwise the remediation list fills with spacer graphics and gets abandoned.
            var spacer = new FigureElement(1, new PageRegion(0, 0, 1, 1)) { PixelWidth = 1, PixelHeight = 1 };
            t.IsTrue(spacer.IsLikelyDecorativeBySize, "a one-point image is decorative");
        });

        t.Test("a figure with a caption but no alt text offers the caption", () =>
        {
            var figure = new FigureElement(1, new PageRegion(0, 0, 200, 150));
            figure.AddChild(new CaptionElement(1, "Figure 4: revenue by region"));

            t.Says(figure.Describe(VerbosityLevel.Normal), "revenue by region");
        });
    }

    #endregion

    #region Links

    private static void RegisterLinks(TestRunner t)
    {
        t.Group("links");

        t.Test("a link with vague text has its destination added", () =>
        {
            var link = new LinkElement(1, "click here", LinkTargetKind.WebUrl, "https://example.org/report");
            string spoken = link.Describe(VerbosityLevel.Normal);

            t.Says(spoken, "click here");
            t.Says(spoken, "example.org");
        });

        t.Test("uninformative link text is flagged", () =>
        {
            var vague = new LinkElement(1, "read more", LinkTargetKind.WebUrl, "https://example.org");
            var clear = new LinkElement(1, "Download the 2026 annual report", LinkTargetKind.WebUrl, "https://example.org");

            t.IsTrue(vague.HasUninformativeText, "'read more' says nothing about the destination");
            t.IsFalse(clear.HasUninformativeText, "a descriptive link is fine");
        });

        t.Test("a bare URL as link text counts as uninformative", () =>
        {
            var link = new LinkElement(1,
                "https://example.org/very/long/path/that/nobody/could/listen/to/report-2026-final.pdf",
                LinkTargetKind.WebUrl, "https://example.org");

            t.IsTrue(link.HasUninformativeText, "a long unbroken URL is unlistenable");
        });

        t.Test("a URL is spoken as its host rather than character by character", () =>
        {
            var link = new LinkElement(1, "report", LinkTargetKind.WebUrl, "https://example.org/docs/2026");
            t.Says(link.SpeakableTarget, "example.org");
        });

        t.Test("a link the editor will not follow explains itself", () =>
        {
            var link = new LinkElement(1, "Run", LinkTargetKind.UnsupportedAction, "javascript:void(0)");
            t.IsFalse(link.CanActivate, "script links are not activated");
        });
    }

    #endregion

    #region Field naming
    // The label resolution order is the single most important piece of form accessibility. These
    // tests pin it, because a regression here turns every field into "edit box, blank".

    private static void RegisterFieldNaming(TestRunner t)
    {
        t.Group("form field naming");

        t.Test("a tooltip wins over everything else", () =>
        {
            var field = new TextFormField(1, "Text1") { ToolTip = "Full name" };
            field.RecoveredLabelForTest("Something else");

            t.AreEqual("Full name", field.Label, "the document's own tooltip is authoritative");
            t.AreEqual(PdfFormField.LabelSource.ToolTip, field.ResolvedLabelSource, "source should be the tooltip");
        });

        t.Test("text recovered from the page is used when there is no tooltip", () =>
        {
            var field = new TextFormField(1, "Text1");
            field.RecoveredLabelForTest("Date of birth");

            t.AreEqual("Date of birth", field.Label, "recovered text is the next best source");
            t.AreEqual(PdfFormField.LabelSource.NearbyText, field.ResolvedLabelSource, "source should be nearby text");
        });

        t.Test("a generic field name is not treated as a label", () =>
        {
            // "Text1" is the form designer's default and carries no information. Reading it as a
            // label would give the user false confidence that they know what they are filling in.
            var field = new TextFormField(1, "Text1");

            t.AreEqual(PdfFormField.LabelSource.None, field.ResolvedLabelSource, "'Text1' is not a label");
            t.IsTrue(field.IsUnlabelled, "the field should report itself as unlabelled");
            t.Says(field.Label, "unlabelled");
        });

        t.Test("a meaningful field name is split into words", () =>
        {
            // Compared without regard to case, deliberately. Case is inaudible — "Full Name" and
            // "full name" are the same sound — so it is not worth normalising, and normalising it
            // would wreck the acronyms covered by the next test.
            var camel = new TextFormField(1, "applicantFullName");
            var snake = new TextFormField(1, "applicant_full_name");

            t.AreEqual("applicant full name", camel.Label.ToLowerInvariant(),
                "camelCase should be split into words");

            t.AreEqual("applicant full name", snake.Label.ToLowerInvariant(),
                "underscores should become spaces");
        });

        t.Test("an acronym in a field name survives intact", () =>
        {
            // "NHS number" must not become "n h s number" or "nhs number" — a screen reader reads
            // capitals as an initialism, which is exactly right here and would be lost by
            // lower-casing the name.
            var field = new TextFormField(1, "applicantNHSNumber");
            t.Says(field.Label, "NHS");
        });

        t.Test("a dotted field name yields the last segment", () =>
        {
            var field = new TextFormField(1, "applicant.address.postcode") { ToolTip = null };
            t.AreEqual("postcode", field.PartialName, "the partial name is the last segment");
        });

        t.Test("an unlabelled field is called out at detailed verbosity", () =>
        {
            var field = new TextFormField(1, "Text1");
            t.Says(field.Describe(VerbosityLevel.Detailed), "no label");
        });
    }

    #endregion

    #region Field validation

    private static void RegisterFieldValidation(TestRunner t)
    {
        t.Group("form field validation");

        t.Test("a rejection says what would be acceptable", () =>
        {
            // "Invalid" alone tells someone they are stuck without telling them how to get unstuck,
            // which for a listener is worse than useless.
            var field = new TextFormField(1, "dateOfBirth") { ToolTip = "Date of birth" };
            field.Format = TextFieldFormat.Date;

            var result = field.TrySetValue("not a date");

            t.IsFalse(result.Accepted, "'not a date' is not a date");
            t.Says(result.Message, "31/03/2026");
        });

        t.Test("a date is normalised and read back unambiguously", () =>
        {
            var field = new TextFormField(1, "dob") { ToolTip = "Date" };
            field.Format = TextFieldFormat.Date;

            var result = field.TrySetValue("3/4/2026");

            t.IsTrue(result.Accepted, "3/4/2026 is a date");
            t.AreEqual("03/04/2026", field.Value, "the stored value should be unambiguous");
        });

        t.Test("format is inferred from the field's own name", () =>
        {
            t.AreEqual(TextFieldFormat.Email,
                TextFormField.InferFormat("Email address", "email", false), "an email field");

            t.AreEqual(TextFieldFormat.Date,
                TextFormField.InferFormat(null, "dateOfBirth", false), "a date field");

            t.AreEqual(TextFieldFormat.PlainText,
                TextFormField.InferFormat(null, "notes", false), "an ordinary field");
        });

        t.Test("a comb field requires an exact length", () =>
        {
            var field = new TextFormField(1, "reference") { ToolTip = "Reference", MaxLength = 6 };
            field.Format = TextFieldFormat.Comb;

            t.IsFalse(field.TrySetValue("123").Accepted, "three characters is not six");
            t.IsTrue(field.TrySetValue("123456").Accepted, "six characters is right");
        });

        t.Test("a currency field tolerates symbols and separators", () =>
        {
            // Rejecting "£1,200.50" for punctuation would be pedantry, not validation.
            var field = new TextFormField(1, "amount") { ToolTip = "Amount" };
            field.Format = TextFieldFormat.Currency;

            t.IsTrue(field.TrySetValue("£1,200.50").Accepted, "a normally-typed amount should be accepted");
        });

        t.Test("clearing a field is always allowed", () =>
        {
            var field = new TextFormField(1, "dob") { ToolTip = "Date" };
            field.Format = TextFieldFormat.Date;
            field.TrySetValue("01/01/2026");

            t.IsTrue(field.Clear().Accepted, "a field can always be emptied");
            t.IsFalse(field.HasValue, "the field should now be empty");
        });

        t.Test("a read-only field refuses politely", () =>
        {
            var field = new TextFormField(1, "reference") { ToolTip = "Reference" };
            field.ApplyLoadedStates(FieldStates.ReadOnly);

            var result = field.TrySetValue("anything");

            t.IsFalse(result.Accepted, "a read-only field cannot be changed");
            t.Says(result.Message, "read-only");
        });

        t.Test("a checkbox accepts the words a person would actually use", () =>
        {
            var field = new CheckBoxFormField(1, "agree") { ToolTip = "I agree" };

            foreach (string yes in new[] { "yes", "on", "true", "1", "ticked" })
            {
                field.TrySetValue(yes);
                t.IsTrue(field.IsChecked, $"'{yes}' should tick the box");
                field.TrySetValue("off");
            }
        });

        t.Test("a choice field matches a unique prefix", () =>
        {
            // How someone actually uses a long list: type enough letters to be unambiguous.
            var field = new ChoiceFormField(1, "country", isComboBox: true) { ToolTip = "Country" };
            field.AddOption(new ChoiceOption("UK", "United Kingdom"));
            field.AddOption(new ChoiceOption("NL", "Netherlands"));
            field.AddOption(new ChoiceOption("NZ", "New Zealand"));

            var result = field.TrySetValue("Neth");

            t.IsTrue(result.Accepted, "'Neth' uniquely identifies Netherlands");
            t.AreEqual("NL", field.SelectedExportValues[0], "the export value should be stored");
        });

        t.Test("an ambiguous prefix matches nothing rather than guessing", () =>
        {
            var field = new ChoiceFormField(1, "country", isComboBox: true) { ToolTip = "Country" };
            field.AddOption(new ChoiceOption("NL", "Netherlands"));
            field.AddOption(new ChoiceOption("NZ", "New Zealand"));

            t.IsFalse(field.TrySetValue("Ne").Accepted, "'Ne' is ambiguous and must not silently pick one");
        });

        t.Test("a rejected choice lists what is available", () =>
        {
            var field = new ChoiceFormField(1, "colour", isComboBox: true) { ToolTip = "Colour" };
            field.AddOption(new ChoiceOption("R", "Red"));
            field.AddOption(new ChoiceOption("B", "Blue"));

            var result = field.TrySetValue("Green");

            t.IsFalse(result.Accepted, "Green is not an option");
            t.Says(result.Message, "Red");
            t.Says(result.Message, "Blue");
        });

        t.Test("a radio group can be set by its spoken label", () =>
        {
            // Everything the user hears is the label; being unable to set a value using the only
            // words the program ever said would be indefensible.
            var group = new RadioGroupFormField(1, "contact") { ToolTip = "Contact method" };
            group.AddOption(new RadioOption("Opt1", "By email"));
            group.AddOption(new RadioOption("Opt2", "By phone"));

            t.IsTrue(group.TrySetValue("By phone").Accepted, "the label should be accepted");
            t.AreEqual("Opt2", group.SelectedExportValue, "the export value should be stored");
        });

        t.Test("a radio group reports its position", () =>
        {
            var group = new RadioGroupFormField(1, "contact") { ToolTip = "Contact method" };
            group.AddOption(new RadioOption("Opt1", "By email"));
            group.AddOption(new RadioOption("Opt2", "By phone"));
            group.AddOption(new RadioOption("Opt3", "By post"));
            group.TrySetValue("Opt2");

            t.Says(group.ValueForSpeech, "2 of 3");
        });

        t.Test("a signature field is never fillable and says why", () =>
        {
            var field = new SignatureFormField(1, "signature") { ToolTip = "Signature" };

            t.IsFalse(field.CanActivate, "this editor does not apply signatures");
            t.IsFalse(field.TrySetValue("me").Accepted, "a signature cannot be typed");
        });
    }

    #endregion

    #region Field state tracking

    private static void RegisterFieldStates(TestRunner t)
    {
        t.Group("form field state");

        t.Test("loading a value does not count as an edit", () =>
        {
            // Otherwise every opened form would immediately report unsaved changes.
            var field = new TextFormField(1, "name") { ToolTip = "Name" };
            field.ApplyLoadedValue("Existing value");

            t.IsTrue(field.HasValue, "the loaded value should be present");
            t.IsFalse(field.IsModified, "loading is not editing");
        });

        t.Test("setting a value does count as an edit", () =>
        {
            var field = new TextFormField(1, "name") { ToolTip = "Name" };
            field.TrySetValue("Typed value");

            t.IsTrue(field.IsModified, "a value the user typed is an edit");
        });

        t.Test("a required empty field needs attention", () =>
        {
            var field = new TextFormField(1, "name") { ToolTip = "Name" };
            field.ApplyLoadedStates(FieldStates.Required);

            t.IsTrue(field.NeedsAttention, "a required empty field is not finished");

            field.TrySetValue("Filled in");
            t.IsFalse(field.NeedsAttention, "once filled it no longer needs attention");
        });

        t.Test("a required read-only field never needs attention", () =>
        {
            // The user cannot act on it, so listing it among the fields still to do would send
            // them looking for something they cannot change.
            var field = new TextFormField(1, "reference") { ToolTip = "Reference" };
            field.ApplyLoadedStates(FieldStates.Required | FieldStates.ReadOnly);

            t.IsFalse(field.NeedsAttention, "a read-only field cannot be filled in by the user");
        });

        t.Test("required and read-only are both announced", () =>
        {
            var field = new TextFormField(1, "name") { ToolTip = "Name" };
            field.ApplyLoadedStates(FieldStates.Required);

            t.Says(field.Describe(VerbosityLevel.Normal), "required");
        });

        t.Test("a password field reports length without revealing contents", () =>
        {
            var field = new TextFormField(1, "pin") { ToolTip = "PIN" };
            field.ApplyLoadedStates(FieldStates.Password);
            field.TrySetValue("1234");

            string spoken = field.ValueForSpeech;
            t.Says(spoken, "4 characters");
            t.DoesNotSay(spoken, "1234");
        });
    }

    #endregion
}

#region Test helpers

/// <summary>
/// Small helpers for setting state the loader normally supplies. The setters are internal in
/// production so that only the loader and the edit commands can reach them; the test assembly is
/// granted access rather than the API being widened.
/// </summary>
internal static class TestFieldExtensions
{
    /// <summary>Sets the label recovered from the page, which the loader normally supplies.</summary>
    public static void RecoveredLabelForTest(this PdfFormField field, string label) =>
        field.RecoveredLabel = label;
}

#endregion
