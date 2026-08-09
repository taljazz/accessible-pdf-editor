using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;
using UglyToad.PdfPig.Util;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using SharpDocument = PdfSharp.Pdf.PdfDocument;

namespace AccessiblePdfEditor.Persistence;

// =====================================================================================
//  StructurePreservation.cs
//
//  The safety check that stops this editor destroying the accessibility it exists to
//  protect.
//
//  THE PROBLEM, established by testing against real files rather than by reading docs:
//
//  PDFsharp 6.2.4 cannot see objects stored inside an object stream (/ObjStm). Object
//  streams are what every modern producer emits — Word, Acrobat, InDesign, anything
//  writing PDF 1.5 or later. In such a file the catalog's /StructTreeRoot resolves to null
//  as far as PDFsharp is concerned, and on save it writes the literal "/StructTreeRoot
//  null" and drops every structure element — while carefully preserving /MarkInfo
//  <</Marked true>>, every page's /StructParents, and all the BDC/EMC marked content.
//
//  The result is a file that still CLAIMS to be tagged, whose page text is byte-identical,
//  whose page count is unchanged, and whose entire logical structure has been deleted.
//  Testing found this on 24 out of 24 real tagged PDFs — trees of 47, 55, 472, 2553 and
//  4809 elements reduced to nothing — with no exception and no warning at any point.
//
//  A page-count check does not catch it. A text comparison does not catch it. Re-reading
//  the file with the same library does not catch it. The only person who would ever find
//  out is the blind reader who opens the document afterwards and discovers that its
//  headings have vanished.
//
//  For an accessibility editor that is the worst bug it is possible to have: the tool
//  silently strips accessibility from precisely those documents that had it.
//
//  So no save happens without this check. When the danger is present the editor says so
//  plainly and, wherever it can, transplants the tree instead.
// =====================================================================================

#region StructureSafetyReport

/// <summary>What inspecting a file found about whether saving it is safe.</summary>
public sealed record StructureSafetyReport
{
    /// <summary>Whether the file carries a logical structure tree.</summary>
    public bool HasStructureTree { get; init; }

    /// <summary>How many structure elements were found, counted independently of PDFsharp.</summary>
    public int StructureElementCount { get; init; }

    /// <summary>
    /// Whether the library that will write the file can actually see the structure tree. When this
    /// is false and <see cref="HasStructureTree"/> is true, saving would delete it.
    /// </summary>
    public bool WriterCanSeeStructureTree { get; init; }

    /// <summary>Whether the file carries a digital signature that saving would invalidate.</summary>
    public bool HasDigitalSignature { get; init; }

    /// <summary>Whether the file declares itself as tagged.</summary>
    public bool ClaimsToBeTagged { get; init; }

    /// <summary>Problems encountered while inspecting. An inspection that failed is treated as unsafe.</summary>
    public IReadOnlyList<string> InspectionProblems { get; init; } = [];

    /// <summary>
    /// True when saving would silently destroy accessibility structure that is currently in the
    /// file. The single most important thing this type reports.
    /// </summary>
    public bool WouldDestroyStructure => HasStructureTree && !WriterCanSeeStructureTree;

    /// <summary>Whether the file can be saved without losing anything the editor knows about.</summary>
    public bool IsSafeToSave => !WouldDestroyStructure && !HasDigitalSignature;

    /// <summary>
    /// The explanation given to the user when saving is refused or qualified. Written to be spoken:
    /// it says what would be lost, why, and what to do instead, in that order.
    /// </summary>
    public string BuildWarning()
    {
        var parts = new List<string>(3);

        if (WouldDestroyStructure)
        {
            parts.Add(
                $"This document contains {StructureElementCount} accessibility tags — its headings, " +
                "lists, tables and image descriptions. They are stored in a compressed form that " +
                "this editor's save process cannot read, so saving would delete all of them while " +
                "leaving the document still claiming to be tagged. That would make it harder to " +
                "read with a screen reader than it is now.");
        }

        if (HasDigitalSignature)
        {
            parts.Add(
                "This document is digitally signed. Saving rewrites the whole file, which will " +
                "break the signature. The signature will still appear to be present but will no " +
                "longer be valid.");
        }

        return string.Join(" ", parts);
    }
}

