using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Model.Forms;

namespace AccessiblePdfEditor.Navigation;

// =====================================================================================
//  NavigationService.cs
//
//  Moving through a document by structure, which is how a screen reader user reads.
//
//  A sighted reader's eye jumps: to the next heading, past the table, back to the figure.
//  None of that is available by ear unless the program provides it deliberately. This
//  class is that provision — every granularity in NavigationGranularity is a key the user
//  can press to move by that unit.
//
//  Two decisions here matter more than the rest:
//
//  1. THE POSITION IS HELD BY ELEMENT ID, NOT BY INDEX. Editing rebuilds the element tree,
//     and an index would silently point somewhere else afterwards. Losing your place in a
//     two-hundred-page document because you fixed an alt text is not a small annoyance.
//
//  2. NOT MOVING SOUNDS DIFFERENT FROM MOVING. Reaching the end of the document returns a
//     boundary result with its own earcon and its own words. A key that appears to do
//     nothing is the most disorienting thing a keyboard-driven program can do: the user
//     presses it twenty more times before working out they stopped moving.
// =====================================================================================

#region NavigationResult

/// <summary>The outcome of a navigation command.</summary>
public sealed record NavigationResult
{
    /// <summary>Whether the position actually changed.</summary>
    public required bool Moved { get; init; }

    /// <summary>Where the position now is.</summary>
    public DocumentElement? Element { get; init; }

    /// <summary>What to say.</summary>
    public required string Announcement { get; init; }

    /// <summary>Which earcon to play. Carries the structural information faster than speech can.</summary>
    public required AudioCue Cue { get; init; }

    /// <summary>Built when a move succeeded.</summary>
    public static NavigationResult MovedTo(DocumentElement element, string announcement, AudioCue cue) =>
        new() { Moved = true, Element = element, Announcement = announcement, Cue = cue };

    /// <summary>
    /// Built when there was nowhere to go. Always carries the boundary cue, so that "you have
    /// reached the end" is audibly different from "you moved".
    /// </summary>
    public static NavigationResult Blocked(string announcement) =>
        new() { Moved = false, Announcement = announcement, Cue = AudioCue.Boundary };
}

#endregion

#region NavigationService

/// <summary>Moves the reading position through a document by structural units.</summary>
public sealed class NavigationService
{
    #region State

    private PdfDocumentModel? _document;
    private int _currentElementId = -1;
    private TextCursor? _cursor;

    /// <summary>The document being navigated.</summary>
    public PdfDocumentModel? Document => _document;

    /// <summary>Where the reading position currently is.</summary>
    public DocumentElement? Current =>
        _currentElementId >= 0 ? _document?.FindById(_currentElementId) : null;

    /// <summary>The page the reading position is on.</summary>
    public int CurrentPage => Current?.PageNumber ?? 0;

    /// <summary>Attaches a document and puts the position at its start.</summary>
    public void Attach(PdfDocumentModel document)
    {
        _document = document;
        _cursor = null;

        var first = FirstReadable(document);
        _currentElementId = first?.Id ?? -1;
    }

    /// <summary>
    /// Restores the position after the tree has been rebuilt by an edit. Held by id, so the user
    /// stays where they were even though every element object may have moved.
    /// </summary>
    public void Reattach()
    {
        if (_document is null)
            return;

        if (Current is null)
            _currentElementId = FirstReadable(_document)?.Id ?? -1;
    }

    private static DocumentElement? FirstReadable(PdfDocumentModel document) =>
        document.ReadingOrder.FirstOrDefault(e =>
            e.IsReadInContinuousReading && e.FullText.Trim().Length > 0);

    #endregion

    #region The main move command

    /// <summary>
    /// Moves by a granularity in a direction. The single entry point every navigation key goes
    /// through.
    /// </summary>
    public NavigationResult Move(
        NavigationGranularity granularity,
        MoveDirection direction,
        VerbosityLevel verbosity = VerbosityLevel.Normal,
        HeadingLevel headingLevel = HeadingLevel.None)
    {
        if (_document is null)
            return NavigationResult.Blocked("No document is open.");

        // Text-level granularities move within the current element rather than between elements.
        if (granularity is NavigationGranularity.Character
            or NavigationGranularity.Word
            or NavigationGranularity.Sentence
            or NavigationGranularity.Line)
        {
            return MoveWithinElement(granularity, direction, verbosity);
        }

        if (granularity == NavigationGranularity.Page)
            return MoveByPage(direction, verbosity);

        return MoveByFilter(granularity, direction, verbosity, headingLevel);
    }

