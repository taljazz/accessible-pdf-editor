using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;

namespace AccessiblePdfEditor.Model.Forms;

// =====================================================================================
//  ActionFormFields.cs
//
//  The two field types that hold no value of their own: push buttons and signature fields.
//
//  Both are places where this editor deliberately does LESS than it could, and says so.
//
//  A push button in a PDF can submit the form to a URL, reset it, run JavaScript, or open
//  a file. Performing any of those silently because the user pressed Enter on something
//  announced as "button" would be indefensible — they cannot see where a submit button
//  points. So the action is resolved, described in words, and confirmed before anything
//  happens, and the actions this editor will not perform at all are named rather than
//  quietly ignored.
//
//  Signature fields are reported, never signed. Applying a cryptographic signature is a
//  legal act, and a signature this program applied on a user's behalf without their
//  certificate and explicit intent would be worse than no signature at all.
// =====================================================================================

#region Push button actions — what a button would do if pressed

/// <summary>What a push button does when activated.</summary>
public enum ButtonAction
{
    /// <summary>Nothing, or an action that could not be identified.</summary>
    None = 0,

    /// <summary>Sends the form's data somewhere.</summary>
    SubmitForm,

    /// <summary>Clears the form's fields.</summary>
    ResetForm,

    /// <summary>Imports field values from a data file.</summary>
    ImportData,

    /// <summary>Follows a link.</summary>
    OpenUrl,

    /// <summary>Runs JavaScript embedded in the document.</summary>
    RunJavaScript,

    /// <summary>Moves to another place in the document.</summary>
    GoToDestination,

    /// <summary>Prints the document.</summary>
    Print,
}

#endregion

#region PushButtonFormField

/// <summary>A push button: performs an action rather than holding a value.</summary>
public sealed class PushButtonFormField : PdfFormField
{
    #region Construction and state

    public PushButtonFormField(int pageNumber, string fullyQualifiedName, ButtonAction action = ButtonAction.None)
        : base(pageNumber, fullyQualifiedName)
    {
        Action = action;
        RefreshDerivedState();
    }

    public override FormFieldKind FieldKind => FormFieldKind.PushButton;

    /// <summary>What this button would do.</summary>
    public ButtonAction Action { get; internal set; }

    /// <summary>
    /// Where the action points — the submit URL, the destination page, the file to open. Read out
    /// before the action is confirmed, because for a submit button this is the only thing that
    /// tells the user where their answers are about to go.
    /// </summary>
    public string? ActionTarget { get; internal set; }

    /// <summary>The button's face text, from its appearance's /CA caption.</summary>
    public string? Caption { get; internal set; }

    /// <summary>A button holds no value, so there is never anything to have filled in.</summary>
    public override bool HasValue => false;

    public override string ValueForSpeech => string.Empty;

    /// <summary>
    /// A button's caption is the best label it has, and it is usually better than its field name —
    /// "Submit application" against "Button3". Checked before the inherited resolution so the
    /// visible text wins over an internal identifier.
    /// </summary>
    private string ButtonLabel =>
        !string.IsNullOrWhiteSpace(Caption) ? Caption!.Trim() : Label;

    #endregion

    #region Describing the action in words
    // The phrasing puts the consequence first — "submit this form", not "run the submit action" —
    // because that is what the user is deciding about.

    /// <summary>What pressing this button would do, phrased as a consequence.</summary>
    public string ActionDescription => Action switch
    {
        ButtonAction.SubmitForm => ActionTarget is { Length: > 0 } url
            ? $"send this form's answers to {url}"
            : "send this form's answers",
        ButtonAction.ResetForm => "clear every field in this form",
        ButtonAction.ImportData => "load field values from a file",
        ButtonAction.OpenUrl => ActionTarget is { Length: > 0 } link
            ? $"open {link}"
            : "open a link",
        ButtonAction.GoToDestination => ActionTarget is { Length: > 0 } destination
            ? $"go to {destination}"
            : "go somewhere else in this document",
        ButtonAction.Print => "print the document",
        ButtonAction.RunJavaScript => "run a script stored in the document",
        _ => "nothing this editor can identify",
    };

    #endregion

    #region Activation — confirmed for anything consequential, refused for scripts

