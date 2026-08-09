using System.Drawing.Imaging;
using AccessiblePdfEditor.Persistence;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace AccessiblePdfEditor.Tests;

// =====================================================================================
//  SampleDocumentBuilder.cs
//
//  Builds a sample PDF for trying the editor out by hand.
//
//  It is DELIBERATELY IMPERFECT. Every accessibility fault the auditor knows how to find is
//  planted in it on purpose:
//
//    an unlabelled required field        -> blocking problem
//    an image with no description        -> serious problem
//    a table with no header cells        -> serious problem
//    no document language                -> a screen reader reads it in the wrong voice
//    no document title                   -> announced by filename
//    a heading level skipped             -> the outline lies about the shape
//    a link reading "click here"         -> tells a listener nothing
//    a running footer on every page      -> read out at every page boundary
//
//  A sample where everything is already correct would prove nothing. This one gives the
//  guided repair workflow something real to walk through, and gives the person testing it
//  a way to hear the difference before and after.
//
//  Run it with:  dotnet run --project tests/AccessiblePdfEditor.Tests -- --sample <path>
// =====================================================================================

internal static class SampleDocumentBuilder
{
    #region Page geometry

    private const double PageWidth = 595;
    private const double PageHeight = 842;
    private const double LeftMargin = 60;

    /// <summary>
    /// Converts a top-down drawing position into the bottom-up rectangle a form field needs.
    /// PDF measures from the bottom of the page; XGraphics measures from the top.
    /// </summary>
    private static PdfArray FieldRectangle(PdfDocument document, double x, double top, double width, double height)
    {
        double bottom = PageHeight - top - height;

        return new PdfArray(document,
            new PdfReal(x), new PdfReal(bottom), new PdfReal(x + width), new PdfReal(bottom + height));
    }

    #endregion

    #region Building

