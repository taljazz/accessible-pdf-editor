using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;

namespace AccessiblePdfEditor.Model.Forms;

// =====================================================================================
//  ToggleFormFields.cs
//
//  Checkboxes and radio button groups.
//
//  PDF stores both of these as the same field type (/Btn), distinguished only by a flag
//  bit, which is why so many form tools announce a radio button as a checkbox and leave the
//  user unable to tell whether choosing one option will clear another. They behave
//  completely differently and are modelled here as separate classes.
//
//  The radio group is deliberately ONE field containing several options, not several
//  fields. That matches how a person thinks about it — "delivery method, three choices" —
//  and it means the group can announce "2 of 3" as the user moves through, which is the
//  information that makes a radio group usable by ear.
// =====================================================================================

#region CheckBoxFormField — an independent on/off toggle

/// <summary>A single checkbox that turns on and off independently of any other field.</summary>
public sealed class CheckBoxFormField : PdfFormField
{
    #region Construction and state

    private bool _isChecked;

    public CheckBoxFormField(int pageNumber, string fullyQualifiedName, bool initiallyChecked = false)
        : base(pageNumber, fullyQualifiedName)
    {
        _isChecked = initiallyChecked;
        RefreshDerivedState();
    }

    public override FormFieldKind FieldKind => FormFieldKind.CheckBox;

    /// <summary>Whether the box is ticked.</summary>
    public bool IsChecked => _isChecked;

    /// <summary>
    /// The PDF name written when the box is ticked — usually /Yes, but a form may use anything.
    /// Preserved rather than assumed, because writing /Yes into a form that expects /On produces a
    /// file that looks correct here and arrives at its destination empty.
    /// </summary>
    public string CheckedStateName { get; init; } = "Yes";

    /// <summary>The PDF name written when the box is clear. Almost always /Off.</summary>
    public string UncheckedStateName { get; init; } = "Off";

    /// <summary>
    /// A checkbox always has a value in the PDF sense — it is either on or off. But for the purpose
    /// of "have you filled this in", only a ticked box counts, because an untouched box and a
    /// deliberately cleared one are indistinguishable in the file.
    /// </summary>
    public override bool HasValue => _isChecked;

    #endregion

    #region Value and validation

    public override string ValueForSpeech => _isChecked ? "checked" : "not checked";

    public override string InputGuidance => "press Space to tick or clear";

    /// <summary>
    /// Accepts the words a person would actually say or type, in either direction. A form filled in
    /// by voice or by a script should not fail because it said "yes" where the PDF wanted "on".
    /// </summary>
    protected override FieldValidation ApplyValue(string rawValue)
    {
        string trimmed = rawValue.Trim();

        bool? parsed = trimmed.ToLowerInvariant() switch
        {
            "" or "off" or "no" or "false" or "0" or "unchecked" or "clear" => false,
            "on" or "yes" or "true" or "1" or "checked" or "tick" or "ticked" or "x" => true,
            _ => null,
        };

        // A form's own state name — which may be neither "yes" nor "on" — always means checked.
        parsed ??= trimmed.Equals(CheckedStateName, StringComparison.OrdinalIgnoreCase)
            ? true
            : trimmed.Equals(UncheckedStateName, StringComparison.OrdinalIgnoreCase)
                ? false
                : null;

        if (parsed is not { } value)
            return FieldValidation.Reject($"{Label} is a checkbox. Say checked or not checked.");

        _isChecked = value;
        return FieldValidation.Accept($"{Label} {ValueForSpeech}.", value ? CheckedStateName : UncheckedStateName);
    }

    /// <summary>Flips the box. The operation the Space key performs.</summary>
    public FieldValidation Toggle() => TrySetValue(_isChecked ? "off" : "on");

    #endregion

    #region Activation and dispatch

    public override bool CanActivate => !IsReadOnly;

    protected override string UnavailableReason => $"{Label} is read-only.";

    public override string ActivationHint => "press Space to tick or clear";

    protected override InteractionResult ActivateCore(IInteractionHost host)
    {
        var result = Toggle();
        return result.Accepted
            ? InteractionResult.ValueChanged(result.Message)
            : InteractionResult.Failed(result.Message);
    }

    public override TResult Accept<TResult>(IFormFieldVisitor<TResult> visitor) => visitor.VisitCheckBox(this);

    #endregion
}

#endregion

#region RadioOption — one choice within a group
// Not a field in its own right. It is a value the group can hold, which is why it does not derive
// from PdfFormField: making each button a field would let the user tab into an option and choose
// it without ever hearing that the other options existed.

/// <summary>One selectable option within a <see cref="RadioGroupFormField"/>.</summary>
public sealed class RadioOption
{
    public RadioOption(string exportValue, string? label = null, int pageNumber = 0)
    {
        ExportValue = exportValue ?? string.Empty;
        Label = label;
        PageNumber = pageNumber;
    }

    /// <summary>The PDF name written when this option is chosen — the widget's /AS on-state.</summary>
    public string ExportValue { get; }

    /// <summary>
    /// The option's readable label, when one could be recovered from the page. PDF stores only the
    /// export value, which is frequently a code such as "Opt2", so the label usually has to be read
    /// off the page beside the button.
    /// </summary>
    public string? Label { get; internal set; }

    /// <summary>The page this option's button sits on.</summary>
    public int PageNumber { get; }

    /// <summary>Where this option sits on its page.</summary>
    public PageRegion Bounds { get; internal set; } = PageRegion.Empty;

    /// <summary>
    /// What to say for this option. Falls back to the export value when no label was recovered —
    /// a code read aloud is poor, but it is still better than an unnamed choice.
    /// </summary>
    public string SpokenLabel =>
        !string.IsNullOrWhiteSpace(Label) ? Label!.Trim() : ExportValue;

