using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Outline;
using UglyToad.PdfPig.Tokens;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace AccessiblePdfEditor.Ingestion;

// =====================================================================================
//  DocumentLoader.cs
//
//  Turns a file on disk into a PdfDocumentModel: the contract, an abstract base holding the
//  work every loader must do, and the PdfPig implementation.
//
//  The base class exists because loading has a fixed shape that must not vary — check the
//  file, parse it, then ALWAYS run the same finishing pass: resolve heading levels, build
//  the reading order, work out how well tagged the document really is. A loader that
//  skipped the finishing pass would produce a document that looked fine and navigated
//  wrongly, so it is not given the opportunity.
//
//  Two pre-passes run before any page is analysed, and both exist because a page cannot
//  answer the question alone:
//
//    body text size — a page of mostly headings would otherwise decide headings are normal
//    repeated furniture — no single page can know its top line appears on every other page
//
//  Getting those wrong is the difference between a document that reads cleanly and one that
//  announces its own running header two hundred times.
// =====================================================================================

#region DocumentLoadResult — what came back

/// <summary>The outcome of trying to load a file.</summary>
public sealed record DocumentLoadResult(
    DocumentLoadState State,
    PdfDocumentModel? Document,
    string Message)
{
    /// <summary>True when a document was produced and can be read.</summary>
    public bool IsSuccess => State == DocumentLoadState.Loaded && Document is not null;

    public static DocumentLoadResult Success(PdfDocumentModel document) =>
        new(DocumentLoadState.Loaded, document, document.BuildOpeningAnnouncement());

    public static DocumentLoadResult NeedsPassword(string path) =>
        new(DocumentLoadState.PasswordRequired, null,
            $"{Path.GetFileName(path)} is password protected. Enter the password to open it.");

    public static DocumentLoadResult Failed(string message) =>
        new(DocumentLoadState.Failed, null, message);
}

#endregion

#region IDocumentLoader — the contract

/// <summary>Loads a file into the application's document model.</summary>
public interface IDocumentLoader
{
    /// <summary>
    /// Loads a document. Never throws for an unreadable file: every failure comes back as a result
    /// with a state and a message the user can be told.
    /// </summary>
    DocumentLoadResult Load(string filePath, string? password = null);
}

#endregion

#region DocumentLoaderBase — the finishing pass no loader may skip

/// <summary>
/// Base class for document loaders. Owns file validation and the finishing pass; subclasses supply
/// only the parsing.
/// </summary>
public abstract class DocumentLoaderBase : IDocumentLoader
{
    #region The load template

    /// <inheritdoc />
    public DocumentLoadResult Load(string filePath, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return DocumentLoadResult.Failed("No file was given.");

        if (!File.Exists(filePath))
            return DocumentLoadResult.Failed($"{Path.GetFileName(filePath)} could not be found.");

        DocumentLoadResult result;

        try
        {
            result = LoadCore(filePath, password);
        }
        catch (UnauthorizedAccessException)
        {
            return DocumentLoadResult.Failed(
                $"{Path.GetFileName(filePath)} could not be opened. You may not have permission to read it.");
        }
        catch (IOException ex)
        {
            return DocumentLoadResult.Failed(
                $"{Path.GetFileName(filePath)} could not be read. It may be open in another program. {ex.Message}");
        }
        catch (Exception ex)
        {
            return DocumentLoadResult.Failed($"{Path.GetFileName(filePath)} could not be opened: {ex.Message}");
        }

        if (result.Document is { } document)
            FinishLoading(document);

        return result;
    }

    /// <summary>Parses a file. Called inside the template's error handling.</summary>
    protected abstract DocumentLoadResult LoadCore(string filePath, string? password);

    #endregion

    #region The finishing pass
    // Runs for every loader, in this order. Each step depends on the one before it: heading levels
    // must be resolved before the reading order is numbered, and the tagged status can only be
    // judged once every element exists.

