using System.Text;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace AccessiblePdfEditor.Persistence;

// =====================================================================================
//  StructureTreeBuilder.cs
//
//  Gives an untagged document a real structure tree.
//
//  This is the remediation the whole program points at. Most PDFs carry no structure at
//  all: the text is drawn on the page and nothing says which line is a heading, which
//  block is a list, or what order any of it should be read in. This editor infers all of
//  that from layout so the user can navigate — but that inference lives in memory and dies
//  when the document is closed. Writing it into the file is what makes the document
//  accessible to everyone else: Acrobat, Edge, a checker, a colleague's screen reader.
//
//  HOW IT FITS TOGETHER
//
//    ContentStreamTagger   marks the text on each page, and reports where each mark landed
//    this class            matches those marks to the elements found by layout, and builds
//                          the tree that describes them
//
//  The tree MIRRORS THE ELEMENT TREE rather than being a flat list. That matters more than
//  it sounds: a run of /TD elements with no /Table and no /TR above them is not a table, it
//  is eleven loose cells, and a reader announcing them would give the numbers with no idea
//  which row or column they came from. Nesting is most of what a structure tree is for.
//
//  THE REFUSAL THAT MATTERS
//
//  If too little of a page's text ends up inside a tag, the document is NOT marked as
//  tagged. A file that claims /MarkInfo /Marked true while half its text sits outside the
//  tree is worse than an honest untagged one, because a checker passes it and a reader
//  believes it. Partial coverage is reported to the user as a number rather than hidden.
// =====================================================================================

#region StructureTreeResult

/// <summary>What building a structure tree achieved, in terms a person can be told.</summary>
public readonly record struct StructureTreeResult(
    int PagesTagged, int ElementsTagged, double Coverage, bool ClaimedTagged)
{
    public bool DidAnything => ElementsTagged > 0;

    /// <summary>The sentence the user hears after the document has been tagged.</summary>
    public string Describe() => !DidAnything
        ? "Nothing on these pages could be tagged, so the document was left as it was."
        : ClaimedTagged
            ? $"Tagged {ElementsTagged} items across {PagesTagged} " +
              $"{(PagesTagged == 1 ? "page" : "pages")}. The document now carries its own structure, " +
              "so any reader can follow its headings and tables — not just this program."
            : $"Tagged {ElementsTagged} items, but only {Coverage:P0} of the text could be matched to " +
              "a heading, paragraph or table. The document has NOT been marked as tagged, because a " +
              "file that claims to be accessible while most of it is untagged is worse than one that " +
              "makes no claim.";
}

#endregion

#region StructureTreeBuilder

/// <summary>Writes a structure tree describing what layout analysis found.</summary>
internal sealed class StructureTreeBuilder(PdfDocument sharp, List<string> warnings)
{
    /// <summary>
    /// Below this share of a page's text landing inside a tag, the document is not allowed to say
    /// it is tagged. Two thirds is deliberately demanding: the cost of refusing is that the user
    /// tries again on a better document, and the cost of accepting is a file that lies.
    /// </summary>
    private const double MinimumCoverageToClaimTagged = 0.66;

    private readonly Dictionary<int, List<MarkedContentReference>> _marksByElement = [];

    private readonly record struct MarkedContentReference(int PageNumber, int MarkedContentId);

    #region Building

    public StructureTreeResult Build(PdfDocumentModel model)
    {
        int totalShows = 0;
        int totalMarked = 0;
        int pagesTagged = 0;

        for (int number = 1; number <= sharp.PageCount && number <= model.PageCount; number++)
        {
            var page = sharp.Pages[number - 1];
            var candidates = TaggableElementsOn(model, number);

            if (candidates.Count == 0)
                continue;

            byte[] content;

            try
            {
                content = ReadContent(page);
            }
            catch (Exception ex)
            {
                warnings.Add($"Page {number}: its content could not be read, so it was not tagged ({ex.Message}).");
                continue;
            }

            var tagged = ContentStreamTagger.Tag(content, 0, (x, y) => Classify(candidates, x, y));

            totalShows += tagged.TextOperatorCount;
            totalMarked += tagged.MarkedOperators;

            if (!tagged.MarkedAnything)
                continue;

            ReplaceContent(page, tagged.Content);

            foreach (var run in tagged.Runs)
            {
                if (!_marksByElement.TryGetValue(run.Tag.ElementKey, out var list))
                    _marksByElement[run.Tag.ElementKey] = list = [];

                list.Add(new MarkedContentReference(number, run.MarkedContentId));
            }

            pagesTagged++;
        }

        if (_marksByElement.Count == 0)
            return new StructureTreeResult(0, 0, 0, false);

        double coverage = totalShows == 0 ? 0 : (double)totalMarked / totalShows;
        bool claim = coverage >= MinimumCoverageToClaimTagged;

        int written = WriteTree(model, claim);

        return new StructureTreeResult(pagesTagged, written, coverage, claim && written > 0);
    }

