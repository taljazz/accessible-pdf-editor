using AccessiblePdfEditor.Auditing;
using AccessiblePdfEditor.Editing;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Model.Forms;
using AccessiblePdfEditor.Navigation;

namespace AccessiblePdfEditor.Tests;

// =====================================================================================
//  BehaviourTests.cs
//
//  Tests for navigation, editing and the accessibility auditor, against a document built
//  by hand so its exact contents are known.
//
//  The recurring theme: assert on what the user EXPERIENCES, not on internal state. That a
//  navigation command returned an element is nearly worthless; that reaching the end of the
//  document produces a boundary sound and says so, rather than silently doing nothing, is
//  the thing that decides whether the program is usable.
// =====================================================================================

internal static class BehaviourTests
{
    public static void Register(TestRunner t)
    {
        RegisterNavigation(t);
        RegisterEditing(t);
        RegisterAuditing(t);
    }

    #region A document built by hand
    // Deliberately imperfect: it has an undescribed figure, an unlabelled required field, a table
    // with no headers, no language and no title. Every one of those is a fault the auditor should
    // find and the workflow should be able to repair.

    private static PdfDocumentModel BuildSampleDocument()
    {
        var root = new DocumentRootElement("Sample");
        var document = new PdfDocumentModel("C:\\sample.pdf", root);

        var page1 = new PageElement(1, 595, 842);
        root.AddChild(page1);

        page1.AddChild(new HeadingElement(1, "Annual Report", HeadingLevel.Level1));
        page1.AddChild(new ParagraphElement(1, "This is the opening paragraph. It has two sentences."));
        page1.AddChild(new HeadingElement(1, "Revenue", HeadingLevel.Level2));

        var list = new ListElement(1, ListMarkerKind.Bullet);
        list.AddChild(new ListItemElement(1, "First point"));
        list.AddChild(new ListItemElement(1, "Second point"));
        list.AddChild(new ListItemElement(1, "Third point"));
        page1.AddChild(list);

        page1.AddChild(new LinkElement(1, "click here", LinkTargetKind.WebUrl, "https://example.org/report"));

        // No alt text: an audit finding waiting to happen.
        page1.AddChild(new FigureElement(1, new PageRegion(50, 400, 300, 600)));

        var page2 = new PageElement(2, 595, 842);
        root.AddChild(page2);

        // A heading that skips from level 2 to level 4.
        page2.AddChild(new HeadingElement(2, "Detailed figures", HeadingLevel.Level4));

        // A table with no header cells.
        var table = new TableElement(2);
        var headerRow = new TableRowElement(2);
        headerRow.AddChild(new TableCellElement(2, "Month"));
        headerRow.AddChild(new TableCellElement(2, "Revenue"));
        table.AddChild(headerRow);

        var dataRow = new TableRowElement(2);
        dataRow.AddChild(new TableCellElement(2, "March"));
        dataRow.AddChild(new TableCellElement(2, "4200"));
        table.AddChild(dataRow);

        page2.AddChild(table);

        // A required field with no label at all.
        var field = new TextFormField(2, "Text1");
        field.ApplyLoadedStates(FieldStates.Required);
        page2.AddChild(field);

        document.RebuildReadingOrder();
        document.TaggedStatus = TaggedStatus.Untagged;

        return document;
    }

    #endregion

    #region Navigation

