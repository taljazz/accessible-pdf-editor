using System.Drawing.Imaging;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using AccessiblePdfEditor.Model;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace AccessiblePdfEditor.Persistence;

// =====================================================================================
//  SignatureWriter.cs
//
//  Draws a captured signature into a signature field's appearance.
//
//  Built by hand as a raw Form XObject rather than through PDFsharp's XForm, because XForm
//  does not expose the underlying PDF object that has to be attached as the widget's /AP,
//  and because the hand-built route has already been proven to survive this project's save
//  pipeline for text field appearances.
//
//  WHAT THIS PRODUCES IS A VISIBLE SIGNATURE, NOT A CRYPTOGRAPHIC ONE. It is a picture of
//  a signature placed in the right box. That is what the overwhelming majority of
//  e-signing actually is, and it is usually sufficient — but it proves nothing about who
//  placed it or whether the document changed afterwards, and the UI says so before
//  applying it. This file is named for what it does so that nobody reading the codebase
//  later mistakes it for signing.
//
//  All three capture routes converge here: an image of a real handwritten signature, a
//  typed name, or strokes drawn on the signature pad.
// =====================================================================================

#region SignatureWriter

/// <summary>Draws a captured signature into a form field's appearance stream.</summary>
internal static class SignatureWriter
{
    #region Layout constants
    // Proportions of the field's box. The mark sits above, the printed name and date below, which
    // is how a signature block is laid out on paper and therefore what a sighted recipient expects.

    /// <summary>Fraction of the height given to the printed name and date.</summary>
    private const double CaptionHeightFraction = 0.28;

    /// <summary>Inset from the field's edge, in points.</summary>
    private const double Padding = 2.0;

    /// <summary>Point size of the printed name.</summary>
    private const double CaptionFontSize = 7.0;

    #endregion

    #region Entry point

    /// <summary>
    /// Draws a signature into a widget's normal appearance.
    /// </summary>
    /// <returns>True when an appearance was written. A warning is returned for anything the user
    /// should know that did not stop the write.</returns>
    public static bool WriteInto(
        PdfDocument document,
        PdfDictionary widget,
        SignatureMark mark,
        out string? warning)
    {
        warning = null;

        try
        {
            var rectangle = widget.Elements.GetRectangle("/Rect");

            if (rectangle.IsZero)
            {
                warning = "The signature field has no position on the page, so nothing could be drawn.";
                return false;
            }

            double width = Math.Abs(rectangle.X2 - rectangle.X1);
            double height = Math.Abs(rectangle.Y2 - rectangle.Y1);

            if (width <= 1 || height <= 1)
            {
                warning = "The signature field is too small to draw into.";
                return false;
            }

            var resources = new PdfDictionary(document);
            var content = new StringBuilder(512);

            content.Append("q\n");

            bool drewMark = mark.Source switch
            {
                SignatureSource.Image => DrawImage(document, resources, content, mark, width, height, ref warning),
                SignatureSource.TypedName => DrawTypedName(resources, content, mark, width, height),
                SignatureSource.Drawn => DrawStrokes(content, mark, width, height),
                _ => false,
            };

            if (!drewMark)
            {
                warning ??= "The signature could not be drawn.";
                return false;
            }

            DrawCaption(resources, content, mark, width, height);
            content.Append("Q\n");

            AttachAppearance(document, widget, resources, content.ToString(), width, height);
            DescribeForScreenReaders(widget, mark);

            return true;
        }
        catch (Exception ex)
        {
            warning = $"The signature could not be drawn: {ex.Message}";
            return false;
        }
    }

    #endregion

    #region Drawing strokes
    // The simplest of the three and the one needing no resources at all: a signature is a set of
    // polylines, and PDF draws those with move-to, line-to and stroke.

