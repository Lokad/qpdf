using Lokad.Qpdf.Objects;

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

    internal PdfTokenizer(ReadOnlyMemory<byte> data)
    {
        _data = data;
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
        SkipWhitespaceAndComments();
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

            token = ReadHexString();
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

            throw new QpdfParseException("Lone '>' byte outside a dictionary close.", offset, null);
        }

        if (b == (byte)'(')
        {
            token = ReadLiteralString();
            return true;
        }

        if (b == (byte)'/')
        {
            token = ReadName();
            return true;
        }

        if (IsNumberStart(b))
        {
            token = ReadNumberOrReference();
            return true;
        }

        if (IsLetter(b))
        {
            token = ReadKeyword();
            return true;
        }

        throw new QpdfParseException($"Unexpected byte 0x{b:X2}.", offset, null);
    }

    internal void SkipWhitespaceAndComments()
    {
        ReadOnlySpan<byte> span = _data.Span;
        while (_position < span.Length)
        {
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
                    _position += 1;
                }

                continue;
            }

            break;
        }
    }

    private static bool IsWhitespace(byte b)
    {
        return b == 0x00 || b == 0x09 || b == 0x0A || b == 0x0C || b == 0x0D || b == 0x20;
    }

    private static bool IsDelimiter(byte b)
    {
        return b == (byte)'(' || b == (byte)')' || b == (byte)'<' || b == (byte)'>'
            || b == (byte)'[' || b == (byte)']' || b == (byte)'/' || b == (byte)'%'
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

    private PdfToken ReadName()
    {
        long offset = _position;
        ReadOnlySpan<byte> span = _data.Span;
        int start = _position + 1;
        int i = start;
        while (i < span.Length && !IsDelimiter(span[i]))
        {
            i += 1;
        }

        _position = i;
        return new PdfToken(PdfTokenKind.Name, _data.Slice(start, i - start), offset);
    }

    private PdfToken ReadLiteralString()
    {
        long offset = _position;
        ReadOnlySpan<byte> span = _data.Span;
        int i = _position + 1;
        int depth = 1;
        while (i < span.Length)
        {
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

        throw new QpdfParseException("Unterminated literal string.", offset, null);
    }

    private PdfToken ReadHexString()
    {
        long offset = _position;
        ReadOnlySpan<byte> span = _data.Span;
        int i = _position + 1;
        while (i < span.Length && span[i] != (byte)'>')
        {
            i += 1;
        }

        if (i >= span.Length)
        {
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

    private PdfToken ReadKeyword()
    {
        long offset = _position;
        ReadOnlySpan<byte> span = _data.Span;
        int i = _position;
        while (i < span.Length && !IsDelimiter(span[i]))
        {
            i += 1;
        }

        ReadOnlyMemory<byte> word = _data.Slice((int)offset, i - (int)offset);
        _position = i;
        string text = ToAscii(word.Span);
        PdfTokenKind kind = text switch
        {
            "null" => PdfTokenKind.Null,
            "true" => PdfTokenKind.True,
            "false" => PdfTokenKind.False,
            "R" => PdfTokenKind.Reference,
            "obj" => PdfTokenKind.Object,
            "endobj" => PdfTokenKind.EndObject,
            "stream" => PdfTokenKind.Stream,
            "endstream" => PdfTokenKind.EndStream,
            "xref" => PdfTokenKind.XRef,
            "trailer" => PdfTokenKind.Trailer,
            "startxref" => PdfTokenKind.StartXRef,
            _ => throw new QpdfParseException($"Unknown keyword '{text}'.", offset, null),
        };
        return new PdfToken(kind, word, offset);
    }

    private static string ToAscii(ReadOnlySpan<byte> span)
    {
        char[] chars = new char[span.Length];
        for (int i = 0; i < span.Length; i++)
        {
            chars[i] = span[i] < 0x80 ? (char)span[i] : '?';
        }

        return new string(chars);
    }
}