    #endregion

    #region Moving between elements

    /// <summary>
    /// Moves to the next or previous element matching a granularity's filter.
    ///
    /// Every structural granularity is the same operation with a different predicate, which is why
    /// they are one method: adding a new one means adding a case to the filter, not another copy
    /// of the search.
    /// </summary>
    private NavigationResult MoveByFilter(
        NavigationGranularity granularity,
        MoveDirection direction,
        VerbosityLevel verbosity,
        HeadingLevel headingLevel)
    {
        var order = _document!.ReadingOrder;
        if (order.Count == 0)
            return NavigationResult.Blocked("This document has nothing to read.");

        var matches = BuildFilter(granularity, headingLevel);
        int start = Current?.ReadingOrder ?? -1;

        DocumentElement? found = direction switch
        {
            MoveDirection.Next => order.FirstOrDefault(e => e.ReadingOrder > start && matches(e)),
            MoveDirection.Previous => order.LastOrDefault(e => e.ReadingOrder < start && matches(e)),
            MoveDirection.First => order.FirstOrDefault(matches),
            MoveDirection.Last => order.LastOrDefault(matches),
            _ => null,
        };

        if (found is null)
            return NavigationResult.Blocked(BuildBoundaryMessage(granularity, direction, headingLevel));

        return Land(found, verbosity, CueFor(found));
    }

    /// <summary>
    /// The predicate for a granularity. Each case answers "what counts as one of these".
    /// </summary>
    private static Func<DocumentElement, bool> BuildFilter(
        NavigationGranularity granularity, HeadingLevel headingLevel) => granularity switch
    {
        NavigationGranularity.Heading => e => e is HeadingElement,

        NavigationGranularity.HeadingAtLevel => e =>
            e is HeadingElement heading && heading.Level == headingLevel,

        NavigationGranularity.List => e => e is ListElement,
        NavigationGranularity.ListItem => e => e is ListItemElement,
        NavigationGranularity.Table => e => e is TableElement,
        NavigationGranularity.TableCell => e => e is TableCellElement,
        NavigationGranularity.Figure => e => e is FigureElement,
        NavigationGranularity.Link => e => e is LinkElement,
        NavigationGranularity.FormField => e => e is PdfFormField,

        // The command that makes a long form tractable: skip straight to what still needs doing.
        NavigationGranularity.UnfilledFormField => e =>
            e is PdfFormField { NeedsAttention: true },

        NavigationGranularity.Annotation => e =>
            e is AnnotationElement { AnnotationKind: not AnnotationKind.Popup },

        // Paragraph movement means block-level content, not literally only paragraphs — otherwise
        // arrowing through a document would skip over every list and table in it.
        NavigationGranularity.Paragraph => e =>
            e.IsReadInContinuousReading
            && e.Kind is ElementKind.Paragraph or ElementKind.Heading or ElementKind.ListItem
                or ElementKind.BlockQuote or ElementKind.Code or ElementKind.Caption
                or ElementKind.TableCell or ElementKind.Figure
            && (e.FullText.Trim().Length > 0 || e is FigureElement),

        // Element movement reaches everything with something to say, including artifacts, because
        // this is the granularity for deliberately inspecting the page.
        NavigationGranularity.Element => e =>
            e.FullText.Trim().Length > 0
            || e is FigureElement or PdfFormField or LinkElement or AnnotationElement,

        _ => e => e.IsReadInContinuousReading && e.FullText.Trim().Length > 0,
    };

    /// <summary>The earcon for landing on an element. Conveys the kind faster than speech can.</summary>
    private static AudioCue CueFor(DocumentElement element) => element switch
    {
        HeadingElement => AudioCue.Heading,
        LinkElement => AudioCue.Link,
        PdfFormField => AudioCue.FormField,
        FigureElement => AudioCue.Figure,
        TableElement or TableCellElement or TableRowElement => AudioCue.Table,
        PageElement => AudioCue.PageTurn,
        _ => AudioCue.Navigation,
    };

    #endregion

    #region Moving by page

    private NavigationResult MoveByPage(MoveDirection direction, VerbosityLevel verbosity)
    {
        int pageCount = _document!.PageCount;
        if (pageCount == 0)
            return NavigationResult.Blocked("This document has no pages.");

        int current = CurrentPage;

        int target = direction switch
        {
            MoveDirection.Next => current + 1,
            MoveDirection.Previous => current - 1,
            MoveDirection.First => 1,
            MoveDirection.Last => pageCount,
            _ => current,
        };

        if (target < 1)
            return NavigationResult.Blocked("This is the first page.");

        if (target > pageCount)
            return NavigationResult.Blocked("This is the last page.");

        return GoToPage(target, verbosity);
    }