    /// <summary>
    /// Completes a freshly parsed document. Subclasses may extend this but must call the base
    /// implementation, because navigation will not work without it.
    /// </summary>
    protected virtual void FinishLoading(PdfDocumentModel document)
    {
        ResolveUnnumberedHeadings(document);
        AssignTabOrder(document);
        document.RebuildReadingOrder();
        document.TaggedStatus = DetermineTaggedStatus(document);
    }

    /// <summary>
    /// Gives a level to headings tagged with a bare /H, whose level comes from nesting depth rather
    /// than from the tag name. Without this they would all report as "heading" with no level, and
    /// jumping between heading levels — the main way a listener skims a long document — would not
    /// work at all.
    /// </summary>
    protected static void ResolveUnnumberedHeadings(PdfDocumentModel document)
    {
        foreach (var heading in document.Root.SelfAndDescendants().OfType<HeadingElement>())
        {
            if (heading.Level != HeadingLevel.None)
                continue;

            // Count only the containers that genuinely nest content. Counting every ancestor would
            // make a heading's level depend on how the producer happened to wrap its paragraphs.
            int depth = heading.Ancestors()
                .Count(a => a.Kind is ElementKind.Section or ElementKind.Document);

            heading.Level = (HeadingLevel)Math.Clamp(depth, 1, 6);
        }
    }

    /// <summary>
    /// Numbers form fields in the order the user will reach them by pressing Tab: down the page,
    /// then across, page by page. PDF can specify its own tab order, but a great many forms do not,
    /// and the geometric order is what a sighted user experiences.
    /// </summary>
    protected static void AssignTabOrder(PdfDocumentModel document)
    {
        var fields = document.Root.SelfAndDescendants()
            .OfType<Model.Forms.PdfFormField>()
            .OrderBy(f => f.PageNumber)
            // Descending Y, because PDF's origin is at the bottom of the page and reading runs
            // downwards. Rounded so that fields sharing a line are not split by sub-point noise.
            .ThenByDescending(f => Math.Round(f.Bounds.Top, 1))
            .ThenBy(f => f.Bounds.Left)
            .ToList();

        for (int i = 0; i < fields.Count; i++)
            fields[i].TabOrder = i + 1;
    }

    /// <summary>
    /// Judges how well tagged a document really is, by measuring how much of its content the tags
    /// actually cover.
    ///
    /// The question is not "does a structure tree exist" — plenty of documents carry a tree that
    /// covers the first page and nothing else, and treating those as tagged would hide the rest of
    /// the document from a reader navigating by structure. So this measures coverage and reports
    /// partial tagging honestly.
    /// </summary>
    protected static TaggedStatus DetermineTaggedStatus(PdfDocumentModel document)
    {
        var contentElements = document.Root.SelfAndDescendants()
            .Where(e => e.Kind is not (ElementKind.Document or ElementKind.Page or ElementKind.Section))
            .ToList();

        if (contentElements.Count == 0)
        {
            bool anyImages = document.Root.SelfAndDescendants().OfType<FigureElement>().Any();
            return anyImages ? TaggedStatus.ScannedWithoutText : TaggedStatus.Untagged;
        }

        // A page carrying images and essentially no text is a scan. Judged per page and then
        // across the document, because a scanned appendix inside a born-digital report is common.
        int imageOnlyPages = 0;
        foreach (var page in document.Pages)
        {
            bool hasText = page.SelfAndDescendants()
                .Any(e => e is TextElement { Kind: not ElementKind.Artifact } text && text.Text.Trim().Length > 2);

            bool hasImages = page.SelfAndDescendants().OfType<FigureElement>().Any();

            if (!hasText && hasImages)
            {
                imageOnlyPages++;
                page.IsImageOnly = true;
            }
        }

        if (document.PageCount > 0 && imageOnlyPages == document.PageCount)
            return TaggedStatus.ScannedWithoutText;

        int tagged = contentElements.Count(e => e.IsFromRealTags);
        double coverage = (double)tagged / contentElements.Count;

        return coverage switch
        {
            >= 0.90 => TaggedStatus.FullyTagged,
            >= 0.10 => TaggedStatus.PartiallyTagged,
            _ => TaggedStatus.Untagged,
        };
    }

