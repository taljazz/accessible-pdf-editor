using AccessiblePdfEditor.Model;

namespace AccessiblePdfEditor.Model.Elements;

// =====================================================================================
//  InteractiveElement.cs
//
//  Everything in a document that can be ACTED on rather than merely read: links, comments
//  and other annotations, file attachments — and, through a subclass in the Forms folder,
//  every form field.
//
//  The design point here is the Activate template method. Activating a link, a comment and
//  a checkbox are completely different operations, but from the reader's point of view they
//  are the same gesture: land on the thing, press Enter, hear what happened. So the base
//  class owns the parts that must never differ — checking that activation is allowed,
//  refusing politely when it is not, and returning a result the caller can announce — and
//  leaves only the actual doing to the subclass.
//
//  The host interface is deliberately UI-free. An element asks "confirm this with the user"
//  or "go to this page"; it never knows there is a window involved. That keeps the whole
//  model testable without starting WinForms.
// =====================================================================================

#region The interaction host — how an element reaches the outside world
// Passed into Activate rather than injected into the element, so elements stay plain data that
// can be built by a loader, compared in tests and thrown away, with no service references
// hiding inside them.

/// <summary>
/// The services an interactive element needs in order to do its work. Implemented by the
/// application shell; deliberately free of any UI type so the model can be tested headlessly.
/// </summary>
public interface IInteractionHost
{
    /// <summary>
    /// Asks the user a yes or no question and waits for the answer. Used before anything that
    /// leaves the document, so that following a link is never a surprise.
    /// </summary>
    bool Confirm(string question);

    /// <summary>Moves the reading position to an element in this document.</summary>
    void NavigateTo(DocumentElement target);

    /// <summary>Moves the reading position to a page.</summary>
    void NavigateToPage(int pageNumber);

    /// <summary>
    /// Opens something outside this document — a web address, a file. The host is responsible for
    /// having confirmed it first; this method just performs it.
    /// </summary>
    bool OpenExternal(string target);

    /// <summary>Says something to the user immediately, without waiting for the caller to return.</summary>
    void Announce(string message, AnnouncementPriority priority = AnnouncementPriority.Polite);
}

#endregion

#region InteractionResult — what happened, in a form the caller can announce

/// <summary>The outcome of activating an element, together with what to say about it.</summary>
public readonly record struct InteractionResult(InteractionOutcome Outcome, string Message)
{
    public static InteractionResult Succeeded(string message) =>
        new(InteractionOutcome.Succeeded, message);

    public static InteractionResult ValueChanged(string message) =>
        new(InteractionOutcome.ValueChanged, message);

    public static InteractionResult Navigated(string message) =>
        new(InteractionOutcome.NavigatedWithinDocument, message);

    public static InteractionResult OpenedExternally(string message) =>
        new(InteractionOutcome.OpenedExternally, message);

    public static InteractionResult NotAvailable(string message) =>
        new(InteractionOutcome.NotAvailable, message);

    public static InteractionResult Cancelled(string message = "Cancelled.") =>
        new(InteractionOutcome.Cancelled, message);

    public static InteractionResult Failed(string message) =>
        new(InteractionOutcome.Failed, message);

    /// <summary>True when the interaction did what it set out to do.</summary>
    public bool IsSuccess => Outcome is InteractionOutcome.Succeeded
        or InteractionOutcome.ValueChanged
        or InteractionOutcome.NavigatedWithinDocument
        or InteractionOutcome.OpenedExternally;
}

#endregion

#region InteractiveElement — the abstract base for anything actionable

/// <summary>
/// Base class for elements the user can act on. Owns the activation contract; subclasses supply
/// only what actually happens.
/// </summary>
public abstract class InteractiveElement : DocumentElement
{
    protected InteractiveElement(int pageNumber)
        : base(pageNumber) { }

    #region The activation contract
    // Activate is not virtual. Subclasses override ActivateCore, so that the guard, the polite
    // refusal and the shape of the result are identical for every interactive thing in the
    // program. A subclass cannot forget to check whether it is enabled, because it never gets the
    // chance to run when it is not.

    /// <summary>Whether activating this element would currently do anything.</summary>
    public abstract bool CanActivate { get; }

    /// <summary>
    /// How to operate this element, in words — "press Enter to follow this link". Spoken at
    /// Detailed verbosity and available on request at any verbosity, because the whole app is
    /// driven by keys and a key you do not know about might as well not exist.
    /// </summary>
    public abstract string ActivationHint { get; }

    /// <summary>
    /// Why this element cannot be activated. Only consulted when <see cref="CanActivate"/> is
    /// false, and spoken instead of silently doing nothing — an unexplained no-op is the most
    /// disorienting thing a keyboard-driven program can do.
    /// </summary>
    protected virtual string UnavailableReason => "This item cannot be activated.";

