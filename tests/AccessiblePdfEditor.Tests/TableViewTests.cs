using System.Windows.Automation;
using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Ingestion;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Persistence;
using AccessiblePdfEditor.UI;

namespace AccessiblePdfEditor.Tests;

// =====================================================================================
//  TableViewTests.cs
//
//  Tests that a table opened from the document is a REAL table as far as assistive
//  technology is concerned.
//
//  These assert on the UI Automation tree, which is the substrate every screen reader
//  reads. That is deliberate: no screen reader is installed here, so claiming "NVDA
//  announces the header" would be a guess. What CAN be established, and is what these
//  tests establish, is that the control exposes the exact interfaces a screen reader uses
//  to find that header — the Table and TableItem patterns — and that the headers those
//  interfaces return are the document's own.
//
//  The control case matters as much as the positive one: a read-only text box holding the
//  same data supports no Grid or Table pattern at all, which is precisely why laying a
//  table out as text could never work however neat the layout.
// =====================================================================================

internal static class TableViewTests
{
    public static void Register(TestRunner t)
    {
        RegisterAutomationTree(t);
        RegisterContent(t);
    }

    #region What assistive technology can see

    private static void RegisterAutomationTree(TestRunner t)
    {
        t.Group("table view — what a screen reader sees");

        t.Test("a text box exposes no table semantics at all", () =>
        {
            // The reason this whole feature exists. However carefully a table is laid out as text,
            // a screen reader is looking at characters.
            using var form = new Form();
            using var box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Text = "Month | Amount\r\nJanuary | 412.00",
            };

            form.Controls.Add(box);
            _ = box.Handle;

            var element = AutomationElement.FromHandle(box.Handle);

            t.IsFalse(element.TryGetCurrentPattern(GridPattern.Pattern, out _),
                "a text box must not be expected to behave as a grid");

            t.IsFalse(element.TryGetCurrentPattern(TablePattern.Pattern, out _),
                "nor as a table");
        });

        t.Test("the table view exposes the Grid and Table patterns", () =>
        {
            WithTableGrid(BuildTable(), grid =>
            {
                var element = AutomationElement.FromHandle(grid.Handle);

                t.IsTrue(element.TryGetCurrentPattern(GridPattern.Pattern, out object gridPattern),
                    "a screen reader must be able to see this as a grid");

                t.IsTrue(element.TryGetCurrentPattern(TablePattern.Pattern, out _),
                    "and as a table, which is what carries the headers");

                var g = (GridPattern)gridPattern;

                t.AreEqual(3, g.Current.RowCount, "three data rows");
                t.AreEqual(3, g.Current.ColumnCount, "three columns");
            });
        });

        t.Test("a cell reports its own row and column headers", () =>
        {
            // The decisive one. This is the call a screen reader makes to announce
            // "February, Amount, 412.00" instead of "412.00".
            WithTableGrid(BuildTable(), grid =>
            {
                var element = AutomationElement.FromHandle(grid.Handle);
                var g = (GridPattern)element.GetCurrentPattern(GridPattern.Pattern);

                var cell = g.GetItem(1, 1);

                t.IsTrue(cell.TryGetCurrentPattern(TableItemPattern.Pattern, out object pattern),
                    "a cell must expose the TableItem pattern, or its headers are unreachable");

                var item = (TableItemPattern)pattern;

                var columnHeaders = item.Current.GetColumnHeaderItems();
                t.IsTrue(columnHeaders.Length > 0, "the cell should have a column header");
                t.AreEqual("Amount", columnHeaders[0].Current.Name, "and it should be the document's own");

                var rowHeaders = item.Current.GetRowHeaderItems();
                t.IsTrue(rowHeaders.Length > 0, "the cell should have a row header");
                t.AreEqual("February", rowHeaders[0].Current.Name,
                    "which must be the label, not the row number");
            });
        });

        t.Test("row headers announce their label rather than a row number", () =>
        {
            // The stock row header cell reports its accessible name as "Row 2" whatever value it
            // holds, which would tell the user the one thing they already know. This pins the
            // override that fixes it.
            WithTableGrid(BuildTable(), grid =>
            {
                t.AreEqual("January", grid.Rows[0].HeaderCell.AccessibilityObject.Name,
                    "the first row's header should announce its label");

                t.AreEqual("March", grid.Rows[2].HeaderCell.AccessibilityObject.Name,
                    "and so should the last");
            });
        });

