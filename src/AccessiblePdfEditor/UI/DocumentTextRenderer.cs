using System.Text;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Model.Forms;

namespace AccessiblePdfEditor.UI;

// =====================================================================================
//  DocumentTextRenderer.cs
//
//  Flattens a document into one continuous piece of text, and remembers where every
//  element landed in it.
//
//  This exists so that the document can live in a real, read-only Windows text box. That
//  one decision gives the user, for free and without this program writing a line of code
//  for any of it:
//
//    the screen reader's own review cursor
//    Say All, with the user's own speed and voice
//    their braille display, tracking as they read
//    text selection and copy, with the keys they already use
//    find-as-you-type in their screen reader, not just ours
//    their own punctuation and symbol settings
//
//  A self-voicing text surface can provide none of that. Whatever effort went into it, the
//  result would be a worse version of tools the user already has and has already configured
//  to their liking.
//
//  The offset map is what keeps the two halves in step: when structural navigation moves to
//  a heading, the caret is moved to that heading's place in the text, so the screen reader
//  and this program always agree about where the user is.
// =====================================================================================

#region RenderedDocument

/// <summary>A document flattened to text, with the position of every element recorded.</summary>
public sealed class RenderedDocument
{
    private readonly Dictionary<int, (int Start, int Length)> _spansByElementId;
    private readonly List<(int Start, int Length, int ElementId)> _spansInOrder;

    internal RenderedDocument(
        string text,
        Dictionary<int, (int Start, int Length)> spansByElementId,
        List<(int Start, int Length, int ElementId)> spansInOrder)
    {
        Text = text;
        _spansByElementId = spansByElementId;
        _spansInOrder = spansInOrder;
    }

    /// <summary>The whole document as one piece of text.</summary>
    public string Text { get; }

    /// <summary>Where an element's text sits, or null when it produced no text.</summary>
    public (int Start, int Length)? SpanOf(DocumentElement element) =>
        _spansByElementId.TryGetValue(element.Id, out var span) ? span : null;

    /// <summary>
    /// Where to put the caret for an element: its own span, or failing that the first descendant
    /// that has one.
    ///
    /// The fallback is not a nicety. A container — a table, a list, a section — has no text of its
    /// own; its children carry it. Without this, navigating to a table announced the table and left
    /// the caret exactly where it was, so the screen reader's review cursor never moved and the
    /// command appeared to do nothing at all. Landing on the first cell is what the user meant by
    /// "go to the table".
    /// </summary>
    public (int Start, int Length)? CaretTargetFor(DocumentElement element)
    {
        if (_spansByElementId.TryGetValue(element.Id, out var own))
            return own;

        foreach (var descendant in element.Descendants())
        {
            if (_spansByElementId.TryGetValue(descendant.Id, out var span))
                return span;
        }

        return null;
    }

    /// <summary>
    /// The element whose text contains a character position. Used when the user moves the caret
    /// themselves — with their screen reader's review cursor, or by clicking — so that this program
    /// can catch up with where they have gone rather than fighting them for control of the position.
    /// </summary>
    public int? ElementIdAt(int characterPosition)
    {
        // Binary search: this runs on every caret move, and a long document has a lot of spans.
        int low = 0, high = _spansInOrder.Count - 1;

        while (low <= high)
        {
            int middle = (low + high) / 2;
            var (start, length, elementId) = _spansInOrder[middle];

            if (characterPosition < start)
                high = middle - 1;
            else if (characterPosition >= start + length)
                low = middle + 1;
            else
                return elementId;
        }

        // A position in the whitespace between two elements belongs to the one before it, which is
        // what a user moving forwards would expect.
        if (high >= 0 && high < _spansInOrder.Count)
            return _spansInOrder[high].ElementId;

        return null;
    }
}

#endregion

#region DocumentTextRenderer

/// <summary>Flattens a document into text for display, recording where each element lands.</summary>
public static class DocumentTextRenderer
{
    #region Rendering

