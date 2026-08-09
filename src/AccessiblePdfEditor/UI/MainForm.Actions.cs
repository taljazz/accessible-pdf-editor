using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Auditing;
using AccessiblePdfEditor.Editing;
using AccessiblePdfEditor.Ingestion;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Model.Forms;
using AccessiblePdfEditor.Navigation;
using AccessiblePdfEditor.Persistence;

namespace AccessiblePdfEditor.UI;

// =====================================================================================
//  MainForm.Actions.cs
//
//  Everything the menus and keys actually do.
//
//  Split from MainForm.cs, which holds the window, the menu and the keyboard, so that
//  neither file becomes too long to hold in your head.
//
//  One rule runs through every action here: NOTHING HAPPENS SILENTLY. Every command
//  announces its outcome, including — especially — the outcome "nothing happened, and here
//  is why". A key that appears to do nothing is the single most disorienting thing a
//  keyboard-driven program can do, because the user cannot tell a no-op from a crash from a
//  misheard keystroke, and their only recourse is to press it again.
// =====================================================================================

public sealed partial class MainForm : IInteractionHost
{
    #region Opening a document

    /// <summary>
    /// Opens the document named on the command line, if there was one.
    ///
    /// Runs from Shown rather than from the constructor, so the window exists to host the dialogs a
    /// difficult file may need — a password prompt, a warning that the structure is unreadable —
    /// and so the opening announcement has already said what is happening.
    /// </summary>
    private void OpenStartupDocument()
    {
        if (_startupDocumentPath is not { Length: > 0 } path)
            return;

        // Cleared first, so a failure does not leave the program trying again on some later event.
        _startupDocumentPath = null;

        if (!File.Exists(path))
        {
            Play(AudioCue.Error);
            Speech.BeginNewAnnouncement();

            Announce(
                $"{Path.GetFileName(path)} could not be found, so nothing was opened. " +
                "Press Control plus O to choose a document.",
                AnnouncementPriority.Assertive);

            return;
        }

        LoadDocument(path);
    }

