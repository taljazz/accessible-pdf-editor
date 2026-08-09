using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;

namespace AccessiblePdfEditor.Editing;

// =====================================================================================
//  AnnotationCommands.cs
//
//  Writing comments: adding one, changing its text, replying to it, and deleting it.
//
//  Reading annotations already worked. Writing them is what turns this from a program
//  that inspects someone else's markup into one a person can use to say something back —
//  which for a form being checked, or a document being reviewed with a colleague, is most
//  of the point.
//
//  WHERE A COMMENT GOES, AND WHY IT IS NOT A CLICK
//
//  In every other PDF editor, commenting means pointing at a place on the page. That is
//  useless without sight. Here a comment is attached to the ELEMENT the user is on — this
//  paragraph, this table cell, this form field — and takes that element's position on the
//  page for its rectangle. The user says "comment on this", which is what they mean, and
//  the coordinates are worked out for them.
//
//  It also makes the comment better than a hand-placed one: because the anchor is a real
//  element, the comment can record what it is about in words, so it reads as "comment on
//  the paragraph beginning 'Payments are due'" rather than "comment at 380, 512".
//
//  UNDO
//
//  Deleting a comment has to be undoable, which means remembering not just the annotation
//  but exactly where it sat — its parent and its position among its siblings. Putting it
//  back at the end of the page would be a different document from the one the user had,
//  and reading order is the thing this program exists to protect.
// =====================================================================================

#region AddAnnotationCommand

/// <summary>Attaches a new comment to an element.</summary>
public sealed class AddAnnotationCommand : EditCommand
{
    private readonly AnnotationElement _annotation;
    private readonly DocumentElement _anchor;
    private readonly string _anchorDescription;

    /// <summary>
    /// Creates a comment on an element.
    /// </summary>
    /// <param name="anchor">What the comment is about. Supplies the page and the rectangle.</param>
    /// <param name="text">The comment itself.</param>
    /// <param name="author">Who is writing it, written to the annotation's /T.</param>
    /// <param name="kind">
    /// Comment for a sticky note, Highlight to mark the anchor's text as well as commenting on it.
    /// </param>
    public AddAnnotationCommand(
        DocumentElement anchor,
        string text,
        string? author,
        AnnotationKind kind = AnnotationKind.Comment)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        _anchor = anchor;
        _anchorDescription = DescribeAnchor(anchor);

        _annotation = new AnnotationElement(anchor.PageNumber, kind, text ?? string.Empty)
        {
            // Generated here rather than at save time, so that an annotation edited and then saved
            // in the same session can still be found in the file by the name it was written under.
            SourceObjectId = "APE-" + Guid.NewGuid().ToString("N")[..16],
        };

        _annotation.Bounds = anchor.Bounds;
        _annotation.Author = author;
        _annotation.ModifiedAt = DateTimeOffset.Now;
        _annotation.IsUnsaved = true;

