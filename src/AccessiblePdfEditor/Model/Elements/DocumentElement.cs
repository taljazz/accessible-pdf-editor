using System.Text;
using AccessiblePdfEditor.Model;

namespace AccessiblePdfEditor.Model.Elements;

// =====================================================================================
//  DocumentElement.cs
//
//  The abstract root of everything a document contains. Every heading, paragraph, list,
//  table, figure, link, annotation and form field in this application is a DocumentElement,
//  and almost all of the reader is written against this type rather than against any
//  concrete one.
//
//  The single most important thing in this file is the Describe template method. It fixes
//  the SHAPE of every spoken announcement in the whole application — role, then content,
//  then state, then position — while leaving each subclass to fill in its own parts. That
//  is what makes a table cell and a checkbox and a heading all sound like they belong to
//  the same program, and it is why adding a new element type cannot accidentally invent a
//  new announcement style.
// =====================================================================================

#region Page region — where an element sits on its page
// PDF user space puts the origin at the BOTTOM-left of the page with Y increasing upwards,
// which is the opposite of every screen coordinate system and the opposite of reading order.
// This struct stores the PDF convention faithfully and exposes the reading-order comparisons
// separately, so the flip happens in exactly one place instead of being re-derived (and
// occasionally got wrong) at each call site.

/// <summary>
/// A rectangle on a page, in PDF user space: origin at the bottom-left, Y increasing upwards,
/// units of 1/72 inch.
/// </summary>
public readonly record struct PageRegion(double Left, double Bottom, double Right, double Top)
{
    /// <summary>An empty region, used for elements that have no position of their own.</summary>
    public static PageRegion Empty => new(0, 0, 0, 0);

    public double Width => Right - Left;

    public double Height => Top - Bottom;

    public bool IsEmpty => Width <= 0 && Height <= 0;

    /// <summary>Vertical midpoint. Used to decide whether two elements share a line.</summary>
    public double CentreY => (Top + Bottom) / 2.0;

    /// <summary>Horizontal midpoint.</summary>
    public double CentreX => (Left + Right) / 2.0;

    /// <summary>
    /// True when this region and <paramref name="other"/> overlap vertically enough to be read as
    /// the same line. Used when recovering table rows and multi-column layouts from an untagged
    /// page, where "same line" is a judgement call rather than an exact match.
    /// </summary>
    public bool SharesLineWith(PageRegion other, double tolerance = 0.5)
    {
        double overlap = Math.Min(Top, other.Top) - Math.Max(Bottom, other.Bottom);
        double shorter = Math.Min(Height, other.Height);
        return shorter > 0 && overlap > shorter * tolerance;
    }

    /// <summary>The smallest region containing both this one and <paramref name="other"/>.</summary>
    public PageRegion Union(PageRegion other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return new PageRegion(
            Math.Min(Left, other.Left),
            Math.Min(Bottom, other.Bottom),
            Math.Max(Right, other.Right),
            Math.Max(Top, other.Top));
    }
}

#endregion

#region DocumentElement — the abstract base every piece of content derives from

/// <summary>
/// Base class for everything a document contains. Holds tree position, page position, language and
/// reading order, and defines the announcement template that every subclass fills in.
/// </summary>
public abstract class DocumentElement
{
    #region Construction and tree state
    // Children are kept private and exposed read-only so the tree can never be corrupted from
    // outside: attaching a child is the only way to add one, and it always fixes up the parent
    // link at the same time. Reading order is assigned once by the loader, after the whole tree
    // exists, because it can only be known relative to everything else.

    private readonly List<DocumentElement> _children = [];

    private static int _nextId;

    protected DocumentElement(int pageNumber)
    {
        PageNumber = pageNumber;
        Id = Interlocked.Increment(ref _nextId);
    }

    /// <summary>
    /// Identity that survives the tree being rebuilt after an edit. Navigation remembers where the
    /// user was by id, so re-analysing a document does not silently move the reading position.
    /// </summary>
    public int Id { get; }

    /// <summary>One-based page number. Zero for elements that belong to the document as a whole.</summary>
    public int PageNumber { get; internal set; }

    /// <summary>Where this element sits on its page, in PDF user space.</summary>
    public PageRegion Bounds { get; internal set; } = PageRegion.Empty;

    /// <summary>The element that contains this one, or null for the document root.</summary>
    public DocumentElement? Parent { get; private set; }

    /// <summary>Contained elements, in reading order.</summary>
    public IReadOnlyList<DocumentElement> Children => _children;

    /// <summary>
    /// Position in the flattened reading order of the whole document. Assigned by the loader once
    /// the tree is complete; this is the number that makes "next element" mean anything.
    /// </summary>
    public int ReadingOrder { get; internal set; } = -1;

    /// <summary>How deep in the tree this element sits. The root is zero.</summary>
    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    /// <summary>Attaches a child and takes ownership of its parent link.</summary>
    public void AddChild(DocumentElement child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (!AcceptsChildren)
            throw new InvalidOperationException($"{Kind} elements cannot contain children.");

        child.Parent?.RemoveChild(child);
        child.Parent = this;
        _children.Add(child);
    }

