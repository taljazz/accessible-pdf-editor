using System.Globalization;
using System.Text;
using AccessiblePdfEditor.Model;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace AccessiblePdfEditor.Persistence;

// =====================================================================================
//  SignatureFlattener.cs
//
//  Draws a signature into the page itself, tags it, and removes the now-redundant
//  signature field.
//
//  This is the default route, and it is different from putting the mark in the field's
//  appearance stream in three ways that matter:
//
//  1. NO VIEWER CHROME. A signature field that is not cryptographically signed is still a
//     signature field, and Adobe Acrobat paints its own "click to sign" panel over it. The
//     user cannot see that their mark has been covered up. Flattening the ink into the page
//     and deleting the field removes the field, so there is nothing left to paint over.
//
//  2. WE CONTROL THE ACCESSIBLE NAME. The flattened ink is wrapped in a tagged /Figure
//     carrying an /Alt, so a screen reader reads OUR wording — "Signature of Thomas
//     Anderson, added 3 August 2026" — rather than announcing an empty signature field, or
//     nothing at all. This is the whole accessibility win, and it is the reason this route
//     is worth the extra work.
//
//  3. IT IS CONFIRMED TO LAND IN THE RIGHT PLACE. Independently verified with a second
//     library reading the output back and reporting the ink's actual position on the page.
//
//  The cost is that the field is consumed: the document stops being a fillable form at that
//  spot. That is the correct trade for a signature, which is the last thing anyone does to
//  a form.
// =====================================================================================

#region FlattenResult

/// <summary>The outcome of flattening a signature into a page.</summary>
public readonly record struct FlattenResult(bool Succeeded, string? Warning)
{
    public static FlattenResult Ok(string? warning = null) => new(true, warning);

    public static FlattenResult Failed(string reason) => new(false, reason);
}

#endregion

#region SignatureFlattener

/// <summary>Draws a signature into page content and tags it for screen readers.</summary>
internal static class SignatureFlattener
{
    #region Entry point

    /// <summary>
    /// Flattens a signature into the page, tags it, and unlinks the signature field.
    /// </summary>
    public static FlattenResult Flatten(
        PdfDocument document,
        PdfDictionary fieldDictionary,
        PdfDictionary widget,
        SignatureMark mark)
    {
        try
        {
            var rectangle = widget.Elements.GetRectangle("/Rect");

            if (rectangle.IsZero)
                return FlattenResult.Failed("The signature field has no position on the page.");

            double width = Math.Abs(rectangle.X2 - rectangle.X1);
            double height = Math.Abs(rectangle.Y2 - rectangle.Y1);

            if (width <= 1 || height <= 1)
                return FlattenResult.Failed("The signature field is too small to draw into.");

            int pageIndex = FindPageIndex(document, widget);

            if (pageIndex < 0)
                return FlattenResult.Failed("The signature field is not attached to any page.");

            var page = document.Pages[pageIndex];
            string? warning = null;

            if (page.Rotate != 0)
            {
                // Drawn anyway, because refusing would leave the user unable to sign at all. Said
                // out loud, because they cannot look at the result to check it.
                warning = $"Page {pageIndex + 1} is rotated. The signature has been placed, but " +
                          "check with someone who can see the page that it landed the right way up.";
            }

            string altText = BuildAltText(mark);

            // Wrapped in a marked-content sequence so the tag can point at it. The MCID must be
            // above anything already on the page: reusing one would make the structure tree
            // ambiguous about which ink each tag refers to.
            int mcid = NextMarkedContentId(page);

            OpenMarkedContent(document, page, mcid, altText);
            DrawInto(page, rectangle, mark);
            CloseMarkedContent(document, page);

            RemoveSignatureField(document, fieldDictionary, widget, page);
            TagAsFigure(document, page, mcid, altText);

            return FlattenResult.Ok(warning);
        }
        catch (Exception ex)
        {
            return FlattenResult.Failed($"The signature could not be placed: {ex.Message}");
        }
    }

