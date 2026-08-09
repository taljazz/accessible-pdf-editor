using System.Text.RegularExpressions;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace AccessiblePdfEditor.Ingestion;

// =====================================================================================
//  HeuristicStructureExtractor.cs
//
//  Works out a page's structure from where the ink sits, for the majority of PDFs that
//  carry no tags at all.
//
//  Everything here is inference, and the file is written on that basis. Each decision
//  records WHY it was made, in words, so the user can ask "why is this a heading?" and get
//  "because it is 1.4 times the body text size and bold" rather than being asked to trust
//  a black box. When the answer is wrong — and on a badly laid-out document it will
//  sometimes be wrong — that explanation is what lets them correct it rather than simply
//  distrust the whole document.
//
//  The pipeline:
//    words   → nearest-neighbour grouping of letters
//    blocks  → Docstrum segmentation into paragraph-sized regions
//    order   → spatial reading-order detection, which handles multi-column layouts
//    classify→ heading / list item / paragraph, from size, weight, length and indentation
//    group   → consecutive list items gathered into real lists
// =====================================================================================

#region HeuristicStructureExtractor

/// <summary>
/// Infers document structure from page layout. Used for untagged pages, which are the majority of
/// real-world PDFs.
/// </summary>
public sealed partial class HeuristicStructureExtractor : StructureExtractorBase
{
    #region Identity

    public override string Name => "layout analysis";

    /// <summary>
    /// Handles any page with extractable text. It is the fallback of last resort, so it never
    /// declines a page that has anything at all to work with.
    /// </summary>
    public override bool CanHandle(Page page) => page.Letters.Count > 0;

    /// <summary>
    /// Nothing this class produces came from a tag. Every element it creates is marked as inferred,
    /// which is what drives the "inferred from layout" announcement and the auditor's findings.
    /// </summary>
    protected override bool ProducesRealTags => false;

    #endregion

    #region Tuning constants
    // Named rather than inline so the reasoning is visible and so they can be adjusted in one
    // place. The heading ratios are the important ones: they are what separates a heading from
    // emphasised body text, and they were chosen to be conservative. Calling a paragraph a heading
    // corrupts the outline that the user navigates by, which is worse than missing one.

    /// <summary>A block this many times the body size is a top-level heading.</summary>
    private const double Level1SizeRatio = 1.60;

    /// <summary>A block this many times the body size is a second-level heading.</summary>
    private const double Level2SizeRatio = 1.35;

    /// <summary>A block this many times the body size is a third-level heading.</summary>
    private const double Level3SizeRatio = 1.18;

    /// <summary>
    /// Bold text at least this much larger than the body counts as a low-level heading. Just above
    /// 1.0, because a bold run at exactly body size is far more often emphasis inside a sentence.
    /// </summary>
    private const double BoldHeadingMinimumRatio = 1.02;

    /// <summary>
    /// Longest a block can be and still be considered a heading. Headings are short; a long run of
    /// large text is a pull quote or a title page, not something to put in the outline.
    /// </summary>
    private const int MaximumHeadingLength = 140;

    #endregion

    #region Extraction

    protected override List<DocumentElement> ExtractCore(Page page, ExtractionContext context)
    {
        var elements = new List<DocumentElement>();

        var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToList();

        if (words.Count == 0)
        {
            // No text at all. Any images present are the entire content of the page, which is the
            // signature of a scan. Reported rather than silently producing an empty page.
            AddFigures(page, context, elements);
            return elements;
        }

        // Tables are found FIRST, from the raw words, and their words are then withheld from block
        // segmentation. Done the other way round, the segmenter merges a table's columns into
        // column-shaped blocks and the grid is gone before anything can recognise it — which is
        // exactly why an untagged table used to read down its columns instead of across its rows.
        var detected = TableDetector.Detect(words, context);

        var remaining = detected.ConsumedWords.Count == 0
            ? words
            : words.Where(w => !detected.ConsumedWords.Contains(w)).ToList();

        var blocks = SegmentIntoBlocks(remaining, context);
        var classified = new List<DocumentElement>(blocks.Count);

        foreach (var block in blocks)
            classified.Add(Classify(block, context));

        // Lists are formed after classification, because whether a line is a list item depends on
        // its own text but whether it belongs to a list depends on its neighbours.
        var grouped = GroupConsecutiveListItems(classified);

        elements.AddRange(MergeTablesIntoReadingOrder(grouped, detected.Tables));

        AddFigures(page, context, elements);

        return elements;
    }

