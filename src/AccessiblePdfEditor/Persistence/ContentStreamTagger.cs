using System.Globalization;
using System.Text;
using AccessiblePdfEditor.Model.Elements;

namespace AccessiblePdfEditor.Persistence;

// =====================================================================================
//  ContentStreamTagger.cs
//
//  Wraps the text already drawn on a page in marked content, so that structure elements
//  have something to point at.
//
//  This is the hard half of tagging an untagged document. A structure tree on its own is
//  a list of labels attached to nothing; the tree becomes real only when each element
//  refers to an MCID, and an MCID exists only where the content stream says so. The text
//  is already drawn, so the only way to tag it is to go back into the stream and mark it.
//
//  WHY IT IS DONE BY POSITION, WHICH IS NOT THE OBVIOUS CHOICE
//
//  Two simpler approaches were tried against a real document first, and both fail:
//
//    Wrap each BT/ET text object.  The sample's entire first page is ONE BT/ET block
//                                  containing sixteen separate show operators, so this
//                                  yields one tag per page. Useless.
//
//    Match operators by their text. The operands are <002B0052...> — glyph indices into
//                                  a subset font, not characters. There is nothing to
//                                  match against without the font's own CMap.
//
//  What IS reliable is where the text lands on the page, because that is exactly what the
//  operators say: 60 772 Td puts the next run at a known point. So this tracks the text
//  matrix through the operators that move it, works out the origin of every show
//  operator, and hands that position to the caller to match against the elements it
//  already detected by layout. Position is the common language between the two.
//
//  WHAT IT REFUSES TO DO
//
//  It never modifies an operator, only inserts between them. A tagger that rewrote
//  operands could change what the page looks like, and the primary user cannot check.
//  Everything here is an insertion at a byte offset, so the drawing is bit-identical and
//  the only difference is the marks around it.
// =====================================================================================

#region ContentTag — what a piece of text should be marked as

/// <summary>
/// The tag to write, and which element it belongs to.
///
/// The element key is not decoration. Two adjacent paragraphs are both tagged "P", and comparing
/// tag names alone would run them together into a single mark — so the document would have one
/// paragraph where it should have two, and a reader moving by paragraph would skip the boundary.
/// The key is what makes "same tag" and "same thing" different questions.
/// </summary>
public readonly record struct ContentTag(string Name, int ElementKey);

#endregion

#region MarkedRun — one stretch of content that became one tag

/// <summary>A run of text that was wrapped in one marked-content sequence.</summary>
public readonly record struct MarkedRun(int MarkedContentId, double X, double Y, ContentTag Tag);

#endregion

#region TaggedContent

/// <summary>The rewritten content stream, and where each mark ended up.</summary>
public sealed record TaggedContent(byte[] Content, IReadOnlyList<MarkedRun> Runs, int TextOperatorCount)
{
    public bool MarkedAnything => Runs.Count > 0;

    /// <summary>
    /// The share of the page's text that ended up inside a tag.
    ///
    /// The number that decides whether the document may claim to be tagged at all. Marking a third
    /// of a page and then setting /MarkInfo /Marked true produces a file that says it is accessible
    /// and is not, which readers and checkers both believe.
    /// </summary>
    public double Coverage => TextOperatorCount == 0 ? 1.0 : (double)MarkedOperators / TextOperatorCount;

    /// <summary>How many show operators fell inside a tag.</summary>
    public int MarkedOperators { get; init; }
}

#endregion

#region ContentStreamTagger

/// <summary>Inserts marked-content sequences around the text drawn on a page.</summary>
internal static class ContentStreamTagger
{
    #region The walk

    /// <summary>
    /// Rewrites a page's content so that each run of text is wrapped in a marked-content sequence.
    /// </summary>
    /// <param name="content">The page's existing operators.</param>
    /// <param name="firstMarkedContentId">
    /// The identifier to start from. Pages are numbered independently, but a page that already
    /// carries marks must not have them duplicated, so the caller passes the next free one.
    /// </param>
    /// <param name="classify">
    /// Decides which element a piece of text at a given point belongs to, and returns the tag name
    /// to use. Returning null leaves that text unmarked, which is correct for page furniture.
    /// </param>
    public static TaggedContent Tag(
        byte[] content, int firstMarkedContentId, Func<double, double, ContentTag?> classify)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(classify);

        var reader = new OperatorReader(content);
        var runs = new List<MarkedRun>();
        var output = new MemoryStream(content.Length + 512);

        var state = new TextState();
        int copiedTo = 0;
        int nextId = firstMarkedContentId;
        int shows = 0;
        int marked = 0;

        ContentTag? openTag = null;

