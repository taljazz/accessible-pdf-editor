using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Model;

namespace AccessiblePdfEditor.UI;

// =====================================================================================
//  AccessibleFormBase.cs
//
//  The base class every window in this application derives from.
//
//  THE CENTRAL DESIGN DECISION OF THE WHOLE UI LIVES HERE, so it is worth stating plainly.
//
//  There are two ways to make a Windows program work with a screen reader:
//
//    (a) SELF-VOICING — the program speaks everything itself through Tolk. Total control
//        over the wording, but it bypasses the screen reader entirely. The user loses their
//        review cursor, their Say All, their braille display, their own speech settings,
//        their punctuation level, and every keyboard habit they have built over years.
//
//    (b) NATIVE — the program uses real Windows controls carrying proper accessible names,
//        roles and values, and lets the screen reader do what it is for. Everything the
//        user already knows how to do keeps working. The program has less control over the
//        exact words.
//
//  This application does BOTH, in that order of priority. Every control is a real control
//  with a real accessible name, so NVDA, JAWS, Narrator and anything else speaking UI
//  Automation work natively — including the review cursor and braille, which self-voicing
//  cannot provide at all. Tolk is then used ON TOP for the things UI Automation has no way
//  to express: "you have moved from a paragraph to a level 2 heading", "3 of 12 required
//  fields are still empty", "that value was rejected because it is not a date".
//
//  Getting this the wrong way round is the commonest mistake in accessible software
//  written by sighted developers. A self-voicing app feels impressive in a demo and is
//  worse to live with, because it takes away tools the user relies on and replaces them
//  with the developer's guesses.
// =====================================================================================

#region AccessibleFormBase

/// <summary>
/// Base class for every window. Owns accessible configuration, the opening announcement, and the
/// keyboard conventions shared across the application.
/// </summary>
public abstract class AccessibleFormBase : Form
{
    #region Construction and services

    private readonly List<Control> _announcedControls = [];
    private bool _contentBuilt;

