using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace AccessiblePdfEditor.Persistence;

// =====================================================================================
//  AnnotationStructureTagger.cs
//
//  Puts a comment into the document's structure tree, so it has a place in the reading
//  order rather than merely existing on the page.
//
//  WHY THIS IS SEPARATE FROM WRITING THE ANNOTATION
//
//  Writing the annotation makes it readable — /Contents is what a screen reader speaks.
//  Tagging it makes it FINDABLE: PDF/UA requires annotations to appear in the structure
//  tree, and a checker run over the document reports an untagged annotation as a fault.
//  The two jobs fail independently, so they are separate: a document with no structure
//  tree still gets a perfectly good comment, it just does not get a tag it has nowhere to
//  put.
//
//  THE ONE DETAIL THAT IS SILENTLY WRONG IF YOU GUESS
//
//  An annotation joins the tree through an object reference, not through marked content,
//  and the two use DIFFERENT parent-tree shapes. From ISO 32000 clause 14.7.5:
//
//     "For an object identified as a content item by means of an object reference, the
//      value is an indirect reference to the parent structure element. For a content
//      stream containing marked-content sequences that are content items, the value is
//      an array of indirect references..."
//
//  So a page's /StructParents maps to an ARRAY indexed by MCID — which is what
//  SignatureFlattener builds — while an annotation's /StructParent maps to a SINGLE
//  reference. Writing an array here produces a file that opens, validates structurally,
//  and leads nowhere: the tag exists, and nothing can get from the annotation to it.
//
//  WHEN THIS DOES NOTHING, ON PURPOSE
//
//  If the document has no structure tree, none is created. That is a deliberate refusal
//  rather than an omission: creating one means setting /MarkInfo /Marked true, and a
//  document that CLAIMS to be tagged while its text is not is worse than an honest
//  untagged one — checkers and readers believe the claim, and the reader who trusts it
//  finds no headings.
//
//  It also does nothing when PDFsharp cannot see the structure tree, which is the /ObjStm
//  case this project has measured at length. That falls out for free: an unreadable tree
//  comes back as null here, so nothing is written — and the save itself is already refused
//  for those documents by StructureSafetyInspector.
// =====================================================================================

#region AnnotationStructureTagger

/// <summary>Adds and removes the structure-tree entries that make a comment part of the document.</summary>
internal static class AnnotationStructureTagger
{
    #region Tagging

    /// <summary>
    /// Gives an annotation a place in the structure tree.
    /// </summary>
    /// <returns>
    /// True when it was tagged. False when the document has no structure tree to put it in, which
    /// is the ordinary case for an untagged PDF and is not a failure.
    /// </returns>
    public static bool Tag(
        PdfDocument document, PdfPage page, PdfDictionary annotation, string? describedAs)
    {
        var root = document.Internals.Catalog.Elements.GetDictionary("/StructTreeRoot");

        // No tree, or one PDFsharp cannot resolve. Either way there is nothing to join.
        if (root is null || root.Elements.Count == 0)
            return false;

        if (annotation.Reference is null)
            return false;

        var element = new PdfDictionary(document);
        element.Elements.SetName("/Type", "StructElem");
        element.Elements.SetName("/S", "Annot");
        element.Elements.SetReference("/P", root);
        element.Elements.SetReference("/Pg", page);

        // Some readers take an annotation's description from /Alt on the structure element rather
        // than from /Contents. Writing both costs nothing and satisfies either.
        if (describedAs is { Length: > 0 })
            element.Elements.SetString("/Alt", describedAs);

        // The object reference is how a structure element points at something that is not marked
        // content. /Pg on the OBJR as well as on the element is what the specification asks for.
        var objectReference = new PdfDictionary(document);
        objectReference.Elements.SetName("/Type", "OBJR");
        objectReference.Elements.SetReference("/Obj", annotation);
        objectReference.Elements.SetReference("/Pg", page);

        element.Elements.SetValue("/K", objectReference);
        document.Internals.AddObject(element);

        AppendToKids(document, root, element);
        LinkIntoParentTree(document, root, annotation, element);

        return true;
    }

    private static void AppendToKids(PdfDocument document, PdfDictionary root, PdfDictionary element)
    {
        var kids = root.Elements.GetArray("/K");

        if (kids is null)
        {
            kids = new PdfArray(document);
            root.Elements.SetValue("/K", kids);
        }

        kids.Elements.Add(element.Reference!);
    }

    /// <summary>
    /// Wires the annotation to its structure element through the parent tree.
    ///
    /// The value stored is the element itself, NOT an array — see the note at the top of this file.
    /// This is the half that makes the tag reachable; without it the structure element exists and
    /// nothing can find it from the annotation.
    /// </summary>
    private static void LinkIntoParentTree(
        PdfDocument document, PdfDictionary root, PdfDictionary annotation, PdfDictionary element)
    {
        var parentTree = root.Elements.GetDictionary("/ParentTree");

        if (parentTree is null)
        {
            parentTree = new PdfDictionary(document);
            parentTree.Elements.SetValue("/Nums", new PdfArray(document));
            document.Internals.AddObject(parentTree);
            root.Elements.SetReference("/ParentTree", parentTree);
        }

        var numbers = parentTree.Elements.GetArray("/Nums");

        if (numbers is null)
        {
            numbers = new PdfArray(document);
            parentTree.Elements.SetValue("/Nums", numbers);
        }

        int key = NextParentTreeKey(root, numbers);

        numbers.Elements.Add(new PdfInteger(key));
        numbers.Elements.Add(element.Reference!);

        annotation.Elements.SetInteger("/StructParent", key);
        root.Elements.SetInteger("/ParentTreeNextKey", key + 1);
    }

