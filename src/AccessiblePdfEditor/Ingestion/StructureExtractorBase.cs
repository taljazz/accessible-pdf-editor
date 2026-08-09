using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace AccessiblePdfEditor.Ingestion;

// =====================================================================================
//  StructureExtractorBase.cs
//
//  The abstract strategy for turning a PDF page into document elements, and the context
//  the strategies share.
//
//  There are exactly two ways to find out what a PDF page means, and they could not be
//  more different:
//
//    1. The document TELLS you, through its structure tree and marked content. Reliable,
//       precise, and present in maybe a third of the PDFs anyone actually receives.
//    2. You WORK IT OUT from where the ink sits on the page. Always available, never
//       certain.
//
//  Both are implemented, as subclasses of this base. The loader picks per page, because
//  real documents are inconsistent — a report generated from a template may be beautifully
//  tagged for thirty pages and then include a scanned appendix with nothing at all.
//
//  This base owns what must be identical whichever route was taken: cleaning up the
//  results, dropping empties, merging fragments and assigning positions. A subclass
//  produces raw elements and cannot skip the tidying, which is what keeps a tagged document
//  and an untagged one sounding like the same program.
// =====================================================================================

#region ExtractionContext — what a strategy needs to know beyond the page itself

/// <summary>
/// Everything a structure extractor needs that cannot be read from a single page: the document's
/// typographic norms, and somewhere to record problems.
/// </summary>
public sealed class ExtractionContext
{
    /// <summary>The one-based page number being extracted.</summary>
    public required int PageNumber { get; init; }

    /// <summary>Page width in points.</summary>
    public required double PageWidth { get; init; }

    /// <summary>Page height in points.</summary>
    public required double PageHeight { get; init; }

    /// <summary>
    /// The document's body text size in points, measured across the whole document rather than one
    /// page. This is the reference every heading decision is made against, and measuring it
    /// per-page would make a page of mostly-headings decide that headings are normal.
    /// </summary>
    public required double BodyFontSize { get; init; }

    /// <summary>The left edge of the document's main text column, in points.</summary>
    public double TextLeftMargin { get; init; }

    /// <summary>Problems worth telling the user about, collected across the whole load.</summary>
    public required List<string> Warnings { get; init; }

    /// <summary>
    /// Text that repeats in the same place on most pages, gathered in a pre-pass. Running heads and
    /// footers, which are page furniture rather than content. Matching against this is the only
    /// reliable way to identify them in an untagged document: one page cannot tell you that its top
    /// line also appears on the other ninety-nine.
    /// </summary>
    public required IReadOnlySet<string> RepeatedPageFurniture { get; init; }
}

#endregion

#region StructureExtractorBase

/// <summary>
/// Base class for the strategies that turn a page into document elements. Owns result tidying;
/// subclasses supply the extraction itself.
/// </summary>
public abstract class StructureExtractorBase
{
    #region Identity and applicability

    /// <summary>
    /// A short name for this strategy, used in warnings and in the accessibility report so the user
    /// can tell which route produced the structure they are navigating.
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Whether this strategy can handle a page. The loader asks each strategy in order of
    /// preference and uses the first that says yes.
    /// </summary>
    public abstract bool CanHandle(Page page);

    /// <summary>
    /// Whether elements from this strategy came from the document's own tags. Recorded on every
    /// element so that the user, and the auditor, can distinguish fact from inference.
    /// </summary>
    protected abstract bool ProducesRealTags { get; }

    #endregion

    #region The extraction template
    // Extract is not virtual. Subclasses implement ExtractCore and get the tidying for free, in the
    // same order, every time. The tidying is not cosmetic: dropping empty elements and merging
    // split fragments is the difference between a document that reads as sentences and one that
    // reads as a stutter.

    /// <summary>
    /// Extracts the elements of a page. Calls <see cref="ExtractCore"/>, then applies the cleanup
    /// every strategy needs.
    /// </summary>
    public IReadOnlyList<DocumentElement> Extract(Page page, ExtractionContext context)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(context);

        List<DocumentElement> elements;

        try
        {
            elements = ExtractCore(page, context);
        }
        catch (Exception ex)
        {
            // A page that cannot be analysed must not stop the other pages loading. The user gets a
            // warning and an empty page rather than a failed document — a hundred readable pages
            // and one broken one is a far better outcome than nothing at all.
            context.Warnings.Add($"Page {context.PageNumber} could not be analysed: {ex.Message}");
            return [];
        }

        foreach (var element in elements)
            element.IsFromRealTags = ProducesRealTags;

        elements = RemoveEmpty(elements);
        elements = MergeSplitParagraphs(elements, context);
        elements = MarkPageFurniture(elements, context);

