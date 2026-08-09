using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Model.Forms;

namespace AccessiblePdfEditor.Auditing;

// =====================================================================================
//  AuditRules.cs
//
//  The individual accessibility checks. One class per way a PDF fails a blind reader.
//
//  Every rule here was chosen because it corresponds to a specific, concrete experience:
//  not a specification clause, but a moment where someone reading with a screen reader is
//  stopped, misled, or made to work harder than a sighted reader would.
//
//  Two principles run through all of them:
//
//  1. The consequence is stated in terms of what the reader EXPERIENCES. "Missing /Alt" is
//     a fact about a file. "You will hear nothing at all where this image is, and no way
//     to tell whether it mattered" is a reason to fix it.
//
//  2. Findings are collapsed where they repeat. A scanned document has a problem on every
//     page; reporting it two hundred times produces a list nobody reaches the end of, and
//     buries the four findings they could actually have acted on.
// =====================================================================================

#region Scanned documents — nothing to read at all

/// <summary>
/// Detects pages that are images of text with no text behind them.
///
/// The most complete failure a document can have: there is nothing to read, and no amount of
/// navigation will find any. Reported first and reported plainly, because the alternative is a
/// reader concluding the program is broken when in fact the document is empty of text.
/// </summary>
public sealed class ScannedDocumentRule : AuditRuleBase
{
    public override string Name => "scanned pages";

    protected override IEnumerable<AccessibilityIssue> CheckCore(PdfDocumentModel document)
    {
        var imageOnlyPages = document.Pages.Where(p => p.IsImageOnly).ToList();

        if (imageOnlyPages.Count == 0)
            yield break;

        bool wholeDocument = imageOnlyPages.Count == document.PageCount;

        yield return Issue(
            IssueSeverity.Blocker,
            IssueFixability.NotFixableHere,
            wholeDocument
                ? "This document is a scan with no text behind it"
                : $"{imageOnlyPages.Count} pages are scans with no text behind them",
            wholeDocument
                ? "There is nothing here that can be read aloud. The pages are pictures of text, " +
                  "and no screen reader can extract words from them."
                : $"Pages {DescribePages(imageOnlyPages)} cannot be read aloud at all. They are " +
                  "pictures of text.",
            "This editor cannot fix it. The document needs to be put through optical character " +
            "recognition first, by the person who produced it or by OCR software.",
            imageOnlyPages[0],
            imageOnlyPages.Count);
    }

    /// <summary>
    /// Lists page numbers compactly, collapsing runs. "Pages 4 to 9 and 12" rather than seven
    /// numbers read out one at a time.
    /// </summary>
    private static string DescribePages(IReadOnlyList<PageElement> pages)
    {
        var numbers = pages.Select(p => p.PageNumber).OrderBy(n => n).ToList();
        var runs = new List<string>();

        int start = numbers[0];
        int previous = start;

        for (int i = 1; i <= numbers.Count; i++)
        {
            bool ended = i == numbers.Count || numbers[i] != previous + 1;

            if (ended)
            {
                runs.Add(start == previous ? $"{start}" : $"{start} to {previous}");

                if (i < numbers.Count)
                {
                    start = numbers[i];
                    previous = start;
                }
            }
            else
            {
                previous = numbers[i];
            }

            if (runs.Count >= 6)
            {
                runs.Add("and others");
                break;
            }
        }

        return string.Join(", ", runs);
    }
}

#endregion

#region Unlabelled form fields — being asked for information without being told what

/// <summary>
/// Detects form fields with no usable name.
///
/// A blocker, and the one that costs people most in practice. The user is asked to type something
/// into a box that announces itself as "edit box" and nothing more. On a benefits claim or a job
/// application, guessing wrong has consequences that have nothing to do with software.
/// </summary>
public sealed class UnlabelledFieldRule : AuditRuleBase
{
    public override string Name => "unlabelled fields";

    public override bool AppliesTo(PdfDocumentModel document) => document.FormFields.Count > 0;

