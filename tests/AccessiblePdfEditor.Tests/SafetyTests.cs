using AccessiblePdfEditor.Ingestion;
using AccessiblePdfEditor.Model;
using AccessiblePdfEditor.Persistence;

namespace AccessiblePdfEditor.Tests;

// =====================================================================================
//  SafetyTests.cs
//
//  Tests that this editor cannot silently destroy a document's accessibility.
//
//  These run against REAL PDFs found on this machine, not synthetic ones. That distinction
//  is the whole point: the failure being guarded against here only appears in files
//  produced by real tools — Word, Acrobat, InDesign — which store their objects in
//  compressed object streams. A hand-built test PDF takes the safe path and proves nothing.
//
//  The specific danger: the library used for writing cannot see structure trees stored in
//  object streams. Saving such a file deletes every heading, list and image description in
//  it, while leaving the page text, the page count and the "this document is tagged" flag
//  all intact. Nothing about the result looks wrong until a blind reader opens it.
//
//  If these tests ever fail, the editor is unsafe to ship.
// =====================================================================================

internal static class SafetyTests
{
    public static void Register(TestRunner t)
    {
        var samples = FindRealPdfs();

        RegisterInspection(t, samples);
        RegisterSaveRefusal(t, samples);
        RegisterLoading(t, samples);
    }

    #region Finding real documents to test against

