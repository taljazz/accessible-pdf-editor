using System.Text.Json;
using System.Text.Json.Serialization;
using AccessiblePdfEditor.Model;

namespace AccessiblePdfEditor;

// =====================================================================================
//  ReaderSettings.cs
//
//  The user's preferences, and where they are kept.
//
//  Every setting here exists because reasonable people want different things from a
//  screen-reader application, and the defaults cannot suit all of them. Verbosity is the
//  clearest case: someone meeting the program for the first time wants roles and hints
//  spelled out; someone using it daily wants the text and nothing else, because repeated
//  boilerplate is what makes reading slow.
//
//  Settings live in the user's own application data folder rather than beside the program,
//  so they survive an update and do not need administrator rights to change.
// =====================================================================================

#region ReaderSettings

/// <summary>The user's preferences.</summary>
public sealed class ReaderSettings
{
    #region Speech and sound

    /// <summary>How much detail announcements include.</summary>
    public VerbosityLevel Verbosity { get; set; } = VerbosityLevel.Normal;

    /// <summary>How a document is linearised for reading.</summary>
    public ReadingMode ReadingMode { get; set; } = ReadingMode.Structured;

    /// <summary>Whether short non-speech sounds are played.</summary>
    public bool PlayAudioCues { get; set; } = true;

    /// <summary>Cue volume, from 0 to 1.</summary>
    public float CueVolume { get; set; } = 0.55f;

    /// <summary>
    /// Whether to write role names into the document text, as in "Heading 2: Introduction".
    ///
    /// On by default. The screen reader reading the text box has no idea which line is a heading —
    /// to it, everything is text. Writing the roles in means someone reading with Say All, or with
    /// a braille display, still learns the document's structure, instead of it being available only
    /// through this program's own navigation keys.
    /// </summary>
    public bool ShowRoleLabelsInText { get; set; } = true;

    /// <summary>
    /// Whether the document is read in the browse view rather than the text box.
    ///
    /// On by default, where the WebView2 runtime allows it. The browse view is a real document as
    /// far as a screen reader is concerned, so the user gets their OWN commands — quick navigation
    /// by heading and table, table navigation with Control plus Alt plus the arrow keys, the
    /// element list, Say All — instead of imitations of them written into this program. The text
    /// box remains one keystroke away for anyone who prefers it, and is used automatically when
    /// the runtime is missing.
    /// </summary>
    public bool UseBrowseView { get; set; } = true;

    /// <summary>
    /// The name written on comments this user writes, as the annotation's /T.
    ///
    /// Defaults to the Windows account name, which is nearly always right and saves asking. It
    /// matters more than it looks: a comment thread where every remark is anonymous cannot be
    /// followed by ear, because there is no other cue distinguishing one speaker from another.
    /// </summary>
    public string AuthorName { get; set; } = Environment.UserName;

    #endregion

    #region Display
    // Present because plenty of screen-reader users have some sight, and because a sighted
    // colleague is often looking at the same screen while helping.

    /// <summary>
    /// Text size in the document view, in points.
    ///
    /// Worth more than it looks. A large share of the people who use a screen reader also have
    /// some usable sight, and read the screen as well as listening — and a sighted colleague
    /// helping with a document is reading it at whatever size this is set to.
    /// </summary>
    public float TextSizePoints { get; set; } = 11f;

    /// <summary>
    /// Whether the page picture is shown beside the text.
    ///
    /// Off by default. The picture is for people who can see it, and a screen reader can make
    /// nothing of it — so the pane stays out of the window and out of the tab order until somebody
    /// asks for it. A sighted person switches it on once and it is remembered.
    /// </summary>
    public bool ShowPagePicture { get; set; }

    /// <summary>The sizes offered, from ordinary reading size up to genuinely large print.</summary>
    private static readonly float[] TextSizeChoices = [9f, 11f, 14f, 18f, 24f, 32f];