    protected override IEnumerable<AccessibilityIssue> CheckCore(PdfDocumentModel document)
    {
        foreach (var field in document.FormFields)
        {
            // Push buttons carry their caption on their face, so they are named even without a
            // tooltip. Signature fields cannot be filled in here anyway.
            if (field.FieldKind is FormFieldKind.PushButton or FormFieldKind.Signature)
                continue;

            if (field.ResolvedLabelSource == PdfFormField.LabelSource.None)
            {
                yield return Issue(
                    IssueSeverity.Blocker,
                    IssueFixability.FixableWithInput,
                    "A form field has no label",
                    "This field announces itself only as its type. There is no way to know what " +
                    "information it is asking for without seeing the page.",
                    "Press Enter to give it a name. The name is saved into the document, so it " +
                    "will work for everyone who opens it afterwards.",
                    field);
            }
            else if (field.ResolvedLabelSource is PdfFormField.LabelSource.NearbyText
                     or PdfFormField.LabelSource.FieldName)
            {
                // A guessed label is usable but unverified. Worth reporting so the user can confirm
                // it and make it permanent, but not worth blocking on.
                yield return Issue(
                    IssueSeverity.Moderate,
                    IssueFixability.FixableWithInput,
                    $"The label \"{field.Label}\" was guessed, not read from the document",
                    field.ResolvedLabelSource == PdfFormField.LabelSource.NearbyText
                        ? "This name was taken from text near the field on the page. It is usually " +
                          "right, but the document does not actually say so."
                        : "This name was derived from the field's internal name, which the form's " +
                          "designer may never have intended anyone to read.",
                    "Press Enter to confirm or correct it, which writes it into the document " +
                    "properly.",
                    field);
            }
        }
    }
}

#endregion

#region Missing alternate text — a hole in the document

/// <summary>
/// Detects figures with no description.
///
/// The classic accessibility failure, and the one this editor is best placed to repair. Findings
/// are produced per figure so each can be visited and described in turn, but very small images are
/// excluded: asking someone to describe forty one-pixel spacers is how a remediation session gets
/// abandoned halfway.
/// </summary>
public sealed class MissingAlternateTextRule : AuditRuleBase
{
    public override string Name => "missing image descriptions";

    public override bool AppliesTo(PdfDocumentModel document) => document.Figures.Count > 0;

    protected override IEnumerable<AccessibilityIssue> CheckCore(PdfDocumentModel document)
    {
        var needing = document.Figures
            .Where(f => f.NeedsAlternateText && !f.IsLikelyDecorativeBySize)
            .ToList();

        foreach (var figure in needing)
        {
            bool hasCaption = figure.Caption is { Text.Length: > 0 };

            yield return Issue(
                IssueSeverity.Serious,
                IssueFixability.FixableWithInput,
                "An image has no description",
                hasCaption
                    ? "There is a caption, but nothing describing the image itself. A caption says " +
                      "what an image is called; a description says what it shows."
                    : "A screen reader has nothing to say where this image is. You cannot tell " +
                      "whether it was decorative or the whole point of the page.",
                "Press Enter to describe it, or mark it as decorative if it carries no information.",
                figure);
        }

        // Tiny images are mentioned once, collectively, so the user knows they were considered and
        // deliberately left out rather than missed.
        int skipped = document.Figures.Count(f => f.NeedsAlternateText && f.IsLikelyDecorativeBySize);

        if (skipped > 0)
        {
            yield return Issue(
                IssueSeverity.Advisory,
                IssueFixability.AutomaticallyFixable,
                $"{skipped} very small images have no description",
                "These are small enough to be spacers, rules or bullet graphics rather than " +
                "pictures. They have been left out of the list above.",
                "They can all be marked decorative in one step.",
                occurrences: skipped);
        }
    }
}

#endregion

#region Untagged documents — everything is guesswork

/// <summary>
/// Reports that a document carries no real structure, so everything the reader navigates by is
/// this program's inference.
/// </summary>
public sealed class UntaggedDocumentRule : AuditRuleBase
{
    public override string Name => "document tagging";