    #endregion

    #region Drawing
    // XGraphics runs y-down from the top-left of the page, which is the same convention the
    // signature pad uses, so normalised stroke coordinates map straight across with no flip. The
    // only conversion needed is turning the field's PDF rectangle, measured from the bottom of the
    // page, into a top-left offset.

    private static void DrawInto(PdfPage page, PdfRectangle rectangle, SignatureMark mark)
    {
        double width = Math.Abs(rectangle.X2 - rectangle.X1);
        double height = Math.Abs(rectangle.Y2 - rectangle.Y1);

        double left = Math.Min(rectangle.X1, rectangle.X2);
        double top = page.Height.Point - Math.Max(rectangle.Y1, rectangle.Y2);

        using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

        graphics.TranslateTransform(left, top);

        if (page.Rotate != 0)
        {
            // Rotated about the middle of the field, so the mark stays inside its box whichever way
            // the page is turned.
            graphics.TranslateTransform(width / 2, height / 2);
            graphics.RotateTransform(-page.Rotate);
            graphics.TranslateTransform(-width / 2, -height / 2);
        }

        DrawSignature(graphics, width, height, mark);
    }

    /// <summary>The one drawing routine, shared by all three ways of capturing a signature.</summary>
    private static void DrawSignature(XGraphics graphics, double width, double height, SignatureMark mark)
    {
        // The printed name sits underneath, as on a paper signature block.
        double captionHeight = height * 0.28;
        double markHeight = height - captionHeight;

        switch (mark.Source)
        {
            case SignatureSource.Image:
                DrawImage(graphics, width, markHeight, mark);
                break;

            case SignatureSource.TypedName:
                DrawTypedName(graphics, width, markHeight, mark);
                break;

            case SignatureSource.Drawn:
                DrawStrokes(graphics, width, markHeight, mark);
                break;
        }

        DrawCaption(graphics, width, height, captionHeight, mark);
    }

    private static void DrawImage(XGraphics graphics, double width, double height, SignatureMark mark)
    {
        if (mark.ImagePath is not { Length: > 0 } path || !File.Exists(path))
            return;

        using var image = XImage.FromFile(path);

        var (x, y, drawWidth, drawHeight) = Fit(image.PixelWidth, image.PixelHeight, width, height, 2);

        // Explicit width and height every time. The overload that takes only a position draws at
        // the image's own resolution, which for a phone photo of a signature is enormous.
        graphics.DrawImage(image, x, y, drawWidth, drawHeight);
    }

    private static void DrawTypedName(XGraphics graphics, double width, double height, SignatureMark mark)
    {
        string text = mark.TypedName ?? string.Empty;

        if (text.Length == 0 || !PdfSharpEnvironment.CanDrawText)
            return;

        var font = new XFont("Times New Roman", Math.Max(8, height * 0.45), XFontStyleEx.Italic);

        graphics.DrawString(text, font, XBrushes.Black,
            new XRect(0, 0, width, height), XStringFormats.Center);
    }

    private static void DrawStrokes(XGraphics graphics, double width, double height, SignatureMark mark)
    {
        if (mark.Strokes.Count == 0)
            return;

        double penWidth = Math.Max(0.7, Math.Min(width, height) * 0.025);

        var pen = new XPen(XColors.DarkBlue, penWidth)
        {
            LineCap = XLineCap.Round,
            LineJoin = XLineJoin.Round,
        };

        foreach (var stroke in mark.Strokes)
        {
            var points = stroke.Points;

            if (points.Count == 0)
                continue;

            // DrawLines throws on fewer than two points, and a single point is a legitimate part of
            // a signature — the dot on an i.
            if (points.Count == 1)
            {
                graphics.DrawEllipse(new XSolidBrush(XColors.DarkBlue),
                    points[0].X * width - penWidth / 2,
                    points[0].Y * height - penWidth / 2,
                    penWidth, penWidth);

                continue;
            }

            var mapped = new XPoint[points.Count];

            for (int i = 0; i < points.Count; i++)
                mapped[i] = new XPoint(points[i].X * width, points[i].Y * height);

            graphics.DrawLines(pen, mapped);
        }
    }

