using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;

namespace AccessiblePdfEditor.Model.Forms;

// =====================================================================================
//  ChoiceFormField.cs
//
//  Drop-down lists and list boxes — the PDF /Ch field type.
//
//  One class covers combo boxes, editable combo boxes and list boxes because in PDF they
//  genuinely are one field type varying by three flag bits (Combo, Edit, MultiSelect). The
//  differences show up in what the field ACCEPTS, which is exactly the sort of variation a
//  single class with clear state handles well, and splitting them would duplicate the
//  option-matching logic three times over.
//
//  The listener's problem with a choice field is that the options are invisible until
//  opened, and a long list is punishing to step through. So the field announces its size
//  on arrival, and matching accepts a unique prefix — typing "sco" chooses "Scotland"
//  without arrowing past forty entries.
// =====================================================================================

#region ChoiceOption — one entry in the list

/// <summary>One option in a choice field.</summary>
public sealed class ChoiceOption
{
    public ChoiceOption(string exportValue, string? displayText = null)
    {
        ExportValue = exportValue ?? string.Empty;
        DisplayText = displayText;
    }

    /// <summary>The value stored in the file when this option is chosen.</summary>
    public string ExportValue { get; }

    /// <summary>
    /// The text shown to the user, when the document provides one distinct from the export value.
    /// PDF allows an option to be a pair of [export, display], and where it is, the display text is
    /// the only part meant for a person.
    /// </summary>
    public string? DisplayText { get; }

    /// <summary>What to say for this option.</summary>
    public string SpokenText =>
        !string.IsNullOrWhiteSpace(DisplayText) ? DisplayText!.Trim() : ExportValue;

    public override string ToString() => SpokenText;
}

#endregion

#region ChoiceFormField

/// <summary>A drop-down list or list box, with single or multiple selection.</summary>
public sealed class ChoiceFormField : PdfFormField
{
    #region Construction and state

    private readonly List<ChoiceOption> _options = [];
    private readonly List<string> _selectedExportValues = [];
    private string? _typedValue;

    public ChoiceFormField(int pageNumber, string fullyQualifiedName, bool isComboBox)
        : base(pageNumber, fullyQualifiedName)
    {
        IsComboBox = isComboBox;
        RefreshDerivedState();
    }

    /// <summary>True for a drop-down, false for a list box (PDF /Ff Combo flag).</summary>
    public bool IsComboBox { get; }

    /// <summary>Whether the user may type a value that is not in the list (PDF /Ff Edit flag).</summary>
    public bool AllowsCustomText { get; init; }

    /// <summary>Whether more than one option may be chosen (PDF /Ff MultiSelect flag).</summary>
    public bool AllowsMultipleSelection { get; init; }

    public override FormFieldKind FieldKind => IsComboBox
        ? (AllowsCustomText ? FormFieldKind.EditableComboBox : FormFieldKind.ComboBox)
        : FormFieldKind.ListBox;

    /// <summary>The available options, in document order.</summary>
    public IReadOnlyList<ChoiceOption> Options => _options;

    /// <summary>Adds an option. Called by the loader.</summary>
    public void AddOption(ChoiceOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        _options.Add(option);
    }

    /// <summary>The export values currently chosen.</summary>
    public IReadOnlyList<string> SelectedExportValues => _selectedExportValues;

    /// <summary>The chosen options.</summary>
    public IReadOnlyList<ChoiceOption> SelectedOptions =>
        _selectedExportValues
            .Select(v => _options.FirstOrDefault(o => o.ExportValue.Equals(v, StringComparison.Ordinal)))
            .OfType<ChoiceOption>()
            .ToList();

    /// <summary>
    /// Text typed into an editable drop-down that matches no option. Kept separately from the
    /// selection so that a custom entry is never mistaken for a listed one when the form is saved.
    /// </summary>
    public string? CustomText => _typedValue;

    public override bool HasValue => _selectedExportValues.Count > 0 || !string.IsNullOrEmpty(_typedValue);

    #endregion

    #region Value and validation

    public override string ValueForSpeech
    {
        get
        {
            if (!string.IsNullOrEmpty(_typedValue))
                return $"{_typedValue}, typed in";

            var selected = SelectedOptions;

            if (selected.Count == 0)
                return $"nothing selected, {_options.Count} options";

            if (selected.Count == 1)
            {
                int position = _options.IndexOf(selected[0]) + 1;
                return $"{selected[0].SpokenText}, {position} of {_options.Count}";
            }

            return $"{selected.Count} of {_options.Count} selected: {string.Join(", ", selected.Select(o => o.SpokenText))}";
        }
    }

    public override string InputGuidance
    {
        get
        {
            if (AllowsMultipleSelection)
                return "use the arrow keys to move, Space to select or deselect, more than one may be chosen";

            return AllowsCustomText
                ? "use the arrow keys to choose, or type your own value"
                : "use the arrow keys to choose, or type the first few letters";
        }
    }

