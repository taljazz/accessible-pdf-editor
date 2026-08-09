using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Ingestion;
using AccessiblePdfEditor.Persistence;
using AccessiblePdfEditor.UI;
using Microsoft.Extensions.DependencyInjection;

namespace AccessiblePdfEditor;

// =====================================================================================
//  Program.cs
//
//  The entry point, and the composition root where every service is created and wired
//  together.
//
//  The order of the startup steps is not arbitrary and is worth stating:
//
//    1. High-DPI and visual styles, before any window exists, or controls are laid out at
//       the wrong size and a user relying on large text gets clipped labels.
//    2. PDFsharp font configuration, before anything creates a font. The default text
//       encoding can only be set once per process, and getting it wrong silently destroys
//       any text outside Latin-1.
//    3. Services, then the window.
//
//  There is also a last-resort exception handler. An unhandled exception in a normal
//  program shows a dialog the user can read; in this one, a crash that produces silence
//  leaves someone with no idea whether the program has stopped, whether their document is
//  safe, or whether they simply mis-pressed a key. So anything that escapes is spoken.
// =====================================================================================

internal static class Program
{
    #region Entry point

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Must happen before the first font is created. See the note above.
        PdfSharpEnvironment.Initialise();

        var settings = ReaderSettings.Load();
        using var services = BuildServices(settings);

        var speech = services.GetRequiredService<ISpeechService>();

        InstallCrashHandlers(speech);

        try
        {
            var window = services.GetRequiredService<MainForm>();

            // A document named on the command line. This is what makes "Open with", double-clicking
            // a PDF, and dragging one onto the program work — all of which are how a person
            // normally opens a file, as opposed to launching an editor and then hunting for it.
            if (FindDocumentArgument(args) is { } path)
                window.OpenOnStartup(path);

            Application.Run(window);
        }
        finally
        {
            settings.Save();
        }
    }

    /// <summary>
    /// The document to open from the command line, or null when none was given.
    ///
    /// Deliberately forgiving about what it is handed, because it is handed whatever Windows
    /// decides to pass: Explorer quotes paths containing spaces, a shortcut may add switches, and a
    /// drag-and-drop can supply several files at once. Anything that is not an existing file is
    /// skipped rather than treated as an error — a stray switch should not stop the program opening.
    ///
    /// The extension is NOT checked. Whether a file is really a PDF is the loader's question, and it
    /// already answers it properly; refusing here on the strength of a file name would reject a
    /// valid document that happened to be named badly, and say something less useful when it did.
    /// </summary>
    internal static string? FindDocumentArgument(string[]? args)
    {
        if (args is null)
            return null;

        foreach (string? raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            // Explorer passes quoted paths for anything containing a space, and the quotes are not
            // always stripped by the time they arrive here.
            string candidate = raw.Trim().Trim('"');

            if (candidate.Length == 0)
                continue;

            try
            {
                // A folder is a plausible thing to be handed and is not something to open.
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch (Exception)
            {
                // A malformed path — illegal characters, too long. Not this one, then; the next
                // argument may still be good, and a bad one must not stop the program starting.
            }
        }

        return null;
    }

    #endregion

    #region Composition
    // Everything is registered against an interface so the whole application can be run headlessly
    // in tests with the recording implementations, which is what makes "does this announce the
    // right thing" a question the build can answer.

    private static ServiceProvider BuildServices(ReaderSettings settings)
    {
        var services = new ServiceCollection();

        services.AddSingleton(settings);

        services.AddSingleton<ISpeechService>(_ => new TolkSpeechService());

        services.AddSingleton<IAudioCueService>(_ =>
        {
            var cues = new OpenAlAudioCueService
            {
                IsEnabled = settings.PlayAudioCues,
            };

            cues.SetVolume(settings.CueVolume);
            return cues;
        });

        services.AddSingleton<IDocumentLoader, PdfPigDocumentLoader>();
        services.AddSingleton<IDocumentSaver, PdfSharpDocumentSaver>();

        services.AddSingleton<MainForm>();

        return services.BuildServiceProvider();
    }

    #endregion

    #region Last-resort error handling
    // A crash that only writes to a log tells a blind user nothing. Whatever happens, they hear
    // that something went wrong and what to do about it.

    private static void InstallCrashHandlers(ISpeechService speech)
    {
        Application.ThreadException += (_, e) => ReportCrash(speech, e.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                ReportCrash(speech, exception);
        };

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
    }

    private static void ReportCrash(ISpeechService speech, Exception exception)
    {
        string message =
            $"Something went wrong: {exception.Message}. " +
            "Your document has not been changed on disk. If you have unsaved edits, try saving as " +
            "a new copy.";

        try
        {
            speech.BeginNewAnnouncement();
            speech.Speak(message, Model.AnnouncementPriority.Assertive);
        }
        catch
        {
            // Speech itself may be what failed. The dialog below is the fallback.
        }

        MessageBox.Show(message, "Accessible PDF Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    #endregion
}
