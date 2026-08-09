using AccessiblePdfEditor.Ingestion;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Forms;
using AccessiblePdfEditor.Persistence;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace AccessiblePdfEditor.Tests;

// =====================================================================================
//  RoundTripTests.cs
//
//  The end-to-end test: build a real PDF form, load it, fill it in, save it, load it
//  again, and check the answers survived.
//
//  This is the test that matters most for the write path, because every failure it guards
//  against is SILENT. Testing established that PDFsharp's own typed setters write corrupt
//  values for radio buttons, combo boxes and list boxes — a radio selection comes out as
//  "/V /21 0 R" and a combo value as "(\(Netherlands\))" — and in every case the save
//  reports success. The file looks fine. It opens fine. The answers are wrong.
//
//  For a blind user filling in a form they cannot see, "saved successfully but the answers
//  are corrupt" is the worst possible outcome: nothing in the process tells them, and they
//  find out when the form is rejected by whoever receives it, weeks later.
//
//  So these tests do not check that saving returned success. They re-open the saved file
//  with a DIFFERENT library and read the values back.
// =====================================================================================

internal static class RoundTripTests
{
    public static void Register(TestRunner t)
    {
        t.Group("form fill round trip");

        t.Test("a text field's value survives save and reload", () =>
        {
            RunRoundTrip(t, (document, field) =>
            {
                var result = field.TrySetValue("Thomas Anderson");
                t.IsTrue(result.Accepted, "the value should be accepted");
            },
            (reloaded, field) =>
            {
                t.AreEqual("Thomas Anderson",
                    (field as TextFormField)?.Value ?? string.Empty,
                    "the value should come back exactly as it went in");
            });
        });

        t.Test("a value containing brackets and backslashes survives", () =>
        {
            // These are the characters that terminate a PDF string literal. Written unescaped they
            // corrupt everything after them in the file, and the corruption is silent.
            const string awkward = @"Smith (Jr) \ O'Brien";

            RunRoundTrip(t, (document, field) => field.TrySetValue(awkward),
                (reloaded, field) =>
                {
                    t.AreEqual(awkward, (field as TextFormField)?.Value ?? string.Empty,
                        "brackets and backslashes must survive");
                });
        });

        t.Test("a value with accented characters survives", () =>
        {
            const string accented = "Zoë Müller-Ferré";

            RunRoundTrip(t, (document, field) => field.TrySetValue(accented),
                (reloaded, field) =>
                {
                    t.AreEqual(accented, (field as TextFormField)?.Value ?? string.Empty,
                        "non-ASCII text must survive");
                });
        });

        t.Test("clearing a field survives", () =>
        {
            RunRoundTrip(t,
                (document, field) =>
                {
                    field.TrySetValue("Something");
                    field.Clear();
                },
                (reloaded, field) =>
                {
                    t.IsFalse(field.HasValue, "the field should come back empty");
                });
        });

        t.Test("saving reports the file it wrote and keeps a backup", () =>
        {
            string path = CreateFormPdf();

            try
            {
                var loaded = new PdfPigDocumentLoader().Load(path);
                t.IsTrue(loaded.IsSuccess, "the generated form should load");

                loaded.Document!.FormFields[0].TrySetValue("Filled in");

                var result = new PdfSharpDocumentSaver().Save(loaded.Document, new SaveOptions
                {
                    CreateBackup = true,
                });

                t.IsTrue(result.IsSuccess, $"the save should succeed, but said: {result.Message}");
                t.IsNotNull(result.BackupPath, "a backup should have been made");
                t.IsTrue(File.Exists(result.BackupPath!), "the backup file should exist");
            }
            finally
            {
                CleanUp(path);
            }
        });

        t.Test("saving a document with no changes does nothing and says so", () =>
        {
            string path = CreateFormPdf();

            try
            {
                var loaded = new PdfPigDocumentLoader().Load(path);
                var result = new PdfSharpDocumentSaver().Save(loaded.Document!, new SaveOptions());

                t.AreEqual(SaveOutcome.NoChanges, result.Outcome, "there is nothing to save");
            }
            finally
            {
                CleanUp(path);
            }
        });

        t.Test("a field's accessible label is written into the file and survives reload", () =>
        {
            // The repair that turns an unfillable form into a fillable one. It has to reach the
            // file, or the whole remediation feature is theatre: the user would fix the form, save,
            // send it on, and the recipient's screen reader would still announce "edit box".
            //
            // The field is named "Text1" on purpose — a form designer's default, carrying no
            // information, which is exactly the case that leaves a field genuinely unlabelled.
            string path = CreateFormPdf(includeToolTip: false, fieldName: "Text1");

            try
            {
                var loaded = new PdfPigDocumentLoader().Load(path);
                var field = loaded.Document!.FormFields[0];

                t.IsTrue(field.IsUnlabelled, "the generated field starts with no usable label");

                var history = new AccessiblePdfEditor.Editing.EditHistory(loaded.Document);
                history.Do(new AccessiblePdfEditor.Editing.SetFieldLabelCommand(field, "Full name"));

                t.AreEqual("Full name", field.Label, "the label should be set in the model");

                var result = new PdfSharpDocumentSaver().Save(loaded.Document, new SaveOptions
                {
                    CreateBackup = false,
                });

                t.IsTrue(result.IsSuccess, $"the save should succeed, but said: {result.Message}");

                var reloaded = new PdfPigDocumentLoader().Load(path);
                var reloadedField = reloaded.Document!.FormFields[0];

                t.AreEqual("Full name", reloadedField.Label,
                    "the label must come back from the file");

                t.AreEqual(PdfFormField.LabelSource.ToolTip, reloadedField.ResolvedLabelSource,
                    "and it must now be the document's own tooltip, not a guess");
            }
            finally
            {
                CleanUp(path);
            }
        });

        t.Test("undoing a label change puts the old label back", () =>
        {
            string path = CreateFormPdf(includeToolTip: true);

            try
            {
                var loaded = new PdfPigDocumentLoader().Load(path);
                var field = loaded.Document!.FormFields[0];
                var history = new AccessiblePdfEditor.Editing.EditHistory(loaded.Document);

                string original = field.Label;

                history.Do(new AccessiblePdfEditor.Editing.SetFieldLabelCommand(field, "Something else"));
                t.AreEqual("Something else", field.Label, "the new label should apply");

                history.Undo();
                t.AreEqual(original, field.Label, "the original label should come back");
            }
            finally
            {
                CleanUp(path);
            }
        });

        t.Test("signing does not claim a cryptographic signature", () =>
        {
            // The distinction the UI promises. A visible mark must leave nothing behind that says
            // the document is cryptographically signed, or the file would assert a guarantee it
            // cannot honour.
            string path = CreateSignatureFormPdf();

            try
            {
                SignAndSave(path, SignatureMark.FromTypedName("Thomas Anderson"));

                var report = StructureSafetyInspector.Inspect(path);

                t.IsFalse(report.HasDigitalSignature,
                    "a visible mark must not make the document claim it is cryptographically signed");
            }
            finally
            {
                CleanUp(path);
            }
        });

        t.Test("flattening removes the signature field so no viewer paints over the mark", () =>
        {
            // The reason flattening is the default. A signature field that is not cryptographically
            // signed is still a signature field, and Acrobat draws its own "click to sign" panel
            // over it — which the person who signed cannot see has happened.
            string path = CreateSignatureFormPdf();

            try
            {
                SignAndSave(path, SignatureMark.FromTypedName("Thomas Anderson"));

                var reloaded = new PdfPigDocumentLoader().Load(path);
                t.IsTrue(reloaded.IsSuccess, "the signed file should still load");

                t.AreEqual(0, reloaded.Document!.FormFields.OfType<SignatureFormField>().Count(),
                    "the signature field should be gone");
            }
            finally
            {
                CleanUp(path);
            }
        });

        t.Test("flattened ink is tagged so a screen reader announces it", () =>
        {
            // The whole accessibility win. Without the tag the signature is ink: nothing to
            // announce, and the person who signed cannot confirm it is announced at all.
            string path = CreateSignatureFormPdf();

            try
            {
                SignAndSave(path, SignatureMark.FromTypedName("Thomas Anderson"));

                using var pig = UglyToad.PdfPig.PdfDocument.Open(path,
                    new UglyToad.PdfPig.ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });

                var marked = pig.GetPage(1).GetMarkedContents();

                var figure = marked.FirstOrDefault(m => m.Tag == "Figure");
                t.IsNotNull(figure, "the flattened signature should be marked as a figure");

                // Read through the extractor's own alt-text path, which is what the reader uses.
                string? alt = ReadAltThroughExtractor(path);

                t.IsNotNull(alt, "the figure should carry alternate text");
                t.Says(alt!, "Thomas Anderson");
                t.Says(alt!, "not cryptographically verified");
            }
            finally
            {
                CleanUp(path);
            }
        });

