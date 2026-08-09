using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using UglyToad.PdfPig.Content;

namespace AccessiblePdfEditor.Ingestion;

// =====================================================================================
//  TableDetector.cs
//
//  Finds tables in untagged pages, from nothing but where the words sit.
//
//  WHY THIS IS WORTH THE EFFORT. A table is the structure where a listener is at the
//  greatest disadvantage. A sighted reader takes in a grid at a glance and reads a cell
//  together with its headers without moving their eyes. Without detection, an untagged
//  table is extracted as separate column blocks and read DOWN each column — "Month,
//  January, February, March" then "Amount, four hundred and twelve, four hundred and
//  twelve, three hundred and ninety-eight fifty". The numbers arrive detached from the
//  months they belong to, and no amount of careful listening puts them back together.
//
//  HOW IT WORKS. A table with no ruled lines is defined entirely by ALIGNMENT: several
//  consecutive lines whose words begin at the same handful of x positions. That is the
//  whole signal, and it is a strong one, because ordinary prose does not do it.
//
//    1. group words into lines by vertical overlap
//    2. find x positions where words repeatedly start, across several lines
//    3. a run of consecutive lines sharing enough of those positions is a table
//    4. assign each line's words to columns; each column becomes a cell
//    5. decide whether the first row is a header
//
//  BEING WRONG IS EXPENSIVE, so this is deliberately conservative. Announcing "table, 4
//  rows, 3 columns" over something that is really a form or a two-column article would
//  actively mislead someone who cannot check. Every threshold here is set to miss a real
//  table rather than invent one, and the reasoning for each is written down beside it.
// =====================================================================================

#region DetectedTables

/// <summary>What table detection found on a page.</summary>
public sealed class DetectedTables
{
    /// <summary>The tables, in top-to-bottom order.</summary>
    public required IReadOnlyList<TableElement> Tables { get; init; }

    /// <summary>
    /// The words consumed by those tables. The caller must exclude these before segmenting the
    /// rest of the page, or every cell would appear twice: once inside its table and again as a
    /// stray paragraph.
    /// </summary>
    public required IReadOnlySet<Word> ConsumedWords { get; init; }

    /// <summary>Nothing found.</summary>
    public static DetectedTables None { get; } = new()
    {
        Tables = [],
        ConsumedWords = new HashSet<Word>(),
    };
}

#endregion

#region TableDetector

/// <summary>Finds tables on a page from the alignment of its words.</summary>
public static class TableDetector
{
    #region Thresholds
    // Every one of these exists to avoid a false positive. A missed table is a table that reads as
    // paragraphs, which is what happens today; an invented table is a confident announcement of a
    // structure that is not there, which is worse.

    /// <summary>
    /// Fewest lines that can form a table. Three, not two: a label and its value sit on two
    /// aligned lines constantly, and calling every such pair a table would find one on every form.
    /// </summary>
    private const int MinimumRows = 3;

    /// <summary>Fewest columns. Two is the minimum for a grid to carry any relational meaning.</summary>
    private const int MinimumColumns = 2;

    /// <summary>
    /// How close two x positions must be to count as the same column, in points. About the width
    /// of a space at normal body size — tight enough to distinguish real columns, loose enough to
    /// absorb the sub-point drift in extracted coordinates.
    /// </summary>
    private const double ColumnTolerance = 4.0;

    /// <summary>
    /// What fraction of a candidate's lines must start a word at a column position for that
    /// position to count as a real column. Below three quarters it is coincidence.
    /// </summary>
    private const double ColumnAgreement = 0.75;

    /// <summary>
    /// Longest average cell text before a candidate is rejected as prose. A two-column article
    /// aligns beautifully and is not a table; cells are short, sentences are not.
    /// </summary>
    private const int MaximumAverageCellLength = 55;

    /// <summary>
    /// How much the gap between lines may vary before they stop looking like table rows. Rows of a
    /// table are evenly spaced; unrelated aligned lines are not.
    /// </summary>
    private const double MaximumLineGapVariation = 2.2;

    #endregion

    #region Entry point

