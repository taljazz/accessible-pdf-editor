using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Editing;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Forms;
using AccessiblePdfEditor.Persistence;

namespace AccessiblePdfEditor.UI;

// =====================================================================================
//  MainForm.Forms.cs
//
//  Operations on a form as a whole: clearing it, flattening it, and signing it.
//
//  All three are consequential and none of them can be judged by looking, so each is
//  confirmed with the consequence stated in plain words before anything happens. That is
//  not defensive padding — clearing a form throws away twenty minutes of typing, flattening
//  is irreversible once saved, and a signature is a legal act.
// =====================================================================================

public sealed partial class MainForm
{
    #region Clearing a form

    /// <summary>
    /// Clears every writable field, after confirming. Reached from a reset button in the document
    /// and from the Tools menu, because a form may want clearing whether or not its designer
    /// provided a button.
    /// </summary>
    private void ResetForm(string? buttonLabel = null)
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        var command = new ResetFormCommand(_document);

        if (command.FieldsWithValues == 0)
        {
            Play(AudioCue.Boundary);
            Announce("There is nothing to clear. None of the fields have been filled in.",
                AnnouncementPriority.Assertive);
            return;
        }

        // Confirmed emphatically, with the count, because this is the single most destructive thing
        // a user can do by accident here and they cannot see the form empty itself.
        string what = buttonLabel is { Length: > 0 }
            ? $"The button \"{buttonLabel}\" clears this form."
            : "This clears the whole form.";

        bool sure = Confirm(
            $"{what} {command.FieldsWithValues} " +
            $"{(command.FieldsWithValues == 1 ? "field has" : "fields have")} been filled in, and " +
            "everything typed into them will be emptied. You can undo it with Control plus Z. " +
            "Are you sure?",
            "Clear the whole form");

        if (!sure)
        {
            Announce("Nothing cleared. The form is as it was.", AnnouncementPriority.Assertive);
            return;
        }