    /// <summary>
    /// Finds real PDFs on this machine. Tests that depend on them skip cleanly when none are
    /// present, so the suite still passes on a clean machine — but on a machine with real
    /// documents, it tests against real documents.
    /// </summary>
    private static IReadOnlyList<string> FindRealPdfs()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };

        var found = new List<string>();

        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
                continue;

            try
            {
                found.AddRange(Directory
                    .EnumerateFiles(root, "*.pdf", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        MaxRecursionDepth = 8,
                    })
                    .Take(40));
            }
            catch
            {
                // A folder that cannot be walked simply contributes nothing.
            }

            if (found.Count >= 40)
                break;
        }

        return found;
    }

    #endregion

    #region Inspection

    private static void RegisterInspection(TestRunner t, IReadOnlyList<string> samples)
    {
        t.Group("structure safety inspection");

        t.Test("inspection never throws, whatever it is given", () =>
        {
            // Runs on every save. A crash here would take the editor down at the exact moment the
            // user pressed Ctrl+S, which is the worst possible time.
            var report = StructureSafetyInspector.Inspect(Path.Combine(Path.GetTempPath(), "does-not-exist.pdf"));
            t.IsNotNull(report, "a missing file should still produce a report");
            t.IsFalse(report.HasStructureTree, "a missing file has no structure tree");
        });

        t.Test("inspection handles a file that is not a PDF", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"not-a-pdf-{Guid.NewGuid():N}.pdf");
            File.WriteAllText(path, "This is plainly not a PDF.");

            try
            {
                var report = StructureSafetyInspector.Inspect(path);
                t.IsNotNull(report, "a junk file should still produce a report");
                t.IsTrue(report.InspectionProblems.Count > 0, "the problem should be recorded");
            }
            finally
            {
                File.Delete(path);
            }
        });

        if (samples.Count == 0)
        {
            t.Test("real-document inspection (skipped: no PDFs found on this machine)", () => { });
            return;
        }

        t.Test($"inspection runs on {samples.Count} real documents without throwing", () =>
        {
            int inspected = 0;

            foreach (string path in samples)
            {
                var report = StructureSafetyInspector.Inspect(path);
                t.IsNotNull(report, $"a report for {Path.GetFileName(path)}");
                inspected++;
            }

            t.AreEqual(samples.Count, inspected, "every sample should have been inspected");
        });

        t.Test("documents whose structure the writer cannot see are detected", () =>
        {
            // The central claim. On a machine with real tagged PDFs at least some are expected to
            // hit this case, and the test reports what it found either way so the result is
            // informative rather than merely green.
            int tagged = 0, wouldDestroy = 0;

            foreach (string path in samples)
            {
                var report = StructureSafetyInspector.Inspect(path);

                if (report.HasStructureTree)
                    tagged++;

                if (report.WouldDestroyStructure)
                    wouldDestroy++;
            }

            Console.WriteLine($"        ({tagged} of {samples.Count} sampled documents are tagged; " +
                              $"{wouldDestroy} would lose their tags on save and are protected)");

            // Every document that would lose structure must also be reported as unsafe to save.
            foreach (string path in samples)
            {
                var report = StructureSafetyInspector.Inspect(path);

                if (report.WouldDestroyStructure)
                {
                    t.IsFalse(report.IsSafeToSave,
                        $"{Path.GetFileName(path)} would lose its structure, so it must not be safe to save");

                    t.Says(report.BuildWarning(), "accessibility tags");
                }
            }
        });
    }

    #endregion

    #region Save refusal

    private static void RegisterSaveRefusal(TestRunner t, IReadOnlyList<string> samples)
    {
        t.Group("save refuses to destroy accessibility");

        var atRisk = samples.FirstOrDefault(p => StructureSafetyInspector.Inspect(p).WouldDestroyStructure);

        if (atRisk is null)
        {
            t.Test("save refusal (skipped: no at-risk document found on this machine)", () => { });
            return;
        }

        t.Test("saving a document that would lose its tags is refused by default", () =>
        {
            string workingCopy = CopyToTemp(atRisk);

            try
            {
                var loader = new PdfPigDocumentLoader();
                var loaded = loader.Load(workingCopy);

                t.IsTrue(loaded.IsSuccess, $"{Path.GetFileName(atRisk)} should load");

                // A real edit, so the save is one that genuinely wants to write. Without this the
                // saver would correctly decline for having nothing to do, and the refusal being
                // tested here would never be reached.
                var history = new AccessiblePdfEditor.Editing.EditHistory(loaded.Document!);
                history.Do(new AccessiblePdfEditor.Editing.SetDocumentTitleCommand(
                    loaded.Document!, "A title added by the test"));

                long sizeBefore = new FileInfo(workingCopy).Length;

                var saver = new PdfSharpDocumentSaver();
                var result = saver.Save(loaded.Document!, new SaveOptions());

                t.AreEqual(SaveOutcome.Cancelled, result.Outcome, "the save should be refused");
                t.Says(result.Message, "Nothing has been saved");
                t.Says(result.Message, "accessibility tags");

                long sizeAfter = new FileInfo(workingCopy).Length;
                t.AreEqual(sizeBefore, sizeAfter, "the original file must be untouched");
            }
            finally
            {
                DeleteQuietly(workingCopy);
            }
        });

        t.Test("the refusal explains what would be lost and offers a way forward", () =>
        {
            var report = StructureSafetyInspector.Inspect(atRisk);
            string warning = report.BuildWarning();

            t.Says(warning, "headings");
            t.IsTrue(report.StructureElementCount > 0, "the count of what is at risk should be known");
        });
    }

    #endregion

    #region Loading real documents

    private static void RegisterLoading(TestRunner t, IReadOnlyList<string> samples)
    {
        t.Group("loading real documents");

        if (samples.Count == 0)
        {
            t.Test("real-document loading (skipped: no PDFs found on this machine)", () => { });
            return;
        }

        var subset = samples.Take(8).ToList();

        t.Test($"loading {subset.Count} real documents never throws", () =>
        {
            foreach (string path in subset)
            {
                var loader = new PdfPigDocumentLoader();
                var result = loader.Load(path);

                t.IsNotNull(result, $"a result for {Path.GetFileName(path)}");

                // Failure is an acceptable outcome for a damaged file; an exception is not.
                t.IsTrue(result.IsSuccess || result.State is DocumentLoadState.Failed
                             or DocumentLoadState.PasswordRequired,
                    $"{Path.GetFileName(path)} produced state {result.State}");
            }
        });

        t.Test("a loaded document announces something useful on opening", () =>
        {
            foreach (string path in subset)
            {
                var result = new PdfPigDocumentLoader().Load(path);
                if (!result.IsSuccess)
                    continue;

                string announcement = result.Document!.BuildOpeningAnnouncement();

                t.IsTrue(announcement.Length > 10,
                    $"{Path.GetFileName(path)} should announce more than a few characters");

                t.Says(announcement, "page");
                return;
            }
        });

        t.Test("every element in a real document can describe itself", () =>
        {
            // Guards against a subclass whose Describe throws on unusual content, which would make
            // one bad element silence the whole document.
            foreach (string path in subset.Take(3))
            {
                var result = new PdfPigDocumentLoader().Load(path);
                if (!result.IsSuccess)
                    continue;

                foreach (var element in result.Document!.ReadingOrder.Take(500))
                {
                    string spoken = element.Describe(VerbosityLevel.Normal);
                    t.IsNotNull(spoken, $"{element.Kind} should produce an announcement");
                }
            }
        });

        t.Test("reading order is assigned to every element", () =>
        {
            foreach (string path in subset.Take(3))
            {
                var result = new PdfPigDocumentLoader().Load(path);
                if (!result.IsSuccess)
                    continue;

                var order = result.Document!.ReadingOrder;
                if (order.Count == 0)
                    continue;

                for (int i = 0; i < order.Count; i++)
                    t.AreEqual(i, order[i].ReadingOrder, "reading order should be sequential");

                return;
            }
        });
    }

    #endregion

    #region Helpers

    private static string CopyToTemp(string path)
    {
        string target = Path.Combine(Path.GetTempPath(), $"apde-test-{Guid.NewGuid():N}.pdf");
        File.Copy(path, target, overwrite: true);
        return target;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);

            // The saver writes backups beside the file; clean those up too.
            string? directory = Path.GetDirectoryName(path);
            string stem = Path.GetFileNameWithoutExtension(path);

            if (directory is not null)
            {
                foreach (string stray in Directory.EnumerateFiles(directory, $"{stem}*"))
                    File.Delete(stray);
            }
        }
        catch
        {
            // Temporary files left behind are untidy, not a test failure.
        }
    }

    #endregion
}