    protected override IEnumerable<AccessibilityIssue> CheckCore(PdfDocumentModel document)
    {
        switch (document.TaggedStatus)
        {
            case TaggedStatus.Untagged:
                yield return Issue(
                    IssueSeverity.Serious,
                    IssueFixability.FixableWithInput,
                    "This document has no accessibility tags",
                    "Nothing in the file says what any of its content means. The headings, lists " +
                    "and tables you can navigate by have all been worked out from the page layout, " +
                    "and some of them will be wrong.",
                    "The structure can be reviewed and corrected, then written into the document " +
                    "so it is right for everyone who opens it afterwards.");
                break;

            case TaggedStatus.PartiallyTagged:
                yield return Issue(
                    IssueSeverity.Moderate,
                    IssueFixability.FixableWithInput,
                    "This document is only partly tagged",
                    "Some of its content says what it is and some does not, so the structure you " +
                    "navigate by is partly the document's own and partly guesswork.",
                    "The untagged parts can be reviewed and corrected.");
                break;
        }
    }
}

#endregion

#region Tables without headers — bare numbers with no meaning

/// <summary>
/// Detects tables whose cells are not distinguishable as headers or data.
///
/// Without header cells, every value in the table is announced on its own — "4200" with nothing to
/// say what it measures or when. A sighted reader gets that context free from the shape of the
/// page; a listener gets nothing.
/// </summary>
public sealed class TableWithoutHeadersRule : AuditRuleBase
{
    public override string Name => "tables without headers";

    public override bool AppliesTo(PdfDocumentModel document) => document.Tables.Count > 0;

    protected override IEnumerable<AccessibilityIssue> CheckCore(PdfDocumentModel document)
    {
        foreach (var table in document.Tables)
        {
            if (table.HasHeaderCells)
                continue;

            // A single-row or single-column table is a layout device rather than data, and marking
            // headers on it would be meaningless.
            if (table.RowCount < 2 || table.ColumnCount < 2)
                continue;

            yield return Issue(
                IssueSeverity.Serious,
                IssueFixability.FixableWithInput,
                $"A {table.RowCount} by {table.ColumnCount} table has no header cells",
                "Every cell in this table will be read as a bare value. You will hear the numbers " +
                "but nothing saying which column or row they belong to.",
                "Press Enter to mark its header row, after which each cell will be announced " +
                "together with its headings.",
                table);
        }
    }
}

#endregion

#region Missing document language — read in the wrong voice

/// <summary>
/// Detects a document that does not declare its language.
///
/// Cheap to fix and disproportionately valuable. Without it a screen reader uses whatever voice it
/// currently has, so a French document read by an English voice is not merely accented — it is
/// unintelligible.
/// </summary>
public sealed class MissingDocumentLanguageRule : AuditRuleBase
{
    public override string Name => "document language";

    protected override IEnumerable<AccessibilityIssue> CheckCore(PdfDocumentModel document)
    {
        if (!string.IsNullOrWhiteSpace(document.Metadata.Language))
            yield break;

        yield return Issue(
            IssueSeverity.Moderate,
            IssueFixability.AutomaticallyFixable,
            "This document does not say what language it is in",
            "A screen reader will read it in whatever voice it happens to be using. If that is not " +
            "the document's language, the pronunciation will be wrong throughout and may be " +
            "impossible to follow.",
            "The language can be set in one step. It applies to the whole document.");
    }
}

#endregion

#region No headings — nothing to navigate by

/// <summary>
/// Detects a document of any length with no headings at all.
///
/// Jumping between headings is how a screen reader user skims. A document without them can only be
/// read from the beginning, which for anything longer than a few pages means it effectively cannot
/// be searched by ear at all.
/// </summary>
public sealed class NoHeadingsRule : AuditRuleBase
{
    public override string Name => "no headings";

    public override bool AppliesTo(PdfDocumentModel document) =>
        document.PageCount >= 3 && document.TaggedStatus != TaggedStatus.ScannedWithoutText;

    protected override IEnumerable<AccessibilityIssue> CheckCore(PdfDocumentModel document)
    {
        if (document.Headings.Count > 0)
            yield break;

        // A document with bookmarks has a usable map even without headings, so the finding is
        // softened rather than dropped: bookmarks are the author's own structure.
        bool hasOutline = document.HasOutline;

        yield return Issue(
            hasOutline ? IssueSeverity.Moderate : IssueSeverity.Serious,
            IssueFixability.FixableWithInput,
            $"This {document.PageCount}-page document has no headings",
            hasOutline
                ? "There is nothing to jump between while reading. The document does have " +
                  "bookmarks, which give some way around it, but within a section you can only " +
                  "read from the start."
                : "There is no way to skim it. Without headings you can only read from the " +
                  "beginning, or search for words you already know are in it.",
            "Text that acts as a heading can be marked as one, which makes it a navigation point " +
            "for every reader afterwards.");
    }
}

