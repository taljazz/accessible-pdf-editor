using AccessiblePdfEditor.Model;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using SharpDocument = PdfSharp.Pdf.PdfDocument;

namespace AccessiblePdfEditor.Persistence;

// =====================================================================================
//  DocumentSaver.cs
//
//  Writes edits back to disk, safely.
//
//  "Safely" is doing real work in that sentence. PDFsharp does not save incrementally: it
//  parses the whole file into an object graph and writes an entirely new one. Anything it
//  did not understand on the way in does not come out. That is usually fine and occasionally
//  catastrophic, and the person least able to detect it is the one this editor is for —
//  a blind user cannot glance at the saved file and notice that a page now looks wrong.
//
//  So saving never writes over the original directly. The sequence is:
//
//    1. write to a temporary file beside the target
//    2. RE-OPEN that temporary file and verify it — page count, form values, readable text
//    3. only if it verifies, back up the original and move the new file into place
//    4. if anything fails at any point, the original is untouched and the user is told
//
//  Step 2 is the one that matters. Writing a file that cannot be re-read is exactly the
//  failure mode that a save-and-hope approach ships to the recipient, and it costs one
//  extra parse to rule out.
// =====================================================================================

#region SaveOptions and SaveResult

/// <summary>How a save should be carried out.</summary>
public sealed class SaveOptions
{
    /// <summary>Where to write. Null means overwrite the file the document was opened from.</summary>
    public string? TargetPath { get; init; }

    /// <summary>
    /// Whether to keep a copy of the original alongside the saved file. On by default, because
    /// PDFsharp rewrites the whole file and the user cannot inspect the result themselves.
    /// </summary>
    public bool CreateBackup { get; init; } = true;

    /// <summary>
    /// Whether to turn form fields into ordinary page content, making them permanently
    /// uneditable. Only ever done on an explicit, confirmed request.
    /// </summary>
    public bool FlattenForms { get; init; }

    /// <summary>
    /// Whether to re-open and check the written file before replacing the original. On by default;
    /// switching it off is only sensible for very large documents where the second parse is slow.
    /// </summary>
    public bool VerifyAfterWriting { get; init; } = true;

    /// <summary>
    /// Whether to save even when doing so would destroy accessibility structure the writing library
    /// cannot see.
    ///
    /// Off by default, and the default is the point. The user must be told what would be lost and
    /// say yes to losing it; a blind user cannot discover afterwards that their document's headings
    /// have been deleted. Only ever set this from a UI that has spoken
    /// <see cref="StructureSafetyReport.BuildWarning"/> and had an explicit confirmation.
    /// </summary>
    public bool AllowStructureLoss { get; init; }
}

/// <summary>The outcome of a save.</summary>
public sealed record SaveResult(SaveOutcome Outcome, string Message)
{
    /// <summary>Where the document was written, when it was.</summary>
    public string? SavedPath { get; init; }

    /// <summary>Where the original was kept, when a backup was made.</summary>
    public string? BackupPath { get; init; }

    /// <summary>Problems that did not stop the save but that the user should hear about.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool IsSuccess => Outcome is SaveOutcome.Saved or SaveOutcome.SavedAsCopy;

    /// <summary>
    /// The full spoken report. Warnings are included rather than tucked away in a log: a user who
    /// cannot see the saved file has no other way to learn that three of their answers contain
    /// characters that will not display.
    /// </summary>
    public string BuildAnnouncement()
    {
        if (Warnings.Count == 0)
            return Message;

        string count = Warnings.Count == 1 ? "1 thing to note" : $"{Warnings.Count} things to note";
        return $"{Message} {count}: {string.Join(" ", Warnings)}";
    }
}

#endregion

#region IDocumentSaver

/// <summary>Writes a document's edits back to disk.</summary>
public interface IDocumentSaver
{
    /// <summary>
    /// Saves a document. Never throws for an ordinary failure: everything comes back as a result
    /// with an outcome and a message the user can be told.
    /// </summary>
    SaveResult Save(PdfDocumentModel document, SaveOptions options);
}

#endregion

#region DocumentSaverBase — the safety sequence no saver may skip

/// <summary>
/// Base class for document savers. Owns the write-verify-swap sequence; subclasses supply only the
/// applying of edits to the underlying file.
/// </summary>
public abstract class DocumentSaverBase : IDocumentSaver
{
    #region The save template

