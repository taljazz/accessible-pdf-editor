using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;
using UglyToad.PdfPig.Util;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace AccessiblePdfEditor.Persistence;

// =====================================================================================
//  DocumentFingerprint.cs
//
//  A count of everything that matters in a document, taken before and after a save and
//  compared.
//
//  This is the safety net that does not depend on knowing what could go wrong.
//
//  The specific failures found in testing — a structure tree silently deleted, an outline
//  gutted by a page removal, annotations dropped — are all different bugs with different
//  causes, and enumerating them one at a time is a losing game: the next version of a
//  library will have a different set. What they have in common is that the saved file
//  contains LESS than the original did, and that is a single question with a single
//  answer.
//
//  So a fingerprint is taken when the document is opened, another after it is written, and
//  if anything went down that the user did not ask to lose, the save is rolled back and
//  they are told exactly what would have gone. The original is never replaced by a file
//  that shrank behind the user's back.
//
//  Both fingerprints are taken with PdfPig, never with the library doing the writing. A
//  writer checking its own output would agree with itself about a structure it cannot see,
//  which is precisely how the tag-deletion bug goes unnoticed.
// =====================================================================================

#region DocumentFingerprint

/// <summary>A count of a document's contents, for detecting silent loss across a save.</summary>
public sealed record DocumentFingerprint
{
    /// <summary>The number of pages.</summary>
    public int PageCount { get; init; }

    /// <summary>The number of structure elements, counted through the raw token layer.</summary>
    public int StructureElementCount { get; init; }

    /// <summary>The number of bookmarks.</summary>
    public int OutlineCount { get; init; }

    /// <summary>The number of annotations, across every page.</summary>
    public int AnnotationCount { get; init; }

    /// <summary>The number of form fields.</summary>
    public int FormFieldCount { get; init; }

    /// <summary>
    /// The total number of characters of extractable text. Catches content-stream damage that
    /// leaves the object counts unchanged.
    /// </summary>
    public long TextLength { get; init; }

    /// <summary>Whether the document declares itself tagged.</summary>
    public bool ClaimsToBeTagged { get; init; }

    /// <summary>Whether the fingerprint could be taken at all.</summary>
    public bool IsValid { get; init; }

    /// <summary>Why it could not be taken, when it could not.</summary>
    public string? Problem { get; init; }

    #region Taking a fingerprint

    /// <summary>
    /// Reads a file and counts what is in it. Always uses PdfPig, whatever wrote the file.
    /// </summary>
    /// <param name="samplePages">
    /// How many pages to measure text from. Text extraction dominates the cost on a long document,
    /// and a save that damaged content streams damages them throughout rather than on page 180
    /// alone, so a sample is enough and keeps saving quick.
    /// </param>
    public static DocumentFingerprint Take(string filePath, int samplePages = 20)
    {
        try
        {
            using var pig = PigDocument.Open(filePath, new ParsingOptions
            {
                UseLenientParsing = true,
                SkipMissingFonts = true,
            });

            var catalog = pig.Structure.Catalog.CatalogDictionary;
            var scanner = pig.Structure.TokenScanner;

            int annotations = 0;
            long textLength = 0;
            int measured = Math.Min(pig.NumberOfPages, samplePages);

            for (int number = 1; number <= pig.NumberOfPages; number++)
            {
                try
                {
                    var page = pig.GetPage(number);

                    annotations += page.GetAnnotations().Count();

                    if (number <= measured)
                        textLength += page.Text?.Length ?? 0;
                }
                catch
                {
                    // A page that will not read is itself a finding, but it must not stop the
                    // fingerprint: the counts from the other pages are still worth comparing.
                }
            }

            return new DocumentFingerprint
            {
                IsValid = true,
                PageCount = pig.NumberOfPages,
                StructureElementCount = CountStructureElements(catalog, scanner),
                OutlineCount = CountOutline(pig),
                AnnotationCount = annotations,
                FormFieldCount = CountFormFields(pig),
                TextLength = textLength,
                ClaimsToBeTagged = ReadMarkedFlag(catalog, scanner),
            };
        }
        catch (Exception ex)
        {
            return new DocumentFingerprint { IsValid = false, Problem = ex.Message };
        }
    }

    private static int CountStructureElements(DictionaryToken catalog, IPdfTokenScanner scanner)
    {
        try
        {
            if (!catalog.TryGetOptionalTokenDirect(
                    NameToken.Create("StructTreeRoot"), scanner, out DictionaryToken? root)
                || root is null)
            {
                return 0;
            }

            return StructureCounter.Count(root, scanner);
        }
        catch
        {
            return 0;
        }
    }