#endregion

#region Skipped heading levels — a broken outline

/// <summary>
/// Detects headings that jump a level, such as a level 1 followed directly by a level 3.
///
/// The outline is how a listener builds a mental model of a document's shape. A skipped level makes
/// that model wrong: they hear level 3 and infer a level 2 section they have somehow missed, and go
/// looking for something that was never there.
/// </summary>
public sealed class SkippedHeadingLevelRule : AuditRuleBase
{
    public override string Name => "skipped heading levels";

    public override bool AppliesTo(PdfDocumentModel document) => document.Headings.Count >= 2;

    protected override IEnumerable<AccessibilityIssue> CheckCore(PdfDocumentModel document)
    {
        var headings = document.Headings
            .Where(h => h.Level != HeadingLevel.None)
            .OrderBy(h => h.ReadingOrder)
            .ToList();

        if (headings.Count < 2)
            yield break;

        var skips = new List<HeadingElement>();

        for (int i = 1; i < headings.Count; i++)
        {
            int previous = (int)headings[i - 1].Level;
            int current = (int)headings[i].Level;

            // Only descending jumps matter. Coming back up several levels at the end of a section
            // is perfectly normal.
            if (current > previous + 1)
                skips.Add(headings[i]);
        }

        if (skips.Count == 0)
            yield break;

        // Reported once with the first example rather than once per skip: on a badly structured
        // document the same mistake repeats throughout, and forty findings say no more than one.
        var first = skips[0];

        yield return Issue(
            IssueSeverity.Moderate,
            IssueFixability.FixableWithInput,
            skips.Count == 1
                ? "A heading skips a level"
                : $"{skips.Count} headings skip a level",
            $"\"{Shorten(first.Text)}\" is a level {(int)first.Level} heading, but the heading " +
            "before it was higher up than one level above. Someone navigating by headings will " +
            "think they have missed a section.",
            "The heading levels can be corrected, which repairs the outline.",
            first,
            skips.Count);
    }

    private static string Shorten(string text) =>
        text.Length <= 40 ? text : string.Concat(text.AsSpan(0, 37).TrimEnd(), "…");
}

#endregion

#region Uninformative link text — "click here", heard out of context

/// <summary>
/// Detects links whose text says nothing about where they go.
///
/// A sighted reader takes context from the surrounding sentence. A screen reader user very often
/// pulls up a list of every link on the page and moves through it — at which point twelve links all
/// reading "click here" are indistinguishable from one another.
/// </summary>
public sealed class UninformativeLinkTextRule : AuditRuleBase
{
    public override string Name => "unclear link text";

    public override bool AppliesTo(PdfDocumentModel document) => document.Links.Count > 0;

    protected override IEnumerable<AccessibilityIssue> CheckCore(PdfDocumentModel document)
    {
        var unclear = document.Links
            .Where(l => l.HasUninformativeText && string.IsNullOrWhiteSpace(l.Description))
            .ToList();

        if (unclear.Count == 0)
            yield break;

        var first = unclear[0];

        yield return Issue(
            IssueSeverity.Moderate,
            IssueFixability.FixableWithInput,
            unclear.Count == 1
                ? "A link's text does not say where it goes"
                : $"{unclear.Count} links have text that does not say where they go",
            "Pulling up a list of the links in this document would show entries that cannot be " +
            "told apart. Their text carries no clue about their destination.",
            "A description can be added to each link, which readers announce in place of the " +
            "visible text.",
            first,
            unclear.Count);
    }
}

#endregion

#region Missing title — announced by filename

/// <summary>
/// Detects a document with no title, or with a title the viewer is not told to display.
///
/// Both halves are checked together because either alone achieves nothing. A title that is not
/// displayed is never announced, and the display flag with no title leaves the reader with the
/// filename regardless.
/// </summary>
public sealed class MissingDocumentTitleRule : AuditRuleBase
{
    public override string Name => "document title";