    #endregion

    #region Untagging

    /// <summary>
    /// Removes the structure entries for an annotation that is being deleted.
    ///
    /// Not optional tidying. A structure element whose object reference points at an annotation
    /// that no longer exists is a broken tree: a checker reports it, and a reader following the
    /// reading order arrives at a tag describing nothing. Deleting a comment has to remove both
    /// halves or it trades one fault for a worse one.
    /// </summary>
    /// <returns>True when a structure element was removed, so the save can declare exactly that.</returns>
    public static bool Untag(PdfDocument document, PdfDictionary annotation)
    {
        var root = document.Internals.Catalog.Elements.GetDictionary("/StructTreeRoot");

        if (root is null || root.Elements.Count == 0)
            return false;

        var element = FindStructureElement(root, annotation);

        if (element is null)
            return false;

        RemoveFromKids(root, element);
        RemoveFromParentTree(root, annotation, element);

        return true;
    }

    /// <summary>
    /// The structure element describing an annotation.
    ///
    /// The parent tree is the direct route and is tried first, which is what /StructParent is for.
    /// The scan is the fallback for annotations written by something that did not maintain it —
    /// slower, but this only runs on deletion and a document has far fewer structure elements than
    /// it has words.
    /// </summary>
    private static PdfDictionary? FindStructureElement(PdfDictionary root, PdfDictionary annotation)
    {
        if (annotation.Elements.ContainsKey("/StructParent")
            && root.Elements.GetDictionary("/ParentTree")?.Elements.GetArray("/Nums") is { } numbers)
        {
            int key = annotation.Elements.GetInteger("/StructParent");

            for (int i = 0; i + 1 < numbers.Elements.Count; i += 2)
            {
                if (numbers.Elements[i] is PdfInteger number && number.Value == key)
                    return Resolve(numbers.Elements[i + 1]);
            }
        }

        return ScanForObjectReference(root.Elements.GetArray("/K"), annotation, depth: 0);
    }

    private static PdfDictionary? ScanForObjectReference(PdfArray? kids, PdfDictionary annotation, int depth)
    {
        // A malformed tree can contain a cycle, and this runs on a file this program did not write.
        if (kids is null || depth > 24)
            return null;

        for (int i = 0; i < kids.Elements.Count; i++)
        {
            if (Resolve(kids.Elements[i]) is not { } element)
                continue;

            if (PointsAt(element.Elements.GetValue("/K"), annotation))
                return element;

            if (ScanForObjectReference(element.Elements.GetArray("/K"), annotation, depth + 1) is { } found)
                return found;
        }

        return null;
    }

    private static bool PointsAt(PdfItem? kid, PdfDictionary annotation)
    {
        if (Resolve(kid) is not { } dictionary)
            return false;

        if (!string.Equals(dictionary.Elements.GetName("/Type"), "/OBJR", StringComparison.Ordinal))
            return false;

        return ReferenceEquals(Resolve(dictionary.Elements.GetValue("/Obj")), annotation);
    }

    private static void RemoveFromKids(PdfDictionary root, PdfDictionary element)
    {
        RemoveFrom(root.Elements.GetArray("/K"), element, depth: 0);
    }

    private static bool RemoveFrom(PdfArray? kids, PdfDictionary element, int depth)
    {
        if (kids is null || depth > 24)
            return false;

        for (int i = kids.Elements.Count - 1; i >= 0; i--)
        {
            var candidate = Resolve(kids.Elements[i]);

            if (ReferenceEquals(candidate, element))
            {
                kids.Elements.RemoveAt(i);
                return true;
            }

            if (candidate is not null && RemoveFrom(candidate.Elements.GetArray("/K"), element, depth + 1))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Clears the parent-tree entry.
    ///
    /// The key and its value are removed as a pair. Number-tree keys are not required to be
    /// contiguous, so leaving a gap is correct; renumbering the ones after it would break every
    /// other /StructParent in the document.
    /// </summary>
    private static void RemoveFromParentTree(
        PdfDictionary root, PdfDictionary annotation, PdfDictionary element)
    {
        if (root.Elements.GetDictionary("/ParentTree")?.Elements.GetArray("/Nums") is not { } numbers)
            return;

        for (int i = numbers.Elements.Count - 2; i >= 0; i -= 2)
        {
            if (ReferenceEquals(Resolve(numbers.Elements[i + 1]), element))
            {
                numbers.Elements.RemoveAt(i + 1);
                numbers.Elements.RemoveAt(i);
            }
        }

        annotation.Elements.Remove("/StructParent");
    }

    #endregion

    #region Shared

    private static int NextParentTreeKey(PdfDictionary root, PdfArray numbers)
    {
        int highest = -1;

        for (int i = 0; i + 1 < numbers.Elements.Count; i += 2)
        {
            if (numbers.Elements[i] is PdfInteger number)
                highest = Math.Max(highest, number.Value);
        }

        if (root.Elements.ContainsKey("/ParentTreeNextKey"))
            highest = Math.Max(highest, root.Elements.GetInteger("/ParentTreeNextKey") - 1);

        return highest + 1;
    }

    private static PdfDictionary? Resolve(PdfItem? item) => item switch
    {
        PdfReference reference => reference.Value as PdfDictionary,
        PdfDictionary dictionary => dictionary,
        _ => null,
    };

    #endregion
}

#endregion