    /// <summary>
    /// Renders a document to text.
    /// </summary>
    /// <param name="document">The document to render.</param>
    /// <param name="mode">
    /// How to linearise it. Structured skips page furniture and adds role prefixes; Layout follows
    /// the page exactly; Raw adds nothing at all.
    /// </param>
    /// <param name="includeRoleLabels">
    /// Whether to write role names into the text, as in "Heading 2: Introduction".
    ///
    /// On by default, and worth explaining. The screen reader reading this text box has no idea
    /// that a particular line is a heading — it is all just text to it. Writing the role in means
    /// that a user reading with Say All, or reviewing with their braille display, still learns the
    /// document's structure. Without it, the structure would be available only through this
    /// program's own navigation commands, and would vanish the moment the user reached for a tool
    /// of their own.
    /// </param>
    public static RenderedDocument Render(
        PdfDocumentModel document,
        ReadingMode mode = ReadingMode.Structured,
        bool includeRoleLabels = true)
    {
        ArgumentNullException.ThrowIfNull(document);

        var builder = new StringBuilder(4096);
        var byId = new Dictionary<int, (int, int)>();
        var inOrder = new List<(int, int, int)>();
        var alreadyEmitted = new HashSet<int>();

        int lastPage = 0;

        foreach (var element in document.ReadingOrder)
        {
            // Cells are written as part of their row, so they must not be written again on their
            // own account when the walk reaches them.
            if (alreadyEmitted.Contains(element.Id))
                continue;

            if (!ShouldRender(element, mode))
                continue;

            // A page boundary is written into the text so that someone reading straight through
            // hears where one page ends and the next begins, which is otherwise invisible.
            if (element.PageNumber > lastPage && element.PageNumber > 0 && mode != ReadingMode.Raw)
            {
                if (builder.Length > 0)
                    builder.Append("\r\n");

                builder.Append("--- Page ").Append(element.PageNumber).Append(" ---\r\n\r\n");
                lastPage = element.PageNumber;
            }

            // A table row is written as ONE line with its cells side by side, so that reading
            // straight through gives "January, £412.00, Paid" as a row. Written one cell per line,
            // a table becomes a column of disconnected values and the rows disappear — which is
            // most of what made tables unusable by ear in the first place.
            if (element is TableRowElement row && mode != ReadingMode.Raw)
            {
                AppendRowLine(row, builder, byId, inOrder, alreadyEmitted);
                continue;
            }

            string line = BuildLine(element, mode, includeRoleLabels);
            if (line.Length == 0)
                continue;

            if (builder.Length > 0)
                builder.Append("\r\n\r\n");

            int start = builder.Length;
            builder.Append(line);

            byId[element.Id] = (start, line.Length);
            inOrder.Add((start, line.Length, element.Id));
        }

        return new RenderedDocument(builder.ToString(), byId, inOrder);
    }

    #endregion

    #region What gets rendered

    private static bool ShouldRender(DocumentElement element, ReadingMode mode)
    {
        // Most containers contribute nothing themselves; their children carry the text.
        if (element.Kind is ElementKind.Document or ElementKind.Page or ElementKind.Section)
            return false;

        // A row writes itself and its cells together, so it renders in structured and layout modes
        // but not in raw, where nothing is interpreted.
        if (element.Kind is ElementKind.TableRow)
            return mode != ReadingMode.Raw;

        // Tables and lists are the exception, and get a line of their own announcing what is
        // coming. Someone reading straight through with Say All would otherwise meet a run of
        // indented cells with nothing to say a table had started, and no idea how many rows were
        // ahead of them — which is precisely the orientation a sighted reader gets free from the
        // shape of the page.
        if (element.Kind is ElementKind.Table or ElementKind.List)
            return mode != ReadingMode.Raw;

        if (element is ArtifactElement)
            return mode == ReadingMode.Layout;

        // A figure with no text still gets a line, because its description — or the absence of one —
        // is part of the document and hiding it would hide the gap.
        if (element is FigureElement)
            return true;

        if (element is PdfFormField)
            return true;

        return element.FullText.Trim().Length > 0;
    }