    protected override IEnumerable<AccessibilityIssue> CheckCore(PdfDocumentModel document)
    {
        bool hasTitle = !string.IsNullOrWhiteSpace(document.Metadata.Title);

        if (!hasTitle)
        {
            yield return Issue(
                IssueSeverity.Advisory,
                IssueFixability.FixableWithInput,
                "This document has no title",
                $"It will be announced by its filename, \"{document.FileName}\", which is rarely " +
                "what anyone would call it.",
                "A title can be set, and the document told to display it.");
        }
        else if (!document.Metadata.DisplaysDocumentTitle)
        {
            yield return Issue(
                IssueSeverity.Advisory,
                IssueFixability.AutomaticallyFixable,
                "This document has a title but does not display it",
                $"It is called \"{document.Metadata.Title}\", but readers are not told to use that, " +
                "so they will announce the filename instead.",
                "This can be switched on in one step.");
        }
    }
}

#endregion

#region Unmarked page furniture — the same header two hundred times

/// <summary>
/// Detects running headers and footers that were never marked as page furniture.
///
/// A well-made PDF marks them so readers skip them. An unmarked one reads its running header at
/// every page boundary; over a long document that is dozens of interruptions carrying no
/// information, and it is one of the main reasons a technically readable PDF is exhausting.
/// </summary>
public sealed class UnmarkedPageFurnitureRule : AuditRuleBase
{
    public override string Name => "unmarked page furniture";

    public override bool AppliesTo(PdfDocumentModel document) => document.PageCount >= 5;

    protected override IEnumerable<AccessibilityIssue> CheckCore(PdfDocumentModel document)
    {
        // Text repeating in the same position across most pages, that was NOT already recognised as
        // furniture. The extractor marks what it can; this finds what got through.
        var counts = new Dictionary<string, List<TextElement>>(StringComparer.Ordinal);

        foreach (var page in document.Pages)
        {
            double topBand = page.Height * 0.88;
            double bottomBand = page.Height * 0.12;

            foreach (var text in page.SelfAndDescendants().OfType<TextElement>())
            {
                if (text.Kind == ElementKind.Artifact || text.Text.Trim().Length < 3)
                    continue;

                double y = text.Bounds.Bottom;
                if (y < topBand && y > bottomBand)
                    continue;

                string key = Ingestion.StructureExtractorBase.NormaliseForRepetitionMatch(text.Text);
                if (key.Length < 3)
                    continue;

                if (!counts.TryGetValue(key, out var list))
                    counts[key] = list = [];

                list.Add(text);
            }
        }

        int threshold = Math.Max(4, document.PageCount / 2);

        foreach (var (_, elements) in counts)
        {
            if (elements.Count < threshold)
                continue;

            yield return Issue(
                IssueSeverity.Moderate,
                IssueFixability.AutomaticallyFixable,
                $"\"{Shorten(elements[0].Text)}\" repeats on {elements.Count} pages",
                "This looks like a running header or footer, but it is not marked as one, so it " +
                "will be read out at every page boundary.",
                "It can be marked as page furniture, after which it is skipped when reading " +
                "straight through but still reachable if you go looking for it.",
                elements[0],
                elements.Count);
        }
    }

    private static string Shorten(string text) =>
        text.Length <= 40 ? text : string.Concat(text.AsSpan(0, 37).TrimEnd(), "…");
}

#endregion

#region Empty headings — a navigation point that says nothing

/// <summary>
/// Detects headings with no text.
///
/// Produced by decorative rules and images tagged as headings. They appear in the heading list and
/// in every heading-to-heading jump, and announce nothing when reached, which reads to the listener
/// as the program having failed.
/// </summary>
public sealed class EmptyHeadingRule : AuditRuleBase
{
    public override string Name => "empty headings";

    public override bool AppliesTo(PdfDocumentModel document) => document.Headings.Count > 0;

    protected override IEnumerable<AccessibilityIssue> CheckCore(PdfDocumentModel document)
    {
        var empty = document.Headings
            .Where(h => h.FullText.Trim().Length == 0)
            .ToList();

        if (empty.Count == 0)
            yield break;

        yield return Issue(
            IssueSeverity.Moderate,
            IssueFixability.AutomaticallyFixable,
            empty.Count == 1 ? "A heading has no text" : $"{empty.Count} headings have no text",
            "Jumping between headings will land on these and announce nothing, which sounds like " +
            "the reader has stopped working.",
            "They can be removed from the structure, or given text.",
            empty[0],
            empty.Count);
    }
}

#endregion
