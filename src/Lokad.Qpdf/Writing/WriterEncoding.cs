using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns an exactly sized PDF token or ASCII copy through its sink await.</summary>
internal sealed class WriterEncoding : IDisposable
{
    private enum Kind { Ascii, Name, Literal, Hex }
    private const int VisitBytes = 8192;
    private ParserMemoryScope? _memory;
    private byte[]? _bytes;

    private WriterEncoding(ParserMemoryScope memory, byte[] bytes)
    {
        _memory = memory;
        _bytes = bytes;
    }

    internal ReadOnlyMemory<byte> Data => _bytes ?? throw new ObjectDisposedException(nameof(WriterEncoding));

    internal static WriterEncoding Ascii(string text, QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Create(Kind.Ascii, ReadOnlySpan<byte>.Empty, text.AsSpan(), limits, governor, cancellationToken);
    }

    internal static WriterEncoding Name(PdfName name, QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Create(Kind.Name, name.RawSpan, ReadOnlySpan<char>.Empty, limits, governor, cancellationToken);
    }

    internal static WriterEncoding String(PdfByteString text, QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Create(text.Hex ? Kind.Hex : Kind.Literal, text.ValueSpan, ReadOnlySpan<char>.Empty, limits, governor, cancellationToken);
    }

    private static WriterEncoding Create(Kind kind, ReadOnlySpan<byte> input, ReadOnlySpan<char> text,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(governor);
        CheckCancellation(governor, cancellationToken);
        long length = kind switch
        {
            Kind.Ascii => text.Length,
            Kind.Hex => 2L + 2L * input.Length,
            Kind.Name => 1L,
            _ => 2L,
        };
        long maximum = Math.Min(limits.MaxOutputBytes, Array.MaxLength);
        CheckLength(length, maximum);
        if (kind is Kind.Name or Kind.Literal)
        {
            for (int offset = 0; offset < input.Length;)
            {
                CheckCancellation(governor, cancellationToken);
                int count = Math.Min(VisitBytes, input.Length - offset);
                governor.ChargeWork(count);
                for (int index = offset; index < offset + count; index++)
                {
                    length += kind == Kind.Name ? NameWidth(input[index]) : LiteralWidth(input[index]);
                    CheckLength(length, maximum);
                }
                offset += count;
            }
        }
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L + (length == 0L ? 0L : 32L + length));
            governor.ChargeWork(length);
            CheckCancellation(governor, cancellationToken);
            byte[] bytes = length == 0L ? Array.Empty<byte>() : new byte[(int)length];
            Fill(kind, input, text, bytes, governor, cancellationToken);
            return new WriterEncoding(memory, bytes);
        }
        catch { memory.Dispose(); throw; }
    }

    private static void Fill(Kind kind, ReadOnlySpan<byte> input, ReadOnlySpan<char> text, Span<byte> target,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        int used = 0;
        if (kind != Kind.Ascii)
        {
            governor.ChargeWork(kind == Kind.Name ? 1L : 2L);
            target[used++] = kind == Kind.Name ? (byte)'/' : kind == Kind.Hex ? (byte)'<' : (byte)'(';
            if (kind != Kind.Name) { target[target.Length - 1] = kind == Kind.Hex ? (byte)'>' : (byte)')'; }
        }
        int size = kind == Kind.Ascii ? text.Length : input.Length;
        for (int offset = 0; offset < size;)
        {
            CheckCancellation(governor, cancellationToken);
            int count = Math.Min(VisitBytes, size - offset);
            governor.ChargeWork(count);
            int outputBytes = kind switch
            {
                Kind.Ascii => count,
                Kind.Hex => 2 * count,
                _ => 0,
            };
            if (kind is Kind.Name or Kind.Literal)
            {
                for (int index = offset; index < offset + count; index++)
                {
                    outputBytes += kind == Kind.Name ? NameWidth(input[index]) : LiteralWidth(input[index]);
                }
                // Admit encoding visits separately from the preceding width visits.
                governor.ChargeWork(count);
            }
            governor.ChargeWork(outputBytes);
            for (int index = offset; index < offset + count; index++)
            {
                if (kind == Kind.Ascii)
                {
                    // ASCIIEncoding's default replacement is one '?' per UTF-16
                    // unit, including both halves of a surrogate pair.
                    target[used++] = text[index] <= 127 ? (byte)text[index] : (byte)'?';
                }
                else if (kind == Kind.Name) { used += WriteName(input[index], target.Slice(used)); }
                else if (kind == Kind.Literal) { used += WriteLiteral(input[index], target.Slice(used)); }
                else
                {
                    target[used++] = Hex(input[index] >> 4);
                    target[used++] = Hex(input[index] & 15);
                }
            }
            offset += count;
        }
    }

    private static int NameWidth(byte value) => value == 0 || IsRegular(value) ? 1 : 3;
    private static bool IsRegular(byte value) => value is >= 33 and <= 126
        && value != '#' && value != '(' && value != ')' && value != '<' && value != '>'
        && value != '[' && value != ']' && value != '{' && value != '}' && value != '/' && value != '%';

    private static int WriteName(byte value, Span<byte> target)
    {
        if (value == 0) { target[0] = (byte)'#'; return 1; }
        if (IsRegular(value)) { target[0] = value; return 1; }
        target[0] = (byte)'#'; target[1] = Hex(value >> 4); target[2] = Hex(value & 15); return 3;
    }

    private static int LiteralWidth(byte value) => value is 10 or 13 or 9 or 8 or 12 or 40 or 41 or 92 ? 2
        : value is < 32 or > 126 ? 4 : 1;

    private static int WriteLiteral(byte value, Span<byte> target)
    {
        if (LiteralWidth(value) == 1) { target[0] = value; return 1; }
        target[0] = (byte)'\\';
        if (LiteralWidth(value) == 2)
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

    private static byte Hex(int value) => (byte)"0123456789ABCDEF"[value];
    private static void CheckLength(long length, long maximum)
    {
        if (length > maximum) { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), length, maximum); }
    }
    private static void CheckCancellation(ResourceGovernor governor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        governor.CheckCancellation();
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _bytes = null;
        _memory = null;
        memory?.Dispose();
    }
}