    /// <summary>
    /// Resolves a value against the option list, then falls back to a typed value where the field
    /// permits one. Multiple selections arrive as a semicolon-separated list.
    /// </summary>
    protected override FieldValidation ApplyValue(string rawValue)
    {
        string trimmed = rawValue.Trim();

        if (trimmed.Length == 0)
        {
            _selectedExportValues.Clear();
            _typedValue = null;
            return FieldValidation.Accept($"{Label} cleared.");
        }

        if (AllowsMultipleSelection && trimmed.Contains(';'))
            return ApplyMultipleValues(trimmed);

        var match = MatchOption(trimmed);

        if (match is not null)
        {
            _selectedExportValues.Clear();
            _selectedExportValues.Add(match.ExportValue);
            _typedValue = null;

            int position = _options.IndexOf(match) + 1;
            return FieldValidation.Accept(
                $"{Label} set to {match.SpokenText}, {position} of {_options.Count}.", match.ExportValue);
        }

        if (AllowsCustomText)
        {
            _selectedExportValues.Clear();
            _typedValue = trimmed;
            return FieldValidation.Accept($"{Label} set to {trimmed}, which is not one of the listed options.", trimmed);
        }

        // The rejection lists what IS available, up to a point. Reading forty options aloud after a
        // mistyped entry would be a punishment; six gives the user something to work with and the
        // count tells them there is more.
        return FieldValidation.Reject(BuildNoMatchMessage(trimmed));
    }

    private FieldValidation ApplyMultipleValues(string rawValue)
    {
        var requested = rawValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matched = new List<ChoiceOption>(requested.Length);

        foreach (string candidate in requested)
        {
            var match = MatchOption(candidate);
            if (match is null)
                return FieldValidation.Reject(BuildNoMatchMessage(candidate));

            if (!matched.Contains(match))
                matched.Add(match);
        }

        _selectedExportValues.Clear();
        _selectedExportValues.AddRange(matched.Select(o => o.ExportValue));
        _typedValue = null;

        return FieldValidation.Accept(
            $"{Label} set to {matched.Count} options: {string.Join(", ", matched.Select(o => o.SpokenText))}.",
            string.Join(";", _selectedExportValues));
    }

    /// <summary>
    /// Finds the option a piece of text refers to. Tries exact matches first, then a unique
    /// case-insensitive prefix — which is how someone actually uses a long list, typing enough
    /// letters to be unambiguous rather than arrowing through every entry. An ambiguous prefix
    /// matches nothing, so "ne" never silently picks Netherlands over New Zealand.
    /// </summary>
    private ChoiceOption? MatchOption(string text)
    {
        var exact = _options.FirstOrDefault(o => o.ExportValue.Equals(text, StringComparison.Ordinal))
            ?? _options.FirstOrDefault(o => o.SpokenText.Equals(text, StringComparison.Ordinal));
        if (exact is not null)
            return exact;

        var caseInsensitive = _options.FirstOrDefault(o => o.ExportValue.Equals(text, StringComparison.OrdinalIgnoreCase))
            ?? _options.FirstOrDefault(o => o.SpokenText.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (caseInsensitive is not null)
            return caseInsensitive;

        var prefixMatches = _options
            .Where(o => o.SpokenText.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return prefixMatches.Count == 1 ? prefixMatches[0] : null;
    }

    private string BuildNoMatchMessage(string attempted)
    {
        const int maxListed = 6;

        var sample = _options.Take(maxListed).Select(o => o.SpokenText);
        string listed = string.Join(", ", sample);

        return _options.Count > maxListed
            ? $"{attempted} is not an option for {Label}. The first {maxListed} of {_options.Count} are: {listed}."
            : $"{attempted} is not an option for {Label}. Choose from: {listed}.";
    }

    /// <summary>Selects an option by its one-based position.</summary>
    public FieldValidation SelectByIndex(int oneBasedIndex)
    {
        if (oneBasedIndex < 1 || oneBasedIndex > _options.Count)
            return FieldValidation.Reject($"{Label} has {_options.Count} options.");

        return TrySetValue(_options[oneBasedIndex - 1].ExportValue);
    }

    /// <summary>
    /// Adds or removes one option from the selection, for multi-select list boxes. Single-select
    /// fields fall back to plain selection, so the Space key does something sensible everywhere.
    /// </summary>
    public FieldValidation ToggleOption(int oneBasedIndex)
    {
        if (oneBasedIndex < 1 || oneBasedIndex > _options.Count)
            return FieldValidation.Reject($"{Label} has {_options.Count} options.");

        if (!AllowsMultipleSelection)
            return SelectByIndex(oneBasedIndex);

        if (IsReadOnly)
            return FieldValidation.Reject($"{Label} is read-only and cannot be changed.");

        var option = _options[oneBasedIndex - 1];
        bool wasSelected = _selectedExportValues.Remove(option.ExportValue);

        if (!wasSelected)
            _selectedExportValues.Add(option.ExportValue);

        SetState(FieldStates.Modified);
        ClearState(FieldStates.Invalid);
        RefreshDerivedState();

        string what = wasSelected ? "deselected" : "selected";
        return FieldValidation.Accept(
            $"{option.SpokenText} {what}. {_selectedExportValues.Count} of {_options.Count} selected.");
    }

    #endregion

    #region Activation and dispatch

    public override bool CanActivate => !IsReadOnly && (_options.Count > 0 || AllowsCustomText);

    protected override string UnavailableReason =>
        _options.Count == 0 ? $"{Label} has no options." : $"{Label} is read-only.";

    public override string ActivationHint => IsComboBox
        ? "press Enter to open the list"
        : "use the arrow keys to move through the list";

    protected override InteractionResult ActivateCore(IInteractionHost host)
    {
        host.Announce($"{Label}. {ValueForSpeech}. {InputGuidance}.", AnnouncementPriority.Assertive);
        return InteractionResult.Succeeded($"Choosing a value for {Label}.");
    }

    public override TResult Accept<TResult>(IFormFieldVisitor<TResult> visitor) => visitor.VisitChoice(this);

    #endregion
}

#endregion
