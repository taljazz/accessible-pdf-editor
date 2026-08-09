using System.Text;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using UglyToad.PdfPig.Content;

namespace AccessiblePdfEditor.Ingestion;

// =====================================================================================
//  TaggedStructureExtractor.cs
//
//  Reads structure from the document's own tags, for PDFs that carry them.
//
//  This is the good path. A tagged PDF states what every piece of its content is —
//  this is a level 2 heading, this is a table header cell, this figure means "quarterly
//  revenue by region" — and reading those statements is both more accurate and far cheaper
//  than inferring them. Where a document has done the work, the right thing to do is
//  believe it.
//
//  Two attributes here are things NO amount of layout analysis could ever recover, and they
//  are the reason tagging exists at all:
//
//    /Alt          — what a figure means. There is nothing in the pixels that says so.
//    /ActualText   — what a run of glyphs should be read as, where the glyphs and the
//                    meaning differ: a ligature, a drop cap, a word split by a decorative
//                    rule.
//
//  Both are honoured, and both are preserved through editing so that saving a document
//  never quietly discards the accessibility work someone else already did.
// =====================================================================================

#region TaggedStructureExtractor

/// <summary>Reads document structure from a page's marked content and structure tags.</summary>
public sealed class TaggedStructureExtractor : StructureExtractorBase
{
    #region Identity

    public override string Name => "document tags";

