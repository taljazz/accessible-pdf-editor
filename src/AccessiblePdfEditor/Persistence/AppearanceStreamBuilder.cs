using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace AccessiblePdfEditor.Persistence;

// =====================================================================================
//  AppearanceStreamBuilder.cs
//
//  Draws the visible contents of a filled form field.
//
//  A PDF form field stores its value (/V) and, separately, a picture of that value
//  (/AP — the appearance stream). Writing only the value produces a file where the data is
//  present and correct but the page LOOKS blank. There is a flag, /NeedAppearances, that
//  asks the viewer to draw it for you, and Acrobat honours it — but Chrome's and Edge's
//  built-in viewers and several mobile readers ignore it entirely.
//
//  That matters here more than it would elsewhere. A blind user fills in a form, saves it,
//  and sends it to someone sighted. If the appearance is missing, the recipient opens it in
//  a browser and sees an empty form. The user has no way to discover this and no way to be
//  warned about it — they did everything right.
//
//  So this class generates the appearance itself, and /NeedAppearances is set as well, as a
//  belt-and-braces measure rather than as the plan.
//
//  Everything is drawn with the standard /Helvetica font, which every PDF viewer has built
//  in. That avoids embedding a font and avoids the font-resolver dependency entirely — at
//  the cost of being limited to Latin-1, which is handled explicitly below.
// =====================================================================================

#region AppearanceStreamBuilder

/// <summary>Builds /AP appearance streams for filled form fields.</summary>
internal static class AppearanceStreamBuilder
{
    #region Layout constants
    // Chosen to match what Acrobat produces, so a form this editor fills looks the same as one
    // filled anywhere else. Getting these wrong is not a correctness problem but it is immediately
    // visible to the sighted person receiving the form.

    /// <summary>Inset from the field's edge to its text, in points.</summary>
    private const double HorizontalPadding = 2.0;

    /// <summary>Default text size when the field does not specify one.</summary>
    private const double DefaultFontSize = 10.0;

    /// <summary>Rough ratio of Helvetica's cap height to its point size, for vertical centring.</summary>
    private const double CapHeightRatio = 0.72;

    #endregion

    #region Building a text appearance

    /// <summary>
    /// Builds and attaches the appearance stream for a text or choice field's widget.
    /// </summary>
    /// <returns>True when an appearance was written.</returns>
    public static bool WriteTextAppearance(
        PdfDocument document,
        PdfDictionary widget,
        string text,
        bool isMultiline)
    {
        try
        {
            var rectangle = ReadRectangle(widget);
            if (rectangle is null)
                return false;

            double width = Math.Abs(rectangle.X2 - rectangle.X1);
            double height = Math.Abs(rectangle.Y2 - rectangle.Y1);

            if (width <= 0 || height <= 0)
                return false;

            double fontSize = ReadFontSize(widget, height);
            string content = BuildTextContent(text, width, height, fontSize, isMultiline);

            var form = CreateFormXObject(document, width, height, content);
            AttachNormalAppearance(document, widget, form);
            return true;
        }
        catch
        {
            // A field whose appearance cannot be drawn still has a correct value. Losing the
            // picture is a visual defect; failing the save would lose the user's work.
            return false;
        }
    }