        ApplyEdit(command);
    }

    #endregion

    #region Flattening

    /// <summary>
    /// Saves a copy with the form fields turned into ordinary page content.
    ///
    /// Deliberately offered only as SAVE A COPY, never as an in-place save. Flattening cannot be
    /// undone once written, and the user cannot open the result to check what happened to it. An
    /// untouched original is the only real protection, so the feature is shaped so that one always
    /// remains.
    /// </summary>
    private void SaveFlattenedCopy()
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        if (_document.FormFields.Count == 0)
        {
            Play(AudioCue.Boundary);
            Announce("This document has no form fields, so there is nothing to flatten.",
                AnnouncementPriority.Assertive);
            return;
        }

        bool sure = Confirm(
            $"Flattening turns this document's {_document.FormFields.Count} form fields into " +
            "ordinary page content. The answers stay visible, but nobody can change them " +
            "afterwards — including you — and a screen reader will no longer announce them as " +
            "fields. This is what you want when sending a completed form to someone. " +
            "It will be saved as a NEW file, and this one will be left exactly as it is. Continue?",
            "Flatten the form");

        if (!sure)
        {
            Announce("Nothing flattened.", AnnouncementPriority.Assertive);
            return;
        }

        using var chooser = new SaveFileDialog
        {
            Title = "Save the flattened copy as",
            Filter = "PDF documents (*.pdf)|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(_document.FilePath) + " (completed).pdf",
            InitialDirectory = Path.GetDirectoryName(_document.FilePath),
        };

        if (chooser.ShowDialog(this) != DialogResult.OK)
        {
            Announce("Nothing flattened.", AnnouncementPriority.Assertive);
            return;
        }

        Play(AudioCue.WorkStarted);
        Cursor = Cursors.WaitCursor;

        SaveResult result;

        try
        {
            result = _saver.Save(_document, new SaveOptions
            {
                TargetPath = chooser.FileName,
                FlattenForms = true,
                CreateBackup = false,
                VerifyAfterWriting = _settings.VerifySaves,
            });
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        Play(result.IsSuccess ? AudioCue.Saved : AudioCue.Error);
        Speech.BeginNewAnnouncement();

        string message = result.IsSuccess
            ? $"{result.BuildAnnouncement()} Your original is unchanged and still fillable."
            : result.BuildAnnouncement();

        Announce(message, AnnouncementPriority.Assertive);
    }

    #endregion

    #region Signing
    // The accessible signing flow. Two facts shape it:
    //
    //   1. A blind user cannot see the signature field, so the program has to take them to it and
    //      say where it is, rather than expecting them to find it.
    //   2. The DEFAULT route needs no pointing device at all. Drawing with a mouse is offered
    //      because some people want it and specifically asked for it, but it is offered third,
    //      after the two routes that need no pointer.

    /// <summary>Signs a signature field, or finds one to sign if the user is not on one.</summary>
    private void SignDocument(SignatureFormField? field = null)
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        field ??= ChooseSignatureField();

        if (field is null)
            return;

        if (field.IsSigned)
        {
            Play(AudioCue.Boundary);
            Announce(
                $"{field.Label} is already signed. {field.ValueForSpeech}. " +
                "This editor does not replace an existing signature.",
                AnnouncementPriority.Assertive);
            return;
        }

        // Where the field sits, in units a person can act on. Useful in itself, and essential if
        // they end up signing in another application.
        AnnounceFieldLocation(field);

        var mark = CaptureSignature(field);
        if (mark is null)
            return;

        ApplySignature(field, mark);
    }

    /// <summary>Finds the signature field to sign, asking when there is more than one.</summary>
    private SignatureFormField? ChooseSignatureField()
    {
        var fields = _document!.FormFields.OfType<SignatureFormField>().ToList();

        switch (fields.Count)
        {
            case 0:
                Play(AudioCue.Boundary);
                Announce(
                    "This document has no signature field. A signature can only be placed where the " +
                    "document provides somewhere for it to go.",
                    AnnouncementPriority.Assertive);
                return null;

            case 1:
                return fields[0];

            default:
                return ListSelectionDialog<SignatureFormField>.Choose(this, Speech, Cues,
                    "Which signature field",
                    $"This document has {fields.Count} signature fields.",
                    fields,
                    f => $"{f.Label}, page {f.PageNumber}",
                    f => f.IsSigned ? f.ValueForSpeech : "not signed",
                    actionButtonText: "&Sign this one");
        }
    }

    /// <summary>
    /// Says where a signature field is on its page, in millimetres from the corner.
    ///
    /// A blind user has no other way to know. It matters because signature fields are conventionally
    /// at the foot of a page, and knowing this one is halfway up tells them something is unusual —
    /// and because if they end up signing in another program, this is the only description of the
    /// place they need to reach.
    /// </summary>
    private void AnnounceFieldLocation(SignatureFormField field)
    {
        var page = _document!.GetPage(field.PageNumber);

        if (page is null || field.Bounds.IsEmpty)
        {
            Announce($"{field.Label} is on page {field.PageNumber}.");
            return;
        }

        // PDF units are 1/72 inch, with the origin at the bottom-left of the page.
        static double ToMillimetres(double points) => Math.Round(points * 25.4 / 72.0);

        double fromLeft = ToMillimetres(field.Bounds.Left);
        double fromBottom = ToMillimetres(field.Bounds.Bottom);
        double width = ToMillimetres(field.Bounds.Width);
        double height = ToMillimetres(field.Bounds.Height);

        string vertical = field.Bounds.CentreY < page.Height / 3 ? "near the bottom of the page"
            : field.Bounds.CentreY > page.Height * 2 / 3 ? "near the top of the page"
            : "about halfway down the page";

        Announce(
            $"{field.Label} is on page {field.PageNumber}, {vertical}, " +
            $"{fromLeft} millimetres from the left edge and {fromBottom} from the bottom. " +
            $"The box is {width} by {height} millimetres.");
    }

    #endregion

    #region Capturing the signature

    private sealed record SignatureMethod(string Name, string Detail, SignatureSource Source);

    /// <summary>
    /// Asks how the user wants to sign.
    ///
    /// Order matters and is deliberate. An image of a real handwritten signature is first because
    /// it is both the most genuine and the easiest without sight — a file picker needs no pointer.
    /// Typing is second for the same reason. Drawing is third: it is what was asked for and it
    /// works, but it is the hardest of the three to do well without seeing, so it should not be
    /// what someone falls into by pressing Enter.
    /// </summary>
    private SignatureMark? CaptureSignature(SignatureFormField field)
    {
        var methods = new List<SignatureMethod>
        {
            new("Use an image of my signature",
                "A scan or photo of your handwritten signature. Needs no mouse.",
                SignatureSource.Image),

            new("Type my name",
                "Your name, drawn into the field as text. Needs no mouse.",
                SignatureSource.TypedName),

            new("Draw my signature now",
                "Draw with the mouse, or with the arrow keys. The pitch tells you where you are.",
                SignatureSource.Drawn),
        };

        var chosen = ListSelectionDialog<SignatureMethod>.Choose(this, Speech, Cues,
            $"Sign: {field.Label}",
            "Choose how you want to sign.",
            methods,
            method => method.Name,
            method => method.Detail,
            actionButtonText: "&Continue");

        if (chosen is null)
        {
            Announce("Not signed.", AnnouncementPriority.Assertive);
            return null;
        }

        string? signerName = AskForSignerName();
        if (signerName is null)
            return null;

        var mark = chosen.Source switch
        {
            SignatureSource.Image => CaptureFromImage(signerName),
            SignatureSource.TypedName => SignatureMark.FromTypedName(signerName),
            SignatureSource.Drawn => SignaturePadDialog.CaptureDrawing(this, Speech, Cues, signerName),
            _ => null,
        };

        if (mark is null)
        {
            Announce("Not signed.", AnnouncementPriority.Assertive);
            return null;
        }

        mark.SignerName = signerName;
        mark.Reason = AskForReason();

        return mark;
    }

    private string? AskForSignerName() =>
        TextPromptDialog.Ask(this, Speech, Cues,
            "Your name",
            "Name to appear with the signature:",
            "This is printed beneath the signature and recorded in the document. A handwritten " +
            "signature is often hard to read, so this is what tells anyone who signed it.",
            initialValue: Environment.UserName,
            validate: value => value.Trim().Length == 0
                ? "Type your name, or press Escape to stop."
                : null);

    private string? AskForReason() =>
        TextPromptDialog.Ask(this, Speech, Cues,
            "Reason (optional)",
            "Why are you signing?",
            "Optional. For example: I agree to these terms. Press Escape to leave it out.");

    private SignatureMark? CaptureFromImage(string signerName)
    {
        using var chooser = new OpenFileDialog
        {
            Title = "Choose an image of your signature",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (chooser.ShowDialog(this) != DialogResult.OK)
            return null;

        var mark = SignatureMark.FromImage(chooser.FileName, signerName);

        if (!mark.IsUsable)
        {
            ReportProblem($"{Path.GetFileName(chooser.FileName)} could not be read.", "Image problem");
            return null;
        }

        return mark;
    }

    #endregion

    #region Applying the signature

    /// <summary>
    /// Places the captured signature into the field, after saying exactly what is about to happen
    /// and what kind of signature it is.
    /// </summary>
    private void ApplySignature(SignatureFormField field, SignatureMark mark)
    {
        // The distinction that must never be blurred. A visible mark is a picture of a signature:
        // often legally sufficient, and anyone with the file could lift it out and reuse it. A
        // sighted user can inspect a signature's properties to find this out; a blind user is told.
        bool sure = Confirm(
            $"About to place {mark.Describe()} into {field.Label} on page {field.PageNumber}. " +
            "Your signature will be drawn into the page itself and labelled so that a screen " +
            "reader announces it, and the empty signature box will be removed so that nothing " +
            "covers your mark. That means this spot can no longer be signed again. " +
            "This is a visible signature — a picture of your signature in the document. It is what " +
            "most e-signing works like and is usually enough. It is NOT a cryptographic signature, " +
            "so it does not prove the document has not been changed since, and anyone with a copy " +
            "of the file could take the image out of it. Continue?",
            "Place your signature");

        if (!sure)
        {
            Announce("Not signed. The field is still empty.", AnnouncementPriority.Assertive);
            return;
        }

        Play(AudioCue.WorkStarted);

        var command = new ApplySignatureCommand(field, mark);
        ApplyEdit(command);

        Announce(
            "The signature has been placed. It is not written into the file until you save — " +
            "press Control plus S. Until then, Control plus Z takes it back.",
            AnnouncementPriority.Assertive);
    }

    #endregion
}