    private static void RegisterNavigation(TestRunner t)
    {
        t.Group("navigation");

        t.Test("moving to the next heading finds it and says its level", () =>
        {
            var document = BuildSampleDocument();
            var navigation = new NavigationService();
            navigation.Attach(document);

            var result = navigation.Move(NavigationGranularity.Heading, MoveDirection.First);

            t.IsTrue(result.Moved, "there is a heading to find");
            t.Says(result.Announcement, "Annual Report");
            t.Says(result.Announcement, "heading level 1");
        });

        t.Test("reaching the end sounds different from moving", () =>
        {
            // The whole point: a key that appears to do nothing is the most disorienting thing a
            // keyboard-driven program can do.
            var document = BuildSampleDocument();
            var navigation = new NavigationService();
            navigation.Attach(document);

            navigation.Move(NavigationGranularity.Heading, MoveDirection.Last);
            var past = navigation.Move(NavigationGranularity.Heading, MoveDirection.Next);

            t.IsFalse(past.Moved, "there is nothing after the last heading");
            t.AreEqual(AccessiblePdfEditor.Accessibility.AudioCue.Boundary, past.Cue,
                "running out must play the boundary sound, not the movement one");
            t.Says(past.Announcement, "no more headings");
        });

        t.Test("the boundary message names the unit being moved by", () =>
        {
            // "No more tables" and "no more headings" are different facts, and the user needs to
            // know which so they can decide whether to change key or change strategy.
            var document = BuildSampleDocument();
            var navigation = new NavigationService();
            navigation.Attach(document);

            navigation.Move(NavigationGranularity.Table, MoveDirection.Last);
            var past = navigation.Move(NavigationGranularity.Table, MoveDirection.Next);

            t.Says(past.Announcement, "tables");
        });

        t.Test("heading level navigation reaches only that level", () =>
        {
            var document = BuildSampleDocument();
            var navigation = new NavigationService();
            navigation.Attach(document);

            var result = navigation.Move(
                NavigationGranularity.HeadingAtLevel, MoveDirection.First,
                VerbosityLevel.Normal, HeadingLevel.Level2);

            t.IsTrue(result.Moved, "there is a level 2 heading");
            t.Says(result.Announcement, "Revenue");
        });

        t.Test("moving to the next unfilled field finds the one needing an answer", () =>
        {
            var document = BuildSampleDocument();
            var navigation = new NavigationService();
            navigation.Attach(document);

            var result = navigation.Move(NavigationGranularity.UnfilledFormField, MoveDirection.First);

            t.IsTrue(result.Moved, "there is a required empty field");
            t.IsTrue(result.Element is PdfFormField, "it should be a form field");
        });

        t.Test("crossing a page boundary announces the new page", () =>
        {
            // Losing track of which page you are on is disorienting in a way that cannot be
            // recovered by listening harder.
            var document = BuildSampleDocument();
            var navigation = new NavigationService();
            navigation.Attach(document);

            navigation.Move(NavigationGranularity.HeadingAtLevel, MoveDirection.First,
                VerbosityLevel.Normal, HeadingLevel.Level4);

            t.AreEqual(2, navigation.CurrentPage, "the level 4 heading is on page 2");
        });

        t.Test("where am I gives the enclosing heading and the page", () =>
        {
            var document = BuildSampleDocument();
            var navigation = new NavigationService();
            navigation.Attach(document);

            navigation.Move(NavigationGranularity.Table, MoveDirection.First);
            string position = navigation.DescribePosition();

            t.Says(position, "page 2");
            t.Says(position, "Detailed figures");
        });

        t.Test("walking by word returns each word once", () =>
        {
            var document = BuildSampleDocument();
            var navigation = new NavigationService();
            navigation.Attach(document);

            navigation.Move(NavigationGranularity.Heading, MoveDirection.First);

            var first = navigation.Move(NavigationGranularity.Word, MoveDirection.Next);
            var second = navigation.Move(NavigationGranularity.Word, MoveDirection.Next);

            t.Says(first.Announcement, "Annual");
            t.Says(second.Announcement, "Report");
        });

        t.Test("a single character is spelled so it cannot be misheard", () =>
        {
            var document = BuildSampleDocument();
            var navigation = new NavigationService();
            navigation.Attach(document);

            navigation.Move(NavigationGranularity.Heading, MoveDirection.First);
            var result = navigation.Move(NavigationGranularity.Character, MoveDirection.Next);

            // "A" alone is easily confused by ear; "capital a" is not.
            t.Says(result.Announcement, "capital a");
        });

        t.Test("the position survives the tree being rebuilt", () =>
        {
            // Losing your place in a long document because you fixed an alt text would make
            // remediation unbearable.
            var document = BuildSampleDocument();
            var navigation = new NavigationService();
            navigation.Attach(document);

            navigation.Move(NavigationGranularity.Table, MoveDirection.First);
            int before = navigation.Current!.Id;

            document.RebuildReadingOrder();
            navigation.Reattach();

            t.AreEqual(before, navigation.Current!.Id, "the position should be unchanged");
        });
    }

    #endregion

    #region Editing

