using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;

namespace AccessiblePdfEditor.UI;

// =====================================================================================
//  TableViewDialog.cs
//
//  A REAL table, in a control the screen reader recognises as one.
//
//  WHY THIS EXISTS. The document is shown as one continuous read-only text box, which is
//  what gives the user Say All over the whole document, the review cursor, braille tracking
//  and find-in-text. A table flattened into that text box is text: however carefully it is
//  laid out, a screen reader sees characters, and its own table commands — which announce a
//  cell together with its row and column headings — have nothing to work on.
//
//  Verified by inspecting the UI Automation tree, which is the substrate every screen
//  reader reads:
//
//     a read-only TextBox      ControlType.Edit    patterns: Text, Value, Scroll
//     a DataGridView           ControlType.DataGrid patterns: Grid, TABLE
//     a cell within it         ControlType.DataItem patterns: GridItem, TABLEITEM, Value
//
//  TableItem is the one that matters: it is how a reader asks "what are this cell's row and
//  column headers", and NVDA does read exactly those properties
//  (UIA_TableItemRowHeaderItemsPropertyId / ...ColumnHeaderItemsPropertyId).
//
//  WHAT THIS DOES NOT GIVE YOU, stated plainly because it would be easy to assume otherwise:
//  NVDA's Ctrl+Alt+arrow TABLE NAVIGATION commands do not work here. Those live in NVDA's
//  DocumentWithTableNavigation mixin, whose cell lookup must return a textInfos.TextInfo —
//  they are a feature of text DOCUMENTS (browse mode, Word, Google Docs), not of controls.
//  A grid is a control, so you move through it with the plain arrow keys, which is how every
//  grid in Windows works.
//
//  And community reports are mixed on whether NVDA speaks the HEADER on each arrow press in
//  a native grid, as opposed to the cell coordinates; JAWS is reported to be more consistent
//  about it. That could not be verified here — no screen reader is installed — so the dialog
//  provides its own on-demand "read this cell with its headings" command rather than
//  assuming, and says nothing it cannot back up.
//
//  WHY A SEPARATE WINDOW rather than grids embedded in the document. Replacing the text box
//  with a mixed surface of text blocks and grids would gain native table commands and lose
//  Say All across the document, review-cursor continuity and find-in-text — capabilities
//  the primary user has today and would immediately notice going. So the document keeps its
//  text surface, and a table opens into a real grid when the user wants to explore it. The
//  same shape as the page picture: additive, on request, and nothing else is compromised.
// =====================================================================================

#region TableViewDialog

/// <summary>Shows a table in a real grid control, with proper table semantics for screen readers.</summary>
public sealed class TableViewDialog : AccessibleFormBase
{
    #region State

    private readonly TableElement _table;
    private readonly int _initialRow;
    private readonly int _initialColumn;

    private DataGridView _grid = null!;

    /// <summary>True when the user asked for the first row to be marked as headings.</summary>
    public bool MarkFirstRowAsHeadings { get; private set; }

    public TableViewDialog(
        ISpeechService speech,
        IAudioCueService cues,
        TableElement table,
        int initialRow = 0,
        int initialColumn = 0)
        : base(speech, cues)
    {
        _table = table;
        _initialRow = initialRow;
        _initialColumn = initialColumn;

        Size = new Size(820, 520);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(420, 260);
    }

    #endregion

    #region Identity

    protected override string WindowTitle => "Table";

    protected override string WindowPurpose =>
        "The table in a grid, where your screen reader's own table commands work.";

    protected override string BuildOpeningAnnouncement()
    {
        var parts = new List<string>(4)
        {
            $"Table, {_table.RowCount} rows, {_table.ColumnCount} columns.",
        };

        if (!_table.HasHeaderCells)
        {
            parts.Add(
                "This table has no headings in the document, so its columns are numbered rather " +
                "than named. Press Control plus H to treat the first row as headings.");
        }

        parts.Add(
            "Arrow keys move between cells. Control plus R reads the current cell with its " +
            "headings. Escape returns to the document.");

        return string.Join(" ", parts);
    }

    #endregion

    #region Layout