        t.Test("a flattened signature is announced by our own reader", () =>
        {
            // End to end: sign, save, reopen with the editor's own loader, and confirm the figure
            // announces the signature rather than reporting a missing description.
            string path = CreateSignatureFormPdf();

            try
            {
                SignAndSave(path, SignatureMark.FromTypedName("Thomas Anderson"));

                var reloaded = new PdfPigDocumentLoader().Load(path);
                t.IsTrue(reloaded.IsSuccess, "the signed file should load");

                var described = reloaded.Document!.Figures
                    .FirstOrDefault(f => f.AlternateText?.Contains("Thomas Anderson",
                        StringComparison.OrdinalIgnoreCase) == true);

                t.IsNotNull(described, "the signature should come back as a described figure");
                t.IsFalse(described!.NeedsAlternateText, "and it should not report as needing a description");
                t.Says(described.Describe(VerbosityLevel.Normal), "Thomas Anderson");
            }
            finally
            {
                CleanUp(path);
            }
        });

        t.Test("flattened strokes put real ink on the page", () =>
        {
            string path = CreateSignatureFormPdf();

            try
            {
                var stroke = new SignatureStroke();
                for (int i = 0; i <= 30; i++)
                    stroke.Add(0.08 + i * 0.028, 0.5 + Math.Sin(i * 0.6) * 0.18);

                long before = new FileInfo(path).Length;
                SignAndSave(path, SignatureMark.FromStrokes([stroke], "Thomas Anderson"));

                t.IsTrue(new FileInfo(path).Length > before,
                    "the drawn signature should add content to the file");

                var reloaded = new PdfPigDocumentLoader().Load(path);
                t.IsTrue(reloaded.IsSuccess, "the file should still be readable");
            }
            finally
            {
                CleanUp(path);
            }
        });

