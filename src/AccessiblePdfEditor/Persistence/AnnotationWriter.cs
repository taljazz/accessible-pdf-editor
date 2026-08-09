using System.Globalization;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace AccessiblePdfEditor.Persistence;

// =====================================================================================
//  AnnotationWriter.cs
//
//  Writes comments into the file: the new ones, the changed ones, and the removals.
//
//  Raw dictionaries throughout, for the same reason as FormWriter: PDFsharp's typed
//  annotation classes build fonts and appearance streams eagerly, which both drags in a
//  font dependency this program deliberately does not need for text-only work, and
//  regenerates appearances on annotations the user never touched.
//
//  WHAT MAKES AN ANNOTATION ACCESSIBLE
//
//  /Contents is the part a screen reader reads. Everything else — the icon, the colour,
//  the position — is for people who can see the page. So /Contents is never left empty:
//  a highlight with no note would be announced as "highlight" and nothing else, which
//  tells a listener that something was marked but not what or why.
//
//  /T carries the author, which is what lets a reader say "Denise wrote: …" rather than
//  reading an anonymous fragment. /M carries the date in PDF's own format, so other
//  editors sort the thread correctly.
//
//  FINDING AN ANNOTATION AGAIN
//
//  Editing and deleting both need to locate an annotation that is already in the file.
//  /NM is the identifier meant for that, and this program writes one on everything it
//  creates. But most real PDFs do not write /NM at all, so there is a fallback: match on
//  subtype and rectangle, which is stable because nothing here ever moves an annotation.
//  Without the fallback, editing a comment in a file produced by anything other than this
//  program would silently do nothing.
//
//  TAGGING
//
//  A written annotation is also given a place in the structure tree, because PDF/UA requires
//  annotations to be tagged and a checker reports an untagged one as a fault. That work is in
//  AnnotationStructureTagger, kept separate because the two jobs fail independently: a
//  document with no structure tree still gets a perfectly good comment, it just has nowhere
//  to file the tag.
//
//  This is safe in the case that worries this project most. A structure tree PDFsharp cannot
//  see — the /ObjStm case measured at 24 of 24 real tagged documents — comes back as null,
//  so nothing is written and nothing is damaged. Those documents are refused by
//  StructureSafetyInspector before the save gets this far in any event.
// =====================================================================================

#region AnnotationWriteSummary

/// <summary>What a write of the annotations actually did, for the save's verification step.</summary>
public readonly record struct AnnotationWriteSummary(int Added, int Edited, int Deleted, int TagsRemoved)
{
    /// <summary>
    /// How many annotations the file should have gained or lost. The save compares before and after
    /// and rolls back on any unexplained loss, so a deliberate deletion has to be declared or it
    /// would look exactly like damage.
    /// </summary>
    public int NetChange => Added - Deleted;

    public bool DidAnything => Added > 0 || Edited > 0 || Deleted > 0;
}

#endregion

#region AnnotationWriter

/// <summary>Writes added, edited and deleted comments into a PDFsharp document.</summary>
internal sealed class AnnotationWriter
{
    private readonly PdfDocument _sharp;
    private readonly List<string> _warnings;

    public AnnotationWriter(PdfDocument sharp, List<string> warnings)
    {
        _sharp = sharp;
        _warnings = warnings;
    }

    #region The whole job

    public AnnotationWriteSummary Write(PdfDocumentModel document)
    {
        int added = 0, edited = 0, deleted = 0;

        // Counted exactly rather than estimated. This number is handed to the save's loss check as
        // "I meant to remove this many tags", and an estimate there would either refuse a legitimate
        // save or, worse, excuse a real loss of somebody's headings.
        int tagsRemoved = 0;

        // Deletions first. Doing them after the additions would mean searching an array that now
        // contains this session's new annotations, and a rectangle-based match could then find the
        // wrong one.
        foreach (var annotation in document.DeletedAnnotations)
        {
            if (Delete(annotation, ref tagsRemoved))
                deleted++;
        }

        foreach (var annotation in document.Annotations.Where(a => a.NeedsWriting))
        {
            if (annotation.IsUnsaved)
            {
                if (Add(annotation))
                    added++;
            }
            else if (Edit(annotation))
            {
                edited++;
            }
        }

        return new AnnotationWriteSummary(added, edited, deleted, tagsRemoved);
    }

