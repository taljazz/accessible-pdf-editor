using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace AccessiblePdfEditor.Persistence;

// =====================================================================================
//  PdfSharpEnvironment.cs
//
//  One-time process-wide setup for PDFsharp, and the glyph pre-flight check that protects
//  a blind author from silently losing text.
//
//  Two facts drive this file, both established by testing rather than assumed:
//
//  1. PDFsharp's font handling MUST be configured before anything touches a font, and the
//     default encoding can be set only ONCE per process — a second assignment throws, and
//     there is no way to reset it. So it happens here, at startup, exactly once.
//
//  2. THE DANGEROUS ONE. When text contains a character the chosen font has no glyph for,
//     PDFsharp does not throw, does not warn, and does not render a placeholder. It writes
//     nothing. The text is simply gone. Testing confirmed 'ä½ å¥½ä¸–ç•Œ' written to a page and
//     extracted back as four spaces, with no error at any point, and MeasureString reports
//     a perfectly normal width for characters that will not appear.
//
//     For a sighted author that is a visible mistake. For a blind author there is no cue
//     at all: they type a name containing a character their font cannot render, save, and
//     hand over a document with a hole in it that they will never know about.
//
//     So every string this application draws goes through CheckGlyphCoverage first, and
//     anything missing is reported out loud BEFORE it is written. This is the single most
//     important safety check in the whole write path.
// =====================================================================================

#region GlyphCoverage — the result of checking whether text can actually be drawn

/// <summary>The outcome of checking that a font can render a piece of text.</summary>
/// <param name="IsComplete">Whether every character can be drawn.</param>
/// <param name="MissingCodePoints">
/// The Unicode code points with no glyph. Code points rather than chars, because an emoji or a
/// rare CJK character occupies two chars and reporting half of one would be meaningless.
/// </param>
public readonly record struct GlyphCoverage(bool IsComplete, IReadOnlyList<int> MissingCodePoints)
{
    /// <summary>Every character can be drawn.</summary>
    public static GlyphCoverage Complete => new(true, []);

    /// <summary>
    /// The missing characters, described so they can be understood by ear.
    ///
    /// Each is given as the character itself AND as its code point, because a character the font
    /// cannot draw is very often one the speech engine cannot pronounce either — hearing "U+4F60"
    /// is at least actionable, where hearing silence is not.
    /// </summary>
    public string DescribeMissing()
    {
        if (IsComplete || MissingCodePoints.Count == 0)
            return string.Empty;

        var distinct = MissingCodePoints.Distinct().ToList();

        var described = distinct
            .Take(8)
            .Select(cp => $"{char.ConvertFromUtf32(cp)} (U+{cp:X4})");

        string list = string.Join(", ", described);

        return distinct.Count > 8
            ? $"{list}, and {distinct.Count - 8} more"
            : list;
    }
}

#endregion

#region PdfSharpEnvironment

/// <summary>
/// Process-wide PDFsharp configuration and the glyph safety check. Call
/// <see cref="Initialise"/> once at startup, before anything creates a font.
/// </summary>
public static class PdfSharpEnvironment
{
    #region Initialisation

    private static readonly Lock Gate = new();
    private static bool _initialised;

    /// <summary>Whether font support is available for drawing text.</summary>
    public static bool CanDrawText { get; private set; }

    /// <summary>
    /// Why text drawing is unavailable, when it is. Reported to the user rather than letting
    /// authoring fail later with an exception they cannot act on.
    /// </summary>
    public static string? FontFailureReason { get; private set; }

    /// <summary>The font family used for authored content.</summary>
    public static string DefaultFontFamily { get; private set; } = "Arial";