    private void OpenDocument()
    {
        using var chooser = new OpenFileDialog
        {
            Title = "Open a PDF",
            Filter = "PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (chooser.ShowDialog(this) != DialogResult.OK)
        {
            Announce("Nothing opened.");
            return;
        }

        LoadDocument(chooser.FileName);
    }

    /// <summary>
    /// Loads a file, prompting for a password if it needs one.
    ///
    /// The password loop is here rather than in the loader because asking is a user-interface act.
    /// The loader reports that a password is needed; this decides how to ask and how many times.
    /// </summary>
    private void LoadDocument(string path, string? password = null)
    {
        Play(AudioCue.WorkStarted);
        Announce($"Opening {Path.GetFileName(path)}.", AnnouncementPriority.Assertive);
        UpdateStatus($"Opening {Path.GetFileName(path)}…");

        Cursor = Cursors.WaitCursor;
        DocumentLoadResult result;

        try
        {
            result = _loader.Load(path, password);
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        switch (result.State)
        {
            case DocumentLoadState.PasswordRequired:
                PromptForPassword(path);
                return;

            case DocumentLoadState.Failed:
                Play(AudioCue.Error);
                ReportProblem(result.Message, "Could not open");
                UpdateStatus("No document open");
                return;
        }

        if (result.Document is null)
            return;

        AdoptDocument(result.Document);
    }

    private void PromptForPassword(string path)
    {
        Play(AudioCue.Warning);

        string? password = TextPromptDialog.Ask(this, Speech, Cues,
            "Password needed",
            $"{Path.GetFileName(path)} is password protected. Password:",
            "The password is not stored anywhere and is used only to open this document now.");

        if (string.IsNullOrEmpty(password))
        {
            Announce("Nothing opened.", AnnouncementPriority.Assertive);
            return;
        }

        LoadDocument(path, password);
    }

    /// <summary>Takes ownership of a freshly loaded document and sets up everything that hangs off it.</summary>
    private void AdoptDocument(PdfDocumentModel document)
    {
        _document = document;
        _history = new EditHistory(document);
        _navigation.Attach(document);
        _search.Clear();
        _lastReport = null;

        _settings.RecordRecentFile(document.FilePath);

        // Its own handle on the file, opened only if the picture pane is actually in use. Nobody
        // should pay to prepare a rendering they have switched off.
        if (_settings.ShowPagePicture)
            _renderer.Open(document.FilePath);

        RenderDocument();
        UpdateStatus();

        // Starting the web view is deferred until a document is actually open, so an empty program
        // does not pay for a browser process it has nothing to show in.
        _ = EnsureBrowseViewReadyAsync();

        Play(AudioCue.WorkFinished);

        Speech.BeginNewAnnouncement();
        Announce(document.BuildOpeningAnnouncement(), AnnouncementPriority.Assertive);

        // Which reading view they have landed in, and therefore whose keys are about to work. A
        // user who is not told has no way of knowing why a command did or did not do anything.
        Announce(DescribeReadingSurface());

        // Load problems are worth hearing: a document that loaded with fonts missing is one whose
        // text may be wrong, and the user should learn that from the program rather than by
        // puzzling over garbled words.
        if (document.LoadWarnings.Count > 0)
        {
            Announce($"{document.LoadWarnings.Count} problems were found while reading the file. " +
                     "Press Control plus D to see them.");
        }

        if (_settings.AuditOnOpen)
            RunAudit(announceSummary: _settings.AnnounceAuditOnOpen);

        ReadingSurface.Focus();
    }

    /// <summary>Rebuilds the text surface from the model. Called after loading and after any edit.</summary>
    private void RenderDocument()
    {
        if (_document is null)
        {
            _rendered = null;
            _documentView.Text = "No document is open. Press Control plus O to open one.";
            RenderBrowseView();
            return;
        }

        _rendered = DocumentTextRenderer.Render(
            _document, _settings.ReadingMode, _settings.ShowRoleLabelsInText);

        // Both surfaces are rebuilt from the same model at the same moment, so they can never
        // disagree about what the document says.
        RenderBrowseView();

        _movingCaret = true;

        try
        {
            _documentView.Text = _rendered.Text;
            _documentView.Select(0, 0);
        }
        finally
        {
            _movingCaret = false;
        }

        // The user's place is held by element id, so an edit that rebuilt the tree does not move
        // them. Losing your position in a long document because you fixed an alt text would make
        // remediation unbearable.
        _navigation.Reattach();

        if (_navigation.Current is { } current)
            MoveCaretTo(current);
    }

    #endregion

    #region Saving

    private void SaveDocument(bool saveAs)
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        if (!saveAs && !_document.HasUnsavedChanges)
        {
            Play(AudioCue.Boundary);
            Announce("There is nothing to save. No changes have been made.", AnnouncementPriority.Assertive);
            return;
        }

        string? target = _document.FilePath;

        if (saveAs)
        {
            using var chooser = new SaveFileDialog
            {
                Title = "Save a copy as",
                Filter = "PDF documents (*.pdf)|*.pdf",
                FileName = Path.GetFileNameWithoutExtension(_document.FilePath) + " (edited).pdf",
                InitialDirectory = Path.GetDirectoryName(_document.FilePath),
            };

            if (chooser.ShowDialog(this) != DialogResult.OK)
            {
                Announce("Not saved.");
                return;
            }

            target = chooser.FileName;
        }

        // Summarised before saving, so the user hears what they are committing while they can still
        // change their mind.
        if (_history is { AppliedCount: > 0 })
            Announce($"Saving. {_history.SummariseChanges()}");

        PerformSave(target, saveAs, allowStructureLoss: false);
    }

    private void PerformSave(string? target, bool saveAs, bool allowStructureLoss)
    {
        if (_document is null)
            return;

        Play(AudioCue.WorkStarted);
        Cursor = Cursors.WaitCursor;

        SaveResult result;

        try
        {
            result = _saver.Save(_document, new SaveOptions
            {
                TargetPath = saveAs ? target : null,
                CreateBackup = _settings.CreateBackupOnSave,
                VerifyAfterWriting = _settings.VerifySaves,
                AllowStructureLoss = allowStructureLoss,
            });
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        // A refusal is not a failure. The user is offered the two ways forward that keep their work
        // safe — save a copy, or accept the loss knowingly — rather than being left with a dead end.
        if (result.Outcome == SaveOutcome.Cancelled && !allowStructureLoss)
        {
            HandleRefusedSave(result);
            return;
        }

        if (result.IsSuccess)
        {
            Play(AudioCue.Saved);
            _history?.Clear();
            UpdateStatus();
        }
        else
        {
            Play(AudioCue.Error);
        }

        Speech.BeginNewAnnouncement();
        Announce(result.BuildAnnouncement(), AnnouncementPriority.Assertive);
    }

    /// <summary>
    /// Handles a save refused because it would destroy accessibility structure.
    ///
    /// The safe option is offered first and made the default, because it is the one that cannot go
    /// wrong: saving a copy leaves the original intact whatever happens.
    /// </summary>
    private void HandleRefusedSave(SaveResult result)
    {
        Play(AudioCue.Warning);
        Speech.BeginNewAnnouncement();
        Announce(result.Message, AnnouncementPriority.Assertive);

        var choice = MessageBox.Show(this,
            result.Message + "\r\n\r\n" +
            "Yes — save as a new copy instead, leaving this file untouched.\r\n" +
            "No — save anyway and accept losing the tags.\r\n" +
            "Cancel — do nothing.",
            "Saving would remove accessibility information",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button1);

        switch (choice)
        {
            case DialogResult.Yes:
                SaveDocument(saveAs: true);
                break;

            case DialogResult.No:
                if (Confirm(
                        "This will permanently remove the document's accessibility tags. " +
                        "The saved file will be harder to read with a screen reader than the " +
                        "original. Are you sure?"))
                {
                    PerformSave(null, saveAs: false, allowStructureLoss: true);
                }
                else
                {
                    Announce("Not saved. Your file is unchanged.", AnnouncementPriority.Assertive);
                }

                break;

            default:
                Announce("Not saved. Your file is unchanged.", AnnouncementPriority.Assertive);
                break;
        }
    }

    #endregion

    #region Navigating

    private void Navigate(
        NavigationGranularity granularity,
        MoveDirection direction,
        HeadingLevel headingLevel = HeadingLevel.None)
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        var result = _navigation.Move(granularity, direction, _settings.Verbosity, headingLevel);

        Play(result.Cue);

        if (result.Moved && result.Element is { } element)
        {
            MoveCaretTo(element);
            SyncPagePicture(element);
            SyncBrowseView(element);
        }

        Speech.BeginNewAnnouncement();
        Announce(result.Announcement, AnnouncementPriority.Assertive);
    }

    private void GoToPagePrompt()
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        string? typed = TextPromptDialog.Ask(this, Speech, Cues,
            "Go to page",
            $"Page number, 1 to {_document.PageCount}:",
            $"You are on page {_navigation.CurrentPage}.",
            validate: value =>
                int.TryParse(value, out int page) && page >= 1 && page <= _document.PageCount
                    ? null
                    : $"Enter a number between 1 and {_document.PageCount}.");

        if (typed is null || !int.TryParse(typed, out int target))
            return;

        var result = _navigation.GoToPage(target, _settings.Verbosity);
        Play(result.Cue);

        if (result.Element is { } element)
            MoveCaretTo(element);

        Announce(result.Announcement, AnnouncementPriority.Assertive);
    }

