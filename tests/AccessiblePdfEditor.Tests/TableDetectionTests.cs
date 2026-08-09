using AccessiblePdfEditor.Ingestion;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Persistence;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace AccessiblePdfEditor.Tests;

// =====================================================================================
//  TableDetectionTests.cs
//
//  Tests for inferring tables from page layout.
//
//  Every test here builds a REAL PDF and reads it back through the real extractor. Table
//  detection is entirely about geometry — where words sit, how far apart, how consistently
//  aligned — and a synthetic fixture would be testing my idea of the geometry rather than
//  the geometry a PDF actually produces.
//
//  Half of these are NEGATIVE tests, deliberately. A missed table reads as paragraphs,
//  which is what happened before this existed. An invented table is a confident
//  announcement of a structure that is not there — headers to look for, relationships to
//  follow — and the listener has no way to discover it is fiction. The negative cases were
//  written after scanning real technical manuals, where lists with hanging indents turned
//  out to be the dominant false positive.
// =====================================================================================

internal static class TableDetectionTests
{
    public static void Register(TestRunner t)
    {
        RegisterPositive(t);
        RegisterNegative(t);
        RegisterAnnouncement(t);
    }

    #region Tables that should be found

    private static void RegisterPositive(TestRunner t)
    {
        t.Group("table detection — found");

        t.Test("a simple grid is detected", () =>
        {
            var document = LoadDrawn(gfx =>
            {
                DrawGrid(gfx, 120,
                    ["Month", "Amount", "Status"],
                    ["January", "412.00", "Paid"],
                    ["February", "412.00", "Paid"],
                    ["March", "398.50", "Pending"]);
            });

            t.AreEqual(1, document.Tables.Count, "the grid should be found");

            var table = document.Tables[0];
            t.AreEqual(4, table.RowCount, "four rows");
            t.AreEqual(3, table.ColumnCount, "three columns");
        });

        t.Test("cells read across rows, not down columns", () =>
        {
            // The entire reason this feature exists. Without detection the columns are extracted
            // separately and a listener hears every month, then every amount, with no way to pair
            // them up.
            var document = LoadDrawn(gfx =>
            {
                DrawGrid(gfx, 120,
                    ["Month", "Amount", "Status"],
                    ["January", "412.00", "Paid"],
                    ["February", "500.00", "Paid"],
                    ["March", "398.50", "Pending"]);
            });

            var row = document.Tables[0].Rows[1];

            t.AreEqual("January", row.Cells[0].Text, "the row should start with its month");
            t.AreEqual("412.00", row.Cells[1].Text, "and carry that month's own amount");
        });

        t.Test("a header row is recognised from words over numbers", () =>
        {
            var document = LoadDrawn(gfx =>
            {
                DrawGrid(gfx, 120,
                    ["Month", "Amount", "Status"],
                    ["January", "412.00", "Paid"],
                    ["February", "412.00", "Paid"],
                    ["March", "398.50", "Pending"]);
            });

            var table = document.Tables[0];

            t.IsTrue(table.HasHeaderCells, "the first row should be recognised as headers");
            t.AreEqual(TableCellRole.ColumnHeader, table.Rows[0].Cells[1].CellRole,
                "the Amount heading should be a column header");
        });

        t.Test("a label column is recognised as row headers", () =>
        {
            // What turns a bare figure into a fact: without it a cell says "Amount, 412" with no
            // indication of which month.
            var document = LoadDrawn(gfx =>
            {
                DrawGrid(gfx, 120,
                    ["Month", "Amount", "Status"],
                    ["January", "412.00", "Paid"],
                    ["February", "412.00", "Paid"],
                    ["March", "398.50", "Pending"]);
            });

            t.AreEqual(TableCellRole.RowHeader, document.Tables[0].Rows[1].Cells[0].CellRole,
                "the month should be a row header");
        });

        t.Test("a table of only words has no row headers invented for it", () =>
        {
            // Labels only mean something beside values. In a grid of words throughout, picking one
            // column as headings would be arbitrary, and a wrong header is worse than none.
            var document = LoadDrawn(gfx =>
            {
                DrawGrid(gfx, 120,
                    ["Colour", "Shape", "Texture"],
                    ["Red", "Round", "Smooth"],
                    ["Blue", "Square", "Rough"],
                    ["Green", "Oval", "Ridged"]);
            });

            if (document.Tables.Count == 0)
                return;

            t.IsFalse(document.Tables[0].Rows[1].Cells[0].CellRole == TableCellRole.RowHeader,
                "no column should be picked as row headings");
        });

        t.Test("the table in the sample document is found", () =>
        {
            string sample = FindSample();

            if (sample.Length == 0)
                return;

            var result = new PdfPigDocumentLoader().Load(sample);
            t.IsTrue(result.IsSuccess, "the sample should load");

            var table = result.Document!.Tables.FirstOrDefault();

            t.IsNotNull(table, "the sample's payments table should be found");
            t.AreEqual(4, table!.RowCount, "one header row and three months");
            t.IsTrue(table.HasHeaderCells, "and its headers should be recognised");
        });
    }