    /// <summary>
    /// Builds the content-stream operators that draw a field's text.
    ///
    /// The whole thing is wrapped in /Tx BMC ... EMC, which marks it as form field content, and in
    /// a clipping rectangle so that text longer than the field is cut off at the edge rather than
    /// spilling across the page.
    /// </summary>
    private static string BuildTextContent(
        string text, double width, double height, double fontSize, bool isMultiline)
    {
        var builder = new StringBuilder(256);

        builder.Append("/Tx BMC\nq\n");

        // Clip to the field's box, inset slightly, exactly as Acrobat does.
        AppendNumber(builder, HorizontalPadding / 2);
        builder.Append(' ');
        AppendNumber(builder, HorizontalPadding / 2);
        builder.Append(' ');
        AppendNumber(builder, Math.Max(0, width - HorizontalPadding));
        builder.Append(' ');
        AppendNumber(builder, Math.Max(0, height - HorizontalPadding));
        builder.Append(" re\nW\nn\n");

        builder.Append("BT\n/Helv ");
        AppendNumber(builder, fontSize);
        builder.Append(" Tf\n0 g\n");

        if (isMultiline)
        {
            double lineHeight = fontSize * 1.15;
            double y = height - HorizontalPadding - fontSize;

            builder.Append("1 0 0 1 ");
            AppendNumber(builder, HorizontalPadding);
            builder.Append(' ');
            AppendNumber(builder, y);
            builder.Append(" Tm\n");

            AppendNumber(builder, lineHeight);
            builder.Append(" TL\n");

            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    builder.Append("T*\n");

                builder.Append(EncodeString(lines[i])).Append(" Tj\n");
            }
        }
        else
        {
            // Single-line fields centre their text vertically in the box, which is what every
            // other tool produces and what the recipient will expect to see.
            double baseline = (height - fontSize * CapHeightRatio) / 2;

            builder.Append("1 0 0 1 ");
            AppendNumber(builder, HorizontalPadding);
            builder.Append(' ');
            AppendNumber(builder, Math.Max(HorizontalPadding, baseline));
            builder.Append(" Tm\n");

            builder.Append(EncodeString(text.Replace("\n", " "))).Append(" Tj\n");
        }