        // What the comment is about, in words. A highlight with no note is meaningless without it,
        // and even a note reads better with it.
        if (anchor.Text.Trim() is { Length: > 0 } anchored)
            _annotation.AnchoredText = anchored.Length > 200 ? anchored[..200] + "…" : anchored;
    }

    /// <summary>The annotation that was created, so the caller can move the reading position to it.</summary>
    public AnnotationElement Annotation => _annotation;

    public override EditKind Kind => EditKind.AnnotationAdded;

    /// <summary>A new annotation is appended to the page; nothing already in the file is disturbed.</summary>
    public override EditConfidence Confidence => EditConfidence.Additive;

    public override DocumentElement? AffectedElement => _annotation;

    public override string Description =>
        $"Added a {DescribeKind()} on {_anchorDescription}" +
        (_annotation.Contents.Length > 0 ? $": {Shorten(_annotation.Contents)}" : string.Empty);

    protected override EditResult ApplyCore(PdfDocumentModel document)
    {
        // Added as a child of the anchor, so it reads immediately after the thing it is about. A
        // comment at the end of the page would be heard minutes after the sentence it refers to.
        // A SIBLING immediately after the anchor, not a child of it. A comment about a paragraph is
        // not part of that paragraph's content, and making it one both misrepresents the document
        // and hides it: the renderers write a paragraph's text, not its children, so a comment
        // nested inside one would exist in the model and appear nowhere the user could read it.
        var parent = _anchor.Parent;

        if (parent is not null)
        {
            int insertAt = IndexOfChild(parent, _anchor) + 1;

            // Past any comments already on this element, so a second comment reads after the first
            // rather than pushing it down. A thread has to be heard in the order it was written, or
            // a reply arrives before the remark it answers.
            while (insertAt < parent.Children.Count && parent.Children[insertAt] is AnnotationElement)
                insertAt++;

            parent.AddChild(_annotation);
            parent.MoveChild(_annotation, insertAt);
        }
        else if (_anchor.AcceptsChildren)
        {
            _anchor.AddChild(_annotation);
        }
        else
        {
            return EditResult.Failed("There is nowhere to attach a comment here.");
        }

        document.RebuildReadingOrder();

        return EditResult.Ok($"Comment added on {_anchorDescription}.");
    }

    protected override EditResult RevertCore(PdfDocumentModel document)
    {
        _annotation.Parent?.RemoveChild(_annotation);
        document.RebuildReadingOrder();

        return EditResult.Ok($"Removed the {DescribeKind()} on {_anchorDescription}.");
    }

    private string DescribeKind() => _annotation.AnnotationKind switch
    {
        AnnotationKind.Highlight => "highlight",
        AnnotationKind.Underline => "underline",
        AnnotationKind.StrikeOut => "strike-through",
        _ => "comment",
    };

    internal static string DescribeAnchor(DocumentElement anchor)
    {
        string role = DocumentElement.DefaultRoleName(anchor.Kind).ToLowerInvariant();
        string text = anchor.Text.Trim();

        return text.Length > 0
            ? $"the {role} beginning “{Shorten(text, 40)}”"
            : $"the {role} on page {anchor.PageNumber}";
    }

    internal static string Shorten(string value, int limit = 60) =>
        value.Length <= limit ? value : value[..limit].TrimEnd() + "…";

    /// <summary>
    /// Where a child sits among its siblings. The tree exposes its children as a read-only list,
    /// which has no IndexOf, and the position is what keeps a comment beside the thing it is about.
    /// </summary>
    internal static int IndexOfChild(DocumentElement parent, DocumentElement child)
    {
        for (int i = 0; i < parent.Children.Count; i++)
        {
            if (ReferenceEquals(parent.Children[i], child))
                return i;
        }

        return -1;
    }
}

#endregion

#region ReplyToAnnotationCommand

/// <summary>Adds a reply to an existing comment, keeping the thread together.</summary>
public sealed class ReplyToAnnotationCommand : EditCommand
{
    private readonly AnnotationElement _parent;
    private readonly AnnotationElement _reply;

    public ReplyToAnnotationCommand(AnnotationElement parent, string text, string? author)
    {
        ArgumentNullException.ThrowIfNull(parent);

        _parent = parent;

        _reply = new AnnotationElement(parent.PageNumber, AnnotationKind.Comment, text ?? string.Empty)
        {
            SourceObjectId = "APE-" + Guid.NewGuid().ToString("N")[..16],
        };

        // A reply sits on top of what it answers. Placing it elsewhere would make the thread read
        // as unrelated comments scattered down the page.
        _reply.Bounds = parent.Bounds;
        _reply.Author = author;
        _reply.ModifiedAt = DateTimeOffset.Now;
        _reply.IsUnsaved = true;
        _reply.InReplyTo = parent;
    }

    public AnnotationElement Reply => _reply;

    public override EditKind Kind => EditKind.AnnotationAdded;

    public override DocumentElement? AffectedElement => _reply;

    public override string Description =>
        $"Replied to {DescribeParent()}: {AddAnnotationCommand.Shorten(_reply.Contents)}";

    protected override EditResult ApplyCore(PdfDocumentModel document)
    {
        _parent.AddChild(_reply);
        document.RebuildReadingOrder();

        return EditResult.Ok($"Reply added to {DescribeParent()}.");
    }

    protected override EditResult RevertCore(PdfDocumentModel document)
    {
        _parent.RemoveChild(_reply);
        document.RebuildReadingOrder();

        return EditResult.Ok($"Removed the reply to {DescribeParent()}.");
    }

    private string DescribeParent() =>
        _parent.Author is { Length: > 0 } author
            ? $"{author}’s comment"
            : $"the comment {AddAnnotationCommand.Shorten(_parent.Contents, 40)}";
}

#endregion

#region EditAnnotationCommand

/// <summary>Changes the text of an existing comment.</summary>
public sealed class EditAnnotationCommand : EditCommand
{
    private readonly AnnotationElement _annotation;
    private readonly string _newText;
    private readonly string _oldText;
    private readonly DateTimeOffset? _oldModifiedAt;
    private readonly bool _wasEdited;

