using AccessiblePdfEditor.Model;

namespace AccessiblePdfEditor.Model.Elements;

// =====================================================================================
//  TextElements.cs
//
//  Every element whose substance is a run of text: paragraphs, headings, captions, quotes,
//  code, notes and page furniture. They share an abstract TextElement parent that owns the
//  text itself and the typographic measurements the heuristic structure extractor needs.
//
//  Those measurements are not decoration. In an untagged PDF — which is most of them —
//  font size, weight and indentation are the ONLY evidence that a line is a heading rather
//  than a sentence, so they are carried on the element and kept after extraction so the
//  user can be told why something was treated as a heading, and correct it if it is wrong.
// =====================================================================================

#region TextElement — the abstract parent of everything made of words

/// <summary>
/// Base class for elements whose content is text. Owns the text and the typographic evidence used
/// to classify it when the document carries no tags of its own.
/// </summary>
public abstract class TextElement : DocumentElement
{
    #region Construction and text

    private string _text;

    protected TextElement(int pageNumber, string text)
        : base(pageNumber)
    {
        _text = NormaliseWhitespace(text);
    }

    /// <summary>
    /// The text, with the PDF /ActualText replacement taking precedence when one is present.
    /// That attribute exists precisely so a document can say "these glyphs should be read as this
    /// instead", and honouring it is the difference between reading a ligature as a word and
    /// reading it as a box.
    /// </summary>
    public override string Text => ActualText ?? _text;

    /// <summary>The extracted text, ignoring any /ActualText override. Used when editing the source.</summary>
    public string RawText => _text;

    /// <summary>Replaces the text. Used by editing commands, which is why it is not init-only.</summary>
    public void SetText(string text) => _text = NormaliseWhitespace(text);

    /// <summary>
    /// Collapses the runs of whitespace that text extraction produces at line breaks and column
    /// boundaries. Without this a screen reader pauses in the middle of sentences, which is one of
    /// the most common ways an otherwise readable PDF becomes tiring to listen to.
    /// </summary>
    protected static string NormaliseWhitespace(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        Span<char> buffer = text.Length <= 512 ? stackalloc char[text.Length] : new char[text.Length];
        int length = 0;
        bool lastWasSpace = false;

        foreach (char c in text)
        {
            // A soft hyphen is an invisible line-break hint. Extraction keeps it, and a screen
            // reader will happily read it as a hyphen in the middle of a word.
            if (c == '­')
                continue;

            bool isSpace = char.IsWhiteSpace(c);
            if (isSpace)
            {
                if (lastWasSpace || length == 0)
                    continue;

                buffer[length++] = ' ';
                lastWasSpace = true;
            }
            else
            {
                buffer[length++] = c;
                lastWasSpace = false;
            }
        }

        while (length > 0 && buffer[length - 1] == ' ')
            length--;

        return new string(buffer[..length]);
    }

    #endregion

    #region Typographic evidence — how an untagged document is understood
    // Populated by the extractors. On a tagged document these are still filled in where known, but
    // nothing depends on them; on an untagged one they are the entire basis for classification.

    /// <summary>Point size of the dominant font in this element.</summary>
    public double FontSize { get; init; }

    /// <summary>Name of the dominant font.</summary>
    public string? FontName { get; init; }

    /// <summary>Whether the dominant font is bold.</summary>
    public bool IsBold { get; init; }

    /// <summary>Whether the dominant font is italic.</summary>
    public bool IsItalic { get; init; }

    /// <summary>
    /// How far this element is indented from the page's left text margin, in points. The signal
    /// that distinguishes a nested list item from a new paragraph.
    /// </summary>
    public double IndentFromMargin { get; init; }

    /// <summary>
    /// Why this element was given its kind, when that was inferred rather than read from a tag.
    /// Spoken on request so the user can judge whether to trust it — "treated as heading level 2
    /// because it is 1.4 times the body text size and bold" — and correct it when it is wrong.
    /// </summary>
    public string? ClassificationReason { get; init; }

    #endregion

    #region Announcement
    // Content is simply the text. Subclasses that need more override the role, not the content.

    protected override string DescribeContent(VerbosityLevel verbosity) => Text;

    #endregion
}

#endregion

#region ParagraphElement — ordinary body text

/// <summary>Ordinary body text. The most common element in any document, and the default when a
/// run of text carries no evidence that it is anything more specific.</summary>
public sealed class ParagraphElement : TextElement
{
    public ParagraphElement(int pageNumber, string text)
        : base(pageNumber, text) { }

    public override ElementKind Kind => ElementKind.Paragraph;

