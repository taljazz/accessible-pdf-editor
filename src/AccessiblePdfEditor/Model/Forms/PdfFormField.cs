using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;

namespace AccessiblePdfEditor.Model.Forms;

// =====================================================================================
//  PdfFormField.cs
//
//  The abstract base for every interactive form field, and the validation contract they
//  all share.
//
//  Filling in a form is where an inaccessible PDF stops being merely annoying and starts
//  costing people money and time: a benefits claim, a job application, a medical intake
//  form. The failure is almost never that the field cannot be typed into — it is that the
//  field has no name, so the user is asked to type something into a box that announces
//  itself as "edit". Everything in this file is arranged around fixing that.
//
//  Two template methods carry the design:
//
//    Label      — resolves a usable name from four sources in a fixed order of trust,
//                 and records when it had to fall back to a guess.
//    TrySetValue — guards, delegates the type-specific check to the subclass, then updates
//                 the shared state flags. A subclass writes only its own validation rule
//                 and cannot forget to maintain the flags the rest of the app reads.
// =====================================================================================

#region FieldValidation — the result of trying to put a value into a field
// Carries a spoken message rather than an error code, because the message is the product:
// "that is not a valid date, try day slash month slash year" is useful, "ERR_FORMAT" is not.

/// <summary>The outcome of validating and applying a value to a field.</summary>
public readonly record struct FieldValidation(bool Accepted, string Message, string? NormalisedValue = null)
{
    /// <summary>The value was accepted, possibly after being tidied into a canonical form.</summary>
    public static FieldValidation Accept(string message, string? normalised = null) =>
        new(true, message, normalised);

    /// <summary>The value was rejected. The message explains what would be acceptable.</summary>
    public static FieldValidation Reject(string message) => new(false, message);
}

#endregion

#region The visitor — how the UI builds an editor without the model knowing about the UI
// Double dispatch. Each concrete field implements Accept by calling the visitor method for its
// own type, so the UI layer can build the right control for each field with no type switch and
// no casting, and — more importantly — the model never references a WinForms type.
//
// The practical payoff: adding a new field type makes the compiler point at every visitor that
// needs updating, instead of leaving a switch statement somewhere to silently fall through to a
// default that renders the field unusable.

/// <summary>
/// Visitor over the form field hierarchy. Implemented by the UI to build editors, and by the save
/// layer to write values back, without either needing to know the full set of field types.
/// </summary>
/// <typeparam name="TResult">What the visitor produces for each field.</typeparam>
public interface IFormFieldVisitor<out TResult>
{
    TResult VisitText(TextFormField field);
    TResult VisitCheckBox(CheckBoxFormField field);
    TResult VisitRadioGroup(RadioGroupFormField field);
    TResult VisitChoice(ChoiceFormField field);
    TResult VisitPushButton(PushButtonFormField field);
    TResult VisitSignature(SignatureFormField field);
}

#endregion

#region PdfFormField — the abstract base

/// <summary>
/// Base class for every interactive form field. Owns naming, state tracking and the validation
/// contract; subclasses supply the type-specific behaviour.
/// </summary>
public abstract class PdfFormField : InteractiveElement
{
    #region Construction and identity
    // A PDF field's name is a dotted path ("applicant.address.postcode"). The partial name is the
    // last segment. Neither is meant for humans, but the last segment is often the only clue to a
    // field's purpose when the author supplied nothing else, so both are kept.

    protected PdfFormField(int pageNumber, string fullyQualifiedName)
        : base(pageNumber)
    {
        FullyQualifiedName = fullyQualifiedName ?? string.Empty;

        int lastDot = FullyQualifiedName.LastIndexOf('.');
        PartialName = lastDot >= 0 && lastDot < FullyQualifiedName.Length - 1
            ? FullyQualifiedName[(lastDot + 1)..]
            : FullyQualifiedName;
    }

    public sealed override ElementKind Kind => ElementKind.FormField;

    /// <summary>Which kind of field this is.</summary>
    public abstract FormFieldKind FieldKind { get; }

    /// <summary>The field's full dotted name, as stored in the PDF.</summary>
    public string FullyQualifiedName { get; }

    /// <summary>The last segment of the field's name.</summary>
    public string PartialName { get; }