        t.Test("flattening does not disturb the rest of the form", () =>
        {
            // A signature is the last thing anyone does to a form, so everything filled in before
            // it must survive.
            string path = CreateFormWithTextAndSignature();

            try
            {
                var loaded = new PdfPigDocumentLoader().Load(path);
                var text = loaded.Document!.FormFields.OfType<TextFormField>().First();
                text.TrySetValue("Thomas Anderson");

                var signature = loaded.Document.FormFields.OfType<SignatureFormField>().First();
                var history = new AccessiblePdfEditor.Editing.EditHistory(loaded.Document);
                history.Do(new AccessiblePdfEditor.Editing.ApplySignatureCommand(
                    signature, SignatureMark.FromTypedName("Thomas Anderson")));

                var result = new PdfSharpDocumentSaver().Save(loaded.Document,
                    new SaveOptions { CreateBackup = false });

                t.IsTrue(result.IsSuccess, $"the save should succeed: {result.BuildAnnouncement()}");

                var reloaded = new PdfPigDocumentLoader().Load(path);
                var reloadedText = reloaded.Document!.FormFields.OfType<TextFormField>().FirstOrDefault();

                t.IsNotNull(reloadedText, "the text field should survive");
                t.AreEqual("Thomas Anderson", reloadedText!.Value, "and keep its value");
            }
            finally
            {
                CleanUp(path);
            }
        });