    /// <summary>
    /// Which element a piece of text at a point belongs to, and what to call it.
    ///
    /// The smallest containing element wins. A cell sits inside a row inside a table, and tagging
    /// the text as the table rather than the cell would lose every distinction the table has.
    /// </summary>
    private static ContentTag? Classify(IReadOnlyList<DocumentElement> candidates, double x, double y)
    {
        DocumentElement? best = null;
        double smallest = double.MaxValue;

        foreach (var element in candidates)
        {
            var bounds = element.Bounds;

            // The origin of a show operator is the text BASELINE, which sits at or slightly above
            // the bottom of the glyph box. The tolerance covers descenders and nothing more.
            const double Tolerance = 8.0;

            bool inside = x >= bounds.Left - Tolerance && x <= bounds.Right + Tolerance
                          && y >= bounds.Bottom - Tolerance && y <= bounds.Top + Tolerance;

            if (!inside)
                continue;

            double area = Math.Max(bounds.Width, 1) * Math.Max(bounds.Height, 1);

            if (area < smallest)
            {
                smallest = area;
                best = element;
            }
        }

        if (best is null || TagFor(best) is not { } name)
            return null;

        return new ContentTag(name, best.Id);
    }

    private static List<DocumentElement> TaggableElementsOn(PdfDocumentModel model, int pageNumber) =>
        model.ReadingOrder
            .Where(e => e.PageNumber == pageNumber && !e.Bounds.IsEmpty && TagFor(e) is not null)
            .ToList();

    #endregion

    #region Writing the tree

    /// <summary>
    /// Walks the element tree and writes a structure element for everything that has marks beneath
    /// it, keeping the nesting. Returns how many elements were written.
    /// </summary>
    private int WriteTree(PdfDocumentModel model, bool claimTagged)
    {
        var catalog = sharp.Internals.Catalog;

        var root = new PdfDictionary(sharp);
        root.Elements.SetName("/Type", "StructTreeRoot");
        root.Elements.SetValue("/K", new PdfArray(sharp));
        sharp.Internals.AddObject(root);
        catalog.Elements.SetReference("/StructTreeRoot", root);

        var parents = new Dictionary<int, PdfArray>();
        int written = 0;

        foreach (var child in model.Root.Children)
            WriteElement(child, root, root, parents, ref written);

        WriteParentTree(root, parents);

        // The claim is made last and only when it has been earned.
        if (claimTagged)
        {
            var markInfo = catalog.Elements.GetDictionary("/MarkInfo") ?? new PdfDictionary(sharp);
            markInfo.Elements.SetBoolean("/Marked", true);
            catalog.Elements.SetValue("/MarkInfo", markInfo);
        }

        return written;
    }

    /// <summary>
    /// Writes one element and its descendants. Returns the structure element created, or null when
    /// there was nothing beneath it worth describing.
    /// </summary>
    private PdfDictionary? WriteElement(
        DocumentElement element,
        PdfDictionary parent,
        PdfDictionary root,
        Dictionary<int, PdfArray> parents,
        ref int written)
    {
        _marksByElement.TryGetValue(element.Id, out var marks);

        // An element with no marks of its own may still be a container whose children have some —
        // a table's text all belongs to its cells. It is skipped only when nothing below it was
        // tagged either, which is what keeps empty branches out of the tree.
        var children = new List<PdfDictionary>();
        string? tag = TagFor(element);

        var self = tag is null || (marks is null && element.Children.Count == 0)
            ? null
            : new PdfDictionary(sharp);

        if (self is not null)
        {
            self.Elements.SetName("/Type", "StructElem");
            self.Elements.SetName("/S", tag!);
            sharp.Internals.AddObject(self);
        }

        var childParent = self ?? parent;

        foreach (var child in element.Children)
        {
            if (WriteElement(child, childParent, root, parents, ref written) is { } created)
                children.Add(created);
        }

        if (self is null)
            return null;

        if (marks is null && children.Count == 0)
        {
            // Nothing beneath it after all. Left out rather than written as an empty tag, which a
            // checker reports and a reader announces as a heading with no text.
            return null;
        }

        self.Elements.SetReference("/P", parent);

        var kids = new PdfArray(sharp);

        if (marks is not null)
        {
            self.Elements.SetReference("/Pg", sharp.Pages[marks[0].PageNumber - 1]);

            foreach (var mark in marks)
            {
                kids.Elements.Add(new PdfInteger(mark.MarkedContentId));
                Record(parents, mark, self);
            }
        }

        foreach (var child in children)
            kids.Elements.Add(child.Reference!);

        self.Elements.SetValue("/K", kids);

        // The description a reader announces where the content itself says nothing — an image with
        // no text of its own is the case that matters.
        if (element is FigureElement figure && figure.AlternateText is { Length: > 0 } alt)
            self.Elements.SetString("/Alt", alt);

        if (element.Language is { Length: > 0 } language)
            self.Elements.SetString("/Lang", language);

        AttachToParent(parent, self);
        written++;

        return self;
    }