    private void AnnouncePosition()
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        Speech.BeginNewAnnouncement();
        Announce(_navigation.DescribePosition(), AnnouncementPriority.Assertive);
    }

    private void AnnounceCurrent()
    {
        if (_navigation.Current is not { } current)
        {
            AnnounceNoDocument();
            return;
        }

        Speech.BeginNewAnnouncement();
        Announce(current.Describe(_settings.Verbosity), AnnouncementPriority.Assertive);
    }

    /// <summary>
    /// Steps the on-screen text size up or down, and says the new size. Announced as well as shown,
    /// because someone with partial sight may be changing it precisely because they cannot read the
    /// screen well enough to tell whether it worked.
    /// </summary>
    /// <summary>
    /// Zooms whichever pane has focus: the text if the user is reading text, the picture if they
    /// are looking at the picture. One key, doing the obvious thing for wherever you are.
    /// </summary>
    private void AdjustZoom(bool larger)
    {
        if (!_split.Panel2Collapsed && _pageView.Focused)
        {
            Play(AudioCue.ValueAccepted);
            Speech.BeginNewAnnouncement();
            Announce(_pageView.AdjustZoom(larger), AnnouncementPriority.Assertive);
            return;
        }

        AdjustTextSize(larger);
    }

    private void AdjustTextSize(bool larger)
    {
        float[] sizes = [9f, 11f, 14f, 18f, 24f, 32f];

        int current = Array.FindIndex(sizes, s => Math.Abs(s - _settings.TextSizePoints) < 0.01f);
        if (current < 0) current = 1;

        int next = Math.Clamp(current + (larger ? 1 : -1), 0, sizes.Length - 1);

        if (next == current)
        {
            Play(AudioCue.Boundary);
            Announce(larger
                ? $"Already at the largest size, {_settings.TextSizePoints:0} point."
                : $"Already at the smallest size, {_settings.TextSizePoints:0} point.",
                AnnouncementPriority.Assertive);
            return;
        }

        _settings.TextSizePoints = sizes[next];
        _settings.Save();
        ApplyTextSize();

        Play(AudioCue.ValueAccepted);
        Announce($"Text size {_settings.TextSizePoints:0} point.", AnnouncementPriority.Assertive);
    }

    private void CycleVerbosity()
    {
        string message = _settings.CycleVerbosity();
        _settings.Save();

        Play(AudioCue.Success);
        Speech.BeginNewAnnouncement();
        Announce(message, AnnouncementPriority.Assertive);
    }

    #endregion

    #region Activating what the position is on

    /// <summary>
    /// Activates the current element: follows a link, edits a field, reads a comment. One key for
    /// all of them, because the user should not have to know what kind of thing they are on before
    /// they can use it.
    /// </summary>
    private void ActivateCurrent()
    {
        if (_navigation.Current is not { } current)
        {
            AnnounceNoDocument();
            return;
        }

        // Fields are edited through the prompt rather than through their own Activate, because
        // typing a value needs a dialog and the model deliberately knows nothing about dialogs.
        if (current is PdfFormField field)
        {
            EditField(field);
            return;
        }

        // A table opens into a real grid, where the screen reader's own table commands work. Text
        // laid out to look like a table is still text as far as assistive technology is concerned.
        if (current is TableElement or TableRowElement or TableCellElement)
        {
            OpenTableView();
            return;
        }

        if (current is not InteractiveElement interactive)
        {
            Play(AudioCue.Boundary);
            Announce("There is nothing to activate here.", AnnouncementPriority.Assertive);
            return;
        }

        var result = interactive.Activate(this);

        Play(result.IsSuccess ? AudioCue.Success : AudioCue.Rejected);
        Speech.BeginNewAnnouncement();
        Announce(result.Message, AnnouncementPriority.Assertive);
    }

    /// <summary>Edits one form field, through a prompt suited to its type.</summary>
    private void EditField(PdfFormField field)
    {
        if (_document is null || _history is null)
            return;

        if (field.IsReadOnly)
        {
            Play(AudioCue.Boundary);
            Announce($"{field.Label} is read-only and cannot be changed.", AnnouncementPriority.Assertive);
            return;
        }

        switch (field)
        {
            case CheckBoxFormField checkbox:
                ApplyEdit(new SetFieldValueCommand(checkbox, checkbox.IsChecked ? "off" : "on"));
                return;

            case RadioGroupFormField radio:
                EditRadioGroup(radio);
                return;

            case ChoiceFormField choice when choice.Options.Count > 0:
                EditChoice(choice);
                return;

            // Reset is intercepted before the button's own Activate, because clearing the form is
            // an operation over every field and no single field can carry it out.
            case PushButtonFormField { Action: ButtonAction.ResetForm } resetButton:
                ResetForm(resetButton.Caption ?? resetButton.Label);
                return;

            case SignatureFormField signature:
                SignDocument(signature);
                return;

            case PushButtonFormField or SignatureFormField:
            {
                var result = field.Activate(this);
                Play(result.IsSuccess ? AudioCue.Success : AudioCue.Rejected);
                Announce(result.Message, AnnouncementPriority.Assertive);
                return;
            }
        }

        var text = field as TextFormField;

        string? typed = TextPromptDialog.Ask(this, Speech, Cues,
            $"Fill in: {field.Label}",
            $"{field.Label}:",
            BuildFieldGuidance(field),
            initialValue: text?.Value ?? string.Empty,
            multiline: text?.IsMultiline ?? false,
            validate: value =>
            {
                // Validated against the field's own rule before the dialog closes, so a rejection
                // is heard while the user is still in the box and can correct it.
                var probe = field.TrySetValue(value);

                if (probe.Accepted)
                {
                    // Put back: the real change goes through the command so it can be undone.
                    field.ApplyLoadedValue(text?.Value ?? string.Empty);
                    return null;
                }

                return probe.Message;
            });

        if (typed is null)
        {
            Announce($"{field.Label} left unchanged.");
            return;
        }

        ApplyEdit(new SetFieldValueCommand(field, typed));
    }

    private static string BuildFieldGuidance(PdfFormField field)
    {
        var parts = new List<string>(3);

        if (field.IsRequired)
            parts.Add("This field is required.");

        if (field.InputGuidance is { Length: > 0 } guidance)
            parts.Add(char.ToUpperInvariant(guidance[0]) + guidance[1..] + ".");

        if (field is TextFormField { MaxLength: { } max })
            parts.Add($"At most {max} characters.");

        return string.Join(" ", parts);
    }

    private void EditRadioGroup(RadioGroupFormField group)
    {
        var chosen = ListSelectionDialog<RadioOption>.Choose(this, Speech, Cues,
            $"Choose: {group.Label}",
            group.IsRequired ? "This choice is required." : "Choose one option.",
            group.Options,
            option => option.SpokenLabel,
            option => option.ExportValue == group.SelectedExportValue
                ? "Currently selected"
                : string.Empty,
            actionButtonText: "&Choose");

        if (chosen is null)
        {
            Announce($"{group.Label} left unchanged.");
            return;
        }

        ApplyEdit(new SetFieldValueCommand(group, chosen.ExportValue));
    }

    private void EditChoice(ChoiceFormField choice)
    {
        var chosen = ListSelectionDialog<ChoiceOption>.Choose(this, Speech, Cues,
            $"Choose: {choice.Label}",
            choice.AllowsMultipleSelection
                ? "More than one may be chosen; choose them one at a time."
                : "Choose one option.",
            choice.Options,
            option => option.SpokenText,
            option => choice.SelectedExportValues.Contains(option.ExportValue)
                ? "Currently selected"
                : string.Empty,
            actionButtonText: "&Choose");

        if (chosen is null)
        {
            Announce($"{choice.Label} left unchanged.");
            return;
        }

        ApplyEdit(new SetFieldValueCommand(choice, chosen.ExportValue));
    }

    #endregion

    #region Making and undoing edits

    /// <summary>
    /// Applies an edit through the history, then re-renders and announces the result. Every change
    /// in the program goes through here, so none can skip being undoable or being announced.
    /// </summary>
    private void ApplyEdit(EditCommand command)
    {
        if (_document is null || _history is null)
        {
            AnnounceNoDocument();
            return;
        }

        var result = _history.Do(command);

        if (!result.Succeeded)
        {
            Play(AudioCue.Rejected);
            Speech.BeginNewAnnouncement();
            Announce(result.Message, AnnouncementPriority.Assertive);
            return;
        }

        RenderDocument();
        UpdateStatus();

        // Repairs get their own sound, because remediation is slow repetitive work and the moment
        // something is genuinely fixed should feel like an achievement rather than another tick.
        Play(command.Kind is EditKind.AlternateText or EditKind.FormFieldLabel
            or EditKind.TableHeaders or EditKind.StructureType or EditKind.Language
            ? AudioCue.IssueFixed
            : AudioCue.ValueAccepted);

        Speech.BeginNewAnnouncement();
        Announce(result.Message, AnnouncementPriority.Assertive);

        if (command.AffectedElement is { } affected)
            MoveCaretTo(affected);
    }

    private void Undo()
    {
        if (_history is null)
        {
            AnnounceNoDocument();
            return;
        }

        var result = _history.Undo();

        if (result.Succeeded)
        {
            RenderDocument();
            UpdateStatus();
            Play(AudioCue.Undone);

            if (result.Command?.AffectedElement is { } affected)
                MoveCaretTo(affected);
        }
        else
        {
            Play(AudioCue.Boundary);
        }

        Speech.BeginNewAnnouncement();
        Announce(result.Message, AnnouncementPriority.Assertive);
    }

    private void Redo()
    {
        if (_history is null)
        {
            AnnounceNoDocument();
            return;
        }

        var result = _history.Redo();

        if (result.Succeeded)
        {
            RenderDocument();
            UpdateStatus();
            Play(AudioCue.Redone);

            if (result.Command?.AffectedElement is { } affected)
                MoveCaretTo(affected);
        }
        else
        {
            Play(AudioCue.Boundary);
        }

        Speech.BeginNewAnnouncement();
        Announce(result.Message, AnnouncementPriority.Assertive);
    }

    private void AnnounceChangeHistory()
    {
        if (_history is null)
        {
            AnnounceNoDocument();
            return;
        }

        var changes = _history.ListRecentChanges().ToList();

        if (changes.Count == 0)
        {
            Play(AudioCue.Boundary);
            Announce("No changes have been made.", AnnouncementPriority.Assertive);
            return;
        }

        ListSelectionDialog<string>.Choose(this, Speech, Cues,
            "Changes made",
            _history.SummariseChanges(),
            changes,
            change => change,
            actionButtonText: "&Close");
    }

    #endregion

    #region Finding

    private void FindText()
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        string? term = TextPromptDialog.Ask(this, Speech, Cues,
            "Find",
            "Text to find:",
            "Comments and form fields are searched as well as the page text. " +
            "Running headers and footers are not.",
            initialValue: _search.Term);

        if (string.IsNullOrWhiteSpace(term))
            return;

        string summary = _search.Search(_document, term);

        Play(_search.Matches.Count > 0 ? AudioCue.Success : AudioCue.Boundary);
        Speech.BeginNewAnnouncement();
        Announce(summary, AnnouncementPriority.Assertive);

        if (_search.Matches.Count > 0)
            FindNext(forward: true);
    }

    private void FindNext(bool forward)
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        if (_search.Matches.Count == 0)
        {
            Play(AudioCue.Boundary);
            Announce(_search.Term.Length == 0
                ? "Nothing has been searched for yet. Press Control plus F to search."
                : $"\"{_search.Term}\" was not found.", AnnouncementPriority.Assertive);
            return;
        }

        // Wrapping is announced. Silently returning to the top makes a listener think the search is
        // repeating itself, and they lose track of whether they have seen everything.
        bool wrapping = forward && _search.NextWouldWrap;

        var match = forward ? _search.Next() : _search.Previous();
        if (match is null)
            return;

        _navigation.GoToElement(match.Element, _settings.Verbosity);
        MoveCaretTo(match.Element);

        Play(AudioCue.Navigation);
        Speech.BeginNewAnnouncement();

        string announcement = wrapping
            ? $"Back to the first match. {match.Describe()}"
            : match.Describe();

        Announce(announcement, AnnouncementPriority.Assertive);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Opens the table at the reading position in a real grid control.
    ///
    /// Works from anywhere inside a table — on the table itself, on a row, or on a cell — and
    /// starts the grid at the cell the user was on, so opening it does not lose their place.
    /// </summary>
    private void OpenTableView()
    {
        if (_document is null || _history is null)
        {
            AnnounceNoDocument();
            return;
        }

        var current = _navigation.Current;

        var table = current as TableElement
            ?? current?.NearestAncestor<TableElement>();

        if (table is null)
        {
            Play(AudioCue.Boundary);
            Announce("You are not in a table. Press T to move to the next one.",
                AnnouncementPriority.Assertive);
            return;
        }

        // Where to put the cursor in the grid, so opening a table from a cell lands on that cell.
        int row = 0;
        int column = 0;

        if (current is TableCellElement cell && cell.Parent is TableRowElement parentRow)
        {
            column = Math.Max(0, cell.ColumnIndex);

            var dataRows = table.Rows.Where(r => !r.IsHeaderRow).ToList();
            row = Math.Max(0, dataRows.FindIndex(r => ReferenceEquals(r, parentRow)));
        }

        Play(AudioCue.Table);

        bool markHeadings = TableViewDialog.Show(this, Speech, Cues, table, row, column);

        if (markHeadings && table.Rows.Count > 0)
        {
            ApplyEdit(new MarkHeaderRowCommand(table.Rows[0]));
            return;
        }

        // Back in the document, on the table they were exploring.
        _navigation.GoToElement(table, _settings.Verbosity);
        MoveCaretTo(table);

        Announce("Back in the document.", AnnouncementPriority.Assertive);
    }

    private void AnnounceNoDocument()
    {
        Play(AudioCue.Boundary);
        Announce("No document is open. Press Control plus O to open one.", AnnouncementPriority.Assertive);
    }

    #endregion

    #region IInteractionHost — how model elements reach the outside world
    // The model asks for these; it never knows there is a window involved. That is what lets the
    // whole model be tested without starting WinForms.

    bool IInteractionHost.Confirm(string question) => Confirm(question);

    void IInteractionHost.NavigateTo(DocumentElement target)
    {
        var result = _navigation.GoToElement(target, _settings.Verbosity);

        if (result.Element is { } element)
            MoveCaretTo(element);
    }

    void IInteractionHost.NavigateToPage(int pageNumber)
    {
        var result = _navigation.GoToPage(pageNumber, _settings.Verbosity);

        if (result.Element is { } element)
            MoveCaretTo(element);

        Play(result.Cue);
        Announce(result.Announcement, AnnouncementPriority.Assertive);
    }

    /// <summary>
    /// Opens something outside the document. The caller has already confirmed it; this performs it.
    ///
    /// Only web, mail and existing files are handled, and each is launched through the shell rather
    /// than executed. Anything else is refused: a PDF is an untrusted document, and following its
    /// instructions to run something is not a decision this program makes for someone who cannot
    /// see what is about to happen.
    /// </summary>
    bool IInteractionHost.OpenExternal(string target)
    {
        try
        {
            if (target.StartsWith("attachment:", StringComparison.Ordinal))
                return SaveAttachment(target["attachment:".Length..]);

            if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme is not ("http" or "https" or "mailto" or "file"))
                {
                    Announce($"This editor will not open a {uri.Scheme} link.",
                        AnnouncementPriority.Assertive);
                    return false;
                }
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });

            return true;
        }
        catch (Exception ex)
        {
            Announce($"That could not be opened: {ex.Message}", AnnouncementPriority.Assertive);
            return false;
        }
    }

    private bool SaveAttachment(string fileName)
    {
        // Reported honestly rather than failing silently. Extracting embedded files needs work this
        // version has not done, and pretending otherwise would waste the user's time.
        Announce($"Saving attachments is not available in this version. The document contains a " +
                 $"file called {fileName}.", AnnouncementPriority.Assertive);

        return false;
    }

    void IInteractionHost.Announce(string message, AnnouncementPriority priority) =>
        Announce(message, priority);

    #endregion
}
