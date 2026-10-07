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
    Comment,
    Whitespace,
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
    private readonly CancellationToken _token;
    private int _position;
    private int _tokenByteCredit;

    internal ContentTokenizer(ReadOnlyMemory<byte> data, QpdfLimits limits, ResourceGovernor governor)
        : this(data, limits, governor, governor?.CancellationToken ?? CancellationToken.None)
    {
    }

    internal ContentTokenizer(ReadOnlyMemory<byte> data, QpdfLimits limits, ResourceGovernor governor,
        CancellationToken token)
    {
        _data = data;
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        _token = token;
        limits.Validate();
    }

    private void Check() { _token.ThrowIfCancellationRequested(); _governor.CheckCancellation(); }
    private void Visit() { Check(); _governor.ChargeWork(1L); }
    // Dispatch funds a bounded prefix, preserving the one-unit short-token
    // contract. Every later scan step pays work; every step checks cancellation.
    private void VisitTokenByte()
    {
        Check();
        if (_tokenByteCredit > 0) { _tokenByteCredit--; }
        else { _governor.ChargeWork(1L); }
    }

    internal bool IncludeComments { get; set; }

    internal bool IncludeWhitespace { get; set; }

    internal long Position => _position;

    internal bool TryReadToken(out ContentToken token)
    {
        Check();
        SkipWhitespaceAndComments();
        if (TakePendingComment(out token))
        {
            Visit();
            return true;
        }

        if (TakePendingWhitespace(out token))
        {
            Visit();
            return true;
        }

        if (_position >= _data.Length)
        {
            token = new ContentToken(ContentTokenKind.Operator, ReadOnlyMemory<byte>.Empty, _position);
            return false;
        }

        Visit();
        _tokenByteCredit = 16;
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
        Check();
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

        Visit();
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
            Visit();
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

    private int IndexOfEI(ReadOnlySpan<byte> span, int start)
    {
        for (int i = start; i + 1 < span.Length; i++)
        {
            Visit();
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
            Visit();
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
            Visit();
            i += 1;
        }

        while (i < span.Length && span[i] == (byte)'%')
        {
            Visit();
            while (i < span.Length && span[i] != (byte)'\n' && span[i] != (byte)'\r')
            {
                Visit();
                i += 1;
            }

            while (i < span.Length && IsWhitespace(span[i]))
            {
                Visit();
                i += 1;
            }
        }

        if (i >= span.Length)
        {
            next = i;
            return ProbeKind.End;
        }

        byte b = span[i];
        Visit();
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
                Visit();
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
            Visit();
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
            Visit();
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
            Visit();
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
            Visit();
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
            VisitTokenByte();
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
            VisitTokenByte();
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
            VisitTokenByte();
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
            VisitTokenByte();
            i += 1;
        }

        int integerDigits = 0;
        while (i < span.Length && span[i] >= (byte)'0' && span[i] <= (byte)'9')
        {
            VisitTokenByte();
            integerDigits += 1;
            i += 1;
        }

        bool real = false;
        if (i < span.Length && span[i] == (byte)'.')
        {
            VisitTokenByte();
            real = true;
            i += 1;
            int fractionDigits = 0;
            while (i < span.Length && span[i] >= (byte)'0' && span[i] <= (byte)'9')
            {
                VisitTokenByte();
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
            VisitTokenByte();
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
            Check();
            byte b = span[_position];
            if (IsWhitespace(b))
            {
                if (!IncludeWhitespace)
                {
                    Visit();
                    _position += 1;
                    continue;
                }

                long start = _position;
                while (_position < span.Length && IsWhitespace(span[_position]))
                {
                    Visit();
                    _position += 1;
                }

                _pendingWhitespace = new ContentToken(ContentTokenKind.Whitespace, _data.Slice((int)start, _position - (int)start), start);
                break;
            }

            if (b == (byte)'%')
            {
                if (!IncludeComments)
                {
                    while (_position < span.Length && span[_position] != (byte)'\n' && span[_position] != (byte)'\r')
                    {
                        Visit();
                        _position += 1;
                    }

                    continue;
                }

                long start = _position;
                while (_position < span.Length && span[_position] != (byte)'\n' && span[_position] != (byte)'\r')
                {
                    Visit();
                    _position += 1;
                }

                _pendingComment = new ContentToken(ContentTokenKind.Comment, _data.Slice((int)start, _position - (int)start), start);
                break;
            }

            break;
        }
    }

    private ContentToken? _pendingComment;

    private ContentToken? _pendingWhitespace;

    private bool TakePendingWhitespace(out ContentToken token)
    {
        if (_pendingWhitespace is ContentToken pending)
        {
            _pendingWhitespace = null;
            token = pending;
            return true;
        }

        token = default;
        return false;
    }

    private bool TakePendingComment(out ContentToken token)
    {
        if (_pendingComment is ContentToken pending)
        {
            _pendingComment = null;
            token = pending;
            return true;
        }

        token = default;
        return false;
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
