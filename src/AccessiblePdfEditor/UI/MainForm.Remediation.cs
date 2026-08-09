using System.Globalization;
using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Auditing;
using AccessiblePdfEditor.Editing;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Model.Elements;
using AccessiblePdfEditor.Model.Forms;
using AccessiblePdfEditor.Navigation;

namespace AccessiblePdfEditor.UI;

// =====================================================================================
//  MainForm.Remediation.cs
//
//  The Fix menu: checking a document's accessibility and repairing it.
//
//  This is what makes the program an editor rather than a reader, and the guided workflow
//  is its centre. Remediation is slow, repetitive work, and a tool that merely produces a
//  list of two hundred faults and leaves you to find each one yourself will be abandoned
//  before the second page. So the workflow takes the user TO each problem in turn, offers
//  the repair right there, and moves on — with an audible sense of progress, because
//  knowing you have done 40 of 60 is what gets someone to 60.
//
//  The other thing this file does is TEACH. Writing good alt text is a skill, and most
//  people have never been shown what it involves. So the prompt does not just say
//  "Description:" — it explains what makes a description useful, every time.
// =====================================================================================

public sealed partial class MainForm
{
    #region Checking accessibility

    private void RunAudit(bool announceSummary = true)
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        Play(AudioCue.WorkStarted);
        Cursor = Cursors.WaitCursor;

        try
        {
            _lastReport = _auditor.Audit(_document);
        }
        finally
        {
            Cursor = Cursors.Default;
        }

        UpdateStatus($"{_document.BuildStatusLine()} — {_lastReport.Issues.Count} accessibility problems");

        if (!announceSummary)
            return;

        Play(_lastReport.Issues.Count == 0 ? AudioCue.Success : AudioCue.IssueFound);
        Speech.BeginNewAnnouncement();

        string summary = _lastReport.BuildSummary();

        if (_lastReport.Fixable.Count > 0)
        {
            summary += " Press Control plus Shift plus F to go through them one at a time, " +
                       "or D to move to the next one.";
        }