    /// <summary>Finds the tables on a page.</summary>
    public static DetectedTables Detect(IReadOnlyList<Word> words, ExtractionContext context)
    {
        if (words.Count < 6)
            return DetectedTables.None;

        try
        {
            var lines = BuildLines(words);

            if (lines.Count < MinimumRows)
                return DetectedTables.None;

            var tables = new List<TableElement>();
            var consumed = new HashSet<Word>();

            int start = 0;

            while (start <= lines.Count - MinimumRows)
            {
                var candidate = GrowCandidate(lines, start);

                if (candidate is null)
                {
                    start++;
                    continue;
                }

                var (rows, columns, end) = candidate.Value;
                var table = BuildTable(lines.GetRange(start, rows), columns, context);

                if (table is not null)
                {
                    tables.Add(table);

                    for (int i = start; i < end; i++)
                    {
                        foreach (var word in lines[i].Words)
                            consumed.Add(word);
                    }

                    start = end;
                }
                else
                {
                    start++;
                }
            }

            return tables.Count == 0
                ? DetectedTables.None
                : new DetectedTables { Tables = tables, ConsumedWords = consumed };
        }
        catch (Exception ex)
        {
            // A page whose geometry defeats the detector reads as paragraphs, which is what it did
            // before this existed. Never a reason to lose the page.
            context.Warnings.Add(
                $"Page {context.PageNumber}: tables could not be worked out from the layout ({ex.Message}).");

            return DetectedTables.None;
        }
    }

    #endregion

    #region Grouping words into lines

    private sealed class TextLine
    {
        public required List<Word> Words { get; init; }
        public required double Top { get; init; }
        public required double Bottom { get; init; }

        public double CentreY => (Top + Bottom) / 2;
    }

    /// <summary>
    /// Groups words into lines by vertical overlap rather than by exact position, because glyphs on
    /// one visual line rarely share an exact baseline once superscripts, differing fonts and
    /// rounding are involved.
    /// </summary>
    private static List<TextLine> BuildLines(IReadOnlyList<Word> words)
    {
        var ordered = words
            .Where(w => w.Text.Trim().Length > 0)
            .OrderByDescending(w => w.BoundingBox.Top)
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();

        var lines = new List<TextLine>();
        var current = new List<Word>();
        double top = 0, bottom = 0;

        foreach (var word in ordered)
        {
            var box = word.BoundingBox;

            if (current.Count == 0)
            {
                current.Add(word);
                top = box.Top;
                bottom = box.Bottom;
                continue;
            }

            double overlap = Math.Min(top, box.Top) - Math.Max(bottom, box.Bottom);
            double shorter = Math.Min(top - bottom, box.Top - box.Bottom);

            if (shorter > 0 && overlap > shorter * 0.5)
            {
                current.Add(word);
                top = Math.Max(top, box.Top);
                bottom = Math.Min(bottom, box.Bottom);
                continue;
            }

            lines.Add(new TextLine
            {
                Words = current.OrderBy(w => w.BoundingBox.Left).ToList(),
                Top = top,
                Bottom = bottom,
            });

            current = [word];
            top = box.Top;
            bottom = box.Bottom;
        }

        if (current.Count > 0)
        {
            lines.Add(new TextLine
            {
                Words = current.OrderBy(w => w.BoundingBox.Left).ToList(),
                Top = top,
                Bottom = bottom,
            });
        }

        return lines;
    }

    #endregion

    #region Growing a candidate
    // Starting from one line, extend downwards for as long as the lines keep agreeing on where
    // their columns are. The moment they stop agreeing, the table has ended.

    /// <summary>
    /// Grows a table candidate from a starting line. Returns the row count, the column positions
    /// and the index one past the last row, or null when no table starts here.
    /// </summary>
    private static (int Rows, List<double> Columns, int End)? GrowCandidate(
        List<TextLine> lines, int start)
    {
        // A line with only one word cannot establish columns, so it cannot start a table.
        if (lines[start].Words.Count < MinimumColumns)
            return null;

        int end = start + 1;
        List<double>? best = null;
        int bestEnd = start;

        while (end < lines.Count)
        {
            if (!LinesAreNeighbours(lines, end))
                break;

            var window = lines.GetRange(start, end - start + 1);
            var columns = FindColumns(window);

            if (columns.Count < MinimumColumns)
            {
                // Adding this line destroyed the alignment, so the table ended before it.
                break;
            }

            // The line must also SIT INSIDE the columns, not merely start words near them. A
            // sentence running the width of the page happens to begin at the left column and, if
            // anything sits out to the right of it, appears to reach a second column too — while
            // actually flowing straight through the boundary between them. Without this, the
            // paragraph after a table gets swallowed as one more row.
            if (!RowRespectsColumns(lines[end], columns))
                break;

            if (window.Count >= MinimumRows)
            {
                best = columns;
                bestEnd = end + 1;
            }

            end++;
        }

        return best is null ? null : (bestEnd - start, best, bestEnd);
    }