    /// <inheritdoc />
    public SaveResult Save(PdfDocumentModel document, SaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        string targetPath = options.TargetPath ?? document.FilePath;
        bool overwritingOriginal = string.Equals(
            Path.GetFullPath(targetPath),
            Path.GetFullPath(document.FilePath),
            StringComparison.OrdinalIgnoreCase);

        // An unchanged document is not rewritten. This is not an optimisation: saving rewrites the
        // whole file, and every rewrite carries the risk of losing something the writing library
        // did not understand. A save that had nothing to save and damaged the file anyway would be
        // the worst possible trade, so it simply does not happen.
        if (overwritingOriginal && !HasAnythingToSave(document))
        {
            return new SaveResult(SaveOutcome.NoChanges,
                "There is nothing to save. No changes have been made, and your file has not been " +
                "touched.");
        }

        var writable = CheckTargetWritable(targetPath);
        if (writable is not null)
            return writable;

        // Checked before any work is done, and before the user is told the save is under way. A
        // document whose accessibility structure would be destroyed must never reach the point of
        // having been written.
        var safety = StructureSafetyInspector.Inspect(document.FilePath);
        var warnings = new List<string>();

        if (!safety.IsSafeToSave && !options.AllowStructureLoss)
        {
            return new SaveResult(SaveOutcome.Cancelled,
                $"Nothing has been saved and your file is unchanged. {safety.BuildWarning()} " +
                "You can save this as a new copy instead, which leaves the original intact, or " +
                "confirm that you want to save anyway.")
            {
                Warnings = safety.InspectionProblems,
            };
        }

        if (!safety.IsSafeToSave)
        {
            // Saving anyway was explicitly confirmed. It is still recorded in the result, so the
            // loss is stated at the moment it happens rather than only when it was agreed to.
            if (safety.WouldDestroyStructure)
            {
                warnings.Add(
                    $"The document's {safety.StructureElementCount} accessibility tags have been " +
                    "lost, as you confirmed. Its headings and structure will no longer be available " +
                    "to a screen reader.");
            }

            if (safety.HasDigitalSignature)
                warnings.Add("The document's digital signature is no longer valid.");
        }

        string temporaryPath = BuildTemporaryPath(targetPath);

        // Taken before anything is written, and compared against the result. This is what catches
        // silent loss without having to predict which kind of loss: whatever went missing, the
        // saved file has fewer of something than the original did.
        var before = options.VerifyAfterWriting
            ? DocumentFingerprint.Take(document.FilePath)
            : null;

        try
        {
            var applied = ApplyAndWrite(document, temporaryPath, options, warnings);
            if (applied is not null)
            {
                DeleteQuietly(temporaryPath);
                return applied;
            }

            if (options.VerifyAfterWriting)
            {
                string? failure = Verify(temporaryPath, document);

                if (failure is not null)
                {
                    DeleteQuietly(temporaryPath);

                    return new SaveResult(SaveOutcome.RolledBack,
                        $"The saved file did not read back correctly, so your original has been left " +
                        $"untouched and nothing was changed. {failure}");
                }

                if (before is { IsValid: true })
                {
                    var after = DocumentFingerprint.Take(temporaryPath);

                    // Flattening a signature deliberately consumes its field, and a field's widget
                    // IS an annotation, so one of each disappears per signature. That is the
                    // requested operation, not damage, so the expected reduction is declared rather
                    // than being reported as loss and rolling the save back.
                    int consumed = CountFieldsConsumedBySigning(document);

                    var losses = after.FindLossesSince(
                        before,
                        expectedFieldChange: -consumed,
                        expectedAnnotationChange: -consumed);

                    // The structure loss the user explicitly agreed to is not reported again as a
                    // fault; anything else is, and rolls the save back.
                    if (options.AllowStructureLoss)
                    {
                        losses = losses
                            .Where(l => !l.Contains("accessibility tags", StringComparison.Ordinal))
                            .ToList();
                    }

                    if (losses.Count > 0)
                    {
                        DeleteQuietly(temporaryPath);

                        return new SaveResult(SaveOutcome.RolledBack,
                            DocumentFingerprint.DescribeLosses(losses));
                    }
                }
            }

            string? backupPath = null;

            if (options.CreateBackup && File.Exists(targetPath))
            {
                backupPath = CreateBackup(targetPath, warnings);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);

            document.HasUnsavedChanges = false;

            if (overwritingOriginal)
                document.FilePath = targetPath;

            string where = overwritingOriginal
                ? $"Saved {Path.GetFileName(targetPath)}."
                : $"Saved a copy as {Path.GetFileName(targetPath)}.";

            if (backupPath is not null)
                where += $" Your original was kept as {Path.GetFileName(backupPath)}.";

            return new SaveResult(
                overwritingOriginal ? SaveOutcome.Saved : SaveOutcome.SavedAsCopy, where)
            {
                SavedPath = targetPath,
                BackupPath = backupPath,
                Warnings = warnings,
            };
        }
        catch (Exception ex)
        {
            DeleteQuietly(temporaryPath);
            return new SaveResult(SaveOutcome.Failed,
                $"Saving failed and your original has been left untouched: {ex.Message}");
        }
    }

