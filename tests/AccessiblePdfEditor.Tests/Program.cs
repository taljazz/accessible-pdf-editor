using AccessiblePdfEditor.Persistence;

namespace AccessiblePdfEditor.Tests;

// =====================================================================================
//  Program.cs
//
//  Entry point for the test suite. Exits non-zero on any failure so it can gate a build.
// =====================================================================================

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Building the sample is a separate job from running the tests, so it is offered as a
        // switch rather than as a side effect of the suite: nobody wants a file appearing in their
        // project every time they run the build.
        if (args.Length > 0 && args[0] is "--sample" or "-s")
        {
            string target = args.Length > 1
                ? args[1]
                : Path.Combine(Directory.GetCurrentDirectory(), "Sample form.pdf");

            string written = SampleDocumentBuilder.Build(target);

            Console.WriteLine($"Sample document written to:{Environment.NewLine}  {written}");
            Console.WriteLine();
            Console.WriteLine("It contains deliberate accessibility faults for the checker to find.");
            return 0;
        }

        Console.WriteLine("Accessible PDF Editor — test suite");
        Console.WriteLine(new string('═', 60));

        // The same one-time setup the application does at startup. Font handling must be
        // configured before anything creates a font, and it can only be done once per process.
        PdfSharpEnvironment.Initialise();

        Console.WriteLine(PdfSharpEnvironment.CanDrawText
            ? $"Font support: available ({PdfSharpEnvironment.DefaultFontFamily})"
            : $"Font support: UNAVAILABLE — {PdfSharpEnvironment.FontFailureReason}");

        var runner = new TestRunner();

        ModelTests.Register(runner);
        BehaviourTests.Register(runner);
        FormOperationTests.Register(runner);
        RenderingTests.Register(runner);
        TableDetectionTests.Register(runner);
        NavigationIntegrationTests.Register(runner);
        TableViewTests.Register(runner);
        BrowseViewTests.Register(runner);
        AnnotationTests.Register(runner);
        ContentTaggingTests.Register(runner);
        CommandLineTests.Register(runner);
        HelpTests.Register(runner);
        RoundTripTests.Register(runner);
        SafetyTests.Register(runner);

        return runner.Run();
    }
}
