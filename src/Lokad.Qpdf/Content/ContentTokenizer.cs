using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Content;

/// <summary>Token kinds produced from content-stream bytes.</summary>
internal enum ContentTokenKind
{
    Integer,
    Real,
    LiteralString,
    HexString,
    Name,
    Boolean,
    Null,
    ArrayOpen,
    ArrayClose,
    DictionaryOpen,
    DictionaryClose,
    Operator,
    InlineImage,
}

/// <summary>A content token as a raw byte slice plus its absolute offset.</summary>
internal readonly struct ContentToken
{
    internal ContentToken(ContentTokenKind kind, ReadOnlyMemory<byte> data, long offset)
    {
        Kind = kind;
        Data = data;
        Offset = offset;
    }

    internal ContentTokenKind Kind { get; }

    internal ReadOnlyMemory<byte> Data { get; }

    internal long Offset { get; }
}

/// <summary>
/// Byte-oriented content-stream tokenizer. Mirrors the object tokenizer
/// conventions: raw spans, skipped whitespace and comments, strict failures
/// with offsets. Words named ID start inline images; call
/// TryReadInlineImageData immediately after reading one.
/// </summary>
internal sealed class ContentTokenizer
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly QpdfLimits _limits;
    private readonly ResourceGovernor _governor;
    private int _position;

    internal ContentTokenizer(ReadOnlyMemory<byte> data, QpdfLimits limits, ResourceGovernor governor)
    {
        _data = data;
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        limits.Validate();
    }

    internal long Position => _position;

    internal bool TryReadToken(out ContentToken token)
    {
        SkipWhitespaceAndComments();
        if (_position >= _data.Length)
        {
            token = new ContentToken(ContentTokenKind.Operator, ReadOnlyMemory<byte>.Empty, _position);
            return false;
        }

        _governor.ChargeWork(1L);
        long offset = _position;
        byte b = _data.Span[_position];
        if (b == (byte)'[')
        {
            _position += 1;
            token = new ContentToken(ContentTokenKind.ArrayOpen, _data.Slice((int)offset, 1), offset);
            return true;
        }

        if (b == (byte)']')
        {
            _position += 1;
            token = new ContentToken(ContentTokenKind.ArrayClose, _data.Slice((int)offset, 1), offset);
            return true;
        }

        if (b == (byte)'<')
        {
            if (_position + 1 < _data.Length && _data.Span[_position + 1] == (byte)'<')
            {
                _position += 2;
                token = new ContentToken(ContentTokenKind.DictionaryOpen, _data.Slice((int)offset, 2), offset);
                return true;
            }

            return ReadHexString(offset, out token);
        }

        if (b == (byte)'(')
        {
            return ReadLiteralString(offset, out token);
        }

        if (b == (byte)'/')
        {
            return ReadName(offset, out token);
        }

        if (IsNumberStart(b))
        {
            return ReadNumber(offset, out token);
        }

        if (b == (byte)'>')
        {
            if (_position + 1 < _data.Length && _data.Span[_position + 1] == (byte)'>')
            {
                _position += 2;
                token = new ContentToken(ContentTokenKind.DictionaryClose, _data.Slice((int)offset, 2), offset);
                return true;
            }

            throw new QpdfParseException("Unexpected closing delimiter in content stream.", offset, null);
        }

        if (b == (byte)')')
        {
            throw new QpdfParseException("Unexpected closing delimiter in content stream.", offset, null);
        }

        return ReadWord(offset, out token);
    }
    internal bool TryReadInlineImageData(out ContentToken token)
    {
        if (_position >= _data.Length)
        {
            throw new QpdfParseException("Unterminated inline image in content stream.", _position, null);
        }

        _position += 1;
        int dataStart = _position;
        int end = FindImageEnd(dataStart);
        if (end < 0)
        {
            throw new QpdfParseException("Unterminated inline image in content stream.", dataStart, null);
        }

        _governor.ChargeWork(1L);
        token = new ContentToken(ContentTokenKind.InlineImage, _data.Slice(dataStart, end - dataStart), dataStart);
        _position = end + 2;
        return true;
    }

    private int FindImageEnd(int dataStart)
    {
        ReadOnlySpan<byte> span = _data.Span;
        int search = dataStart;
        int last = -1;
        while (search + 1 < span.Length)
        {
            int candidate = IndexOfEI(span, search);
            if (candidate < 0)
            {
                break;
            }

            if (candidate > 0 && IsImageDelimiter(span[candidate - 1])
                && (candidate + 2 >= span.Length || IsImageDelimiter(span[candidate + 2])))
            {
                if (ValidateEILookahead(candidate + 2))
                {
                    return candidate;
                }

                last = candidate;
            }

            search = candidate + 2;
        }

        return last;
    }

    private static int IndexOfEI(ReadOnlySpan<byte> span, int start)
    {
        for (int i = start; i + 1 < span.Length; i++)
        {
            if (span[i] == (byte)'E' && span[i + 1] == (byte)'I')
            {
                return i;
            }
        }

        return -1;
    }

    private bool ValidateEILookahead(int position)
    {
        for (int i = 0; i < 10; i++)
        {
            _governor.ChargeWork(1L);
            ProbeKind kind = ScanToken(position, out int next, out bool suspiciousWord);
            if (kind == ProbeKind.End)
            {
                return true;
            }

            if (kind == ProbeKind.Bad || suspiciousWord)
            {
                return false;
            }

            position = next;
        }

        return true;
    }

    private enum ProbeKind
    {
        Clean,
        Bad,
        End,
    }
    private ProbeKind ScanToken(int position, out int next, out bool suspiciousWord)
    {
        suspiciousWord = false;
        next = position;
        ReadOnlySpan<byte> span = _data.Span;
        int i = position;
        while (i < span.Length && IsWhitespace(span[i]))
        {
            i += 1;
        }

        while (i < span.Length && span[i] == (byte)'%')
        {
            while (i < span.Length && span[i] != (byte)'\n' && span[i] != (byte)'\r')
            {
                i += 1;
            }

            while (i < span.Length && IsWhitespace(span[i]))
            {
                i += 1;
            }
        }

        if (i >= span.Length)
        {
            next = i;
            return ProbeKind.End;
        }

        byte b = span[i];
        if (b == (byte)'[' || b == (byte)']')
        {
            next = i + 1;
            return ProbeKind.Clean;
        }

        if (b == (byte)')' || (b == (byte)'>' && (i + 1 >= span.Length || span[i + 1] != (byte)'>')))
        {
            next = i + 1;
            return ProbeKind.Bad;
        }

        if (b == (byte)'<' )
        {
            if (i + 1 < span.Length && span[i + 1] == (byte)'<')
            {
                next = i + 2;
                return ProbeKind.Clean;
            }

            return ScanHex(i, out next);
        }

        if (b == (byte)'(')
        {
            return ScanLiteral(i, out next);
        }

        if (b == (byte)'/')
        {
            int j = i + 1;
            while (j < span.Length && !IsDelimiter(span[j]))
            {
                j += 1;
            }

            next = j;
            return ProbeKind.Clean;
        }

        return ScanWord(i, out next, out suspiciousWord);
    }

    private ProbeKind ScanHex(int position, out int next)
    {
        next = position;
        ReadOnlySpan<byte> span = _data.Span;
        int i = position + 1;
        while (i < span.Length)
        {
            byte b = span[i];
            if (b == (byte)'>')
            {
                next = i + 1;
                return ProbeKind.Clean;
            }

            if (!IsWhitespace(b) && !IsHexDigit(b))
            {
                next = i + 1;
                return ProbeKind.Bad;
            }

            i += 1;
        }

        next = i;
        return ProbeKind.Bad;
    }

    private ProbeKind ScanLiteral(int position, out int next)
    {
        next = position;
        ReadOnlySpan<byte> span = _data.Span;
        int depth = 0;
        int i = position;
        while (i < span.Length)
        {
            if (span[i] == (byte)'\\')
            {
                i += 2;
                continue;
            }

            if (span[i] == (byte)'(')
            {
                depth += 1;
            }
            else if (span[i] == (byte)')')
            {
                depth -= 1;
                if (depth == 0)
                {
                    next = i + 1;
                    return ProbeKind.Clean;
                }
            }

            i += 1;
        }

        next = i;
        return ProbeKind.Bad;
    }

    private ProbeKind ScanWord(int position, out int next, out bool suspiciousWord)
    {
        suspiciousWord = false;
        next = position;
        ReadOnlySpan<byte> span = _data.Span;
        int i = position;
        while (i < span.Length && !IsDelimiter(span[i]))
        {
            i += 1;
        }

        next = i;
        if (i == position)
        {
            return ProbeKind.Bad;
        }

        bool alpha = false;
        bool other = false;
        for (int j = position; j < i; j++)
        {
            byte b = span[j];
            if ((b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z') || b == (byte)'*')
            {
                alpha = true;
            }
            else if ((sbyte)b < 32 && !IsLookaheadSpace(b))
            {
                return ProbeKind.Bad;
            }
            else
            {
                other = true;
            }
        }

        suspiciousWord = alpha && other;
        return ProbeKind.Clean;
    }
    private bool ReadLiteralString(long offset, out ContentToken token)
    {
        token = default;
        ReadOnlySpan<byte> span = _data.Span;
        int depth = 0;
        int i = _position;
        while (i < span.Length)
        {
            if (span[i] == (byte)'\\')
            {
                i += 2;
                continue;
            }

            if (span[i] == (byte)'(')
            {
                depth += 1;
            }
            else if (span[i] == (byte)')')
            {
                depth -= 1;
                if (depth == 0)
                {
                    int length = (i + 1) - _position;
                    token = new ContentToken(ContentTokenKind.LiteralString, _data.Slice(_position, length), offset);
                    _position = i + 1;
                    return true;
                }
            }

            i += 1;
        }

        throw new QpdfParseException("Unterminated string in content stream.", offset, null);
    }

    private bool ReadHexString(long offset, out ContentToken token)
    {
        token = default;
        ReadOnlySpan<byte> span = _data.Span;
        int i = _position + 1;
        while (i < span.Length)
        {
            byte b = span[i];
            if (b == (byte)'>')
            {
                int length = (i + 1) - _position;
                token = new ContentToken(ContentTokenKind.HexString, _data.Slice(_position, length), offset);
                _position = i + 1;
                return true;
            }

            if (!IsWhitespace(b) && !IsHexDigit(b))
            {
                throw new QpdfParseException("Invalid character in hexadecimal string.", offset, null);
            }

            i += 1;
        }

        throw new QpdfParseException("Unterminated hexadecimal string in content stream.", offset, null);
    }

    private bool ReadName(long offset, out ContentToken token)
    {
        token = default;
        ReadOnlySpan<byte> span = _data.Span;
        int start = _position + 1;
        int i = start;
        while (i < span.Length && !IsDelimiter(span[i]))
        {
            i += 1;
        }

        token = new ContentToken(ContentTokenKind.Name, _data.Slice(start, i - start), offset);
        _position = i;
        return true;
    }
    private bool ReadNumber(long offset, out ContentToken token)
    {
        token = default;
        ReadOnlySpan<byte> span = _data.Span;
        int i = _position;
        if (i < span.Length && (span[i] == (byte)'+' || span[i] == (byte)'-'))
        {
            i += 1;
        }

        int integerDigits = 0;
        while (i < span.Length && span[i] >= (byte)'0' && span[i] <= (byte)'9')
        {
            integerDigits += 1;
            i += 1;
        }

        bool real = false;
        if (i < span.Length && span[i] == (byte)'.')
        {
            real = true;
            i += 1;
            int fractionDigits = 0;
            while (i < span.Length && span[i] >= (byte)'0' && span[i] <= (byte)'9')
            {
                fractionDigits += 1;
                i += 1;
            }

            if (integerDigits == 0 && fractionDigits == 0)
            {
                throw new QpdfParseException("Malformed number in content stream.", offset, null);
            }
        }
        else if (integerDigits == 0)
        {
            throw new QpdfParseException("Malformed number in content stream.", offset, null);
        }

        ContentTokenKind kind = real ? ContentTokenKind.Real : ContentTokenKind.Integer;
        token = new ContentToken(kind, _data.Slice(_position, i - _position), offset);
        _position = i;
        return true;
    }

    private bool ReadWord(long offset, out ContentToken token)
    {
        token = default;
        ReadOnlySpan<byte> span = _data.Span;
        int i = _position;
        while (i < span.Length && !IsDelimiter(span[i]))
        {
            i += 1;
        }

        ReadOnlyMemory<byte> raw = _data.Slice(_position, i - _position);
        ContentTokenKind kind = ContentTokenKind.Operator;
        if (raw.Length == 4 && raw.Span[0] == (byte)'t' && raw.Span[1] == (byte)'r'
            && raw.Span[2] == (byte)'u' && raw.Span[3] == (byte)'e')
        {
            kind = ContentTokenKind.Boolean;
        }
        else if (raw.Length == 5 && raw.Span[0] == (byte)'f' && raw.Span[1] == (byte)'a'
            && raw.Span[2] == (byte)'l' && raw.Span[3] == (byte)'s' && raw.Span[4] == (byte)'e')
        {
            kind = ContentTokenKind.Boolean;
        }
        else if (raw.Length == 4 && raw.Span[0] == (byte)'n' && raw.Span[1] == (byte)'u'
            && raw.Span[2] == (byte)'l' && raw.Span[3] == (byte)'l')
        {
            kind = ContentTokenKind.Null;
        }

        token = new ContentToken(kind, raw, offset);
        _position = i;
        return true;
    }

    private void SkipWhitespaceAndComments()
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

    private static bool IsLookaheadSpace(byte b)
    {
        return b == 0x20 || b == 0x0A || b == 0x0D || b == 0x09 || b == 0x0C || b == 0x0B;
    }

    private static bool IsImageDelimiter(byte b)
    {
        return b == (byte)'(' || b == (byte)')' || b == (byte)'<' || b == (byte)'>'
            || b == (byte)'[' || b == (byte)']' || b == (byte)'/' || b == (byte)'%'
            || b == (byte)'{' || b == (byte)'}' || IsWhitespace(b) || b == 0x0B;
    }

    private static bool IsDelimiter(byte b)
    {
        return b == (byte)'(' || b == (byte)')' || b == (byte)'<' || b == (byte)'>'
            || b == (byte)'[' || b == (byte)']' || b == (byte)'/' || b == (byte)'%'
            || IsWhitespace(b);
    }

    private static bool IsNumberStart(byte b)
    {
        return (b >= (byte)'0' && b <= (byte)'9') || b == (byte)'+' || b == (byte)'-' || b == (byte)'.';
    }

    private static bool IsHexDigit(byte b)
    {
        return (b >= (byte)'0' && b <= (byte)'9') || (b >= (byte)'a' && b <= (byte)'f')
            || (b >= (byte)'A' && b <= (byte)'F');
    }
}