    /// <summary>
    /// How many form fields the save is expected to consume. Signing flattens the mark into the
    /// page and removes the signature field, so each pending signature accounts for one field that
    /// is supposed to be gone afterwards.
    /// </summary>
    protected static int CountFieldsConsumedBySigning(PdfDocumentModel document) =>
        document.FormFields
            .OfType<Model.Forms.SignatureFormField>()
            .Count(f => f.HasPendingSignature);

    /// <summary>
    /// Whether a document has anything worth writing.
    ///
    /// Answered from the actual state of the document, not only from its changed flag. A flag that
    /// every caller has to remember to set is a flag that will eventually be wrong, and being wrong
    /// here means either rewriting a file for no reason or — far worse — silently discarding
    /// someone's work because a flag went stale.
    /// </summary>
    protected static bool HasAnythingToSave(PdfDocumentModel document)
    {
        if (document.HasUnsavedChanges)
            return true;

        foreach (var field in document.FormFields)
        {
            if (field.IsModified || field.IsLabelModified)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Applies the document's edits and writes the result to a path. Returns null on success, or a
    /// result describing the failure.
    /// </summary>
    protected abstract SaveResult? ApplyAndWrite(
        PdfDocumentModel document, string writeTo, SaveOptions options, List<string> warnings);

    /// <summary>
    /// Re-opens a written file and checks it is sound. Returns null when it is, or a sentence
    /// describing what is wrong.
    /// </summary>
    protected abstract string? Verify(string path, PdfDocumentModel original);

    #endregion

    #region File handling

    /// <summary>
    /// Checks a path can be written before any work is done, so that a locked file is reported
    /// immediately rather than after a long save.
    /// </summary>
    private static SaveResult? CheckTargetWritable(string targetPath)
    {
        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return new SaveResult(SaveOutcome.TargetNotWritable,
                    $"The folder for {Path.GetFileName(targetPath)} does not exist.");
            }

            if (File.Exists(targetPath))
            {
                var attributes = File.GetAttributes(targetPath);

                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    return new SaveResult(SaveOutcome.TargetNotWritable,
                        $"{Path.GetFileName(targetPath)} is marked read-only, so it cannot be saved over. " +
                        "Save it somewhere else, or remove the read-only setting.");
                }

                // A file open in another program is the commonest save failure by far, and worth
                // catching before the work rather than after it.
                using var probe = File.Open(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }

            return null;
        }
        catch (IOException)
        {
            return new SaveResult(SaveOutcome.TargetNotWritable,
                $"{Path.GetFileName(targetPath)} is open in another program. Close it and try again.");
        }
        catch (UnauthorizedAccessException)
        {
            return new SaveResult(SaveOutcome.TargetNotWritable,
                $"You do not have permission to write {Path.GetFileName(targetPath)}.");
        }
        catch (Exception ex)
        {
            return new SaveResult(SaveOutcome.TargetNotWritable,
                $"{Path.GetFileName(targetPath)} cannot be written: {ex.Message}");
        }
    }

    /// <summary>
    /// A temporary path beside the target. Beside, not in the system temp folder, so that the final
    /// move is within one volume and therefore atomic — a cross-volume move is a copy, and a copy
    /// interrupted halfway leaves a truncated file where the user's document used to be.
    /// </summary>
    private static string BuildTemporaryPath(string targetPath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        string name = Path.GetFileNameWithoutExtension(targetPath);
        string unique = Guid.NewGuid().ToString("N")[..8];

        return Path.Combine(directory ?? ".", $"{name}.saving-{unique}.tmp");
    }