    public EditAnnotationCommand(AnnotationElement annotation, string newText)
    {
        ArgumentNullException.ThrowIfNull(annotation);

        _annotation = annotation;
        _newText = newText ?? string.Empty;

        // Captured now, because by the time Revert runs the model no longer holds it.
        _oldText = annotation.Contents;
        _oldModifiedAt = annotation.ModifiedAt;
        _wasEdited = annotation.IsEdited;
    }

    public override EditKind Kind => EditKind.AnnotationEdited;

    public override DocumentElement? AffectedElement => _annotation;

    public override string Description =>
        _oldText.Length == 0
            ? $"Wrote the comment {AddAnnotationCommand.Shorten(_newText)}"
            : $"Changed a comment from “{AddAnnotationCommand.Shorten(_oldText, 30)}” " +
              $"to “{AddAnnotationCommand.Shorten(_newText, 30)}”";

    protected override EditResult ApplyCore(PdfDocumentModel document)
    {
        _annotation.SetContents(_newText);
        _annotation.MarkChanged(DateTimeOffset.Now);

        return EditResult.Ok("Comment changed.");
    }

    protected override EditResult RevertCore(PdfDocumentModel document)
    {
        _annotation.SetContents(_oldText);
        _annotation.ModifiedAt = _oldModifiedAt;
        _annotation.IsEdited = _wasEdited;

        return EditResult.Ok($"Put the comment back to “{AddAnnotationCommand.Shorten(_oldText, 30)}”.");
    }

    /// <summary>
    /// Successive edits of the SAME comment collapse into one undo step. Someone correcting a
    /// comment twice means one change, and having to press Control+Z repeatedly to get back past
    /// their own corrections would make undo useless for exactly the case it is needed.
    /// </summary>
    public override bool CanMergeWith(EditCommand later) =>
        later is EditAnnotationCommand other && ReferenceEquals(other._annotation, _annotation);
}

#endregion

#region DeleteAnnotationCommand

/// <summary>Removes a comment, and remembers exactly where it was so undo can put it back.</summary>
public sealed class DeleteAnnotationCommand : EditCommand
{
    private readonly AnnotationElement _annotation;
    private readonly string _summary;

    private DocumentElement? _formerParent;
    private int _formerIndex = -1;

    public DeleteAnnotationCommand(AnnotationElement annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);

        _annotation = annotation;

        _summary = annotation.Contents.Trim() is { Length: > 0 } text
            ? $"“{AddAnnotationCommand.Shorten(text, 40)}”"
            : $"an empty comment on page {annotation.PageNumber}";
    }

    public override EditKind Kind => EditKind.AnnotationDeleted;

    /// <summary>
    /// Deleting somebody's comment removes information from the document, and unlike a value that
    /// can be typed again there is no way to recover the wording once the file is saved. Flagged so
    /// the save takes a backup and says what is about to go.
    /// </summary>
    public override EditConfidence Confidence => EditConfidence.Lossy;

    public override string Description => $"Deleted the comment {_summary}";

    protected override EditResult ApplyCore(PdfDocumentModel document)
    {
        var parent = _annotation.Parent;

        if (parent is null)
            return EditResult.Failed("That comment is not part of the document any more.");

        // Remembered before the removal, because afterwards there is nothing left to ask.
        _formerParent = parent;
        _formerIndex = AddAnnotationCommand.IndexOfChild(parent, _annotation);

        parent.RemoveChild(_annotation);
        document.RecordAnnotationDeleted(_annotation);
        document.RebuildReadingOrder();

        int replies = _annotation.Replies.Count;

        return EditResult.Ok(replies > 0
            ? $"Comment deleted, along with its {replies} {(replies == 1 ? "reply" : "replies")}."
            : "Comment deleted.");
    }

    protected override EditResult RevertCore(PdfDocumentModel document)
    {
        if (_formerParent is null)
            return EditResult.Failed("Where that comment came from is no longer known.");

        // Back in its own place, not appended. Reading order is the thing this program protects,
        // and an undo that moved the comment would be a different document from the one before it.
        _formerParent.AddChild(_annotation);

        if (_formerIndex >= 0)
            _formerParent.MoveChild(_annotation, _formerIndex);

        document.RestoreDeletedAnnotation(_annotation);
        document.RebuildReadingOrder();

        return EditResult.Ok($"Put back the comment {_summary}.");
    }
}

#endregion
