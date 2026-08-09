using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Model.Forms;

namespace AccessiblePdfEditor.Model;

// =====================================================================================
//  PdfDocumentModel.cs
//
//  A loaded document, in the form the rest of the application works with.
//
//  Nothing above this layer touches PdfPig or PDFsharp. The loader turns a file into one of
//  these; navigation, search, the auditor, the editing commands and the whole UI operate on
//  it alone. That boundary is what lets the model be tested by constructing documents by
//  hand, and it is what would let the PDF libraries be swapped without touching anything
//  the user can see.
//
//  The flattened element list is the other important thing here. The tree gives structure;
//  the flat list in reading order gives "what comes next", which is the question every
//  navigation command is really asking. Both are needed, and keeping them in step is this
//  class's responsibility rather than each caller's.
// =====================================================================================

#region Document metadata — the facts about a document, several of which are accessibility features

/// <summary>
/// A document's descriptive metadata. Several of these fields are accessibility requirements
/// rather than niceties, and the auditor checks them.
/// </summary>
public sealed class DocumentMetadata
{
    /// <summary>
    /// The document's title. More than a label: when a document has a title and is set to display
    /// it, a screen reader announces the title on opening instead of the filename. The difference
    /// between hearing "Annual Report 2026" and "AR26-final-v3-USE-THIS.pdf" is the whole point.
    /// </summary>
    public string? Title { get; set; }

    public string? Author { get; set; }

    public string? Subject { get; set; }

    public string? Keywords { get; set; }

    public string? Creator { get; set; }

    public string? Producer { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public DateTimeOffset? ModifiedAt { get; set; }

    /// <summary>
    /// The document's language as a BCP 47 tag. Without it a screen reader reads the document in
    /// whatever voice it happens to be using, so a French document read by an English voice becomes
    /// unintelligible. One of the cheapest and highest-value repairs available.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Whether the viewer is told to show the title rather than the filename. Setting a title
    /// without setting this achieves nothing, which is why they are checked together.
    /// </summary>
    public bool DisplaysDocumentTitle { get; set; }

    /// <summary>The PDF version the file declares.</summary>
    public double PdfVersion { get; set; }

    /// <summary>Whether the file is encrypted.</summary>
    public bool IsEncrypted { get; set; }

    /// <summary>
    /// Whether the document's permission flags allow extracting text for accessibility. A document
    /// that forbids it is stating that it should not be readable by a screen reader, which is worth
    /// reporting plainly rather than silently working around.
    /// </summary>
    public bool AllowsAccessibilityExtraction { get; set; } = true;

    /// <summary>Whether the file claims conformance with PDF/UA, the accessibility standard.</summary>
    public bool ClaimsPdfUaConformance { get; set; }

    /// <summary>The file's size in bytes.</summary>
    public long FileSizeBytes { get; set; }
}

#endregion

#region Outline node — a bookmark in the document's own table of contents

/// <summary>
/// One entry in the document's outline. The author's own map of the document, and usually the
/// fastest way to get somewhere in a long one — often better than the headings, because a human
/// chose what went in it.
/// </summary>
public sealed class OutlineNode
{
    public OutlineNode(string title, int level, int? targetPage)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "untitled bookmark" : title.Trim();
        Level = level;
        TargetPage = targetPage;
    }

    /// <summary>The bookmark's text.</summary>
    public string Title { get; }

    /// <summary>Its depth in the outline, starting at zero.</summary>
    public int Level { get; }

    /// <summary>The page it points at, when it points inside this document.</summary>
    public int? TargetPage { get; }

    /// <summary>An external destination, for bookmarks that leave the document.</summary>
    public string? ExternalTarget { get; init; }

    private readonly List<OutlineNode> _children = [];

    /// <summary>Nested bookmarks.</summary>
    public IReadOnlyList<OutlineNode> Children => _children;

    public void AddChild(OutlineNode child) => _children.Add(child);

    /// <summary>This node, then every descendant, depth first.</summary>
    public IEnumerable<OutlineNode> SelfAndDescendants()
    {
        yield return this;

        foreach (var child in _children)
        {
            foreach (var descendant in child.SelfAndDescendants())
                yield return descendant;
        }
    }

    public override string ToString() => $"{new string(' ', Level * 2)}{Title} → p{TargetPage}";
}

#endregion

#region PdfDocumentModel — the loaded document

/// <summary>A loaded PDF, in the application's own vocabulary.</summary>
public sealed class PdfDocumentModel
{
    #region Construction and identity

    private readonly List<DocumentElement> _readingOrder = [];

