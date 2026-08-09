using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Editing;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Model.Forms;

namespace AccessiblePdfEditor.Tests;

// =====================================================================================
//  FormOperationTests.cs
//
//  Tests for clearing a form, signing, and the audio that makes the signature pad usable.
//
//  Two of these guard against a specific class of harm rather than a bug: a button that
//  reports success without doing anything, and a signature applied by a slip of the mouse.
//  Both are invisible to someone who cannot see the page, which is exactly why they need
//  to be caught by a test rather than by the user noticing.
// =====================================================================================

internal static class FormOperationTests
{
    public static void Register(TestRunner t)
    {
        RegisterReset(t);
        RegisterButtonHonesty(t);
        RegisterSignatureMark(t);
        RegisterSigning(t);
        RegisterPositionAudio(t);
    }

    #region A form to work on

    private static PdfDocumentModel BuildFilledForm()
    {
        var root = new DocumentRootElement("Form");
        var document = new PdfDocumentModel("C:\\form.pdf", root);

        var page = new PageElement(1, 595, 842);
        root.AddChild(page);

        var name = new TextFormField(1, "fullName") { ToolTip = "Full name" };
        name.ApplyLoadedValue("Thomas Anderson");
        page.AddChild(name);

        var agree = new CheckBoxFormField(1, "agree") { ToolTip = "I agree" };
        agree.ApplyLoadedValue("on");
        page.AddChild(agree);

        var reference = new TextFormField(1, "reference") { ToolTip = "Reference" };
        reference.ApplyLoadedStates(FieldStates.ReadOnly);
        reference.ApplyLoadedValue("REF-2026-001");
        page.AddChild(reference);

        var empty = new TextFormField(1, "notes") { ToolTip = "Notes" };
        page.AddChild(empty);

        page.AddChild(new SignatureFormField(1, "signature") { ToolTip = "Signature" });

        document.RebuildReadingOrder();
        return document;
    }

    #endregion

    #region Clearing a form

    private static void RegisterReset(TestRunner t)
    {
        t.Group("clearing a form");

        t.Test("clearing empties the filled fields", () =>
        {
            var document = BuildFilledForm();
            var history = new EditHistory(document);

            history.Do(new ResetFormCommand(document));

            var name = document.FormFields.OfType<TextFormField>().First(f => f.PartialName == "fullName");
            t.IsFalse(name.HasValue, "the name field should be emptied");
        });

        t.Test("clearing leaves read-only fields alone", () =>
        {
            // They are not the user's to clear, and emptying one would change a value the document
            // deliberately locked.
            var document = BuildFilledForm();
            var history = new EditHistory(document);

            history.Do(new ResetFormCommand(document));

            var reference = document.FormFields.OfType<TextFormField>()
                .First(f => f.PartialName == "reference");

            t.IsTrue(reference.HasValue, "a read-only field must keep its value");
            t.AreEqual("REF-2026-001", reference.Value, "and keep it unchanged");
        });

        t.Test("clearing counts only the fields that held something", () =>
        {
            var document = BuildFilledForm();
            var command = new ResetFormCommand(document);

            // Name and checkbox are filled; the read-only one is excluded, the empty one has
            // nothing to clear, and the signature holds no value.
            t.AreEqual(2, command.FieldsWithValues, "two fields hold a clearable value");
        });

        t.Test("undo puts every value back exactly", () =>
        {
            // The whole reason clearing is allowed to be a single keystroke.
            var document = BuildFilledForm();
            var history = new EditHistory(document);

            history.Do(new ResetFormCommand(document));
            history.Undo();

            var name = document.FormFields.OfType<TextFormField>().First(f => f.PartialName == "fullName");
            var agree = document.FormFields.OfType<CheckBoxFormField>().First();

            t.AreEqual("Thomas Anderson", name.Value, "the name should come back");
            t.IsTrue(agree.IsChecked, "the checkbox should come back ticked");
        });

        t.Test("clearing an untouched form refuses rather than pretending", () =>
        {
            var root = new DocumentRootElement("Empty");
            var document = new PdfDocumentModel("C:\\empty.pdf", root);
            var page = new PageElement(1, 595, 842);
            root.AddChild(page);
            page.AddChild(new TextFormField(1, "notes") { ToolTip = "Notes" });
            document.RebuildReadingOrder();

            var command = new ResetFormCommand(document);
            t.AreEqual(0, command.FieldsWithValues, "nothing is filled in");

            var result = command.Apply(document);
            t.IsFalse(result.Succeeded, "there is nothing to clear");
            t.Says(result.Message, "nothing to clear");
        });

        t.Test("the description says how many fields were cleared", () =>
        {
            var document = BuildFilledForm();
            t.Says(new ResetFormCommand(document).Description, "2");
        });
    }