        Announce(summary, AnnouncementPriority.Assertive);
    }

    /// <summary>Moves to the next accessibility finding in the document.</summary>
    private void GoToNextIssue(MoveDirection direction)
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        if (_lastReport is null)
        {
            RunAudit(announceSummary: false);

            if (_lastReport is null)
                return;
        }

        var located = _lastReport.Issues
            .Where(i => i.Element is not null)
            .OrderBy(i => i.Element!.ReadingOrder)
            .ToList();

        if (located.Count == 0)
        {
            Play(AudioCue.Boundary);
            Announce(_lastReport.Issues.Count == 0
                ? "No accessibility problems were found."
                : "The problems found are about the document as a whole rather than about any one " +
                  "place in it. Press Control plus Shift plus A to hear them.",
                AnnouncementPriority.Assertive);
            return;
        }

        int position = _navigation.Current?.ReadingOrder ?? -1;

        var next = direction == MoveDirection.Previous
            ? located.LastOrDefault(i => i.Element!.ReadingOrder < position)
            : located.FirstOrDefault(i => i.Element!.ReadingOrder > position);

        if (next is null)
        {
            Play(AudioCue.Boundary);
            Announce(direction == MoveDirection.Previous
                ? "No more problems before this point."
                : "No more problems after this point.", AnnouncementPriority.Assertive);
            return;
        }

        _navigation.GoToElement(next.Element!, _settings.Verbosity);
        MoveCaretTo(next.Element!);

        Play(AudioCue.IssueFound);
        Speech.BeginNewAnnouncement();
        Announce(next.Describe(VerbosityLevel.Detailed), AnnouncementPriority.Assertive);
    }

    #endregion

    #region The guided workflow — the thing that actually gets documents fixed

    /// <summary>
    /// Walks the user through every repairable finding in turn, offering the repair at each.
    ///
    /// Progress is announced as it goes, because a list of two hundred faults is demoralising
    /// while "12 of 60, and this one is an image needing a description" is a job of work.
    /// </summary>
    private void StartGuidedRemediation()
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        RunAudit(announceSummary: false);

        var fixable = _lastReport?.Fixable ?? [];

        if (fixable.Count == 0)
        {
            Play(AudioCue.Success);
            Announce(_lastReport?.Issues.Count > 0
                ? "None of the problems found can be repaired here. Press Control plus Shift plus A " +
                  "to hear what they are."
                : "There is nothing to repair. No accessibility problems were found.",
                AnnouncementPriority.Assertive);
            return;
        }

        Speech.BeginNewAnnouncement();
        Announce($"Going through {fixable.Count} problems that can be repaired. " +
                 "For each one you will be taken to it and offered the repair. " +
                 "Press Escape at any point to stop.", AnnouncementPriority.Assertive);

        int repaired = 0;
        int skipped = 0;

        for (int i = 0; i < fixable.Count; i++)
        {
            var issue = fixable[i];

            // Re-audited findings can go stale as earlier repairs change the document, so each is
            // checked before the user is asked about it. Being offered a repair for something
            // already fixed wastes the one resource remediation is short of: patience.
            if (IsAlreadyResolved(issue))
            {
                skipped++;
                continue;
            }

            if (issue.Element is { } element)
            {
                _navigation.GoToElement(element, _settings.Verbosity);
                MoveCaretTo(element);
            }

            Play(AudioCue.IssueFound);
            Speech.BeginNewAnnouncement();
            Announce($"{i + 1} of {fixable.Count}. {issue.Describe(VerbosityLevel.Detailed)}",
                AnnouncementPriority.Assertive);

            var outcome = OfferRepair(issue);

            if (outcome == RepairOutcome.Stopped)
            {
                Announce($"Stopped. {repaired} repaired, {fixable.Count - i - 1} left.",
                    AnnouncementPriority.Assertive);
                return;
            }

            if (outcome == RepairOutcome.Repaired)
                repaired++;
            else
                skipped++;
        }

        Play(repaired > 0 ? AudioCue.IssueFixed : AudioCue.WorkFinished);
        Speech.BeginNewAnnouncement();

        Announce(repaired == 0
            ? $"Finished. Nothing was repaired; {skipped} were skipped."
            : $"Finished. {repaired} {(repaired == 1 ? "problem" : "problems")} repaired" +
              (skipped > 0 ? $", {skipped} skipped" : string.Empty) +
              ". Press Control plus S to save.", AnnouncementPriority.Assertive);
    }

    private enum RepairOutcome
    {
        Repaired,
        Skipped,
        Stopped,
    }

    /// <summary>Whether a finding has already been dealt with by an earlier repair in this run.</summary>
    private static bool IsAlreadyResolved(AccessibilityIssue issue) => issue.Element switch
    {
        FigureElement figure => !figure.NeedsAlternateText,
        PdfFormField field => field.ResolvedLabelSource == PdfFormField.LabelSource.ToolTip,
        TableElement table => table.HasHeaderCells,
        _ => false,
    };

    /// <summary>
    /// Offers the repair for one finding. Dispatches on what the finding is ABOUT rather than on
    /// which rule produced it, so a new rule reporting an undescribed figure gets the alt-text
    /// repair without any further wiring.
    /// </summary>
    private RepairOutcome OfferRepair(AccessibilityIssue issue)
    {
        switch (issue.Element)
        {
            case FigureElement figure:
                return RepairFigure(figure);

            case PdfFormField field:
                return RepairFieldLabel(field);

            case TableElement table:
                return RepairTableHeaders(table);

            case TextElement text when issue.RuleName == "unmarked page furniture":
                return RepairPageFurniture(text);
        }

        // Document-wide findings, which have no element of their own.
        return issue.RuleName switch
        {
            "document language" => RepairLanguage(),
            "document title" => RepairTitle(),
            _ => AskToSkip(issue),
        };
    }

    private RepairOutcome AskToSkip(AccessibilityIssue issue)
    {
        Announce($"This one cannot be repaired automatically. {issue.Remedy ?? string.Empty} Moving on.");
        return RepairOutcome.Skipped;
    }

    #endregion

    #region Individual repairs

    private RepairOutcome RepairFigure(FigureElement figure)
    {
        var choice = MessageBox.Show(this,
            "This image has no description.\r\n\r\n" +
            "Yes — describe it.\r\n" +
            "No — mark it as decorative, meaning it carries no information.\r\n" +
            "Cancel — stop going through the problems.",
            $"Image on page {figure.PageNumber}",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

        switch (choice)
        {
            case DialogResult.Cancel:
                return RepairOutcome.Stopped;

            case DialogResult.No:
                ApplyEdit(new MarkFigureDecorativeCommand(figure));
                return RepairOutcome.Repaired;
        }

        string? description = AskForAlternateText(figure);

        if (description is null)
            return RepairOutcome.Skipped;

        ApplyEdit(new SetAlternateTextCommand(figure, description));
        return RepairOutcome.Repaired;
    }

    /// <summary>
    /// Asks for a figure's description, and teaches what a good one looks like.
    ///
    /// The guidance is not decoration. Most people have never been told how to write alt text, and
    /// the commonest results — "image", "photo", the filename — are worse than useless because
    /// they convince a reader they have been told something. Three short rules, every time, cost
    /// nothing and change what people write.
    /// </summary>
    private string? AskForAlternateText(FigureElement figure)
    {
        string context = figure.Caption is { Text.Length: > 0 } caption
            ? $" Its caption reads: {caption.Text}"
            : string.Empty;

        // The image itself, cropped out of the rendered page. Useless to the blind user — that is
        // the whole point of the task — but it turns an impossible request into an ordinary one for
        // a sighted colleague sitting alongside, who until now could not see it either.
        using var picture = RenderFigureForDescribing(figure);

        return TextPromptDialog.Ask(this, Speech, Cues,
            $"Describe the image on page {figure.PageNumber}",
            "Description:",
            "Say what the image shows and why it is there, in a sentence or two. " +
            "Do not begin with \"image of\" — readers already announce that. " +
            "If it is a chart, give the point it makes rather than every number." + context,
            initialValue: figure.AlternateText ?? string.Empty,
            multiline: true,
            picture: picture,
            validate: value =>
            {
                if (value.Trim().Length == 0)
                    return "Type a description, or press Escape to leave it and move on.";

                // The commonest useless descriptions, caught kindly. A reader hearing "image" learns
                // nothing they did not already know.
                string lowered = value.Trim().ToLowerInvariant();

                if (lowered is "image" or "picture" or "photo" or "graphic" or "figure" or "logo")
                    return "That only repeats what readers already announce. Say what it shows.";

                return null;
            });
    }

    /// <summary>
    /// Renders just the figure being described, cropped out of its page.
    ///
    /// The renderer is opened on demand here even when the page picture pane is switched off,
    /// because this is the one moment where seeing the image genuinely changes what a person can
    /// do. Returns null if it cannot be produced; the prompt simply appears without a picture,
    /// exactly as it did before, rather than the repair becoming unavailable.
    /// </summary>
    private Bitmap? RenderFigureForDescribing(FigureElement figure)
    {
        if (_document is null)
            return null;

        try
        {
            if (!_renderer.IsAvailable)
                _renderer.Open(_document.FilePath);

            return _renderer.RenderElement(figure, scale: 2.0f, padding: 8);
        }
        catch
        {
            return null;
        }
    }

    private RepairOutcome RepairFieldLabel(PdfFormField field)
    {
        string suggestion = field.ResolvedLabelSource == PdfFormField.LabelSource.None
            ? string.Empty
            : field.Label;

        string? label = TextPromptDialog.Ask(this, Speech, Cues,
            $"Name the form field on page {field.PageNumber}",
            "Field name:",
            "This is what a screen reader will announce for the field. " +
            "Use the words printed beside it on the page, such as \"Date of birth\" or " +
            "\"National Insurance number\"." +
            (suggestion.Length > 0 ? $" The current guess is \"{suggestion}\"." : string.Empty),
            initialValue: suggestion,
            validate: value => value.Trim().Length == 0
                ? "Type a name, or press Escape to leave it and move on."
                : null);

        if (label is null)
            return RepairOutcome.Skipped;

        ApplyEdit(new SetFieldLabelCommand(field, label));
        return RepairOutcome.Repaired;
    }

    private RepairOutcome RepairTableHeaders(TableElement table)
    {
        if (table.Rows.Count == 0)
            return RepairOutcome.Skipped;

        var firstRow = table.Rows[0];

        string preview = string.Join(", ", firstRow.Cells.Take(5).Select(c => c.Text));

        bool useFirstRow = Confirm(
            $"This table's first row contains: {preview}. " +
            "Is that the header row? If it is, marking it means every cell below will be announced " +
            "together with its heading.",
            "Table headers");

        if (!useFirstRow)
        {
            Announce("Left alone. You can mark a header row later from the Fix menu.");
            return RepairOutcome.Skipped;
        }

        ApplyEdit(new MarkHeaderRowCommand(firstRow));
        return RepairOutcome.Repaired;
    }

    private RepairOutcome RepairPageFurniture(TextElement text)
    {
        bool mark = Confirm(
            $"\"{text.Text}\" repeats on many pages. Mark it as page furniture, so it is skipped " +
            "when reading straight through?",
            "Page furniture");

        if (!mark)
            return RepairOutcome.Skipped;

        ApplyEdit(new MarkAsArtifactCommand(text));
        return RepairOutcome.Repaired;
    }

    private RepairOutcome RepairLanguage()
    {
        SetLanguagePrompt();
        return _document?.Metadata.Language is { Length: > 0 }
            ? RepairOutcome.Repaired
            : RepairOutcome.Skipped;
    }

    private RepairOutcome RepairTitle()
    {
        SetTitlePrompt();
        return _document?.Metadata.Title is { Length: > 0 }
            ? RepairOutcome.Repaired
            : RepairOutcome.Skipped;
    }

    #endregion

    #region Repairs invoked directly from the menu

    private void DescribeCurrentFigure()
    {
        if (_navigation.Current is not FigureElement figure)
        {
            Play(AudioCue.Boundary);
            Announce("You are not on an image. Press G to move to the next one.",
                AnnouncementPriority.Assertive);
            return;
        }

        string? description = AskForAlternateText(figure);

        if (description is not null)
            ApplyEdit(new SetAlternateTextCommand(figure, description));
    }

    private void LabelCurrentField()
    {
        if (_navigation.Current is not PdfFormField field)
        {
            Play(AudioCue.Boundary);
            Announce("You are not on a form field. Press F to move to the next one.",
                AnnouncementPriority.Assertive);
            return;
        }

        RepairFieldLabel(field);
    }

    private void SetHeadingLevelPrompt()
    {
        if (_navigation.Current is not HeadingElement heading)
        {
            Play(AudioCue.Boundary);
            Announce("You are not on a heading. Press H to move to the next one.",
                AnnouncementPriority.Assertive);
            return;
        }

        var levels = Enumerable.Range(1, 6).Select(level => (HeadingLevel)level).ToList();

        var chosen = ListSelectionDialog<object>.Choose(this, Speech, Cues,
            "Heading level",
            $"\"{heading.Text}\" is currently level {(int)heading.Level}. " +
            "Level 1 is the document's title, level 2 a main section, level 3 a subsection.",
            levels.Cast<object>().ToList(),
            level => $"Level {(int)(HeadingLevel)level}",
            level => (HeadingLevel)level == heading.Level ? "Current level" : string.Empty,
            actionButtonText: "&Set");

        if (chosen is HeadingLevel newLevel && newLevel != heading.Level)
            ApplyEdit(new SetHeadingLevelCommand(heading, newLevel));
    }

    private void MarkCurrentAsArtifact()
    {
        if (_navigation.Current is not TextElement text || text is ArtifactElement)
        {
            Play(AudioCue.Boundary);
            Announce("You are not on text that can be marked as page furniture.",
                AnnouncementPriority.Assertive);
            return;
        }

        ApplyEdit(new MarkAsArtifactCommand(text));
    }

    private void SetLanguagePrompt()
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        // Offered as a list of names rather than as a box wanting a code. Nobody should have to
        // know that British English is "en-GB" in order to make their document readable.
        var languages = BuildLanguageChoices();

        var chosen = ListSelectionDialog<LanguageChoice>.Choose(this, Speech, Cues,
            "Document language",
            "The language a screen reader will use to read this document. " +
            (_document.Metadata.Language is { Length: > 0 } current
                ? $"It is currently {SetDocumentLanguageCommand.DescribeLanguage(current)}."
                : "It is not currently set."),
            languages,
            language => language.Name,
            language => language.Tag,
            actionButtonText: "&Set");

        if (chosen is not null)
            ApplyEdit(new SetDocumentLanguageCommand(_document, chosen.Tag));
    }

    private sealed record LanguageChoice(string Name, string Tag);

    /// <summary>
    /// The languages offered. The system's own language comes first, since it is by far the most
    /// likely answer, followed by the common ones and then everything the machine knows about.
    /// </summary>
    private static List<LanguageChoice> BuildLanguageChoices()
    {
        var choices = new List<LanguageChoice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(CultureInfo culture)
        {
            if (culture.Name.Length == 0 || !seen.Add(culture.Name))
                return;

            choices.Add(new LanguageChoice(culture.EnglishName, culture.Name));
        }

        Add(CultureInfo.CurrentUICulture);
        Add(CultureInfo.CurrentCulture);

        foreach (string tag in new[]
                 {
                     "en-GB", "en-US", "fr-FR", "de-DE", "es-ES", "it-IT", "pt-PT", "nl-NL",
                     "pl-PL", "sv-SE", "da-DK", "nb-NO", "fi-FI", "cs-CZ", "el-GR", "ru-RU",
                     "ar-SA", "he-IL", "hi-IN", "zh-CN", "ja-JP", "ko-KR",
                 })
        {
            try { Add(CultureInfo.GetCultureInfo(tag)); }
            catch (CultureNotFoundException) { /* Not present on this machine. */ }
        }

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                     .OrderBy(c => c.EnglishName, StringComparer.OrdinalIgnoreCase))
        {
            Add(culture);
        }

        return choices;
    }

    private void SetTitlePrompt()
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        string? title = TextPromptDialog.Ask(this, Speech, Cues,
            "Document title",
            "Title:",
            "This is announced when the document is opened, in place of the filename. " +
            "Setting it also switches on the option that makes readers use it.",
            initialValue: _document.Metadata.Title ?? Path.GetFileNameWithoutExtension(_document.FileName),
            validate: value => value.Trim().Length == 0
                ? "Type a title, or press Escape to leave it unchanged."
                : null);

        if (title is not null)
            ApplyEdit(new SetDocumentTitleCommand(_document, title));
    }

    #endregion

    #region Lists and information

    private void ShowBookmarks()
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        var bookmarks = _document.FlatOutline.ToList();

        if (bookmarks.Count == 0)
        {
            Play(AudioCue.Boundary);
            Announce("This document has no bookmarks. Press H to move between headings instead.",
                AnnouncementPriority.Assertive);
            return;
        }

        var chosen = ListSelectionDialog<OutlineNode>.Choose(this, Speech, Cues,
            "Bookmarks",
            "The document's own contents list, written by whoever made it.",
            bookmarks,
            // Indentation conveys depth on screen; the level is spoken so it is not lost by ear.
            node => $"{new string(' ', node.Level * 2)}{node.Title}",
            node => node.TargetPage is { } page
                ? $"Level {node.Level + 1}, page {page}"
                : $"Level {node.Level + 1}");

        if (chosen?.TargetPage is { } target)
        {
            var result = _navigation.GoToPage(target, _settings.Verbosity);

            if (result.Element is { } element)
                MoveCaretTo(element);

            Play(result.Cue);
            Announce($"{chosen.Title}. {result.Announcement}", AnnouncementPriority.Assertive);
        }
    }

    private void ShowLinks()
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        var links = _document.Links;

        if (links.Count == 0)
        {
            Play(AudioCue.Boundary);
            Announce("This document has no links.", AnnouncementPriority.Assertive);
            return;
        }

        var chosen = ListSelectionDialog<LinkElement>.Choose(this, Speech, Cues,
            "Links",
            "Every link in the document.",
            links,
            link => link.Text.Length > 0 ? link.Text : link.SpeakableTarget,
            link => $"Page {link.PageNumber}, goes to {link.SpeakableTarget}");

        if (chosen is not null)
        {
            _navigation.GoToElement(chosen, _settings.Verbosity);
            MoveCaretTo(chosen);
            Play(AudioCue.Link);
            Announce(chosen.Describe(_settings.Verbosity), AnnouncementPriority.Assertive);
        }
    }

    private void ShowAnnotations()
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        var annotations = _document.Annotations;

        if (annotations.Count == 0)
        {
            Play(AudioCue.Boundary);
            Announce("This document has no comments.", AnnouncementPriority.Assertive);
            return;
        }

        var chosen = ListSelectionDialog<AnnotationElement>.Choose(this, Speech, Cues,
            "Comments",
            "Every comment, highlight and note in the document.",
            annotations,
            annotation => annotation.Describe(VerbosityLevel.Terse),
            annotation => annotation.Describe(VerbosityLevel.Detailed));

        if (chosen is not null)
        {
            _navigation.GoToElement(chosen, _settings.Verbosity);
            MoveCaretTo(chosen);
            Announce(chosen.Describe(VerbosityLevel.Detailed), AnnouncementPriority.Assertive);
        }
    }

    /// <summary>
    /// Lists the form's fields with their state, so the user can see the whole form at once rather
    /// than discovering it field by field, and jump to whichever needs attention.
    /// </summary>
    private void ShowFormFillDialog()
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        var fields = _document.FormFields;

        if (fields.Count == 0)
        {
            Play(AudioCue.Boundary);
            Announce("This document has no form fields.", AnnouncementPriority.Assertive);
            return;
        }

        int remaining = fields.Count(f => f.NeedsAttention);

        var chosen = ListSelectionDialog<PdfFormField>.Choose(this, Speech, Cues,
            "Fill in this form",
            remaining > 0
                ? $"{fields.Count} fields, {remaining} still needing an answer."
                : $"{fields.Count} fields, all required ones answered.",
            fields,
            field =>
            {
                string marker = field.NeedsAttention ? "* " : "  ";
                return $"{marker}{field.Label}: {field.ValueForSpeech}";
            },
            field => field.Describe(VerbosityLevel.Detailed),
            actionButtonText: "&Fill in");

        if (chosen is null)
            return;

        _navigation.GoToElement(chosen, _settings.Verbosity);
        MoveCaretTo(chosen);
        EditField(chosen);
    }

    private void ShowDocumentProperties()
    {
        if (_document is null)
        {
            AnnounceNoDocument();
            return;
        }

        var lines = new List<string>
        {
            $"File: {_document.FileName}",
            $"Title: {_document.Metadata.Title ?? "(none)"}",
            $"Author: {_document.Metadata.Author ?? "(none)"}",
            $"Language: {(_document.Metadata.Language is { Length: > 0 } language
                ? SetDocumentLanguageCommand.DescribeLanguage(language)
                : "(not set)")}",
            $"Announces its title instead of the filename: {(_document.Metadata.DisplaysDocumentTitle ? "yes" : "no")}",
            $"Pages: {_document.PageCount}",
            $"Accessibility tagging: {_document.TaggedStatusDescription}",
            $"Headings: {_document.Headings.Count}",
            $"Form fields: {_document.FormFields.Count}",
            $"Links: {_document.Links.Count}",
            $"Images: {_document.Figures.Count}",
            $"Comments: {_document.Annotations.Count}",
            $"Bookmarks: {_document.FlatOutline.Count()}",
            $"PDF version: {_document.Metadata.PdfVersion:0.0}",
            $"Encrypted: {(_document.Metadata.IsEncrypted ? "yes" : "no")}",
            $"Text extraction permitted: {(_document.Metadata.AllowsAccessibilityExtraction ? "yes" : "no")}",
        };

        foreach (string warning in _document.LoadWarnings)
            lines.Add($"Problem while reading: {warning}");

        ListSelectionDialog<string>.Choose(this, Speech, Cues,
            "Document properties",
            "Everything known about this document.",
            lines,
            line => line,
            actionButtonText: "&Close");
    }

    private void ShowSettings()
    {
        var options = new List<string>
        {
            $"Verbosity: {_settings.Verbosity}. Choose to change.",
            $"Reading mode: {_settings.ReadingMode}. Choose to change.",
            $"Text size on screen: {_settings.TextSizePoints:0} point. Choose to change.",
            $"Page picture beside the text: {(_settings.ShowPagePicture ? "shown" : "hidden")}. Choose to change.",
            $"Sounds: {(_settings.PlayAudioCues ? "on" : "off")}. Choose to change.",
            $"Show item types in the text: {(_settings.ShowRoleLabelsInText ? "on" : "off")}. Choose to change.",
            $"Keep a backup when saving: {(_settings.CreateBackupOnSave ? "on" : "off")}. Choose to change.",
            $"Check the saved file before replacing the original: {(_settings.VerifySaves ? "on" : "off")}. Choose to change.",
            $"Check accessibility when a document opens: {(_settings.AuditOnOpen ? "on" : "off")}. Choose to change.",
        };

        string? chosen = ListSelectionDialog<string>.Choose(this, Speech, Cues,
            "Settings",
            "Choose a setting to change it.",
            options,
            option => option,
            actionButtonText: "&Change");

        if (chosen is null)
            return;

        int index = options.IndexOf(chosen);
        string message;

        switch (index)
        {
            case 0: message = _settings.CycleVerbosity(); break;
            case 1:
                message = _settings.CycleReadingMode();
                RenderDocument();
                break;
            case 2:
                message = _settings.CycleTextSize();
                ApplyTextSize();
                break;
            case 3:
                TogglePagePicture();
                return;
            case 4:
                _settings.PlayAudioCues = !_settings.PlayAudioCues;
                Cues.IsEnabled = _settings.PlayAudioCues;
                message = $"Sounds {(_settings.PlayAudioCues ? "on" : "off")}.";
                break;
            case 5:
                _settings.ShowRoleLabelsInText = !_settings.ShowRoleLabelsInText;
                RenderDocument();
                message = $"Item types in the text {(_settings.ShowRoleLabelsInText ? "on" : "off")}.";
                break;
            case 6:
                _settings.CreateBackupOnSave = !_settings.CreateBackupOnSave;
                message = $"Backups {(_settings.CreateBackupOnSave ? "on" : "off")}.";
                break;
            case 7:
                _settings.VerifySaves = !_settings.VerifySaves;
                message = _settings.VerifySaves
                    ? "Saved files will be checked before they replace the original."
                    : "Saved files will not be checked. This is how accessibility information can be " +
                      "lost without warning; leaving it on is strongly recommended.";
                break;
            case 8:
                _settings.AuditOnOpen = !_settings.AuditOnOpen;
                message = $"Accessibility check on opening {(_settings.AuditOnOpen ? "on" : "off")}.";
                break;
            default: return;
        }

        _settings.Save();
        Play(AudioCue.Success);
        Speech.BeginNewAnnouncement();
        Announce(message, AnnouncementPriority.Assertive);
    }

    private void ShowAbout()
    {
        string message =
            "Accessible PDF Editor. Reads PDFs aloud with full structure navigation, fills in forms, " +
            "and repairs a document's accessibility: image descriptions, form field names, headings, " +
            "table headers, language and title. " +
            "It cannot rewrite the text already printed on a page — PDF is a fixed-layout format and " +
            "that is not something any tool does reliably — but everything a screen reader announces " +
            "about that text can be corrected. " +
            "Saving always checks the result before replacing your original, and refuses rather than " +
            "quietly losing a document's accessibility information.";

        Speech.BeginNewAnnouncement();
        Announce(message, AnnouncementPriority.Assertive);
        MessageBox.Show(this, message, "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    #endregion
}