    public PdfDocumentModel(string filePath, DocumentRootElement root)
    {
        FilePath = filePath;
        Root = root;
        Metadata = new DocumentMetadata();
    }

    /// <summary>The file this document was loaded from.</summary>
    public string FilePath { get; internal set; }

    /// <summary>The filename alone, for announcements and the title bar.</summary>
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>The root of the element tree.</summary>
    public DocumentRootElement Root { get; }

    /// <summary>The document's metadata.</summary>
    public DocumentMetadata Metadata { get; }

    /// <summary>How much real accessibility structure this document carries.</summary>
    public TaggedStatus TaggedStatus { get; internal set; } = TaggedStatus.Unknown;

    /// <summary>Where the document is in its lifecycle.</summary>
    public DocumentLoadState LoadState { get; internal set; } = DocumentLoadState.Loaded;

    /// <summary>Whether there are unsaved edits.</summary>
    public bool HasUnsavedChanges { get; internal set; }

    /// <summary>
    /// Problems encountered while loading that did not prevent it. Announced on request, because a
    /// document that loaded with three fonts missing is one whose text may be wrong, and the user
    /// should be able to find that out rather than puzzling over garbled words.
    /// </summary>
    public List<string> LoadWarnings { get; } = [];

    #endregion

    #region Pages

    /// <summary>The document's pages, in order.</summary>
    public IReadOnlyList<PageElement> Pages => Root.Children.OfType<PageElement>().ToList();

    /// <summary>The number of pages.</summary>
    public int PageCount => Root.Children.Count(c => c.Kind == ElementKind.Page);

    /// <summary>The page with a given one-based number, or null.</summary>
    public PageElement? GetPage(int pageNumber) =>
        Root.Children.OfType<PageElement>().FirstOrDefault(p => p.PageNumber == pageNumber);

    #endregion

    #region The flattened reading order
    // Built once after loading and rebuilt after any edit that changes the tree. Every element's
    // ReadingOrder index is assigned here, so an element always knows where it sits in the whole
    // document without having to walk back up to the root and count.

    /// <summary>Every element in the document, in reading order.</summary>
    public IReadOnlyList<DocumentElement> ReadingOrder => _readingOrder;

    /// <summary>
    /// Rebuilds the flat reading order from the tree and renumbers every element. Called after
    /// loading and after any structural edit; navigation positions are held by element id rather
    /// than by index precisely so that this can be called without losing the user's place.
    /// </summary>
    public void RebuildReadingOrder()
    {
        _readingOrder.Clear();

        int index = 0;
        foreach (var element in Root.SelfAndDescendants())
        {
            element.ReadingOrder = index++;
            _readingOrder.Add(element);
        }
    }

    /// <summary>The element with a given id, or null.</summary>
    public DocumentElement? FindById(int id) => _readingOrder.FirstOrDefault(e => e.Id == id);

    #endregion

    #region Typed views over the content
    // Computed on demand rather than cached, because every one of them changes when the document is
    // edited and a stale list of form fields is worse than a slightly slower one. Documents that
    // are large enough for this to matter are large enough that the loader has already dominated.

    /// <summary>Every form field, in tab order.</summary>
    public IReadOnlyList<PdfFormField> FormFields =>
        _readingOrder.OfType<PdfFormField>().OrderBy(f => f.TabOrder).ToList();

    /// <summary>Every link.</summary>
    public IReadOnlyList<LinkElement> Links => _readingOrder.OfType<LinkElement>().ToList();

    /// <summary>Every figure.</summary>
    public IReadOnlyList<FigureElement> Figures => _readingOrder.OfType<FigureElement>().ToList();

    /// <summary>Every table.</summary>
    public IReadOnlyList<TableElement> Tables => _readingOrder.OfType<TableElement>().ToList();

    /// <summary>Every heading.</summary>
    public IReadOnlyList<HeadingElement> Headings => _readingOrder.OfType<HeadingElement>().ToList();

    /// <summary>
    /// Every annotation that is not a popup. Popups are excluded because they duplicate the comment
    /// they belong to, and listing both would double the length of every comment list.
    /// </summary>
    public IReadOnlyList<AnnotationElement> Annotations =>
        _readingOrder.OfType<AnnotationElement>()
            .Where(a => a.AnnotationKind != AnnotationKind.Popup)
            .ToList();

    /// <summary>Every embedded file.</summary>
    public IReadOnlyList<AttachmentElement> Attachments => _readingOrder.OfType<AttachmentElement>().ToList();