    private static void DrawCaption(
        XGraphics graphics, double width, double height, double captionHeight, SignatureMark mark)
    {
        if (!PdfSharpEnvironment.CanDrawText)
            return;

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

        double size = Math.Clamp(captionHeight / (lines.Count + 0.8), 4, 9);
        var font = new XFont("Arial", size);
        var brush = new XSolidBrush(XColor.FromArgb(64, 64, 64));

        double y = height - captionHeight + size;

        foreach (string line in lines)
        {
            graphics.DrawString(line, font, brush, new XPoint(2, y));
            y += size * 1.2;
        }
    }

    /// <summary>Fits a source size into a box, preserving aspect ratio and centring it.</summary>
    private static (double X, double Y, double Width, double Height) Fit(
        double sourceWidth, double sourceHeight, double boxWidth, double boxHeight, double padding)
    {
        double availableWidth = boxWidth - padding * 2;
        double availableHeight = boxHeight - padding * 2;

        if (sourceWidth <= 0 || sourceHeight <= 0 || availableWidth <= 0 || availableHeight <= 0)
            return (padding, padding, Math.Max(0, availableWidth), Math.Max(0, availableHeight));

        double scale = Math.Min(availableWidth / sourceWidth, availableHeight / sourceHeight);

        double width = sourceWidth * scale;
        double height = sourceHeight * scale;

        return (padding + (availableWidth - width) / 2, padding + (availableHeight - height) / 2,
            width, height);
    }

    #endregion

    #region Marked content
    // The ink has to sit inside a BDC/EMC pair carrying an MCID before a structure element can
    // point at it. The /Alt is written into the property list as well as onto the structure
    // element: some readers take it from one, some from the other, and it costs nothing to satisfy
    // both.

    private static void OpenMarkedContent(PdfDocument document, PdfPage page, int mcid, string altText)
    {
        var builder = new StringBuilder(96);

        builder.Append("/Figure <</MCID ")
            .Append(mcid.ToString(CultureInfo.InvariantCulture))
            .Append(" /Alt ")
            .Append(EncodeString(altText))
            .Append(">> BDC\n");

        AppendRawContent(document, page, builder.ToString());
    }

    private static void CloseMarkedContent(PdfDocument document, PdfPage page) =>
        AppendRawContent(document, page, "EMC\n");

    private static void AppendRawContent(PdfDocument document, PdfPage page, string operators)
    {
        var content = page.Contents.AppendContent();
        content.CreateStream(Encoding.Latin1.GetBytes(operators));
    }

    /// <summary>
    /// The next free marked-content identifier on a page.
    ///
    /// Scanned from the page's existing content rather than assumed to be zero. Testing found that
    /// blindly using zero on an already-marked document produced duplicate identifiers on every
    /// page and orphaned more than a thousand existing marked-content sequences — silently
    /// wrecking the structure of exactly the well-made documents this editor should treat best.
    /// </summary>
    private static int NextMarkedContentId(PdfPage page)
    {
        int highest = -1;

        try
        {
            foreach (var content in page.Contents)
            {
                byte[]? bytes = content.Stream?.Value;

                if (bytes is null || bytes.Length == 0)
                    continue;

                highest = Math.Max(highest, HighestMarkedContentId(Encoding.Latin1.GetString(bytes)));
            }
        }
        catch
        {
            // A page whose content cannot be scanned gets a high identifier instead, which is safe:
            // colliding with nothing is better than colliding with something.
            return 10_000;
        }

        return highest + 1;
    }