    /// <summary>
    /// Buttons that run JavaScript cannot be activated. This editor does not execute scripts out of
    /// documents, and pretending the button worked would be worse than saying plainly that it will
    /// not run.
    /// </summary>
    public override bool CanActivate =>
        !IsReadOnly && Action is not (ButtonAction.None or ButtonAction.RunJavaScript);

    protected override string UnavailableReason => Action switch
    {
        ButtonAction.RunJavaScript =>
            $"{ButtonLabel} runs a script stored in the document. This editor does not run scripts, so this button does nothing here.",
        ButtonAction.None => $"{ButtonLabel} has no action attached to it.",
        _ => $"{ButtonLabel} is read-only.",
    };

    public override string ActivationHint => "press Enter to activate, you will be asked to confirm first";

    /// <summary>
    /// Every button that does something consequential is confirmed first, with the consequence
    /// spoken. Reset in particular is confirmed emphatically: it discards everything the user has
    /// typed, and a misplaced Enter on a form that took twenty minutes to fill in is not a mistake
    /// anyone should be able to make by accident.
    /// </summary>
    protected override InteractionResult ActivateCore(IInteractionHost host)
    {
        string question = Action == ButtonAction.ResetForm
            ? $"{ButtonLabel} will clear every field in this form, including everything you have typed. Are you sure?"
            : $"{ButtonLabel} will {ActionDescription}. Continue?";

        if (!host.Confirm(question))
            return InteractionResult.Cancelled($"{ButtonLabel} not activated.");

        return Action switch
        {
            ButtonAction.OpenUrl or ButtonAction.SubmitForm when ActionTarget is { Length: > 0 } target =>
                host.OpenExternal(target)
                    ? InteractionResult.OpenedExternally($"{ButtonLabel} activated.")
                    : InteractionResult.Failed($"Could not carry out {ButtonLabel}."),

            ButtonAction.GoToDestination when int.TryParse(ActionTarget, out int page) =>
                Navigate(host, page),

            // Reset changes every field in the form rather than this one, so it is carried out by
            // the controller that owns them all. Reaching here means the controller did not
            // intercept it, and saying so is better than reporting a success that did not happen.
            ButtonAction.ResetForm =>
                InteractionResult.Failed(
                    $"{ButtonLabel} could not clear the form. Use Clear this form on the Tools menu."),

            // Honestly refused rather than reported as done. A button that claims to have imported
            // data or printed, and did neither, is worse than one that admits it cannot.
            ButtonAction.ImportData =>
                InteractionResult.NotAvailable(
                    $"{ButtonLabel} loads field values from a data file. This editor does not do that, " +
                    "so nothing has changed."),

            ButtonAction.Print =>
                InteractionResult.NotAvailable(
                    $"{ButtonLabel} prints the document. This editor does not print; open the file in " +
                    "another reader to print it."),

            _ => InteractionResult.NotAvailable($"{ButtonLabel} cannot be carried out here."),
        };
    }

    private static InteractionResult Navigate(IInteractionHost host, int page)
    {
        host.NavigateToPage(page);
        return InteractionResult.Navigated($"Moved to page {page}.");
    }

    /// <summary>A button holds no value, so setting one is always refused.</summary>
    protected override FieldValidation ApplyValue(string rawValue) =>
        FieldValidation.Reject($"{ButtonLabel} is a button. It has no value to set.");

    public override TResult Accept<TResult>(IFormFieldVisitor<TResult> visitor) => visitor.VisitPushButton(this);

    #endregion

    #region Announcement

    protected override string DescribeContent(VerbosityLevel verbosity) => ButtonLabel;

    protected override string DescribeState(VerbosityLevel verbosity)
    {
        var parts = new List<string>(2);

        // The consequence is announced at Normal verbosity, not only at Detailed. What a button
        // does is not extra detail — it is the thing the user needs before pressing it.
        if (verbosity != VerbosityLevel.Terse && Action != ButtonAction.None)
            parts.Add($"will {ActionDescription}");

        string baseState = base.DescribeState(verbosity);
        if (baseState.Length > 0)
            parts.Add(baseState);

        return string.Join(", ", parts);
    }

    #endregion
}

#endregion

#region SignatureFormField

/// <summary>
/// A digital signature field. Its state is reported; this editor never applies a signature.
/// </summary>
public sealed class SignatureFormField : PdfFormField
{
    #region Construction and state

    public SignatureFormField(int pageNumber, string fullyQualifiedName)
        : base(pageNumber, fullyQualifiedName)
    {
        RefreshDerivedState();
    }