#endregion

#region StructureSafetyInspector

/// <summary>
/// Inspects a file to determine whether saving it would destroy structure the editor cannot see.
/// </summary>
public static class StructureSafetyInspector
{
    #region Inspection

    /// <summary>
    /// Compares what an independent parser can see in a file against what the writing library can
    /// see. The disagreement between the two IS the finding: one library resolving a structure tree
    /// that the other reports as absent is exactly the condition under which saving deletes it.
    /// </summary>
    public static StructureSafetyReport Inspect(string filePath)
    {
        var problems = new List<string>();

        var (hasTree, elementCount, claimsTagged, hasSignature) = InspectWithReader(filePath, problems);
        bool writerSeesTree = InspectWithWriter(filePath, problems);

        return new StructureSafetyReport
        {
            HasStructureTree = hasTree,
            StructureElementCount = elementCount,
            WriterCanSeeStructureTree = writerSeesTree,
            ClaimsToBeTagged = claimsTagged,
            HasDigitalSignature = hasSignature,
            InspectionProblems = problems,
        };
    }

    /// <summary>
    /// Reads the file with PdfPig, which resolves objects inside object streams correctly, and
    /// counts what is really there.
    /// </summary>
    private static (bool HasTree, int Count, bool ClaimsTagged, bool HasSignature) InspectWithReader(
        string filePath, List<string> problems)
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

            bool claimsTagged = ReadMarkedFlag(catalog, scanner);
            bool hasSignature = ReadSignatureFlag(catalog, scanner);

            if (!catalog.TryGetOptionalTokenDirect(
                    NameToken.Create("StructTreeRoot"), scanner, out DictionaryToken? root)
                || root is null)
            {
                return (false, 0, claimsTagged, hasSignature);
            }