    #endregion

    #region Adding

    private bool Add(AnnotationElement annotation)
    {
        var page = PageFor(annotation.PageNumber);

        if (page is null)
        {
            _warnings.Add($"A comment could not be saved: page {annotation.PageNumber} was not found.");
            return false;
        }

        var dictionary = new PdfDictionary(_sharp);

        dictionary.Elements.SetName("/Type", "/Annot");
        dictionary.Elements.SetName("/Subtype", SubtypeFor(annotation.AnnotationKind));
        dictionary.Elements.SetValue("/Rect", RectangleFor(annotation));

        // The text a screen reader reads. Written even when empty so the key exists and other
        // editors show an editable note rather than an unlabelled mark.
        dictionary.Elements.SetString("/Contents", annotation.Contents);

        if (annotation.Author is { Length: > 0 } author)
            dictionary.Elements.SetString("/T", author);

        dictionary.Elements.SetString("/M", FormatPdfDate(annotation.ModifiedAt ?? DateTimeOffset.Now));

        if (annotation.SourceObjectId is { Length: > 0 } name)
            dictionary.Elements.SetString("/NM", name);

        // Print, so the comment exists on paper as well as on screen. A reviewer who prints the
        // document to read it should not find the comments have vanished.
        dictionary.Elements.SetInteger("/F", 4);

        ApplyAppearanceHints(dictionary, annotation);

        // Threads: /IRT points at the comment being answered, /RT /R says it is a reply rather
        // than a grouped annotation. Without these a reply reads as an unrelated second comment.
        if (annotation.InReplyTo is { } parent && FindExisting(parent) is { } parentDictionary)
        {
            dictionary.Elements.SetReference("/IRT", parentDictionary);
            dictionary.Elements.SetName("/RT", "/R");
        }

        _sharp.Internals.AddObject(dictionary);
        AnnotationsArrayFor(page).Elements.Add(dictionary.Reference!);

        // A place in the reading order, not just on the page. Does nothing when the document has
        // no structure tree, which is the ordinary case and not a failure — the comment is still
        // perfectly readable, it just has nowhere to be filed.
        AnnotationStructureTagger.Tag(_sharp, page, dictionary, DescribeForTag(annotation));

        annotation.IsUnsaved = false;
        annotation.IsEdited = false;

        return true;
    }

    /// <summary>
    /// The visual part: an icon for a note, a coloured band for a highlight.
    ///
    /// Deliberately minimal. No appearance stream is generated — viewers build one from these
    /// properties, and a hand-drawn appearance would be one more thing to get wrong on a surface
    /// the primary user cannot check.
    /// </summary>
    private static void ApplyAppearanceHints(PdfDictionary dictionary, AnnotationElement annotation)
    {
        switch (annotation.AnnotationKind)
        {
            case AnnotationKind.Highlight:
            case AnnotationKind.Underline:
            case AnnotationKind.StrikeOut:
            case AnnotationKind.Squiggly:
                // A text-markup annotation is defined by its quad points, not its rectangle. Without
                // them the mark has no extent and most viewers draw nothing at all.
                dictionary.Elements.SetValue("/QuadPoints", QuadPointsFor(dictionary, annotation.Bounds));
                dictionary.Elements.SetValue("/C", Colour(dictionary, 1.0, 0.92, 0.23));
                break;

            default:
                dictionary.Elements.SetName("/Name", "/Comment");
                dictionary.Elements.SetValue("/C", Colour(dictionary, 1.0, 0.82, 0.24));
                dictionary.Elements.SetBoolean("/Open", false);
                break;
        }
    }

    #endregion

    #region Editing and deleting

    private bool Edit(AnnotationElement annotation)
    {
        var existing = FindExisting(annotation);

        if (existing is null)
        {
            _warnings.Add(
                $"A changed comment could not be found in the file, so its new text was not saved: " +
                $"“{Shorten(annotation.Contents)}”.");

            return false;
        }

        existing.Elements.SetString("/Contents", annotation.Contents);
        existing.Elements.SetString("/M", FormatPdfDate(DateTimeOffset.Now));

        // A stale appearance stream would keep showing the old text in viewers that trust it over
        // /Contents. Removing it makes them rebuild from what is actually there now.
        existing.Elements.Remove("/AP");

        annotation.IsEdited = false;
        return true;
    }