    #endregion

    #region Things that must NOT be called tables

    private static void RegisterNegative(TestRunner t)
    {
        t.Group("table detection — correctly rejected");

        t.Test("a numbered list is not a table", () =>
        {
            // Found in real manuals. A numbered list aligns perfectly: the number in one column,
            // the text in another, down the whole page.
            var document = LoadDrawn(gfx =>
            {
                var body = new XFont("Arial", 11);
                double y = 120;

                foreach (string text in new[]
                         {
                             "Make sure there is no profile already loaded.",
                             "Open the settings file in a text editor.",
                             "Add the section name at the end of the file.",
                             "Save the file and restart the program.",
                         })
                {
                    gfx.DrawString($"{y / 22:0}.", body, XBrushes.Black, new XPoint(60, y));
                    gfx.DrawString(text, body, XBrushes.Black, new XPoint(90, y));
                    y += 22;
                }
            });

            t.AreEqual(0, document.Tables.Count, "a numbered list must not be announced as a table");
        });

        t.Test("a bulleted list is not a table", () =>
        {
            var document = LoadDrawn(gfx =>
            {
                var body = new XFont("Arial", 11);
                double y = 120;

                foreach (string text in new[]
                         {
                             "Handle: from an external run request.",
                             "Handle: from the internal scheduler.",
                             "Handle: from a keyboard shortcut.",
                             "Handle: from a button press.",
                         })
                {
                    gfx.DrawString("*", body, XBrushes.Black, new XPoint(60, y));
                    gfx.DrawString(text, body, XBrushes.Black, new XPoint(80, y));
                    y += 22;
                }
            });

            t.AreEqual(0, document.Tables.Count, "a bulleted list must not be announced as a table");
        });

        t.Test("a definition list is not a table", () =>
        {
            // A term beside its explanation. A real structure, but not a grid — announcing it as
            // one sends the listener looking for headers that do not exist.
            var document = LoadDrawn(gfx =>
            {
                var body = new XFont("Arial", 11);
                double y = 120;

                (string Term, string Meaning)[] entries =
                [
                    ("LuaKill", "to forcibly terminate a running plug-in by name"),
                    ("LuaSet", "to set a flag which a waiting plug-in can test for"),
                    ("LuaClear", "to clear a flag previously set by another plug-in"),
                    ("LuaToggle", "to invert the current state of the named flag"),
                ];

                foreach (var (term, meaning) in entries)
                {
                    gfx.DrawString(term, body, XBrushes.Black, new XPoint(60, y));
                    gfx.DrawString(meaning, body, XBrushes.Black, new XPoint(160, y));
                    y += 22;
                }
            });

            t.AreEqual(0, document.Tables.Count,
                "a term-and-description list must not be announced as a table");
        });

        t.Test("ordinary paragraphs are not a table", () =>
        {
            var document = LoadDrawn(gfx =>
            {
                var body = new XFont("Arial", 11);
                double y = 120;

                foreach (string line in new[]
                         {
                             "This section explains how your application will be assessed",
                             "and what happens next. We will write to you within fifteen",
                             "working days of receiving your completed form. If anything",
                             "changes before then, you must tell us straight away.",
                         })
                {
                    gfx.DrawString(line, body, XBrushes.Black, new XPoint(60, y));
                    y += 22;
                }
            });

            t.AreEqual(0, document.Tables.Count, "wrapped prose must not be announced as a table");
        });

        t.Test("two aligned lines are not enough for a table", () =>
        {
            // A label and its value sit on two aligned lines constantly. Calling that a table would
            // find one on every form in existence.
            var document = LoadDrawn(gfx =>
            {
                var body = new XFont("Arial", 11);
                gfx.DrawString("Name", body, XBrushes.Black, new XPoint(60, 120));
                gfx.DrawString("Thomas", body, XBrushes.Black, new XPoint(200, 120));
                gfx.DrawString("Date", body, XBrushes.Black, new XPoint(60, 142));
                gfx.DrawString("2026", body, XBrushes.Black, new XPoint(200, 142));
            });

            t.AreEqual(0, document.Tables.Count, "two rows is not a table");
        });

        t.Test("a paragraph after a table is not swallowed into it", () =>
        {
            // Caught on the sample: the sentence beneath the table began at the left column and had
            // something out to the right, so it looked like one more row while actually flowing
            // straight through the column boundary.
            var document = LoadDrawn(gfx =>
            {
                DrawGrid(gfx, 120,
                    ["Month", "Amount", "Status"],
                    ["January", "412.00", "Paid"],
                    ["February", "412.00", "Paid"],
                    ["March", "398.50", "Pending"]);

                var body = new XFont("Arial", 11);
                gfx.DrawString("For more information about how we use your data see the notice.",
                    body, XBrushes.Black, new XPoint(60, 120 + 4 * 22 + 8));
            });

            t.AreEqual(1, document.Tables.Count, "the table should still be found");
            t.AreEqual(4, document.Tables[0].RowCount,
                "and the sentence beneath it must not become a fifth row");
        });
    }