    /// <summary>
    /// Builds one element's line, with a role prefix where that carries information the plain text
    /// would lose.
    /// </summary>
    private static string BuildLine(DocumentElement element, ReadingMode mode, bool includeRoleLabels)
    {
        if (mode == ReadingMode.Raw)
            return element.Text.Trim();

        if (!includeRoleLabels)
            return element.Text.Trim();

        return element switch
        {
            HeadingElement heading =>
                $"Heading {(int)heading.Level}: {heading.Text}".TrimEnd(),

            // The count comes first, because it is the thing a listener cannot get any other way.
            TableElement table =>
                $"Table: {table.RowCount} {(table.RowCount == 1 ? "row" : "rows")}, " +
                $"{table.ColumnCount} {(table.ColumnCount == 1 ? "column" : "columns")}" +
                (table.HasHeaderCells ? string.Empty : ", no header cells"),

            ListElement list =>
                $"List of {list.ItemCount} {(list.ItemCount == 1 ? "item" : "items")}",

            ListItemElement item =>
                $"  • {item.Text}",

            // Only reached by a cell with no row of its own, which a malformed document can
            // produce. A cell inside a row is written by AppendRowLine instead.
            TableCellElement cell => $"    {(cell.Text.Length > 0 ? cell.Text : "(blank)")}",

            FigureElement figure => figure.IsMarkedDecorative
                ? "[Decorative image]"
                : figure.AlternateText is { Length: > 0 } alt
                    ? $"[Image: {alt}]"
                    : "[Image with no description]",

            PdfFormField field => BuildFieldLine(field),

            LinkElement link =>
                $"[Link: {(link.Text.Length > 0 ? link.Text : link.SpeakableTarget)}]",

            AnnotationElement annotation =>
                $"[{DocumentElement.DefaultRoleName(ElementKind.Annotation)}: {annotation.Text}]",

            CaptionElement caption => $"Caption: {caption.Text}",

            BlockQuoteElement quote => $"Quote: {quote.Text}",

            ArtifactElement artifact => $"[{artifact.ArtifactType}: {artifact.Text}]",

            _ => element.Text.Trim(),
        };
    }

    /// <summary>
    /// Writes a table row as one line, and records where each cell sits within it.
    ///
    /// The cells get their own spans inside the line, which is what keeps cell-by-cell navigation
    /// working while still reading as a row. Both matter: the row order is what makes a table
    /// comprehensible when read straight through, and the per-cell spans are what let the caret
    /// land on one cell when the user steps through them deliberately.
    /// </summary>
    private static void AppendRowLine(
        TableRowElement row,
        StringBuilder builder,
        Dictionary<int, (int, int)> byId,
        List<(int, int, int)> inOrder,
        HashSet<int> alreadyEmitted)
    {
        var cells = row.Cells;

        if (cells.Count == 0)
            return;

        if (builder.Length > 0)
            builder.Append("\r\n\r\n");

        int rowStart = builder.Length;

        // The header row is marked once at the start rather than on every cell. Repeating "header"
        // before each one triples the length of the line for no added information.
        builder.Append(row.IsHeaderRow ? "  Headings: " : "  ");

        for (int i = 0; i < cells.Count; i++)
        {
            if (i > 0)
                builder.Append("  |  ");

            var cell = cells[i];
            string value = cell.Text.Length > 0 ? cell.Text : "(blank)";

            int cellStart = builder.Length;
            builder.Append(value);

            byId[cell.Id] = (cellStart, value.Length);
            inOrder.Add((cellStart, value.Length, cell.Id));
            alreadyEmitted.Add(cell.Id);
        }

        int rowLength = builder.Length - rowStart;

        // Recorded after the cells so that a position inside the row resolves to the CELL rather
        // than the row: the ordered list is searched in order, and the more specific answer is the
        // useful one.
        byId[row.Id] = (rowStart, rowLength);
    }

    /// <summary>
    /// Renders a form field as a line showing its name, its value and whether it still needs one.
    /// A reader running Say All over the document therefore hears the state of the whole form
    /// without having to visit each field.
    /// </summary>
    private static string BuildFieldLine(PdfFormField field)
    {
        var parts = new List<string>(4)
        {
            $"[{DocumentElement.DefaultRoleName(ElementKind.FormField)}: {field.Label}",
        };

        if (field.IsRequired)
            parts.Add("required");

        string value = field.HasValue ? field.ValueForSpeech : "not filled in";
        parts.Add(value);

        if (field.IsReadOnly)
            parts.Add("read-only");

        return string.Join(", ", parts) + "]";
    }

    #endregion
}

#endregion