    #endregion

    #region Buttons telling the truth

    private static void RegisterButtonHonesty(TestRunner t)
    {
        t.Group("buttons that cannot act say so");

        t.Test("an import button does not claim to have imported", () =>
        {
            // It used to report success while doing nothing. For a user who cannot see the form,
            // a false success is worse than a plain refusal: they would believe their data loaded.
            var button = new PushButtonFormField(1, "import", ButtonAction.ImportData)
            {
                Caption = "Load my details",
            };

            var host = new StubHost { ConfirmAnswer = true };
            var result = button.Activate(host);

            t.IsFalse(result.IsSuccess, "importing is not carried out");
            t.Says(result.Message, "does not do that");
        });

        t.Test("a print button does not claim to have printed", () =>
        {
            var button = new PushButtonFormField(1, "print", ButtonAction.Print);
            var result = button.Activate(new StubHost { ConfirmAnswer = true });

            t.IsFalse(result.IsSuccess, "printing is not carried out");
            t.Says(result.Message, "does not print");
        });

        t.Test("a reset button reaching the field directly reports failure, not success", () =>
        {
            // The window intercepts reset before it gets here. If that interception is ever removed
            // this must fail loudly rather than silently doing nothing.
            var button = new PushButtonFormField(1, "reset", ButtonAction.ResetForm);
            var result = button.Activate(new StubHost { ConfirmAnswer = true });

            t.IsFalse(result.IsSuccess, "the field itself cannot clear the form");
            t.Says(result.Message, "Clear this form");
        });

        t.Test("a script button is refused and explains why", () =>
        {
            var button = new PushButtonFormField(1, "calc", ButtonAction.RunJavaScript);

            t.IsFalse(button.CanActivate, "scripts are never run");

            var result = button.Activate(new StubHost());
            t.Says(result.Message, "does not run scripts");
        });

        t.Test("a reset button warns about losing everything typed", () =>
        {
            var button = new PushButtonFormField(1, "reset", ButtonAction.ResetForm);
            var host = new StubHost { ConfirmAnswer = false };

            button.Activate(host);

            t.Says(host.LastQuestion ?? string.Empty, "everything you have typed");
        });
    }

    #endregion

    #region The captured signature