    /// <summary>
    /// The field's tooltip, from the PDF /TU. This is the accessible name: it is what NVDA and JAWS
    /// announce for a field, and writing one is how an unlabelled field gets fixed.
    /// </summary>
    public string? ToolTip { get; internal set; }

    /// <summary>The field's export mapping name, from the PDF /TM.</summary>
    public string? MappingName { get; init; }

    /// <summary>
    /// A label recovered from the page by looking at the text next to the field. A guess, used only
    /// when the document supplied nothing better, and always reported as a guess.
    /// </summary>
    public string? RecoveredLabel { get; internal set; }

    /// <summary>The order in which pressing Tab reaches this field.</summary>
    public int TabOrder { get; internal set; }

    #endregion

    #region Naming — four sources, in descending order of trust
    // The order is the whole point. A tooltip is the author's own statement of what the field is
    // for and is trusted absolutely. Text found near the field on the page is usually right but
    // sometimes catches the wrong caption. The field's internal name is a programmer's identifier
    // that may or may not mean anything. And when all of those fail, saying so is better than
    // reading out a meaningless name as though it were a label.

    /// <summary>Where <see cref="Label"/> came from, so the user can judge how much to trust it.</summary>
    public enum LabelSource
    {
        /// <summary>From the document's own tooltip. Authoritative.</summary>
        ToolTip = 0,

        /// <summary>Recovered from text near the field on the page. A good guess.</summary>
        NearbyText,

        /// <summary>Derived from the field's internal name. A weak guess.</summary>
        FieldName,

        /// <summary>Nothing usable was found.</summary>
        None,
    }