    #endregion
}

#endregion

#region PdfPigDocumentLoader — the real one

/// <summary>Loads PDFs using PdfPig, choosing a structure extractor per page.</summary>
public sealed class PdfPigDocumentLoader : DocumentLoaderBase
{
    #region Strategy selection
    // Ordered by preference. The tagged extractor is asked first for every page and declines the
    // ones it cannot help with, so a document that is tagged in places gets the best available
    // treatment page by page rather than being forced down one route for its whole length.

    private readonly StructureExtractorBase[] _extractors =
    [
        new TaggedStructureExtractor(),
        new HeuristicStructureExtractor(),
    ];

    private StructureExtractorBase SelectExtractor(Page page)
    {
        foreach (var extractor in _extractors)
        {
            if (extractor.CanHandle(page))
                return extractor;
        }

        return _extractors[^1];
    }

    #endregion

    #region Parsing

    protected override DocumentLoadResult LoadCore(string filePath, string? password)
    {
        var options = new ParsingOptions
        {
            // Real PDFs are full of small specification violations, and refusing to open one
            // because of a malformed cross-reference entry helps nobody.
            UseLenientParsing = true,

            // A missing font must not stop the load. The text is usually still extractable, and a
            // document with one unreadable heading beats no document at all.
            SkipMissingFonts = true,

            // Honours the /ActualText attribute during extraction, which is what makes ligatures
            // and drop caps read as the words they represent.
            UseActualText = true,

            Password = password ?? string.Empty,
        };

        PigDocument pig;

        try
        {
            pig = PigDocument.Open(filePath, options);
        }
        catch (PdfDocumentEncryptedException)
        {
            return DocumentLoadResult.NeedsPassword(filePath);
        }
        catch (PdfDocumentFormatException ex)
        {
            return DocumentLoadResult.Failed(
                $"{Path.GetFileName(filePath)} is not a valid PDF, or is damaged: {ex.Message}");
        }

        using (pig)
        {
            return BuildModel(filePath, pig);
        }
    }

    private DocumentLoadResult BuildModel(string filePath, PigDocument pig)
    {
        var warnings = new List<string>();

        var metadata = ReadMetadata(filePath, pig, warnings);
        string title = metadata.Title is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(filePath);

        var root = new DocumentRootElement(title);
        var document = new PdfDocumentModel(filePath, root);

        CopyMetadata(document.Metadata, metadata);

        var pages = ReadPagesSafely(pig, warnings);

        if (pages.Count == 0)
        {
            document.LoadWarnings.AddRange(warnings);
            document.TaggedStatus = TaggedStatus.Untagged;
            return DocumentLoadResult.Success(document);
        }

        double bodyFontSize = MeasureBodyFontSize(pages);
        double leftMargin = MeasureLeftMargin(pages);
        var repeatedFurniture = FindRepeatedPageFurniture(pages);

        foreach (var page in pages)
        {
            var pageElement = new PageElement(page.Number, page.Width, page.Height);
            root.AddChild(pageElement);

            var context = new ExtractionContext
            {
                PageNumber = page.Number,
                PageWidth = page.Width,
                PageHeight = page.Height,
                BodyFontSize = bodyFontSize,
                TextLeftMargin = leftMargin,
                Warnings = warnings,
                RepeatedPageFurniture = repeatedFurniture,
            };

            var extractor = SelectExtractor(page);

            foreach (var element in extractor.Extract(page, context))
                pageElement.AddChild(element);

            AddLinks(page, pageElement, warnings);
            AddAnnotations(page, pageElement, warnings);
        }

        AddFormFields(pig, document, warnings);
        ReadOutline(pig, document, warnings);

        document.LoadWarnings.AddRange(warnings);
        return DocumentLoadResult.Success(document);
    }