    /// <summary>
    /// A paragraph announces its role only when the user has asked for full detail. At Normal
    /// verbosity saying "paragraph" before every paragraph would double the length of the document
    /// without adding anything: prose is the default, so it is the one role worth leaving unsaid.
    /// </summary>
    protected override string DescribeRole(VerbosityLevel verbosity) =>
        verbosity == VerbosityLevel.Detailed ? "paragraph" : string.Empty;
}

#endregion

#region HeadingElement — the backbone of navigation
// Headings matter more than any other element, because jumping between them is how a screen
// reader user skims. A document whose headings are right is usable; one whose headings are only
// visually bold is a wall of text. This class therefore also carries whether its level was
// inferred, so the auditor can report guessed headings and the user can confirm or correct them.

/// <summary>A heading, with its level in the document outline.</summary>
public sealed class HeadingElement : TextElement
{
    public HeadingElement(int pageNumber, string text, HeadingLevel level)
        : base(pageNumber, text)
    {
        Level = level;
    }

    public override ElementKind Kind => ElementKind.Heading;

    /// <summary>The outline depth of this heading. Settable because correcting it is a core repair.</summary>
    public HeadingLevel Level { get; internal set; }

    /// <summary>
    /// Announced as "heading level 2" rather than "heading 2", matching what NVDA and JAWS already
    /// say, so someone moving between this editor and a browser hears the same words for the same
    /// thing.
    /// </summary>
    protected override string DescribeRole(VerbosityLevel verbosity) =>
        Level == HeadingLevel.None ? "heading" : $"heading level {(int)Level}";

    /// <summary>
    /// An inferred heading is flagged at Detailed verbosity. The user is entitled to know that the
    /// structure they are navigating by is this program's guess rather than the document's own
    /// statement, because it changes how much they should trust it.
    /// </summary>
    protected override string DescribeState(VerbosityLevel verbosity)
    {
        if (verbosity != VerbosityLevel.Detailed || IsFromRealTags)
            return string.Empty;

        return "inferred from layout";
    }
}

#endregion

#region CaptionElement — text bound to a figure or table

/// <summary>
/// A caption belonging to a figure or a table. Kept as its own kind rather than folded into
/// paragraphs so that reading a figure can offer its caption, and so that the auditor does not
/// mistake a caption for a figure's missing alt text — they serve different purposes and a
/// document needs both.
/// </summary>
public sealed class CaptionElement : TextElement
{
    public CaptionElement(int pageNumber, string text)
        : base(pageNumber, text) { }

    public override ElementKind Kind => ElementKind.Caption;

    protected override string DescribeRole(VerbosityLevel verbosity) => "caption";
}

#endregion

#region BlockQuoteElement — quoted material

/// <summary>A block quotation. Worth its own kind because the boundary between quoted and
/// authored text is invisible when listening unless something says so.</summary>
public sealed class BlockQuoteElement : TextElement
{
    public BlockQuoteElement(int pageNumber, string text)
        : base(pageNumber, text) { }

    public override ElementKind Kind => ElementKind.BlockQuote;

    protected override string DescribeRole(VerbosityLevel verbosity) => "quotation";
}

#endregion

#region CodeElement — preformatted text where every character counts

/// <summary>
/// Monospaced or preformatted text. Announced as code so the listener knows that spacing and
/// punctuation are significant here and that the character-by-character review command is likely
/// to be needed.
/// </summary>
public sealed class CodeElement : TextElement
{
    public CodeElement(int pageNumber, string text)
        : base(pageNumber, text) { }

    public override ElementKind Kind => ElementKind.Code;

    /// <summary>
    /// Whitespace is meaningful in code, so the base class's collapsing would destroy it. This is
    /// the one text element that keeps its extracted text exactly as it was found.
    /// </summary>
    public override string Text => ActualText ?? RawText;

    protected override string DescribeRole(VerbosityLevel verbosity) => "code";

    protected override string DescribeState(VerbosityLevel verbosity)
    {
        if (verbosity != VerbosityLevel.Detailed)
            return string.Empty;

        int lines = RawText.AsSpan().Count('\n') + 1;
        return lines > 1 ? $"{lines} lines" : string.Empty;
    }
}

#endregion

#region NoteElement — footnotes and endnotes

/// <summary>A footnote or endnote. Skipped during continuous reading of the body text and reached
/// deliberately instead, because a footnote read where it falls on the page interrupts the
/// sentence that referenced it.</summary>
public sealed class NoteElement : TextElement
{
    public NoteElement(int pageNumber, string text)
        : base(pageNumber, text) { }

