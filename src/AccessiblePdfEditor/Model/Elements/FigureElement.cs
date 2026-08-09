using AccessiblePdfEditor.Model;

namespace AccessiblePdfEditor.Model.Elements;

// =====================================================================================
//  FigureElement.cs
//
//  Images, charts, diagrams and anything else on a page that is not text.
//
//  This is the element where accessibility either happens or does not. A figure with alt
//  text is a sentence; a figure without it is a hole in the document that a blind reader
//  cannot even measure — they cannot tell whether they missed a decorative flourish or the
//  chart the entire report is about.
//
//  So this class treats missing alt text as a first-class state rather than an absence:
//  it is announced, it is auditable, and writing one is a supported edit. It also
//  distinguishes "no alt text" from "explicitly marked decorative", because those are
//  opposite situations that look identical if you only check whether a string is empty.
// =====================================================================================

#region FigureElement

/// <summary>A non-text graphic: an image, chart, diagram or drawing.</summary>
public sealed class FigureElement : DocumentElement
{
    #region Construction and state

    public FigureElement(int pageNumber, PageRegion bounds)
        : base(pageNumber)
    {
        Bounds = bounds;
    }

    public override ElementKind Kind => ElementKind.Figure;

    /// <summary>
    /// The figure's alternate text, from the PDF /Alt attribute. This is the description a screen
    /// reader speaks in place of the image, and setting it is the single most valuable repair this
    /// editor performs.
    /// </summary>
    public string? AlternateText { get; internal set; }

    /// <summary>
    /// True when the document explicitly marks this figure as decorative — an /Artifact, or a
    /// figure whose /Alt is deliberately empty.
    ///
    /// This must not be confused with simply having no alt text. "Decorative" is a positive
    /// statement by the author that there is nothing here worth describing, and it is correct to
    /// skip. "No alt text" is the author having said nothing at all, and it is a fault. Collapsing
    /// the two would silently reclassify every unlabelled image in every untagged PDF as
    /// intentionally decorative, which is exactly the wrong answer.
    /// </summary>
    public bool IsMarkedDecorative { get; internal set; }

    /// <summary>The caption bound to this figure, when the document has one.</summary>
    public CaptionElement? Caption => Children.OfType<CaptionElement>().FirstOrDefault();

    /// <summary>Pixel width of the underlying image, when known. Used to spot spacer images.</summary>
    public int? PixelWidth { get; init; }

    /// <summary>Pixel height of the underlying image, when known.</summary>
    public int? PixelHeight { get; init; }

    /// <summary>
    /// True when this figure needs a description and does not have one. The condition the auditor
    /// reports and the remediation workflow walks the user through.
    /// </summary>
    public bool NeedsAlternateText =>
        !IsMarkedDecorative && string.IsNullOrWhiteSpace(AlternateText);

    /// <summary>
    /// True when the figure is too small to carry meaning — a spacer, a rule, a bullet glyph drawn
    /// as an image. Flagging these keeps the remediation list honest: asking someone to write alt
    /// text for forty one-pixel spacers is how a good workflow gets abandoned.
    /// </summary>
    public bool IsLikelyDecorativeBySize =>
        (Bounds.Width > 0 && Bounds.Width < 8) ||
        (Bounds.Height > 0 && Bounds.Height < 8) ||
        (PixelWidth is > 0 and < 8) ||
        (PixelHeight is > 0 and < 8);

    #endregion

    #region Editing support

    /// <summary>
    /// Sets or clears the alternate text. Passing null or whitespace clears it; marking a figure
    /// decorative is done through <see cref="MarkDecorative"/> instead, so that the two states can
    /// never be confused by a caller.
    /// </summary>
    public void SetAlternateText(string? text)
    {
        AlternateText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (AlternateText is not null)
            IsMarkedDecorative = false;
    }

    /// <summary>
    /// Marks the figure as carrying no information worth describing. Clears any alternate text,
    /// because a decorative figure with a description is a contradiction that would leave readers
    /// disagreeing about whether to announce it.
    /// </summary>
    public void MarkDecorative()
    {
        IsMarkedDecorative = true;
        AlternateText = null;
    }

    #endregion

    #region Announcement
    // A described figure reads as "figure, a bar chart of quarterly revenue". An undescribed one
    // says so plainly. It does not stay silent and it does not read the filename: a listener needs
    // to know that something is here and that its meaning was never recorded, so they can decide
    // whether to go looking for it elsewhere.

    protected override string DescribeRole(VerbosityLevel verbosity) =>
        IsMarkedDecorative ? "decorative image" : "figure";

    protected override string DescribeContent(VerbosityLevel verbosity)
    {
        if (IsMarkedDecorative)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(AlternateText))
            return AlternateText!;

        if (Caption is { Text.Length: > 0 } caption)
            return $"no description. Caption reads: {caption.Text}";

        return "no description available";
    }

    protected override string DescribeState(VerbosityLevel verbosity)
    {
        if (verbosity == VerbosityLevel.Terse)
            return string.Empty;

        if (NeedsAlternateText && !IsLikelyDecorativeBySize)
            return "needs a description";

        return string.Empty;
    }

    protected override string DescribePosition(VerbosityLevel verbosity)
    {
        string page = base.DescribePosition(verbosity);
        if (Bounds.IsEmpty)
            return page;

        // Rounded to whole points: the exact size is noise, but "how big is this thing" is a real
        // question when deciding whether an undescribed image matters.
        string size = $"{Math.Round(Bounds.Width)} by {Math.Round(Bounds.Height)} points";
        return page.Length > 0 ? $"{page}, {size}" : size;
    }

    #endregion
}

#endregion
