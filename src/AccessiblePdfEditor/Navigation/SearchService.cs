using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;

namespace AccessiblePdfEditor.Navigation;

// =====================================================================================
//  SearchService.cs
//
//  Finding text in a document.
//
//  Search matters more without sight than with it. A sighted reader scans a page in a
//  second; a listener cannot, so search is often the only practical way to answer "does
//  this document mention X" without reading the whole thing.
//
//  Two things follow from that, and they shape this file:
//
//  1. A match must be announced WITH ITS CONTEXT and WITH ITS LOCATION. "Found on page 12"
//     is nearly useless. "Page 12, under the heading Payment terms: the deadline is 31
//     March" tells the listener whether this is the match they wanted without moving there.
//
//  2. Matches must be counted up front. "Match 3 of 17" lets someone decide whether to
//     step through them or refine the search — a decision a sighted reader makes by
//     glancing at highlighted results.
// =====================================================================================

#region SearchMatch

/// <summary>One occurrence of a search term.</summary>
public sealed record SearchMatch
{
    /// <summary>The element containing the match.</summary>
    public required DocumentElement Element { get; init; }

    /// <summary>Where in that element's text the match begins.</summary>
    public required int Offset { get; init; }

    /// <summary>How long the match is.</summary>
    public required int Length { get; init; }

    /// <summary>The page it is on.</summary>
    public int PageNumber => Element.PageNumber;

    /// <summary>Its position in the ordered list of matches, one-based.</summary>
    public int MatchNumber { get; init; }

    /// <summary>The total number of matches.</summary>
    public int TotalMatches { get; init; }

    /// <summary>The nearest heading above the match, which says which section it is in.</summary>
    public string? EnclosingHeading { get; init; }

    /// <summary>
    /// The matched text with enough of its surroundings to judge it by. Trimmed to whole words so
    /// the announcement does not begin or end mid-syllable.
    /// </summary>
    public required string Context { get; init; }

    /// <summary>
    /// The match read aloud: which one it is, where it is, what section it is in, and what it says.
    /// In that order, because the listener needs to place it before they hear it.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>(4);

        if (TotalMatches > 0)
            parts.Add($"Match {MatchNumber} of {TotalMatches}");

        if (PageNumber > 0)
            parts.Add($"page {PageNumber}");

        if (EnclosingHeading is { Length: > 0 } heading)
            parts.Add($"under \"{heading}\"");

        parts.Add(Context);

        return string.Join(", ", parts);
    }
}

#endregion

#region SearchOptions

/// <summary>How a search should be carried out.</summary>
public sealed class SearchOptions
{
    /// <summary>Whether case must match.</summary>
    public bool MatchCase { get; init; }

    /// <summary>Whether the term must match whole words.</summary>
    public bool WholeWord { get; init; }

    /// <summary>
    /// Whether to search page furniture as well. Off by default: searching for a word that appears
    /// in the running header would otherwise return one match per page and bury the real ones.
    /// </summary>
    public bool IncludeArtifacts { get; init; }

    /// <summary>Whether to search form field values and labels.</summary>
    public bool IncludeFormFields { get; init; } = true;

    /// <summary>Whether to search comments and other annotations.</summary>
    public bool IncludeAnnotations { get; init; } = true;

    /// <summary>How many characters of surrounding text to include with each match.</summary>
    public int ContextCharacters { get; init; } = 70;
}

#endregion

#region SearchService

/// <summary>Finds text in a document.</summary>
public sealed class SearchService
{
    #region State

    private readonly List<SearchMatch> _matches = [];
    private string _term = string.Empty;
    private int _currentIndex = -1;

    /// <summary>The matches from the last search, in reading order.</summary>
    public IReadOnlyList<SearchMatch> Matches => _matches;

    /// <summary>The term last searched for.</summary>
    public string Term => _term;

    /// <summary>The match currently being visited, or null.</summary>
    public SearchMatch? Current =>
        _currentIndex >= 0 && _currentIndex < _matches.Count ? _matches[_currentIndex] : null;

    #endregion

    #region Searching