    /// <summary>
    /// Puts the detected tables back among the ordered blocks, at the place their position on the
    /// page says they belong.
    ///
    /// The blocks have already been through reading-order detection, which handles columns and is
    /// worth preserving. So rather than re-sorting everything and losing that, each table is
    /// inserted before the first block that starts below it — which is right for a single column
    /// and sensible everywhere else.
    /// </summary>
    private static List<DocumentElement> MergeTablesIntoReadingOrder(
        List<DocumentElement> ordered, IReadOnlyList<TableElement> tables)
    {
        if (tables.Count == 0)
            return ordered;

        var result = new List<DocumentElement>(ordered);

        foreach (var table in tables)
        {
            int index = result.FindIndex(e =>
                !e.Bounds.IsEmpty && e.Bounds.Top < table.Bounds.Top);

            if (index < 0)
                result.Add(table);
            else
                result.Insert(index, table);
        }

        return result;
    }

    /// <summary>
    /// Segments a page's words into paragraph-sized blocks and puts them in reading order.
    ///
    /// Docstrum estimates the document's own line and word spacing from the page rather than
    /// assuming fixed thresholds, which is what makes it work on documents this code has never
    /// seen. The reading-order detector then handles multi-column layouts — without it, a
    /// two-column page reads straight across the gutter and produces interleaved nonsense.
    /// </summary>
    private static List<TextBlock> SegmentIntoBlocks(IReadOnlyList<Word> words, ExtractionContext context)
    {
        try
        {
            var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);

            if (blocks.Count == 0)
                return [];

            var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks).ToList();
            return ordered.Count > 0 ? ordered : blocks.ToList();
        }
        catch (Exception ex)
        {
            // Segmentation can fail on pathological geometry. Falling back to one block per line
            // loses paragraph grouping but keeps every word readable, which is the right trade.
            context.Warnings.Add(
                $"Page {context.PageNumber}: layout analysis fell back to line-by-line reading ({ex.Message}).");

            return words
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 1))
                .OrderByDescending(g => g.Key)
                .Select(g => new TextBlock([new TextLine(g.OrderBy(w => w.BoundingBox.Left).ToList())]))
                .ToList();
        }
    }

    #endregion

    #region Classification — and the explanation that goes with every decision

    /// <summary>
    /// Decides what a block is. Every branch records its reasoning, which the user can ask for.
    /// </summary>
    private DocumentElement Classify(TextBlock block, ExtractionContext context)
    {
        var letters = block.TextLines.SelectMany(l => l.Words).SelectMany(w => w.Letters).ToList();

        string text = TextElementText(block);
        double size = DominantPointSize(letters);
        bool bold = IsMostlyBold(letters);
        bool italic = IsMostlyItalic(letters);
        string? fontName = DominantFontName(letters);
        var bounds = ToRegion(block.BoundingBox);
        double indent = bounds.Left - context.TextLeftMargin;

        double ratio = context.BodyFontSize > 0 ? size / context.BodyFontSize : 1.0;

        // Lists are checked first. A numbered heading like "3. Methodology" would otherwise be
        // classified as a heading and lose its place in the list it belongs to; checking the marker
        // first and the size second keeps both facts.
        if (TryMatchListMarker(text, out string marker, out string body, out ListMarkerKind markerKind))
        {
            var item = new ListItemElement(context.PageNumber, body, marker) { Bounds = bounds };
            item.Language = null;

            // Carried on the item so that list grouping can read the marker kind back off it.
            _pendingMarkerKind[item] = markerKind;
            return item;
        }

        var headingLevel = DetermineHeadingLevel(text, ratio, bold, block);

        if (headingLevel != HeadingLevel.None)
        {
            return new HeadingElement(context.PageNumber, text, headingLevel)
            {
                FontSize = size,
                FontName = fontName,
                IsBold = bold,
                IsItalic = italic,
                IndentFromMargin = indent,
                ClassificationReason = BuildHeadingReason(ratio, bold, text),
                Bounds = bounds,
            };
        }

        // A wholly italic block set in from the margin is the conventional look of a block quote.
        if (italic && indent > context.BodyFontSize && text.Length > 40)
        {
            return new BlockQuoteElement(context.PageNumber, text)
            {
                FontSize = size,
                FontName = fontName,
                IsItalic = true,
                IndentFromMargin = indent,
                ClassificationReason = "italic and indented from the margin",
                Bounds = bounds,
            };
        }

        // A monospaced font is the only reliable signal for preformatted text, and it is a strong
        // one: nobody sets body prose in Courier by accident.
        if (fontName is not null && LooksMonospaced(fontName))
        {
            return new CodeElement(context.PageNumber, text)
            {
                FontSize = size,
                FontName = fontName,
                IndentFromMargin = indent,
                ClassificationReason = $"set in {fontName}, a monospaced font",
                Bounds = bounds,
            };
        }

        return new ParagraphElement(context.PageNumber, text)
        {
            FontSize = size,
            FontName = fontName,
            IsBold = bold,
            IsItalic = italic,
            IndentFromMargin = indent,
            ClassificationReason = "body text",
            Bounds = bounds,
        };
    }

    /// <summary>
    /// Chooses a heading level, or None. Size is the primary signal and weight the secondary one;
    /// length is a veto, because a long block is prose however it is set.
    /// </summary>
    private static HeadingLevel DetermineHeadingLevel(string text, double ratio, bool bold, TextBlock block)
    {
        if (text.Length == 0 || text.Length > MaximumHeadingLength)
            return HeadingLevel.None;

        // A block running to several lines is a paragraph even when it is large — a pull quote, or
        // a title page's standfirst.
        if (block.TextLines.Count > 3)
            return HeadingLevel.None;

        // Terminal punctuation means a sentence, and sentences are not headings. A question mark is
        // allowed through because headings are quite often questions.
        char last = text[^1];
        if (last is '.' or ';' or ',')
            return HeadingLevel.None;

        if (ratio >= Level1SizeRatio) return HeadingLevel.Level1;
        if (ratio >= Level2SizeRatio) return HeadingLevel.Level2;
        if (ratio >= Level3SizeRatio) return HeadingLevel.Level3;

        if (bold && ratio >= BoldHeadingMinimumRatio && text.Length <= 80)
            return HeadingLevel.Level4;

        // Short, entirely upper-case, and at body size: the house style of a great many reports,
        // and invisible to a size-based test.
        if (IsShoutedHeading(text) && text.Length <= 60)
            return HeadingLevel.Level4;

        return HeadingLevel.None;
    }

    /// <summary>Whether text is set entirely in capitals, with enough letters for that to mean anything.</summary>
    private static bool IsShoutedHeading(string text)
    {
        int letters = 0, upper = 0;

        foreach (char c in text)
        {
            if (!char.IsLetter(c))
                continue;

            letters++;
            if (char.IsUpper(c))
                upper++;
        }

        return letters >= 4 && upper == letters;
    }

    /// <summary>Builds the sentence the user hears when they ask why something is a heading.</summary>
    private static string BuildHeadingReason(double ratio, bool bold, string text)
    {
        if (ratio >= Level3SizeRatio)
        {
            string weight = bold ? " and bold" : string.Empty;
            return $"{ratio:0.0} times the body text size{weight}";
        }

        if (bold)
            return "bold, short, and set apart from the surrounding text";

        if (IsShoutedHeading(text))
            return "short and set entirely in capitals";

        return "set apart from the surrounding text";
    }

    /// <summary>
    /// Whether a font name indicates a monospaced face. Name matching is the only option available:
    /// the metrics that would prove it are not exposed, and the common families are few enough that
    /// a list covers nearly every real document.
    /// </summary>
    private static bool LooksMonospaced(string fontName)
    {
        ReadOnlySpan<string> families =
        [
            "courier", "consolas", "monaco", "menlo", "mono", "lucida console",
            "andale", "inconsolata", "source code", "dejavu sans mono", "roboto mono",
        ];

        foreach (string family in families)
        {
            if (fontName.Contains(family, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Joins a block's lines, healing hyphenated word breaks at line ends.</summary>
    private static string TextElementText(TextBlock block)
    {
        var builder = new System.Text.StringBuilder();

        foreach (var line in block.TextLines)
        {
            string text = line.Text.Trim();
            if (text.Length == 0)
                continue;

            if (builder.Length == 0)
            {
                builder.Append(text);
                continue;
            }

            if (builder[^1] == '-' && text.Length > 0 && char.IsLower(text[0]))
            {
                builder.Length--;
                builder.Append(text);
            }
            else
            {
                builder.Append(' ').Append(text);
            }
        }

        return builder.ToString();
    }

    #endregion

    #region List detection
    // Recognising a list is recognising its marker. The markers below cover bullets, numbers,
    // letters and roman numerals in the punctuation styles that actually occur. Getting this right
    // matters because a list read as a run of paragraphs loses the one thing a listener most needs
    // from it: how many items there are.

    /// <summary>Marker kinds carried from classification through to list grouping.</summary>
    private readonly Dictionary<DocumentElement, ListMarkerKind> _pendingMarkerKind = [];

    /// <summary>
    /// Detects a list marker at the start of a block and splits it from the item's text.
    /// </summary>
    private static bool TryMatchListMarker(
        string text, out string marker, out string body, out ListMarkerKind kind)
    {
        marker = string.Empty;
        body = text;
        kind = ListMarkerKind.None;

        if (text.Length < 2)
            return false;

        // Bullets: a single symbol followed by whitespace.
        ReadOnlySpan<char> bullets = ['•', '·', '▪', '▫', '◦', '‣', '⁃', '−', '–', '—', '*', '+'];
        if (bullets.Contains(text[0]) && text.Length > 1 && char.IsWhiteSpace(text[1]))
        {
            marker = text[0].ToString();
            body = text[2..].TrimStart();
            kind = ListMarkerKind.Bullet;
            return body.Length > 0;
        }

        // A hyphen only counts as a bullet when followed by a space; otherwise it is a dash or
        // part of a hyphenated word.
        if (text[0] == '-' && text.Length > 1 && text[1] == ' ')
        {
            marker = "-";
            body = text[2..].TrimStart();
            kind = ListMarkerKind.Bullet;
            return body.Length > 0;
        }

        var match = OrderedMarker().Match(text);
        if (!match.Success)
            return false;

        marker = match.Groups["marker"].Value;
        body = text[match.Length..].TrimStart();

        if (body.Length == 0)
            return false;

        string label = match.Groups["label"].Value;

        kind = label.All(char.IsDigit) ? ListMarkerKind.Decimal
            : IsRomanNumeral(label) ? (char.IsUpper(label[0]) ? ListMarkerKind.UpperRoman : ListMarkerKind.LowerRoman)
            : char.IsUpper(label[0]) ? ListMarkerKind.UpperAlpha
            : ListMarkerKind.LowerAlpha;

        return true;
    }

    /// <summary>
    /// Matches "1.", "12)", "(a)", "iv." and the like at the start of a line. Deliberately requires
    /// trailing whitespace so that a sentence beginning with a year, or a decimal number, is not
    /// mistaken for a numbered item.
    /// </summary>
    [GeneratedRegex(@"^(?<marker>\(?(?<label>\d{1,3}|[a-zA-Z]|[ivxlcdmIVXLCDM]{1,6})[\.\)\]]\s)", RegexOptions.CultureInvariant)]
    private static partial Regex OrderedMarker();

    private static bool IsRomanNumeral(string label)
    {
        if (label.Length == 0)
            return false;

        foreach (char c in label)
        {
            if (!"ivxlcdmIVXLCDM".Contains(c))
                return false;
        }

        // A single letter is far more likely to be an alphabetic marker than a roman numeral, with
        // the exception of "i", which begins every roman-numbered list there has ever been.
        return label.Length > 1 || label is "i" or "I";
    }

    /// <summary>
    /// Gathers runs of consecutive list items into real list elements.
    ///
    /// A single stray item is left as a paragraph: one item is not a list, and announcing "list
    /// with 1 item" every time a paragraph happens to begin with a number in brackets would be
    /// worse than not detecting it at all.
    /// </summary>
    private List<DocumentElement> GroupConsecutiveListItems(List<DocumentElement> elements)
    {
        var result = new List<DocumentElement>(elements.Count);
        int index = 0;

        while (index < elements.Count)
        {
            if (elements[index] is not ListItemElement)
            {
                result.Add(elements[index]);
                index++;
                continue;
            }

            int runEnd = index;
            while (runEnd < elements.Count && elements[runEnd] is ListItemElement)
                runEnd++;

            int runLength = runEnd - index;

            if (runLength < 2)
            {
                // Demote the lone item back to a paragraph, restoring its marker so no text is lost.
                var lone = (ListItemElement)elements[index];
                string restored = lone.Label is { Length: > 0 } label ? $"{label} {lone.Body}" : lone.Body;

                var paragraph = new ParagraphElement(lone.PageNumber, restored)
                {
                    ClassificationReason = "starts with what looks like a list marker, but stands alone",
                    Bounds = lone.Bounds,
                };

                result.Add(paragraph);
                index = runEnd;
                continue;
            }

            var first = (ListItemElement)elements[index];
            var kind = _pendingMarkerKind.GetValueOrDefault(first, ListMarkerKind.None);

            var list = new ListElement(first.PageNumber, kind)
            {
                Bounds = first.Bounds,
            };

            for (int i = index; i < runEnd; i++)
            {
                var item = (ListItemElement)elements[i];
                list.AddChild(item);
                list.Bounds = list.Bounds.Union(item.Bounds);
            }

            result.Add(list);
            index = runEnd;
        }

        _pendingMarkerKind.Clear();
        return result;
    }

    #endregion

    #region Figures
    // Untagged images never carry alt text, so every one of these becomes an audit finding and a
    // remediation opportunity. Very small images are still produced as elements, but the figure
    // itself knows it is probably decorative, so the remediation workflow can skip them rather
    // than asking the user to describe forty spacer graphics.

    private static void AddFigures(Page page, ExtractionContext context, List<DocumentElement> elements)
    {
        IReadOnlyList<IPdfImage> images;

        try
        {
            images = page.GetImages().ToList();
        }
        catch (Exception ex)
        {
            context.Warnings.Add($"Page {context.PageNumber}: images could not be read ({ex.Message}).");
            return;
        }

        foreach (var image in images)
        {
            var figure = new FigureElement(context.PageNumber, ToRegion(image.BoundingBox))
            {
                PixelWidth = image.WidthInSamples,
                PixelHeight = image.HeightInSamples,
            };

            // An image mask is a stencil used for drawing shapes and rules rather than a picture.
            // Marking them decorative keeps them out of the remediation list, where they would be
            // pure noise.
            if (image.IsImageMask)
                figure.MarkDecorative();

            elements.Add(figure);
        }
    }

    #endregion
}

#endregion
