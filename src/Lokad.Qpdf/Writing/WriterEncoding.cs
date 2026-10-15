using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns an exactly sized PDF token or ASCII copy through its sink await.</summary>
internal sealed class WriterEncoding : IDisposable
{
    private enum Kind { Ascii, Name, Literal, Hex }
    private const int VisitBytes = 8192;
    // Fixed PDF syntax has process lifetime and is never exposed for mutation.
    // Borrow it through sink awaits instead of allocating a scope and buffer per token.
    private static readonly Dictionary<string, ReadOnlyMemory<byte>> AsciiLiterals =
        new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
        {
            [" "] = " "u8.ToArray(), ["\n"] = "\n"u8.ToArray(),
            ["["] = "["u8.ToArray(), ["]"] = "]"u8.ToArray(), ["/"] = "/"u8.ToArray(),
            ["<<"] = "<<"u8.ToArray(), [">>"] = ">>"u8.ToArray(),
            ["null"] = "null"u8.ToArray(), ["true"] = "true"u8.ToArray(), ["false"] = "false"u8.ToArray(),
            ["\nendobj\n"] = "\nendobj\n"u8.ToArray(), ["\nendobj\n\n"] = "\nendobj\n\n"u8.ToArray(),
            ["\nstream\n"] = "\nstream\n"u8.ToArray(), [">>\nstream\n"] = ">>\nstream\n"u8.ToArray(),
            ["endstream"] = "endstream"u8.ToArray(), ["\nendstream"] = "\nendstream"u8.ToArray(),
            ["\nendstream\nendobj\n"] = "\nendstream\nendobj\n"u8.ToArray(),
            ["0000000000 65535 f \n"] = "0000000000 65535 f \n"u8.ToArray(),
            ["0"] = "0"u8.ToArray(), ["1"] = "1"u8.ToArray(), ["2"] = "2"u8.ToArray(),
            ["3"] = "3"u8.ToArray(), ["4"] = "4"u8.ToArray(),
        };
    private static readonly Dictionary<PdfName, ReadOnlyMemory<byte>> NameLiterals =
        new Dictionary<PdfName, ReadOnlyMemory<byte>>
        {
            [PdfName.FromAscii("Type")] = "/Type"u8.ToArray(),
            [PdfName.FromAscii("Page")] = "/Page"u8.ToArray(),
            [PdfName.FromAscii("Pages")] = "/Pages"u8.ToArray(),
            [PdfName.FromAscii("Catalog")] = "/Catalog"u8.ToArray(),
            [PdfName.FromAscii("Count")] = "/Count"u8.ToArray(),
            [PdfName.FromAscii("Kids")] = "/Kids"u8.ToArray(),
            [PdfName.FromAscii("Parent")] = "/Parent"u8.ToArray(),
            [PdfName.FromAscii("MediaBox")] = "/MediaBox"u8.ToArray(),
            [PdfName.FromAscii("Contents")] = "/Contents"u8.ToArray(),
            [PdfName.FromAscii("Resources")] = "/Resources"u8.ToArray(),
            [PdfName.FromAscii("Length")] = "/Length"u8.ToArray(),
            [PdfName.FromAscii("Font")] = "/Font"u8.ToArray(),
            [PdfName.FromAscii("F1")] = "/F1"u8.ToArray(),
            [PdfName.FromAscii("Subtype")] = "/Subtype"u8.ToArray(),
            [PdfName.FromAscii("Type1")] = "/Type1"u8.ToArray(),
            [PdfName.FromAscii("BaseFont")] = "/BaseFont"u8.ToArray(),
            [PdfName.FromAscii("Helvetica")] = "/Helvetica"u8.ToArray(),
            [PdfName.FromAscii("Annots")] = "/Annots"u8.ToArray(),
            [PdfName.FromAscii("AcroForm")] = "/AcroForm"u8.ToArray(),
            [PdfName.FromAscii("Fields")] = "/Fields"u8.ToArray(),
            [PdfName.FromAscii("FT")] = "/FT"u8.ToArray(),
            [PdfName.FromAscii("T")] = "/T"u8.ToArray(),
            [PdfName.FromAscii("Rect")] = "/Rect"u8.ToArray(),
            [PdfName.FromAscii("AP")] = "/AP"u8.ToArray(),
            [PdfName.FromAscii("N")] = "/N"u8.ToArray(),
        };
    private ParserMemoryScope? _memory;
    private byte[]? _bytes;

    private WriterEncoding(ParserMemoryScope memory, byte[] bytes)
    {
        _memory = memory;
        _bytes = bytes;
    }

    internal ReadOnlyMemory<byte> Data => _bytes ?? throw new ObjectDisposedException(nameof(WriterEncoding));

    internal static bool TryGetSharedAscii(string text, QpdfLimits limits, ResourceGovernor governor,
        CancellationToken cancellationToken, out ReadOnlyMemory<byte> bytes)
    {
        // Bound lookup work before falling back to the cancellable encoder.
        if (text.Length > 32) { bytes = default; return false; }
        if (!AsciiLiterals.TryGetValue(text, out bytes)) { return false; }
        CheckCancellation(governor, cancellationToken);
        CheckLength(bytes.Length, limits.MaxOutputBytes);
        governor.ChargeWork(bytes.Length);
        return true;
    }

    internal static bool TryGetSharedName(PdfName name, QpdfLimits limits, ResourceGovernor governor,
        CancellationToken cancellationToken, out ReadOnlyMemory<byte> bytes)
    {
        if (name.RawSpan.Length > 9) { bytes = default; return false; }
        if (!NameLiterals.TryGetValue(name, out bytes)) { return false; }
        CheckCancellation(governor, cancellationToken);
        CheckLength(bytes.Length, limits.MaxOutputBytes);
        governor.ChargeWork(bytes.Length);
        return true;
    }

