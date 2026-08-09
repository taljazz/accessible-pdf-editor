using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;

namespace AccessiblePdfEditor.Editing;

// =====================================================================================
//  EditCommand.cs
//
//  The abstract base for every change the user can make, and the contract that lets the
//  edit history be read aloud.
//
//  The design point that shapes everything here: an edit command must be able to DESCRIBE
//  ITSELF in words. A sighted user pressing Ctrl+Z watches the page and sees what came
//  back. A blind user hears "undo" and has no idea what just happened — unless the program
//  tells them. So every command carries a sentence saying what it did, in the past tense
//  and in plain language, and undo says "undone: set the description of figure 3 to a bar
//  chart of quarterly revenue" rather than merely "undone".
//
//  That single requirement is why this is a class hierarchy rather than a pile of lambdas.
//  Each command owns three things that only it can know: how to apply itself, how to put
//  things back exactly as they were, and how to say what it is.
//
//  The Apply and Revert methods are template methods. The base class handles the state
//  bookkeeping — has this been applied, is the document now dirty, what did it change —
//  so that a subclass cannot forget it and leave the history inconsistent.
// =====================================================================================

#region EditResult

/// <summary>The outcome of applying or reverting an edit.</summary>
public readonly record struct EditResult(bool Succeeded, string Message)
{
    public static EditResult Ok(string message) => new(true, message);

    public static EditResult Failed(string message) => new(false, message);
}

#endregion

#region EditCommand — the abstract base

/// <summary>
/// Base class for every change to a document. Owns the applied/reverted bookkeeping; subclasses
/// supply the change itself, how to undo it, and how to describe it.
/// </summary>
public abstract class EditCommand
{
    #region Identity and classification

    /// <summary>What category of change this is. Used for grouping and for the save summary.</summary>
    public abstract EditKind Kind { get; }

    /// <summary>
    /// How safely this change can be written back. Anything above <see cref="EditConfidence.Safe"/>
    /// forces a backup on save and is called out to the user beforehand.
    /// </summary>
    public virtual EditConfidence Confidence => EditConfidence.Safe;

    /// <summary>
    /// What this command does, in the past tense, as a sentence a person would say.
    ///
    /// The most important member on this class. It is what undo and redo read out, what the edit
    /// history lists, and what the save summary is built from. "Set the description of figure 3 on
    /// page 4" — not "AltTextCommand(id=17)".
    /// </summary>
    public abstract string Description { get; }

    /// <summary>
    /// The element this change affects, when it affects one. Lets the UI move the reading position
    /// to whatever just changed, so that undo does not merely announce something happened
    /// somewhere else in the document.
    /// </summary>
    public virtual DocumentElement? AffectedElement => null;

    /// <summary>When the change was made, for the history list.</summary>
    public DateTimeOffset MadeAt { get; } = DateTimeOffset.Now;

    /// <summary>Whether the change is currently applied.</summary>
    public bool IsApplied { get; private set; }

    #endregion

    #region The apply and revert templates
    // Neither is virtual. Subclasses implement ApplyCore and RevertCore, so the guard against
    // double-applying, the dirty flag and the error handling are identical for every command. A
    // command that applied twice, or that threw and left the history thinking it had succeeded,
    // would corrupt the undo stack in a way the user could not diagnose.

    /// <summary>Applies the change to a document.</summary>
    public EditResult Apply(PdfDocumentModel document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (IsApplied)
            return EditResult.Failed("That change has already been made.");

        try
        {
            var result = ApplyCore(document);

            if (result.Succeeded)
            {
                IsApplied = true;
                document.HasUnsavedChanges = true;
            }

            return result;
        }
        catch (Exception ex)
        {
            return EditResult.Failed($"That change could not be made: {ex.Message}");
        }
    }

    /// <summary>Puts the document back as it was before this change.</summary>
    public EditResult Revert(PdfDocumentModel document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!IsApplied)
            return EditResult.Failed("That change has not been made, so there is nothing to undo.");

        try
        {
            var result = RevertCore(document);

            if (result.Succeeded)
            {
                IsApplied = false;
                document.HasUnsavedChanges = true;
            }

            return result;
        }
        catch (Exception ex)
        {
            return EditResult.Failed($"That change could not be undone: {ex.Message}");
        }
    }

    /// <summary>Makes the change. Called only when it is not already applied.</summary>
    protected abstract EditResult ApplyCore(PdfDocumentModel document);

    /// <summary>
    /// Restores the previous state exactly. Every command must capture whatever it needs for this
    /// at construction time, because by the time Revert runs the old value is gone from the model.
    /// </summary>
    protected abstract EditResult RevertCore(PdfDocumentModel document);

    #endregion

    #region Merging
    // Typing a value into a field character by character would otherwise produce one undo entry per
    // keystroke, so that undoing a mistyped name means pressing Ctrl+Z fifteen times and hearing
    // fifteen near-identical announcements.

    /// <summary>
    /// Whether this command can absorb a later one, so that a run of small related changes becomes
    /// a single undoable step. Refused by default: merging is only correct where the commands
    /// genuinely describe one user action.
    /// </summary>
    public virtual bool CanMergeWith(EditCommand later) => false;

    /// <summary>
    /// Absorbs a later command. Only called when <see cref="CanMergeWith"/> returned true, and the
    /// absorbed command is then discarded.
    /// </summary>
    public virtual void MergeWith(EditCommand later) { }

    #endregion

    #region Diagnostics

    public override string ToString() => $"{Kind}: {Description}";

    #endregion
}

#endregion
