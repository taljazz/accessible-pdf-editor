namespace AccessiblePdfEditor.Model;

// =====================================================================================
//  DocumentEnums.cs
//
//  Every enumerated value used to describe what is IN a PDF and what condition it is in.
//  These are the vocabulary the whole application speaks: the structure extractors produce
//  them, the navigation service filters on them, the auditor reasons about them, and the
//  speech layer turns them into words a screen reader says out loud.
//
//  Enums for what the user is DOING (navigating, editing) live in InteractionEnums.cs.
// =====================================================================================

#region Element kinds — what a single piece of a document actually is
// ElementKind is the discriminator for the DocumentElement hierarchy. It maps closely to the
// PDF standard structure types (ISO 32000-1 Table 333) because that is what a tagged PDF
// actually stores, and what NVDA and JAWS report when they read a tagged PDF. Keeping our
// vocabulary aligned with the tag names means remediation is a direct translation rather
// than a guess.

/// <summary>
/// What a <see cref="Elements.DocumentElement"/> represents. Values correspond to PDF standard
/// structure types so that a tag read from a document, and a tag we write back, use one vocabulary.
/// </summary>
public enum ElementKind
{
    /// <summary>Kind could not be determined. Treated as a paragraph when read aloud.</summary>
    Unknown = 0,

    /// <summary>The whole document. Root of the element tree.</summary>
    Document,

    /// <summary>A single page. Always present, even in an untagged document.</summary>
    Page,

    /// <summary>A logical grouping with no reading semantics of its own (PDF /Sect, /Part, /Div).</summary>
    Section,

    /// <summary>A heading. The level is carried separately by <see cref="HeadingLevel"/>.</summary>
    Heading,

    /// <summary>Ordinary body text (PDF /P).</summary>
    Paragraph,

    /// <summary>A list container (PDF /L).</summary>
    List,

    /// <summary>One item within a list (PDF /LI, whose /LBody carries the text).</summary>
    ListItem,

    /// <summary>A table (PDF /Table).</summary>
    Table,

    /// <summary>A table row (PDF /TR).</summary>
    TableRow,

    /// <summary>A table cell, header or data — see <see cref="TableCellRole"/> (PDF /TH, /TD).</summary>
    TableCell,

    /// <summary>An image or graphic (PDF /Figure). Carries alt text, or is flagged by the auditor.</summary>
    Figure,

    /// <summary>A caption attached to a figure or table (PDF /Caption).</summary>
    Caption,

    /// <summary>A block quotation (PDF /BlockQuote).</summary>
    BlockQuote,

    /// <summary>Preformatted or monospaced text (PDF /Code).</summary>
    Code,

    /// <summary>A footnote or endnote (PDF /Note).</summary>
    Note,

    /// <summary>A table of contents container (PDF /TOC) or one of its entries (/TOCI).</summary>
    TableOfContents,

    /// <summary>A hyperlink (PDF /Link plus a Link annotation).</summary>
    Link,

    /// <summary>An interactive form field (PDF /Form plus a Widget annotation).</summary>
    FormField,

    /// <summary>A comment, highlight, or other markup annotation.</summary>
    Annotation,

    /// <summary>A file attached to the document.</summary>
    Attachment,

    /// <summary>
    /// Page furniture — running header, footer, page number, decorative rule. Tagged /Artifact in a
    /// well-made PDF. Skipped during continuous reading, but still reachable on request.
    /// </summary>
    Artifact,
}

#endregion

#region Heading level — depth in the document outline
// Kept as its own enum rather than a bare int so that "no heading" is representable and so the
// speech layer can say "heading level 2" without a magic number. PDF defines /H1../H6 explicitly,
// plus an unnumbered /H whose depth comes from nesting.

/// <summary>Depth of a heading, matching the PDF /H1../H6 structure types.</summary>
public enum HeadingLevel
{
    /// <summary>Not a heading.</summary>
    None = 0,

    Level1 = 1,
    Level2 = 2,
    Level3 = 3,
    Level4 = 4,
    Level5 = 5,
    Level6 = 6,
}

#endregion

#region Table cell role — header cells carry meaning that data cells do not
// A screen reader announces a data cell together with its header ("Revenue, column, 4,200").
// That only works if header cells are distinguishable, which is exactly what /TH versus /TD
// records. An untagged table loses this, and the auditor reports it.

/// <summary>Whether a table cell is a header or a data cell, and which axis a header governs.</summary>
public enum TableCellRole
{
    /// <summary>An ordinary data cell (PDF /TD).</summary>
    Data = 0,

    /// <summary>A header cell whose scope is unknown (PDF /TH with no /Scope).</summary>
    Header,

    /// <summary>A header cell that labels its column (PDF /TH with /Scope /Column).</summary>
    ColumnHeader,

    /// <summary>A header cell that labels its row (PDF /TH with /Scope /Row).</summary>
    RowHeader,
}

#endregion

#region List marker kind — how a list item is introduced when spoken
// Mirrors the PDF /ListNumbering attribute. It decides whether we say "bullet", "1.", or "a."
// before an item, which is how a listener tells an ordered list from an unordered one.

/// <summary>How the items of a list are marked, mirroring the PDF /ListNumbering attribute.</summary>
public enum ListMarkerKind
{
    /// <summary>No marker, or the marker could not be determined.</summary>
    None = 0,