    public override FormFieldKind FieldKind => FormFieldKind.Signature;

    /// <summary>Whether this field already carries a signature.</summary>
    public bool IsSigned => States.HasFlag(FieldStates.Signed);

    /// <summary>Who signed it, from the signature dictionary's /Name.</summary>
    public string? SignerName { get; internal set; }

    /// <summary>When it was signed, from the signature dictionary's /M.</summary>
    public DateTimeOffset? SignedAt { get; internal set; }

    /// <summary>The stated reason for signing, from /Reason.</summary>
    public string? SigningReason { get; internal set; }

    /// <summary>The stated place of signing, from /Location.</summary>
    public string? SigningLocation { get; internal set; }

    /// <summary>Marks this field as carrying a signature. Called by the loader.</summary>
    public void MarkSigned() => SetState(FieldStates.Signed);

    /// <summary>
    /// A signature the user has placed but not yet saved.
    ///
    /// Held on the field rather than applied immediately because nothing in this editor writes to
    /// disk until the user saves. That matters more here than elsewhere: a signature applied the
    /// moment it was drawn could not be undone, and undoing a mistake is exactly what someone who
    /// cannot see the result needs most.
    /// </summary>
    public SignatureMark? PendingMark { get; private set; }

    /// <summary>Places a signature on this field, to be written when the document is saved.</summary>
    public void PlaceSignature(SignatureMark? mark)
    {
        PendingMark = mark;

        if (mark is null)
            ClearState(FieldStates.Modified);
        else
            SetState(FieldStates.Modified);

        RefreshDerivedState();
    }

    /// <summary>True when a signature is placed but not yet written to the file.</summary>
    public bool HasPendingSignature => PendingMark is not null;

    public override bool HasValue => IsSigned || PendingMark is not null;

    #endregion

    #region Value in speech
    // A signed field reports who, when and why — the facts someone would want before relying on a
    // signed document. Note that this reports what the document CLAIMS: this editor does not
    // validate the cryptography, and saying "signed by X" must not be mistaken for "verified".

    public override string ValueForSpeech
    {
        get
        {
            if (PendingMark is { } pending)
                return $"signed with {pending.Describe()}, not yet saved";

            if (!IsSigned)
                return "not signed";

            var parts = new List<string>(4) { "signed" };

            if (SignerName is { Length: > 0 } signer)
                parts.Add($"by {signer}");

            if (SignedAt is { } when)
                parts.Add($"on {when:d MMMM yyyy}");

            if (SigningReason is { Length: > 0 } reason)
                parts.Add($"reason: {reason}");

            return string.Join(", ", parts);
        }
    }

    #endregion

    #region Activation — always refused, with an explanation

    /// <summary>
    /// Never activatable. Signing is a legal act requiring the user's own certificate and
    /// deliberate intent, and it is not something this editor performs.
    /// </summary>
    public override bool CanActivate => false;

    protected override string UnavailableReason => IsSigned
        ? $"{Label} is already signed. This editor can read signatures but does not verify or change them."
        : $"{Label} is a signature field. This editor does not apply signatures; use a dedicated signing tool.";

    public override string ActivationHint => "signature fields cannot be filled in here";

    protected override InteractionResult ActivateCore(IInteractionHost host) =>
        InteractionResult.NotAvailable(UnavailableReason);

    protected override FieldValidation ApplyValue(string rawValue) =>
        FieldValidation.Reject($"{Label} is a signature field and cannot be typed into.");

    public override TResult Accept<TResult>(IFormFieldVisitor<TResult> visitor) => visitor.VisitSignature(this);

    #endregion

    #region Announcement

    /// <summary>
    /// The caveat is stated at Detailed verbosity: this editor reports what the document says about
    /// its signature, and does not check whether that claim is true. Anyone deciding whether to
    /// trust a signed document deserves to know which of those they are hearing.
    /// </summary>
    protected override string DescribeState(VerbosityLevel verbosity)
    {
        var parts = new List<string>(2);

        if (IsSigned && verbosity == VerbosityLevel.Detailed)
            parts.Add("signature not verified by this editor");

        string baseState = base.DescribeState(verbosity);
        if (baseState.Length > 0)
            parts.Add(baseState);

        return string.Join(", ", parts);
    }

    #endregion
}

#endregion
