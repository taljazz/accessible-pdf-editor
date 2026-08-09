using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;

namespace AccessiblePdfEditor.Auditing;

// =====================================================================================
//  AccessibilityAudit.cs
//
//  The types that describe what is wrong with a document and what can be done about it.
//
//  A finding here is written for the person who has to act on it, not for a compliance
//  report. Each one says three things:
//
//    what is wrong        "the figure on page 4 has no description"
//    why it matters       "a screen reader has nothing to say in its place"
//    what to do           "press Enter to describe it"
//
//  The second of those is the part usually left out, and it is the part that decides
//  whether someone bothers. "Missing alternate text — WCAG 1.1.1" tells a specialist
//  something and tells everyone else nothing.
// =====================================================================================

#region AccessibilityIssue

/// <summary>One accessibility problem found in a document.</summary>
public sealed record AccessibilityIssue
{
    /// <summary>How badly this affects someone reading with a screen reader.</summary>
    public required IssueSeverity Severity { get; init; }

    /// <summary>Whether and how the editor can repair it.</summary>
    public required IssueFixability Fixability { get; init; }

    /// <summary>A short statement of what is wrong.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Why it matters, in terms of what a reader actually experiences. The part that makes a
    /// finding worth acting on rather than merely worth logging.
    /// </summary>
    public required string Consequence { get; init; }

    /// <summary>What the user can do about it here, when they can do anything.</summary>
    public string? Remedy { get; init; }

    /// <summary>The element concerned, so navigation can take the user straight to it.</summary>
    public DocumentElement? Element { get; init; }

    /// <summary>The page it is on. Zero for document-wide findings.</summary>
    public int PageNumber { get; init; }

    /// <summary>The rule that produced this finding.</summary>
    public required string RuleName { get; init; }

    /// <summary>
    /// The number of identical findings collapsed into this one. A document with two hundred
    /// undescribed images produces one finding saying so, not two hundred; the individual ones are
    /// still reachable, but a list nobody can get to the end of helps nobody.
    /// </summary>
    public int OccurrenceCount { get; init; } = 1;

    /// <summary>
    /// The finding read aloud. Severity first, because it tells the listener whether to keep
    /// listening.
    /// </summary>
    public string Describe(VerbosityLevel verbosity = VerbosityLevel.Normal)
    {
        var parts = new List<string>(5) { DescribeSeverity(Severity), Title };

        if (OccurrenceCount > 1)
            parts.Add($"{OccurrenceCount} times");

        if (PageNumber > 0)
            parts.Add($"page {PageNumber}");

        if (verbosity != VerbosityLevel.Terse)
            parts.Add(Consequence);

        if (verbosity == VerbosityLevel.Detailed && Remedy is { Length: > 0 } remedy)
            parts.Add(remedy);

        return string.Join(". ", parts) + ".";
    }

    /// <summary>Severity in words. Written to be understood without a legend.</summary>
    public static string DescribeSeverity(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Blocker => "Blocking problem",
        IssueSeverity.Serious => "Serious problem",
        IssueSeverity.Moderate => "Problem",
        _ => "Suggestion",
    };
}

#endregion

#region AccessibilityReport

/// <summary>Everything an audit found, with the summary that gets read out first.</summary>
public sealed class AccessibilityReport
{
    public AccessibilityReport(PdfDocumentModel document, IReadOnlyList<AccessibilityIssue> issues)
    {
        DocumentName = document.FileName;
        TaggedStatus = document.TaggedStatus;
        PageCount = document.PageCount;

        // Sorted most serious first, then by page, so that reading straight down the list starts
        // with what actually stops someone reading the document.
        Issues = issues
            .OrderBy(i => i.Severity)
            .ThenBy(i => i.PageNumber)
            .ThenBy(i => i.Title, StringComparer.Ordinal)
            .ToList();
    }

    public string DocumentName { get; }

    public TaggedStatus TaggedStatus { get; }

    public int PageCount { get; }

    /// <summary>Everything found, most serious first.</summary>
    public IReadOnlyList<AccessibilityIssue> Issues { get; }

    public int BlockerCount => Issues.Count(i => i.Severity == IssueSeverity.Blocker);

    public int SeriousCount => Issues.Count(i => i.Severity == IssueSeverity.Serious);

    /// <summary>Findings the editor can repair, which is what the guided workflow walks through.</summary>
    public IReadOnlyList<AccessibilityIssue> Fixable =>
        Issues.Where(i => i.Fixability != IssueFixability.NotFixableHere).ToList();

    /// <summary>Findings that need a tool this editor is not.</summary>
    public IReadOnlyList<AccessibilityIssue> NotFixableHere =>
        Issues.Where(i => i.Fixability == IssueFixability.NotFixableHere).ToList();