    /// <summary>Moves to a page and lands on its first readable content.</summary>
    public NavigationResult GoToPage(int pageNumber, VerbosityLevel verbosity = VerbosityLevel.Normal)
    {
        if (_document is null)
            return NavigationResult.Blocked("No document is open.");

        var page = _document.GetPage(pageNumber);
        if (page is null)
            return NavigationResult.Blocked($"There is no page {pageNumber}.");

        var landing = page.Descendants()
            .FirstOrDefault(e => e.IsReadInContinuousReading && e.FullText.Trim().Length > 0)
            ?? (DocumentElement)page;

        _currentElementId = landing.Id;
        _cursor = null;

        // The page is announced before its content, so the listener knows where they have arrived
        // before they start hearing what is there.
        string announcement = $"{page.Describe(verbosity)}. {landing.Describe(verbosity)}";

        return NavigationResult.MovedTo(landing, announcement, AudioCue.PageTurn);
    }

    /// <summary>Moves directly to an element. Used by the audit list, the links list and search.</summary>
    public NavigationResult GoToElement(
        DocumentElement element, VerbosityLevel verbosity = VerbosityLevel.Normal)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (_document is null)
            return NavigationResult.Blocked("No document is open.");

        return Land(element, verbosity, CueFor(element));
    }

    #endregion

    #region Moving within an element
    // Character, word, sentence and line movement walk the current element's text rather than the
    // document. This is how a user checks a value they have typed, or spells out a word they did
    // not catch — the operations that need a smaller unit than "the next paragraph".

    private NavigationResult MoveWithinElement(
        NavigationGranularity granularity, MoveDirection direction, VerbosityLevel verbosity)
    {
        var element = Current;
        if (element is null)
            return NavigationResult.Blocked("There is nothing to read here.");

        _cursor ??= new TextCursor(element.FullText);

        // The cursor is rebuilt when the element changes underneath it, which happens after an edit.
        if (!_cursor.Matches(element.FullText))
            _cursor = new TextCursor(element.FullText);

        string? piece = granularity switch
        {
            NavigationGranularity.Character => _cursor.MoveCharacter(direction),
            NavigationGranularity.Word => _cursor.MoveWord(direction),
            NavigationGranularity.Sentence => _cursor.MoveSentence(direction),
            NavigationGranularity.Line => _cursor.MoveSentence(direction),
            _ => null,
        };

        if (piece is null)
        {
            // Running off the end of one element steps to the next, so reading does not stop dead
            // at a paragraph boundary. Only the document's own ends are true boundaries.
            if (direction is MoveDirection.Next or MoveDirection.Previous)
            {
                var stepped = MoveByFilter(NavigationGranularity.Paragraph, direction, verbosity,
                    HeadingLevel.None);

                if (stepped.Moved)
                {
                    _cursor = new TextCursor(stepped.Element!.FullText);
                    _cursor.MoveToEdge(direction == MoveDirection.Next ? MoveDirection.First : MoveDirection.Last);
                }

                return stepped;
            }

            return NavigationResult.Blocked("There is no more text here.");
        }

        // Spoken as a single character, a letter is often ambiguous by ear — "b" and "p" and "v"
        // sound alike over a poor connection or a fast voice.
        string announcement = granularity == NavigationGranularity.Character
            ? SpellCharacter(piece)
            : piece;

        return NavigationResult.MovedTo(element, announcement, AudioCue.Navigation);
    }

    /// <summary>
    /// Describes a single character so it cannot be misheard. Punctuation is named, because a
    /// screen reader reading at speed usually skips it entirely.
    /// </summary>
    private static string SpellCharacter(string character)
    {
        if (character.Length == 0)
            return "blank";

        char c = character[0];

        if (char.IsWhiteSpace(c))
            return "space";

        string name = c switch
        {
            '.' => "full stop",
            ',' => "comma",
            ';' => "semicolon",
            ':' => "colon",
            '\'' => "apostrophe",
            '"' => "quote",
            '-' => "hyphen",
            '_' => "underscore",
            '(' => "open bracket",
            ')' => "close bracket",
            '/' => "slash",
            '\\' => "backslash",
            '@' => "at",
            '&' => "ampersand",
            '£' => "pound sign",
            '$' => "dollar sign",
            '%' => "percent",
            _ => character,
        };

        // Capitals are called out, since case is otherwise inaudible and matters when checking a
        // reference number or a password.
        return char.IsUpper(c) ? $"capital {char.ToLowerInvariant(c)}" : name;
    }

    #endregion

    #region Landing and announcing

    private NavigationResult Land(DocumentElement element, VerbosityLevel verbosity, AudioCue cue)
    {
        bool changedPage = element.PageNumber != CurrentPage && element.PageNumber > 0;

        _currentElementId = element.Id;
        _cursor = null;

        string announcement = element.Describe(verbosity);

        // A page change is announced even when it was not what the user asked for, because losing
        // track of which page you are on is disorienting in a way that cannot be recovered by
        // listening harder.
        if (changedPage && verbosity != VerbosityLevel.Terse)
            announcement = $"Page {element.PageNumber}. {announcement}";

        return NavigationResult.MovedTo(element, announcement, changedPage ? AudioCue.PageTurn : cue);
    }

    /// <summary>
    /// The message for a move that could not happen. Names the unit that was being moved by, so
    /// "there are no more headings" is distinguishable from "this is the end of the document" —
    /// the user needs to know whether to change key or change strategy.
    /// </summary>
    private static string BuildBoundaryMessage(
        NavigationGranularity granularity, MoveDirection direction, HeadingLevel headingLevel)
    {
        string unit = granularity switch
        {
            NavigationGranularity.Heading => "headings",
            NavigationGranularity.HeadingAtLevel => $"level {(int)headingLevel} headings",
            NavigationGranularity.List => "lists",
            NavigationGranularity.ListItem => "list items",
            NavigationGranularity.Table => "tables",
            NavigationGranularity.TableCell => "cells",
            NavigationGranularity.Figure => "figures",
            NavigationGranularity.Link => "links",
            NavigationGranularity.FormField => "form fields",
            NavigationGranularity.UnfilledFormField => "form fields still to fill in",
            NavigationGranularity.Annotation => "comments",
            NavigationGranularity.Paragraph => "paragraphs",
            _ => "content",
        };

        return direction switch
        {
            MoveDirection.Next => $"No more {unit} after this point.",
            MoveDirection.Previous => $"No more {unit} before this point.",
            _ => $"There are no {unit} in this document.",
        };
    }

    #endregion

    #region Where am I

    /// <summary>
    /// Describes the current position in full: what you are on, where it sits, and what contains
    /// it. The answer to "where am I", which is the question a listener asks most often and can
    /// least easily answer for themselves.
    /// </summary>
    public string DescribePosition()
    {
        if (_document is null)
            return "No document is open.";

        var element = Current;
        if (element is null)
            return "The reading position is not set.";

        var parts = new List<string>(5) { element.Describe(VerbosityLevel.Detailed) };

        // The chain of containers, nearest first. Tells the listener they are inside a table inside
        // a section, which nothing else in the announcement conveys.
        var containers = element.Ancestors()
            .Where(a => a.Kind is ElementKind.List or ElementKind.Table or ElementKind.TableRow
                or ElementKind.Section or ElementKind.Figure)
            .Take(3)
            .Select(a => a.Describe(VerbosityLevel.Terse))
            .ToList();

        if (containers.Count > 0)
            parts.Add($"inside {string.Join(", inside ", containers)}");

        // The nearest heading above is the strongest orientation cue there is: it says which section
        // of the document you are in, which page numbers alone do not.
        var heading = _document.ReadingOrder
            .Take(Math.Max(0, element.ReadingOrder))
            .OfType<HeadingElement>()
            .LastOrDefault();

        if (heading is not null)
            parts.Add($"under the heading \"{heading.Text}\"");

        if (element.PageNumber > 0)
            parts.Add($"page {element.PageNumber} of {_document.PageCount}");

        return string.Join(", ", parts) + ".";
    }

    #endregion
}