    private static int HighestMarkedContentId(string content)
    {
        const string marker = "/MCID";
        int highest = -1;
        int index = 0;

        while ((index = content.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            int cursor = index + marker.Length;

            while (cursor < content.Length && char.IsWhiteSpace(content[cursor]))
                cursor++;

            int start = cursor;

            while (cursor < content.Length && char.IsDigit(content[cursor]))
                cursor++;

            if (cursor > start && int.TryParse(content.AsSpan(start, cursor - start), out int value))
                highest = Math.Max(highest, value);

            index = cursor;
        }

        return highest;
    }

    #endregion

    #region Removing the signature field
    // The field must go, or a viewer will still paint its own signing chrome over the ink we just
    // drew. Removal has three parts and missing any one of them leaves a ghost: testing found that
    // unlinking only from the top-level field array is a silent no-op on the nested field layout
    // that Acrobat and LiveCycle both produce.

    private static void RemoveSignatureField(
        PdfDocument document, PdfDictionary field, PdfDictionary widget, PdfPage page)
    {
        RemoveFromFieldTree(document, field);
        RemoveFromAnnotations(page, widget);
        RemoveStructureElementFor(document, widget);
    }

    private static void RemoveFromFieldTree(PdfDocument document, PdfDictionary field)
    {
        var acroForm = document.Internals.Catalog.Elements.GetDictionary("/AcroForm");
        var fields = acroForm?.Elements.GetArray("/Fields");

        if (acroForm is null || fields is null)
            return;

        RemoveFromArray(fields, field, depth: 0);

        // With no fields left there is no form. Leaving an empty one behind makes readers announce
        // a form with nothing in it.
        if (fields.Elements.Count == 0)
        {
            acroForm.Elements.Remove("/Fields");
            document.Internals.Catalog.Elements.Remove("/AcroForm");
        }
    }

    /// <summary>
    /// Removes a field from an array, recursing into intermediate nodes and pruning any that are
    /// left childless.
    /// </summary>
    private static bool RemoveFromArray(PdfArray fields, PdfDictionary target, int depth)
    {
        const int maximumDepth = 32;

        if (depth > maximumDepth)
            return false;

        for (int i = fields.Elements.Count - 1; i >= 0; i--)
        {
            var entry = Resolve(fields.Elements[i]);

            if (entry is null)
                continue;

            if (ReferenceEquals(entry, target))
            {
                fields.Elements.RemoveAt(i);
                return true;
            }

            var kids = entry.Elements.GetArray("/Kids");

            if (kids is null || !RemoveFromArray(kids, target, depth + 1))
                continue;

            // An intermediate node with no children left is a dead branch of the field tree.
            if (kids.Elements.Count == 0)
                fields.Elements.RemoveAt(i);

            return true;
        }

        return false;
    }

    private static void RemoveFromAnnotations(PdfPage page, PdfDictionary widget)
    {
        var annotations = page.Elements.GetArray("/Annots");

        if (annotations is null)
            return;

        for (int i = annotations.Elements.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(Resolve(annotations.Elements[i]), widget))
                annotations.Elements.RemoveAt(i);
        }

        if (annotations.Elements.Count == 0)
            page.Elements.Remove("/Annots");
    }

    /// <summary>
    /// Removes the structure element that referred to the deleted widget.
    ///
    /// A tagged document wraps every annotation in a structure element containing an object
    /// reference. Deleting the widget without this leaves that reference dangling, and a dangling
    /// reference keeps the deleted objects alive in the saved file — the ghost field reappears.
    /// </summary>
    private static void RemoveStructureElementFor(PdfDocument document, PdfDictionary widget)
    {
        var root = document.Internals.Catalog.Elements.GetDictionary("/StructTreeRoot");

        if (root is null)
            return;

        RemoveObjectReference(root, widget, depth: 0);
    }