    /// <summary>
    /// Annotations the user has deleted but which are still in the file on disk.
    ///
    /// A deleted annotation is taken out of the element tree at once, so the document reads as the
    /// user expects immediately. But the save has to know which ones to remove from the PDF, and by
    /// then they are no longer anywhere in the tree to be found. So they are kept here — deletion is
    /// not "forget it", it is "remember to remove it".
    /// </summary>
    public IReadOnlyList<AnnotationElement> DeletedAnnotations => _deletedAnnotations;

    private readonly List<AnnotationElement> _deletedAnnotations = [];

    /// <summary>Records that an annotation was deleted, so the next save removes it from the file.</summary>
    internal void RecordAnnotationDeleted(AnnotationElement annotation)
    {
        // One that was never written to the file has nothing to delete on disk; dropping it from the
        // tree is the whole of the job.
        if (annotation.IsUnsaved)
            return;

        if (!_deletedAnnotations.Contains(annotation))
            _deletedAnnotations.Add(annotation);
    }

    /// <summary>Cancels a recorded deletion, when the user undoes it.</summary>
    internal void RestoreDeletedAnnotation(AnnotationElement annotation) =>
        _deletedAnnotations.Remove(annotation);

    #endregion

    #region Outline

    /// <summary>The document's own bookmarks, when it has any.</summary>
    public List<OutlineNode> Outline { get; } = [];

    /// <summary>Whether the document has bookmarks.</summary>
    public bool HasOutline => Outline.Count > 0;

    /// <summary>Every bookmark, flattened.</summary>
    public IEnumerable<OutlineNode> FlatOutline => Outline.SelectMany(n => n.SelfAndDescendants());

    #endregion

    #region Summaries — what the user is told when a document opens
    // The opening announcement is the single most important thing this application says. It has to
    // answer, in one breath: what is this, how big is it, can I actually read it, and is there
    // anything I should know before I start. Anything longer than that gets talked over.

    /// <summary>
    /// The announcement made when a document finishes loading. Leads with the tagging state,
    /// because that determines whether everything the user is about to hear can be trusted.
    /// </summary>
    public string BuildOpeningAnnouncement()
    {
        var parts = new List<string>(6);

        string name = Metadata.Title is { Length: > 0 } title ? title : FileName;
        parts.Add(name);

        parts.Add(PageCount == 1 ? "1 page" : $"{PageCount} pages");

        parts.Add(TaggedStatus switch
        {
            TaggedStatus.FullyTagged =>
                "fully tagged, so headings, lists and tables can be trusted",
            TaggedStatus.PartiallyTagged =>
                "partly tagged, so some structure is the document's own and some is worked out from the layout",
            TaggedStatus.Untagged =>
                "not tagged, so all structure here is worked out from the page layout and may be wrong",
            TaggedStatus.ScannedWithoutText =>
                "this looks like a scan with no text behind it, so there is nothing to read aloud",
            _ => string.Empty,
        });

        int fields = FormFields.Count;
        if (fields > 0)
        {
            int needing = FormFields.Count(f => f.NeedsAttention);
            parts.Add(needing > 0
                ? $"{fields} form fields, {needing} still to fill in"
                : $"{fields} form fields");
        }

        int headings = Headings.Count;
        if (headings > 0)
            parts.Add($"{headings} headings");

        if (HasOutline)
            parts.Add($"{Outline.Count} bookmarks");

        int comments = Annotations.Count;
        if (comments > 0)
            parts.Add($"{comments} {(comments == 1 ? "comment" : "comments")}");

        if (!Metadata.AllowsAccessibilityExtraction)
            parts.Add("note: this document's permissions forbid extracting its text");

        return string.Join(". ", parts.Where(p => p.Length > 0)) + ".";
    }

    /// <summary>
    /// A one-line summary for the window title and the status bar. Kept short enough to be read at
    /// a glance and to fit on a braille display.
    /// </summary>
    public string BuildStatusLine()
    {
        string changed = HasUnsavedChanges ? " (edited)" : string.Empty;
        return $"{FileName}{changed} — {PageCount} pages, {TaggedStatusDescription}";
    }

    /// <summary>The tagging state in two or three words.</summary>
    public string TaggedStatusDescription => TaggedStatus switch
    {
        TaggedStatus.FullyTagged => "tagged",
        TaggedStatus.PartiallyTagged => "partly tagged",
        TaggedStatus.Untagged => "untagged",
        TaggedStatus.ScannedWithoutText => "scanned, no text",
        _ => "unknown structure",
    };

    #endregion

    #region Diagnostics

    public override string ToString() =>
        $"{FileName}: {PageCount} pages, {_readingOrder.Count} elements, {TaggedStatus}";

    #endregion
}

#endregion