    private static bool DrawStrokes(
        StringBuilder content, SignatureMark mark, double width, double height)
    {
        if (mark.Strokes.Count == 0)
            return false;

        double markHeight = height * (1 - CaptionHeightFraction);

        // Pen width scaled to the field, so a signature in a large box does not come out as a
        // hairline and one in a small box does not come out as a blot.
        double penWidth = Math.Clamp(Math.Min(width, markHeight) / 60.0, 0.5, 2.5);

        content.Append("0 G\n");
        Number(content, penWidth);
        content.Append(" w\n1 J\n1 j\n");

        foreach (var stroke in mark.Strokes)
        {
            var points = stroke.Points;
            if (points.Count < 2)
                continue;

            for (int i = 0; i < points.Count; i++)
            {
                // The pad's Y runs downwards like a screen; PDF's runs upwards from the bottom of
                // the page. Flipping here is what stops every signature coming out upside down.
                double x = Padding + points[i].X * (width - Padding * 2);
                double y = height - Padding - points[i].Y * (markHeight - Padding * 2);

                Number(content, x);
                content.Append(' ');
                Number(content, y);
                content.Append(i == 0 ? " m\n" : " l\n");
            }

            content.Append("S\n");
        }

        return true;
    }

    #endregion

    #region Drawing a typed name

    private static bool DrawTypedName(
        PdfDictionary resources, StringBuilder content, SignatureMark mark, double width, double height)
    {
        string text = mark.TypedName ?? string.Empty;
        if (text.Length == 0)
            return false;

        AddStandardFont(resources, "/SigFont", "Helvetica-Oblique");

        double markHeight = height * (1 - CaptionHeightFraction);

        // Sized to fill the box, then capped. Helvetica averages a little over half its point size
        // per character, which is close enough to fit a name without measuring properly.
        double byWidth = (width - Padding * 4) / (text.Length * 0.55);
        double size = Math.Clamp(Math.Min(byWidth, markHeight * 0.6), 6, 28);

        double baseline = height - Padding - markHeight * 0.62;

        content.Append("0 g\nBT\n/SigFont ");
        Number(content, size);
        content.Append(" Tf\n1 0 0 1 ");
        Number(content, Padding * 2);
        content.Append(' ');
        Number(content, baseline);
        content.Append(" Tm\n");
        content.Append(EncodeString(text)).Append(" Tj\nET\n");

        return true;
    }

    #endregion

    #region Drawing an image
    // The recommended route, because it is the user's real handwriting and needs no pointer to
    // produce. Also the most involved to write, because the image has to be embedded as a PDF
    // image XObject rather than merely referenced.

    private static bool DrawImage(
        PdfDocument document,
        PdfDictionary resources,
        StringBuilder content,
        SignatureMark mark,
        double width,
        double height,
        ref string? warning)
    {
        if (mark.ImagePath is not { Length: > 0 } path || !File.Exists(path))
        {
            warning = "The signature image could not be found.";
            return false;
        }

        using var bitmap = new Bitmap(path);

        var imageObject = BuildImageXObject(document, bitmap, ref warning);
        if (imageObject is null)
            return false;

        var xobjects = new PdfDictionary(document);
        xobjects.Elements.SetReference("/SigImage", imageObject);
        resources.Elements.SetValue("/XObject", xobjects);

        double markHeight = height * (1 - CaptionHeightFraction);
        double availableWidth = width - Padding * 2;
        double availableHeight = markHeight - Padding * 2;

        // Fitted preserving aspect ratio, then centred. A stretched signature looks forged, and the
        // person applying it cannot see that it happened.
        double scale = Math.Min(availableWidth / bitmap.Width, availableHeight / bitmap.Height);

        double drawWidth = bitmap.Width * scale;
        double drawHeight = bitmap.Height * scale;

        double left = (width - drawWidth) / 2;
        double bottom = height - Padding - markHeight + (availableHeight - drawHeight) / 2;

        // An image XObject is drawn by mapping the unit square through the current matrix, so the
        // matrix carries the size and position.
        content.Append("q\n");
        Number(content, drawWidth);
        content.Append(" 0 0 ");
        Number(content, drawHeight);
        content.Append(' ');
        Number(content, left);
        content.Append(' ');
        Number(content, bottom);
        content.Append(" cm\n/SigImage Do\nQ\n");

        return true;
    }