    private static void RegisterSignatureMark(TestRunner t)
    {
        t.Group("signature capture");

        t.Test("a typed name is usable", () =>
        {
            var mark = SignatureMark.FromTypedName("Thomas Anderson");

            t.IsTrue(mark.IsUsable, "a typed name can be drawn");
            t.Says(mark.Describe(), "Thomas Anderson");
        });

        t.Test("an image that does not exist is not usable", () =>
        {
            var mark = SignatureMark.FromImage("C:\\nowhere\\missing.png", "Someone");
            t.IsFalse(mark.IsUsable, "a missing file cannot be drawn");
        });

        t.Test("a stray dot is recognised as too small to be a signature", () =>
        {
            // A sighted user sees this instantly. A blind user would sign a document with it.
            var stroke = new SignatureStroke();
            stroke.Add(0.50, 0.50);
            stroke.Add(0.51, 0.50);

            var mark = SignatureMark.FromStrokes([stroke], "Someone");

            t.IsTrue(mark.IsSuspiciouslySmall, "a two-point stroke is not a signature");
        });

        t.Test("a real signature is not flagged as too small", () =>
        {
            var stroke = new SignatureStroke();
            for (int i = 0; i <= 20; i++)
                stroke.Add(0.1 + i * 0.04, 0.5 + Math.Sin(i) * 0.1);

            var mark = SignatureMark.FromStrokes([stroke], "Someone");

            t.IsFalse(mark.IsSuspiciouslySmall, "a stroke across most of the pad is a signature");
            t.IsTrue(mark.DrawnExtent > 0.5, "and it should measure as wide");
        });

        t.Test("stroke points are clamped to the drawing area", () =>
        {
            // The pointer can leave the pad mid-stroke, and a point outside 0 to 1 would scale to
            // somewhere outside the signature field on the page.
            var stroke = new SignatureStroke();
            stroke.Add(-0.5, 1.8);

            t.AreEqual(0.0, stroke.Points[0].X, "X should be clamped to 0");
            t.AreEqual(1.0, stroke.Points[0].Y, "Y should be clamped to 1");
        });

        t.Test("single-point strokes are dropped", () =>
        {
            var dot = new SignatureStroke();
            dot.Add(0.5, 0.5);

            var mark = SignatureMark.FromStrokes([dot], "Someone");
            t.AreEqual(0, mark.Strokes.Count, "a stroke with one point cannot be drawn");
        });

        t.Test("the description says which way the signature was made", () =>
        {
            var stroke = new SignatureStroke();
            stroke.Add(0.1, 0.5);
            stroke.Add(0.9, 0.5);

            t.Says(SignatureMark.FromStrokes([stroke], "A").Describe(), "drew");
            t.Says(SignatureMark.FromTypedName("A").Describe(), "drawn as text");
        });
    }

    #endregion

    #region Signing a field

    private static void RegisterSigning(TestRunner t)
    {
        t.Group("signing");

        t.Test("placing a signature marks the field as signed but unsaved", () =>
        {
            var document = BuildFilledForm();
            var history = new EditHistory(document);
            var field = document.FormFields.OfType<SignatureFormField>().First();

            history.Do(new ApplySignatureCommand(field, SignatureMark.FromTypedName("Thomas Anderson")));

            t.IsTrue(field.HasPendingSignature, "the signature should be placed");
            t.Says(field.ValueForSpeech, "not yet saved");
        });

        t.Test("undo removes a placed signature", () =>
        {
            // The edit a user is least able to check for themselves, so being able to take it back
            // matters more here than anywhere else.
            var document = BuildFilledForm();
            var history = new EditHistory(document);
            var field = document.FormFields.OfType<SignatureFormField>().First();

            history.Do(new ApplySignatureCommand(field, SignatureMark.FromTypedName("Thomas Anderson")));
            var undone = history.Undo();

            t.IsTrue(undone.Succeeded, "it should be undoable");
            t.IsFalse(field.HasPendingSignature, "the signature should be gone");
            t.Says(field.ValueForSpeech, "not signed");
        });

        t.Test("an unusable signature is refused", () =>
        {
            var document = BuildFilledForm();
            var field = document.FormFields.OfType<SignatureFormField>().First();

            var command = new ApplySignatureCommand(field,
                SignatureMark.FromImage("C:\\nowhere\\missing.png", "Someone"));

            var result = command.Apply(document);

            t.IsFalse(result.Succeeded, "a missing image cannot be applied");
        });

        t.Test("an already-signed field still cannot be typed into", () =>
        {
            var field = new SignatureFormField(1, "sig") { ToolTip = "Signature" };
            field.MarkSigned();

            t.IsFalse(field.CanActivate, "an existing signature is not replaced");
            t.IsFalse(field.TrySetValue("me").Accepted, "a signature is not a text field");
        });

        t.Test("the placed signature says it is visible, not cryptographic", () =>
        {
            // The distinction a blind user cannot check for themselves by inspecting properties.
            var document = BuildFilledForm();
            var field = document.FormFields.OfType<SignatureFormField>().First();

            var result = new ApplySignatureCommand(field, SignatureMark.FromTypedName("A"))
                .Apply(document);

            t.Says(result.Message, "not a cryptographic");
        });
    }