    private static int CountOutline(PigDocument pig)
    {
        try
        {
            return pig.TryGetBookmarks(out var bookmarks, allowContainerNode: true) && bookmarks is not null
                ? bookmarks.GetNodes().Count()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int CountFormFields(PigDocument pig)
    {
        try
        {
            if (!pig.TryGetForm(out var form) || form is null)
                return 0;

            return UglyToad.PdfPig.AcroForms.AcroFormExtensions.GetFields(form).Count();
        }
        catch
        {
            return 0;
        }
    }

    private static bool ReadMarkedFlag(DictionaryToken catalog, IPdfTokenScanner scanner)
    {
        try
        {
            return catalog.TryGetOptionalTokenDirect(
                       NameToken.Create("MarkInfo"), scanner, out DictionaryToken? markInfo)
                   && markInfo is not null
                   && markInfo.TryGet(NameToken.Create("Marked"), out BooleanToken? marked)
                   && marked?.Data == true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Comparing

    /// <summary>
    /// Compares a fingerprint taken after saving against the one taken when the document was
    /// opened, and describes anything that was lost.
    ///
    /// Only LOSSES are reported. A save that adds annotations or form fields is the user doing
    /// their job; a save that removes them without being asked is data loss.
    /// </summary>
    /// <param name="expectedPageChange">
    /// How many pages the user deliberately added or removed, so that an intended change is not
    /// reported as damage.
    /// </param>
    /// <param name="expectedFieldChange">
    /// How many form fields the user deliberately added or removed. Negative for removals — a
    /// flattened signature field is meant to disappear, and reporting that as data loss would
    /// refuse the very operation that was requested.
    /// </param>
    /// <param name="expectedAnnotationChange">
    /// How many annotations the user deliberately added or removed. A form field's widget IS an
    /// annotation, so consuming a signature field removes one of each.
    /// </param>
    /// <param name="expectedStructureChange">
    /// How many structure elements the user deliberately added or removed.
    ///
    /// Kept deliberately hard to reach for: losing accessibility tags is the failure this whole
    /// class exists to catch, so a caller passing a negative number here is asserting that it MEANT
    /// to remove a tag. Deleting a tagged comment is the case that needs it — the comment's /Annot
    /// element goes with it, and without declaring that, deleting a comment would roll back every
    /// other change in the same save.
    /// </param>
    public IReadOnlyList<string> FindLossesSince(
        DocumentFingerprint original,
        int expectedPageChange = 0,
        int expectedFieldChange = 0,
        int expectedAnnotationChange = 0,
        int expectedStructureChange = 0)
    {
        var losses = new List<string>();

        if (!IsValid)
        {
            losses.Add($"the saved file could not be read back ({Problem})");
            return losses;
        }

        if (!original.IsValid)
            return losses;

        int expectedPages = original.PageCount + expectedPageChange;
        if (PageCount < expectedPages)
            losses.Add($"{expectedPages - PageCount} pages");

        int expectedStructure = original.StructureElementCount + expectedStructureChange;

        if (StructureElementCount < expectedStructure)
        {
            int lost = expectedStructure - StructureElementCount;

            // Named as what they are to the user, not as what they are in the file. "Accessibility
            // tags" is the thing they would miss; "StructElem objects" is not.
            losses.Add(StructureElementCount == 0
                ? $"all {original.StructureElementCount} accessibility tags — the document's headings, " +
                  "lists, tables and image descriptions"
                : $"{lost} of {original.StructureElementCount} accessibility tags");
        }

        if (OutlineCount < original.OutlineCount)
            losses.Add($"{original.OutlineCount - OutlineCount} bookmarks");

        int expectedAnnotations = original.AnnotationCount + expectedAnnotationChange;

        if (AnnotationCount < expectedAnnotations)
            losses.Add($"{expectedAnnotations - AnnotationCount} comments or links");

        int expectedFields = original.FormFieldCount + expectedFieldChange;

        if (FormFieldCount < expectedFields)
            losses.Add($"{expectedFields - FormFieldCount} form fields");

        // Text is compared with a tolerance. Saving legitimately shifts whitespace by a character
        // or two, and a rigid comparison would refuse every save; a drop of more than a twentieth
        // is content going missing.
        if (original.TextLength > 0)
        {
            double retained = (double)TextLength / original.TextLength;

            if (retained < 0.95)
                losses.Add($"about {(int)((1 - retained) * 100)} percent of the document's text");
        }

        return losses;
    }

    /// <summary>
    /// The sentence read out when a save is rolled back. Says what would have been lost, in the
    /// user's terms, and confirms that nothing was actually changed.
    /// </summary>
    public static string DescribeLosses(IReadOnlyList<string> losses) =>
        $"Saving would have lost {string.Join(", ", losses)}. " +
        "Nothing has been changed and your original file is exactly as it was.";

    #endregion
}

#endregion

#region StructureCounter — shared walk over the structure tree

/// <summary>
/// Counts structure elements. Shared by the fingerprint and the safety inspector so that both
/// always agree about how many tags a document has — two different counts of the same thing would
/// make one of them wrong at exactly the moment it mattered.
/// </summary>
internal static class StructureCounter
{
    /// <summary>Counts the structure elements reachable from a tree root.</summary>
    public static int Count(DictionaryToken root, IPdfTokenScanner scanner)
    {
        const int maximumElements = 100_000;
        const int maximumDepth = 64;

        int count = 0;
        var visited = new HashSet<(long, int)>();

        void WalkChildren(DictionaryToken node, int depth)
        {
            if (count >= maximumElements || depth > maximumDepth)
                return;

            if (node.TryGet(NameToken.K, out IToken? kids))
                Walk(kids, depth);
        }

        void Walk(IToken token, int depth)
        {
            if (count >= maximumElements || depth > maximumDepth)
                return;

            switch (token)
            {
                case IndirectReferenceToken reference:
                    if (!visited.Add((reference.Data.ObjectNumber, reference.Data.Generation)))
                        return;

                    try
                    {
                        if (scanner.Get(reference.Data)?.Data is { } resolved)
                            Walk(resolved, depth);
                    }
                    catch
                    {
                        // One unresolvable reference, not a failed count.
                    }

                    return;

                case ArrayToken array:
                    foreach (var item in array.Data)
                        Walk(item, depth + 1);

                    return;

                case DictionaryToken dictionary:
                    // Only nodes declaring a structure type are elements; the tree also holds
                    // marked-content and object references, which point into the page instead.
                    if (dictionary.ContainsKey(NameToken.S))
                        count++;

                    WalkChildren(dictionary, depth + 1);
                    return;
            }
        }

        WalkChildren(root, 0);
        return count;
    }
}

#endregion