        t.Test("the appearance-stream fallback still describes itself", () =>
        {
            // The route used when flattening cannot be done. It leaves the field in place, so the
            // description has to go on the widget instead of on a tag — and it must still be there,
            // or a signature placed that way would be invisible to assistive technology.
            string path = CreateSignatureFormPdf();

            try
            {
                using var document = PdfSharp.Pdf.IO.PdfReader.Open(
                    path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);

                var widget = FindSignatureWidget(document);
                t.IsNotNull(widget, "the signature widget should be found");

                bool written = SignatureWriter.WriteInto(
                    document, widget!, SignatureMark.FromTypedName("Thomas Anderson"), out _);

                t.IsTrue(written, "the appearance should be written");
                t.Says(widget!.Elements.GetString("/Contents"), "Thomas Anderson");
                t.Says(widget.Elements.GetString("/Contents"), "Signature");
            }
            finally
            {
                CleanUp(path);
            }
        });

        t.Test("an empty signature field does not block saving", () =>
        {
            // A regression guard. The safety check originally tested the form's /SigFlags, which is
            // set on any document that merely CONTAINS a signature field — including every blank
            // contract waiting to be signed. That refused to save exactly the documents this editor
            // exists to help someone fill in, and it did so with a message about breaking a
            // signature that was not there.
            string path = CreateSignatureFormPdf();

            try
            {
                var report = StructureSafetyInspector.Inspect(path);

                t.IsFalse(report.HasDigitalSignature,
                    "an unsigned signature field is not a signature");

                t.IsTrue(report.IsSafeToSave,
                    "a form with an empty signature field must still be saveable");
            }
            finally
            {
                CleanUp(path);
            }
        });

        t.Test("flattening writes a new file and leaves the original alone", () =>
        {
            string path = CreateFormPdf();
            string flattened = Path.Combine(Path.GetTempPath(), $"apde-flat-{Guid.NewGuid():N}.pdf");

            try
            {
                var loaded = new PdfPigDocumentLoader().Load(path);
                loaded.Document!.FormFields[0].TrySetValue("Thomas Anderson");

                long originalSize = new FileInfo(path).Length;

                var result = new PdfSharpDocumentSaver().Save(loaded.Document, new SaveOptions
                {
                    TargetPath = flattened,
                    FlattenForms = true,
                    CreateBackup = false,
                });

                t.IsTrue(result.IsSuccess, $"flattening should succeed: {result.Message}");
                t.IsTrue(File.Exists(flattened), "the flattened copy should exist");

                t.AreEqual(originalSize, new FileInfo(path).Length,
                    "the original must be untouched");

                // The flattened copy must still be a readable PDF.
                var reloaded = new PdfPigDocumentLoader().Load(flattened);
                t.IsTrue(reloaded.IsSuccess, "the flattened copy should open");
            }
            finally
            {
                CleanUp(path);
                CleanUp(flattened);
            }
        });

