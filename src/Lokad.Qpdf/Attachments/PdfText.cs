using System.Text;

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

    /// <summary>
    /// Encodes text into PDFDocEncoding bytes when every character round-trips.
    /// Returns false for characters outside PDFDocEncoding (including surrogates
    /// and the replacement character); callers fall back to UTF-16.
    /// </summary>
    internal static bool TryEncodePdfDoc(string value, out byte[] bytes)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        byte[] encoded = new byte[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            if (!EncodeMap.TryGetValue(value[i], out byte unit))
            {
                bytes = [];
                return false;
            }

            encoded[i] = unit;
        }

        bytes = encoded;
        return true;
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
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (value.Length >= 2 && ((value[0] == 0xFE && value[1] == 0xFF) || (value[0] == 0xFF && value[1] == 0xFE)))
        {
            return DecodeUtf16(value);
        }

        StringBuilder output = new(value.Length);
        foreach (byte b in value)
        {
            int unit = b;
            if (b >= 24 && b <= 31)
            {
                unit = LowTable[b - 24];
            }
            else if (b >= 127 && b <= 160)
            {
                unit = HighTable[b - 127];
            }
            else if (b == 173)
            {
                unit = 0xFFFD;
            }

            output.Append((char)unit);
        }

        return output.ToString();
    }

    private static string DecodeUtf16(byte[] value)
    {
        bool little = value[0] == 0xFF;
        StringBuilder output = new(value.Length / 2);
        int i = 2;
        while (i + 1 < value.Length)
        {
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
                        output.Append(char.ConvertFromUtf32(0x10000 + ((unit & 0x3FF) << 10) + (next & 0x3FF)));
                        continue;
                    }
                }

                continue;
            }

            if ((unit & 0xFC00) == 0xDC00)
            {
                output.Append((char)(unit & 0x3FF));
                continue;
            }

            output.Append((char)unit);
        }

        return output.ToString();
    }
}