    /// <summary>
    /// Searches a document and returns a spoken summary of what was found.
    /// </summary>
    public string Search(PdfDocumentModel document, string term, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        options ??= new SearchOptions();
        _matches.Clear();
        _currentIndex = -1;
        _term = term?.Trim() ?? string.Empty;

        if (_term.Length == 0)
            return "Nothing to search for.";

        var comparison = options.MatchCase
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        foreach (var element in document.ReadingOrder)
        {
            if (!ShouldSearch(element, options))
                continue;

            string haystack = element.Text;
            if (haystack.Length == 0)
                continue;

            CollectMatches(element, haystack, comparison, options, document);
        }

        // Numbered after collection, so every match knows the total. That total is what lets the
        // user decide whether to step through them or narrow the search first.
        for (int i = 0; i < _matches.Count; i++)
        {
            _matches[i] = _matches[i] with
            {
                MatchNumber = i + 1,
                TotalMatches = _matches.Count,
            };
        }

        return _matches.Count switch
        {
            0 => $"\"{_term}\" was not found.",
            1 => $"1 match for \"{_term}\".",
            _ => $"{_matches.Count} matches for \"{_term}\".",
        };
    }

    private static bool ShouldSearch(DocumentElement element, SearchOptions options) => element switch
    {
        ArtifactElement => options.IncludeArtifacts,
        Model.Forms.PdfFormField => options.IncludeFormFields,
        AnnotationElement => options.IncludeAnnotations,
        _ => true,
    };

    private void CollectMatches(
        DocumentElement element,
        string haystack,
        StringComparison comparison,
        SearchOptions options,
        PdfDocumentModel document)
    {
        int from = 0;

        while (from < haystack.Length)
        {
            int found = haystack.IndexOf(_term, from, comparison);
            if (found < 0)
                break;

            if (!options.WholeWord || IsWholeWord(haystack, found, _term.Length))
            {
                _matches.Add(new SearchMatch
                {
                    Element = element,
                    Offset = found,
                    Length = _term.Length,
                    Context = BuildContext(haystack, found, _term.Length, options.ContextCharacters),
                    EnclosingHeading = FindEnclosingHeading(element, document),
                });
            }

            from = found + Math.Max(1, _term.Length);
        }
    }

    private static bool IsWholeWord(string haystack, int offset, int length)
    {
        bool startsCleanly = offset == 0 || !char.IsLetterOrDigit(haystack[offset - 1]);

        int after = offset + length;
        bool endsCleanly = after >= haystack.Length || !char.IsLetterOrDigit(haystack[after]);

        return startsCleanly && endsCleanly;
    }

    /// <summary>
    /// Builds the surrounding text for a match, trimmed to whole words at both ends so that the
    /// announcement neither begins nor ends part-way through a word.
    /// </summary>
    private static string BuildContext(string haystack, int offset, int length, int radius)
    {
        int start = Math.Max(0, offset - radius);
        int end = Math.Min(haystack.Length, offset + length + radius);

        // Pull the start forward to a word boundary, unless that would cut into the match itself.
        while (start > 0 && start < offset && !char.IsWhiteSpace(haystack[start - 1]))
            start++;

        while (end < haystack.Length && end > offset + length && !char.IsWhiteSpace(haystack[end]))
            end--;

        string context = haystack[start..end].Trim();

        if (start > 0)
            context = "… " + context;

        if (end < haystack.Length)
            context += " …";

        return context;
    }

    /// <summary>
    /// The nearest heading before an element. The strongest orientation cue available: it tells the
    /// listener which section a match is in, which a page number alone does not.
    /// </summary>
    private static string? FindEnclosingHeading(DocumentElement element, PdfDocumentModel document)
    {
        if (element.ReadingOrder <= 0)
            return null;

        for (int i = Math.Min(element.ReadingOrder, document.ReadingOrder.Count) - 1; i >= 0; i--)
        {
            if (document.ReadingOrder[i] is HeadingElement heading && heading.Text.Length > 0)
                return heading.Text;
        }

        return null;
    }

    #endregion

    #region Stepping through matches

    /// <summary>Moves to the next match, wrapping round to the first.</summary>
    public SearchMatch? Next()
    {
        if (_matches.Count == 0)
            return null;

        _currentIndex = (_currentIndex + 1) % _matches.Count;
        return _matches[_currentIndex];
    }

    /// <summary>Moves to the previous match, wrapping round to the last.</summary>
    public SearchMatch? Previous()
    {
        if (_matches.Count == 0)
            return null;

        _currentIndex = _currentIndex <= 0 ? _matches.Count - 1 : _currentIndex - 1;
        return _matches[_currentIndex];
    }

    /// <summary>
    /// Whether moving to the next match would wrap round to the start. Announced when it happens,
    /// because silently returning to the top makes a listener think the search is repeating itself.
    /// </summary>
    public bool NextWouldWrap => _matches.Count > 0 && _currentIndex == _matches.Count - 1;

    /// <summary>Forgets the current search.</summary>
    public void Clear()
    {
        _matches.Clear();
        _currentIndex = -1;
        _term = string.Empty;
    }

    #endregion
}

#endregion
