using System.Globalization;
using System.Text.RegularExpressions;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;

namespace AccessiblePdfEditor.Model.Forms;

// =====================================================================================
//  TextFormField.cs
//
//  Text entry fields, single-line and multi-line.
//
//  This is the field type where validation earns its keep. A PDF expresses input rules as
//  JavaScript format actions, which this editor will not execute — running scripts out of a
//  document handed to you is not something to do casually, and most readers cannot run them
//  anyway. The result is that a blind user typing a date into a form gets no feedback at
//  all: the value is accepted silently and rejected weeks later by whoever receives it.
//
//  So the format is inferred from the field's own name and tooltip, and enforced here. The
//  guidance is spoken on entry ("enter a date, day slash month slash year"), and a rejection
//  says what would be acceptable rather than merely that something was wrong.
// =====================================================================================

#region TextFormField

/// <summary>A text entry field. Handles single-line, multi-line and comb fields.</summary>
public sealed partial class TextFormField : PdfFormField
{
    #region Construction and state

    private string _value;

    public TextFormField(int pageNumber, string fullyQualifiedName, string? initialValue = null)
        : base(pageNumber, fullyQualifiedName)
    {
        _value = initialValue ?? string.Empty;
        RefreshDerivedState();
    }

    public override FormFieldKind FieldKind =>
        IsMultiline ? FormFieldKind.MultilineText : FormFieldKind.Text;

    /// <summary>Whether this field accepts line breaks (PDF /Ff Multiline flag).</summary>
    public bool IsMultiline { get; init; }

    /// <summary>The maximum number of characters, from the PDF /MaxLen. Null when unlimited.</summary>
    public int? MaxLength { get; init; }

    /// <summary>
    /// The format this field expects. Set by the loader, which infers it from the field's name and
    /// tooltip; may be corrected by the user, which is why it is not init-only.
    /// </summary>
    public TextFieldFormat Format { get; internal set; } = TextFieldFormat.PlainText;

    /// <summary>Whether the field's contents are masked when displayed.</summary>
    public bool IsPassword => States.HasFlag(FieldStates.Password);

    /// <summary>The current text.</summary>
    public string Value => _value;

    public override bool HasValue => _value.Length > 0;

    #endregion

    #region Value in speech
    // A password field never has its contents read aloud, but it must not be silent either: the
    // user needs to know whether anything is in there. The character count answers that without
    // exposing the value to anyone listening over their shoulder or over a speakerphone.

    public override string ValueForSpeech
    {
        get
        {
            if (!HasValue)
                return "blank";

            if (IsPassword)
                return $"{_value.Length} characters entered";

            if (!IsMultiline)
                return _value;

            // A long multi-line answer read out in full on every arrival makes moving through a
            // form unbearable. The first line plus a count orients the user; they can read the
            // whole thing deliberately once they know it is worth doing.
            int newline = _value.IndexOf('\n');
            if (newline < 0)
                return _value;

            int lines = _value.AsSpan().Count('\n') + 1;
            return $"{_value[..newline].TrimEnd()}, {lines} lines in total";
        }
    }

    #endregion

    #region Guidance — spoken on entry, so the format is known before it is needed

    public override string InputGuidance => Format switch
    {
        TextFieldFormat.Number => "enter a number",
        TextFieldFormat.Currency => "enter an amount",
        TextFieldFormat.DigitsOnly => MaxLength is { } length
            ? $"enter {length} digits"
            : "enter digits only",
        TextFieldFormat.Date => "enter a date, for example 31/03/2026",
        TextFieldFormat.Time => "enter a time, for example 14:30",
        TextFieldFormat.Email => "enter an email address",
        TextFieldFormat.Telephone => "enter a telephone number",
        TextFieldFormat.PostalCode => "enter a postcode",
        TextFieldFormat.Comb => MaxLength is { } combLength
            ? $"enter exactly {combLength} characters, one per box"
            : "enter one character per box",
        _ => IsMultiline ? "multi-line, press Alt+Enter for a new line" : string.Empty,
    };

    #endregion

    #region Validation — one rule per format
    // Each branch returns a message that says what WOULD work. "Invalid" on its own tells someone
    // they are stuck without telling them how to get unstuck, which for a listener is worse than
    // useless because they cannot glance at an example elsewhere on the page.