    protected AccessibleFormBase(ISpeechService speech, IAudioCueService cues)
    {
        Speech = speech;
        Cues = cues;

        // KeyPreview lets the form see keys before its controls do, which is how the global
        // shortcuts work. Individual controls still get anything the form does not claim.
        KeyPreview = true;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;

        // Fonts are left at the system default deliberately. A user who has set a large system
        // font has done so for a reason, and overriding it would undo their own configuration.
        Font = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 10f);
    }

    /// <summary>Supplemental speech, for what UI Automation cannot express.</summary>
    protected ISpeechService Speech { get; }

    /// <summary>Short non-speech sounds.</summary>
    protected IAudioCueService Cues { get; }

    #endregion

    #region What each window supplies

    /// <summary>The window's title, shown in the title bar and announced on opening.</summary>
    protected abstract string WindowTitle { get; }

    /// <summary>
    /// What the window is for, in one sentence, spoken when it opens. Every window says what it is
    /// and how to leave it, because a dialog that appears without explanation and traps focus is
    /// the most disorienting thing a keyboard user meets.
    /// </summary>
    protected abstract string WindowPurpose { get; }

    /// <summary>Builds the window's controls.</summary>
    protected abstract void BuildContent();

    /// <summary>
    /// Applies accessible names, roles and relationships. Called after
    /// <see cref="BuildContent"/>; the base implementation walks every control and fills in
    /// anything missing, so a window that forgets is caught rather than shipped silent.
    /// </summary>
    protected virtual void ConfigureAccessibility()
    {
        ApplyFallbackAccessibleNames(Controls);
    }

    /// <summary>
    /// The full announcement made when the window opens. Overridable for windows that need to say
    /// more, such as reporting how many items a list contains.
    /// </summary>
    protected virtual string BuildOpeningAnnouncement() =>
        $"{WindowTitle}. {WindowPurpose}";

    #endregion

    #region Lifecycle

    protected override void OnLoad(EventArgs e)
    {
        Text = WindowTitle;

        if (!_contentBuilt)
        {
            BuildContent();
            ConfigureAccessibility();
            _contentBuilt = true;
        }

        base.OnLoad(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Spoken assertively: the user has just moved into a new window, and anything still being
        // said about the previous one is now out of date and actively confusing.
        Speech.BeginNewAnnouncement();
        Announce(BuildOpeningAnnouncement(), AnnouncementPriority.Assertive);

        FocusFirstControl();
    }

    /// <summary>
    /// Moves focus to the control the user most likely wants. Overridable; the default takes the
    /// first control that can be tabbed to, which is right for nearly every window.
    /// </summary>
    protected virtual void FocusFirstControl()
    {
        SelectNextControl(null, forward: true, tabStopOnly: true, nested: true, wrap: true);
    }

    #endregion

    #region Keyboard conventions
    // Shared across every window so that a key means the same thing everywhere. Anything a window
    // does not claim falls through to its controls, so text boxes still work normally.

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                if (CloseOnEscape)
                {
                    Close();
                    return true;
                }

                break;

            case Keys.F1:
                ShowHelp();
                return true;

            // The same help, in a window that can be read at the user's own pace instead of being
            // spoken once and gone. Every window gets this, because every window has keys worth
            // looking up rather than memorising.
            case Keys.Shift | Keys.F1:
                ShowBrowsableHelp();
                return true;

            // Repeat the last announcement. Indispensable: speech is transient, and a phrase talked
            // over by a system sound is otherwise simply lost.
            case Keys.Control | Keys.Space:
                Speech.RepeatLast();
                return true;
        }

        if (HandleShortcut(keyData))
            return true;

        return base.ProcessCmdKey(ref message, keyData);
    }

    /// <summary>Whether Escape closes this window. True for dialogs, overridden to false for the main window.</summary>
    protected virtual bool CloseOnEscape => true;

    /// <summary>Handles a window-specific shortcut. Return true when it was handled.</summary>
    protected virtual bool HandleShortcut(Keys keyData) => false;

    /// <summary>
    /// Describes this window's keys, phrased to be SPOKEN. Read out on F1 for a quick reminder,
    /// without opening anything.
    /// </summary>
    protected virtual string BuildKeyHelp() =>
        "Escape closes this window. F1 repeats this help. Shift plus F1 opens it in a window you " +
        "can read through at your own pace. Control plus Space repeats the last thing said.";

    /// <summary>
    /// The same help, laid out to be READ rather than heard: line breaks, one item per line, and
    /// grouped headings.
    ///
    /// Separate from <see cref="BuildKeyHelp"/> because the two have genuinely different demands.
    /// Spoken help wants flowing sentences and no punctuation the voice will read out; browsable
    /// help wants short lines you can arrow through and a shape you can skim. A single string
    /// serving both is worse at each.
    ///
    /// Defaults to the spoken version, so a window that has not written a browsable one still shows
    /// something useful rather than an empty box.
    /// </summary>
    protected virtual string BuildBrowsableHelp() => BuildKeyHelp();

    /// <summary>Speaks the key help.</summary>
    protected void ShowHelp()
    {
        Speech.BeginNewAnnouncement();
        Announce(BuildKeyHelp(), AnnouncementPriority.Assertive);
    }

    /// <summary>Opens the key help in a window the user can browse.</summary>
    protected void ShowBrowsableHelp()
    {
        TextViewerDialog.Show(this, Speech, Cues,
            $"Keys — {WindowTitle}",
            "The keys available in this window.",
            BuildBrowsableHelp());
    }

    #endregion

    #region Speaking and sounding

    /// <summary>Says something to the user.</summary>
    protected void Announce(string message, AnnouncementPriority priority = AnnouncementPriority.Polite)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        Speech.Speak(message, priority);
    }

    /// <summary>Plays an earcon.</summary>
    protected void Play(AudioCue cue) => Cues.Play(cue);

    /// <summary>
    /// Announces an outcome with a matching sound. Keeps the pairing of words and earcon
    /// consistent — a failure always sounds like a failure — rather than leaving each call site to
    /// remember.
    /// </summary>
    protected void AnnounceOutcome(bool succeeded, string message)
    {
        Play(succeeded ? AudioCue.Success : AudioCue.Rejected);
        Announce(message, succeeded ? AnnouncementPriority.Polite : AnnouncementPriority.Assertive);
    }

    #endregion

    #region Building accessible controls
    // Factory helpers used by every window, so that no control can be created without an accessible
    // name. A control with no name announces as its type alone — "edit", "button" — which is the
    // single commonest accessibility fault in Windows software.

    /// <summary>Creates a label.</summary>
    protected static Label CreateLabel(string text, int tabIndex = 0) => new()
    {
        Text = text,
        AutoSize = true,
        TabIndex = tabIndex,
        AccessibleRole = AccessibleRole.StaticText,
        AccessibleName = text,
        Margin = new Padding(3, 6, 3, 3),
    };

    /// <summary>
    /// Creates a text box with an accessible name and description.
    ///
    /// The description is where the format guidance goes — "enter a date, for example 31/03/2026".
    /// Screen readers announce it after the name, which is exactly when the user needs it.
    /// </summary>
    protected static TextBox CreateTextBox(
        string accessibleName,
        string? description = null,
        bool multiline = false,
        int tabIndex = 0)
    {
        var box = new TextBox
        {
            AccessibleName = accessibleName,
            AccessibleDescription = description,
            AccessibleRole = AccessibleRole.Text,
            Multiline = multiline,
            TabIndex = tabIndex,
            Width = 320,
        };

        if (multiline)
        {
            box.Height = 90;
            box.ScrollBars = ScrollBars.Vertical;

            // Without this, Enter would activate the dialog's default button instead of starting a
            // new line, which makes a multi-line field impossible to use.
            box.AcceptsReturn = true;
        }

        return box;
    }

    /// <summary>Creates a button.</summary>
    protected static Button CreateButton(
        string text,
        EventHandler onClick,
        string? description = null,
        int tabIndex = 0)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AccessibleName = text.Replace("&", string.Empty),
            AccessibleDescription = description,
            AccessibleRole = AccessibleRole.PushButton,
            TabIndex = tabIndex,
            Padding = new Padding(8, 4, 8, 4),
        };

        button.Click += onClick;
        return button;
    }

    /// <summary>
    /// Creates a list box that announces its contents properly.
    ///
    /// A list box is the right control for a set of findings or links: the screen reader announces
    /// "3 of 17" as the user arrows through it, without the program having to say anything, and
    /// first-letter navigation works for free.
    /// </summary>
    protected static ListBox CreateListBox(string accessibleName, string? description = null, int tabIndex = 0) => new()
    {
        AccessibleName = accessibleName,
        AccessibleDescription = description,
        AccessibleRole = AccessibleRole.List,
        TabIndex = tabIndex,
        IntegralHeight = false,
        Width = 560,
        Height = 320,
    };

    /// <summary>
    /// Ties a label to the control it names, for readers that use the relationship rather than
    /// reading order, and fills in the control's accessible name from the label when it has none.
    /// </summary>
    protected static void AssociateLabel(Label label, Control control)
    {
        string name = label.Text.Replace("&", string.Empty).TrimEnd(':', ' ');

        if (string.IsNullOrEmpty(control.AccessibleName))
            control.AccessibleName = name;

        // Placing the label immediately before the control in tab order is what makes most screen
        // readers pair them, so the tab indices are set explicitly rather than left to chance.
        label.TabIndex = Math.Max(0, control.TabIndex - 1);
    }

    /// <summary>
    /// Walks the control tree and gives an accessible name to anything without one, taking it from
    /// the control's own text. A safety net: a window that forgot to name a control still announces
    /// something useful rather than "edit".
    /// </summary>
    private static void ApplyFallbackAccessibleNames(Control.ControlCollection controls)
    {
        foreach (Control control in controls)
        {
            if (string.IsNullOrEmpty(control.AccessibleName) && !string.IsNullOrEmpty(control.Text))
                control.AccessibleName = control.Text.Replace("&", string.Empty);

            if (control.HasChildren)
                ApplyFallbackAccessibleNames(control.Controls);
        }
    }

    #endregion

    #region Standard dialog layout
    // Every dialog in this application lays out the same way — content, then buttons at the bottom
    // in a fixed order — because a predictable tab order is worth more than a clever layout when
    // you cannot see it.

    /// <summary>
    /// Builds the standard button row. OK first in tab order, then Cancel, which matches every
    /// other Windows dialog and is what a keyboard user expects to find.
    /// </summary>
    protected FlowLayoutPanel CreateButtonRow(params Button[] buttons)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8),
            AccessibleRole = AccessibleRole.Grouping,
            AccessibleName = "Actions",
        };

        // Added in reverse so that visually they read left to right while the first button given
        // is the one reached first by Tab.
        foreach (var button in buttons.Reverse())
            panel.Controls.Add(button);

        return panel;
    }

    /// <summary>Registers a button as the one Enter activates.</summary>
    protected void SetDefaultButton(Button button) => AcceptButton = button;

    /// <summary>Registers a button as the one Escape activates.</summary>
    protected void SetCancelButton(Button button) => CancelButton = button;

    #endregion

    #region Confirmation
    // Confirmations are spoken before the dialog appears, because a message box's own announcement
    // is easy to miss and its buttons give no clue what is being agreed to.

    /// <summary>
    /// Asks a yes or no question. The question is spoken before the box appears, so it is heard in
    /// full even if the reader announces only the buttons.
    /// </summary>
    protected bool Confirm(string question, string title = "Please confirm")
    {
        Speech.BeginNewAnnouncement();
        Announce(question, AnnouncementPriority.Assertive);
        Play(AudioCue.Warning);

        var answer = MessageBox.Show(this, question, title,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

        return answer == DialogResult.Yes;
    }

    /// <summary>Reports something the user must acknowledge.</summary>
    protected void ReportProblem(string message, string title = "Problem")
    {
        Speech.BeginNewAnnouncement();
        Announce(message, AnnouncementPriority.Assertive);
        Play(AudioCue.Error);

        MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    #endregion
}

#endregion
