using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Editing;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;

namespace AccessiblePdfEditor.UI;

// =====================================================================================
//  MainForm.Annotations.cs
//
//  Writing comments: adding, editing, replying and deleting.
//
//  WHAT MAKES THIS DIFFERENT FROM EVERY OTHER PDF COMMENTING TOOL
//
//  Everywhere else, commenting starts with pointing at the page. Select some text with the
//  mouse, click the highlighter, drag a box. All of it assumes you can see where you are
//  putting things, and none of it has a keyboard equivalent that means anything.
//
//  Here a comment attaches to whatever the user is ON. They have navigated to a paragraph,
//  a table cell, a form field; "comment on this" is a complete instruction, and the page
//  coordinates are derived from the element rather than asked for. That is not a
//  workaround for the absence of a mouse — it is a better description of what the user
//  actually means, and it produces a comment that can say what it is about in words.
//
//  Every one of these goes through the ordinary edit history, so a comment can be undone
//  with Control+Z like anything else and the undo says which comment it removed.
// =====================================================================================

public sealed partial class MainForm
{
    #region Adding a comment

    /// <summary>
    /// Attaches a new comment to the element the reading position is on.
    /// </summary>
    /// <param name="kind">
    /// Comment for a note, Highlight to mark the text as well. A highlight still asks for a note:
    /// a mark with no words is announced as "highlight" and nothing more, which tells a listener
    /// that something was singled out but not what for.
    /// </param>
    private void AddCommentHere(AnnotationKind kind = AnnotationKind.Comment)
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        if (_navigation.Current is not { } anchor || anchor.Kind is ElementKind.Document)
        {
            Play(AudioCue.Boundary);
            Announce(
                "There is nothing here to comment on. Move to a paragraph, a table cell or a form " +
                "field first, then try again.",
                AnnouncementPriority.Assertive);

            return;
        }

        string what = kind == AnnotationKind.Highlight ? "Highlight" : "Comment";

        string? text = TextPromptDialog.Ask(this, Speech, Cues,
            $"{what} on {AddAnnotationCommand.DescribeAnchor(anchor)}",
            $"{what}:",
            BuildCommentGuidance(anchor, kind),
            multiline: true);

        if (text is null)
        {
            Announce("Nothing added.", AnnouncementPriority.Assertive);
            return;
        }

        if (text.Trim().Length == 0 && kind != AnnotationKind.Highlight)
        {
            Play(AudioCue.Rejected);
            Announce("An empty comment would say nothing to anyone reading it, so nothing was added.",
                AnnouncementPriority.Assertive);

            return;
        }

        ApplyEdit(new AddAnnotationCommand(anchor, text, _settings.AuthorName, kind));
    }

    private string BuildCommentGuidance(DocumentElement anchor, AnnotationKind kind)
    {
        var parts = new List<string>(3);

        if (kind == AnnotationKind.Highlight)
            parts.Add("The text will be marked, and whatever you write here is read out with it.");

        if (anchor.Text.Trim() is { Length: > 0 } text)
            parts.Add($"About: {AddAnnotationCommand.Shorten(text, 120)}");

        parts.Add(_settings.AuthorName is { Length: > 0 } author
            ? $"It will be signed {author}. Change that in Settings."
            : "It will be unsigned. Set your name in Settings to sign your comments.");

        return string.Join(" ", parts);
    }

    #endregion

    #region Editing, replying and deleting

    /// <summary>Changes the text of the comment the reading position is on.</summary>
    private void EditCommentHere()
    {
        if (CurrentComment("change") is not { } annotation)
            return;

        string? text = TextPromptDialog.Ask(this, Speech, Cues,
            "Change this comment",
            "Comment:",
            DescribeCommentForEditing(annotation),
            initialValue: annotation.Contents,
            multiline: true);

        if (text is null)
        {
            Announce("Left unchanged.", AnnouncementPriority.Assertive);
            return;
        }

        if (string.Equals(text, annotation.Contents, StringComparison.Ordinal))
        {
            Announce("That is what it already said, so nothing was changed.", AnnouncementPriority.Assertive);
            return;
        }

        ApplyEdit(new EditAnnotationCommand(annotation, text));
    }

    /// <summary>Adds a reply to the comment the reading position is on.</summary>
    private void ReplyToCommentHere()
    {
        if (CurrentComment("reply to") is not { } annotation)
            return;

        string? text = TextPromptDialog.Ask(this, Speech, Cues,
            "Reply to this comment",
            "Reply:",
            DescribeCommentForEditing(annotation),
            multiline: true);

        if (text is null || text.Trim().Length == 0)
        {
            Announce("No reply added.", AnnouncementPriority.Assertive);
            return;
        }

        ApplyEdit(new ReplyToAnnotationCommand(annotation, text, _settings.AuthorName));
    }

    /// <summary>
    /// Deletes the comment the reading position is on, after reading it back.
    ///
    /// The confirmation quotes the comment in full rather than asking "delete this comment?".
    /// Someone who cannot see the page has no other way to check they are on the one they meant,
    /// and deleting somebody else's remark by accident is not recoverable once the file is saved.
    /// </summary>
    private void DeleteCommentHere()
    {
        if (CurrentComment("delete") is not { } annotation)
            return;

        int replies = annotation.Replies.Count;

        string question =
            $"Delete this comment? {DescribeCommentForEditing(annotation)}" +
            (replies > 0
                ? $" Its {replies} {(replies == 1 ? "reply goes" : "replies go")} with it."
                : string.Empty);

        if (!Confirm(question, "Delete comment"))
        {
            Announce("Kept.", AnnouncementPriority.Assertive);
            return;
        }

        ApplyEdit(new DeleteAnnotationCommand(annotation));
    }

    #endregion

    #region Shared

    /// <summary>
    /// The comment the user is on, or null with an announcement saying how to reach one. The
    /// announcement names the key that moves to a comment, because "you are not on a comment" on
    /// its own leaves the user knowing they are somewhere wrong but not how to get somewhere right.
    /// </summary>
    private AnnotationElement? CurrentComment(string verb)
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return null;
        }

        if (_navigation.Current is AnnotationElement annotation)
            return annotation;

        Play(AudioCue.Boundary);

        Announce(
            _document.Annotations.Count == 0
                ? $"There are no comments in this document to {verb}. Press Control plus Shift plus M " +
                  "to write one."
                : $"You are not on a comment. Press A to move to the next one, or Control plus M for " +
                  $"the list of all {_document.Annotations.Count}.",
            AnnouncementPriority.Assertive);

        return null;
    }

    private static string DescribeCommentForEditing(AnnotationElement annotation)
    {
        var parts = new List<string>(3);

        if (annotation.Author is { Length: > 0 } author)
            parts.Add($"{author} wrote:");

        parts.Add(annotation.Contents.Length > 0 ? $"“{annotation.Contents}”" : "(no text)");

        if (annotation.AnchoredText is { Length: > 0 } anchored)
            parts.Add($"About: {AddAnnotationCommand.Shorten(anchored, 80)}");

        return string.Join(" ", parts);
    }

    #endregion
}