    /// <summary>An unordered bullet.</summary>
    Bullet,

    /// <summary>1, 2, 3 …</summary>
    Decimal,

    /// <summary>a, b, c …</summary>
    LowerAlpha,

    /// <summary>A, B, C …</summary>
    UpperAlpha,

    /// <summary>i, ii, iii …</summary>
    LowerRoman,

    /// <summary>I, II, III …</summary>
    UpperRoman,
}

#endregion

#region Tagged status — how accessible the document already is
// This is the single most important fact about any PDF handed to a blind reader, and it is the
// first thing the editor announces after a load. It decides which structure extractor runs and
// what the remediation workflow offers to fix.

/// <summary>How much real accessibility structure a document carries.</summary>
public enum TaggedStatus
{
    /// <summary>Not yet determined.</summary>
    Unknown = 0,

    /// <summary>
    /// A structure tree is present and covers essentially all the content. Headings, lists and
    /// tables can be trusted, so navigation uses them directly.
    /// </summary>
    FullyTagged,

    /// <summary>
    /// A structure tree exists but leaves a lot of content untagged, so the tags alone would hide
    /// text from the reader. Structure is merged with heuristics rather than trusted outright.
    /// </summary>
    PartiallyTagged,

    /// <summary>
    /// No structure tree at all. There is real text, but nothing says what any of it means, so
    /// headings and lists are inferred from layout. Very common, and the main remediation target.
    /// </summary>
    Untagged,

    /// <summary>
    /// Pages carry images and almost no extractable text — a scan that was never run through OCR.
    /// Nothing can be read aloud, and the editor says so plainly instead of reporting an empty page.
    /// </summary>
    ScannedWithoutText,
}

#endregion

#region Document load state — tracked across the whole open/close lifecycle
// The UI keys off this: what the title bar says, which menu items are enabled, and what happens
// if the user presses a key while a large document is still being analysed.

/// <summary>Where a document is in its open/parse/close lifecycle.</summary>
public enum DocumentLoadState
{
    /// <summary>No document is open.</summary>
    Empty = 0,

    /// <summary>A file is being read and analysed.</summary>
    Loading,

    /// <summary>Loaded and ready to read and edit.</summary>
    Loaded,

    /// <summary>The file is encrypted and a password is needed before anything can be read.</summary>
    PasswordRequired,

    /// <summary>
    /// Opened, but the document's own permission flags forbid extracting text for accessibility.
    /// Reported honestly rather than worked around.
    /// </summary>
    ExtractionNotPermitted,

    /// <summary>The file could not be parsed. <see cref="Ingestion.DocumentLoadResult.Message"/> says why.</summary>
    Failed,
}

#endregion

#region Annotation kinds — the markup that lives alongside page content
// Subset of the PDF annotation subtypes that carry meaning for a reader. Widget (form field) and
// Link are modelled as their own element types instead, because they behave differently.

/// <summary>The kind of markup annotation, for the subtypes that a reader cares about.</summary>
public enum AnnotationKind
{
    /// <summary>A subtype we do not model specially.</summary>
    Other = 0,

    /// <summary>A sticky-note comment (PDF /Text).</summary>
    Comment,

    /// <summary>Highlighted text (PDF /Highlight).</summary>
    Highlight,

    /// <summary>Underlined text (PDF /Underline).</summary>
    Underline,

    /// <summary>Struck-through text (PDF /StrikeOut).</summary>
    StrikeOut,

    /// <summary>Squiggly-underlined text (PDF /Squiggly).</summary>
    Squiggly,

    /// <summary>Free-standing text placed on the page (PDF /FreeText).</summary>
    FreeText,

    /// <summary>A stamp (PDF /Stamp).</summary>
    Stamp,

    /// <summary>Freehand ink (PDF /Ink). Has no text of its own, so its /Contents is all we can say.</summary>
    Ink,

    /// <summary>An attached file (PDF /FileAttachment).</summary>
    FileAttachment,

    /// <summary>A popup window belonging to another annotation. Never announced on its own.</summary>
    Popup,
}

#endregion

#region Link targets — where following a link would actually go
// Announced before the user activates a link, so that leaving the document is never a surprise
// and an external address can be heard in full first.

/// <summary>What a link points at.</summary>
public enum LinkTargetKind
{
    /// <summary>Target could not be resolved.</summary>
    Unknown = 0,

    /// <summary>Another place in this document.</summary>
    InternalDestination,

    /// <summary>A web address.</summary>
    WebUrl,

    /// <summary>An email address (a mailto action).</summary>
    Email,

    /// <summary>Another file on disk.</summary>
    ExternalFile,

    /// <summary>A file embedded inside this document.</summary>
    EmbeddedFile,

    /// <summary>An action we can name but will not perform, such as running JavaScript.</summary>
    UnsupportedAction,
}

#endregion

#region Text direction — needed to read mixed-script documents correctly
// Not cosmetic: reading a right-to-left paragraph in visual left-to-right order produces
// nonsense. Carried per element because a single document can mix directions.

/// <summary>Base writing direction of an element's text.</summary>
public enum TextDirection
{
    /// <summary>Inherited from the parent element, or the document default.</summary>
    Inherit = 0,

    LeftToRight,
    RightToLeft,
}

#endregion