    /// <summary>
    /// Whether a line's content stays within its columns.
    ///
    /// The structural difference between a table row and a sentence. A cell's text sits inside its
    /// column; a sentence crosses every boundary on the way past. Checking this is what stops the
    /// paragraph beneath a table being read as part of it.
    /// </summary>
    private static bool RowRespectsColumns(TextLine line, List<double> columns)
    {
        for (int c = 0; c + 1 < columns.Count; c++)
        {
            double from = columns[c] - ColumnTolerance;
            double boundary = columns[c + 1] - ColumnTolerance;

            foreach (var word in line.Words)
            {
                var box = word.BoundingBox;

                if (box.Left < from || box.Left >= boundary)
                    continue;

                // A word that begins inside this column and ends past the next column's start has
                // flowed through the boundary, so this line is not laid out in these columns.
                if (box.Right > boundary + ColumnTolerance)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether two lines are close enough vertically to be rows of the same table. Two lines
    /// separated by a large gap are separate structures however well they happen to align.
    /// </summary>
    private static bool LinesAreNeighbours(List<TextLine> lines, int index)
    {
        double height = Math.Max(1, lines[index].Top - lines[index].Bottom);
        double gap = lines[index - 1].Bottom - lines[index].Top;

        // A negative gap means the lines overlap, which is fine. A gap of more than about three
        // line heights is a new block of content.
        return gap < height * 3.0;
    }

    #endregion

    #region Finding columns

    /// <summary>
    /// Finds the x positions where words repeatedly start across a set of lines. These are the
    /// column boundaries, and their existence is the entire evidence that this is a table.
    /// </summary>
    private static List<double> FindColumns(List<TextLine> lines)
    {
        var starts = new List<double>();

        foreach (var line in lines)
        {
            foreach (double x in WordGroupStarts(line))
                starts.Add(x);
        }

        if (starts.Count == 0)
            return [];

        starts.Sort();

        // Cluster the positions, then keep the clusters that most lines agree on.
        var clusters = new List<List<double>>();
        var current = new List<double> { starts[0] };

        for (int i = 1; i < starts.Count; i++)
        {
            if (starts[i] - current[^1] <= ColumnTolerance)
            {
                current.Add(starts[i]);
            }
            else
            {
                clusters.Add(current);
                current = [starts[i]];
            }
        }

        clusters.Add(current);

        int required = Math.Max(2, (int)Math.Ceiling(lines.Count * ColumnAgreement));
        var columns = new List<double>();

        foreach (var cluster in clusters)
        {
            // Counted per LINE, not per position: a line whose two words both fall in one cluster
            // still only agrees once, which stops a single ragged line inventing a column.
            int agreeing = lines.Count(line =>
                WordGroupStarts(line).Any(x => Math.Abs(x - cluster.Average()) <= ColumnTolerance));

            if (agreeing >= required)
                columns.Add(cluster.Average());
        }

        columns.Sort();
        return columns;
    }

    /// <summary>
    /// Where each run of words on a line begins. Words separated by an ordinary space belong to the
    /// same cell; a wide gap means a new column, and that gap is what the whole detector reads.
    /// </summary>
    private static IEnumerable<double> WordGroupStarts(TextLine line)
    {
        if (line.Words.Count == 0)
            yield break;

        yield return line.Words[0].BoundingBox.Left;

        for (int i = 1; i < line.Words.Count; i++)
        {
            double gap = line.Words[i].BoundingBox.Left - line.Words[i - 1].BoundingBox.Right;
            double height = Math.Max(1, line.Top - line.Bottom);

            // A gap wider than roughly one character is a column boundary rather than a word space.
            if (gap > height * 0.9)
                yield return line.Words[i].BoundingBox.Left;
        }
    }

    #endregion

    #region Building the table

    /// <summary>
    /// Turns a run of aligned lines into a table, or returns null if it fails the sanity checks.
    /// </summary>
    private static TableElement? BuildTable(
        IReadOnlyList<TextLine> rows, List<double> columns, ExtractionContext context)
    {
        if (rows.Count < MinimumRows || columns.Count < MinimumColumns)
            return null;

        var cellText = new List<List<string>>(rows.Count);
        var cellBold = new List<List<bool>>(rows.Count);

        foreach (var row in rows)
        {
            var texts = new List<string>(columns.Count);
            var bolds = new List<bool>(columns.Count);

            for (int c = 0; c < columns.Count; c++)
            {
                double from = columns[c] - ColumnTolerance;
                double to = c + 1 < columns.Count ? columns[c + 1] - ColumnTolerance : double.MaxValue;

                var inCell = row.Words
                    .Where(w => w.BoundingBox.Left >= from && w.BoundingBox.Left < to)
                    .ToList();

                texts.Add(string.Join(" ", inCell.Select(w => w.Text)).Trim());
                bolds.Add(inCell.Count > 0 && inCell.All(IsBold));
            }

            cellText.Add(texts);
            cellBold.Add(bolds);
        }

        if (!LooksLikeATable(cellText))
            return null;

        var table = new TableElement(context.PageNumber);

        bool headerRow = FirstRowLooksLikeHeaders(cellText, cellBold);
        bool headerColumn = FirstColumnLooksLikeHeaders(cellText, headerRow);

        for (int r = 0; r < rows.Count; r++)
        {
            var rowElement = new TableRowElement(context.PageNumber)
            {
                Bounds = new PageRegion(columns[0], rows[r].Bottom,
                    rows[r].Words.Count > 0 ? rows[r].Words[^1].BoundingBox.Right : columns[^1],
                    rows[r].Top),
            };

            for (int c = 0; c < columns.Count; c++)
            {
                // A cell at the intersection stays a column header: it labels the column of row
                // labels beneath it, which is what a reader needs it to say.
                var role =
                    headerRow && r == 0 ? TableCellRole.ColumnHeader
                    : headerColumn && c == 0 ? TableCellRole.RowHeader
                    : TableCellRole.Data;

                var cell = new TableCellElement(context.PageNumber, cellText[r][c], role)
                {
                    Bounds = new PageRegion(
                        columns[c], rows[r].Bottom,
                        c + 1 < columns.Count ? columns[c + 1] : rowElement.Bounds.Right,
                        rows[r].Top),
                };

                rowElement.AddChild(cell);
            }

            table.AddChild(rowElement);
        }

        table.RecalculateBoundsFromChildren();
        return table;
    }

    /// <summary>
    /// Whether a two-column candidate is really a list with a hanging indent.
    ///
    /// Found by testing against real technical manuals, where it was the dominant false positive. A
    /// bulleted or numbered list puts its marker in one column and its text in another, aligns
    /// perfectly down the page, and satisfies every geometric test for a table. Announcing "table,
    /// 6 rows, 2 columns" over a list of bullet points would be worse than saying nothing: the
    /// listener would go hunting for headers and relationships that do not exist.
    /// </summary>
    private static bool FirstColumnIsListMarkers(List<List<string>> cells)
    {
        int markers = 0;
        int filled = 0;

        foreach (var row in cells)
        {
            if (row.Count == 0 || row[0].Length == 0)
                continue;

            filled++;

            if (IsListMarker(row[0]))
                markers++;
        }

        // Nearly all of them, not merely most: a genuine table can begin a row with something that
        // reads like a marker, but not row after row.
        return filled >= 2 && markers >= filled - 1;
    }

    private static bool IsListMarker(string text)
    {
        string trimmed = text.Trim();

        if (trimmed.Length == 0 || trimmed.Length > 5)
            return false;

        if (trimmed.Length == 1 && "•·▪▫◦‣⁃-–—*+>".Contains(trimmed[0]))
            return true;

        // "1.", "12)", "(a)", "iv." and the like.
        string body = trimmed.TrimEnd('.', ')', ']', ':').TrimStart('(', '[');

        if (body.Length == 0 || body.Length > 4 || body == trimmed)
            return false;

        return body.All(char.IsDigit)
            || (body.Length <= 2 && body.All(char.IsLetter));
    }

    /// <summary>
    /// The sanity checks that keep prose and forms from being announced as tables.
    /// </summary>
    private static bool LooksLikeATable(List<List<string>> cells)
    {
        int filled = 0;
        int total = 0;
        int totalLength = 0;

        foreach (var row in cells)
        {
            foreach (string text in row)
            {
                total++;

                if (text.Length > 0)
                {
                    filled++;
                    totalLength += text.Length;
                }
            }
        }

        if (filled == 0)
            return false;

        // Long cells mean sentences, and sentences mean this is a column of prose that happens to
        // line up. A real table's cells are short.
        if (totalLength / filled > MaximumAverageCellLength)
            return false;

        // A grid that is mostly empty is not a grid. Half-filled is generous — real tables do have
        // blank cells — but it rules out text that merely aligns in places.
        if ((double)filled / total < 0.5)
            return false;

        // Every row needs content in at least two columns, or the "table" is really one column with
        // occasional strays beside it.
        foreach (var row in cells)
        {
            if (row.Count(text => text.Length > 0) < MinimumColumns)
                return false;
        }

        // Two columns is the weakest evidence a grid can offer, so it is held to a higher standard.
        // Three or more aligned columns is already strong; two is also what a hanging-indent list
        // and a definition list both look like.
        if (cells[0].Count == 2)
        {
            if (FirstColumnIsListMarkers(cells))
                return false;

            for (int c = 0; c < 2; c++)
            {
                var lengths = cells
                    .Where(r => c < r.Count && r[c].Length > 0)
                    .Select(r => r[c].Length)
                    .ToList();

                // A column of sentences beside a column of labels is a description list. Real
                // enough as a structure, but not a table, and calling it one sends the listener
                // looking for headers and relationships that are not there.
                if (lengths.Count > 0 && lengths.Average() > 40)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Decides whether the first row is a header.
    ///
    /// Two signals, because a table's header is marked either typographically or semantically.
    /// Bold is the obvious one. The other is that headers are words while the data beneath them is
    /// numbers — "Amount" over "£412.00" — which catches the many tables whose headers are not bold
    /// at all. Being wrong here is cheap in one direction only: missing a header row leaves the
    /// auditor reporting a table with no headers, which the user can fix in one step.
    /// </summary>
    private static bool FirstRowLooksLikeHeaders(List<List<string>> cells, List<List<bool>> bold)
    {
        if (cells.Count < 2)
            return false;

        var first = cells[0];

        // Typographic: the whole first row bold, and not everything below it.
        bool firstRowBold = bold[0].Where((_, i) => first[i].Length > 0).All(b => b);
        bool restBold = bold.Skip(1).SelectMany(r => r).Any(b => b);

        if (firstRowBold && !restBold)
            return true;

        // Semantic: words on top, numbers underneath.
        int numericColumns = 0;
        int comparableColumns = 0;

        for (int c = 0; c < first.Count; c++)
        {
            if (first[c].Length == 0 || LooksNumeric(first[c]))
                continue;

            var below = cells.Skip(1)
                .Where(r => c < r.Count && r[c].Length > 0)
                .Select(r => r[c])
                .ToList();

            if (below.Count == 0)
                continue;

            comparableColumns++;

            if (below.Count(LooksNumeric) * 2 > below.Count)
                numericColumns++;
        }

        // ONE such column is enough. A table of labels with a single value column — a month against
        // an amount, a name against a total — is the commonest shape there is, and requiring a
        // majority of numeric columns would miss almost all of them.
        //
        // It stays safe because a header cell that is itself numeric is skipped above: a data row
        // that happens to sit at the top cannot qualify, since its own numbers disqualify exactly
        // the columns that would otherwise vote for it.
        return comparableColumns > 0 && numericColumns > 0;
    }

    /// <summary>
    /// Decides whether the first column labels its rows.
    ///
    /// Worth detecting separately from the header row, because it is what turns a bare figure into
    /// a fact. Without it a cell announces "Amount, £412.00" — the right column, but no idea which
    /// month. With it, "January, Amount, £412.00", which is what a sighted reader takes from the
    /// grid at a glance.
    ///
    /// The signature is a column of labels beside columns of values: every entry non-numeric, while
    /// at least one other column is mostly numeric.
    /// </summary>
    private static bool FirstColumnLooksLikeHeaders(List<List<string>> cells, bool hasHeaderRow)
    {
        var dataRows = cells.Skip(hasHeaderRow ? 1 : 0).ToList();

        if (dataRows.Count < 2)
            return false;

        var labels = dataRows
            .Where(r => r.Count > 0 && r[0].Length > 0)
            .Select(r => r[0])
            .ToList();

        // Every label must be a label. One number in the column and it is data, not headings.
        if (labels.Count < 2 || labels.Any(LooksNumeric))
            return false;

        // Labels only mean something if they sit beside values. A grid of words throughout is a
        // list of things, and picking one column of it as headings would be arbitrary.
        for (int c = 1; c < cells[0].Count; c++)
        {
            var below = dataRows
                .Where(r => c < r.Count && r[c].Length > 0)
                .Select(r => r[c])
                .ToList();

            if (below.Count >= 2 && below.Count(LooksNumeric) * 2 > below.Count)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a cell reads as a number. Currency symbols, separators, percent signs and brackets
    /// for negatives are all stripped, because a column of money is a column of numbers however it
    /// is punctuated.
    /// </summary>
    private static bool LooksNumeric(string text)
    {
        int digits = 0;
        int others = 0;

        foreach (char c in text)
        {
            if (char.IsDigit(c))
                digits++;
            else if (!char.IsWhiteSpace(c) && "£$€%.,-+()/:".IndexOf(c) < 0)
                others++;
        }

        return digits > 0 && others == 0;
    }

    private static bool IsBold(Word word)
    {
        foreach (var letter in word.Letters)
        {
            if (letter.FontDetails?.IsBold == true)
                return true;

            if (letter.FontName?.Contains("bold", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }

        return false;
    }

    #endregion
}

#endregion