        while (reader.ReadOperator() is { } op)
        {
            state.Apply(op);

            if (!IsShowingText(op.Name))
                continue;

            shows++;

            // Where this run of text starts, in page space.
            var (x, y) = state.CurrentPoint();
            ContentTag? tag = classify(x, y);

            if (tag is not null)
                marked++;

            if (tag == openTag)
                continue;

            // The tag changed, so the open sequence ends here and a new one may begin. Everything
            // between the last insertion point and the start of this operator is copied verbatim.
            int insertAt = op.StartOffset;

            output.Write(content, copiedTo, insertAt - copiedTo);
            copiedTo = insertAt;

            if (openTag is not null)
                Write(output, "EMC\n");

            if (tag is { } opening)
            {
                Write(output, $"/{opening.Name} <</MCID {nextId.ToString(CultureInfo.InvariantCulture)}>> BDC\n");
                runs.Add(new MarkedRun(nextId, x, y, opening));
                nextId++;
            }

            openTag = tag;
        }

        output.Write(content, copiedTo, content.Length - copiedTo);

        // A sequence left open at the end of the stream would swallow everything drawn after it on
        // any page whose content continues in another stream.
        if (openTag is not null)
            Write(output, "\nEMC\n");

        return new TaggedContent(output.ToArray(), runs, shows) { MarkedOperators = marked };
    }

    private static bool IsShowingText(string name) =>
        name is "Tj" or "TJ" or "'" or "\"";

    private static void Write(Stream stream, string text)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    #endregion

    #region Text position
    // Only as much of the graphics state as it takes to know where a show operator puts its text.
    // Font size, colour, word spacing and the rest are irrelevant here and deliberately ignored:
    // every one of them is another thing to get wrong for no gain.

    private sealed class TextState
    {
        private readonly Stack<double[]> _saved = new();

        // Row-major 2x3 affine transforms, as PDF writes them: a b c d e f.
        private double[] _ctm = Identity();
        private double[] _text = Identity();
        private double[] _line = Identity();
        private double _leading;

        private static double[] Identity() => [1, 0, 0, 1, 0, 0];

        public void Apply(ContentOperator op)
        {
            switch (op.Name)
            {
                case "q":
                    _saved.Push((double[])_ctm.Clone());
                    break;

                case "Q":
                    if (_saved.Count > 0)
                        _ctm = _saved.Pop();
                    break;

                case "cm" when op.Numbers.Count >= 6:
                    _ctm = Multiply(Matrix(op.Numbers), _ctm);
                    break;

                case "BT":
                    _text = Identity();
                    _line = Identity();
                    break;

                case "Tm" when op.Numbers.Count >= 6:
                    _text = Matrix(op.Numbers);
                    _line = (double[])_text.Clone();
                    break;

                case "TL" when op.Numbers.Count >= 1:
                    _leading = op.Numbers[^1];
                    break;

                case "Td" when op.Numbers.Count >= 2:
                    Translate(op.Numbers[^2], op.Numbers[^1]);
                    break;

                case "TD" when op.Numbers.Count >= 2:
                    _leading = -op.Numbers[^1];
                    Translate(op.Numbers[^2], op.Numbers[^1]);
                    break;

                case "T*":
                    Translate(0, -_leading);
                    break;

                // Both of these move to the next line before showing anything.
                case "'":
                case "\"":
                    Translate(0, -_leading);
                    break;
            }
        }

        /// <summary>Moves to a new line, which in PDF is relative to the START of the current one.</summary>
        private void Translate(double x, double y)
        {
            _line = Multiply([1, 0, 0, 1, x, y], _line);
            _text = (double[])_line.Clone();
        }

        /// <summary>Where the next text will be drawn, in page space.</summary>
        public (double X, double Y) CurrentPoint()
        {
            var combined = Multiply(_text, _ctm);
            return (combined[4], combined[5]);
        }

        private static double[] Matrix(IReadOnlyList<double> numbers)
        {
            int start = numbers.Count - 6;
            return
            [
                numbers[start], numbers[start + 1], numbers[start + 2],
                numbers[start + 3], numbers[start + 4], numbers[start + 5],
            ];
        }

        private static double[] Multiply(double[] m, double[] n) =>
        [
            m[0] * n[0] + m[1] * n[2],
            m[0] * n[1] + m[1] * n[3],
            m[2] * n[0] + m[3] * n[2],
            m[2] * n[1] + m[3] * n[3],
            m[4] * n[0] + m[5] * n[2] + n[4],
            m[4] * n[1] + m[5] * n[3] + n[5],
        ];
    }

    #endregion

    #region Reading operators
    // A content stream is postfix: operands, then the operator. This reads just enough to know
    // which operator is which, where it starts, and what numbers preceded it. Strings, names,
    // arrays and dictionaries are skipped rather than parsed, because nothing here needs them —
    // but they MUST be skipped properly, since a string can contain anything at all including
    // text that looks like an operator.

    private readonly record struct ContentOperator(string Name, int StartOffset, IReadOnlyList<double> Numbers);

    private sealed class OperatorReader(byte[] content)
    {
        private readonly List<double> _numbers = [];
        private int _position;
        private int _operandStart;

        /// <summary>
        /// Whether the start of the current operator's operands has been noted yet.
        ///
        /// This flag is the whole of a bug that silently destroyed text. The start used to be
        /// re-noted on any token while no NUMBERS had accumulated — so for an operator whose
        /// operand is a string, such as &lt;0041&gt; Tj, it ended up pointing at the Tj itself. A
        /// mark inserted there lands BETWEEN the string and the operator that shows it, leaving Tj
        /// with nothing to draw. The page still opened, and a third of its text was gone.
        /// </summary>
        private bool _haveOperandStart;

        public ContentOperator? ReadOperator()
        {
            while (_position < content.Length)
            {
                SkipWhitespaceAndComments();

                if (_position >= content.Length)
                    return null;

                if (!_haveOperandStart)
                {
                    _operandStart = _position;
                    _haveOperandStart = true;
                }

                byte b = content[_position];

                if (b == '(') { SkipLiteralString(); continue; }
                if (b == '<' && Peek(1) == '<') { _position += 2; continue; }
                if (b == '>' && Peek(1) == '>') { _position += 2; continue; }
                if (b == '<') { SkipHexString(); continue; }
                if (b == '[' || b == ']' || b == '{' || b == '}') { _position++; continue; }
                if (b == '/') { SkipName(); continue; }

                if (IsNumberStart(b))
                {
                    _numbers.Add(ReadNumber());
                    continue;
                }

                string name = ReadKeyword();

                if (name.Length == 0)
                {
                    _position++;
                    continue;
                }

                // Inline images hide arbitrary bytes between ID and EI, and those bytes can look
                // like anything. Skipped whole, or the reader would start parsing image data.
                if (name == "BI")
                {
                    SkipInlineImage();
                    Reset();
                    continue;
                }

                var op = new ContentOperator(name, _operandStart, _numbers.ToArray());
                Reset();

                return op;
            }

            return null;
        }

        private void Reset()
        {
            _numbers.Clear();
            _haveOperandStart = false;
        }

        private byte Peek(int ahead) =>
            _position + ahead < content.Length ? content[_position + ahead] : (byte)0;

        private static bool IsWhitespace(byte b) =>
            b is 0 or 9 or 10 or 12 or 13 or 32;

        private static bool IsDelimiter(byte b) =>
            b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
                or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

        private static bool IsNumberStart(byte b) =>
            (b >= '0' && b <= '9') || b == '+' || b == '-' || b == '.';

        private void SkipWhitespaceAndComments()
        {
            while (_position < content.Length)
            {
                byte b = content[_position];

                if (IsWhitespace(b))
                {
                    _position++;
                }
                else if (b == '%')
                {
                    while (_position < content.Length && content[_position] is not ((byte)'\n' or (byte)'\r'))
                        _position++;
                }
                else
                {
                    return;
                }
            }
        }

        private void SkipLiteralString()
        {
            _position++;
            int depth = 1;

            while (_position < content.Length && depth > 0)
            {
                byte b = content[_position];

                if (b == '\\') { _position += 2; continue; }
                if (b == '(') depth++;
                if (b == ')') depth--;

                _position++;
            }
        }

        private void SkipHexString()
        {
            _position++;

            while (_position < content.Length && content[_position] != '>')
                _position++;

            if (_position < content.Length)
                _position++;
        }

        private void SkipName()
        {
            _position++;

            while (_position < content.Length
                   && !IsWhitespace(content[_position])
                   && !IsDelimiter(content[_position]))
            {
                _position++;
            }
        }

        private double ReadNumber()
        {
            int start = _position;

            while (_position < content.Length
                   && !IsWhitespace(content[_position])
                   && !IsDelimiter(content[_position]))
            {
                _position++;
            }

            string text = Encoding.Latin1.GetString(content, start, _position - start);

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value
                : 0;
        }

        private string ReadKeyword()
        {
            int start = _position;

            while (_position < content.Length
                   && !IsWhitespace(content[_position])
                   && !IsDelimiter(content[_position]))
            {
                _position++;
            }

            return Encoding.Latin1.GetString(content, start, _position - start);
        }

        private void SkipInlineImage()
        {
            // Everything from here to the EI that ends the image data.
            while (_position + 1 < content.Length)
            {
                if (content[_position] == 'E' && content[_position + 1] == 'I'
                    && (_position == 0 || IsWhitespace(content[_position - 1])))
                {
                    _position += 2;
                    return;
                }

                _position++;
            }

            _position = content.Length;
        }
    }

    #endregion
}

#endregion
