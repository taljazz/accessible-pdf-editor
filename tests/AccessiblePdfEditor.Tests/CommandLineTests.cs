// 'Program' on its own would bind to this test project's own entry point, not the application's.
using App = global::AccessiblePdfEditor.Program;

namespace AccessiblePdfEditor.Tests;

// =====================================================================================
//  CommandLineTests.cs
//
//  Tests for the document named on the command line.
//
//  This is what makes double-clicking a PDF, "Open with", and dragging a file onto the
//  program work — which is how a person normally opens a document, as opposed to
//  launching an editor and then going to find it. For someone using a screen reader that
//  difference is larger than it sounds: a file dialog is several more steps in a place
//  they did not ask to be.
//
//  The parsing is deliberately forgiving, because it is handed whatever Windows decides to
//  pass rather than what a well-behaved caller would write. The tests below are mostly
//  about the shapes that actually arrive: quoted paths, several files at once, switches
//  from a shortcut, and paths that are simply wrong.
// =====================================================================================

internal static class CommandLineTests
{
    public static void Register(TestRunner t)
    {
        t.Group("command line");

        t.Test("no arguments means no document", () =>
        {
            t.IsNull(App.FindDocumentArgument([]), "an empty command line opens nothing");
            t.IsNull(App.FindDocumentArgument(null), "and neither does a missing one");
        });

        t.Test("an existing file is opened", () =>
        {
            WithTemporaryFile(path =>
            {
                t.AreEqual(path, App.FindDocumentArgument([path]), "the file should be chosen");
            });
        });

        t.Test("a quoted path is unquoted", () =>
        {
            // Explorer quotes anything containing a space, and the quotes are not always stripped
            // by the time they arrive. Left in place, every path with a space in it would fail —
            // which is most of the ones in somebody's Documents folder.
            WithTemporaryFile(path =>
            {
                t.AreEqual(path, App.FindDocumentArgument([$"\"{path}\""]),
                    "surrounding quotes should not stop the file being found");
            });
        });

        t.Test("a relative path is made absolute", () =>
        {
            // The working directory when Explorer launches a program is not the folder the file is
            // in, and a path that is merely remembered rather than resolved would break the moment
            // anything changed it.
            WithTemporaryFile(path =>
            {
                string directory = Path.GetDirectoryName(path)!;
                string previous = Directory.GetCurrentDirectory();

                try
                {
                    Directory.SetCurrentDirectory(directory);

                    string? found = App.FindDocumentArgument([Path.GetFileName(path)]);

                    t.AreEqual(path, found, "a bare file name should resolve to a full path");
                }
                finally
                {
                    Directory.SetCurrentDirectory(previous);
                }
            });
        });

        t.Test("a path that does not exist is ignored rather than opened", () =>
        {
            string missing = Path.Combine(Path.GetTempPath(), $"no-such-file-{Guid.NewGuid():N}.pdf");

            t.IsNull(App.FindDocumentArgument([missing]), "nothing should be returned");
        });

        t.Test("switches are skipped", () =>
        {
            // A shortcut can carry them, and one arriving first must not stop the file after it
            // being found.
            WithTemporaryFile(path =>
            {
                t.AreEqual(path, App.FindDocumentArgument(["--quiet", "-x", path]),
                    "the file should still be found after the switches");
            });
        });

        t.Test("a folder is not treated as a document", () =>
        {
            t.IsNull(App.FindDocumentArgument([Path.GetTempPath()]),
                "a directory is a plausible thing to be handed and is not something to open");
        });

        t.Test("the first real file wins when several are dragged on", () =>
        {
            WithTemporaryFile(first =>
            {
                WithTemporaryFile(second =>
                {
                    t.AreEqual(first, App.FindDocumentArgument([first, second]),
                        "one window opens one document");
                });
            });
        });

        t.Test("a malformed path does not stop the program starting", () =>
        {
            // Illegal characters, or a path far past the length limit. Throwing here would mean the
            // program refused to start at all because of one bad argument.
            string[] nonsense = ["\0\0\0", new string('x', 5000), "??|<>"];

            t.IsNull(App.FindDocumentArgument(nonsense), "it should come back empty-handed, not throw");
        });

        t.Test("blank arguments are skipped", () =>
        {
            WithTemporaryFile(path =>
            {
                t.AreEqual(path, App.FindDocumentArgument(["", "   ", path]),
                    "empty and whitespace arguments should be passed over");
            });
        });

        t.Test("the extension is not checked", () =>
        {
            // Whether a file is really a PDF is the loader's question, and it answers it properly.
            // Refusing here on the strength of a file name would reject a valid document that
            // happened to be named badly, and say something less useful when it did.
            WithTemporaryFile(path =>
            {
                t.AreEqual(path, App.FindDocumentArgument([path]),
                    "a file without a .pdf extension should still be handed to the loader");
            }, extension: ".dat");
        });
    }

    #region Helpers

    private static void WithTemporaryFile(Action<string> test, string extension = ".pdf")
    {
        string path = Path.Combine(Path.GetTempPath(), $"ape-cli-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, "not really a PDF, and it does not need to be");

        try
        {
            test(Path.GetFullPath(path));
        }
        finally
        {
            try { File.Delete(path); } catch { /* a leftover temporary file is not worth a failure */ }
        }
    }

    #endregion
}