    internal static WriterEncoding Ascii(string text, QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Create(Kind.Ascii, ReadOnlySpan<byte>.Empty, text.AsSpan(), limits, governor, cancellationToken, null);
    }

    internal static bool TryGetUnescapedName(PdfName name, QpdfLimits limits, ResourceGovernor governor,
        CancellationToken cancellationToken, out ReadOnlyMemory<byte> bytes)
    {
        CheckCancellation(governor, cancellationToken);
        ReadOnlySpan<byte> raw = name.RawSpan;
        if (!raw.IsEmpty && !IsRegular(raw[0]))
        {
            governor.ChargeWork(1L); bytes = default; return false;
        }
        // These bytes have immutable value lifetime and may survive a host's
        // retained chunk. Escaped names keep the independently encoded path.
        for (int offset = 0; offset < raw.Length;)
        {
            CheckCancellation(governor, cancellationToken);
            int count = Math.Min(VisitBytes, raw.Length - offset);
            governor.ChargeWork(count);
            foreach (byte value in raw.Slice(offset, count))
            {
                if (!IsRegular(value)) { bytes = default; return false; }
            }
            offset += count;
        }
        CheckLength(1L + raw.Length, Math.Min(limits.MaxOutputBytes, Array.MaxLength));
        governor.ChargeWork(raw.Length);
        bytes = name.RawMemory;
        return true;
    }

    internal static WriterEncoding Name(PdfName name, QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Create(Kind.Name, name.RawSpan, ReadOnlySpan<char>.Empty, limits, governor, cancellationToken, null);
    }

    internal static WriterEncoding String(PdfByteString text, QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Create(text.Hex ? Kind.Hex : Kind.Literal, text.ValueSpan, ReadOnlySpan<char>.Empty, limits, governor, cancellationToken, null);
    }

    // Completed-block observation keeps cancellation controls independent of
    // scheduler races. Ordinary callers retain no callback or observer state.
    internal static WriterEncoding Ascii(string text, QpdfLimits limits, ResourceGovernor governor,
        CancellationToken cancellationToken, Action observe)
    {
        ArgumentNullException.ThrowIfNull(text); ArgumentNullException.ThrowIfNull(observe);
        return Create(Kind.Ascii, ReadOnlySpan<byte>.Empty, text.AsSpan(), limits, governor, cancellationToken, observe);
    }

    internal static WriterEncoding Name(PdfName name, QpdfLimits limits, ResourceGovernor governor,
        CancellationToken cancellationToken, Action observe)
    {
        ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(observe);
        return Create(Kind.Name, name.RawSpan, ReadOnlySpan<char>.Empty, limits, governor, cancellationToken, observe);
    }

    internal static WriterEncoding String(PdfByteString text, QpdfLimits limits, ResourceGovernor governor,
        CancellationToken cancellationToken, Action observe)
    {
        ArgumentNullException.ThrowIfNull(text); ArgumentNullException.ThrowIfNull(observe);
        return Create(text.Hex ? Kind.Hex : Kind.Literal, text.ValueSpan, ReadOnlySpan<char>.Empty, limits, governor, cancellationToken, observe);
    }

    internal static long MeasureAscii(string text, QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Measure(Kind.Ascii, ReadOnlySpan<byte>.Empty, text.AsSpan(), limits, governor, token, null);
    }

    internal static long MeasureName(PdfName name, QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Measure(Kind.Name, name.RawSpan, ReadOnlySpan<char>.Empty, limits, governor, token, null);
    }

    internal static long MeasureString(PdfByteString text, QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Measure(text.Hex ? Kind.Hex : Kind.Literal, text.ValueSpan, ReadOnlySpan<char>.Empty, limits, governor, token, null);
    }

    internal static void FillAscii(string text, Span<byte> target, ResourceGovernor governor, CancellationToken token)
        => Fill(Kind.Ascii, ReadOnlySpan<byte>.Empty, text.AsSpan(), target, governor, token, null);

    internal static void FillName(PdfName name, Span<byte> target, ResourceGovernor governor, CancellationToken token)
        => Fill(Kind.Name, name.RawSpan, ReadOnlySpan<char>.Empty, target, governor, token, null);

    internal static void FillString(PdfByteString text, Span<byte> target, ResourceGovernor governor, CancellationToken token)
        => Fill(text.Hex ? Kind.Hex : Kind.Literal, text.ValueSpan, ReadOnlySpan<char>.Empty, target, governor, token, null);

    private static long Measure(Kind kind, ReadOnlySpan<byte> input, ReadOnlySpan<char> text,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken, Action? observe)
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
                observe?.Invoke();
            }
        }
        return length;
    }

    private static WriterEncoding Create(Kind kind, ReadOnlySpan<byte> input, ReadOnlySpan<char> text,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken, Action? observe)
    {
        long length = Measure(kind, input, text, limits, governor, cancellationToken, observe);
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L + (length == 0L ? 0L : 32L + length));
            governor.ChargeWork(length);
            CheckCancellation(governor, cancellationToken);
            // Fill writes every byte before the token can reach a sink.
            byte[] bytes = length == 0L ? Array.Empty<byte>() : GC.AllocateUninitializedArray<byte>((int)length);
            Fill(kind, input, text, bytes, governor, cancellationToken, observe);
            return new WriterEncoding(memory, bytes);
        }
        catch { memory.Dispose(); throw; }
    }

    private static void Fill(Kind kind, ReadOnlySpan<byte> input, ReadOnlySpan<char> text, Span<byte> target,
        ResourceGovernor governor, CancellationToken cancellationToken, Action? observe)
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
            observe?.Invoke();
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
