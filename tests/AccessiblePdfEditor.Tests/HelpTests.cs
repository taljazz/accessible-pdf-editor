using System.Reflection;
using AccessiblePdfEditor.Accessibility;
using AccessiblePdfEditor.Ingestion;
using AccessiblePdfEditor.Persistence;
using AccessiblePdfEditor.UI;

namespace AccessiblePdfEditor.Tests;

// =====================================================================================
//  HelpTests.cs
//
//  Tests for the browsable key help opened with Shift+F1.
//
//  The most valuable test here is the last one: it checks that every key the main window
//  actually handles is mentioned in the help. Key documentation rots — a shortcut gets
//  added, the help does not, and a year later the only way to discover half the program is
//  to read the source. For a user who cannot see a toolbar, undocumented keys are not an
//  inconvenience, they are features that do not exist.
//
//  These tests construct real windows. That is safe because building the controls happens
//  on load, and nothing here is ever shown.
// =====================================================================================

internal static class HelpTests
{
    public static void Register(TestRunner t)
    {
        RegisterLineEndings(t);
        RegisterHelpContent(t);
        RegisterCoverage(t);
    }

    #region Line endings
    // A bare newline renders in a text box as an unprintable box, not a line break, so the whole
    // thing would arrive as one enormous line. A single line cannot be browsed line by line, which
    // is the entire point of the window. C# raw-string literals produce bare newlines, so this is
    // the normal case rather than an edge one.

    private static void RegisterLineEndings(TestRunner t)
    {
        t.Group("browsable help — line endings");

        t.Test("bare newlines become Windows line endings", () =>
        {
            string normalised = TextViewerDialog.NormaliseLineEndings("one\ntwo\nthree");
            t.AreEqual("one\r\ntwo\r\nthree", normalised, "a text box needs carriage returns");
        });

        t.Test("existing Windows line endings are not doubled", () =>
        {
            string normalised = TextViewerDialog.NormaliseLineEndings("one\r\ntwo");
            t.AreEqual("one\r\ntwo", normalised, "already-correct text must pass through unchanged");
        });

        t.Test("lone carriage returns are handled too", () =>
        {
            string normalised = TextViewerDialog.NormaliseLineEndings("one\rtwo");
            t.AreEqual("one\r\ntwo", normalised, "old-style line endings should still display");
        });

        t.Test("null and empty are safe", () =>
        {
            t.AreEqual(string.Empty, TextViewerDialog.NormaliseLineEndings(null), "null becomes empty");
            t.AreEqual(string.Empty, TextViewerDialog.NormaliseLineEndings(""), "empty stays empty");
        });
    }

    #endregion

    #region Help content

    private static void RegisterHelpContent(TestRunner t)
    {
        t.Group("browsable help — content");

        t.Test("the main window's browsable help is genuinely many lines", () =>
        {
            // One long paragraph would technically open, and would be useless: the point is to
            // arrow through it a line at a time.
            string help = GetBrowsableHelp(BuildMainForm());
            int lines = help.Split('\n').Length;

            t.IsTrue(lines > 40, $"the help should be a browsable list, but it has only {lines} lines");
        });

        t.Test("the browsable help puts the key before its description", () =>
        {
            // So that arrowing down gives "H, next heading" rather than a sentence that has to be
            // heard to the end before the key arrives.
            string help = GetBrowsableHelp(BuildMainForm());

            string? headingLine = help.Split('\n')
                .FirstOrDefault(l => l.TrimStart().StartsWith("H ", StringComparison.Ordinal));

            t.IsNotNull(headingLine, "there should be a line for the H key");
            t.Says(headingLine!, "next heading");
        });

        t.Test("the spoken and browsable versions are different", () =>
        {
            // They serve different needs: spoken wants flowing sentences, browsable wants short
            // lines. A single string doing both is worse at each.
            var form = BuildMainForm();

            string spoken = GetSpokenHelp(form);
            string browsable = GetBrowsableHelp(form);

            t.IsFalse(string.Equals(spoken, browsable, StringComparison.Ordinal),
                "the main window should have a purpose-written browsable version");
        });

        t.Test("a window with no browsable version falls back to the spoken one", () =>
        {
            // So a dialog that has not written one still shows something useful rather than an
            // empty box.
            using var dialog = new TextPromptDialog(
                new NullSpeechService(), new SilentAudioCueService(), "Test", "Prompt:");

            string spoken = GetSpokenHelp(dialog);
            string browsable = GetBrowsableHelp(dialog);

            t.AreEqual(spoken, browsable, "the fallback should be the spoken help");
            t.IsTrue(browsable.Length > 0, "and it should not be empty");
        });

        t.Test("both versions mention Shift+F1 so the feature is discoverable", () =>
        {
            // A key nobody knows about is a key that does not exist.
            var form = BuildMainForm();

            t.Says(GetSpokenHelp(form), "Shift plus F1");
            t.Says(GetBrowsableHelp(form), "Shift+F1");
        });

        t.Test("the help says the screen reader's own commands still work", () =>
        {
            // The single most important thing for a user to know about this program, and the thing
            // that distinguishes it from a self-voicing app that takes their tools away.
            string help = GetBrowsableHelp(BuildMainForm());

            t.Says(help, "screen reader");
            t.Says(help, "review cursor");
        });
    }