        return elements;
    }

    /// <summary>Extracts a page's elements. Called inside the template's error handling.</summary>
    protected abstract List<DocumentElement> ExtractCore(Page page, ExtractionContext context);

    #endregion

    #region Shared cleanup — applied to every strategy's output

    /// <summary>
    /// Drops elements with no text and no children. Extraction routinely produces these from
    /// whitespace-only draw operations, and each one would be an arrow-key press that appears to do
    /// nothing.
    /// </summary>
    protected virtual List<DocumentElement> RemoveEmpty(List<DocumentElement> elements)
    {
        var kept = new List<DocumentElement>(elements.Count);

        foreach (var element in elements)
        {
            bool hasContent = element.Children.Count > 0
                || element.FullText.Trim().Length > 0
                || element is FigureElement
                || element is Model.Forms.PdfFormField;

            if (hasContent)
                kept.Add(element);
        }

        return kept;
    }

    /// <summary>
    /// Joins paragraphs that were split by a column or page break mid-sentence.
    ///
    /// A paragraph that ends without terminal punctuation and is followed by one starting
    /// lower-case is almost always one paragraph that the layout happened to break. Left alone it
    /// reads as two, with a full stop's worth of pause in the middle of a clause, which is one of
    /// the most persistent ways an extracted PDF sounds wrong without the listener being able to
    /// say why.
    /// </summary>
    protected virtual List<DocumentElement> MergeSplitParagraphs(
        List<DocumentElement> elements, ExtractionContext context)
    {
        var merged = new List<DocumentElement>(elements.Count);

        foreach (var element in elements)
        {
            if (element is not ParagraphElement current
                || merged.Count == 0
                || merged[^1] is not ParagraphElement previous)
            {
                merged.Add(element);
                continue;
            }

            if (!ShouldJoin(previous.Text, current.Text))
            {
                merged.Add(element);
                continue;
            }

            var combined = new ParagraphElement(previous.PageNumber, JoinText(previous.Text, current.Text))
            {
                FontSize = previous.FontSize,
                FontName = previous.FontName,
                IsBold = previous.IsBold,
                IsItalic = previous.IsItalic,
                IndentFromMargin = previous.IndentFromMargin,
                ClassificationReason = previous.ClassificationReason,
            };

            combined.Bounds = previous.Bounds.Union(current.Bounds);
            combined.IsFromRealTags = previous.IsFromRealTags && current.IsFromRealTags;
            combined.Language = previous.Language;

            merged[^1] = combined;
        }

        return merged;
    }

    /// <summary>Whether two consecutive paragraphs are really one broken in two.</summary>
    private static bool ShouldJoin(string first, string second)
    {
        if (first.Length == 0 || second.Length == 0)
            return false;

        char last = first[^1];
        char next = second[0];

        // A sentence that ended properly was meant to end.
        if (last is '.' or '!' or '?' or ':' or ';' or '"' or '”' or ')' or ']')
            return false;

        // A new sentence, a new bullet or a new numbered item was meant to start.
        if (char.IsUpper(next) || char.IsDigit(next) || next is '•' or '-' or '–' or '*')
            return false;

        // Only join when the continuation genuinely looks like mid-sentence text.
        return char.IsLower(next);
    }

    /// <summary>
    /// Joins two halves of a split paragraph, healing a hyphenated word break. "inter-" followed by
    /// "national" must become "international", not "inter- national", which a screen reader reads as
    /// two words with an audible hyphen.
    /// </summary>
    private static string JoinText(string first, string second)
    {
        if (first.EndsWith('-') && second.Length > 0 && char.IsLower(second[0]))
            return string.Concat(first.AsSpan(0, first.Length - 1), second);

        return $"{first} {second}";
    }

    /// <summary>
    /// Reclassifies text that repeats across pages as page furniture.
    ///
    /// This runs for both strategies deliberately. An untagged document has no artifact marking at
    /// all, but a partly-tagged one often has running heads that the producer forgot to mark, and
    /// hearing the same running header at every page boundary for two hundred pages is exactly the
    /// kind of thing that makes a document unbearable to read rather than merely imperfect.
    /// </summary>
    protected virtual List<DocumentElement> MarkPageFurniture(
        List<DocumentElement> elements, ExtractionContext context)
    {
        if (context.RepeatedPageFurniture.Count == 0)
            return elements;

        var result = new List<DocumentElement>(elements.Count);

        foreach (var element in elements)
        {
            if (element is ArtifactElement || element is not TextElement text)
            {
                result.Add(element);
                continue;
            }

            string normalised = NormaliseForRepetitionMatch(text.Text);

            if (normalised.Length == 0 || !context.RepeatedPageFurniture.Contains(normalised))
            {
                result.Add(element);
                continue;
            }

            string position = text.Bounds.Bottom > context.PageHeight * 0.85 ? "header"
                : text.Bounds.Top < context.PageHeight * 0.15 ? "footer"
                : "page furniture";

            var artifact = new ArtifactElement(text.PageNumber, text.Text, position)
            {
                FontSize = text.FontSize,
                FontName = text.FontName,
                IsBold = text.IsBold,
                IsItalic = text.IsItalic,
                ClassificationReason = "repeats in the same place on most pages",
            };

            artifact.Bounds = text.Bounds;
            result.Add(artifact);
        }

        return result;
    }

    /// <summary>
    /// Reduces text to a form that matches across pages. Digits collapse to a placeholder so that
    /// "Page 4 of 120" and "Page 5 of 120" are recognised as the same running footer rather than as
    /// a hundred and twenty distinct pieces of content.
    /// </summary>
    public static string NormaliseForRepetitionMatch(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        Span<char> buffer = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
        int length = 0;
        bool lastWasDigit = false;

        foreach (char c in text)
        {
            if (char.IsDigit(c))
            {
                if (!lastWasDigit)
                    buffer[length++] = '#';

                lastWasDigit = true;
                continue;
            }

            lastWasDigit = false;

            if (char.IsWhiteSpace(c))
            {
                if (length > 0 && buffer[length - 1] != ' ')
                    buffer[length++] = ' ';

                continue;
            }

            buffer[length++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..length]).Trim();
    }

    #endregion

    #region Geometry helpers shared by both strategies

    /// <summary>Converts a PdfPig rectangle into the application's own region type.</summary>
    protected static PageRegion ToRegion(PdfRectangle rectangle) =>
        new(rectangle.Left, rectangle.Bottom, rectangle.Right, rectangle.Top);

    /// <summary>
    /// The bounding region of a run of letters. Used where a strategy has letters but no rectangle
    /// of its own, which is the normal case for marked content.
    /// </summary>
    protected static PageRegion RegionOf(IReadOnlyList<Letter> letters)
    {
        if (letters.Count == 0)
            return PageRegion.Empty;

        double left = double.MaxValue, bottom = double.MaxValue;
        double right = double.MinValue, top = double.MinValue;

        foreach (var letter in letters)
        {
            var box = letter.BoundingBox;
            left = Math.Min(left, box.Left);
            bottom = Math.Min(bottom, box.Bottom);
            right = Math.Max(right, box.Right);
            top = Math.Max(top, box.Top);
        }

        return new PageRegion(left, bottom, right, top);
    }

    /// <summary>
    /// The dominant point size of a run of letters, weighted by how many letters use it. A weighted
    /// measure rather than an average, because a heading containing one superscript footnote marker
    /// should still be measured at the heading's size.
    /// </summary>
    protected static double DominantPointSize(IReadOnlyList<Letter> letters)
    {
        if (letters.Count == 0)
            return 0;

        var counts = new Dictionary<double, int>();

        foreach (var letter in letters)
        {
            if (char.IsWhiteSpace(letter.Value.Length > 0 ? letter.Value[0] : ' '))
                continue;

            double size = Math.Round(letter.PointSize, 1);
            counts[size] = counts.GetValueOrDefault(size) + 1;
        }

        if (counts.Count == 0)
            return Math.Round(letters[0].PointSize, 1);

        double dominant = 0;
        int best = -1;

        foreach (var (size, count) in counts)
        {
            if (count > best)
            {
                best = count;
                dominant = size;
            }
        }

        return dominant;
    }

    /// <summary>Whether most of a run of letters is bold.</summary>
    protected static bool IsMostlyBold(IReadOnlyList<Letter> letters)
    {
        int bold = 0, total = 0;

        foreach (var letter in letters)
        {
            if (letter.Value.Length == 0 || char.IsWhiteSpace(letter.Value[0]))
                continue;

            total++;

            // Font flags are the reliable signal where present; the name is the fallback, because
            // plenty of PDFs embed a bold face without setting the flag.
            if (letter.FontDetails?.IsBold == true ||
                (letter.FontName?.Contains("bold", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                bold++;
            }
        }

        return total > 0 && bold * 2 > total;
    }

    /// <summary>Whether most of a run of letters is italic.</summary>
    protected static bool IsMostlyItalic(IReadOnlyList<Letter> letters)
    {
        int italic = 0, total = 0;

        foreach (var letter in letters)
        {
            if (letter.Value.Length == 0 || char.IsWhiteSpace(letter.Value[0]))
                continue;

            total++;

            if (letter.FontDetails?.IsItalic == true ||
                (letter.FontName?.Contains("italic", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (letter.FontName?.Contains("oblique", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                italic++;
            }
        }

        return total > 0 && italic * 2 > total;
    }

    /// <summary>The dominant font name of a run of letters.</summary>
    protected static string? DominantFontName(IReadOnlyList<Letter> letters)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var letter in letters)
        {
            if (string.IsNullOrEmpty(letter.FontName))
                continue;

            counts[letter.FontName] = counts.GetValueOrDefault(letter.FontName) + 1;
        }

        if (counts.Count == 0)
            return null;

        string? best = null;
        int bestCount = -1;

        foreach (var (name, count) in counts)
        {
            if (count > bestCount)
            {
                bestCount = count;
                best = name;
            }
        }

        return best;
    }

    #endregion
}

#endregion