    /// <summary>
    /// Activates the element. Guards, then delegates to <see cref="ActivateCore"/>.
    /// </summary>
    public InteractionResult Activate(IInteractionHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (!CanActivate)
            return InteractionResult.NotAvailable(UnavailableReason);

        try
        {
            return ActivateCore(host);
        }
        catch (Exception ex)
        {
            // An element must never take the application down. Whatever went wrong, the user gets
            // a sentence they can act on and keeps their place in the document.
            return InteractionResult.Failed($"Could not complete that: {ex.Message}");
        }
    }

    /// <summary>Performs the activation. Called only when <see cref="CanActivate"/> is true.</summary>
    protected abstract InteractionResult ActivateCore(IInteractionHost host);

    /// <summary>
    /// Interactive elements append their operating hint to the state description at Detailed
    /// verbosity. Subclasses that override this should call the base and keep its result.
    /// </summary>
    protected override string DescribeState(VerbosityLevel verbosity) =>
        verbosity == VerbosityLevel.Detailed ? ActivationHint : string.Empty;

    #endregion
}

#endregion

#region LinkElement — a hyperlink, which always says where it goes before going there
// Announcing the destination before following it is not politeness, it is safety. A sighted user
// sees a URL in a status bar and can decide not to click. A listener gets that chance only if the
// program says the address out loud and waits.

/// <summary>A hyperlink in the document.</summary>
public sealed class LinkElement : InteractiveElement
{
    #region Construction and state

    public LinkElement(int pageNumber, string text, LinkTargetKind targetKind, string target)
        : base(pageNumber)
    {
        LinkText = text ?? string.Empty;
        TargetKind = targetKind;
        Target = target ?? string.Empty;
    }

    public override ElementKind Kind => ElementKind.Link;

    /// <summary>The visible text of the link.</summary>
    public string LinkText { get; }

    public override string Text => ActualText ?? LinkText;

    /// <summary>What sort of thing the link points at.</summary>
    public LinkTargetKind TargetKind { get; }

    /// <summary>
    /// The destination: a URL, an email address, a file path, or a page number as text for an
    /// internal jump.
    /// </summary>
    public string Target { get; }

    /// <summary>The page this link jumps to, for internal destinations.</summary>
    public int? TargetPage { get; init; }

    /// <summary>
    /// The link's own description, from the annotation's /Contents. Authors use it to explain a
    /// link whose visible text does not stand alone, and it is what a screen reader prefers over
    /// the link text when present.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>Whether the link has been followed in this session.</summary>
    public bool HasBeenVisited { get; private set; }