    /// <summary>Detaches a child. Returns false when it was not a child of this element.</summary>
    public bool RemoveChild(DocumentElement child)
    {
        if (!_children.Remove(child))
            return false;

        child.Parent = null;
        return true;
    }

    /// <summary>
    /// Moves a child to a new index among its siblings. This is the tree operation behind fixing
    /// reading order, which is one of the most valuable repairs this editor can make.
    /// </summary>
    public bool MoveChild(DocumentElement child, int newIndex)
    {
        int current = _children.IndexOf(child);
        if (current < 0) return false;

        newIndex = Math.Clamp(newIndex, 0, _children.Count - 1);
        if (newIndex == current) return false;

        _children.RemoveAt(current);
        _children.Insert(newIndex, child);
        return true;
    }

    #endregion

    #region Identity and content — what each subclass must declare
    // Kind is abstract so no element can exist without saying what it is. Text is virtual with an
    // empty default because plenty of elements (a table, a list, a page) genuinely have no text of
    // their own and get theirs from their children.

    /// <summary>What this element is. The discriminator for the whole hierarchy.</summary>
    public abstract ElementKind Kind { get; }

    /// <summary>This element's own text, not including its children's.</summary>
    public virtual string Text => string.Empty;

    /// <summary>
    /// This element's text together with all its descendants', in reading order. What continuous
    /// reading and search actually operate on.
    /// </summary>
    public virtual string FullText
    {
        get
        {
            if (_children.Count == 0)
                return Text;

            var builder = new StringBuilder();
            if (Text.Length > 0)
                builder.Append(Text);

            foreach (var child in _children)
            {
                string childText = child.FullText;
                if (childText.Length == 0)
                    continue;

                if (builder.Length > 0)
                    builder.Append(' ');
                builder.Append(childText);
            }

            return builder.ToString();
        }
    }

    /// <summary>Whether this element may contain other elements.</summary>
    public virtual bool AcceptsChildren => true;

    /// <summary>
    /// Whether continuous reading includes this element. False for page furniture, which is correct
    /// to skip when reading a document straight through but must still be reachable deliberately —
    /// skipping is not the same as hiding.
    /// </summary>
    public virtual bool IsReadInContinuousReading => true;

    /// <summary>
    /// The language of this element's text as a BCP 47 tag, when it differs from the document's.
    /// A screen reader switches voice on this, which is the difference between a French quotation
    /// being read as French and being read as mangled English.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>Base writing direction, inherited from the parent unless set explicitly.</summary>
    public TextDirection Direction { get; set; } = TextDirection.Inherit;

    /// <summary>
    /// Text that should replace this element's extracted characters when read aloud, from the PDF
    /// /ActualText attribute. Exists for content whose glyphs do not match its meaning — a
    /// ligature, a drop cap split across two draw operations, a word broken by a decorative rule.
    /// </summary>
    public string? ActualText { get; set; }

    /// <summary>
    /// Whether this element's structure came from real tags in the file rather than from layout
    /// guesswork. Announced on request, and used by the auditor: an inferred heading is a finding,
    /// a tagged one is not.
    /// </summary>
    public bool IsFromRealTags { get; internal set; }

    #endregion

    #region The announcement template — the one place the shape of speech is decided
    // This is the template method the whole application depends on. Describe fixes the order of
    // the parts and how they are joined; the four protected members below let each subclass supply
    // its own. Subclasses override the parts, never Describe itself, so every element in the
    // program is announced in the same order no matter who wrote it.
    //
    // The order is deliberate and matches what screen reader users already expect from the web:
    //   role first  — so you know what you have landed on before hearing its content
    //   content     — the actual text
    //   state       — required, invalid, visited, unlabelled
    //   position    — "3 of 12", page number
    // Verbosity trims from the ends inwards: Terse keeps content alone, Detailed keeps everything.

