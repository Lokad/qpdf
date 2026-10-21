using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Attachments;

/// <summary>
/// PDF text-string decoding for attachment keys and file names: UTF-16 with
/// a byte-order mark, otherwise PDFDocEncoding. Mirrors the reference
/// mapping (low range, high range with undefined slots, lonely 0xAD).
/// </summary>
internal static class PdfText
{
    private static readonly int[] LowTable = [0x02D8, 0x02C7, 0x02C6, 0x02D9, 0x02DD, 0x02DB, 0x02DA, 0x02DC];

    private static readonly int[] HighTable =
    [
        0xFFFD, 0x2022, 0x2020, 0x2021, 0x2026, 0x2014, 0x2013, 0x0192,
        0x2044, 0x2039, 0x203A, 0x2212, 0x2030, 0x201E, 0x201C, 0x201D,
        0x2018, 0x2019, 0x201A, 0x2122, 0xFB01, 0xFB02, 0x0141, 0x0152,
        0x0160, 0x0178, 0x017D, 0x0131, 0x0142, 0x0153, 0x0161, 0x017E,
        0xFFFD, 0x20AC,
    ];

    private static readonly Dictionary<char, byte> EncodeMap = BuildEncodeMap();

    internal static bool TryEncodePdfDocByte(char value, out byte encoded)
        => EncodeMap.TryGetValue(value, out encoded);

    // Allocation belongs to the caller: JSON import prepays its model, while
    // graph edits admit the final array through their parser scope.
    internal static int GetEncodedLength(ReadOnlySpan<char> value, ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        for (int i = 0; i < value.Length; i++)
        {
            if ((i & 255) == 0) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); }
            if (!TryEncodePdfDocByte(value[i], out _)) { return checked(2 + value.Length * 2); }
        }
        return value.Length;
    }

    internal static void Encode(ReadOnlySpan<char> value, Span<byte> target, ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        bool pdfDoc = target.Length == value.Length;
        if (!pdfDoc)
        {
            if (target.Length != checked(2 + value.Length * 2)) { throw new ArgumentException("Incorrect text buffer length.", nameof(target)); }
            target[0] = 0xfe; target[1] = 0xff;
        }
        for (int i = 0; i < value.Length; i++)
        {
            if ((i & 255) == 0) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); }
            if (pdfDoc)
            {
                if (!TryEncodePdfDocByte(value[i], out target[i])) { throw new ArgumentException("Text requires UTF-16 encoding.", nameof(target)); }
            }
            else { target[2 + 2 * i] = (byte)(value[i] >> 8); target[3 + 2 * i] = (byte)value[i]; }
        }
    }

    private static Dictionary<char, byte> BuildEncodeMap()
    {
        Dictionary<char, byte> map = new();
        for (int unit = 0; unit <= 255; unit++)
        {
            byte b = (byte)unit;
            string decoded = Decode([b]);
            if (decoded.Length == 1 && decoded[0] != '\uFFFD')
            {
                map.TryAdd(decoded[0], b);
            }
        }

        return map;
    }

    internal static string Decode(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return DecodeCore(value, null, null, CancellationToken.None);
    }

    internal static string Decode(ReadOnlyMemory<byte> value, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken token)
    {
        memory.CheckOwner(governor);
        return DecodeCore(value, memory, governor, token);
    }

    private static string DecodeCore(ReadOnlyMemory<byte> value, ParserMemoryScope? memory,
        ResourceGovernor? governor, CancellationToken token)
    {
        int length = CopyDecoded(value.Span, Span<char>.Empty, governor, token);
        if (length == 0) { return string.Empty; }
        long bytes = GovernedText.StringBytes(length); memory?.Reserve(bytes);
        try
        {
            token.ThrowIfCancellationRequested(); governor?.CheckCancellation();
            return string.Create(length, (value, governor, token), static (target, state) =>
                CopyDecoded(state.value.Span, target, state.governor, state.token));
        }
        catch { memory?.Release(bytes); throw; }
    }

    internal static char DecodePdfDocByte(byte value)
        => (char)(value is >= 24 and <= 31 ? LowTable[value - 24]
            : value is >= 127 and <= 160 ? HighTable[value - 127]
            : value == 173 ? 0xFFFD : value);

    private static int CopyDecoded(ReadOnlySpan<byte> value, Span<char> target,
        ResourceGovernor? governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor?.CheckCancellation();
        bool utf16 = value.Length >= 2 && ((value[0] == 0xFE && value[1] == 0xFF) || (value[0] == 0xFF && value[1] == 0xFE));
        int length = 0;
        if (!utf16)
        {
            for (int at = 0; at < value.Length; at++)
            {
                token.ThrowIfCancellationRequested(); governor?.CheckCancellation();
                if (!target.IsEmpty) { target[length] = DecodePdfDocByte(value[at]); }
                length++;
            }
            return length;
        }
        bool little = value[0] == 0xFF;
        int i = 2;
        while (i + 1 < value.Length)
        {
            token.ThrowIfCancellationRequested(); governor?.CheckCancellation();
            int unit = little ? (value[i] | (value[i + 1] << 8)) : ((value[i] << 8) | value[i + 1]);
            i += 2;
            if ((unit & 0xFC00) == 0xD800)
            {
                if (i + 1 < value.Length)
                {
                    int next = little ? (value[i] | (value[i + 1] << 8)) : ((value[i] << 8) | value[i + 1]);
                    if ((next & 0xFC00) == 0xDC00)
                    {
                        i += 2;
                        if (!target.IsEmpty) { target[length] = (char)unit; target[length + 1] = (char)next; }
                        length += 2;
                        continue;
                    }
                }

                continue;
            }

            if ((unit & 0xFC00) == 0xDC00)
            {
                if (!target.IsEmpty) { target[length] = (char)(unit & 0x3FF); }
                length++;
                continue;
            }

            if (!target.IsEmpty) { target[length] = (char)unit; }
            length++;
        }

        return length;
    }
}