    #endregion

    #region What a detected table announces

    private static void RegisterAnnouncement(TestRunner t)
    {
        t.Group("table detection — announcements");

        t.Test("a data cell is announced with both its headers", () =>
        {
            // The payoff. A sighted reader gets this from the shape of the grid; a listener gets it
            // only if the program says it.
            var document = LoadDrawn(gfx =>
            {
                DrawGrid(gfx, 120,
                    ["Month", "Amount", "Status"],
                    ["January", "412.00", "Paid"],
                    ["February", "500.00", "Paid"],
                    ["March", "398.50", "Pending"]);
            });

            string spoken = document.Tables[0].Rows[1].Cells[1].Describe(VerbosityLevel.Normal);

            t.Says(spoken, "January");
            t.Says(spoken, "Amount");
            t.Says(spoken, "412.00");
        });

        t.Test("a detected table is reachable by table navigation", () =>
        {
            var document = LoadDrawn(gfx =>
            {
                DrawGrid(gfx, 120,
                    ["Month", "Amount", "Status"],
                    ["January", "412.00", "Paid"],
                    ["February", "500.00", "Paid"],
                    ["March", "398.50", "Pending"]);
            });

            var navigation = new AccessiblePdfEditor.Navigation.NavigationService();
            navigation.Attach(document);

            var result = navigation.Move(NavigationGranularity.Table, MoveDirection.First);

            t.IsTrue(result.Moved, "pressing T should find the table");
            t.Says(result.Announcement, "table");
        });

        t.Test("a detected table with no headers is reported by the checker", () =>
        {
            // The rule could never fire on an untagged document before, because no table was ever
            // detected in one.
            var document = LoadDrawn(gfx =>
            {
                DrawGrid(gfx, 120,
                    ["Alpha", "Bravo", "Charlie"],
                    ["Delta", "Echo", "Foxtrot"],
                    ["Golf", "Hotel", "India"],
                    ["Juliett", "Kilo", "Lima"]);
            });

            if (document.Tables.Count == 0 || document.Tables[0].HasHeaderCells)
                return;

            var report = new AccessiblePdfEditor.Auditing.AccessibilityAuditor().Audit(document);

            t.IsNotNull(report.Issues.FirstOrDefault(i => i.RuleName == "tables without headers"),
                "a detected table with no headers should now be reported");
        });
    }

    #endregion

    #region Building and loading real PDFs

    /// <summary>Draws a grid of evenly spaced, left-aligned columns.</summary>
    private static void DrawGrid(XGraphics gfx, double top, params string[][] rows)
    {
        var body = new XFont("Arial", 11);
        double[] columns = [60, 200, 320];
        double y = top;

        foreach (string[] row in rows)
        {
            for (int i = 0; i < row.Length && i < columns.Length; i++)
                gfx.DrawString(row[i], body, XBrushes.Black, new XPoint(columns[i], y));

            y += 22;
        }
    }

    /// <summary>
    /// Draws a page, saves it as a real PDF, loads it back through the real extractor, and returns
    /// the model. Round-tripping through an actual file is the point: this feature reads geometry,
    /// and only a real PDF has the geometry a real PDF has.
    /// </summary>
    private static PdfDocumentModel LoadDrawn(Action<XGraphics> draw)
    {
        PdfSharpEnvironment.Initialise();

        string path = Path.Combine(Path.GetTempPath(), $"apde-table-{Guid.NewGuid():N}.pdf");

        try
        {
            using (var document = new PdfDocument())
            {
                var page = document.AddPage();
                page.Width = XUnit.FromPoint(595);
                page.Height = XUnit.FromPoint(842);

                using (var gfx = XGraphics.FromPdfPage(page))
                    draw(gfx);

                document.Save(path);
            }

            var result = new PdfPigDocumentLoader().Load(path);

            return result.Document
                ?? throw new AssertionException($"the drawn page did not load: {result.Message}");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
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