        t.Test("the saved file still reads back with the same page count", () =>
        {
            string path = CreateFormPdf();

            try
            {
                var loaded = new PdfPigDocumentLoader().Load(path);
                int before = loaded.Document!.PageCount;

                loaded.Document.FormFields[0].TrySetValue("Value");
                var result = new PdfSharpDocumentSaver().Save(loaded.Document, new SaveOptions());

                t.IsTrue(result.IsSuccess, $"the save should succeed, but said: {result.Message}");

                var reloaded = new PdfPigDocumentLoader().Load(path);
                t.AreEqual(before, reloaded.Document!.PageCount, "the page count must not change");
            }
            finally
            {
                CleanUp(path);
            }
        });
    }

    #region The round-trip harness

    /// <summary>
    /// Builds a form, applies a change, saves, reloads, and hands the reloaded field to a check.
    /// </summary>
    private static void RunRoundTrip(
        TestRunner t,
        Action<PdfDocumentModel, PdfFormField> change,
        Action<PdfDocumentModel, PdfFormField> verify)
    {
        string path = CreateFormPdf();

        try
        {
            var loaded = new PdfPigDocumentLoader().Load(path);
            t.IsTrue(loaded.IsSuccess, $"the generated form should load, but said: {loaded.Message}");

            var fields = loaded.Document!.FormFields;
            t.IsTrue(fields.Count > 0, "the generated form should have a field");

            change(loaded.Document, fields[0]);

            var result = new PdfSharpDocumentSaver().Save(loaded.Document, new SaveOptions
            {
                CreateBackup = false,
            });

            t.IsTrue(result.IsSuccess, $"the save should succeed, but said: {result.Message}");

            // Reloaded from disk with the reading library, not by inspecting what was written. A
            // writer checking its own output would agree with itself about a mistake.
            var reloaded = new PdfPigDocumentLoader().Load(path);
            t.IsTrue(reloaded.IsSuccess, "the saved file should load again");

            var reloadedFields = reloaded.Document!.FormFields;
            t.IsTrue(reloadedFields.Count > 0, "the saved file should still have its field");

            verify(reloaded.Document, reloadedFields[0]);
        }
        finally
        {
            CleanUp(path);
        }
    }

    #endregion

    #region Building a real PDF form
    // Built as raw dictionaries because PDFsharp 6.2.4 has no public API for creating form fields
    // at all — every constructor is internal and the field collection has no Add. This is the same
    // recipe the application's own writer relies on, so the test exercises the real thing.

    /// <summary>Creates a one-page PDF with a single text field, and returns its path.</summary>
    private static string CreateFormPdf(bool includeToolTip = true, string fieldName = "fullName")
    {
        string path = Path.Combine(Path.GetTempPath(), $"apde-form-{Guid.NewGuid():N}.pdf");

        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);

        // The font resource must be an indirect object before it can be referenced. Testing found
        // that missing this is what makes the common recipe throw on any document that already has
        // a form.
        var helvetica = new PdfDictionary(document);
        helvetica.Elements.SetName("/Type", "Font");
        helvetica.Elements.SetName("/Subtype", "Type1");
        helvetica.Elements.SetName("/BaseFont", "Helvetica");
        helvetica.Elements.SetName("/Encoding", "WinAnsiEncoding");
        document.Internals.AddObject(helvetica);

        var field = new PdfDictionary(document);
        field.Elements.SetName("/Type", "Annot");
        field.Elements.SetName("/Subtype", "Widget");
        field.Elements.SetName("/FT", "Tx");
        field.Elements.SetString("/T", fieldName);

        if (includeToolTip)
            field.Elements.SetString("/TU", "Full name");

        field.Elements.SetString("/DA", "/Helv 10 Tf 0 g");

        // Bit 2 is the Required flag.
        field.Elements.SetInteger("/Ff", 2);

        field.Elements.SetValue("/Rect", new PdfArray(document,
            new PdfInteger(60), new PdfInteger(700), new PdfInteger(360), new PdfInteger(722)));

        document.Internals.AddObject(field);
        field.Elements.SetReference("/P", page);

        var annotations = new PdfArray(document);
        annotations.Elements.Add(field.Reference!);
        page.Elements.SetValue("/Annots", annotations);

        var fonts = new PdfDictionary(document);
        fonts.Elements.SetReference("/Helv", helvetica);

        var resources = new PdfDictionary(document);
        resources.Elements.SetValue("/Font", fonts);

        var fields = new PdfArray(document);
        fields.Elements.Add(field.Reference!);

        var acroForm = new PdfDictionary(document);
        acroForm.Elements.SetValue("/Fields", fields);
        acroForm.Elements.SetString("/DA", "/Helv 10 Tf 0 g");
        acroForm.Elements.SetValue("/DR", resources);
        acroForm.Elements.SetBoolean("/NeedAppearances", true);

        document.Internals.AddObject(acroForm);
        document.Internals.Catalog.Elements.SetReference("/AcroForm", acroForm);

        document.Save(path);
        return path;
    }

    /// <summary>Creates a one-page PDF with a single empty signature field.</summary>
    private static string CreateSignatureFormPdf()
    {
        string path = Path.Combine(Path.GetTempPath(), $"apde-sig-{Guid.NewGuid():N}.pdf");

        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);

        var field = new PdfDictionary(document);
        field.Elements.SetName("/Type", "Annot");
        field.Elements.SetName("/Subtype", "Widget");
        field.Elements.SetName("/FT", "Sig");
        field.Elements.SetString("/T", "signature1");
        field.Elements.SetString("/TU", "Sign here");

        field.Elements.SetValue("/Rect", new PdfArray(document,
            new PdfInteger(60), new PdfInteger(120), new PdfInteger(300), new PdfInteger(190)));

        document.Internals.AddObject(field);
        field.Elements.SetReference("/P", page);

        var annotations = new PdfArray(document);
        annotations.Elements.Add(field.Reference!);
        page.Elements.SetValue("/Annots", annotations);

        var fields = new PdfArray(document);
        fields.Elements.Add(field.Reference!);

        var acroForm = new PdfDictionary(document);
        acroForm.Elements.SetValue("/Fields", fields);

        // Bit 1 says the document contains at least one signature field.
        acroForm.Elements.SetInteger("/SigFlags", 3);

        document.Internals.AddObject(acroForm);
        document.Internals.Catalog.Elements.SetReference("/AcroForm", acroForm);

        document.Save(path);
        return path;
    }

    /// <summary>
    /// Re-opens a file and reports whether its signature widget carries a normal appearance with
    /// real content. Checked through the raw object model rather than through any typed API, so it
    /// verifies what is actually in the file.
    /// </summary>
    private static bool SignatureAppearanceExists(string path, out int streamLength)
    {
        streamLength = 0;

        using var document = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);

        var acroForm = document.Internals.Catalog.Elements.GetDictionary("/AcroForm");
        var fields = acroForm?.Elements.GetArray("/Fields");

        if (fields is null)
            return false;

        for (int i = 0; i < fields.Elements.Count; i++)
        {
            var field = fields.Elements[i] is PdfReference reference
                ? reference.Value as PdfDictionary
                : fields.Elements[i] as PdfDictionary;

            if (field?.Elements.GetName("/FT") != "/Sig")
                continue;

            var normal = field.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N");
            if (normal?.Stream is null)
                continue;

            streamLength = normal.Stream.Value?.Length ?? 0;
            return streamLength > 0;
        }

        return false;
    }

    /// <summary>Loads a file, signs its signature field, and saves in place.</summary>
    private static void SignAndSave(string path, SignatureMark mark)
    {
        var loaded = new PdfPigDocumentLoader().Load(path);
        var field = loaded.Document!.FormFields.OfType<SignatureFormField>().First();

        var history = new AccessiblePdfEditor.Editing.EditHistory(loaded.Document);
        history.Do(new AccessiblePdfEditor.Editing.ApplySignatureCommand(field, mark));

        var result = new PdfSharpDocumentSaver().Save(loaded.Document,
            new SaveOptions { CreateBackup = false });

        if (!result.IsSuccess)
            throw new AssertionException($"signing failed: {result.BuildAnnouncement()}");
    }

    /// <summary>
    /// Reads the flattened figure's alternate text through the editor's own extraction path, so the
    /// test proves what the READER will actually announce rather than merely what was written.
    /// </summary>
    private static string? ReadAltThroughExtractor(string path)
    {
        var loaded = new PdfPigDocumentLoader().Load(path);

        return loaded.Document?.Figures
            .Select(f => f.AlternateText)
            .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
    }

    /// <summary>Creates a PDF with both a text field and a signature field.</summary>
    private static string CreateFormWithTextAndSignature()
    {
        string path = Path.Combine(Path.GetTempPath(), $"apde-mixed-{Guid.NewGuid():N}.pdf");

        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);

        var helvetica = new PdfDictionary(document);
        helvetica.Elements.SetName("/Type", "Font");
        helvetica.Elements.SetName("/Subtype", "Type1");
        helvetica.Elements.SetName("/BaseFont", "Helvetica");
        helvetica.Elements.SetName("/Encoding", "WinAnsiEncoding");
        document.Internals.AddObject(helvetica);

        var text = new PdfDictionary(document);
        text.Elements.SetName("/Type", "Annot");
        text.Elements.SetName("/Subtype", "Widget");
        text.Elements.SetName("/FT", "Tx");
        text.Elements.SetString("/T", "fullName");
        text.Elements.SetString("/TU", "Full name");
        text.Elements.SetString("/DA", "/Helv 10 Tf 0 g");
        text.Elements.SetValue("/Rect", new PdfArray(document,
            new PdfInteger(60), new PdfInteger(700), new PdfInteger(360), new PdfInteger(722)));
        document.Internals.AddObject(text);
        text.Elements.SetReference("/P", page);

        var signature = new PdfDictionary(document);
        signature.Elements.SetName("/Type", "Annot");
        signature.Elements.SetName("/Subtype", "Widget");
        signature.Elements.SetName("/FT", "Sig");
        signature.Elements.SetString("/T", "signature1");
        signature.Elements.SetString("/TU", "Sign here");
        signature.Elements.SetValue("/Rect", new PdfArray(document,
            new PdfInteger(60), new PdfInteger(120), new PdfInteger(300), new PdfInteger(190)));
        document.Internals.AddObject(signature);
        signature.Elements.SetReference("/P", page);

        var annotations = new PdfArray(document);
        annotations.Elements.Add(text.Reference!);
        annotations.Elements.Add(signature.Reference!);
        page.Elements.SetValue("/Annots", annotations);

        var fonts = new PdfDictionary(document);
        fonts.Elements.SetReference("/Helv", helvetica);

        var resources = new PdfDictionary(document);
        resources.Elements.SetValue("/Font", fonts);

        var fields = new PdfArray(document);
        fields.Elements.Add(text.Reference!);
        fields.Elements.Add(signature.Reference!);

        var acroForm = new PdfDictionary(document);
        acroForm.Elements.SetValue("/Fields", fields);
        acroForm.Elements.SetString("/DA", "/Helv 10 Tf 0 g");
        acroForm.Elements.SetValue("/DR", resources);
        acroForm.Elements.SetInteger("/SigFlags", 3);

        document.Internals.AddObject(acroForm);
        document.Internals.Catalog.Elements.SetReference("/AcroForm", acroForm);

        document.Save(path);
        return path;
    }

    /// <summary>Finds the signature field's widget dictionary in an open document.</summary>
    private static PdfDictionary? FindSignatureWidget(PdfDocument document)
    {
        var fields = document.Internals.Catalog.Elements
            .GetDictionary("/AcroForm")?.Elements.GetArray("/Fields");

        if (fields is null)
            return null;

        for (int i = 0; i < fields.Elements.Count; i++)
        {
            var field = fields.Elements[i] is PdfReference reference
                ? reference.Value as PdfDictionary
                : fields.Elements[i] as PdfDictionary;

            if (field?.Elements.GetName("/FT") == "/Sig")
                return field;
        }

        return null;
    }

    #endregion

    #region Clean up

    private static void CleanUp(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            string stem = Path.GetFileNameWithoutExtension(path);

            if (directory is null)
                return;

            foreach (string stray in Directory.EnumerateFiles(directory, $"{stem}*"))
                File.Delete(stray);
        }
        catch
        {
            // Temporary files left behind are untidy, not a test failure.
        }
    }

    #endregion
}
