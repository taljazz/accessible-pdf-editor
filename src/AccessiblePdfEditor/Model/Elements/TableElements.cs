using System.Text;
using AccessiblePdfEditor.Model;

namespace AccessiblePdfEditor.Model.Elements;

// =====================================================================================
//  TableElements.cs
//
//  Tables, rows and cells.
//
//  Tables are the hardest structure to read by ear and the one where good tagging pays off
//  most. A sighted reader looking at a cell containing "4,200" sees the column heading
//  "Revenue" and the row heading "March" without moving their eyes. A listener gets the
//  bare number unless the program supplies the headers — which it can only do if the table
//  distinguishes header cells from data cells.
//
//  So this file's real work is header resolution: given a cell, find the headers that
//  govern it, and put them in the announcement. Everything else here exists to serve that.
// =====================================================================================

#region TableElement — the container, and the source of header lookups

/// <summary>A table. Contains <see cref="TableRowElement"/> children.</summary>
public sealed class TableElement : DocumentElement
{
    #region Construction and shape

    public TableElement(int pageNumber)
        : base(pageNumber) { }

    public override ElementKind Kind => ElementKind.Table;

    /// <summary>An optional summary of the table's purpose, from the PDF /Summary attribute.</summary>
    public string? Summary { get; set; }

    /// <summary>The table's rows, in order.</summary>
    public IReadOnlyList<TableRowElement> Rows => Children.OfType<TableRowElement>().ToList();

    /// <summary>The number of rows.</summary>
    public int RowCount => Children.Count(c => c.Kind == ElementKind.TableRow);

    /// <summary>
    /// The number of columns, taken as the widest row. Ragged tables are common in extracted
    /// content, and the widest row is the only count that does not under-report the table's size.
    /// </summary>
    public int ColumnCount
    {
        get
        {
            int widest = 0;
            foreach (var row in Children.OfType<TableRowElement>())
                widest = Math.Max(widest, row.CellCount);
            return widest;
        }
    }

    /// <summary>
    /// True when at least one cell is marked as a header. A table without any is the single most
    /// common serious table fault, and the auditor reports it: every cell then reads as a bare
    /// value with nothing to say what it means.
    /// </summary>
    public bool HasHeaderCells =>
        Children.OfType<TableRowElement>()
            .SelectMany(r => r.Cells)
            .Any(c => c.CellRole != TableCellRole.Data);

    #endregion

    #region Header resolution — the reason this class exists
    // Finding the headers for a cell is done positionally: the column header is the header cell in
    // the same column of the nearest header row above, and the row header is the header cell in
    // the same row to the left. This mirrors how the PDF /Scope attribute is meant to work, and it
    // degrades sensibly on tables whose headers were inferred rather than tagged.

    /// <summary>
    /// The header text governing a cell's column, or null. Searches upwards from the cell's row for
    /// the nearest row containing a header in the same column.
    /// </summary>
    public string? FindColumnHeaderFor(TableCellElement cell)
    {
        int columnIndex = cell.ColumnIndex;
        if (columnIndex < 0)
            return null;

        var rows = Rows;

        int cellRowIndex = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], cell.Parent))
            {
                cellRowIndex = i;
                break;
            }
        }

        if (cellRowIndex < 0)
            return null;

        for (int r = cellRowIndex - 1; r >= 0; r--)
        {
            var candidate = rows[r].CellAt(columnIndex);
            if (candidate is null)
                continue;

            if (candidate.CellRole is TableCellRole.ColumnHeader or TableCellRole.Header)
                return candidate.Text;
        }

        return null;
    }

    /// <summary>
    /// The header text governing a cell's row, or null. Searches leftwards along the cell's own row.
    /// </summary>
    public string? FindRowHeaderFor(TableCellElement cell)
    {
        if (cell.Parent is not TableRowElement row)
            return null;

        var cells = row.Cells;
        int index = cell.ColumnIndex;

        for (int c = index - 1; c >= 0 && c < cells.Count; c--)
        {
            if (cells[c].CellRole is TableCellRole.RowHeader or TableCellRole.Header)
                return cells[c].Text;
        }

        return null;
    }

    #endregion

    #region Announcement

    protected override string DescribeRole(VerbosityLevel verbosity) => "table";

    protected override string DescribeContent(VerbosityLevel verbosity)
    {
        var builder = new StringBuilder();
        builder.Append(RowCount).Append(RowCount == 1 ? " row" : " rows");
        builder.Append(", ").Append(ColumnCount).Append(ColumnCount == 1 ? " column" : " columns");

        if (Summary is { Length: > 0 } summary)
            builder.Append(". ").Append(summary);

        return builder.ToString();
    }

    /// <summary>
    /// A table with no header cells is called out immediately rather than only in the audit report.
    /// It tells the listener, at the moment they arrive, that the cell announcements they are about
    /// to hear will be bare values — which is exactly when that warning is useful.
    /// </summary>
    protected override string DescribeState(VerbosityLevel verbosity)
    {
        if (verbosity == VerbosityLevel.Terse || HasHeaderCells)
            return string.Empty;

        return "no header cells, so cells cannot be announced with their headings";
    }

    #endregion
}

#endregion

#region TableRowElement — one row

/// <summary>One row of a table.</summary>
public sealed class TableRowElement : DocumentElement
{
    public TableRowElement(int pageNumber)
        : base(pageNumber) { }

    public override ElementKind Kind => ElementKind.TableRow;

    /// <summary>The cells of this row, in order.</summary>
    public IReadOnlyList<TableCellElement> Cells => Children.OfType<TableCellElement>().ToList();

    /// <summary>The number of cells in this row.</summary>
    public int CellCount => Children.Count(c => c.Kind == ElementKind.TableCell);