    /// <summary>Which of the four sources supplied <see cref="Label"/>.</summary>
    public LabelSource ResolvedLabelSource
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ToolTip)) return LabelSource.ToolTip;
            if (!string.IsNullOrWhiteSpace(RecoveredLabel)) return LabelSource.NearbyText;
            if (LooksLikeReadableName(PartialName)) return LabelSource.FieldName;
            return LabelSource.None;
        }
    }

    /// <summary>
    /// The name to announce for this field, resolved from the best available source.
    /// </summary>
    public string Label => ResolvedLabelSource switch
    {
        LabelSource.ToolTip => ToolTip!.Trim(),
        LabelSource.NearbyText => RecoveredLabel!.Trim(),
        LabelSource.FieldName => HumaniseFieldName(PartialName),
        _ => "unlabelled field",
    };

    /// <summary>
    /// Whether a PDF field name is likely to mean something to a person. Names like "Text1" and
    /// "untitled3" are the form designer's defaults and carry no information, so treating them as
    /// labels would be worse than admitting the field has none — it would give the user false
    /// confidence that they know what they are filling in.
    /// </summary>
    protected static bool LooksLikeReadableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 3)
            return false;

        ReadOnlySpan<string> generic = ["text", "field", "untitled", "check", "box", "button", "radio", "form"];
        string trimmed = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '_', '-', ' ');

        foreach (string prefix in generic)
        {
            if (trimmed.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // All digits, or nothing left once the trailing number is removed.
        return trimmed.Length >= 3 && trimmed.Any(char.IsLetter);
    }

    /// <summary>
    /// Turns a programmer's field name into something readable: "applicantFullName" and
    /// "applicant_full_name" both become "applicant full name". Spoken, the original forms are
    /// either run together into one nonsense word or read with the underscores announced.
    /// </summary>
    protected static string HumaniseFieldName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var builder = new System.Text.StringBuilder(name.Length + 8);
        char previous = '\0';

        foreach (char c in name)
        {
            if (c is '_' or '-' or '.')
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                    builder.Append(' ');
            }
            else
            {
                // Insert a break at a lower-to-upper transition, which is where a camelCase word
                // boundary sits.
                if (char.IsUpper(c) && char.IsLower(previous) && builder.Length > 0)
                    builder.Append(' ');

                builder.Append(c);
            }

            previous = c;
        }

        string result = builder.ToString().Trim();
        return result.Length > 0 ? char.ToLowerInvariant(result[0]) + result[1..] : result;
    }

    #endregion

    #region State flags — tracked here so every field reports them the same way
    // Kept as a flags enum on the base class rather than as booleans on each subclass, so that the
    // form-fill UI can ask "how many required fields are still empty" across a mixed set of field
    // types without knowing what any of them are.

    /// <summary>Everything currently true about this field.</summary>
    public FieldStates States { get; protected set; } = FieldStates.None;

    /// <summary>The document marks this field as required.</summary>
    public bool IsRequired => States.HasFlag(FieldStates.Required);

    /// <summary>The document marks this field read-only.</summary>
    public bool IsReadOnly => States.HasFlag(FieldStates.ReadOnly);

    /// <summary>The value was changed in this session and is not yet saved.</summary>
    public bool IsModified => States.HasFlag(FieldStates.Modified);

    /// <summary>The accessible label was changed in this session and is not yet saved.</summary>
    public bool IsLabelModified => States.HasFlag(FieldStates.LabelModified);

    /// <summary>
    /// Sets the field's accessible label — the tooltip a screen reader announces — and records that
    /// it needs writing back.
    ///
    /// This is the repair that turns an unfillable form into a fillable one, so it goes through a
    /// method that maintains the state flag rather than through a bare property setter that a
    /// caller could use without the save path ever finding out.
    /// </summary>
    public void SetAccessibleLabel(string? label)
    {
        ToolTip = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        SetState(FieldStates.LabelModified);
        RefreshDerivedState();
    }

    /// <summary>
    /// Restores a label without marking it as needing to be written. Used by undo, which puts back
    /// a value that is already what the file contains.
    /// </summary>
    public void RestoreAccessibleLabel(string? label, bool stillNeedsWriting)
    {
        ToolTip = label;

        if (stillNeedsWriting)
            SetState(FieldStates.LabelModified);
        else
            ClearState(FieldStates.LabelModified);

        RefreshDerivedState();
    }

    /// <summary>The current value failed validation.</summary>
    public bool IsInvalid => States.HasFlag(FieldStates.Invalid);

    /// <summary>
    /// The field has no label from any source. A blocker-level accessibility fault: the user is
    /// being asked for information without being told what information.
    /// </summary>
    public bool IsUnlabelled => ResolvedLabelSource == LabelSource.None;

    /// <summary>Adds state flags.</summary>
    protected void SetState(FieldStates flags) => States |= flags;

    /// <summary>Removes state flags.</summary>
    protected void ClearState(FieldStates flags) => States &= ~flags;

    /// <summary>
    /// Recomputes the flags that are derived from other facts rather than set by the document.
    /// Called after every value change so that HasValue and Unlabelled never drift out of step
    /// with reality.
    /// </summary>
    protected void RefreshDerivedState()
    {
        if (HasValue) SetState(FieldStates.HasValue);
        else ClearState(FieldStates.HasValue);

        if (IsUnlabelled) SetState(FieldStates.Unlabelled);
        else ClearState(FieldStates.Unlabelled);
    }

    /// <summary>
    /// True when this field still needs attention before the form is complete: required, writable,
    /// and either empty or invalid. This is what "go to the next field that needs filling in"
    /// navigates by, which is the command that makes a long form tractable.
    /// </summary>
    public bool NeedsAttention =>
        !IsReadOnly && IsRequired && (!HasValue || IsInvalid);

    #endregion

    #region Values and validation — the second template method
    // TrySetValue is sealed in effect: subclasses override ApplyValue, never TrySetValue. That
    // guarantees the read-only guard runs, the flags are maintained, and the spoken confirmation
    // has the same shape for every field type in the program.

    /// <summary>Whether the field currently holds a value.</summary>
    public abstract bool HasValue { get; }

    /// <summary>The current value, phrased for speech. "not filled in" when empty, never silence.</summary>
    public abstract string ValueForSpeech { get; }

    /// <summary>
    /// What this field will accept, in words — "enter a date as day slash month slash year".
    /// Spoken when the user enters the field, because being told the format up front is far better
    /// than discovering it by being rejected.
    /// </summary>
    public virtual string InputGuidance => string.Empty;

    /// <summary>
    /// Validates and applies a value. Guards against read-only fields, delegates the type-specific
    /// rule to <see cref="ApplyValue"/>, then updates the shared state flags.
    /// </summary>
    public FieldValidation TrySetValue(string? rawValue)
    {
        if (IsReadOnly)
            return FieldValidation.Reject($"{Label} is read-only and cannot be changed.");

        var result = ApplyValue(rawValue ?? string.Empty);

        if (result.Accepted)
        {
            SetState(FieldStates.Modified);
            ClearState(FieldStates.Invalid);
        }
        else
        {
            SetState(FieldStates.Invalid);
        }

        RefreshDerivedState();
        return result;
    }

    /// <summary>
    /// Applies a value after the base class has confirmed the field is writable. Each subclass
    /// implements its own rule here and nothing else.
    /// </summary>
    protected abstract FieldValidation ApplyValue(string rawValue);

    /// <summary>Empties the field.</summary>
    public virtual FieldValidation Clear() => TrySetValue(string.Empty);

    #endregion

    #region Loading — setting state that came from the file rather than from the user
    // These deliberately bypass TrySetValue. A value read out of the document is not an edit: it
    // must not set the Modified flag, and it must be applied even to a read-only field, because a
    // read-only field with a value is entirely normal and refusing to load it would show the user
    // an empty box where the document has an answer.

    /// <summary>
    /// Applies a value exactly as it was found in the file, without marking the field as edited.
    /// Called only by the loader.
    /// </summary>
    public void ApplyLoadedValue(string? rawValue)
    {
        var result = ApplyValue(rawValue ?? string.Empty);

        // A value already in the document can still be invalid by this field's rules — a date field
        // holding "TBC", say. That is worth flagging to the user, but it is not an edit.
        if (!result.Accepted)
            SetState(FieldStates.Invalid);

        ClearState(FieldStates.Modified);
        RefreshDerivedState();
    }

    /// <summary>
    /// Applies the state flags declared by the document. Called only by the loader.
    /// </summary>
    public void ApplyLoadedStates(FieldStates states)
    {
        SetState(states);
        RefreshDerivedState();
    }

    #endregion

    #region Visitor dispatch

    /// <summary>
    /// Dispatches to the visitor method for this field's concrete type. Implemented by each
    /// subclass as a single call, which is what makes the double dispatch work.
    /// </summary>
    public abstract TResult Accept<TResult>(IFormFieldVisitor<TResult> visitor);

    #endregion

    #region Announcement
    // The order — label, type, value, state — is the order a screen reader uses for a web form
    // control, and matching it means someone who fills in forms in a browser already knows how to
    // listen to this one.

    protected override string DescribeRole(VerbosityLevel verbosity) => FieldKind switch
    {
        FormFieldKind.Text => "edit box",
        FormFieldKind.MultilineText => "multi-line edit box",
        FormFieldKind.CheckBox => "checkbox",
        FormFieldKind.RadioGroup => "radio group",
        FormFieldKind.ComboBox => "drop-down list",
        FormFieldKind.EditableComboBox => "editable drop-down list",
        FormFieldKind.ListBox => "list box",
        FormFieldKind.PushButton => "button",
        FormFieldKind.Signature => "signature field",
        _ => "form field",
    };

    protected override string DescribeContent(VerbosityLevel verbosity)
    {
        string value = ValueForSpeech;
        return value.Length > 0 ? $"{Label}, {value}" : Label;
    }

    protected override string DescribeState(VerbosityLevel verbosity)
    {
        var parts = new List<string>(5);

        if (IsRequired) parts.Add("required");
        if (IsReadOnly) parts.Add("read-only");
        if (IsInvalid) parts.Add("invalid entry");

        // Only said at Detailed verbosity: on a form full of unlabelled fields, repeating the
        // warning on every one would bury the field names that were recovered successfully.
        if (verbosity == VerbosityLevel.Detailed)
        {
            switch (ResolvedLabelSource)
            {
                case LabelSource.None:
                    parts.Add("this field has no label in the document");
                    break;
                case LabelSource.NearbyText:
                    parts.Add("label guessed from the page");
                    break;
                case LabelSource.FieldName:
                    parts.Add("label taken from the field's internal name");
                    break;
            }
        }

        if (IsModified) parts.Add("changed");

        if (verbosity == VerbosityLevel.Detailed && InputGuidance.Length > 0)
            parts.Add(InputGuidance);

        return string.Join(", ", parts);
    }

    protected override string DescribePosition(VerbosityLevel verbosity) =>
        TabOrder > 0 ? $"field {TabOrder}, page {PageNumber}" : base.DescribePosition(verbosity);

    #endregion
}

#endregion
