using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Model.Forms;

namespace AccessiblePdfEditor.Editing;

// =====================================================================================
//  FormCommands.cs
//
//  Commands that act on a form as a whole rather than on one field.
//
//  Clearing a form is the most destructive thing a user can do by accident in this
//  application. A misplaced Enter on a reset button, after twenty minutes of filling in a
//  benefits claim, throws all of it away — and a blind user cannot glance at the page to
//  see that it happened. So this command is built around being reversible and being loud
//  about what it did: it captures every value before touching anything, restores them
//  exactly on undo, and reports the count both ways.
// =====================================================================================

#region ResetFormCommand

/// <summary>
/// Clears every writable field in a form, and can put them all back.
/// </summary>
public sealed class ResetFormCommand : EditCommand
{
    #region Construction
    // Every value is captured up front, because after Apply runs the fields no longer know what
    // they held and nothing else in the program remembers.

    private readonly List<CapturedField> _captured = [];

    public ResetFormCommand(PdfDocumentModel document)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var field in document.FormFields)
        {
            // Read-only fields are not the user's to clear, and buttons and signatures hold no
            // value to begin with. Touching them would be a change the user did not ask for.
            if (field.IsReadOnly || field.FieldKind is FormFieldKind.PushButton or FormFieldKind.Signature)
                continue;

            if (!field.HasValue)
                continue;

            _captured.Add(new CapturedField(field, CaptureValue(field), field.IsModified));
        }
    }

    private sealed record CapturedField(PdfFormField Field, string Value, bool WasModified);

    /// <summary>How many fields hold a value and would be cleared.</summary>
    public int FieldsWithValues => _captured.Count;

    #endregion

    #region Identity

    public override EditKind Kind => EditKind.FormFieldValue;

    /// <summary>
    /// Clearing writes ordinary field values, so it is as safe to persist as filling one in. The
    /// danger is to the user's work, not to the file, and that is handled by confirmation and undo
    /// rather than by treating the write as risky.
    /// </summary>
    public override EditConfidence Confidence => EditConfidence.Safe;

    public override DocumentElement? AffectedElement => _captured.Count > 0 ? _captured[0].Field : null;

    public override string Description => _captured.Count == 1
        ? "cleared 1 form field"
        : $"cleared all {_captured.Count} filled-in form fields";

    #endregion

    #region Applying and reverting

    protected override EditResult ApplyCore(PdfDocumentModel document)
    {
        if (_captured.Count == 0)
            return EditResult.Failed("There is nothing to clear. No fields have been filled in.");

        int cleared = 0;

        foreach (var captured in _captured)
        {
            if (captured.Field.Clear().Accepted)
                cleared++;
        }

        return EditResult.Ok(
            $"Form cleared. {cleared} {(cleared == 1 ? "field" : "fields")} emptied. " +
            "Press Control plus Z to put everything back.");
    }

    protected override EditResult RevertCore(PdfDocumentModel document)
    {
        foreach (var captured in _captured)
        {
            // Restored as a loaded value first so the field ends up holding exactly what it did
            // before, then re-marked as edited only if it already was. Otherwise undoing a reset
            // would quietly drop the user's earlier edits from the next save.
            captured.Field.ApplyLoadedValue(captured.Value);

            if (captured.WasModified)
                captured.Field.TrySetValue(captured.Value);
        }

        return EditResult.Ok(
            $"Form restored. {_captured.Count} {(_captured.Count == 1 ? "field" : "fields")} put back.");
    }

    #endregion

    #region Capturing values

    /// <summary>
    /// Reads a field's value in the form that can be given straight back to it. Each field type
    /// stores its value differently, so each is asked for the representation it accepts.
    /// </summary>
    private static string CaptureValue(PdfFormField field) => field switch
    {
        TextFormField text => text.Value,
        CheckBoxFormField box => box.IsChecked ? box.CheckedStateName : box.UncheckedStateName,
        RadioGroupFormField radio => radio.SelectedExportValue ?? string.Empty,
        ChoiceFormField choice => choice.CustomText ?? string.Join(";", choice.SelectedExportValues),
        _ => string.Empty,
    };

    #endregion
}

#endregion

#region ApplySignatureCommand

/// <summary>
/// Places a signature on a signature field, to be written when the document is saved.
///
/// Undoable, deliberately. A signature is the one edit a user is least able to check for
/// themselves — they cannot look at the page and see whether it landed where they meant, or
/// whether the image came out upside down — so being able to take it back before saving matters
/// more here than anywhere else in the program.
/// </summary>
public sealed class ApplySignatureCommand : EditCommand
{
    private readonly SignatureFormField _field;
    private readonly SignatureMark _mark;
    private readonly SignatureMark? _previousMark;

    public ApplySignatureCommand(SignatureFormField field, SignatureMark mark)
    {
        _field = field ?? throw new ArgumentNullException(nameof(field));
        _mark = mark ?? throw new ArgumentNullException(nameof(mark));
        _previousMark = field.PendingMark;
    }

    public override EditKind Kind => EditKind.FormFieldValue;

    /// <summary>
    /// Adds an appearance to an existing field without disturbing anything else in the file.
    /// </summary>
    public override EditConfidence Confidence => EditConfidence.Additive;

    public override DocumentElement AffectedElement => _field;

    public override string Description =>
        $"signed {_field.Label} on page {_field.PageNumber} with {_mark.Describe()}";

    protected override EditResult ApplyCore(PdfDocumentModel document)
    {
        if (!_mark.IsUsable)
            return EditResult.Failed("That signature has nothing to draw.");

        _field.PlaceSignature(_mark);

        return EditResult.Ok(
            $"{_field.Label} signed with {_mark.Describe()}. " +
            "This is a visible signature, not a cryptographic one.");
    }

    protected override EditResult RevertCore(PdfDocumentModel document)
    {
        _field.PlaceSignature(_previousMark);

        return EditResult.Ok(_previousMark is null
            ? $"Signature removed from {_field.Label}. The field is empty again."
            : $"{_field.Label} put back to its previous signature.");
    }
}

#endregion