#endregion

#region TextCursor — walking the text of one element
// A cursor rather than a split-and-index, because the user moves back and forth and each move must
// return exactly the piece crossed. Returning the piece crossed rather than the piece landed on is
// what makes walking a word letter by letter hear each letter exactly once, in both directions.

/// <summary>Walks a piece of text by character, word and sentence.</summary>
internal sealed class TextCursor
{
    private readonly string _text;
    private int _position;

    public TextCursor(string text)
    {
        _text = text ?? string.Empty;
        _position = 0;
    }

    /// <summary>Whether this cursor still belongs to a given piece of text.</summary>
    public bool Matches(string text) => string.Equals(_text, text, StringComparison.Ordinal);

    /// <summary>Jumps to the start or end.</summary>
    public void MoveToEdge(MoveDirection direction) =>
        _position = direction == MoveDirection.Last ? _text.Length : 0;

    /// <summary>Moves one character and returns the character crossed, or null at the end.</summary>
    public string? MoveCharacter(MoveDirection direction)
    {
        switch (direction)
        {
            case MoveDirection.Next:
                if (_position >= _text.Length) return null;
                return _text[_position++].ToString();

            case MoveDirection.Previous:
                if (_position <= 0) return null;
                return _text[--_position].ToString();

            case MoveDirection.First:
                _position = 0;
                return _text.Length > 0 ? _text[0].ToString() : null;

            case MoveDirection.Last:
                if (_text.Length == 0) return null;
                _position = _text.Length - 1;
                return _text[^1].ToString();

            default:
                return null;
        }
    }