    protected override FieldValidation ApplyValue(string rawValue)
    {
        string candidate = IsMultiline ? rawValue.Replace("\r\n", "\n") : rawValue.Replace("\n", " ").Trim();

        // Clearing a field is always allowed. Whether it is then acceptable to LEAVE it empty is a
        // separate question, answered by the required-fields check when the form is reviewed.
        if (candidate.Length == 0)
        {
            _value = string.Empty;
            return FieldValidation.Accept($"{Label} cleared.");
        }

        if (Format == TextFieldFormat.Comb && MaxLength is { } combLength && candidate.Length != combLength)
        {
            return FieldValidation.Reject(
                $"{Label} needs exactly {combLength} characters. You entered {candidate.Length}.");
        }

        if (MaxLength is { } max && candidate.Length > max)
        {
            return FieldValidation.Reject(
                $"{Label} holds at most {max} characters. You entered {candidate.Length}.");
        }

        var check = ValidateFormat(candidate);
        if (!check.Accepted)
            return check;

        _value = check.NormalisedValue ?? candidate;

        // Confirming with the stored value rather than the typed one lets the user hear that their
        // input was tidied — "31/3/2026" heard back as "31/03/2026" confirms it was understood.
        return FieldValidation.Accept($"{Label} set to {_value}.", _value);
    }

    /// <summary>
    /// Applies this field's format rule. Split out from <see cref="ApplyValue"/> so the length and
    /// emptiness checks that apply to every format are not repeated in each branch.
    /// </summary>
    private FieldValidation ValidateFormat(string candidate) => Format switch
    {
        TextFieldFormat.Number => ValidateNumber(candidate),
        TextFieldFormat.Currency => ValidateCurrency(candidate),
        TextFieldFormat.DigitsOnly => candidate.All(char.IsDigit)
            ? FieldValidation.Accept(string.Empty, candidate)
            : FieldValidation.Reject($"{Label} accepts digits only."),
        TextFieldFormat.Date => ValidateDate(candidate),
        TextFieldFormat.Time => ValidateTime(candidate),
        TextFieldFormat.Email => ValidateEmail(candidate),
        TextFieldFormat.Telephone => ValidateTelephone(candidate),
        _ => FieldValidation.Accept(string.Empty, candidate),
    };

    private FieldValidation ValidateNumber(string candidate) =>
        double.TryParse(candidate, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out _)
        || double.TryParse(candidate, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _)
            ? FieldValidation.Accept(string.Empty, candidate)
            : FieldValidation.Reject($"{Label} needs a number. You entered {candidate}.");

    private FieldValidation ValidateCurrency(string candidate)
    {
        // Currency symbols and thousands separators are stripped before parsing, because a person
        // filling in an amount field types what looks right to them, and rejecting "£1,200.50" for
        // punctuation would be pedantry rather than validation.
        string stripped = new(candidate.Where(c => char.IsDigit(c) || c is '.' or ',' or '-' or '+').ToArray());

        return decimal.TryParse(stripped, NumberStyles.Currency, CultureInfo.CurrentCulture, out _)
            || decimal.TryParse(stripped, NumberStyles.Currency, CultureInfo.InvariantCulture, out _)
                ? FieldValidation.Accept(string.Empty, candidate)
                : FieldValidation.Reject($"{Label} needs an amount. You entered {candidate}.");
    }