    /// <summary>
    /// Embeds a bitmap as a PDF image XObject.
    ///
    /// Written as raw RGB with Flate compression rather than as JPEG, because a signature is line
    /// art on a plain background and JPEG's ringing artefacts around hard edges make handwriting
    /// look smudged. An alpha channel becomes a soft mask, so a signature scanned onto a
    /// transparent background sits properly over whatever is underneath it.
    /// </summary>
    private static PdfDictionary? BuildImageXObject(PdfDocument document, Bitmap bitmap, ref string? warning)
    {
        const int maximumPixels = 4_000_000;

        if ((long)bitmap.Width * bitmap.Height > maximumPixels)
        {
            warning = "The signature image is very large. A smaller one would keep the file size down.";
        }

        int width = bitmap.Width;
        int height = bitmap.Height;

        var rgb = new byte[width * height * 3];
        byte[]? alpha = null;

        bool hasAlpha = Image.IsAlphaPixelFormat(bitmap.PixelFormat);
        if (hasAlpha)
            alpha = new byte[width * height];

        int rgbIndex = 0;
        int alphaIndex = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);

                rgb[rgbIndex++] = pixel.R;
                rgb[rgbIndex++] = pixel.G;
                rgb[rgbIndex++] = pixel.B;

                if (alpha is not null)
                    alpha[alphaIndex++] = pixel.A;
            }
        }

        var image = new PdfDictionary(document);
        image.Elements.SetName("/Type", "XObject");
        image.Elements.SetName("/Subtype", "Image");
        image.Elements.SetInteger("/Width", width);
        image.Elements.SetInteger("/Height", height);
        image.Elements.SetName("/ColorSpace", "DeviceRGB");
        image.Elements.SetInteger("/BitsPerComponent", 8);
        image.Elements.SetName("/Filter", "FlateDecode");
        image.CreateStream(Deflate(rgb));

        document.Internals.AddObject(image);

        if (alpha is not null)
        {
            var mask = new PdfDictionary(document);
            mask.Elements.SetName("/Type", "XObject");
            mask.Elements.SetName("/Subtype", "Image");
            mask.Elements.SetInteger("/Width", width);
            mask.Elements.SetInteger("/Height", height);
            mask.Elements.SetName("/ColorSpace", "DeviceGray");
            mask.Elements.SetInteger("/BitsPerComponent", 8);
            mask.Elements.SetName("/Filter", "FlateDecode");
            mask.CreateStream(Deflate(alpha));

            document.Internals.AddObject(mask);
            image.Elements.SetReference("/SMask", mask);
        }
        else
        {
            warning ??= "This image has no transparent background, so it will appear as a " +
                        "rectangle over the page. A PNG with a transparent background looks better.";
        }

        return image;
    }

    /// <summary>
    /// Compresses bytes in the zlib format that /FlateDecode expects. ZLibStream rather than
    /// DeflateStream: the latter omits the zlib header and every PDF reader would reject it.
    /// </summary>
    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();

        using (var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            compressor.Write(data, 0, data.Length);

        return output.ToArray();
    }

    #endregion

    #region The printed caption
    // A handwritten signature is very often illegible, and a drawn one usually is. The printed name
    // beneath is what tells a recipient who signed, and it is the part a screen reader can read
    // back if the document is ever re-opened.

    private static void DrawCaption(
        PdfDictionary resources, StringBuilder content, SignatureMark mark, double width, double height)
    {
        var lines = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(mark.SignerName))
            lines.Add(mark.SignerName);

        if (mark.ShowDate)
        {
            string when = mark.SignedAt.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
            lines.Add(mark.Reason is { Length: > 0 } reason ? $"{when} — {reason}" : when);
        }

        if (lines.Count == 0)
            return;

        AddStandardFont(resources, "/CapFont", "Helvetica");

        double captionHeight = height * CaptionHeightFraction;
        double size = Math.Clamp(Math.Min(CaptionFontSize, captionHeight / (lines.Count + 0.6)), 4, 9);
        double y = captionHeight - size;

        content.Append("0.25 g\nBT\n/CapFont ");
        Number(content, size);
        content.Append(" Tf\n");

        foreach (string line in lines)
        {
            content.Append("1 0 0 1 ");
            Number(content, Padding * 2);
            content.Append(' ');
            Number(content, Math.Max(1, y));
            content.Append(" Tm\n");
            content.Append(EncodeString(line)).Append(" Tj\n");

            y -= size * 1.2;
        }

        content.Append("ET\n");
    }

    #endregion

    #region Building the appearance object

    private static void AttachAppearance(
        PdfDocument document,
        PdfDictionary widget,
        PdfDictionary resources,
        string content,
        double width,
        double height)
    {
        resources.Elements.SetValue("/ProcSet",
            new PdfArray(document, new PdfName("/PDF"), new PdfName("/Text"), new PdfName("/ImageC")));

        var form = new PdfDictionary(document);
        form.Elements.SetName("/Type", "XObject");
        form.Elements.SetName("/Subtype", "Form");
        form.Elements.SetInteger("/FormType", 1);

        form.Elements.SetValue("/BBox", new PdfArray(document,
            new PdfInteger(0), new PdfInteger(0), new PdfReal(width), new PdfReal(height)));

        form.Elements.SetValue("/Resources", resources);

        // Latin-1: the standard fonts used here have no way to draw anything outside it, and
        // EncodeString has already substituted for what cannot be shown.
        form.CreateStream(Encoding.Latin1.GetBytes(content));
        document.Internals.AddObject(form);

        var appearances = widget.Elements.GetDictionary("/AP");

        if (appearances is null)
        {
            appearances = new PdfDictionary(document);
            widget.Elements.SetValue("/AP", appearances);
        }

        appearances.Elements.SetReference("/N", form);
    }

    /// <summary>
    /// Writes a description of the signature onto the widget annotation, so that the NEXT person to
    /// open this document with a screen reader is told who signed it.
    ///
    /// Without this, a placed signature is a picture and nothing more: it announces as an empty
    /// signature field, and the mark itself is invisible to assistive technology. /Contents is the
    /// annotation's alternate description and is exactly what a reader falls back to — so the one
    /// thing this editor exists to do, it should do for its own output too.
    /// </summary>
    private static void DescribeForScreenReaders(PdfDictionary widget, SignatureMark mark)
    {
        try
        {
            var parts = new List<string>(3) { "Signature" };

            if (!string.IsNullOrWhiteSpace(mark.SignerName))
                parts.Add($"of {mark.SignerName}");

            if (mark.ShowDate)
                parts.Add($"dated {mark.SignedAt.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}");

            if (mark.Reason is { Length: > 0 } reason)
                parts.Add($"reason: {reason}");

            widget.Elements.SetString("/Contents", string.Join(", ", parts));
        }
        catch
        {
            // The mark is drawn either way; losing its description is a fault worth avoiding but
            // not worth failing the signature over.
        }
    }

    private static void AddStandardFont(PdfDictionary resources, string name, string baseFont)
    {
        var fonts = resources.Elements.GetDictionary("/Font");

        if (fonts is null)
        {
            fonts = new PdfDictionary();
            resources.Elements.SetValue("/Font", fonts);
        }

        var font = new PdfDictionary();
        font.Elements.SetName("/Type", "Font");
        font.Elements.SetName("/Subtype", "Type1");
        font.Elements.SetName("/BaseFont", baseFont);
        font.Elements.SetName("/Encoding", "WinAnsiEncoding");

        fonts.Elements.SetValue(name, font);
    }

    #endregion

    #region Content-stream encoding
    // The same two traps as everywhere else content streams are written by hand: a locale that
    // formats decimals with a comma, and unescaped brackets in a string literal.

    private static void Number(StringBuilder builder, double value) =>
        builder.Append(value.ToString("0.###", CultureInfo.InvariantCulture));

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
                case '\n':
                    builder.Append(' ');
                    break;

                default:
                    builder.Append(c > 0xFF ? '?' : c);
                    break;
            }
        }

        builder.Append(')');
        return builder.ToString();
    }

    #endregion
}

#endregion