    /// <summary>
    /// True when the link text says nothing about where it goes. "Click here" and a bare URL are
    /// both faults, for opposite reasons: one has no information, the other is unlistenable. The
    /// auditor reports these and the editor can write a better description.
    /// </summary>
    public bool HasUninformativeText
    {
        get
        {
            string text = Text.Trim();
            if (text.Length == 0)
                return true;

            if (text.Length > 60 && !text.Contains(' '))
                return true;

            ReadOnlySpan<string> empty =
            [
                "click here", "here", "read more", "more", "link", "this link",
                "download", "continue", "go", "learn more", "see more", "details",
            ];

            foreach (string phrase in empty)
            {
                if (text.Equals(phrase, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }

    #endregion

    #region Activation

    public override bool CanActivate => TargetKind != LinkTargetKind.UnsupportedAction && Target.Length > 0;

    protected override string UnavailableReason => TargetKind == LinkTargetKind.UnsupportedAction
        ? "This link runs an action this editor will not perform."
        : "This link has no destination.";

    public override string ActivationHint => TargetKind == LinkTargetKind.InternalDestination
        ? "press Enter to jump there"
        : "press Enter to open, you will be asked to confirm first";

    /// <summary>
    /// Internal jumps happen immediately, because staying inside the document is not a decision
    /// worth interrupting for. Anything that leaves the document is read out in full and confirmed,
    /// because that is the point at which the user might be being sent somewhere they did not
    /// intend — and hearing the address is their only defence.
    /// </summary>
    protected override InteractionResult ActivateCore(IInteractionHost host)
    {
        HasBeenVisited = true;

        if (TargetKind == LinkTargetKind.InternalDestination)
        {
            if (TargetPage is not { } page)
                return InteractionResult.Failed("This link points inside the document but its destination could not be resolved.");

            host.NavigateToPage(page);
            return InteractionResult.Navigated($"Jumped to page {page}.");
        }

        string what = TargetKind switch
        {
            LinkTargetKind.WebUrl => "web address",
            LinkTargetKind.Email => "email address",
            LinkTargetKind.ExternalFile => "file",
            LinkTargetKind.EmbeddedFile => "embedded file",
            _ => "destination",
        };

        if (!host.Confirm($"Open this {what}? {SpeakableTarget}"))
            return InteractionResult.Cancelled("Link not opened.");

        return host.OpenExternal(Target)
            ? InteractionResult.OpenedExternally($"Opened {what}.")
            : InteractionResult.Failed($"Could not open that {what}.");
    }

    /// <summary>
    /// The target rendered so it can be understood by ear. A URL read character by character is
    /// unintelligible, so the host name — the part that actually tells you where you are going —
    /// is separated out and said first.
    /// </summary>
    public string SpeakableTarget
    {
        get
        {
            if (TargetKind == LinkTargetKind.WebUrl && Uri.TryCreate(Target, UriKind.Absolute, out var uri))
            {
                string host = uri.Host;
                return uri.AbsolutePath is "" or "/" && string.IsNullOrEmpty(uri.Query)
                    ? host
                    : $"{host}, path {uri.AbsolutePath}";
            }

            return Target;
        }
    }

    #endregion

    #region Announcement

    protected override string DescribeRole(VerbosityLevel verbosity) =>
        HasBeenVisited ? "visited link" : "link";

    protected override string DescribeContent(VerbosityLevel verbosity)
    {
        // The annotation's own description beats the visible text: it exists precisely because the
        // author judged the visible text insufficient.
        if (Description is { Length: > 0 } description)
            return description;

        string text = Text;
        if (text.Length > 0 && !HasUninformativeText)
            return text;

        // Uninformative text alone would leave the listener with nothing, so the destination is
        // added rather than substituted — the visible text still matters for finding it again.
        return text.Length > 0 ? $"{text}, to {SpeakableTarget}" : SpeakableTarget;
    }

    protected override string DescribeState(VerbosityLevel verbosity)
    {
        var parts = new List<string>(2);

        if (verbosity != VerbosityLevel.Terse && HasUninformativeText && Description is null)
            parts.Add("link text does not describe its destination");

        string baseState = base.DescribeState(verbosity);
        if (baseState.Length > 0)
            parts.Add(baseState);

        return string.Join(", ", parts);
    }

    #endregion
}

#endregion

#region AnnotationElement — comments, highlights and other markup

/// <summary>A markup annotation: a comment, a highlight, a strike-through and so on.</summary>
public sealed class AnnotationElement : InteractiveElement
{
    #region Construction and state

    public AnnotationElement(int pageNumber, AnnotationKind annotationKind, string contents)
        : base(pageNumber)
    {
        AnnotationKind = annotationKind;
        Contents = contents ?? string.Empty;
    }

    public override ElementKind Kind => ElementKind.Annotation;

    /// <summary>Which sort of annotation this is.</summary>
    public AnnotationKind AnnotationKind { get; }

    /// <summary>The annotation's text, from its /Contents.</summary>
    public string Contents { get; private set; }

    public override string Text => ActualText ?? Contents;

    /// <summary>Who wrote it, from the annotation's /T.</summary>
    public string? Author { get; init; }

    /// <summary>When it was last changed, from the annotation's /M.</summary>
    public DateTimeOffset? ModifiedAt { get; init; }

    /// <summary>
    /// The document text this annotation covers, recovered from its quad points. A highlight with
    /// no note of its own is meaningless without this: "highlight" tells the listener nothing,
    /// "highlight over 'the deadline is 31 March'" tells them everything.
    /// </summary>
    public string? AnchoredText { get; set; }

    /// <summary>The annotation this one replies to, for comment threads.</summary>
    public AnnotationElement? InReplyTo { get; set; }

    /// <summary>Replies to this annotation, in order.</summary>
    public IReadOnlyList<AnnotationElement> Replies => Children.OfType<AnnotationElement>().ToList();

    /// <summary>True when this annotation was created in this session and is not yet saved.</summary>
    public bool IsUnsaved { get; internal set; }

    /// <summary>The identifier of the underlying PDF object, so edits can find it again on save.</summary>
    public string? SourceObjectId { get; init; }

    /// <summary>Replaces the annotation's text. Used by editing commands.</summary>
    public void SetContents(string contents) => Contents = contents ?? string.Empty;

    #endregion

    #region Activation

    /// <summary>
    /// Popups belong to another annotation and are never activated on their own; activating one
    /// would announce the same comment twice.
    /// </summary>
    public override bool CanActivate => AnnotationKind != AnnotationKind.Popup;

    public override string ActivationHint => "press Enter to read the full comment and any replies";

    protected override InteractionResult ActivateCore(IInteractionHost host)
    {
        var lines = new List<string>(4);

        if (Author is { Length: > 0 } author)
            lines.Add($"{author} wrote:");

        lines.Add(Contents.Length > 0 ? Contents : "No comment text.");

        if (AnchoredText is { Length: > 0 } anchored)
            lines.Add($"About the text: {anchored}");

        var replies = Replies;
        if (replies.Count > 0)
            lines.Add($"{replies.Count} {(replies.Count == 1 ? "reply" : "replies")}.");

        host.Announce(string.Join(" ", lines), AnnouncementPriority.Assertive);
        return InteractionResult.Succeeded("Comment read.");
    }

    #endregion

    #region Announcement

    protected override string DescribeRole(VerbosityLevel verbosity) => AnnotationKind switch
    {
        AnnotationKind.Comment => "comment",
        AnnotationKind.Highlight => "highlight",
        AnnotationKind.Underline => "underline",
        AnnotationKind.StrikeOut => "strike-through",
        AnnotationKind.Squiggly => "squiggly underline",
        AnnotationKind.FreeText => "text box",
        AnnotationKind.Stamp => "stamp",
        AnnotationKind.Ink => "drawing",
        AnnotationKind.FileAttachment => "attached file",
        AnnotationKind.Popup => "popup",
        _ => "annotation",
    };

    protected override string DescribeContent(VerbosityLevel verbosity)
    {
        var parts = new List<string>(3);

        if (verbosity != VerbosityLevel.Terse && Author is { Length: > 0 } author)
            parts.Add($"by {author}");

        // A markup annotation over text is about that text, so lead with what it covers. Otherwise
        // "strike-through" leaves the listener knowing something was struck out but not what.
        if (AnchoredText is { Length: > 0 } anchored)
            parts.Add($"over {Truncate(anchored, 60)}");

        if (Contents.Length > 0)
            parts.Add(Truncate(Contents, 120));
        else if (parts.Count == 0)
            parts.Add("no text");

        return string.Join(", ", parts);
    }

    protected override string DescribeState(VerbosityLevel verbosity)
    {
        var parts = new List<string>(3);

        int replies = Replies.Count;
        if (replies > 0)
            parts.Add($"{replies} {(replies == 1 ? "reply" : "replies")}");

        if (IsUnsaved)
            parts.Add("not saved");

        string baseState = base.DescribeState(verbosity);
        if (baseState.Length > 0)
            parts.Add(baseState);

        return string.Join(", ", parts);
    }

    #endregion
}

#endregion

#region AttachmentElement — a file carried inside the document

/// <summary>A file embedded in the document.</summary>
public sealed class AttachmentElement : InteractiveElement
{
    public AttachmentElement(int pageNumber, string fileName, long sizeInBytes)
        : base(pageNumber)
    {
        FileName = fileName ?? "unnamed file";
        SizeInBytes = sizeInBytes;
    }

    public override ElementKind Kind => ElementKind.Attachment;

    /// <summary>The attachment's filename.</summary>
    public string FileName { get; }

    /// <summary>Its size in bytes.</summary>
    public long SizeInBytes { get; }

    /// <summary>The attachment's description, from its /Desc.</summary>
    public string? Description { get; init; }

    public override string Text => FileName;

    public override bool CanActivate => true;

    public override string ActivationHint => "press Enter to save this attachment to disk";

    /// <summary>
    /// An attachment is saved to disk rather than opened. Opening a file that arrived inside a
    /// document, without the user choosing where it lands or seeing what it is, is not a decision
    /// this program makes on someone's behalf.
    /// </summary>
    protected override InteractionResult ActivateCore(IInteractionHost host)
    {
        if (!host.Confirm($"Save the attached file {FileName}, {SpokenSize}, to disk?"))
            return InteractionResult.Cancelled("Attachment not saved.");

        return host.OpenExternal($"attachment:{FileName}")
            ? InteractionResult.Succeeded($"Saved {FileName}.")
            : InteractionResult.Failed($"Could not save {FileName}.");
    }

    /// <summary>The size in units a person would say out loud.</summary>
    public string SpokenSize => SizeInBytes switch
    {
        < 1024 => $"{SizeInBytes} bytes",
        < 1024 * 1024 => $"{SizeInBytes / 1024.0:0.#} kilobytes",
        < 1024L * 1024 * 1024 => $"{SizeInBytes / (1024.0 * 1024):0.#} megabytes",
        _ => $"{SizeInBytes / (1024.0 * 1024 * 1024):0.#} gigabytes",
    };

    protected override string DescribeRole(VerbosityLevel verbosity) => "attachment";

    protected override string DescribeContent(VerbosityLevel verbosity)
    {
        var parts = new List<string> { FileName };

        if (Description is { Length: > 0 } description)
            parts.Add(description);

        if (verbosity != VerbosityLevel.Terse)
            parts.Add(SpokenSize);

        return string.Join(", ", parts);
    }
}

#endregion