    /// <summary>
    /// The spoken summary, and the first thing the user hears after an audit.
    ///
    /// It answers, in one breath: can I read this document, what is the worst of it, and how much
    /// of it can I fix from here. Anything longer gets talked over.
    /// </summary>
    public string BuildSummary()
    {
        if (Issues.Count == 0)
        {
            return $"{DocumentName} has no accessibility problems that this editor can detect. " +
                   $"It is {DescribeTagging()}.";
        }

        var parts = new List<string>(5)
        {
            $"{DocumentName}: {Issues.Count} {(Issues.Count == 1 ? "problem" : "problems")} found",
        };

        if (BlockerCount > 0)
        {
            parts.Add($"{BlockerCount} {(BlockerCount == 1 ? "blocks" : "block")} reading altogether");
        }

        if (SeriousCount > 0)
            parts.Add($"{SeriousCount} serious");

        int fixable = Fixable.Count;
        parts.Add(fixable == 0
            ? "none of them can be repaired here"
            : $"{fixable} can be repaired here");

        parts.Add($"The document is {DescribeTagging()}");

        return string.Join(". ", parts) + ".";
    }

    private string DescribeTagging() => TaggedStatus switch
    {
        TaggedStatus.FullyTagged => "fully tagged",
        TaggedStatus.PartiallyTagged => "only partly tagged",
        TaggedStatus.Untagged => "not tagged at all",
        TaggedStatus.ScannedWithoutText => "a scan with no text behind it",
        _ => "of unknown structure",
    };
}

#endregion

#region AuditRuleBase — one rule per way a document fails a reader

/// <summary>
/// Base class for audit rules. Each rule knows one way a document can fail a reader, and produces
/// findings for it.
///
/// A class per rule rather than one long method, because each rule needs its own explanation, its
/// own severity reasoning and its own repair, and because adding a new check should mean adding a
/// file rather than editing a switch statement that everything else depends on.
/// </summary>
public abstract class AuditRuleBase
{
    #region Identity

    /// <summary>A short name for the rule, recorded on every finding it produces.</summary>
    public abstract string Name { get; }

    /// <summary>
    /// Whether this rule applies to a document at all. A rule about table headers has nothing to
    /// say about a document with no tables, and running it anyway would be wasted work.
    /// </summary>
    public virtual bool AppliesTo(PdfDocumentModel document) => true;

    #endregion

    #region The check template
    // A rule that throws must not take the whole audit down with it. The audit is often the first
    // thing a user runs on an unfamiliar document, and a document odd enough to break one rule is
    // exactly the document they most need the other rules' findings about.

    /// <summary>Runs the rule, catching anything it throws.</summary>
    public IReadOnlyList<AccessibilityIssue> Check(PdfDocumentModel document)
    {
        if (!AppliesTo(document))
            return [];

        try
        {
            return CheckCore(document).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Produces this rule's findings.</summary>
    protected abstract IEnumerable<AccessibilityIssue> CheckCore(PdfDocumentModel document);

    #endregion

    #region Building findings

    /// <summary>Builds a finding, filling in this rule's name.</summary>
    protected AccessibilityIssue Issue(
        IssueSeverity severity,
        IssueFixability fixability,
        string title,
        string consequence,
        string? remedy = null,
        DocumentElement? element = null,
        int occurrences = 1) =>
        new()
        {
            Severity = severity,
            Fixability = fixability,
            Title = title,
            Consequence = consequence,
            Remedy = remedy,
            Element = element,
            PageNumber = element?.PageNumber ?? 0,
            RuleName = Name,
            OccurrenceCount = occurrences,
        };

    #endregion
}

#endregion

#region AccessibilityAuditor

/// <summary>Runs every audit rule over a document and collects the findings.</summary>
public sealed class AccessibilityAuditor
{
    #region The rule set
    // Order does not matter — findings are sorted by severity afterwards — but the list is kept in
    // roughly descending order of importance so that reading it conveys what the auditor considers
    // to matter most.

    private readonly IReadOnlyList<AuditRuleBase> _rules;

    public AccessibilityAuditor()
        : this(DefaultRules()) { }

    public AccessibilityAuditor(IReadOnlyList<AuditRuleBase> rules)
    {
        _rules = rules;
    }

    /// <summary>The rules run by default.</summary>
    public static IReadOnlyList<AuditRuleBase> DefaultRules() =>
    [
        new ScannedDocumentRule(),
        new UnlabelledFieldRule(),
        new MissingAlternateTextRule(),
        new UntaggedDocumentRule(),
        new TableWithoutHeadersRule(),
        new MissingDocumentLanguageRule(),
        new NoHeadingsRule(),
        new SkippedHeadingLevelRule(),
        new UninformativeLinkTextRule(),
        new MissingDocumentTitleRule(),
        new UnmarkedPageFurnitureRule(),
        new EmptyHeadingRule(),
    ];

    #endregion

    #region Running

    /// <summary>Audits a document.</summary>
    public AccessibilityReport Audit(PdfDocumentModel document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var findings = new List<AccessibilityIssue>();

        foreach (var rule in _rules)
            findings.AddRange(rule.Check(document));

        return new AccessibilityReport(document, findings);
    }

    #endregion
}

#endregion
