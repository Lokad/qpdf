using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Syntax;

/// <summary>Token kinds produced from raw PDF bytes.</summary>
internal enum PdfTokenKind
{
    Null,
    True,
    False,
    Integer,
    Real,
    Name,
    LiteralString,
    HexString,
    ArrayOpen,
    ArrayClose,
    DictionaryOpen,
    DictionaryClose,
    Reference,
    Object,
    EndObject,
    Stream,
    EndStream,
    XRef,
    Trailer,
    StartXRef,
    End,
    UnknownWord,
    BadToken,
}

/// <summary>A token as a raw byte slice plus its absolute offset.</summary>
internal readonly struct PdfToken
{
    internal PdfToken(PdfTokenKind kind, ReadOnlyMemory<byte> data, long offset)
    {
        Kind = kind;
        Data = data;
        Offset = offset;
    }

    internal PdfTokenKind Kind { get; }

    internal ReadOnlyMemory<byte> Data { get; }

    internal long Offset { get; }
}

/// <summary>
/// Byte-oriented PDF tokenizer. Never decodes the input as text; string and
/// name escapes are preserved raw for the parser. Whitespace and comments are
/// skipped. All failures throw <see cref="QpdfParseException" /> with offsets.
/// </summary>
internal sealed class PdfTokenizer
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _position;
    private ResourceGovernor? _governor;
    private int _scanWork;
    private readonly CancellationToken _token;

    internal PdfTokenizer(ReadOnlyMemory<byte> data)
        : this(data, CancellationToken.None) { }

    internal PdfTokenizer(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        _data = data;
        _token = token;
        _position = 0;
    }

    internal long Position => _position;

    internal ReadOnlyMemory<byte> Data => _data;

    internal void SetPosition(long position)
    {
        if (position < 0 || position > _data.Length)
        {
            throw new QpdfParseException($"Seek position {position} is outside the input.", position, null);
        }

        _position = (int)position;
    }

    internal bool TryReadToken(out PdfToken token)
    {
        return TryReadTokenCore(out token, false);
    }

    internal void ResetForObject(long position)
    {
        SetPosition(position);
        _governor = null;
        _scanWork = 0;
    }

    internal bool TryReadToken(out PdfToken token, bool tolerateErrors, ResourceGovernor governor)
    {
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        _governor.CheckCancellation();
        return TryReadTokenCore(out token, tolerateErrors);
    }

    internal bool TryReadReferencePart(bool generation, out PdfToken token, out long offset,
        bool tolerateErrors, ResourceGovernor governor)
    {
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        _governor.CheckCancellation();
        _token.ThrowIfCancellationRequested();
        SkipWhitespaceAndComments();
        offset = _position;
        ReadOnlySpan<byte> data = _data.Span;
        if (_position >= data.Length || (generation ? !IsNumberStart(data[_position])
            : data[_position] != (byte)'R' || (_position + 1 < data.Length && !IsDelimiter(data[_position + 1]))))
        { token = default; return false; }
        return TryReadTokenAfterWhitespace(out token, tolerateErrors);
    }

    private bool TryReadTokenCore(out PdfToken token, bool tolerateErrors)
    {
        _token.ThrowIfCancellationRequested();
        SkipWhitespaceAndComments();
        return TryReadTokenAfterWhitespace(out token, tolerateErrors);
    }

    private bool TryReadTokenAfterWhitespace(out PdfToken token, bool tolerateErrors)
    {
        if (_position >= _data.Length)
        {
            token = new PdfToken(PdfTokenKind.End, ReadOnlyMemory<byte>.Empty, _position);
            return false;
        }

        long offset = _position;
        byte b = _data.Span[_position];
        if (b == (byte)'[')
        {
            _position += 1;
            token = new PdfToken(PdfTokenKind.ArrayOpen, _data.Slice((int)offset, 1), offset);
            return true;
        }

        if (b == (byte)']')
        {
            _position += 1;
            token = new PdfToken(PdfTokenKind.ArrayClose, _data.Slice((int)offset, 1), offset);
            return true;
        }

        if (b == (byte)'<')
        {
            if (_position + 1 < _data.Length && _data.Span[_position + 1] == (byte)'<')
            {
                _position += 2;
                token = new PdfToken(PdfTokenKind.DictionaryOpen, _data.Slice((int)offset, 2), offset);
                return true;
            }

            token = ReadHexString(tolerateErrors);
            return true;
        }

        if (b == (byte)'>')
        {
            if (_position + 1 < _data.Length && _data.Span[_position + 1] == (byte)'>')
            {
                _position += 2;
                token = new PdfToken(PdfTokenKind.DictionaryClose, _data.Slice((int)offset, 2), offset);
                return true;
            }

            if (tolerateErrors)
            {
                _position += 1;
                token = new PdfToken(PdfTokenKind.BadToken, _data.Slice((int)offset, 1), offset);
                return true;
            }

            throw new QpdfParseException("Lone '>' byte outside a dictionary close.", offset, null);
        }

        if (b == (byte)'(')
        {
            token = ReadLiteralString(tolerateErrors);
            return true;
        }

        if (b == (byte)'/')
        {
            token = ReadName(tolerateErrors);
            return true;
        }

        if (IsNumberStart(b))
        {
            try
            {
                token = ReadNumberOrReference();
                return true;
            }
            catch (QpdfParseException) when (tolerateErrors)
            {
                token = ReadKeyword(true);
                return true;
            }
        }

        if (IsLetter(b))
        {
            token = ReadKeyword(tolerateErrors);
            return true;
        }

        if (tolerateErrors)
        {
            if (b is (byte)')' or (byte)'{' or (byte)'}')
            {
                _position += 1;
                token = new PdfToken(PdfTokenKind.BadToken, _data.Slice((int)offset, 1), offset);
            }
            else
            {
                token = ReadKeyword(true);
            }

            return true;
        }

        throw new QpdfParseException($"Unexpected byte 0x{b:X2}.", offset, null);
    }

    internal void SkipWhitespaceAndComments()
    {
        ReadOnlySpan<byte> span = _data.Span;
        while (_position < span.Length)
        {
            ScanStep();
            byte b = span[_position];
            if (IsWhitespace(b))
            {
                _position += 1;
                continue;
            }

            if (b == (byte)'%')
            {
                while (_position < span.Length && span[_position] != (byte)'\n' && span[_position] != (byte)'\r')
                {
                    ScanStep();
                    _position += 1;
                }

                continue;
            }

            break;
        }
    }

    internal static bool IsWhitespace(byte b)
    {
        return b == 0x00 || b == 0x09 || b == 0x0A || b == 0x0C || b == 0x0D || b == 0x20;
    }

    internal static bool IsDelimiter(byte b)
    {
        return b == (byte)'(' || b == (byte)')' || b == (byte)'<' || b == (byte)'>'
            || b == (byte)'[' || b == (byte)']' || b == (byte)'/' || b == (byte)'%'
            || b == (byte)'{' || b == (byte)'}'
            || IsWhitespace(b);
    }

    private static bool IsLetter(byte b)
    {
        return (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z');
    }

    private static bool IsNumberStart(byte b)
    {
        return (b >= (byte)'0' && b <= (byte)'9') || b == (byte)'+' || b == (byte)'-' || b == (byte)'.';
    }

    private PdfToken ReadName(bool tolerateErrors)
    {
        long offset = _position;
        ReadOnlySpan<byte> span = _data.Span;
        int start = _position + 1;
        int i = start;
        bool nulEscape = false;
        while (i < span.Length && !IsDelimiter(span[i]))
        {
            ScanStep();
            // Inspect original escapes exactly once. #2300 decodes to the
            // characters "#00", which are valid name bytes rather than NUL.
            nulEscape |= span[i] == (byte)'#' && span.Length - i >= 3
                && span[i + 1] == (byte)'0' && span[i + 2] == (byte)'0';
            i += 1;
        }

        _position = i;
        if (nulEscape && !tolerateErrors)
        {
            throw new QpdfParseException("NUL escape is invalid in a name.", offset, null);
        }

        return new PdfToken(nulEscape ? PdfTokenKind.BadToken : PdfTokenKind.Name,
            _data.Slice(start, i - start), offset);
    }

    private PdfToken ReadLiteralString(bool tolerateErrors)
    {
        long offset = _position;
        ReadOnlySpan<byte> span = _data.Span;
        int i = _position + 1;
        int depth = 1;
        while (i < span.Length)
        {
            ScanStep();
            byte b = span[i];
            if (b == (byte)'\\')
            {
                i += 2;
                continue;
            }

            if (b == (byte)'(')
            {
                depth += 1;
                i += 1;
                continue;
            }

            if (b == (byte)')')
            {
                depth -= 1;
                i += 1;
                if (depth == 0)
                {
                    _position = i;
                    return new PdfToken(PdfTokenKind.LiteralString, _data.Slice((int)offset + 1, i - (int)offset - 2), offset);
                }

                continue;
            }

            i += 1;
        }

        if (tolerateErrors)
        {
            _position = span.Length;
            return new PdfToken(PdfTokenKind.BadToken, _data.Slice((int)offset), offset);
        }

        throw new QpdfParseException("Unterminated literal string.", offset, null);
    }

    private PdfToken ReadHexString(bool tolerateErrors)
    {
        long offset = _position;
        ReadOnlySpan<byte> span = _data.Span;
        int i = _position + 1;
        while (i < span.Length && span[i] != (byte)'>')
        {
            ScanStep();
            if (tolerateErrors && !IsWhitespace(span[i])
                && !(span[i] >= (byte)'0' && span[i] <= (byte)'9')
                && !(span[i] >= (byte)'a' && span[i] <= (byte)'f')
                && !(span[i] >= (byte)'A' && span[i] <= (byte)'F'))
            {
                _position = i + 1;
                return new PdfToken(PdfTokenKind.BadToken, _data.Slice((int)offset, i - (int)offset + 1), offset);
            }

            i += 1;
        }

        if (i >= span.Length)
        {
            if (tolerateErrors)
            {
                _position = span.Length;
                return new PdfToken(PdfTokenKind.BadToken, _data.Slice((int)offset), offset);
            }

            throw new QpdfParseException("Unterminated hexadecimal string.", offset, null);
        }

        int start = (int)offset + 1;
        _position = i + 1;
        return new PdfToken(PdfTokenKind.HexString, _data.Slice(start, i - start), offset);
    }

    private PdfToken ReadNumberOrReference()
    {
        long offset = _position;
        ReadOnlySpan<byte> span = _data.Span;
        int i = _position;
        bool dot = false;
        if (i < span.Length && (span[i] == (byte)'+' || span[i] == (byte)'-'))
        {
            i += 1;
        }

        int digits = 0;
        while (i < span.Length)
        {
            ScanStep();
            byte b = span[i];
            if (b >= (byte)'0' && b <= (byte)'9')
            {
                digits += 1;
                i += 1;
                continue;
            }

            if (b == (byte)'.' && !dot)
            {
                dot = true;
                i += 1;
                continue;
            }

            break;
        }

        if (digits == 0)
        {
            throw new QpdfParseException("Malformed numeric token.", offset, null);
        }

        if (i < span.Length && !IsDelimiter(span[i]))
        {
            throw new QpdfParseException("Malformed numeric token.", offset, null);
        }

        _position = i;
        PdfTokenKind kind = dot ? PdfTokenKind.Real : PdfTokenKind.Integer;
        return new PdfToken(kind, _data.Slice((int)offset, i - (int)offset), offset);
    }

    private PdfToken ReadKeyword(bool tolerateErrors)
    {
        long offset = _position;
        ReadOnlySpan<byte> span = _data.Span;
        int i = _position;
        while (i < span.Length && !IsDelimiter(span[i]))
        {
            ScanStep();
            i += 1;
        }

        ReadOnlyMemory<byte> word = _data.Slice((int)offset, i - (int)offset);
        _position = i;
        ReadOnlySpan<byte> value = word.Span;
        PdfTokenKind kind = value.SequenceEqual("null"u8) ? PdfTokenKind.Null
            : value.SequenceEqual("true"u8) ? PdfTokenKind.True
            : value.SequenceEqual("false"u8) ? PdfTokenKind.False
            : value.SequenceEqual("R"u8) ? PdfTokenKind.Reference
            : value.SequenceEqual("obj"u8) ? PdfTokenKind.Object
            : value.SequenceEqual("endobj"u8) ? PdfTokenKind.EndObject
            : value.SequenceEqual("stream"u8) ? PdfTokenKind.Stream
            : value.SequenceEqual("endstream"u8) ? PdfTokenKind.EndStream
            : value.SequenceEqual("xref"u8) ? PdfTokenKind.XRef
            : value.SequenceEqual("trailer"u8) ? PdfTokenKind.Trailer
            : value.SequenceEqual("startxref"u8) ? PdfTokenKind.StartXRef
            : PdfTokenKind.UnknownWord;
        if (kind == PdfTokenKind.UnknownWord && !tolerateErrors)
        {
            throw new QpdfParseException("Unknown keyword.", offset, null);
        }

        return new PdfToken(kind, word, offset);
    }

    private void ScanStep()
    {
        _token.ThrowIfCancellationRequested();
        if (++_scanWork >= 4096)
        {
            _governor?.ChargeWork(1L);
            _governor?.CheckCancellation();
            _scanWork = 0;
        }
    }
}