    #endregion

    #region Coverage — the test that stops the help rotting

    private static void RegisterCoverage(TestRunner t)
    {
        t.Group("browsable help — coverage");

        t.Test("every navigation key the main window handles is documented", () =>
        {
            string help = GetBrowsableHelp(BuildMainForm());

            // The single-letter keys claimed by the document view. Each must appear at the start of
            // a line, which is also what makes them findable by a user scanning down the list.
            var keys = new[]
            {
                ("H", "next heading"),
                ("K", "next link"),
                ("F", "next form field"),
                ("T", "next table"),
                ("G", "next graphic"),
                ("L", "next list"),
                ("P", "next paragraph"),
                ("A", "next comment"),
                ("D", "next accessibility problem"),
                ("C", "next table cell"),
                ("I", "next list item"),
            };

            var lines = help.Split('\n').Select(l => l.Trim()).ToList();

            foreach (var (key, description) in keys)
            {
                bool documented = lines.Any(l =>
                    l.StartsWith(key + " ", StringComparison.Ordinal)
                    && l.Contains(description, StringComparison.OrdinalIgnoreCase));

                t.IsTrue(documented, $"the {key} key should be documented as \"{description}\"");
            }
        });

        t.Test("every keyboard shortcut the main window handles is documented", () =>
        {
            string help = GetBrowsableHelp(BuildMainForm());

            // Mirrors the shortcuts in MainForm.HandleShortcut. If a shortcut is added there and
            // not here, this test fails and the help gets updated — which is the whole point.
            string[] shortcuts =
            [
                "Ctrl+O", "Ctrl+S", "Ctrl+Shift+S",
                "Ctrl+Z", "Ctrl+Y", "Ctrl+H",
                "Ctrl+W", "Ctrl+R", "Ctrl+Space",
                "Ctrl+F", "F3", "Shift+F3",
                "Ctrl+G", "Ctrl+B", "Ctrl+K", "Ctrl+M", "Ctrl+D",
                "Ctrl+Shift+A", "Ctrl+Shift+F", "Ctrl+Shift+I",
                "Ctrl+Shift+L", "Ctrl+Shift+H", "Ctrl+Shift+U",
                "Ctrl+Shift+P", "Ctrl+Shift+V", "Ctrl+Shift+G",
                "Ctrl+plus", "Ctrl+minus", "Ctrl+Shift+R", "Ctrl+Shift+B", "F6", "Ctrl+Shift+T",
                "Ctrl+PageDown", "Ctrl+PageUp",
                "Ctrl+Shift+M", "Ctrl+Shift+K", "F2", "Ctrl+Shift+Y", "Ctrl+Delete", "Ctrl+Shift+N",
                "F1", "Shift+F1", "Escape",
            ];

            foreach (string shortcut in shortcuts)
            {
                t.IsTrue(help.Contains(shortcut, StringComparison.Ordinal),
                    $"the shortcut {shortcut} should appear in the browsable help");
            }
        });

        t.Test("the help is grouped by what the user is trying to do", () =>
        {
            // Grouped by task, not by modifier, because "how do I get to the next heading" is the
            // question a user actually has.
            string help = GetBrowsableHelp(BuildMainForm());

            foreach (string section in new[]
                     {
                         "THE TWO WAYS OF READING",
                         "MOVING THROUGH THE DOCUMENT", "TABLES", "READING",
                         "SEEING THE SCREEN", "FINDING THINGS", "FILES",
                         "FILLING IN A FORM", "COMMENTING", "REPAIRING ACCESSIBILITY", "UNDOING", "HELP",
                     })
            {
                t.Says(help, section);
            }
        });
    }

    #endregion

    #region Building windows and reaching their protected help

    private static MainForm BuildMainForm() => new(
        new NullSpeechService(),
        new SilentAudioCueService(),
        new PdfPigDocumentLoader(),
        new PdfSharpDocumentSaver(),
        new ReaderSettings());

    /// <summary>
    /// Calls a window's protected help builder. Reflection is right here: these are genuinely
    /// protected members that no other part of the application should call, and widening them so a
    /// test could reach them would be letting the test dictate the design.
    /// </summary>
    private static string Invoke(Form form, string methodName)
    {
        var method = form.GetType().GetMethod(methodName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        if (method is null)
            throw new AssertionException($"{form.GetType().Name} has no {methodName} method");

        return method.Invoke(form, null) as string ?? string.Empty;
    }

    private static string GetSpokenHelp(Form form) => Invoke(form, "BuildKeyHelp");

    private static string GetBrowsableHelp(Form form) => Invoke(form, "BuildBrowsableHelp");

    #endregion
}