    /// <summary>
    /// Handles a page when its marked content actually describes the content, rather than merely
    /// existing. A page whose only marked content is an artifact wrapper carries no useful
    /// structure and is better served by layout analysis.
    /// </summary>
    public override bool CanHandle(Page page)
    {
        try
        {
            var contents = page.GetMarkedContents();
            if (contents.Count == 0)
                return false;

            return contents.Any(HasMeaningfulStructure);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasMeaningfulStructure(MarkedContentElement element)
    {
        if (!element.IsArtifact && MapTag(element.Tag) != ElementKind.Unknown)
            return true;

        foreach (var child in element.Children)
        {
            if (HasMeaningfulStructure(child))
                return true;
        }

        return false;
    }

    /// <summary>Everything this class produces came from the document's own tags.</summary>
    protected override bool ProducesRealTags => true;

    #endregion

    #region Extraction

    protected override List<DocumentElement> ExtractCore(Page page, ExtractionContext context)
    {
        var elements = new List<DocumentElement>();

        foreach (var marked in page.GetMarkedContents())
        {
            var converted = Convert(marked, context);
            if (converted is not null)
                elements.Add(converted);
        }

        return elements;
    }

    /// <summary>
    /// Turns one marked-content element, and everything inside it, into document elements.
    /// Returns null for content that produces nothing worth reading.
    /// </summary>
    private DocumentElement? Convert(MarkedContentElement marked, ExtractionContext context)
    {
        // Artifacts are page furniture the producer has explicitly marked as such. Honouring that
        // is the whole reason the attribute exists.
        if (marked.IsArtifact)
            return ConvertArtifact(marked, context);

        var kind = MapTag(marked.Tag);
        string ownText = TextOf(marked);
        var bounds = RegionOf(marked.Letters);

        DocumentElement element = kind switch
        {
            ElementKind.Heading => new HeadingElement(context.PageNumber, ownText, HeadingLevelOf(marked.Tag)),
            ElementKind.Paragraph => new ParagraphElement(context.PageNumber, ownText),
            ElementKind.List => new ListElement(context.PageNumber, ListMarkerKindOf(marked)),
            ElementKind.ListItem => new ListItemElement(context.PageNumber, ownText, LabelOf(marked)),
            ElementKind.Table => new TableElement(context.PageNumber),
            ElementKind.TableRow => new TableRowElement(context.PageNumber),
            ElementKind.TableCell => new TableCellElement(context.PageNumber, ownText, CellRoleOf(marked)),
            ElementKind.Figure => BuildFigure(marked, context, bounds),
            ElementKind.Caption => new CaptionElement(context.PageNumber, ownText),
            ElementKind.BlockQuote => new BlockQuoteElement(context.PageNumber, ownText),
            ElementKind.Code => new CodeElement(context.PageNumber, ownText),
            ElementKind.Note => new NoteElement(context.PageNumber, ownText),
            ElementKind.TableOfContents => new SectionElement(context.PageNumber, "table of contents"),
            ElementKind.Section => new SectionElement(context.PageNumber),

            // An unrecognised tag still contains real content. Treating it as a section keeps its
            // children reachable; discarding it would silently hide text from the reader, which is
            // the one outcome this program must never produce.
            _ => new SectionElement(context.PageNumber),
        };

        ApplyAccessibilityAttributes(element, marked, context);

        if (!bounds.IsEmpty)
            element.Bounds = bounds;

        // A structural container's own text belongs to its children, so recursion happens for
        // every kind that can hold them. Leaf text elements keep the text already assigned.
        if (element.AcceptsChildren)
        {
            foreach (var child in marked.Children)
            {
                var converted = Convert(child, context);
                if (converted is not null)
                    element.AddChild(converted);
            }
        }

        element.RecalculateBoundsFromChildren();

        bool hasAnything = element.Children.Count > 0
            || element.FullText.Trim().Length > 0
            || element is FigureElement;

        return hasAnything ? element : null;
    }

    private static DocumentElement? ConvertArtifact(MarkedContentElement marked, ExtractionContext context)
    {
        string text = CollectText(marked);
        if (text.Trim().Length == 0)
            return null;

        string subtype = ArtifactSubtype(marked);

        var artifact = new ArtifactElement(context.PageNumber, text, subtype)
        {
            ClassificationReason = "marked as an artifact by the document",
        };

        artifact.Bounds = RegionOf(marked.Letters);
        return artifact;
    }

    /// <summary>
    /// Reads the artifact's own subtype, so a running head is announced as a header rather than as
    /// generic furniture when the user goes looking for it.
    /// </summary>
    private static string ArtifactSubtype(MarkedContentElement marked)
    {
        try
        {
            if (marked.Properties?.TryGet(UglyToad.PdfPig.Tokens.NameToken.Create("Subtype"),
                    out UglyToad.PdfPig.Tokens.NameToken? subtype) == true && subtype is not null)
            {
                return subtype.Data switch
                {
                    "Header" => "header",
                    "Footer" => "footer",
                    "Watermark" => "watermark",
                    "Pagination" => "page number",
                    "Layout" => "layout decoration",
                    "Page" => "page furniture",
                    _ => "page furniture",
                };
            }
        }
        catch
        {
            // Properties are optional and may be malformed. The generic name is always correct.
        }

        return "page furniture";
    }

    #endregion

    #region The accessibility attributes — the part layout analysis can never recover

    /// <summary>
    /// Copies the accessibility attributes a tagged document carries onto the element.
    ///
    /// These are the whole payload of tagging. /Alt gives a figure its meaning; /ActualText says
    /// what glyphs should be read as; /Lang lets a screen reader switch voice mid-document;
    /// /E expands an abbreviation. None of them can be inferred from the page, and all of them are
    /// preserved through editing.
    /// </summary>
    private static void ApplyAccessibilityAttributes(
        DocumentElement element, MarkedContentElement marked, ExtractionContext context)
    {
        if (!string.IsNullOrWhiteSpace(marked.ActualText))
            element.ActualText = marked.ActualText.Trim();

        if (!string.IsNullOrWhiteSpace(marked.Language))
            element.Language = marked.Language.Trim();

        if (element is FigureElement figure)
        {
            string? alt = ReadAlternateDescription(marked);
            if (!string.IsNullOrWhiteSpace(alt))
                figure.SetAlternateText(alt);
        }

        // An expanded form belongs to an abbreviation. Where the document supplies one, reading the
        // expansion is nearly always what the author intended — "World Health Organization" rather
        // than "W H O" — so it stands in as the actual text unless one was already given.
        if (!string.IsNullOrWhiteSpace(marked.ExpandedForm) && element.ActualText is null)
            element.ActualText = marked.ExpandedForm.Trim();
    }

    /// <summary>
    /// Reads a figure's alternate text.
    ///
    /// PdfPig 0.1.15 exposes an <c>AlternateDescription</c> property but never populates it — /Alt
    /// is parsed and lands in the raw property list, but is not mapped onto the typed property.
    /// Since /Alt is the single most important accessibility attribute in a tagged PDF, relying on
    /// the typed property alone would silently discard every figure description in every properly
    /// tagged document. So the property is tried first, in case a later PdfPig fixes it, and the
    /// raw dictionary is read as the fallback that actually works today.
    /// </summary>
    private static string? ReadAlternateDescription(MarkedContentElement marked)
    {
        if (!string.IsNullOrWhiteSpace(marked.AlternateDescription))
            return marked.AlternateDescription.Trim();

        try
        {
            var properties = marked.Properties;
            if (properties is null)
                return null;

            // /Alt may be stored as a literal string or as a UTF-16 hex string; both occur, and a
            // description in any language other than English is very often the hex form.
            if (properties.TryGet(UglyToad.PdfPig.Tokens.NameToken.Alt,
                    out UglyToad.PdfPig.Tokens.StringToken? literal) && literal is not null)
            {
                return string.IsNullOrWhiteSpace(literal.Data) ? null : literal.Data.Trim();
            }

            if (properties.TryGet(UglyToad.PdfPig.Tokens.NameToken.Alt,
                    out UglyToad.PdfPig.Tokens.HexToken? hex) && hex is not null)
            {
                return string.IsNullOrWhiteSpace(hex.Data) ? null : hex.Data.Trim();
            }
        }
        catch
        {
            // A malformed property list costs this one description, not the page.
        }

        return null;
    }

    private static FigureElement BuildFigure(
        MarkedContentElement marked, ExtractionContext context, PageRegion bounds)
    {
        var region = bounds;

        // A figure's letters are usually none; its geometry comes from the image it contains.
        if (region.IsEmpty && marked.Images.Count > 0)
        {
            region = PageRegion.Empty;
            foreach (var image in marked.Images)
                region = region.Union(ToRegion(image.BoundingBox));
        }

        var figure = new FigureElement(context.PageNumber, region);

        if (marked.Images.Count == 1)
        {
            var image = marked.Images[0];
            return new FigureElement(context.PageNumber, region)
            {
                PixelWidth = image.WidthInSamples,
                PixelHeight = image.HeightInSamples,
            };
        }

        return figure;
    }

    #endregion

    #region Tag mapping
    // Maps the PDF standard structure types (ISO 32000-1 Table 333) onto our element kinds. Tag
    // names are compared without the leading slash, which is how PdfPig reports them.

    /// <summary>Maps a PDF structure tag to an element kind.</summary>
    private static ElementKind MapTag(string? tag) => tag switch
    {
        null or "" => ElementKind.Unknown,

        "Document" or "DocumentFragment" => ElementKind.Document,
        "Part" or "Art" or "Sect" or "Div" => ElementKind.Section,

        "H" or "H1" or "H2" or "H3" or "H4" or "H5" or "H6" or "H7" or "H8" or "H9" =>
            ElementKind.Heading,

        "P" => ElementKind.Paragraph,

        "L" => ElementKind.List,
        "LI" => ElementKind.ListItem,

        // A list item's label and body are its parts rather than elements in their own right; the
        // item reads them itself, so they map to paragraph-like content inside it.
        "Lbl" or "LBody" => ElementKind.Paragraph,

        "Table" => ElementKind.Table,
        "TR" => ElementKind.TableRow,
        "TH" or "TD" => ElementKind.TableCell,

        // Table head, body and foot groupings carry no reading semantics of their own; their rows
        // matter, and treating the grouping as a section keeps those rows in place.
        "THead" or "TBody" or "TFoot" => ElementKind.Section,

        "Figure" or "Formula" => ElementKind.Figure,
        "Caption" => ElementKind.Caption,
        "BlockQuote" => ElementKind.BlockQuote,
        "Code" => ElementKind.Code,
        "Note" or "FENote" => ElementKind.Note,
        "TOC" or "TOCI" => ElementKind.TableOfContents,
        "Link" => ElementKind.Link,
        "Form" => ElementKind.FormField,
        "Annot" => ElementKind.Annotation,
        "Artifact" => ElementKind.Artifact,

        // Inline-level tags that decorate a run of text within a block rather than forming one.
        "Span" or "Quote" or "Reference" or "BibEntry" or "Ruby" or "Warichu" or "Em" or "Strong" =>
            ElementKind.Paragraph,

        _ => ElementKind.Unknown,
    };

    private static HeadingLevel HeadingLevelOf(string? tag) => tag switch
    {
        "H1" => HeadingLevel.Level1,
        "H2" => HeadingLevel.Level2,
        "H3" => HeadingLevel.Level3,
        "H4" => HeadingLevel.Level4,
        "H5" => HeadingLevel.Level5,
        "H6" => HeadingLevel.Level6,

        // Levels beyond 6 are not expressible in the PDF heading tags and are clamped rather than
        // dropped: a level 7 heading is still a heading, and losing it would leave a hole in the
        // outline the user navigates by.
        "H7" or "H8" or "H9" => HeadingLevel.Level6,

        // A bare /H takes its level from how deeply it is nested, which is what the specification
        // intends. Resolved by the loader once the whole tree exists.
        _ => HeadingLevel.None,
    };

    /// <summary>
    /// Reads a table cell's role, including the /Scope attribute that says whether a header governs
    /// its row or its column. Scope is what lets a data cell announce the right headers, so it is
    /// read where present rather than guessed from position.
    /// </summary>
    private static TableCellRole CellRoleOf(MarkedContentElement marked)
    {
        if (marked.Tag != "TH")
            return TableCellRole.Data;

        try
        {
            if (marked.Properties?.TryGet(UglyToad.PdfPig.Tokens.NameToken.Create("Scope"),
                    out UglyToad.PdfPig.Tokens.NameToken? scope) == true && scope is not null)
            {
                return scope.Data switch
                {
                    "Row" => TableCellRole.RowHeader,
                    "Column" => TableCellRole.ColumnHeader,
                    _ => TableCellRole.Header,
                };
            }
        }
        catch
        {
            // Malformed attributes fall back to an unscoped header, which still beats a data cell.
        }

        return TableCellRole.Header;
    }

    /// <summary>Reads a list's numbering style from its /ListNumbering attribute.</summary>
    private static ListMarkerKind ListMarkerKindOf(MarkedContentElement marked)
    {
        try
        {
            if (marked.Properties?.TryGet(UglyToad.PdfPig.Tokens.NameToken.Create("ListNumbering"),
                    out UglyToad.PdfPig.Tokens.NameToken? numbering) == true && numbering is not null)
            {
                return numbering.Data switch
                {
                    "Disc" or "Circle" or "Square" => ListMarkerKind.Bullet,
                    "Decimal" => ListMarkerKind.Decimal,
                    "LowerAlpha" => ListMarkerKind.LowerAlpha,
                    "UpperAlpha" => ListMarkerKind.UpperAlpha,
                    "LowerRoman" => ListMarkerKind.LowerRoman,
                    "UpperRoman" => ListMarkerKind.UpperRoman,
                    _ => ListMarkerKind.None,
                };
            }
        }
        catch
        {
            // Optional attribute; absence is not an error.
        }

        return ListMarkerKind.None;
    }

    /// <summary>The visible marker of a list item, from its /Lbl child.</summary>
    private static string? LabelOf(MarkedContentElement marked)
    {
        foreach (var child in marked.Children)
        {
            if (child.Tag == "Lbl")
            {
                string label = CollectText(child).Trim();
                return label.Length > 0 ? label : null;
            }
        }

        return null;
    }

    #endregion

    #region Text collection

    /// <summary>
    /// An element's own text, from its direct letters only. Children contribute their own text
    /// through the element tree, so including theirs here would say everything twice.
    /// </summary>
    private static string TextOf(MarkedContentElement marked)
    {
        if (marked.Letters.Count == 0)
            return string.Empty;

        var builder = new StringBuilder(marked.Letters.Count);
        foreach (var letter in marked.Letters)
            builder.Append(letter.Value);

        return builder.ToString();
    }

    /// <summary>An element's text including all its descendants'. Used for artifacts and labels,
    /// which are read as a unit and never become a tree of their own.</summary>
    private static string CollectText(MarkedContentElement marked)
    {
        var builder = new StringBuilder();

        void Walk(MarkedContentElement node)
        {
            foreach (var letter in node.Letters)
                builder.Append(letter.Value);

            foreach (var child in node.Children)
                Walk(child);
        }

        Walk(marked);
        return builder.ToString();
    }

    #endregion
}

#endregion