    public override string ToString() => SpokenLabel;
}

#endregion

#region RadioGroupFormField — one field, several mutually exclusive options

/// <summary>
/// A group of mutually exclusive radio buttons, modelled as a single field holding one of several
/// options.
/// </summary>
public sealed class RadioGroupFormField : PdfFormField
{
    #region Construction and state

    private readonly List<RadioOption> _options = [];
    private string? _selectedExportValue;

    public RadioGroupFormField(int pageNumber, string fullyQualifiedName)
        : base(pageNumber, fullyQualifiedName)
    {
        RefreshDerivedState();
    }

    public override FormFieldKind FieldKind => FormFieldKind.RadioGroup;

    /// <summary>The options in this group, in the order the user meets them.</summary>
    public IReadOnlyList<RadioOption> Options => _options;

    /// <summary>Adds an option. Called by the loader as it discovers each button widget.</summary>
    public void AddOption(RadioOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        _options.Add(option);
    }

    /// <summary>The export value of the chosen option, or null when nothing is chosen.</summary>
    public string? SelectedExportValue => _selectedExportValue;

    /// <summary>The chosen option, or null.</summary>
    public RadioOption? SelectedOption =>
        _selectedExportValue is null
            ? null
            : _options.FirstOrDefault(o => o.ExportValue.Equals(_selectedExportValue, StringComparison.Ordinal));

    /// <summary>The one-based position of the chosen option, or zero when nothing is chosen.</summary>
    public int SelectedIndex
    {
        get
        {
            var selected = SelectedOption;
            return selected is null ? 0 : _options.IndexOf(selected) + 1;
        }
    }

    public override bool HasValue => _selectedExportValue is not null;

    /// <summary>
    /// Sets the selection directly by position. Used by the editor control, where the user has
    /// already moved to an option and the export value is an implementation detail they never see.
    /// </summary>
    public FieldValidation SelectByIndex(int oneBasedIndex)
    {
        if (oneBasedIndex < 1 || oneBasedIndex > _options.Count)
            return FieldValidation.Reject($"{Label} has {_options.Count} options.");

        return TrySetValue(_options[oneBasedIndex - 1].ExportValue);
    }

    #endregion

    #region Value and validation

    public override string ValueForSpeech
    {
        get
        {
            if (SelectedOption is not { } selected)
                return $"nothing selected, {_options.Count} options";

            return $"{selected.SpokenLabel}, {SelectedIndex} of {_options.Count}";
        }
    }

    public override string InputGuidance => "use the arrow keys to move between options";

    /// <summary>
    /// Matches on the export value first, then on the readable label. Matching the label matters
    /// because everything the user hears is the label — being unable to set a value using the only
    /// words the program ever said would be indefensible.
    /// </summary>
    protected override FieldValidation ApplyValue(string rawValue)
    {
        string trimmed = rawValue.Trim();

        if (trimmed.Length == 0 || trimmed.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            _selectedExportValue = null;
            return FieldValidation.Accept($"{Label} cleared.", "Off");
        }

        var match = _options.FirstOrDefault(o => o.ExportValue.Equals(trimmed, StringComparison.Ordinal))
            ?? _options.FirstOrDefault(o => o.ExportValue.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            ?? _options.FirstOrDefault(o => o.SpokenLabel.Equals(trimmed, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            string available = string.Join(", ", _options.Select(o => o.SpokenLabel));
            return FieldValidation.Reject($"{trimmed} is not an option for {Label}. Choose from: {available}.");
        }

        _selectedExportValue = match.ExportValue;
        return FieldValidation.Accept($"{Label} set to {match.SpokenLabel}.", match.ExportValue);
    }

    #endregion

    #region Activation and dispatch

    public override bool CanActivate => !IsReadOnly && _options.Count > 0;

    protected override string UnavailableReason =>
        _options.Count == 0 ? $"{Label} has no options." : $"{Label} is read-only.";

    public override string ActivationHint => "use the arrow keys to choose an option";

    /// <summary>
    /// Activating the group moves to the next option and selects it, wrapping at the end. This
    /// matches how a radio group behaves everywhere else on Windows, where the arrow keys both move
    /// and choose — there is no separate "confirm" step, because an unselected radio group is not
    /// a state the control can rest in once the user has started choosing.
    /// </summary>
    protected override InteractionResult ActivateCore(IInteractionHost host)
    {
        int next = SelectedIndex >= _options.Count ? 1 : SelectedIndex + 1;
        var result = SelectByIndex(next);

        return result.Accepted
            ? InteractionResult.ValueChanged(result.Message)
            : InteractionResult.Failed(result.Message);
    }

    public override TResult Accept<TResult>(IFormFieldVisitor<TResult> visitor) => visitor.VisitRadioGroup(this);

    #endregion

    #region Announcement

    /// <summary>
    /// A radio group whose options have no recovered labels is called out. The user would otherwise
    /// be choosing between "Opt1" and "Opt2" with no way to know what either means, and that is
    /// worth saying rather than leaving them to work out.
    /// </summary>
    protected override string DescribeState(VerbosityLevel verbosity)
    {
        string baseState = base.DescribeState(verbosity);

        if (verbosity == VerbosityLevel.Terse || _options.Count == 0)
            return baseState;

        bool anyLabelled = _options.Any(o => !string.IsNullOrWhiteSpace(o.Label));
        if (anyLabelled)
            return baseState;

        const string warning = "options are unlabelled in the document";
        return baseState.Length > 0 ? $"{baseState}, {warning}" : warning;
    }

    #endregion
}

#endregion