    #endregion

    #region Position audio
    // The channel that makes the signature pad usable. Speech cannot do this: by the time a
    // coordinate had been read out, the pointer would have moved.

    private static void RegisterPositionAudio(TestRunner t)
    {
        t.Group("position audio");

        t.Test("pitch rises as the pointer moves right", () =>
        {
            var cues = new SilentAudioCueService();

            // The same mapping the pad uses: horizontal sets the note, vertical the octave.
            PlayAt(cues, 0.1, 0.5);
            PlayAt(cues, 0.9, 0.5);

            t.AreEqual(2, cues.Tones.Count, "both positions should have sounded");
            t.IsTrue(cues.Tones[1].Frequency > cues.Tones[0].Frequency,
                "further right should be higher");
        });

        t.Test("pitch rises as the pointer moves up", () =>
        {
            var cues = new SilentAudioCueService();

            // Screen coordinates run downwards, so a smaller Y is higher up the pad.
            PlayAt(cues, 0.5, 0.9);
            PlayAt(cues, 0.5, 0.1);

            t.IsTrue(cues.Tones[1].Frequency > cues.Tones[0].Frequency,
                "higher up should be higher in pitch");
        });

        t.Test("frequencies stay in a comfortable hearing range", () =>
        {
            // A tone that is painfully high or inaudibly low conveys nothing and would make the pad
            // unpleasant to use for the minutes a signature takes.
            var cues = new SilentAudioCueService();

            foreach (double x in new[] { 0.0, 0.5, 1.0 })
            {
                foreach (double y in new[] { 0.0, 0.5, 1.0 })
                    PlayAt(cues, x, y);
            }

            foreach (var tone in cues.Tones)
            {
                t.IsTrue(tone.Frequency is >= 200 and <= 1000,
                    $"{tone.Frequency:0} Hz should be comfortable to listen to");
            }
        });

        t.Test("an absurd frequency is clamped rather than played", () =>
        {
            var cues = new SilentAudioCueService();
            cues.PlayTone(50_000, 40);

            t.AreEqual(1, cues.Tones.Count, "the tone should still play");
            t.IsTrue(cues.Tones[0].Frequency <= 6000, "but at an audible pitch");
        });

        t.Test("tones are silent when cues are switched off", () =>
        {
            var cues = new SilentAudioCueService { IsEnabled = false };
            cues.PlayTone(440, 40);

            t.AreEqual(0, cues.Tones.Count, "nothing should sound when cues are off");
        });

        /// <summary>Mirrors the mapping in SignaturePadControl.SoundNormalisedPosition.</summary>
        static void PlayAt(IAudioCueService cues, double x, double y)
        {
            double horizontal = Math.Clamp(x, 0, 1);
            double vertical = 1 - Math.Clamp(y, 0, 1);

            cues.PlayTone(220 * Math.Pow(2, horizontal + vertical), 45, 0.4);
        }
    }

    #endregion

    #region A stand-in for the application shell

    /// <summary>
    /// An interaction host that records what it was asked and answers however the test wants. Lets
    /// the confirmation wording be asserted on, which is where most of the safety in this program
    /// actually lives.
    /// </summary>
    private sealed class StubHost : IInteractionHost
    {
        public bool ConfirmAnswer { get; init; }

        public string? LastQuestion { get; private set; }

        public List<string> Announcements { get; } = [];

        public bool Confirm(string question)
        {
            LastQuestion = question;
            return ConfirmAnswer;
        }

        public void NavigateTo(DocumentElement target) { }

        public void NavigateToPage(int pageNumber) { }

        public bool OpenExternal(string target) => true;

        public void Announce(string message, AnnouncementPriority priority) =>
            Announcements.Add(message);
    }

    #endregion
}