    private static void RemoveObjectReference(PdfDictionary node, PdfDictionary widget, int depth)
    {
        const int maximumDepth = 48;

        if (depth > maximumDepth)
            return;

        var kids = node.Elements.GetArray("/K");

        if (kids is null)
            return;

        for (int i = kids.Elements.Count - 1; i >= 0; i--)
        {
            var child = Resolve(kids.Elements[i]);

            if (child is null)
                continue;

            // An object reference pointing at the widget we removed.
            bool isReferenceToWidget =
                child.Elements.GetName("/Type") == "/OBJR"
                && ReferenceEquals(Resolve(child.Elements["/Obj"]), widget);

            if (isReferenceToWidget)
            {
                kids.Elements.RemoveAt(i);
                continue;
            }

            RemoveObjectReference(child, widget, depth + 1);

            // A structure element left with no children describes nothing.
            var grandchildren = child.Elements.GetArray("/K");

            if (child.Elements.GetName("/S") == "/Form"
                && grandchildren is { Elements.Count: 0 })
            {
                kids.Elements.RemoveAt(i);
            }
        }
    }

    #endregion

    #region Tagging the flattened ink
    // The accessibility payoff. Without this the signature is ink: a screen reader has nothing to
    // say where it is, and the user who signed cannot verify it is announced at all.

    private static void TagAsFigure(PdfDocument document, PdfPage page, int mcid, string altText)
    {
        var catalog = document.Internals.Catalog;
        var root = catalog.Elements.GetDictionary("/StructTreeRoot");

        if (root is null)
        {
            root = new PdfDictionary(document);
            root.Elements.SetName("/Type", "StructTreeRoot");
            root.Elements.SetValue("/K", new PdfArray(document));
            document.Internals.AddObject(root);
            catalog.Elements.SetReference("/StructTreeRoot", root);
        }

        var figure = new PdfDictionary(document);
        figure.Elements.SetName("/Type", "StructElem");
        figure.Elements.SetName("/S", "Figure");
        figure.Elements.SetString("/Alt", altText);
        figure.Elements.SetInteger("/K", mcid);
        figure.Elements.SetReference("/P", root);
        figure.Elements.SetReference("/Pg", page);
        document.Internals.AddObject(figure);

        var kids = root.Elements.GetArray("/K");

        if (kids is null)
        {
            kids = new PdfArray(document);
            root.Elements.SetValue("/K", kids);
        }

        kids.Elements.Add(figure.Reference!);

        LinkIntoParentTree(document, root, page, mcid, figure);

        // The document now carries structure, so it must say so — otherwise readers will not look
        // for the tag that was just written.
        var markInfo = catalog.Elements.GetDictionary("/MarkInfo");

        if (markInfo is null)
        {
            markInfo = new PdfDictionary(document);
            catalog.Elements.SetValue("/MarkInfo", markInfo);
        }

        markInfo.Elements.SetBoolean("/Marked", true);
    }

    /// <summary>
    /// Wires the new figure into the parent tree, which is how a reader gets from a piece of marked
    /// content back to the structure element describing it.
    ///
    /// A page that already has a parent-tree entry has its existing array extended; one that does
    /// not gets a fresh key. Getting this wrong does not corrupt anything visibly — the ink still
    /// draws — but the tag becomes unreachable, which means the alt text is never announced and the
    /// whole point of flattening is lost.
    /// </summary>
    private static void LinkIntoParentTree(
        PdfDocument document, PdfDictionary root, PdfPage page, int mcid, PdfDictionary figure)
    {
        var parentTree = root.Elements.GetDictionary("/ParentTree");

        if (parentTree is null)
        {
            parentTree = new PdfDictionary(document);
            parentTree.Elements.SetValue("/Nums", new PdfArray(document));
            document.Internals.AddObject(parentTree);
            root.Elements.SetReference("/ParentTree", parentTree);
        }

        var numbers = parentTree.Elements.GetArray("/Nums");

        if (numbers is null)
        {
            numbers = new PdfArray(document);
            parentTree.Elements.SetValue("/Nums", numbers);
        }

        bool pageHasKey = page.Elements.ContainsKey("/StructParents");
        int key = pageHasKey
            ? page.Elements.GetInteger("/StructParents")
            : NextParentTreeKey(root, numbers);

        // The array for a page maps each marked-content identifier to the element that owns it, by
        // position, so the entry has to sit at index mcid.
        PdfArray? entries = pageHasKey ? FindParentTreeEntry(numbers, key) : null;

        if (entries is null)
        {
            entries = new PdfArray(document);
            document.Internals.AddObject(entries);

            numbers.Elements.Add(new PdfInteger(key));
            numbers.Elements.Add(entries.Reference!);
        }

        while (entries.Elements.Count < mcid)
            entries.Elements.Add(PdfNull.Value);

        if (entries.Elements.Count == mcid)
            entries.Elements.Add(figure.Reference!);
        else
            entries.Elements[mcid] = figure.Reference!;

        page.Elements.SetInteger("/StructParents", key);
        root.Elements.SetInteger("/ParentTreeNextKey", Math.Max(key + 1, NextParentTreeKey(root, numbers)));
    }

