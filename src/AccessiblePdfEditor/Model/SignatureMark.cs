namespace AccessiblePdfEditor.Model;

// =====================================================================================
//  SignatureMark.cs
//
//  What a user's signature actually IS, captured in one of the three ways a blind person
//  can realistically produce one.
//
//  The distinction this file exists to keep honest:
//
//    A VISIBLE MARK is a picture of a signature. It is what almost every e-signature
//    workflow in the world actually uses, it is often legally sufficient, and anyone with
//    a copy of the file could lift it out and reuse it.
//
//    A DIGITAL SIGNATURE is a cryptographic operation over the file's bytes using a
//    certificate. It proves who signed and that nothing has changed since.
//
//  They are completely different guarantees and the UI must never blur them. A blind user
//  cannot see a "signed" badge and inspect its properties, so the program has to say which
//  one it applied, in words, every time.
// =====================================================================================

#region SignatureSource

/// <summary>How the user produced their signature.</summary>
public enum SignatureSource
{
    /// <summary>
    /// An image file of their handwritten signature — a scan or a photograph.
    /// The recommended route: it is their real signature, and it needs no pointer at all.
    /// </summary>
    Image = 0,

    /// <summary>Their name, typed and drawn into the field.</summary>
    TypedName,

    /// <summary>Drawn in the moment, with a mouse, trackpad, stylus or the arrow keys.</summary>
    Drawn,
}

#endregion

#region SignatureStroke

/// <summary>
/// One continuous stroke of a drawn signature, in coordinates from 0 to 1 across the drawing area.
///
/// Normalised rather than in pixels so a signature drawn on any size of pad scales correctly into
/// any size of signature field, without the capture surface needing to know the field's dimensions.
/// </summary>
public sealed class SignatureStroke
{
    private readonly List<(double X, double Y)> _points = [];

    /// <summary>The points of this stroke, in order.</summary>
    public IReadOnlyList<(double X, double Y)> Points => _points;

    /// <summary>Adds a point, clamped to the drawing area.</summary>
    public void Add(double x, double y) =>
        _points.Add((Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1)));

    /// <summary>True when the stroke has enough points to draw as a line.</summary>
    public bool IsDrawable => _points.Count >= 2;
}

#endregion

#region SignatureMark

/// <summary>A captured signature, ready to be drawn into a signature field.</summary>
public sealed class SignatureMark
{
    #region Construction
    // Private constructor and named factories, so a mark can never be built in a half-specified
    // state — an Image mark with no image, or a Drawn mark with no strokes.

    private SignatureMark(SignatureSource source)
    {
        Source = source;
    }

    /// <summary>How this signature was produced.</summary>
    public SignatureSource Source { get; }

    /// <summary>The image file, for <see cref="SignatureSource.Image"/>.</summary>
    public string? ImagePath { get; private init; }

    /// <summary>The typed name, for <see cref="SignatureSource.TypedName"/>.</summary>
    public string? TypedName { get; private init; }

    /// <summary>The strokes, for <see cref="SignatureSource.Drawn"/>.</summary>
    public IReadOnlyList<SignatureStroke> Strokes { get; private init; } = [];

    /// <summary>
    /// The name printed beneath the mark, and used in the announcement. Always present, whichever
    /// route was taken: a scanned signature is often illegible, and the field should still say
    /// whose it is.
    /// </summary>
    public string SignerName { get; set; } = string.Empty;

    /// <summary>Why the document was signed, written into the signature's /Reason.</summary>
    public string? Reason { get; set; }

    /// <summary>Where it was signed, written into /Location.</summary>
    public string? Location { get; set; }

    /// <summary>When it was signed.</summary>
    public DateTimeOffset SignedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Whether the date is printed beneath the mark.</summary>
    public bool ShowDate { get; set; } = true;

    #endregion

    #region Factories

    /// <summary>A signature taken from an image file.</summary>
    public static SignatureMark FromImage(string imagePath, string signerName) =>
        new(SignatureSource.Image)
        {
            ImagePath = imagePath,
            SignerName = signerName,
        };

    /// <summary>A signature drawn from the user's typed name.</summary>
    public static SignatureMark FromTypedName(string name) =>
        new(SignatureSource.TypedName)
        {
            TypedName = name,
            SignerName = name,
        };

    /// <summary>A signature drawn by hand.</summary>
    public static SignatureMark FromStrokes(IReadOnlyList<SignatureStroke> strokes, string signerName) =>
        new(SignatureSource.Drawn)
        {
            Strokes = strokes.Where(s => s.IsDrawable).ToList(),
            SignerName = signerName,
        };

    #endregion

    #region Validity and description

    /// <summary>Whether this mark has anything to draw.</summary>
    public bool IsUsable => Source switch
    {
        SignatureSource.Image => !string.IsNullOrWhiteSpace(ImagePath) && File.Exists(ImagePath),
        SignatureSource.TypedName => !string.IsNullOrWhiteSpace(TypedName),
        SignatureSource.Drawn => Strokes.Count > 0,
        _ => false,
    };

    /// <summary>
    /// How much of the drawing area a drawn signature covers, from 0 to 1.
    ///
    /// Used to warn about a signature that is little more than a dot or a single short line. A
    /// sighted user sees immediately that they have not drawn anything usable; a blind user has no
    /// such feedback and would otherwise sign a document with a stray click.
    /// </summary>
    public double DrawnExtent
    {
        get
        {
            if (Source != SignatureSource.Drawn || Strokes.Count == 0)
                return 0;

            double minX = 1, maxX = 0, minY = 1, maxY = 0;
            int points = 0;

            foreach (var stroke in Strokes)
            {
                foreach (var (x, y) in stroke.Points)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                    points++;
                }
            }

            if (points < 2)
                return 0;

            return Math.Max(maxX - minX, maxY - minY);
        }
    }

    /// <summary>Whether a drawn signature is too small to be a real mark.</summary>
    public bool IsSuspiciouslySmall =>
        Source == SignatureSource.Drawn && DrawnExtent < 0.15;

    /// <summary>The mark described for speech, so the user hears what they are about to apply.</summary>
    public string Describe()
    {
        string what = Source switch
        {
            SignatureSource.Image => $"an image of your signature from {Path.GetFileName(ImagePath)}",
            SignatureSource.TypedName => $"your name, {TypedName}, drawn as text",
            SignatureSource.Drawn =>
                $"a signature you drew, {Strokes.Count} {(Strokes.Count == 1 ? "stroke" : "strokes")}",
            _ => "a signature",
        };

        var parts = new List<string>(4) { what };

        if (!string.IsNullOrWhiteSpace(SignerName))
            parts.Add($"signed by {SignerName}");

        if (ShowDate)
            parts.Add($"dated {SignedAt:d MMMM yyyy}");

        if (!string.IsNullOrWhiteSpace(Reason))
            parts.Add($"reason: {Reason}");

        return string.Join(", ", parts);
    }

    #endregion
}

#endregion