    private static void AttachToParent(PdfDictionary parent, PdfDictionary child)
    {
        // The root and an intermediate element hold their children the same way, so this does not
        // need to know which it has.
        var kids = parent.Elements.GetArray("/K");

        if (kids is null)
        {
            kids = new PdfArray(parent.Owner);
            parent.Elements.SetValue("/K", kids);
        }

        kids.Elements.Add(child.Reference!);
    }

    private static void Record(
        Dictionary<int, PdfArray> parents, MarkedContentReference mark, PdfDictionary element)
    {
        if (!parents.TryGetValue(mark.PageNumber, out var entries))
            parents[mark.PageNumber] = entries = new PdfArray(element.Owner);

        // Indexed BY identifier, so a gap has to be filled rather than skipped: the reader looks up
        // position N, not the Nth entry.
        while (entries.Elements.Count < mark.MarkedContentId)
            entries.Elements.Add(PdfNull.Value);

        if (entries.Elements.Count == mark.MarkedContentId)
            entries.Elements.Add(element.Reference!);
        else
            entries.Elements[mark.MarkedContentId] = element.Reference!;
    }

    /// <summary>
    /// Writes the number tree that gets a reader from a piece of marked content back to the element
    /// describing it. Without it every tag written above is unreachable.
    /// </summary>
    private void WriteParentTree(PdfDictionary root, Dictionary<int, PdfArray> parents)
    {
        var parentTree = new PdfDictionary(sharp);
        var numbers = new PdfArray(sharp);

        int key = 0;

        foreach (int pageNumber in parents.Keys.OrderBy(n => n))
        {
            var entries = parents[pageNumber];
            sharp.Internals.AddObject(entries);

            numbers.Elements.Add(new PdfInteger(key));
            numbers.Elements.Add(entries.Reference!);

            sharp.Pages[pageNumber - 1].Elements.SetInteger("/StructParents", key);
            key++;
        }

        parentTree.Elements.SetValue("/Nums", numbers);
        sharp.Internals.AddObject(parentTree);

        root.Elements.SetReference("/ParentTree", parentTree);
        root.Elements.SetInteger("/ParentTreeNextKey", key);
    }

    #endregion

    #region Tag names

    /// <summary>
    /// The PDF structure type for an element, or null for anything that should not be tagged.
    ///
    /// Artifacts return null deliberately. A running head or a page number is page furniture, and
    /// putting it in the reading order drops "Page 4 of 12" into the middle of a sentence every
    /// time the reader crosses a page boundary.
    /// </summary>
    internal static string? TagFor(DocumentElement element) => element switch
    {
        ArtifactElement => null,

        HeadingElement heading => heading.Level switch
        {
            HeadingLevel.Level1 => "H1",
            HeadingLevel.Level2 => "H2",
            HeadingLevel.Level3 => "H3",
            HeadingLevel.Level4 => "H4",
            HeadingLevel.Level5 => "H5",
            HeadingLevel.Level6 => "H6",
            _ => "H2",
        },

        ParagraphElement => "P",
        CaptionElement => "Caption",
        BlockQuoteElement => "BlockQuote",
        CodeElement => "Code",
        NoteElement => "Note",

        ListElement => "L",
        ListItemElement => "LI",

        TableElement => "Table",
        TableRowElement => "TR",
        TableCellElement cell => cell.CellRole == TableCellRole.Data ? "TD" : "TH",

        FigureElement => "Figure",
        SectionElement => "Sect",

        // Links and form fields reach the tree through their annotations, not through page content,
        // and are handled by AnnotationStructureTagger. Tagging them here as well would describe
        // the same thing twice.
        _ => null,
    };

    #endregion

    #region Page content

    internal static byte[] ReadContent(PdfPage page)
    {
        using var buffer = new MemoryStream();

        foreach (var item in page.Contents.Elements)
        {
            var stream = item switch
            {
                PdfReference reference => (reference.Value as PdfDictionary)?.Stream,
                PdfDictionary dictionary => dictionary.Stream,
                _ => null,
            };

            if (stream is null)
                continue;

            byte[] bytes = stream.UnfilteredValue;
            buffer.Write(bytes, 0, bytes.Length);

            // Streams are concatenated as if they were one, and a stream that ended mid-token would
            // otherwise fuse its last operator to the next stream's first.
            buffer.WriteByte((byte)'\n');
        }

        return buffer.ToArray();
    }

    private void ReplaceContent(PdfPage page, byte[] content)
    {
        var replacement = new PdfDictionary(sharp);
        replacement.CreateStream(content);
        sharp.Internals.AddObject(replacement);

        var array = new PdfArray(sharp);
        array.Elements.Add(replacement.Reference!);

        page.Elements.SetValue("/Contents", array);
    }

    #endregion
}

#endregion
