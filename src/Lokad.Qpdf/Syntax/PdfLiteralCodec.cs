using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Syntax;

/// <summary>PDF literal payload decoding and canonical token encoding; callers own framing and storage.</summary>
internal static class PdfLiteralCodec
{
    private const int EncodeBlock = 8192;

    internal static int DecodedLength(ReadOnlySpan<byte> source, long offset, ResourceGovernor governor, CancellationToken token)
        => DecodeCore(source, Span<byte>.Empty, offset, false, governor, token, null);

    internal static int DecodedLength(ReadOnlySpan<byte> source, long offset, ResourceGovernor governor, CancellationToken token, Action observe)
    {
        ArgumentNullException.ThrowIfNull(observe);
        return DecodeCore(source, Span<byte>.Empty, offset, false, governor, token, observe);
    }

    internal static void Decode(ReadOnlySpan<byte> source, Span<byte> target, long offset, ResourceGovernor governor, CancellationToken token)
    {
        int written = DecodeCore(source, target, offset, true, governor, token, null);
        if (written != target.Length) { throw new ArgumentException("Literal destination length differs.", nameof(target)); }
    }

    private static void CheckDecode(int position, ref long nextCheck, ResourceGovernor governor, CancellationToken token, Action? observe)
    {
        if (position < nextCheck) { return; }
        if (position != 0) { observe?.Invoke(); }
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        nextCheck = (long)position + 4096L;
    }

    private static int DecodeCore(ReadOnlySpan<byte> span, Span<byte> result, long offset, bool write, ResourceGovernor governor, CancellationToken token, Action? observe)
    {
        int outCount = 0;
        long nextCheck = 0L;
        for (int i = 0; i < span.Length; i++)
        {
            CheckDecode(i, ref nextCheck, governor, token, observe);
            if (span[i] != (byte)'\\')
            {
                // Raw CR and CRLF are a single PDF end-of-line marker. An
                // escaped \r below is a literal carriage return instead.
                if (write) { result[outCount] = span[i] == (byte)'\r' ? (byte)'\n' : span[i]; }
                outCount += 1;
                if (span[i] == (byte)'\r' && i + 1 < span.Length && span[i + 1] == (byte)'\n')
                {
                    i += 1;
                }
                continue;
            }

            i += 1;
            if (i >= span.Length)
            {
                throw new QpdfParseException("Trailing backslash in literal string.", offset, null);
            }

            byte b = span[i];
            if (b == (byte)'n')
            {
                if (write) { result[outCount] = (byte)'\n'; }
                outCount += 1;
            }
            else if (b == (byte)'r')
            {
                if (write) { result[outCount] = (byte)'\r'; }
                outCount += 1;
            }
            else if (b == (byte)'t')
            {
                if (write) { result[outCount] = (byte)'\t'; }
                outCount += 1;
            }
            else if (b == (byte)'b')
            {
                if (write) { result[outCount] = (byte)'\b'; }
                outCount += 1;
            }
            else if (b == (byte)'f')
            {
                if (write) { result[outCount] = (byte)'\f'; }
                outCount += 1;
            }
            else if (b == (byte)'(' || b == (byte)')' || b == (byte)'\\')
            {
                if (write) { result[outCount] = b; }
                outCount += 1;
            }
            else if (b == (byte)'\r')
            {
                if (i + 1 < span.Length && span[i + 1] == (byte)'\n')
                {
                    i += 1;
                }
            }
            else if (b == (byte)'\n')
            {
            }
            else if (b >= (byte)'0' && b <= (byte)'7')
            {
                int value = b - (byte)'0';
                for (int k = 0; k < 2; k++)
                {
                    if (i + 1 < span.Length && span[i + 1] >= (byte)'0' && span[i + 1] <= (byte)'7')
                    {
                        i += 1;
                        value = (value * 8) + (span[i] - (byte)'0');
                    }
                    else
                    {
                        break;
                    }
                }

                if (write) { result[outCount] = (byte)(value & 0xFF); }
                outCount += 1;
            }
            else
            {
                if (write) { result[outCount] = b; }
                outCount += 1;
            }
        }

        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        return outCount;
    }


    internal static long EncodedLength(ReadOnlySpan<byte> source, long maximum, string limitName,
        ResourceGovernor governor, CancellationToken token, Action? observe)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        long length = 2L;
        if (length > maximum) { throw new QpdfLimitException(limitName, length, maximum); }
        for (int offset = 0; offset < source.Length;)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.NoteOperation();
            int count = Math.Min(EncodeBlock, source.Length - offset);
            foreach (byte value in source.Slice(offset, count))
            {
                length += Width(value);
                if (length > maximum) { throw new QpdfLimitException(limitName, length, maximum); }
            }
            offset += count; observe?.Invoke();
        }
        return length;
    }

    internal static void Encode(ReadOnlySpan<byte> source, Span<byte> target,
        ResourceGovernor governor, CancellationToken token, Action? observe)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.NoteOperation();
        int used = 0; target[used++] = (byte)'(';
        for (int offset = 0; offset < source.Length;)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.NoteOperation();
            int count = Math.Min(EncodeBlock, source.Length - offset);
            foreach (byte value in source.Slice(offset, count)) { used += WriteByte(value, target[used..]); }
            offset += count; observe?.Invoke();
        }
        target[used++] = (byte)')';
        if (used != target.Length) { throw new ArgumentException("Literal destination length differs.", nameof(target)); }
    }

    private static int Width(byte value) => value is 10 or 13 or 9 or 8 or 12 or 40 or 41 or 92 ? 2
        : value is < 32 or > 126 ? 4 : 1;

    private static int WriteByte(byte value, Span<byte> target)
    {
        if (Width(value) == 1) { target[0] = value; return 1; }
        target[0] = (byte)'\\';
        if (Width(value) == 2)
        {
            target[1] = value switch
            {
                10 => (byte)'n', 13 => (byte)'r', 9 => (byte)'t', 8 => (byte)'b', 12 => (byte)'f', _ => value,
            };
            return 2;
        }
        target[1] = (byte)('0' + (value >> 6));
        target[2] = (byte)('0' + ((value >> 3) & 7));
        target[3] = (byte)('0' + (value & 7));
        return 4;
    }

}