    /// <summary>
    /// Builds the spoken announcement for this element. This method is deliberately not virtual:
    /// subclasses shape the announcement by overriding <see cref="DescribeRole"/>,
    /// <see cref="DescribeContent"/>, <see cref="DescribeState"/> and <see cref="DescribePosition"/>,
    /// which keeps every announcement in the application in the same order.
    /// </summary>
    public string Describe(VerbosityLevel verbosity)
    {
        var parts = new List<string>(4);

        if (verbosity > VerbosityLevel.Terse)
        {
            string role = DescribeRole(verbosity);
            if (role.Length > 0)
                parts.Add(role);
        }

        string content = DescribeContent(verbosity);
        if (content.Length > 0)
            parts.Add(content);

        string state = DescribeState(verbosity);
        if (state.Length > 0)
            parts.Add(state);

        if (verbosity == VerbosityLevel.Detailed)
        {
            string position = DescribePosition(verbosity);
            if (position.Length > 0)
                parts.Add(position);
        }

        // Terse verbosity drops the role, so an element with no text of its own would otherwise
        // announce as silence. Falling back to the role keeps every element audible.
        if (parts.Count == 0)
        {
            string fallback = DescribeRole(VerbosityLevel.Normal);
            return fallback.Length > 0 ? fallback : "empty";
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// What this element is, in words — "heading level 2", "list with 4 items", "edit box".
    /// Defaults to a plain reading of <see cref="Kind"/>; most subclasses say something better.
    /// </summary>
    protected virtual string DescribeRole(VerbosityLevel verbosity) => DefaultRoleName(Kind);

    /// <summary>
    /// The element's content as it should be heard. The only part every subclass must supply,
    /// because an element with nothing to say has no reason to exist.
    /// </summary>
    protected abstract string DescribeContent(VerbosityLevel verbosity);

    /// <summary>
    /// Anything true about this element right now that changes what the user should do — required,
    /// invalid, already visited, missing its alt text. Empty for most content elements.
    /// </summary>
    protected virtual string DescribeState(VerbosityLevel verbosity) => string.Empty;

    /// <summary>
    /// Where this element sits — "page 4", "item 3 of 9". Only spoken at Detailed verbosity, since
    /// hearing it on every element quickly becomes noise.
    /// </summary>
    protected virtual string DescribePosition(VerbosityLevel verbosity) =>
        PageNumber > 0 ? $"page {PageNumber}" : string.Empty;

    /// <summary>
    /// A short, human-readable name for a kind. Shared by the default role description and by the
    /// UI's element lists so that a "figure" is never a "Figure" in one place and an "image" in
    /// another — one word per concept, everywhere.
    /// </summary>
    public static string DefaultRoleName(ElementKind kind) => kind switch
    {
        ElementKind.Document => "document",
        ElementKind.Page => "page",
        ElementKind.Section => "section",
        ElementKind.Heading => "heading",
        ElementKind.Paragraph => "paragraph",
        ElementKind.List => "list",
        ElementKind.ListItem => "list item",
        ElementKind.Table => "table",
        ElementKind.TableRow => "row",
        ElementKind.TableCell => "cell",
        ElementKind.Figure => "figure",
        ElementKind.Caption => "caption",
        ElementKind.BlockQuote => "quotation",
        ElementKind.Code => "code",
        ElementKind.Note => "note",
        ElementKind.TableOfContents => "table of contents",
        ElementKind.Link => "link",
        ElementKind.FormField => "form field",
        ElementKind.Annotation => "annotation",
        ElementKind.Attachment => "attachment",
        ElementKind.Artifact => "page furniture",
        _ => "text",
    };

    #endregion

    #region Tree queries — used by navigation, search and the auditor
    // Written once here so that no caller has to hand-roll a tree walk. Everything is lazy so
    // that "find the next heading" stops as soon as it finds one rather than materialising the
    // whole document first, which matters on a long report.

    /// <summary>This element, then every descendant, in reading order.</summary>
    public IEnumerable<DocumentElement> SelfAndDescendants()
    {
        yield return this;

        foreach (var child in _children)
        {
            foreach (var descendant in child.SelfAndDescendants())
                yield return descendant;
        }
    }

    /// <summary>Every descendant, in reading order, excluding this element.</summary>
    public IEnumerable<DocumentElement> Descendants()
    {
        foreach (var child in _children)
        {
            foreach (var element in child.SelfAndDescendants())
                yield return element;
        }
    }

    /// <summary>This element's ancestors, nearest first, up to the root.</summary>
    public IEnumerable<DocumentElement> Ancestors()
    {
        for (var current = Parent; current is not null; current = current.Parent)
            yield return current;
    }

    /// <summary>
    /// The nearest ancestor of a given type, or null. This is how a table cell finds the table it
    /// belongs to in order to announce "row 3, column 2 of 5".
    /// </summary>
    public T? NearestAncestor<T>() where T : DocumentElement =>
        Ancestors().OfType<T>().FirstOrDefault();

    /// <summary>This element's index among its siblings, or -1 when it has no parent.</summary>
    public int IndexAmongSiblings => Parent?._children.IndexOf(this) ?? -1;

    /// <summary>
    /// Recomputes <see cref="Bounds"/> from this element's children. Container elements have no
    /// geometry of their own, but navigation and the "where am I on the page" announcement need
    /// one, so it is derived from what they contain.
    /// </summary>
    public void RecalculateBoundsFromChildren()
    {
        if (_children.Count == 0)
            return;

        var union = PageRegion.Empty;
        foreach (var child in _children)
        {
            child.RecalculateBoundsFromChildren();
            union = union.Union(child.Bounds);
        }

        if (Bounds.IsEmpty)
            Bounds = union;
    }

    #endregion

    #region Diagnostics

    public override string ToString() =>
        $"{Kind}#{Id} p{PageNumber} order={ReadingOrder} \"{Truncate(FullText, 40)}\"";

    /// <summary>
    /// Shortens text for logs and for list views, breaking at a word boundary so a truncated line
    /// still reads as words rather than stopping mid-syllable when spoken.
    /// </summary>
    protected static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;

        int cut = text.LastIndexOf(' ', Math.Min(maxLength, text.Length - 1));
        if (cut < maxLength / 2)
            cut = maxLength;

        return string.Concat(text.AsSpan(0, cut).TrimEnd(), "…");
    }

    #endregion
}

#endregion