    /// <summary>Moves one word and returns it, or null at the end.</summary>
    public string? MoveWord(MoveDirection direction)
    {
        if (_text.Length == 0)
            return null;

        if (direction is MoveDirection.First or MoveDirection.Last)
        {
            _position = direction == MoveDirection.First ? 0 : _text.Length;
            return direction == MoveDirection.First ? MoveWord(MoveDirection.Next) : MoveWord(MoveDirection.Previous);
        }

        if (direction == MoveDirection.Next)
        {
            int i = _position;
            while (i < _text.Length && char.IsWhiteSpace(_text[i])) i++;
            if (i >= _text.Length) { _position = _text.Length; return null; }

            int start = i;
            while (i < _text.Length && !char.IsWhiteSpace(_text[i])) i++;

            _position = i;
            return _text[start..i];
        }

        int j = _position;
        while (j > 0 && char.IsWhiteSpace(_text[j - 1])) j--;
        if (j <= 0) { _position = 0; return null; }

        int end = j;
        while (j > 0 && !char.IsWhiteSpace(_text[j - 1])) j--;

        _position = j;
        return _text[j..end];
    }

    /// <summary>
    /// Moves one sentence and returns it, or null at the end. Sentences are the most comfortable
    /// unit for listening to prose — long enough to carry a thought, short enough to re-hear.
    /// </summary>
    public string? MoveSentence(MoveDirection direction)
    {
        if (_text.Length == 0)
            return null;

        if (direction is MoveDirection.First or MoveDirection.Last)
        {
            _position = direction == MoveDirection.First ? 0 : _text.Length;
            return direction == MoveDirection.First
                ? MoveSentence(MoveDirection.Next)
                : MoveSentence(MoveDirection.Previous);
        }

        if (direction == MoveDirection.Next)
        {
            if (_position >= _text.Length)
                return null;

            int start = _position;
            int end = FindSentenceEnd(start);

            _position = end;
            string sentence = _text[start..end].Trim();

            return sentence.Length > 0 ? sentence : null;
        }

        if (_position <= 0)
            return null;

        int previousStart = FindSentenceStartBefore(_position);
        string previous = _text[previousStart.._position].Trim();
        _position = previousStart;

        return previous.Length > 0 ? previous : null;
    }

    /// <summary>
    /// Finds where a sentence ends. A full stop only ends a sentence when followed by whitespace,
    /// which keeps "3.5" and "example.org" from being split in the middle.
    /// </summary>
    private int FindSentenceEnd(int from)
    {
        for (int i = from; i < _text.Length; i++)
        {
            if (_text[i] is not ('.' or '!' or '?'))
                continue;

            int next = i + 1;

            while (next < _text.Length && _text[next] is '"' or '\'' or ')' or ']')
                next++;

            if (next >= _text.Length)
                return _text.Length;

            if (char.IsWhiteSpace(_text[next]))
                return next;
        }

        return _text.Length;
    }

    private int FindSentenceStartBefore(int before)
    {
        int i = before - 1;

        while (i > 0 && char.IsWhiteSpace(_text[i]))
            i--;

        for (; i > 0; i--)
        {
            if (_text[i] is not ('.' or '!' or '?'))
                continue;

            int next = i + 1;
            if (next < _text.Length && char.IsWhiteSpace(_text[next]))
                return next;
        }

        return 0;
    }
}

#endregion