    private static void RegisterEditing(TestRunner t)
    {
        t.Group("editing and undo");

        t.Test("an edit describes itself in words", () =>
        {
            // Undo without sight is a leap of faith unless the program says what it reversed.
            var document = BuildSampleDocument();
            var figure = document.Figures[0];

            var command = new SetAlternateTextCommand(figure, "A bar chart of quarterly revenue");

            t.Says(command.Description, "described the figure");
            t.Says(command.Description, "bar chart");
        });

        t.Test("undo says what it undid", () =>
        {
            var document = BuildSampleDocument();
            var history = new EditHistory(document);
            var figure = document.Figures[0];

            history.Do(new SetAlternateTextCommand(figure, "A bar chart of quarterly revenue"));
            var undone = history.Undo();

            t.IsTrue(undone.Succeeded, "the change should be undoable");
            t.Says(undone.Message, "Undone");
            t.Says(undone.Message, "bar chart");
        });

        t.Test("undo restores the previous value exactly", () =>
        {
            var document = BuildSampleDocument();
            var history = new EditHistory(document);
            var figure = document.Figures[0];

            figure.SetAlternateText("Original description");
            history.Do(new SetAlternateTextCommand(figure, "New description"));

            t.AreEqual("New description", figure.AlternateText, "the change should be applied");

            history.Undo();
            t.AreEqual("Original description", figure.AlternateText, "the original should come back");
        });

        t.Test("undoing with nothing to undo says so instead of doing nothing", () =>
        {
            var document = BuildSampleDocument();
            var history = new EditHistory(document);

            var result = history.Undo();

            t.IsFalse(result.Succeeded, "there is nothing to undo");
            t.Says(result.Message, "nothing to undo");
        });

        t.Test("typing into one field collapses into a single undo step", () =>
        {
            // Otherwise undoing a mistyped name means fifteen presses of Ctrl+Z, each announcing a
            // near-identical change.
            var document = BuildSampleDocument();
            var history = new EditHistory(document);
            var field = document.FormFields[0];

            history.Do(new SetFieldValueCommand(field, "Th"));
            history.Do(new SetFieldValueCommand(field, "Thom"));
            history.Do(new SetFieldValueCommand(field, "Thomas"));

            t.AreEqual(1, history.AppliedCount, "three keystrokes should be one undo step");
        });

        t.Test("changes are summarised by kind rather than listed one by one", () =>
        {
            var document = BuildSampleDocument();
            var history = new EditHistory(document);

            history.Do(new SetAlternateTextCommand(document.Figures[0], "A chart"));
            history.Do(new SetFieldLabelCommand(document.FormFields[0], "Full name"));
            history.Do(new SetDocumentLanguageCommand(document, "en-GB"));

            string summary = history.SummariseChanges();

            t.Says(summary, "3 changes");
            t.Says(summary, "image description");
            t.Says(summary, "field label");
        });

        t.Test("making a change after undoing discards the redo branch", () =>
        {
            var document = BuildSampleDocument();
            var history = new EditHistory(document);
            var figure = document.Figures[0];

            history.Do(new SetAlternateTextCommand(figure, "First"));
            history.Undo();

            t.IsTrue(history.CanRedo, "the undone change should be redoable");

            history.Do(new SetAlternateTextCommand(figure, "Second"));

            t.IsFalse(history.CanRedo, "a new change should discard the redo branch");
        });

        t.Test("setting a title also switches on displaying it", () =>
        {
            // A title that is not displayed is never announced, so setting one alone achieves
            // nothing. The two go together.
            var document = BuildSampleDocument();
            var history = new EditHistory(document);

            history.Do(new SetDocumentTitleCommand(document, "Annual Report 2026"));

            t.AreEqual("Annual Report 2026", document.Metadata.Title, "the title should be set");
            t.IsTrue(document.Metadata.DisplaysDocumentTitle, "displaying it should be switched on too");
        });

        t.Test("marking a header row makes the cells below announce their headings", () =>
        {
            var document = BuildSampleDocument();
            var history = new EditHistory(document);
            var table = document.Tables[0];

            t.IsFalse(table.HasHeaderCells, "the table starts with no headers");

            history.Do(new MarkHeaderRowCommand(table.Rows[0]));

            t.IsTrue(table.HasHeaderCells, "the table should now have headers");

            string spoken = table.Rows[1].Cells[1].Describe(VerbosityLevel.Normal);
            t.Says(spoken, "Revenue");
            t.Says(spoken, "4200");
        });

        t.Test("an edit marks the document as having unsaved changes", () =>
        {
            var document = BuildSampleDocument();
            var history = new EditHistory(document);

            t.IsFalse(document.HasUnsavedChanges, "a fresh document has no changes");

            history.Do(new SetAlternateTextCommand(document.Figures[0], "A chart"));

            t.IsTrue(document.HasUnsavedChanges, "an edit should mark it changed");
        });
    }

    #endregion

    #region Auditing