    private FieldValidation ValidateDate(string candidate)
    {
        string[] formats =
        [
            "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy", "yyyy-MM-dd",
            "d.M.yyyy", "dd.MM.yyyy", "d MMM yyyy", "d MMMM yyyy", "MM/dd/yyyy",
        ];

        if (DateTime.TryParseExact(candidate, formats, CultureInfo.CurrentCulture,
                DateTimeStyles.None, out var parsed)
            || DateTime.TryParse(candidate, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsed))
        {
            // Normalised to an unambiguous form and read back, so a user who typed "3/4/2026"
            // hears which of March and April it was understood as.
            return FieldValidation.Accept(string.Empty, parsed.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
        }

        return FieldValidation.Reject(
            $"{Label} needs a date, for example 31/03/2026. You entered {candidate}.");
    }

    private FieldValidation ValidateTime(string candidate) =>
        DateTime.TryParse(candidate, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out var parsed)
            ? FieldValidation.Accept(string.Empty, parsed.ToString("HH:mm", CultureInfo.InvariantCulture))
            : FieldValidation.Reject($"{Label} needs a time, for example 14:30. You entered {candidate}.");

    private FieldValidation ValidateEmail(string candidate)
    {
        // Deliberately permissive. The full grammar for an address permits things no validator
        // should reject on a user's behalf; the only real errors worth catching are a missing at
        // sign or an obviously truncated domain.
        bool looksRight = EmailShape().IsMatch(candidate);

        return looksRight
            ? FieldValidation.Accept(string.Empty, candidate.Trim())
            : FieldValidation.Reject(
                $"{Label} needs an email address, such as name at example dot com. You entered {candidate}.");
    }

    private FieldValidation ValidateTelephone(string candidate)
    {
        int digits = candidate.Count(char.IsDigit);
        bool onlyAllowed = candidate.All(c => char.IsDigit(c) || c is '+' or '-' or ' ' or '(' or ')' or '.');

        return digits >= 5 && onlyAllowed
            ? FieldValidation.Accept(string.Empty, candidate.Trim())
            : FieldValidation.Reject($"{Label} needs a telephone number. You entered {candidate}.");
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$")]
    private static partial Regex EmailShape();

    #endregion

    #region Format inference — recovering the rule the document did not state
    // Reads the field's tooltip and name for words that reveal what it expects. Not clever, and it
    // does not need to be: form fields are named by people, and people call a date field
    // something with "date" in it. Getting this right for the common cases means most fields in
    // most forms announce their format, and the ones that do not simply behave as plain text.

    /// <summary>
    /// Infers the expected format from a field's label and name. Returns
    /// <see cref="TextFieldFormat.PlainText"/> when nothing suggests otherwise, which is always a
    /// safe answer because it validates nothing.
    /// </summary>
    public static TextFieldFormat InferFormat(string? toolTip, string fieldName, bool isComb)
    {
        if (isComb)
            return TextFieldFormat.Comb;

        string haystack = $"{toolTip} {fieldName}".ToLowerInvariant();

        if (ContainsAny(haystack, "e-mail", "email", "mail address")) return TextFieldFormat.Email;
        if (ContainsAny(haystack, "date", "dob", "birth", "expiry", "expires")) return TextFieldFormat.Date;
        if (ContainsAny(haystack, "time", "hour")) return TextFieldFormat.Time;
        if (ContainsAny(haystack, "phone", "telephone", "mobile", "fax")) return TextFieldFormat.Telephone;
        if (ContainsAny(haystack, "postcode", "post code", "zip")) return TextFieldFormat.PostalCode;
        if (ContainsAny(haystack, "amount", "total", "price", "cost", "salary", "fee", "£", "$", "€"))
            return TextFieldFormat.Currency;
        if (ContainsAny(haystack, "quantity", "number of", "count", "age", "year")) return TextFieldFormat.Number;
        if (ContainsAny(haystack, "account number", "reference number", "national insurance", "ssn"))
            return TextFieldFormat.DigitsOnly;

        return TextFieldFormat.PlainText;
    }

    private static bool ContainsAny(string haystack, params ReadOnlySpan<string> needles)
    {
        foreach (string needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    #endregion

    #region Activation and dispatch

    public override bool CanActivate => !IsReadOnly;

    protected override string UnavailableReason => $"{Label} is read-only.";

    public override string ActivationHint => IsMultiline
        ? "press Enter to edit, Alt+Enter for a new line within the field"
        : "press Enter to edit";

    /// <summary>
    /// Activating a text field opens it for editing. The actual typing is handled by the editor
    /// control the visitor builds; this only announces the transition and the format expected.
    /// </summary>
    protected override InteractionResult ActivateCore(IInteractionHost host)
    {
        string guidance = InputGuidance.Length > 0 ? $" {InputGuidance}." : string.Empty;
        host.Announce($"Editing {Label}.{guidance} Current value: {ValueForSpeech}.",
            AnnouncementPriority.Assertive);

        return InteractionResult.Succeeded($"Editing {Label}.");
    }

    public override TResult Accept<TResult>(IFormFieldVisitor<TResult> visitor) => visitor.VisitText(this);

    #endregion
}

#endregion