    /// <summary>Moves to the next text size, wrapping round, and describes it.</summary>
    public string CycleTextSize()
    {
        int current = Array.FindIndex(TextSizeChoices, s => Math.Abs(s - TextSizePoints) < 0.01f);
        TextSizePoints = TextSizeChoices[(current + 1) % TextSizeChoices.Length];

        string description = TextSizePoints switch
        {
            <= 9f => "small",
            <= 11f => "normal",
            <= 14f => "large",
            <= 18f => "larger",
            <= 24f => "very large",
            _ => "largest",
        };

        return $"Text size {TextSizePoints:0} point, {description}.";
    }

    #endregion

    #region Editing and saving

    /// <summary>Whether a backup copy is kept when a document is saved over.</summary>
    public bool CreateBackupOnSave { get; set; } = true;

    /// <summary>
    /// Whether a saved file is re-opened and checked before it replaces the original. On by
    /// default; this is what catches a save that silently dropped a document's accessibility tags,
    /// and turning it off should be a deliberate choice for very large files.
    /// </summary>
    public bool VerifySaves { get; set; } = true;

    /// <summary>
    /// Whether to run the accessibility check automatically when a document opens.
    ///
    /// On by default, because the first thing worth knowing about an unfamiliar PDF is whether it
    /// can be read at all — and a check nobody remembers to run finds nothing.
    /// </summary>
    public bool AuditOnOpen { get; set; } = true;

    /// <summary>Whether to announce the audit summary aloud on opening, or only show it in the status line.</summary>
    public bool AnnounceAuditOnOpen { get; set; } = true;

    #endregion

    #region Recent files

    /// <summary>Recently opened files, most recent first.</summary>
    public List<string> RecentFiles { get; set; } = [];

    /// <summary>Records a file as recently opened.</summary>
    public void RecordRecentFile(string path)
    {
        const int maximum = 10;

        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);

        while (RecentFiles.Count > maximum)
            RecentFiles.RemoveAt(RecentFiles.Count - 1);
    }

    #endregion

    #region Persistence
    // Failures are swallowed on purpose. Settings not loading means the defaults are used, and
    // settings not saving means a preference is lost — neither is worth an error message, and
    // neither should ever stop the program starting or closing.

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AccessiblePdfEditor",
        "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads the saved settings, or returns defaults.</summary>
    public static ReaderSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new ReaderSettings();

            string json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<ReaderSettings>(json, SerializerOptions) ?? new ReaderSettings();
        }
        catch
        {
            return new ReaderSettings();
        }
    }

    /// <summary>Writes the settings to disk.</summary>
    public void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(SettingsPath);

            if (directory is not null && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch
        {
            // A preference that could not be saved is not worth interrupting the user for.
        }
    }

    #endregion

    #region Cycling through choices
    // Every cycling setting returns the sentence to speak, so the call site never has to work out
    // how to describe the new value and they can never drift apart.

    /// <summary>Moves to the next verbosity level, wrapping round, and describes it.</summary>
    public string CycleVerbosity()
    {
        Verbosity = Verbosity switch
        {
            VerbosityLevel.Terse => VerbosityLevel.Normal,
            VerbosityLevel.Normal => VerbosityLevel.Detailed,
            _ => VerbosityLevel.Terse,
        };

        return Verbosity switch
        {
            VerbosityLevel.Terse =>
                "Verbosity: brief. Only the text is announced.",
            VerbosityLevel.Normal =>
                "Verbosity: normal. Announcements include what each item is.",
            _ =>
                "Verbosity: full. Announcements include what each item is, its state, its position, " +
                "and how to use it.",
        };
    }

    /// <summary>Moves to the next reading mode, wrapping round, and describes it.</summary>
    public string CycleReadingMode()
    {
        ReadingMode = ReadingMode switch
        {
            ReadingMode.Structured => ReadingMode.Layout,
            ReadingMode.Layout => ReadingMode.Raw,
            _ => ReadingMode.Structured,
        };

        return ReadingMode switch
        {
            ReadingMode.Structured =>
                "Reading by structure. Running headers and page numbers are skipped, and each item " +
                "is announced with what it is.",
            ReadingMode.Layout =>
                "Reading by page layout. Everything on the page is included, in the order it falls.",
            _ =>
                "Reading raw text, with nothing added or interpreted.",
        };
    }

    #endregion
}

#endregion