    /// <summary>Writes the sample document and returns its path.</summary>
    public static string Build(string path)
    {
        PdfSharpEnvironment.Initialise();

        var document = new PdfDocument();

        // Deliberately NOT set: no Title, no Language. Both are audit findings, and both are
        // one-step repairs, which makes them a good first thing to try.
        document.Info.Author = "Example Council";
        document.Info.Subject = "Sample form for testing the editor";

        var fields = new PdfArray(document);

        BuildFirstPage(document, fields);
        BuildSecondPage(document, fields);

        // Continuation pages, carrying the same running footer. The unmarked-page-furniture rule
        // needs a document long enough for "repeats on most pages" to mean something, which is the
        // situation where an unmarked footer actually becomes wearing to listen to.
        for (int number = 3; number <= 6; number++)
            BuildContinuationPage(document, number);

        AttachForm(document, fields);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (directory is not null && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        document.Save(path);
        document.Dispose();

        return path;
    }

    #endregion

    #region Page one — the form

    private static void BuildFirstPage(PdfDocument document, PdfArray fields)
    {
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(PageWidth);
        page.Height = XUnit.FromPoint(PageHeight);

        using (var gfx = XGraphics.FromPdfPage(page))
        {
            var title = new XFont("Arial", 22, XFontStyleEx.Bold);
            var heading = new XFont("Arial", 15, XFontStyleEx.Bold);
            var body = new XFont("Arial", 11);
            var small = new XFont("Arial", 8);

            // Sized well above the body text, so the layout analysis infers it as a level 1 heading.
            gfx.DrawString("Housing Benefit Application", title, XBrushes.Black, new XPoint(LeftMargin, 70));

            gfx.DrawString(
                "Please complete every section. Fields marked as required must be filled in before",
                body, XBrushes.Black, new XPoint(LeftMargin, 105));

            gfx.DrawString(
                "this form can be submitted. Use Tab or the F key to move between fields.",
                body, XBrushes.Black, new XPoint(LeftMargin, 122));

            gfx.DrawString("Your details", heading, XBrushes.Black, new XPoint(LeftMargin, 165));

            // Labels sit immediately left of each field on the same line, which is the layout the
            // label recovery is built to read.
            gfx.DrawString("Full name:", body, XBrushes.Black, new XPoint(LeftMargin, 205));
            gfx.DrawString("Date of birth:", body, XBrushes.Black, new XPoint(LeftMargin, 240));
            gfx.DrawString("Email address:", body, XBrushes.Black, new XPoint(LeftMargin, 275));
            gfx.DrawString("Reference number:", body, XBrushes.Black, new XPoint(LeftMargin, 310));

            gfx.DrawString("How should we contact you?", body, XBrushes.Black, new XPoint(LeftMargin, 355));
            gfx.DrawString("By email", body, XBrushes.Black, new XPoint(LeftMargin + 30, 385));
            gfx.DrawString("By phone", body, XBrushes.Black, new XPoint(LeftMargin + 30, 410));
            gfx.DrawString("By post", body, XBrushes.Black, new XPoint(LeftMargin + 30, 435));

            gfx.DrawString("Country:", body, XBrushes.Black, new XPoint(LeftMargin, 475));

            gfx.DrawString("I confirm the information above is correct", body, XBrushes.Black,
                new XPoint(LeftMargin + 30, 515));

            // A field with no printed label at all, so nothing can be recovered from the page.
            gfx.DrawString("Office use only", heading, XBrushes.Black, new XPoint(LeftMargin, 560));

            // A running footer, repeated on both pages and never marked as page furniture, so it is
            // read out at every page boundary until somebody marks it.
            gfx.DrawString("Housing Benefit Application — Form HB1 — Page 1 of 6", small,
                XBrushes.Gray, new XPoint(LeftMargin, 800));
        }

        // ---- The fields themselves ----

        // Properly labelled and required. The good case, for comparison.
        AddTextField(document, page, fields, "applicantFullName", "Full name",
            FieldRectangle(document, 190, 190, 280, 20), required: true);

        // Labelled, and its name makes the date format inferable.
        AddTextField(document, page, fields, "dateOfBirth", "Date of birth",
            FieldRectangle(document, 190, 225, 160, 20), required: true);

        AddTextField(document, page, fields, "emailAddress", "Email address",
            FieldRectangle(document, 190, 260, 280, 20), required: false);

        // Read-only, so it is announced but never asked for.
        AddTextField(document, page, fields, "referenceNumber", null,
            FieldRectangle(document, 190, 295, 160, 20), required: false, readOnly: true,
            value: "HB1-2026-004821");

        AddRadioGroup(document, page, fields, "contactMethod", "How should we contact you",
        [
            ("Email", FieldRectangle(document, LeftMargin + 8, 372, 14, 14)),
            ("Phone", FieldRectangle(document, LeftMargin + 8, 397, 14, 14)),
            ("Post", FieldRectangle(document, LeftMargin + 8, 422, 14, 14)),
        ]);

        // Export values differ from display text, which is the case that breaks naive form tools.
        AddComboBox(document, page, fields, "country", "Country",
            FieldRectangle(document, 190, 460, 220, 20),
        [
            ("GB", "United Kingdom"),
            ("IE", "Ireland"),
            ("NL", "Netherlands"),
            ("NZ", "New Zealand"),
        ]);

        AddCheckBox(document, page, fields, "confirmCorrect", "I confirm the information is correct",
            FieldRectangle(document, LeftMargin + 8, 502, 14, 14), required: true);

        // THE BLOCKER: required, generically named, and with no printed label beside it. Nothing
        // can be recovered, so it announces as an unlabelled field until somebody names it.
        AddTextField(document, page, fields, "Text1", null,
            FieldRectangle(document, 190, 580, 200, 20), required: true);
    }

    #endregion

    #region Page two — declaration, image, table and signature

    private static void BuildSecondPage(PdfDocument document, PdfArray fields)
    {
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(PageWidth);
        page.Height = XUnit.FromPoint(PageHeight);

        using (var gfx = XGraphics.FromPdfPage(page))
        {
            var heading = new XFont("Arial", 15, XFontStyleEx.Bold);

            // Only fractionally larger than the body text. That infers as a level 4 heading
            // directly after a level 2, which is a skipped level: someone navigating by headings
            // hears level 4 and goes looking for the level 3 section that was never there.
            var subHeading = new XFont("Arial", 11.5, XFontStyleEx.Bold);
            var body = new XFont("Arial", 11);
            var small = new XFont("Arial", 8);

            gfx.DrawString("Declaration", heading, XBrushes.Black, new XPoint(LeftMargin, 80));

            gfx.DrawString(
                "I declare that the information I have given on this form is correct and complete.",
                body, XBrushes.Black, new XPoint(LeftMargin, 115));

            gfx.DrawString(
                "I understand that if I give information that is incorrect or incomplete, action may",
                body, XBrushes.Black, new XPoint(LeftMargin, 132));

            gfx.DrawString("be taken against me.", body, XBrushes.Black, new XPoint(LeftMargin, 149));

            // An undescribed image: the serious finding the guided workflow exists to fix.
            DrawUndescribedChart(document, gfx);

            // Set well clear of the table beneath it. Too close and the layout analysis groups the
            // heading into the same block as the rows, which makes it too long to read as a
            // heading at all — the same thing that happens with real cramped documents.
            gfx.DrawString("Payments received", subHeading, XBrushes.Black, new XPoint(LeftMargin, 395));

            DrawUntaggedTable(gfx, body);

            gfx.DrawString("For more information about how we use your data ", body, XBrushes.Black,
                new XPoint(LeftMargin, 545));

            // Uninformative link text, with a real link annotation over it.
            gfx.DrawString("click here", body, XBrushes.Blue, new XPoint(LeftMargin + 265, 545));

            gfx.DrawString("Signature:", body, XBrushes.Black, new XPoint(LeftMargin, 620));

            gfx.DrawString("Housing Benefit Application — Form HB1 — Page 2 of 6", small,
                XBrushes.Gray, new XPoint(LeftMargin, 800));
        }

        // The link, over the words "click here".
        page.AddWebLink(
            new PdfRectangle(new XPoint(LeftMargin + 263, PageHeight - 548),
                new XPoint(LeftMargin + 320, PageHeight - 534)),
            "https://example.gov.uk/privacy-notice/housing-benefit");

        AddSignatureField(document, page, fields, "applicantSignature", "Sign here",
            FieldRectangle(document, 150, 595, 240, 60));
    }

    /// <summary>
    /// A continuation page carrying the same running footer as every other page, and never marked
    /// as page furniture — so a reader announces it at every page boundary.
    /// </summary>
    private static void BuildContinuationPage(PdfDocument document, int number)
    {
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(PageWidth);
        page.Height = XUnit.FromPoint(PageHeight);

        using var gfx = XGraphics.FromPdfPage(page);

        var heading = new XFont("Arial", 15, XFontStyleEx.Bold);
        var body = new XFont("Arial", 11);
        var small = new XFont("Arial", 8);

        gfx.DrawString($"Notes and guidance, part {number - 2}", heading, XBrushes.Black,
            new XPoint(LeftMargin, 80));

        string[] paragraphs =
        [
            "This section explains how your application will be assessed and what happens next.",
            "We will write to you within fifteen working days of receiving your completed form.",
            "If any of your circumstances change before then, you must tell us straight away.",
            "You can ask for a review of any decision within one month of the date on the letter.",
        ];

        double y = 120;

        foreach (string paragraph in paragraphs)
        {
            gfx.DrawString(paragraph, body, XBrushes.Black, new XPoint(LeftMargin, y));
            y += 34;
        }

        // The same footer text on every page. Numbers are normalised out when the editor looks for
        // repetition, so "Page 3 of 6" and "Page 4 of 6" are recognised as one running footer.
        gfx.DrawString($"Housing Benefit Application — Form HB1 — Page {number} of 6", small,
            XBrushes.Gray, new XPoint(LeftMargin, 800));
    }

    /// <summary>
    /// Draws a chart as a real embedded image, so it comes back as a figure needing a description
    /// rather than as vector graphics the reader would ignore.
    /// </summary>
    private static void DrawUndescribedChart(PdfDocument document, XGraphics gfx)
    {
        using var bitmap = new Bitmap(360, 200);

        using (var canvas = Graphics.FromImage(bitmap))
        {
            canvas.Clear(Color.White);

            using var axis = new Pen(Color.Black, 2);
            canvas.DrawLine(axis, 40, 170, 340, 170);
            canvas.DrawLine(axis, 40, 20, 40, 170);

            int[] values = [70, 110, 95, 140];
            using var bar = new SolidBrush(Color.FromArgb(40, 90, 160));

            for (int i = 0; i < values.Length; i++)
                canvas.FillRectangle(bar, 70 + i * 70, 170 - values[i], 44, values[i]);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;

        using var image = XImage.FromStream(stream);
        gfx.DrawImage(image, LeftMargin, 175, 300, 167);
    }

    /// <summary>
    /// Draws a table as plain positioned text with no tagging, which is how the overwhelming
    /// majority of real PDF tables arrive: visually a table, structurally nothing.
    /// </summary>
    private static void DrawUntaggedTable(XGraphics gfx, XFont body)
    {
        string[][] rows =
        [
            ["Month", "Amount", "Status"],
            ["January", "£412.00", "Paid"],
            ["February", "£412.00", "Paid"],
            ["March", "£398.50", "Pending"],
        ];

        double[] columns = [LeftMargin, LeftMargin + 150, LeftMargin + 260];
        double y = 450;

        foreach (string[] row in rows)
        {
            for (int i = 0; i < row.Length; i++)
                gfx.DrawString(row[i], body, XBrushes.Black, new XPoint(columns[i], y));

            y += 22;
        }
    }

    #endregion

    #region Field construction
    // All raw dictionaries: PDFsharp 6.2.4 has no public API for creating form fields, so this is
    // the same route the editor's own writer uses.

    private static PdfDictionary NewWidget(
        PdfDocument document, PdfPage page, string fieldType, string name, string? toolTip, PdfArray rectangle)
    {
        var field = new PdfDictionary(document);

        field.Elements.SetName("/Type", "Annot");
        field.Elements.SetName("/Subtype", "Widget");
        field.Elements.SetName("/FT", fieldType);
        field.Elements.SetString("/T", name);

        if (toolTip is { Length: > 0 })
            field.Elements.SetString("/TU", toolTip);

        field.Elements.SetValue("/Rect", rectangle);

        // Print flag, so the field appears when the form is printed.
        field.Elements.SetInteger("/F", 4);

        document.Internals.AddObject(field);
        field.Elements.SetReference("/P", page);

        AddToAnnotations(document, page, field);
        return field;
    }

    private static void AddToAnnotations(PdfDocument document, PdfPage page, PdfDictionary widget)
    {
        var annotations = page.Elements.GetArray("/Annots");

        if (annotations is null)
        {
            annotations = new PdfArray(document);
            page.Elements.SetValue("/Annots", annotations);
        }

        annotations.Elements.Add(widget.Reference!);
    }

    private static void AddTextField(
        PdfDocument document, PdfPage page, PdfArray fields,
        string name, string? toolTip, PdfArray rectangle,
        bool required, bool readOnly = false, string? value = null)
    {
        var field = NewWidget(document, page, "Tx", name, toolTip, rectangle);

        field.Elements.SetString("/DA", "/Helv 10 Tf 0 g");

        int flags = 0;
        if (readOnly) flags |= 1;   // bit 1
        if (required) flags |= 2;   // bit 2
        field.Elements.SetInteger("/Ff", flags);

        if (value is { Length: > 0 })
            field.Elements.SetString("/V", value);

        fields.Elements.Add(field.Reference!);
    }

    private static void AddCheckBox(
        PdfDocument document, PdfPage page, PdfArray fields,
        string name, string toolTip, PdfArray rectangle, bool required)
    {
        var field = NewWidget(document, page, "Btn", name, toolTip, rectangle);

        field.Elements.SetInteger("/Ff", required ? 2 : 0);
        field.Elements.SetName("/V", "Off");
        field.Elements.SetName("/AS", "Off");

        // The appearance dictionary is what defines the on-state name; the editor reads it from
        // here rather than assuming /Yes.
        AddToggleAppearances(document, field, "Yes");

        fields.Elements.Add(field.Reference!);
    }

    private static void AddRadioGroup(
        PdfDocument document, PdfPage page, PdfArray fields,
        string name, string toolTip, (string Export, PdfArray Rectangle)[] options)
    {
        var group = new PdfDictionary(document);
        group.Elements.SetName("/FT", "Btn");
        group.Elements.SetString("/T", name);
        group.Elements.SetString("/TU", toolTip);

        // Bit 16 is Radio, bit 15 NoToggleToOff, bit 2 Required.
        group.Elements.SetInteger("/Ff", 32768 + 16384 + 2);
        group.Elements.SetName("/V", "Off");

        document.Internals.AddObject(group);

        var kids = new PdfArray(document);

        foreach (var (export, rectangle) in options)
        {
            var kid = new PdfDictionary(document);
            kid.Elements.SetName("/Type", "Annot");
            kid.Elements.SetName("/Subtype", "Widget");
            kid.Elements.SetValue("/Rect", rectangle);
            kid.Elements.SetInteger("/F", 4);
            kid.Elements.SetName("/AS", "Off");

            document.Internals.AddObject(kid);

            kid.Elements.SetReference("/Parent", group);
            kid.Elements.SetReference("/P", page);

            AddToggleAppearances(document, kid, export);
            AddToAnnotations(document, page, kid);

            kids.Elements.Add(kid.Reference!);
        }

        group.Elements.SetValue("/Kids", kids);
        fields.Elements.Add(group.Reference!);
    }

    private static void AddComboBox(
        PdfDocument document, PdfPage page, PdfArray fields,
        string name, string toolTip, PdfArray rectangle, (string Export, string Display)[] options)
    {
        var field = NewWidget(document, page, "Ch", name, toolTip, rectangle);

        // Bit 18 is Combo.
        field.Elements.SetInteger("/Ff", 131072);
        field.Elements.SetString("/DA", "/Helv 10 Tf 0 g");

        var choices = new PdfArray(document);

        foreach (var (export, display) in options)
        {
            // The pair form: export value first, display text second. Reading the wrong one is how
            // a country list ends up announced as codes.
            var pair = new PdfArray(document, new PdfString(export), new PdfString(display));
            choices.Elements.Add(pair);
        }

        field.Elements.SetValue("/Opt", choices);
        fields.Elements.Add(field.Reference!);
    }

    private static void AddSignatureField(
        PdfDocument document, PdfPage page, PdfArray fields,
        string name, string toolTip, PdfArray rectangle)
    {
        var field = NewWidget(document, page, "Sig", name, toolTip, rectangle);
        fields.Elements.Add(field.Reference!);
    }

    /// <summary>
    /// Gives a toggle an appearance dictionary with a named on-state, drawn as a simple box and
    /// mark. The on-state NAME is what matters most: the editor reads it from here so that a form
    /// using /On or /Oui instead of /Yes still saves correctly.
    /// </summary>
    private static void AddToggleAppearances(PdfDocument document, PdfDictionary widget, string onState)
    {
        var rectangle = widget.Elements.GetRectangle("/Rect");
        double width = Math.Abs(rectangle.X2 - rectangle.X1);
        double height = Math.Abs(rectangle.Y2 - rectangle.Y1);

        var normal = new PdfDictionary(document);
        normal.Elements.SetValue("/" + onState, BuildToggleAppearance(document, width, height, marked: true));
        normal.Elements.SetValue("/Off", BuildToggleAppearance(document, width, height, marked: false));

        var appearances = new PdfDictionary(document);
        appearances.Elements.SetValue("/N", normal);

        widget.Elements.SetValue("/AP", appearances);
    }

    private static PdfReference BuildToggleAppearance(
        PdfDocument document, double width, double height, bool marked)
    {
        string content = marked
            ? $"q 0 G 0.8 w 1 1 {width - 2:0.##} {height - 2:0.##} re S " +
              $"3 3 m {width - 3:0.##} {height - 3:0.##} l S " +
              $"3 {height - 3:0.##} m {width - 3:0.##} 3 l S Q"
            : $"q 0 G 0.8 w 1 1 {width - 2:0.##} {height - 2:0.##} re S Q";

        var form = new PdfDictionary(document);
        form.Elements.SetName("/Type", "XObject");
        form.Elements.SetName("/Subtype", "Form");
        form.Elements.SetInteger("/FormType", 1);

        form.Elements.SetValue("/BBox", new PdfArray(document,
            new PdfInteger(0), new PdfInteger(0), new PdfReal(width), new PdfReal(height)));

        form.CreateStream(System.Text.Encoding.Latin1.GetBytes(content));
        document.Internals.AddObject(form);

        return form.Reference!;
    }

    private static void AttachForm(PdfDocument document, PdfArray fields)
    {
        var helvetica = new PdfDictionary(document);
        helvetica.Elements.SetName("/Type", "Font");
        helvetica.Elements.SetName("/Subtype", "Type1");
        helvetica.Elements.SetName("/BaseFont", "Helvetica");
        helvetica.Elements.SetName("/Encoding", "WinAnsiEncoding");
        document.Internals.AddObject(helvetica);

        var fonts = new PdfDictionary(document);
        fonts.Elements.SetReference("/Helv", helvetica);

        var resources = new PdfDictionary(document);
        resources.Elements.SetValue("/Font", fonts);

        var acroForm = new PdfDictionary(document);
        acroForm.Elements.SetValue("/Fields", fields);
        acroForm.Elements.SetString("/DA", "/Helv 10 Tf 0 g");
        acroForm.Elements.SetValue("/DR", resources);
        acroForm.Elements.SetInteger("/SigFlags", 3);
        acroForm.Elements.SetBoolean("/NeedAppearances", true);

        document.Internals.AddObject(acroForm);
        document.Internals.Catalog.Elements.SetReference("/AcroForm", acroForm);
    }

    #endregion
}