    private bool Delete(AnnotationElement annotation, ref int tagsRemoved)
    {
        var page = PageFor(annotation.PageNumber);

        if (page is null)
            return false;

        var annotations = page.Elements.GetArray("/Annots");

        if (annotations is null)
            return false;

        for (int i = 0; i < annotations.Elements.Count; i++)
        {
            if (Resolve(annotations.Elements[i]) is not { } candidate)
                continue;

            if (!Matches(candidate, annotation))
                continue;

            annotations.Elements.RemoveAt(i);

            // Its popup is now orphaned, and an orphaned popup shows in some viewers as an empty
            // note the user cannot get rid of.
            RemovePopupOf(annotations, candidate);

            // And its tag, which would otherwise be a structure element describing an annotation
            // that no longer exists — a broken tree, and a worse fault than the one just fixed.
            if (AnnotationStructureTagger.Untag(_sharp, candidate))
                tagsRemoved++;

            return true;
        }

        _warnings.Add(
            $"A deleted comment could not be found in the file, so it may still be there when " +
            $"the document is re-opened: “{Shorten(annotation.Contents)}”.");

        return false;
    }

    private static void RemovePopupOf(PdfArray annotations, PdfDictionary parent)
    {
        if (parent.Elements.GetDictionary("/Popup") is not { } popup)
            return;

        for (int i = annotations.Elements.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(Resolve(annotations.Elements[i]), popup))
                annotations.Elements.RemoveAt(i);
        }
    }

    #endregion

    #region Finding an annotation that is already in the file

    private PdfDictionary? FindExisting(AnnotationElement annotation)
    {
        var page = PageFor(annotation.PageNumber);
        var annotations = page?.Elements.GetArray("/Annots");

        if (annotations is null)
            return null;

        for (int i = 0; i < annotations.Elements.Count; i++)
        {
            if (Resolve(annotations.Elements[i]) is { } candidate && Matches(candidate, annotation))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Whether a dictionary in the file is the annotation held in the model.
    ///
    /// /NM is the identifier the format provides, and is decisive when present. Most real documents
    /// do not write one, so the fallback compares subtype and rectangle — safe here because nothing
    /// in this program ever moves an annotation, so a rectangle identifies one for as long as the
    /// session lasts.
    /// </summary>
    private static bool Matches(PdfDictionary candidate, AnnotationElement annotation)
    {
        if (annotation.SourceObjectId is { Length: > 0 } wanted)
        {
            string? name = candidate.Elements.GetString("/NM");

            if (!string.IsNullOrEmpty(name))
                return string.Equals(name, wanted, StringComparison.Ordinal);
        }

        string subtype = candidate.Elements.GetName("/Subtype");

        if (!string.Equals(subtype, SubtypeFor(annotation.AnnotationKind), StringComparison.Ordinal))
            return false;

        return RectangleMatches(candidate.Elements.GetArray("/Rect"), annotation.Bounds);
    }

    private static bool RectangleMatches(PdfArray? rectangle, PageRegion bounds)
    {
        if (rectangle is null || rectangle.Elements.Count < 4)
            return false;

        // A whole point of tolerance. PDF coordinates are written as decimals and round-tripping
        // through a text format does not always return the identical value.
        const double Tolerance = 1.0;

        return Math.Abs(rectangle.Elements.GetReal(0) - bounds.Left) < Tolerance
               && Math.Abs(rectangle.Elements.GetReal(1) - bounds.Bottom) < Tolerance
               && Math.Abs(rectangle.Elements.GetReal(2) - bounds.Right) < Tolerance
               && Math.Abs(rectangle.Elements.GetReal(3) - bounds.Top) < Tolerance;
    }

    private static PdfDictionary? Resolve(PdfItem? item) => item switch
    {
        PdfReference reference => reference.Value as PdfDictionary,
        PdfDictionary dictionary => dictionary,
        _ => null,
    };

    #endregion

    #region Page and geometry

    private PdfPage? PageFor(int pageNumber) =>
        pageNumber >= 1 && pageNumber <= _sharp.PageCount ? _sharp.Pages[pageNumber - 1] : null;

    private PdfArray AnnotationsArrayFor(PdfPage page)
    {
        if (page.Elements.GetArray("/Annots") is { } existing)
            return existing;

        var created = new PdfArray(_sharp);
        page.Elements.SetValue("/Annots", created);

        return created;
    }

    /// <summary>
    /// The rectangle for the annotation.
    ///
    /// A sticky note is drawn as a fixed-size icon whatever its rectangle says, so giving it the
    /// full extent of a long paragraph would put a note-sized icon in a paragraph-sized box and
    /// confuse the viewers that do honour it. A small square at the top left of the anchor is what
    /// every other editor produces. A text-markup annotation, by contrast, genuinely covers its
    /// anchor and gets the whole thing.
    /// </summary>
    private PdfArray RectangleFor(AnnotationElement annotation)
    {
        var bounds = annotation.Bounds;

        if (annotation.AnnotationKind is AnnotationKind.Comment or AnnotationKind.Popup
            or AnnotationKind.FileAttachment or AnnotationKind.Other)
        {
            const double IconSize = 20.0;
            double top = bounds.Top > 0 ? bounds.Top : IconSize;
            double left = bounds.Left;

            return new PdfArray(_sharp,
                new PdfReal(left), new PdfReal(top - IconSize),
                new PdfReal(left + IconSize), new PdfReal(top));
        }

        return new PdfArray(_sharp,
            new PdfReal(bounds.Left), new PdfReal(bounds.Bottom),
            new PdfReal(bounds.Right), new PdfReal(bounds.Top));
    }

    /// <summary>
    /// The four corners a text-markup annotation covers, in the order the format requires:
    /// upper-left, upper-right, lower-left, lower-right. Getting that order wrong is a classic
    /// bug — the mark renders as a bow tie, or not at all.
    /// </summary>
    private static PdfArray QuadPointsFor(PdfDictionary owner, PageRegion bounds) =>
        new(owner.Owner,
            new PdfReal(bounds.Left), new PdfReal(bounds.Top),
            new PdfReal(bounds.Right), new PdfReal(bounds.Top),
            new PdfReal(bounds.Left), new PdfReal(bounds.Bottom),
            new PdfReal(bounds.Right), new PdfReal(bounds.Bottom));

    private static PdfArray Colour(PdfDictionary owner, double red, double green, double blue) =>
        new(owner.Owner, new PdfReal(red), new PdfReal(green), new PdfReal(blue));

    #endregion

    #region Formatting

    /// <summary>
    /// A date in PDF's own format, D:YYYYMMDDHHmmSSOHH'mm'. Other editors parse this to sort a
    /// comment thread, so a plain string here would put the conversation in the wrong order.
    /// </summary>
    internal static string FormatPdfDate(DateTimeOffset when)
    {
        var offset = when.Offset;
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        var magnitude = offset.Duration();

        return string.Create(CultureInfo.InvariantCulture,
            $"D:{when:yyyyMMddHHmmss}{sign}{magnitude.Hours:D2}'{magnitude.Minutes:D2}'");
    }

    private static string SubtypeFor(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Highlight => "/Highlight",
        AnnotationKind.Underline => "/Underline",
        AnnotationKind.StrikeOut => "/StrikeOut",
        AnnotationKind.Squiggly => "/Squiggly",
        AnnotationKind.FreeText => "/FreeText",
        AnnotationKind.Stamp => "/Stamp",
        AnnotationKind.Ink => "/Ink",
        AnnotationKind.FileAttachment => "/FileAttachment",
        AnnotationKind.Popup => "/Popup",
        _ => "/Text",
    };

    private static string Shorten(string value) =>
        value.Length <= 40 ? value : value[..40].TrimEnd() + "…";

    /// <summary>
    /// What the structure element says the annotation is, for readers that take a description from
    /// the tag rather than from /Contents. A highlight with no note of its own is described by the
    /// text it covers, because "highlight" alone tells a listener nothing.
    /// </summary>
    private static string? DescribeForTag(AnnotationElement annotation)
    {
        if (annotation.Contents.Trim() is { Length: > 0 } contents)
            return contents;

        return annotation.AnchoredText is { Length: > 0 } anchored
            ? $"Marked: {Shorten(anchored)}"
            : null;
    }

    #endregion
}

#endregion