        builder.Append("ET\nQ\nEMC\n");
        return builder.ToString();
    }

    #endregion

    #region Creating and attaching the form XObject

    /// <summary>
    /// Wraps content-stream operators in a Form XObject, which is what an /AP entry must contain.
    /// </summary>
    private static PdfDictionary CreateFormXObject(
        PdfDocument document, double width, double height, string content)
    {
        var form = new PdfDictionary(document);

        form.Elements.SetName("/Type", "XObject");
        form.Elements.SetName("/Subtype", "Form");
        form.Elements.SetInteger("/FormType", 1);

        var box = new PdfArray(document,
            new PdfInteger(0), new PdfInteger(0),
            new PdfReal(width), new PdfReal(height));

        form.Elements.SetValue("/BBox", box);
        form.Elements.SetValue("/Resources", BuildResources(document));

        // The stream must be created before the object becomes indirect, and the content must be
        // Latin-1 bytes because that is the encoding the standard /Helvetica font uses.
        form.CreateStream(Encoding.Latin1.GetBytes(content));

        document.Internals.AddObject(form);
        return form;
    }

    /// <summary>
    /// Builds the resource dictionary naming the font used by the appearance.
    ///
    /// /Helvetica is one of the fourteen fonts every PDF viewer is required to have built in, so
    /// nothing is embedded and the file does not grow. The cost is that only Latin-1 characters can
    /// be drawn, which is handled in <see cref="EncodeString"/>.
    /// </summary>
    private static PdfDictionary BuildResources(PdfDocument document)
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
        resources.Elements.SetValue("/ProcSet",
            new PdfArray(document, new PdfName("/PDF"), new PdfName("/Text")));

        return resources;
    }

    /// <summary>Sets a widget's normal appearance to a form XObject.</summary>
    private static void AttachNormalAppearance(
        PdfDocument document, PdfDictionary widget, PdfDictionary form)
    {
        var appearances = widget.Elements.GetDictionary("/AP");

        if (appearances is null)
        {
            appearances = new PdfDictionary(document);
            widget.Elements.SetValue("/AP", appearances);
        }

        appearances.Elements.SetReference("/N", form);
    }

    #endregion

    #region Toggle appearances — checkboxes and radio buttons
    // Toggles are the easy case: the document already contains both pictures, drawn by whoever made
    // the form. Only the /AS entry, which chooses between them, has to change. Generating anything
    // here would replace the form's own tick or dot with ours, which would look wrong.

    /// <summary>
    /// Sets which of a toggle's existing appearances is shown, by writing /AS.
    /// </summary>
    /// <returns>
    /// True when the requested state exists in the widget's appearance dictionary. False means the
    /// form does not define that state, which is worth knowing: the value will still be written,
    /// but nothing will be visible.
    /// </returns>
    public static bool SetToggleAppearanceState(PdfDictionary widget, string stateName)
    {
        try
        {
            widget.Elements.SetName("/AS", stateName);

            var appearances = widget.Elements.GetDictionary("/AP");
            var normal = appearances?.Elements.GetDictionary("/N");

            if (normal is null)
                return false;

            return normal.Elements.ContainsKey("/" + stateName.TrimStart('/'));
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Reading the widget's own formatting

    private static PdfRectangle? ReadRectangle(PdfDictionary widget)
    {
        try
        {
            var rectangle = widget.Elements.GetRectangle("/Rect");
            return rectangle.IsZero ? null : rectangle;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the text size from the field's default appearance string (/DA), which looks like
    /// "/Helv 0 Tf 0 g". A size of zero means "fit the text to the box", which is resolved here to
    /// a size that fits, because a literal zero would draw nothing at all.
    /// </summary>
    private static double ReadFontSize(PdfDictionary widget, double fieldHeight)
    {
        double fitted = Math.Clamp(fieldHeight * 0.65, 6.0, 14.0);

        try
        {
            string? defaultAppearance = widget.Elements.GetString("/DA");

            if (string.IsNullOrWhiteSpace(defaultAppearance))
            {
                // Widgets often inherit /DA from their parent field or from the form itself.
                var parent = widget.Elements.GetDictionary("/Parent");
                defaultAppearance = parent?.Elements.GetString("/DA");
            }

            if (string.IsNullOrWhiteSpace(defaultAppearance))
                return fitted;

            var parts = defaultAppearance.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < parts.Length; i++)
            {
                if (!parts[i].Equals("Tf", StringComparison.Ordinal) || i == 0)
                    continue;

                if (double.TryParse(parts[i - 1], NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double size))
                {
                    return size > 0 ? size : fitted;
                }
            }
        }
        catch
        {
            // Malformed /DA is common. The fitted size is always usable.
        }

        return fitted > 0 ? fitted : DefaultFontSize;
    }

    #endregion

    #region Content-stream encoding
    // Two things here would silently corrupt a content stream if got wrong, and both have bitten
    // real projects: a locale that formats decimals with a comma, and unescaped parentheses in a
    // string literal.

    /// <summary>
    /// Appends a number in the invariant culture. A machine set to a locale using comma decimal
    /// separators would otherwise write "12,5" into the content stream, which is two operands where
    /// one was meant and corrupts everything after it.
    /// </summary>
    private static void AppendNumber(StringBuilder builder, double value) =>
        builder.Append(value.ToString("0.###", CultureInfo.InvariantCulture));

    /// <summary>
    /// Encodes text as a PDF string literal.
    ///
    /// Parentheses and backslashes must be escaped or they terminate the string early and corrupt
    /// the stream — a name like "Smith (Jr)" would break the whole appearance. Characters outside
    /// Latin-1 cannot be drawn by the standard font and are replaced with a question mark rather
    /// than dropped: a visible placeholder tells the recipient something is missing, where silence
    /// would let a wrong name pass unnoticed.
    /// </summary>
    private static string EncodeString(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        builder.Append('(');

        foreach (char c in text)
        {
            switch (c)
            {
                case '(':
                case ')':
                case '\\':
                    builder.Append('\\').Append(c);
                    break;

                case '\r':
                    builder.Append("\\r");
                    break;

                case '\t':
                    builder.Append("\\t");
                    break;

                default:
                    if (c > 0xFF)
                        builder.Append('?');
                    else
                        builder.Append(c);
                    break;
            }
        }

        builder.Append(')');
        return builder.ToString();
    }

    /// <summary>
    /// Whether text can be drawn by the standard font. Callers use this to warn the user before
    /// saving, since anything outside Latin-1 will appear as a question mark on the page even
    /// though the stored value remains correct.
    /// </summary>
    public static bool CanRenderInStandardFont(string text)
    {
        foreach (char c in text)
        {
            if (c > 0xFF)
                return false;
        }

        return true;
    }

    #endregion
}

#endregion