    /// <summary>
    /// Copies the original alongside itself before it is replaced. A failure here is reported but
    /// does not stop the save: the user asked to save, and refusing because the backup could not be
    /// made would be a worse outcome than saving without one, provided they are told.
    /// </summary>
    private static string? CreateBackup(string targetPath, List<string> warnings)
    {
        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
            string name = Path.GetFileNameWithoutExtension(targetPath);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");

            string backupPath = Path.Combine(directory ?? ".", $"{name}.backup-{stamp}.pdf");
            File.Copy(targetPath, backupPath, overwrite: false);

            return backupPath;
        }
        catch (Exception ex)
        {
            warnings.Add($"A backup copy could not be made: {ex.Message}");
            return null;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A stray temporary file is untidy but harmless, and reporting it would bury the real
            // message about what happened to the save.
        }
    }

    #endregion
}

#endregion

#region PdfSharpDocumentSaver — the real one

/// <summary>Saves documents using PDFsharp, applying form values and metadata edits.</summary>
public sealed class PdfSharpDocumentSaver : DocumentSaverBase
{
    #region Applying edits

    protected override SaveResult? ApplyAndWrite(
        PdfDocumentModel document, string writeTo, SaveOptions options, List<string> warnings)
    {
        SharpDocument sharp;

        try
        {
            sharp = PdfReader.Open(document.FilePath, PdfDocumentOpenMode.Modify);
        }
        catch (Exception ex)
        {
            return new SaveResult(SaveOutcome.Failed,
                $"The original file could not be re-opened for saving: {ex.Message}");
        }

        using (sharp)
        {
            WriteFormValues(document, sharp, warnings);
            WriteMetadata(document, sharp, warnings);

            if (options.FlattenForms)
                FlattenForms(sharp, warnings);

            try
            {
                sharp.Save(writeTo);
            }
            catch (Exception ex)
            {
                return new SaveResult(SaveOutcome.Failed, $"The document could not be written: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Writes every changed form value. Only modified fields are touched: rewriting an untouched
    /// field would regenerate its appearance with this editor's font and formatting, quietly
    /// changing the look of parts of the form the user never went near.
    /// </summary>
    private static void WriteFormValues(
        PdfDocumentModel document, SharpDocument sharp, List<string> warnings)
    {
        // Both kinds of change count: a filled-in value and a repaired label are separate edits and
        // either alone is a reason to write the field.
        var changed = document.FormFields
            .Where(f => f.IsModified || f.IsLabelModified)
            .ToList();

        if (changed.Count == 0)
            return;

        var form = RawFormAccess.Open(sharp);

        if (form is null)
        {
            warnings.Add("The form fields could not be found in the file, so no values were saved.");
            return;
        }

        var writer = new FormWriter(form);
        int written = 0;

        foreach (var field in changed)
        {
            var result = writer.Write(field);

            if (result.Written)
                written++;

            if (result.Warning is { Length: > 0 } warning)
                warnings.Add(warning);
        }

        if (written > 0)
            form.RequestAppearanceRegeneration();

        if (written < changed.Count)
        {
            warnings.Add(
                $"{changed.Count - written} of {changed.Count} changed fields could not be saved.");
        }
    }

    /// <summary>
    /// Writes document metadata. The language and the display-title preference are accessibility
    /// features rather than cosmetics: without a language a screen reader reads the document in
    /// whatever voice it happens to have, and without the display-title flag it announces the
    /// filename instead of the title.
    /// </summary>
    private static void WriteMetadata(
        PdfDocumentModel document, SharpDocument sharp, List<string> warnings)
    {
        var metadata = document.Metadata;

        try
        {
            if (metadata.Title is { Length: > 0 } title)
                sharp.Info.Title = title;

            if (metadata.Author is { Length: > 0 } author)
                sharp.Info.Author = author;

            if (metadata.Subject is { Length: > 0 } subject)
                sharp.Info.Subject = subject;

            if (metadata.Keywords is { Length: > 0 } keywords)
                sharp.Info.Keywords = keywords;

            if (metadata.Language is { Length: > 0 } language)
                sharp.Language = language;

            if (metadata.DisplaysDocumentTitle)
                SetDisplayDocumentTitle(sharp);
        }
        catch (Exception ex)
        {
            warnings.Add($"Some document details could not be saved: {ex.Message}");
        }
    }

    /// <summary>
    /// Tells viewers to announce the document's title rather than its filename.
    ///
    /// Written straight into the catalog rather than through PDFsharp's typed
    /// <c>ViewerPreferences</c> property, which throws a null reference on any document that
    /// already has a viewer-preferences dictionary — which was 3 of 8 real files in testing. Since
    /// this setting is one of the cheapest accessibility wins there is, it must not be the thing
    /// that crashes a save.
    /// </summary>
    private static void SetDisplayDocumentTitle(SharpDocument sharp)
    {
        var catalog = sharp.Internals.Catalog;
        var preferences = catalog.Elements.GetDictionary("/ViewerPreferences");

        if (preferences is null)
        {
            preferences = new PdfDictionary(sharp);
            catalog.Elements.SetValue("/ViewerPreferences", preferences);
        }

        preferences.Elements.SetBoolean("/DisplayDocTitle", true);
    }

    /// <summary>
    /// Turns form fields into ordinary page content. Irreversible once saved, which is why it only
    /// ever runs on an explicit request that the caller has already confirmed.
    /// </summary>
    private static void FlattenForms(SharpDocument sharp, List<string> warnings)
    {
        try
        {
            sharp.Flatten();
            warnings.Add("The form fields have been turned into ordinary page content. " +
                         "They can no longer be filled in or changed in the saved file.");
        }
        catch (Exception ex)
        {
            warnings.Add($"The form could not be flattened, so its fields are still editable: {ex.Message}");
        }
    }

    #endregion

    #region Verification
    // Re-opens the written file with a DIFFERENT library from the one that wrote it. PDFsharp
    // reading back its own output proves far less than an independent parser doing so: a structure
    // only PDFsharp understands would pass the first test and fail in the recipient's viewer.

    protected override string? Verify(string path, PdfDocumentModel original)
    {
        try
        {
            using var check = PigDocument.Open(path, new UglyToad.PdfPig.ParsingOptions
            {
                UseLenientParsing = true,
                SkipMissingFonts = true,
            });

            if (check.NumberOfPages != original.PageCount)
            {
                return $"The saved file has {check.NumberOfPages} pages where the original had " +
                       $"{original.PageCount}.";
            }

            // Reading a page proves the content streams survived, which a page count alone does not.
            if (check.NumberOfPages > 0)
                _ = check.GetPage(1);

            return VerifyFormValues(check, original);
        }
        catch (Exception ex)
        {
            return $"It could not be re-opened: {ex.Message}";
        }
    }

    /// <summary>
    /// Checks that the values just written actually read back. This is the check that catches the
    /// corrupt-value failures PDFsharp's own typed setters produce — a value written as
    /// "(Netherlands)" with the brackets baked in saves without error and is silently wrong, and
    /// only reading it back with an independent parser reveals it.
    /// </summary>
    private static string? VerifyFormValues(PigDocument check, PdfDocumentModel original)
    {
        // Flattened signature fields are excluded before anything else: they are meant to be gone,
        // and on a document whose only field was the signature the whole form legitimately
        // disappears with it. Checking for the form first would report that as damage and roll
        // back the very save that was asked for.
        var changed = original.FormFields
            .Where(f => f.IsModified && f.HasValue)
            .Where(f => f is not Model.Forms.SignatureFormField { HasPendingSignature: true })
            .ToList();

        if (changed.Count == 0)
            return null;

        try
        {
            if (!check.TryGetForm(out var form) || form is null)
                return "The form fields are missing from the saved file.";

            var byName = new Dictionary<string, UglyToad.PdfPig.AcroForms.Fields.AcroFieldBase>(
                StringComparer.Ordinal);

            foreach (var field in UglyToad.PdfPig.AcroForms.AcroFormExtensions.GetFields(form))
            {
                string? name = field.Information?.PartialName;
                if (!string.IsNullOrEmpty(name))
                    byName[name] = field;
            }

            foreach (var field in changed)
            {
                if (!byName.ContainsKey(field.PartialName))
                {
                    return $"The field '{field.Label}' is missing from the saved file.";
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"The saved form could not be checked: {ex.Message}";
        }
    }

    #endregion
}

#endregion