    private static PdfArray? FindParentTreeEntry(PdfArray numbers, int key)
    {
        for (int i = 0; i + 1 < numbers.Elements.Count; i += 2)
        {
            if (numbers.Elements[i] is PdfInteger number && number.Value == key)
                return ResolveArray(numbers.Elements[i + 1]);
        }

        return null;
    }

    private static int NextParentTreeKey(PdfDictionary root, PdfArray numbers)
    {
        int highest = -1;

        for (int i = 0; i + 1 < numbers.Elements.Count; i += 2)
        {
            if (numbers.Elements[i] is PdfInteger number)
                highest = Math.Max(highest, number.Value);
        }

        if (root.Elements.ContainsKey("/ParentTreeNextKey"))
            highest = Math.Max(highest, root.Elements.GetInteger("/ParentTreeNextKey") - 1);

        return highest + 1;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// What a screen reader announces where the signature is. The one sentence that turns flattened
    /// ink back into information.
    /// </summary>
    private static string BuildAltText(SignatureMark mark)
    {
        var parts = new List<string>(4) { "Signature" };

        if (!string.IsNullOrWhiteSpace(mark.SignerName))
            parts.Add($"of {mark.SignerName}");

        if (mark.ShowDate)
            parts.Add($"added {mark.SignedAt.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}");

        if (mark.Reason is { Length: > 0 } reason)
            parts.Add($"reason: {reason}");

        // Stated in the document itself, not only in this application's own announcements. Anyone
        // opening the file later deserves to know which kind of signature they are looking at.
        parts.Add("visual mark, not cryptographically verified");

        return string.Join(", ", parts);
    }

    private static int FindPageIndex(PdfDocument document, PdfDictionary widget)
    {
        // The widget's own page reference is the reliable answer.
        if (Resolve(widget.Elements["/P"]) is { } referenced)
        {
            for (int i = 0; i < document.PageCount; i++)
            {
                if (ReferenceEquals(document.Pages[i], referenced))
                    return i;
            }
        }

        // Failing that, the page whose annotations contain it.
        for (int i = 0; i < document.PageCount; i++)
        {
            var annotations = document.Pages[i].Elements.GetArray("/Annots");

            if (annotations is null)
                continue;

            for (int j = 0; j < annotations.Elements.Count; j++)
            {
                if (ReferenceEquals(Resolve(annotations.Elements[j]), widget))
                    return i;
            }
        }

        return -1;
    }

    private static PdfDictionary? Resolve(PdfItem? item) => item switch
    {
        PdfReference reference => reference.Value as PdfDictionary,
        PdfDictionary dictionary => dictionary,
        _ => null,
    };

    /// <summary>
    /// Resolves an item to an array, following an indirect reference. Parent-tree entries are
    /// normally indirect, so reading them directly finds a reference rather than the array.
    /// </summary>
    private static PdfArray? ResolveArray(PdfItem? item) => item switch
    {
        PdfReference reference => reference.Value as PdfArray,
        PdfArray array => array,
        _ => null,
    };

    /// <summary>
    /// Encodes a PDF string literal. Unescaped brackets would terminate the string early and
    /// corrupt every operator after it in the content stream.
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