    /// <summary>
    /// Configures PDFsharp. Safe to call more than once; only the first call does anything.
    ///
    /// Two things happen here and the ORDER matters. The default encoding must be Unicode, because
    /// the alternative silently destroys any text outside Latin-1 — Greek written under WinAnsi
    /// comes back as question marks with no error. And it can only ever be set once, so it is set
    /// before any font exists.
    /// </summary>
    public static void Initialise()
    {
        lock (Gate)
        {
            if (_initialised)
                return;

            _initialised = true;

            try
            {
                // Unicode, always. The default encoding emits fonts with no /ToUnicode map for
                // ASCII-only runs, which some accessibility validators reject, and WinAnsi loses
                // non-Latin-1 text outright. This assignment throws if anything has already
                // created a font, which is why Initialise must run before the first document.
                GlobalFontSettings.DefaultFontEncoding = PdfFontEncoding.Unicode;
            }
            catch (InvalidOperationException)
            {
                // Already set, by an earlier call or by a library. Not fatal.
            }

            try
            {
                // Reads the installed Windows fonts directly, with no GDI dependency. This gives
                // real Arial and Times New Roman, correctly subsetted and embedded, and covers far
                // more of Unicode than the fonts bundled with PDFsharp.
                GlobalFontSettings.UseWindowsFontsUnderWindows = true;

                // Prove it works now rather than at the moment the user tries to author something.
                _ = new PdfSharp.Drawing.XFont(DefaultFontFamily, 12);
                CanDrawText = true;
            }
            catch (Exception ex)
            {
                CanDrawText = false;
                FontFailureReason =
                    $"No usable font could be found, so new text cannot be added to documents. {ex.Message}";
            }
        }
    }

    #endregion

    #region Glyph pre-flight — the check that stops text disappearing silently

    /// <summary>
    /// Checks that a font can actually render every character of a string.
    ///
    /// Must be called before drawing any text the user supplied. A character with no glyph is
    /// written as nothing at all — no exception, no placeholder, no warning — so this check is the
    /// only thing standing between a blind author and a document with words missing from it.
    /// </summary>
    public static GlyphCoverage CheckGlyphCoverage(string text, PdfSharp.Drawing.XFont font)
    {
        if (string.IsNullOrEmpty(text) || !CanDrawText)
            return GlyphCoverage.Complete;

        try
        {
            // Returns a code point paired with its glyph index. A glyph index of zero is .notdef:
            // the font has nothing to draw for that code point, and PDFsharp will emit nothing.
            var pairs = GlyphHelper.GlyphIndicesFromString(text, font);
            var missing = new List<int>();

            foreach (var pair in pairs)
            {
                if (pair.GlyphIndex != 0)
                    continue;

                // Whitespace and control characters legitimately map to no glyph.
                var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(
                    char.ConvertFromUtf32(pair.CodePoint), 0);

                if (category is System.Globalization.UnicodeCategory.SpaceSeparator
                    or System.Globalization.UnicodeCategory.Control
                    or System.Globalization.UnicodeCategory.Format
                    or System.Globalization.UnicodeCategory.LineSeparator
                    or System.Globalization.UnicodeCategory.ParagraphSeparator)
                {
                    continue;
                }

                missing.Add(pair.CodePoint);
            }

            return missing.Count == 0
                ? GlyphCoverage.Complete
                : new GlyphCoverage(false, missing);
        }
        catch
        {
            // If the check itself cannot run, report complete rather than blocking the user's work
            // on a check that is itself broken. The risk of a silent omission is real but small;
            // the certainty of blocking every edit would not be.
            return GlyphCoverage.Complete;
        }
    }

    /// <summary>
    /// Builds the warning spoken when text cannot be fully rendered, phrased so the user knows
    /// exactly what would be lost and can decide what to do about it.
    /// </summary>
    public static string BuildGlyphWarning(GlyphCoverage coverage, string fontFamily)
    {
        if (coverage.IsComplete)
            return string.Empty;

        return $"Warning: {fontFamily} cannot draw these characters, and they would be left out of " +
               $"the document without any visible sign: {coverage.DescribeMissing()}. " +
               "Choose a different font, or change the text.";
    }

    #endregion
}

#endregion