            int count = CountStructureElements(root, scanner);
            return (count > 0, count, claimsTagged, hasSignature);
        }
        catch (Exception ex)
        {
            // An inspection that cannot run must not be read as "everything is fine". It is recorded
            // as a problem, and the caller treats an unverifiable file as one to be careful with.
            problems.Add($"The document's structure could not be inspected: {ex.Message}");
            return (false, 0, false, false);
        }
    }

    /// <summary>
    /// Opens the file with PDFsharp — the library that will do the writing — and asks whether it can
    /// resolve the structure tree.
    /// </summary>
    private static bool InspectWithWriter(string filePath, List<string> problems)
    {
        try
        {
            // Opened in Modify mode specifically because that is the mode the save path uses. A
            // cheaper mode might resolve objects differently, and the only question worth asking
            // here is what will happen during the actual save.
            using var sharp = PdfReader.Open(filePath, PdfDocumentOpenMode.Modify);

            var root = sharp.Internals.Catalog.Elements.GetDictionary("/StructTreeRoot");

            // A tree PDFsharp cannot resolve comes back as null or as a dictionary with nothing in
            // it. Either way it has nothing to write, and what it writes is nothing.
            return root is not null && root.Elements.Count > 0;
        }
        catch (Exception ex)
        {
            problems.Add($"The document could not be opened for saving: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Reading individual facts

    private static bool ReadMarkedFlag(DictionaryToken catalog, IPdfTokenScanner scanner)
    {
        try
        {
            if (!catalog.TryGetOptionalTokenDirect(
                    NameToken.Create("MarkInfo"), scanner, out DictionaryToken? markInfo)
                || markInfo is null)
            {
                return false;
            }

            return markInfo.TryGet(NameToken.Create("Marked"), out BooleanToken? marked)
                && marked?.Data == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Detects a signature that has actually been APPLIED. Saving rewrites the whole file, so any
    /// applied signature stops matching the bytes it signed — and the signature dictionary survives
    /// intact, which makes the damage look like success.
    ///
    /// Deliberately NOT the form's /SigFlags. That flag only says the document contains a signature
    /// FIELD, which an unsigned form waiting to be signed also does. Testing it would refuse to
    /// save every blank contract in existence, including the ones this editor exists to help
    /// someone fill in and sign. What matters is whether a field carries a /V — the signature
    /// dictionary itself.
    /// </summary>
    private static bool ReadSignatureFlag(DictionaryToken catalog, IPdfTokenScanner scanner)
    {
        try
        {
            if (!catalog.TryGetOptionalTokenDirect(
                    NameToken.AcroForm, scanner, out DictionaryToken? acroForm)
                || acroForm is null)
            {
                return false;
            }

            if (!acroForm.TryGetOptionalTokenDirect(
                    NameToken.Create("Fields"), scanner, out ArrayToken? fields)
                || fields is null)
            {
                return false;
            }

            return AnyFieldIsSigned(fields, scanner, depth: 0);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Walks the field tree looking for a signature field that holds a value. Depth-bounded, since
    /// this runs on every save and a malformed field tree can contain a cycle.
    /// </summary>
    private static bool AnyFieldIsSigned(ArrayToken fields, IPdfTokenScanner scanner, int depth)
    {
        const int maximumDepth = 32;

        if (depth > maximumDepth)
            return false;

        foreach (var entry in fields.Data)
        {
            DictionaryToken? field = entry switch
            {
                IndirectReferenceToken reference => Resolve(reference, scanner),
                DictionaryToken direct => direct,
                _ => null,
            };

            if (field is null)
                continue;

            bool isSignatureField =
                field.TryGet(NameToken.Create("FT"), out NameToken? type)
                && type?.Data == "Sig";

            // A signature field with a /V carries an applied signature. Without one it is an empty
            // box waiting for somebody to sign it, which is not a reason to refuse a save.
            if (isSignatureField && field.ContainsKey(NameToken.V))
                return true;

            if (field.TryGetOptionalTokenDirect(NameToken.Kids, scanner, out ArrayToken? kids)
                && kids is not null
                && AnyFieldIsSigned(kids, scanner, depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    private static DictionaryToken? Resolve(IndirectReferenceToken reference, IPdfTokenScanner scanner)
    {
        try
        {
            return scanner.Get(reference.Data)?.Data as DictionaryToken;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Counts the structure elements reachable from the tree root.
    ///
    /// Bounded on both depth and total count: a malformed tree can contain a reference cycle, and
    /// this runs on every save, so it must terminate on a hostile file rather than hang the editor
    /// at the moment the user pressed Ctrl+S.
    /// </summary>
    private static int CountStructureElements(DictionaryToken root, IPdfTokenScanner scanner)
    {
        const int maximumElements = 100_000;
        const int maximumDepth = 64;

        int count = 0;
        var visited = new HashSet<(long ObjectNumber, int Generation)>();

        void Walk(DictionaryToken node, int depth)
        {
            if (count >= maximumElements || depth > maximumDepth)
                return;

            if (!node.TryGet(NameToken.K, out IToken? kids))
                return;

            WalkToken(kids, depth);
        }

        void WalkToken(IToken token, int depth)
        {
            if (count >= maximumElements || depth > maximumDepth)
                return;

            switch (token)
            {
                case IndirectReferenceToken reference:
                    var key = (reference.Data.ObjectNumber, reference.Data.Generation);
                    if (!visited.Add(key))
                        return;

                    try
                    {
                        var resolved = scanner.Get(reference.Data)?.Data;
                        if (resolved is not null)
                            WalkToken(resolved, depth);
                    }
                    catch
                    {
                        // A reference that will not resolve is one element uncounted, not a reason
                        // to abandon the count.
                    }

                    return;

                case ArrayToken array:
                    foreach (var item in array.Data)
                        WalkToken(item, depth + 1);
                    return;

                case DictionaryToken dictionary:
                    // Only dictionaries that declare a structure type are elements. The tree also
                    // contains marked-content references and object references, which are pointers
                    // into the page rather than nodes of their own.
                    if (dictionary.ContainsKey(NameToken.S))
                        count++;

                    Walk(dictionary, depth + 1);
                    return;
            }
        }

        Walk(root, 0);
        return count;
    }

    #endregion
}

#endregion