    /// <summary>The one-based position of this row in its table.</summary>
    public int RowNumber
    {
        get
        {
            if (Parent is not TableElement table)
                return 0;

            int number = 0;
            foreach (var child in table.Children)
            {
                if (child.Kind != ElementKind.TableRow)
                    continue;

                number++;
                if (ReferenceEquals(child, this))
                    return number;
            }

            return 0;
        }
    }

    /// <summary>
    /// The cell at a column index, accounting for cells that span several columns. Returns null
    /// when the row is short, which is normal in tables recovered from an untagged page.
    /// </summary>
    public TableCellElement? CellAt(int columnIndex)
    {
        int column = 0;
        foreach (var cell in Children.OfType<TableCellElement>())
        {
            if (columnIndex >= column && columnIndex < column + cell.ColumnSpan)
                return cell;

            column += cell.ColumnSpan;
        }

        return null;
    }

    /// <summary>True when every cell in this row is a header cell.</summary>
    public bool IsHeaderRow
    {
        get
        {
            var cells = Cells;
            return cells.Count > 0 && cells.All(c => c.CellRole != TableCellRole.Data);
        }
    }

    protected override string DescribeRole(VerbosityLevel verbosity)
    {
        int number = RowNumber;
        if (number == 0)
            return "row";

        int total = (Parent as TableElement)?.RowCount ?? 0;
        string label = IsHeaderRow ? "header row" : "row";
        return total > 0 ? $"{label} {number} of {total}" : $"{label} {number}";
    }

    protected override string DescribeContent(VerbosityLevel verbosity) =>
        string.Join(", ", Cells.Select(c => c.Text).Where(t => t.Length > 0));
}

#endregion

#region TableCellElement — the cell, which announces itself with its headers
// This is where the file's purpose is realised. A data cell asks its table for the headers that
// govern it and speaks them with its value, so "4,200" becomes "March, Revenue, 4,200" — the same
// information a sighted reader takes from the table's shape.

/// <summary>One cell of a table row.</summary>
public sealed class TableCellElement : DocumentElement
{
    #region Construction and state

    private string _text;

    public TableCellElement(int pageNumber, string text, TableCellRole role = TableCellRole.Data)
        : base(pageNumber)
    {
        _text = text ?? string.Empty;
        CellRole = role;
    }

    public override ElementKind Kind => ElementKind.TableCell;

    public override string Text => ActualText ?? _text;

    /// <summary>Replaces the cell's text. Used by editing commands.</summary>
    public void SetText(string text) => _text = text ?? string.Empty;

    /// <summary>
    /// Whether this is a header or a data cell, and which axis a header governs. Settable because
    /// marking a table's header cells is one of the highest-value repairs the editor offers.
    /// </summary>
    public TableCellRole CellRole { get; internal set; }

    /// <summary>How many columns this cell spans (PDF /ColSpan).</summary>
    public int ColumnSpan { get; init; } = 1;

    /// <summary>How many rows this cell spans (PDF /RowSpan).</summary>
    public int RowSpan { get; init; } = 1;

    /// <summary>
    /// The zero-based column this cell starts in, counting the spans of the cells before it, so a
    /// cell after a two-column span lands in the right column rather than one too far left.
    /// </summary>
    public int ColumnIndex
    {
        get
        {
            if (Parent is not TableRowElement row)
                return -1;

            int column = 0;
            foreach (var cell in row.Children.OfType<TableCellElement>())
            {
                if (ReferenceEquals(cell, this))
                    return column;

                column += cell.ColumnSpan;
            }

            return -1;
        }
    }

    #endregion

    #region Announcement — headers first, then the value
    // Order matters. Headers come before the value because they are the context that makes the
    // value mean anything, and because a listener who already knows the context can stop listening
    // as soon as they hear the number.

    protected override string DescribeRole(VerbosityLevel verbosity)
    {
        if (CellRole != TableCellRole.Data)
            return CellRole switch
            {
                TableCellRole.ColumnHeader => "column header",
                TableCellRole.RowHeader => "row header",
                _ => "header",
            };

        if (verbosity == VerbosityLevel.Terse)
            return string.Empty;

        var table = NearestAncestor<TableElement>();
        var row = Parent as TableRowElement;
        if (table is null || row is null)
            return "cell";

        int column = ColumnIndex + 1;
        return $"row {row.RowNumber}, column {column}";
    }

    protected override string DescribeContent(VerbosityLevel verbosity)
    {
        string value = Text.Length > 0 ? Text : "blank";

        // Header cells are their own context; repeating headers for a header would be circular.
        if (CellRole != TableCellRole.Data || verbosity == VerbosityLevel.Terse)
            return value;

        var table = NearestAncestor<TableElement>();
        if (table is null || !table.HasHeaderCells)
            return value;

        var parts = new List<string>(3);

        string? rowHeader = table.FindRowHeaderFor(this);
        if (rowHeader is { Length: > 0 })
            parts.Add(rowHeader);

        string? columnHeader = table.FindColumnHeaderFor(this);
        if (columnHeader is { Length: > 0 })
            parts.Add(columnHeader);

        parts.Add(value);
        return string.Join(", ", parts);
    }

    /// <summary>
    /// A spanning cell says so. Without it, a listener stepping across a row would lose count of
    /// which column they are in as soon as they crossed a merged cell.
    /// </summary>
    protected override string DescribeState(VerbosityLevel verbosity)
    {
        if (verbosity == VerbosityLevel.Terse)
            return string.Empty;

        var parts = new List<string>(2);
        if (ColumnSpan > 1) parts.Add($"spans {ColumnSpan} columns");
        if (RowSpan > 1) parts.Add($"spans {RowSpan} rows");
        return string.Join(", ", parts);
    }

    #endregion
}

#endregion