    private static void RegisterAuditing(TestRunner t)
    {
        t.Group("accessibility audit");

        t.Test("the undescribed figure is found", () =>
        {
            var report = new AccessibilityAuditor().Audit(BuildSampleDocument());

            var finding = report.Issues.FirstOrDefault(i => i.RuleName == "missing image descriptions");

            t.IsNotNull(finding, "the undescribed figure should be reported");
            t.AreEqual(IssueSeverity.Serious, finding!.Severity, "it is a serious problem");
        });

        t.Test("the unlabelled required field is a blocker", () =>
        {
            // The user is asked to type something into a box with no name. On a real form that has
            // consequences well beyond software.
            var report = new AccessibilityAuditor().Audit(BuildSampleDocument());

            var finding = report.Issues.FirstOrDefault(i => i.RuleName == "unlabelled fields");

            t.IsNotNull(finding, "the unlabelled field should be reported");
            t.AreEqual(IssueSeverity.Blocker, finding!.Severity, "an unnamed field blocks the form");
        });

        t.Test("the table with no headers is found", () =>
        {
            var report = new AccessibilityAuditor().Audit(BuildSampleDocument());

            t.IsNotNull(report.Issues.FirstOrDefault(i => i.RuleName == "tables without headers"),
                "the header-less table should be reported");
        });

        t.Test("the skipped heading level is found", () =>
        {
            var report = new AccessibilityAuditor().Audit(BuildSampleDocument());

            t.IsNotNull(report.Issues.FirstOrDefault(i => i.RuleName == "skipped heading levels"),
                "level 2 followed by level 4 should be reported");
        });

        t.Test("the vague link text is found", () =>
        {
            var report = new AccessibilityAuditor().Audit(BuildSampleDocument());

            t.IsNotNull(report.Issues.FirstOrDefault(i => i.RuleName == "unclear link text"),
                "\"click here\" should be reported");
        });

        t.Test("the missing language is found", () =>
        {
            var report = new AccessibilityAuditor().Audit(BuildSampleDocument());

            t.IsNotNull(report.Issues.FirstOrDefault(i => i.RuleName == "document language"),
                "a document with no declared language should be reported");
        });

        t.Test("every finding says what it means for a reader", () =>
        {
            // "Missing /Alt" is a fact about a file. "You will hear nothing where this image is" is
            // a reason to fix it.
            var report = new AccessibilityAuditor().Audit(BuildSampleDocument());

            foreach (var issue in report.Issues)
            {
                t.IsTrue(issue.Consequence.Length > 20,
                    $"the finding \"{issue.Title}\" should explain what it means for a reader");
            }
        });

        t.Test("the summary leads with whether the document can be read", () =>
        {
            var report = new AccessibilityAuditor().Audit(BuildSampleDocument());
            string summary = report.BuildSummary();

            t.Says(summary, "problems found");
            t.Says(summary, "repaired here");
        });

        t.Test("findings sort most serious first", () =>
        {
            var report = new AccessibilityAuditor().Audit(BuildSampleDocument());

            for (int i = 1; i < report.Issues.Count; i++)
            {
                t.IsTrue(report.Issues[i - 1].Severity <= report.Issues[i].Severity,
                    "findings should be ordered by severity");
            }
        });

        t.Test("a repaired problem stops being reported", () =>
        {
            var document = BuildSampleDocument();
            var auditor = new AccessibilityAuditor();

            int before = auditor.Audit(document).Issues
                .Count(i => i.RuleName == "missing image descriptions");

            document.Figures[0].SetAlternateText("A bar chart of quarterly revenue");

            int after = auditor.Audit(document).Issues
                .Count(i => i.RuleName == "missing image descriptions");

            t.IsTrue(before > 0, "the problem should exist before the repair");
            t.AreEqual(0, after, "and be gone after it");
        });

        t.Test("an auditor with a broken rule still returns the other findings", () =>
        {
            // The audit is often the first thing run on an unfamiliar document, and a document odd
            // enough to break one rule is exactly the one whose other findings matter most.
            var rules = new List<AuditRuleBase>
            {
                new ThrowingRule(),
                new MissingAlternateTextRule(),
            };

            var report = new AccessibilityAuditor(rules).Audit(BuildSampleDocument());

            t.IsTrue(report.Issues.Count > 0, "the working rule's findings should survive");
        });
    }

    /// <summary>A rule that always throws, to prove one bad rule cannot take the audit down.</summary>
    private sealed class ThrowingRule : AuditRuleBase
    {
        public override string Name => "deliberately broken";

        protected override IEnumerable<AccessibilityIssue> CheckCore(PdfDocumentModel document) =>
            throw new InvalidOperationException("This rule is broken on purpose.");
    }

    #endregion
}