    protected override void BuildContent()
    {
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,

            // Read-only throughout. This is a view of a document, and editing a PDF's table
            // contents is not something this editor does; a grid that looked editable would be
            // promising something it cannot deliver.
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToOrderColumns = false,
            EditMode = DataGridViewEditMode.EditProgrammatically,

            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,

            RowHeadersVisible = HasRowHeaders(),
            ColumnHeadersVisible = true,

            AccessibleName = $"Table, {_table.RowCount} rows, {_table.ColumnCount} columns",
            AccessibleRole = AccessibleRole.Table,

            BackgroundColor = SystemColors.Window,
            TabIndex = 0,
        };

        BuildColumns();
        BuildRows();

        var close = CreateButton("&Close", (_, _) => Close(), "Return to the document", tabIndex: 2);

        var markHeadings = CreateButton("Treat first row as &headings", (_, _) => RequestHeadings(),
            "Mark the first row as column headings, so every cell is announced with its heading",
            tabIndex: 1);

        markHeadings.Enabled = !_table.HasHeaderCells;

        Controls.Add(_grid);
        Controls.Add(CreateButtonRow(markHeadings, close));

        SetCancelButton(close);
    }

    private bool HasRowHeaders() =>
        _table.Rows.Any(r => r.Cells.Any(c => c.CellRole == TableCellRole.RowHeader));

    /// <summary>
    /// Builds the columns, taking their names from the document's own header cells.
    ///
    /// A column's header text is what a screen reader announces alongside every value beneath it,
    /// so where the document has no headings the columns are NUMBERED rather than given invented
    /// names. A made-up heading would be announced with every cell as though the document had said
    /// it, which is worse than admitting there is none.
    /// </summary>
    private void BuildColumns()
    {
        var headerRow = _table.Rows.FirstOrDefault(r => r.IsHeaderRow);

        for (int c = 0; c < _table.ColumnCount; c++)
        {
            string name = headerRow?.CellAt(c)?.Text is { Length: > 0 } text
                ? text
                : $"Column {c + 1}";

            var column = new DataGridViewTextBoxColumn
            {
                HeaderText = name,
                Name = $"column{c}",
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            };

            _grid.Columns.Add(column);
        }
    }

    private void BuildRows()
    {
        // The header row supplies the column names and is not repeated as data; showing it twice
        // would have a reader announce every heading as though it were a value.
        var dataRows = _table.Rows.Where(r => !r.IsHeaderRow).ToList();

        foreach (var row in dataRows)
        {
            var values = new object[_table.ColumnCount];

            for (int c = 0; c < _table.ColumnCount; c++)
                values[c] = row.CellAt(c)?.Text ?? string.Empty;

            int index = _grid.Rows.Add(values);

            if (!_grid.RowHeadersVisible)
                continue;

            // Row headers must be a cell type of our own: the stock one reports its accessible name
            // as "Row 2" whatever value it holds, so a reader would announce the row number instead
            // of the label. Verified by inspecting the automation tree.
            var label = row.Cells.FirstOrDefault(c => c.CellRole == TableCellRole.RowHeader)?.Text;

            _grid.Rows[index].HeaderCell = new LabelledRowHeaderCell();
            _grid.Rows[index].HeaderCell.Value = label ?? string.Empty;
        }
    }

    protected override void FocusFirstControl()
    {
        _grid.Focus();

        try
        {
            int row = Math.Clamp(_initialRow, 0, Math.Max(0, _grid.Rows.Count - 1));
            int column = Math.Clamp(_initialColumn, 0, Math.Max(0, _grid.Columns.Count - 1));

            if (_grid.Rows.Count > 0 && _grid.Columns.Count > 0)
                _grid.CurrentCell = _grid.Rows[row].Cells[column];
        }
        catch (ArgumentOutOfRangeException)
        {
            // A table whose model and grid disagree about its shape still opens; the user simply
            // starts at the first cell.
        }
    }

    #endregion

    #region Marking headings

    /// <summary>
    /// Asks for the first row to be treated as headings, which the caller applies as an undoable
    /// edit to the document.
    ///
    /// Offered here because this is the moment the user discovers the table has no headings — they
    /// are standing in it, hearing bare values — and sending them back to a menu to fix it is how
    /// a repair does not get made.
    /// </summary>
    private void RequestHeadings()
    {
        if (_table.HasHeaderCells)
        {
            Play(AudioCue.Boundary);
            Announce("This table already has headings.", AnnouncementPriority.Assertive);
            return;
        }

        MarkFirstRowAsHeadings = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override bool HandleShortcut(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.H:
                RequestHeadings();
                return true;

            case Keys.Control | Keys.R:
                AnnounceCurrentCell();
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Reads the current cell with its headings.
    ///
    /// Provided on demand rather than automatically on every cell move, and that is a deliberate
    /// choice made under uncertainty. NVDA reads the UIA header properties this grid exposes, but
    /// whether it SPEAKS them on each arrow press in a native grid could not be verified here — no
    /// screen reader is installed — and reports differ. Announcing automatically would risk saying
    /// everything twice, which is worse to live with than pressing a key. If it turns out NVDA
    /// stays quiet about headers, making this automatic is a one-line change.
    /// </summary>
    private void AnnounceCurrentCell()
    {
        if (_grid.CurrentCell is not { } cell)
        {
            Play(AudioCue.Boundary);
            Announce("No cell is selected.", AnnouncementPriority.Assertive);
            return;
        }

        var parts = new List<string>(4);

        string? rowHeader = _grid.RowHeadersVisible
            ? _grid.Rows[cell.RowIndex].HeaderCell.Value?.ToString()
            : null;

        if (!string.IsNullOrWhiteSpace(rowHeader))
            parts.Add(rowHeader!);

        string columnHeader = _grid.Columns[cell.ColumnIndex].HeaderText;

        if (!string.IsNullOrWhiteSpace(columnHeader))
            parts.Add(columnHeader);

        string value = cell.Value?.ToString() ?? string.Empty;
        parts.Add(value.Length > 0 ? value : "blank");

        parts.Add($"row {cell.RowIndex + 1} of {_grid.Rows.Count}, " +
                  $"column {cell.ColumnIndex + 1} of {_grid.Columns.Count}");

        Speech.BeginNewAnnouncement();
        Announce(string.Join(", ", parts), AnnouncementPriority.Assertive);
    }

    protected override string BuildKeyHelp() =>
        "Arrow keys move between cells, as in any grid in Windows. Home and End go to the first and " +
        "last cell of a row, Control plus Home and Control plus End to the start and end of the " +
        "table. " +
        "Control plus R reads the cell you are on together with its headings. " +
        "Control plus H treats the first row as headings. " +
        "Escape returns to the document. " +
        "Note that your screen reader's Control plus Alt plus arrow table commands are a feature of " +
        "documents rather than of grids, so they do not apply here; the ordinary arrow keys are how " +
        "you move through this.";

    #endregion

    #region A row header that announces its label
    // The stock DataGridViewRowHeaderCell reports its accessible name as "Row 2" no matter what
    // value it carries — confirmed by reading the automation tree. That means a reader announcing a
    // cell's row header would say the row NUMBER, which is exactly the information the user already
    // has and none of the information they need.

    /// <summary>A row header cell that announces its own value rather than its row number.</summary>
    private sealed class LabelledRowHeaderCell : DataGridViewRowHeaderCell
    {
        protected override AccessibleObject CreateAccessibilityInstance() =>
            new LabelledAccessibleObject(this);

        private sealed class LabelledAccessibleObject(LabelledRowHeaderCell owner)
            : DataGridViewRowHeaderCellAccessibleObject(owner)
        {
            public override string Name
            {
                get
                {
                    string? value = owner.Value?.ToString();
                    return string.IsNullOrWhiteSpace(value) ? base.Name ?? string.Empty : value;
                }
            }

            public override AccessibleRole Role => AccessibleRole.RowHeader;
        }
    }

    #endregion

    #region Convenience

    /// <summary>
    /// Shows a table in a grid. Returns true when the user asked for the first row to be treated as
    /// headings.
    /// </summary>
    public static bool Show(
        IWin32Window owner,
        ISpeechService speech,
        IAudioCueService cues,
        TableElement table,
        int initialRow = 0,
        int initialColumn = 0)
    {
        using var dialog = new TableViewDialog(speech, cues, table, initialRow, initialColumn);
        dialog.ShowDialog(owner);

        return dialog.MarkFirstRowAsHeadings;
    }

    #endregion
}

#endregion