    public override ElementKind Kind => ElementKind.Note;

    protected override string DescribeRole(VerbosityLevel verbosity) => "note";
}

#endregion

#region ArtifactElement — running heads, footers, page numbers, rules
// Artifacts are the reason a badly tagged 200-page report is exhausting: the same running header
// and page number read out at every page boundary. A well-made PDF marks them /Artifact and
// readers skip them. This element represents that, and marking content as an artifact is one of
// the repairs the editor offers.

/// <summary>
/// Page furniture that carries no document content: a running header or footer, a page number, a
/// decorative rule. Excluded from continuous reading but still reachable on request, because
/// "skip by default" must never mean "cannot be read at all".
/// </summary>
public sealed class ArtifactElement : TextElement
{
    public ArtifactElement(int pageNumber, string text, string artifactType = "page furniture")
        : base(pageNumber, text)
    {
        ArtifactType = artifactType;
    }

    public override ElementKind Kind => ElementKind.Artifact;

    /// <summary>What sort of furniture this is — "header", "footer", "page number".</summary>
    public string ArtifactType { get; }

    /// <summary>Artifacts are skipped when reading straight through. This is the whole point of them.</summary>
    public override bool IsReadInContinuousReading => false;

    protected override string DescribeRole(VerbosityLevel verbosity) => ArtifactType;
}

#endregion

#region Container elements — document, page and section
// These hold no text of their own. They exist so that the tree has a shape: a document contains
// pages, a page contains sections and blocks. Their announcements summarise what they contain,
// because that is the only useful thing a container can say.

/// <summary>The root of the element tree. One per open document.</summary>
public sealed class DocumentRootElement : DocumentElement
{
    public DocumentRootElement(string title)
        : base(pageNumber: 0)
    {
        Title = title;
    }

    public override ElementKind Kind => ElementKind.Document;

    /// <summary>The document's title, from its metadata or failing that its filename.</summary>
    public string Title { get; set; }

    protected override string DescribeContent(VerbosityLevel verbosity) => Title;

    protected override string DescribeRole(VerbosityLevel verbosity) => "document";

    protected override string DescribePosition(VerbosityLevel verbosity)
    {
        int pages = Children.Count(c => c.Kind == ElementKind.Page);
        return pages > 0 ? $"{pages} pages" : string.Empty;
    }
}

/// <summary>One page of the document.</summary>
public sealed class PageElement : DocumentElement
{
    public PageElement(int pageNumber, double width, double height)
        : base(pageNumber)
    {
        Width = width;
        Height = height;
        Bounds = new PageRegion(0, 0, width, height);
    }

    public override ElementKind Kind => ElementKind.Page;

    /// <summary>Page width in points.</summary>
    public double Width { get; }

    /// <summary>Page height in points.</summary>
    public double Height { get; }

    /// <summary>
    /// The page's printed label when it differs from its ordinal position — "iv" for front matter,
    /// or "A-3" in a document with appendices. Announced in preference to the index, because it is
    /// the number the user would read out to a colleague.
    /// </summary>
    public string? PageLabel { get; set; }

    /// <summary>
    /// True when the page holds images but essentially no extractable text: a scan that was never
    /// put through OCR. Said plainly, because the alternative is announcing an empty page and
    /// leaving the user to conclude the program is broken.
    /// </summary>
    public bool IsImageOnly { get; set; }

    protected override string DescribeContent(VerbosityLevel verbosity) =>
        PageLabel is { Length: > 0 } label && label != PageNumber.ToString()
            ? $"page {label}"
            : $"page {PageNumber}";

    protected override string DescribeRole(VerbosityLevel verbosity) => string.Empty;

    protected override string DescribeState(VerbosityLevel verbosity) =>
        IsImageOnly ? "image only, no readable text on this page" : string.Empty;
}

/// <summary>
/// A grouping with no reading semantics of its own, from the PDF /Sect, /Part and /Div types.
/// Kept because it carries nesting depth that headings alone would lose.
/// </summary>
public sealed class SectionElement : DocumentElement
{
    public SectionElement(int pageNumber, string? title = null)
        : base(pageNumber)
    {
        SectionTitle = title;
    }

    public override ElementKind Kind => ElementKind.Section;

    /// <summary>The section's title, when it has one distinct from its first heading.</summary>
    public string? SectionTitle { get; set; }

    protected override string DescribeContent(VerbosityLevel verbosity) => SectionTitle ?? string.Empty;

    protected override string DescribeRole(VerbosityLevel verbosity) =>
        verbosity == VerbosityLevel.Detailed ? "section" : string.Empty;
}

#endregion
