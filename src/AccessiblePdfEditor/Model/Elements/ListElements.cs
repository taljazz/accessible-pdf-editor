using AccessiblePdfEditor.Model;

namespace AccessiblePdfEditor.Model.Elements;

// =====================================================================================
//  ListElements.cs
//
//  Lists and their items.
//
//  A list is one of the few structures where a listener is at a real disadvantage compared
//  with a reader looking at the page. A sighted reader sees seven bullets at a glance and
//  knows how much is coming; a listener finds out only by reaching the end. So the list
//  announces its length on entry, and every item announces its position within it. That
//  single habit — say how many, then say which one — is what makes a list navigable by ear.
// =====================================================================================

#region ListElement — the container

/// <summary>A list. Contains <see cref="ListItemElement"/> children.</summary>
public sealed class ListElement : DocumentElement
{
    #region Construction and state

    public ListElement(int pageNumber, ListMarkerKind markerKind = ListMarkerKind.None)
        : base(pageNumber)
    {
        MarkerKind = markerKind;
    }

    public override ElementKind Kind => ElementKind.List;

    /// <summary>
    /// How this list's items are marked. Settable because correcting an unordered list that is
    /// really a numbered one is a legitimate repair, and because an untagged document never says.
    /// </summary>
    public ListMarkerKind MarkerKind { get; internal set; }

    /// <summary>The number of items directly in this list, not counting items of nested lists.</summary>
    public int ItemCount => Children.Count(c => c.Kind == ElementKind.ListItem);

    /// <summary>
    /// How deeply this list is nested inside other lists. Announced because a listener has no other
    /// way to tell a sub-list from a continuation of the one they were already in.
    /// </summary>
    public int NestingLevel => Ancestors().Count(a => a.Kind == ElementKind.List) + 1;

    #endregion

    #region Announcement
    // "list with 7 items" on entry, and at depth "nested list, level 2, with 3 items". Both say
    // the count, because the count is the thing a listener cannot otherwise get.

    protected override string DescribeRole(VerbosityLevel verbosity)
    {
        string ordering = MarkerKind switch
        {
            ListMarkerKind.Bullet => "bulleted list",
            ListMarkerKind.Decimal => "numbered list",
            ListMarkerKind.LowerAlpha or ListMarkerKind.UpperAlpha => "lettered list",
            ListMarkerKind.LowerRoman or ListMarkerKind.UpperRoman => "roman numeral list",
            _ => "list",
        };

        int level = NestingLevel;
        return level > 1 ? $"nested {ordering}, level {level}" : ordering;
    }

    protected override string DescribeContent(VerbosityLevel verbosity)
    {
        int count = ItemCount;
        return count switch
        {
            0 => "empty",
            1 => "with 1 item",
            _ => $"with {count} items",
        };
    }

    #endregion
}

#endregion

#region ListItemElement — one entry, which always knows where it sits
// In PDF an /LI holds an optional /Lbl (the visible bullet or number) and an /LBody (the text).
// We keep the label separate because it should be spoken as a position, not read as characters:
// hearing "bullet" is useful, hearing "•" as "black circle" is not.

/// <summary>One item of a list.</summary>
public sealed class ListItemElement : DocumentElement
{
    #region Construction and state

    public ListItemElement(int pageNumber, string text, string? label = null)
        : base(pageNumber)
    {
        Body = text ?? string.Empty;
        Label = label;
    }

    public override ElementKind Kind => ElementKind.ListItem;

    /// <summary>The item's text, from the PDF /LBody.</summary>
    public string Body { get; private set; }

    /// <summary>
    /// The visible marker, from the PDF /Lbl — "3.", "b)", "•". Kept for reference but not read out
    /// verbatim; the spoken position is derived from the list instead, because a listener needs
    /// "3 of 7", not "3 dot".
    /// </summary>
    public string? Label { get; }

    public override string Text => ActualText ?? Body;

    /// <summary>Replaces the item's text. Used by editing commands.</summary>
    public void SetBody(string text) => Body = text ?? string.Empty;

    /// <summary>The one-based position of this item within its list.</summary>
    public int ItemNumber
    {
        get
        {
            if (Parent is not ListElement list)
                return 0;

            int number = 0;
            foreach (var child in list.Children)
            {
                if (child.Kind != ElementKind.ListItem)
                    continue;

                number++;
                if (ReferenceEquals(child, this))
                    return number;
            }

            return 0;
        }
    }

    #endregion

    #region Announcement
    // The item's own position is part of its ROLE, not its position description, because it must
    // be heard at Normal verbosity. Knowing you are on item 3 of 7 is not extra detail — it is the
    // basic orientation a sighted reader gets free from the shape of the page.

    protected override string DescribeRole(VerbosityLevel verbosity)
    {
        if (Parent is not ListElement list)
            return "list item";

        int number = ItemNumber;
        int total = list.ItemCount;

        if (number == 0 || total == 0)
            return "list item";

        return verbosity == VerbosityLevel.Terse
            ? $"{number}."
            : $"item {number} of {total}";
    }

    protected override string DescribeContent(VerbosityLevel verbosity) => Text;

    /// <summary>
    /// A nested list inside this item is announced as state, so the listener learns there is more
    /// underneath before deciding whether to move on. Without it, arrowing to the next item would
    /// silently descend into a sub-list.
    /// </summary>
    protected override string DescribeState(VerbosityLevel verbosity)
    {
        if (verbosity == VerbosityLevel.Terse)
            return string.Empty;

        var nested = Children.OfType<ListElement>().FirstOrDefault();
        if (nested is null)
            return string.Empty;

        return $"contains a list of {nested.ItemCount} items";
    }

    #endregion
}

#endregion