        t.Test("a cell reports its position in the grid", () =>
        {
            WithTableGrid(BuildTable(), grid =>
            {
                var element = AutomationElement.FromHandle(grid.Handle);
                var g = (GridPattern)element.GetCurrentPattern(GridPattern.Pattern);

                var cell = g.GetItem(2, 1);
                var item = (GridItemPattern)cell.GetCurrentPattern(GridItemPattern.Pattern);

                t.AreEqual(2, item.Current.Row, "row index");
                t.AreEqual(1, item.Current.Column, "column index");
            });
        });
    }

    #endregion

    #region What the grid contains

    private static void RegisterContent(TestRunner t)
    {
        t.Group("table view — contents");

        t.Test("the header row supplies column names and is not repeated as data", () =>
        {
            // Shown twice, a reader would announce every heading as though it were a value.
            WithTableGrid(BuildTable(), grid =>
            {
                t.AreEqual("Month", grid.Columns[0].HeaderText, "the column takes its name from the document");
                t.AreEqual(3, grid.Rows.Count, "only the three data rows appear as rows");
                t.AreEqual("January", grid.Rows[0].Cells[0].Value?.ToString(), "starting with the first month");
            });
        });

        t.Test("a table with no headings gets numbered columns rather than invented names", () =>
        {
            // A made-up heading would be announced with every cell as though the document had said
            // it, which is worse than admitting there is none.
            var table = BuildTable(withHeaders: false);

            WithTableGrid(table, grid =>
            {
                t.AreEqual("Column 1", grid.Columns[0].HeaderText, "columns should be numbered");
                t.AreEqual(4, grid.Rows.Count, "and no row is treated as headings");
            });
        });

        t.Test("the grid is read-only", () =>
        {
            // This is a view of a document. A grid that looked editable would promise something the
            // editor cannot deliver.
            WithTableGrid(BuildTable(), grid =>
            {
                t.IsTrue(grid.ReadOnly, "the grid must not be editable");
                t.IsFalse(grid.AllowUserToAddRows, "and must not offer to add rows");
            });
        });

        t.Test("the real sample's table opens as a grid with its headers", () =>
        {
            string sample = FindSample();

            if (sample.Length == 0)
                return;

            var result = new PdfPigDocumentLoader().Load(sample);
            var table = result.Document?.Tables.FirstOrDefault();

            if (table is null)
                return;

            WithTableGrid(table, grid =>
            {
                t.AreEqual("Month", grid.Columns[0].HeaderText, "from a document with no tags at all");
                t.AreEqual(3, grid.Rows.Count, "three months");

                var element = AutomationElement.FromHandle(grid.Handle);
                t.IsTrue(element.TryGetCurrentPattern(TablePattern.Pattern, out _),
                    "and it is a real table to assistive technology");
            });
        });
    }

    #endregion

    #region Harness

    /// <summary>
    /// Builds the dialog, shows it off-screen long enough for its accessibility tree to exist, and
    /// hands the grid to a check. A WinForms control has no automation tree until its handle is
    /// created, so there is no way to test this without actually realising the window.
    /// </summary>
    private static void WithTableGrid(TableElement table, Action<DataGridView> check)
    {
        using var dialog = new TableViewDialog(
            new NullSpeechService(), new SilentAudioCueService(), table);

        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new Point(-4000, -4000);
        dialog.ShowInTaskbar = false;

        Exception? failure = null;

        void OnShown(object? sender, EventArgs e)
        {
            try
            {
                var grid = FindGrid(dialog)
                    ?? throw new AssertionException("the dialog has no grid in it");

                check(grid);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                dialog.Close();
            }
        }

        dialog.Shown += OnShown;
        dialog.ShowDialog();

        if (failure is not null)
            throw failure;
    }

    private static DataGridView? FindGrid(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is DataGridView grid)
                return grid;

            if (FindGrid(child) is { } nested)
                return nested;
        }

        return null;
    }

    private static TableElement BuildTable(bool withHeaders = true)
    {
        var table = new TableElement(1);

        var head = new TableRowElement(1);
        var headRole = withHeaders ? TableCellRole.ColumnHeader : TableCellRole.Data;
        head.AddChild(new TableCellElement(1, "Month", headRole));
        head.AddChild(new TableCellElement(1, "Amount", headRole));
        head.AddChild(new TableCellElement(1, "Status", headRole));
        table.AddChild(head);

        var rowRole = withHeaders ? TableCellRole.RowHeader : TableCellRole.Data;

        foreach (var (month, amount, status) in new[]
                 {
                     ("January", "412.00", "Paid"),
                     ("February", "412.00", "Paid"),
                     ("March", "398.50", "Pending"),
                 })
        {
            var row = new TableRowElement(1);
            row.AddChild(new TableCellElement(1, month, rowRole));
            row.AddChild(new TableCellElement(1, amount));
            row.AddChild(new TableCellElement(1, status));
            table.AddChild(row);
        }

        return table;
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