    /// <summary>
    /// Materialises every page, tolerating individual failures. One unreadable page in a long
    /// document must not cost the user the other ninety-nine.
    /// </summary>
    private static List<Page> ReadPagesSafely(PigDocument pig, List<string> warnings)
    {
        var pages = new List<Page>(pig.NumberOfPages);

        for (int number = 1; number <= pig.NumberOfPages; number++)
        {
            try
            {
                pages.Add(pig.GetPage(number));
            }
            catch (Exception ex)
            {
                warnings.Add($"Page {number} could not be read and has been skipped: {ex.Message}");
            }
        }

        return pages;
    }

    #endregion

    #region Pre-passes — the measurements a single page cannot make

    /// <summary>
    /// Measures the document's body text size: the point size used by more characters than any
    /// other, across the whole document.
    ///
    /// The mode rather than the mean, because a document's body size is a specific value that a
    /// designer chose, and averaging it with the headings and footnotes produces a number that no
    /// text on the page actually uses.
    /// </summary>
    private static double MeasureBodyFontSize(IReadOnlyList<Page> pages)
    {
        var counts = new Dictionary<double, int>();

        // Sampling the first pages is enough and keeps a 500-page document opening quickly. A
        // document that changes its body size a third of the way through is vanishingly rare.
        int sampled = Math.Min(pages.Count, 12);

        for (int i = 0; i < sampled; i++)
        {
            foreach (var letter in pages[i].Letters)
            {
                if (letter.Value.Length == 0 || char.IsWhiteSpace(letter.Value[0]))
                    continue;

                double size = Math.Round(letter.PointSize, 1);
                if (size <= 0)
                    continue;

                counts[size] = counts.GetValueOrDefault(size) + 1;
            }
        }

        if (counts.Count == 0)
            return 12.0;

        double dominant = 12.0;
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

    /// <summary>
    /// Finds the left edge of the main text column, used to measure how far elements are indented.
    /// The most common left edge rather than the leftmost, so that one outdented drop cap does not
    /// redefine the margin for the whole document.
    /// </summary>
    private static double MeasureLeftMargin(IReadOnlyList<Page> pages)
    {
        var counts = new Dictionary<int, int>();
        int sampled = Math.Min(pages.Count, 8);

        for (int i = 0; i < sampled; i++)
        {
            foreach (var word in pages[i].GetWords())
            {
                // Bucketed to 3-point bands so that near-identical left edges are counted together
                // rather than each forming its own value.
                int bucket = (int)(word.BoundingBox.Left / 3);
                counts[bucket] = counts.GetValueOrDefault(bucket) + 1;
            }
        }

        if (counts.Count == 0)
            return 0;

        int bestBucket = 0, best = -1;

        foreach (var (bucket, count) in counts)
        {
            if (count > best)
            {
                best = count;
                bestBucket = bucket;
            }
        }

        return bestBucket * 3.0;
    }

    /// <summary>
    /// Finds text that repeats in the same position across most pages: running heads, footers and
    /// page numbers.
    ///
    /// This is the pre-pass that makes long documents bearable. A running header that appears on
    /// every page is read at every page boundary unless something identifies it, and no single page
    /// can tell that its own top line is furniture rather than content. Numbers are normalised out
    /// beforehand so that "Page 4 of 120" and "Page 5 of 120" count as the same footer.
    /// </summary>
    private static HashSet<string> FindRepeatedPageFurniture(IReadOnlyList<Page> pages)
    {
        var repeated = new HashSet<string>(StringComparer.Ordinal);

        // Below this, "repeats on most pages" is not a meaningful statement.
        if (pages.Count < 4)
            return repeated;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int sampled = Math.Min(pages.Count, 30);

        for (int i = 0; i < sampled; i++)
        {
            var page = pages[i];
            double topBand = page.Height * 0.88;
            double bottomBand = page.Height * 0.12;

            var seenOnThisPage = new HashSet<string>(StringComparer.Ordinal);

            foreach (var word in page.GetWords())
            {
                double y = word.BoundingBox.Bottom;
                if (y < topBand && y > bottomBand)
                    continue;

                // Group the words of a header or footer line together, so the whole line is matched
                // rather than each word separately.
                string line = word.Text;
                if (line.Trim().Length == 0)
                    continue;

                string normalised = StructureExtractorBase.NormaliseForRepetitionMatch(line);
                if (normalised.Length < 2)
                    continue;

                seenOnThisPage.Add(normalised);
            }

            foreach (string entry in seenOnThisPage)
                counts[entry] = counts.GetValueOrDefault(entry) + 1;
        }

        // Present on at least two thirds of the sampled pages. High enough that a phrase which
        // merely recurs often is not silenced, low enough to catch documents whose first pages
        // carry no running head.
        int threshold = Math.Max(3, sampled * 2 / 3);

        foreach (var (text, count) in counts)
        {
            if (count >= threshold)
                repeated.Add(text);
        }

        return repeated;
    }

    #endregion

    #region Metadata

    private sealed record RawMetadata(
        string? Title, string? Author, string? Subject, string? Keywords,
        string? Creator, string? Producer, string? Language,
        DateTimeOffset? Created, DateTimeOffset? Modified,
        bool DisplaysTitle, bool IsEncrypted, bool ClaimsPdfUa, double Version, long FileSize);

    private static RawMetadata ReadMetadata(string filePath, PigDocument pig, List<string> warnings)
    {
        string? language = null;
        bool displaysTitle = false;
        bool claimsPdfUa = false;

        try
        {
            var catalog = pig.Structure.Catalog.CatalogDictionary;

            if (catalog.TryGet(NameToken.Lang, out StringToken? lang))
                language = lang?.Data;

            if (catalog.TryGet(NameToken.Create("ViewerPreferences"), out DictionaryToken? preferences)
                && preferences is not null
                && preferences.TryGet(NameToken.Create("DisplayDocTitle"), out BooleanToken? display))
            {
                displaysTitle = display?.Data ?? false;
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"The document's catalog could not be fully read: {ex.Message}");
        }

        try
        {
            if (pig.TryGetXmpMetadata(out var xmp))
            {
                string xml = xmp.GetXDocument().ToString();
                claimsPdfUa = xml.Contains("pdfuaid", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // XMP is optional and frequently malformed. Its absence tells us nothing either way.
        }

        var information = pig.Information;
        long size = 0;

        try { size = new FileInfo(filePath).Length; }
        catch { /* Size is a nicety, not worth failing a load for. */ }

        return new RawMetadata(
            Clean(information.Title),
            Clean(information.Author),
            Clean(information.Subject),
            Clean(information.Keywords),
            Clean(information.Creator),
            Clean(information.Producer),
            Clean(language),
            information.GetCreatedDateTimeOffset(),
            information.GetModifiedDateTimeOffset(),
            displaysTitle,
            pig.IsEncrypted,
            claimsPdfUa,
            pig.Version,
            size);

        static string? Clean(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static void CopyMetadata(DocumentMetadata target, RawMetadata source)
    {
        target.Title = source.Title;
        target.Author = source.Author;
        target.Subject = source.Subject;
        target.Keywords = source.Keywords;
        target.Creator = source.Creator;
        target.Producer = source.Producer;
        target.Language = source.Language;
        target.CreatedAt = source.Created;
        target.ModifiedAt = source.Modified;
        target.DisplaysDocumentTitle = source.DisplaysTitle;
        target.IsEncrypted = source.IsEncrypted;
        target.ClaimsPdfUaConformance = source.ClaimsPdfUa;
        target.PdfVersion = source.Version;
        target.FileSizeBytes = source.FileSize;
    }

    #endregion

    #region Links

    /// <summary>
    /// Reads a page's links.
    ///
    /// Link ANNOTATIONS are the source of truth, not PdfPig's GetHyperlinks(). That helper returns
    /// only URI-action links that have extractable text sitting under their rectangle, so it
    /// silently omits internal jumps, links drawn over an image, and links over a blank area. A
    /// link the reader cannot reach is a link that does not exist for them, so the complete set is
    /// enumerated here and the link text is recovered separately.
    /// </summary>
    private static void AddLinks(Page page, PageElement pageElement, List<string> warnings)
    {
        IReadOnlyList<UglyToad.PdfPig.Annotations.Annotation> annotations;

        try
        {
            annotations = page.GetAnnotations()
                .Where(a => a.Type == UglyToad.PdfPig.Annotations.AnnotationType.Link)
                .ToList();
        }
        catch (Exception ex)
        {
            warnings.Add($"Page {page.Number}: links could not be read ({ex.Message}).");
            return;
        }

        if (annotations.Count == 0)
            return;

        // GetHyperlinks does recover link text well where it applies, so its results are used to
        // supply text, matched by position, rather than being ignored entirely.
        var textByPosition = new List<(PageRegion Bounds, string Text)>();

        try
        {
            foreach (var hyperlink in page.GetHyperlinks())
            {
                if (!string.IsNullOrWhiteSpace(hyperlink.Text))
                {
                    textByPosition.Add((
                        new PageRegion(hyperlink.Bounds.Left, hyperlink.Bounds.Bottom,
                            hyperlink.Bounds.Right, hyperlink.Bounds.Top),
                        hyperlink.Text.Trim()));
                }
            }
        }
        catch
        {
            // Text recovery is a bonus; the links themselves are already in hand.
        }

        foreach (var annotation in annotations)
        {
            var bounds = new PageRegion(
                annotation.Rectangle.Left, annotation.Rectangle.Bottom,
                annotation.Rectangle.Right, annotation.Rectangle.Top);

            var (kind, target, targetPage) = ClassifyAction(annotation.Action);

            string text = FindTextForBounds(textByPosition, bounds)
                ?? RecoverAnchoredText(page, bounds)
                ?? string.Empty;

            string? contents = annotation.Content?.Trim();

            var link = new LinkElement(page.Number, text, kind, target)
            {
                Description = string.IsNullOrWhiteSpace(contents) ? null : contents,
                TargetPage = targetPage,
            };

            link.Bounds = bounds;
            pageElement.AddChild(link);
        }
    }

    private static string? FindTextForBounds(
        List<(PageRegion Bounds, string Text)> candidates, PageRegion bounds)
    {
        foreach (var (candidateBounds, text) in candidates)
        {
            bool overlaps = candidateBounds.Left < bounds.Right
                && candidateBounds.Right > bounds.Left
                && candidateBounds.Bottom < bounds.Top
                && candidateBounds.Top > bounds.Bottom;

            if (overlaps)
                return text;
        }

        return null;
    }

    /// <summary>
    /// Works out where a link actually goes, so the user is told "web address" or "another page in
    /// this document" rather than having a raw string read at them.
    /// </summary>
    private static (LinkTargetKind Kind, string Target, int? TargetPage) ClassifyAction(
        UglyToad.PdfPig.Actions.PdfAction? action)
    {
        switch (action)
        {
            case UglyToad.PdfPig.Actions.UriAction uri:
                return ClassifyUri(uri.Uri);

            case UglyToad.PdfPig.Actions.GoToAction goTo:
                return (LinkTargetKind.InternalDestination,
                    $"page {goTo.Destination.PageNumber}",
                    goTo.Destination.PageNumber);

            case UglyToad.PdfPig.Actions.GoToRAction remote:
                return (LinkTargetKind.ExternalFile, remote.Filename ?? string.Empty, null);

            case UglyToad.PdfPig.Actions.GoToEAction embedded:
                return (LinkTargetKind.EmbeddedFile, embedded.FileSpecification ?? string.Empty, null);

            case UglyToad.PdfPig.Actions.LaunchAction launch:
                return (LinkTargetKind.ExternalFile, launch.FileName ?? string.Empty, null);

            // Named and JavaScript actions are things this editor will not carry out. They are
            // still surfaced as links so the user knows something is there, with a kind that makes
            // the refusal explainable rather than leaving the key doing nothing.
            case UglyToad.PdfPig.Actions.JavaScriptAction:
                return (LinkTargetKind.UnsupportedAction, "a script stored in the document", null);

            case null:
                return (LinkTargetKind.Unknown, string.Empty, null);

            default:
                return (LinkTargetKind.UnsupportedAction, action.Type.ToString(), null);
        }
    }

    private static (LinkTargetKind Kind, string Target, int? TargetPage) ClassifyUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return (LinkTargetKind.Unknown, string.Empty, null);

        string trimmed = uri.Trim();

        if (trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return (LinkTargetKind.Email, trimmed[7..], null);

        if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return (LinkTargetKind.ExternalFile, trimmed, null);

        if (trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            return (LinkTargetKind.UnsupportedAction, trimmed, null);

        return (LinkTargetKind.WebUrl, trimmed, null);
    }

    #endregion

    #region Annotations

    private static void AddAnnotations(Page page, PageElement pageElement, List<string> warnings)
    {
        IReadOnlyList<UglyToad.PdfPig.Annotations.Annotation> annotations;

        try
        {
            annotations = page.GetAnnotations().ToList();
        }
        catch (Exception ex)
        {
            warnings.Add($"Page {page.Number}: comments could not be read ({ex.Message}).");
            return;
        }

        foreach (var annotation in annotations)
        {
            var kind = MapAnnotationKind(annotation.Type);

            // Widget annotations are form fields and Link annotations are links; both are added
            // through their own paths, and adding them again here would list every form field twice
            // in the comments panel.
            if (kind is null)
                continue;

            string contents = annotation.Content?.Trim() ?? string.Empty;

            var element = new AnnotationElement(page.Number, kind.Value, contents)
            {
                Author = ReadAnnotationAuthor(annotation),
                SourceObjectId = annotation.Name,
            };

            element.Bounds = new PageRegion(
                annotation.Rectangle.Left, annotation.Rectangle.Bottom,
                annotation.Rectangle.Right, annotation.Rectangle.Top);

            // A highlight or strike-through means nothing without the text it covers. Recovering
            // it turns "highlight" into "highlight over 'the deadline is 31 March'".
            element.AnchoredText = RecoverAnchoredText(page, element.Bounds);

            pageElement.AddChild(element);
        }
    }

    private static string? ReadAnnotationAuthor(UglyToad.PdfPig.Annotations.Annotation annotation)
    {
        try
        {
            if (annotation.AnnotationDictionary.TryGet(NameToken.Create("T"), out StringToken? author))
            {
                string? value = author?.Data?.Trim();
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }
        catch
        {
            // The author is optional.
        }

        return null;
    }

    /// <summary>
    /// Finds the page text lying under an annotation's rectangle. Used to say what a markup
    /// annotation is about, which is the only thing that makes a highlight meaningful when heard.
    /// </summary>
    private static string? RecoverAnchoredText(Page page, PageRegion region)
    {
        if (region.IsEmpty)
            return null;

        try
        {
            var covered = page.GetWords()
                .Where(w =>
                    w.BoundingBox.Left < region.Right &&
                    w.BoundingBox.Right > region.Left &&
                    w.BoundingBox.Bottom < region.Top &&
                    w.BoundingBox.Top > region.Bottom)
                .OrderByDescending(w => Math.Round(w.BoundingBox.Bottom, 1))
                .ThenBy(w => w.BoundingBox.Left)
                .Select(w => w.Text)
                .Take(60)
                .ToList();

            if (covered.Count == 0)
                return null;

            string text = string.Join(" ", covered).Trim();
            return text.Length > 0 ? text : null;
        }
        catch
        {
            return null;
        }
    }

    private static AnnotationKind? MapAnnotationKind(UglyToad.PdfPig.Annotations.AnnotationType type) => type switch
    {
        UglyToad.PdfPig.Annotations.AnnotationType.Text => AnnotationKind.Comment,
        UglyToad.PdfPig.Annotations.AnnotationType.Highlight => AnnotationKind.Highlight,
        UglyToad.PdfPig.Annotations.AnnotationType.Underline => AnnotationKind.Underline,
        UglyToad.PdfPig.Annotations.AnnotationType.StrikeOut => AnnotationKind.StrikeOut,
        UglyToad.PdfPig.Annotations.AnnotationType.Squiggly => AnnotationKind.Squiggly,
        UglyToad.PdfPig.Annotations.AnnotationType.FreeText => AnnotationKind.FreeText,
        UglyToad.PdfPig.Annotations.AnnotationType.Stamp => AnnotationKind.Stamp,
        UglyToad.PdfPig.Annotations.AnnotationType.Ink => AnnotationKind.Ink,
        UglyToad.PdfPig.Annotations.AnnotationType.FileAttachment => AnnotationKind.FileAttachment,

        // Handled elsewhere: widgets become form fields, links become link elements, popups belong
        // to the annotation they accompany.
        UglyToad.PdfPig.Annotations.AnnotationType.Widget => null,
        UglyToad.PdfPig.Annotations.AnnotationType.Link => null,
        UglyToad.PdfPig.Annotations.AnnotationType.Popup => null,

        _ => null,
    };

    #endregion

    #region Outline

    private static void ReadOutline(PigDocument pig, PdfDocumentModel document, List<string> warnings)
    {
        try
        {
            if (!pig.TryGetBookmarks(out var bookmarks, allowContainerNode: true) || bookmarks is null)
                return;

            foreach (var node in bookmarks.Roots)
            {
                var converted = ConvertBookmark(node);
                if (converted is not null)
                    document.Outline.Add(converted);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"The document's bookmarks could not be read: {ex.Message}");
        }
    }

    private static OutlineNode? ConvertBookmark(BookmarkNode node)
    {
        int? targetPage = node switch
        {
            DocumentBookmarkNode document => document.PageNumber,
            _ => null,
        };

        string? external = node switch
        {
            UriBookmarkNode uri => uri.Uri,
            ExternalBookmarkNode file => file.FileName,
            _ => null,
        };

        var converted = new OutlineNode(node.Title, node.Level, targetPage)
        {
            ExternalTarget = external,
        };

        foreach (var child in node.Children)
        {
            var convertedChild = ConvertBookmark(child);
            if (convertedChild is not null)
                converted.AddChild(convertedChild);
        }

        return converted;
    }

    #endregion

    #region Form fields
    // Delegated to FormFieldReader, which is substantial enough to warrant its own file: recovering
    // usable labels for unlabelled fields is most of the work, and it is the single thing that
    // decides whether a form is fillable by ear.

    private static void AddFormFields(PigDocument pig, PdfDocumentModel document, List<string> warnings)
    {
        try
        {
            if (!pig.TryGetForm(out var form) || form is null)
                return;

            FormFieldReader.ReadInto(form, pig, document, warnings);
        }
        catch (Exception ex)
        {
            warnings.Add($"The document's form fields could not be read: {ex.Message}");
        }
    }

    #endregion
}

#endregion
