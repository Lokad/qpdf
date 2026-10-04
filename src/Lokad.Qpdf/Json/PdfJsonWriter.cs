using System.Globalization;
using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Json;
/// <summary>
/// PDF object to JSON value encoding for inspection. Version 1 writes names
/// normalized and strings as decoded Unicode; version 2 uses the canonical
/// unambiguous syntax. References stay references.
/// </summary>
internal static class PdfJsonWriter
{
    internal static void AppendJsonString(StringBuilder builder, string text)
    {
        builder.Append((char)34);
        foreach (char c in text)
        {
            if ((c > 34 && c != 92) || c == 32 || c == 33)
            {
                builder.Append(c);
                continue;
            }

            switch (c)
            {
                case (char)92: builder.Append("\\\\"); break;
                case (char)34: builder.Append("\\\""); break;
                case (char)8: builder.Append("\\b"); break;
                case (char)12: builder.Append("\\f"); break;
                case (char)10: builder.Append("\\n"); break;
                case (char)13: builder.Append("\\r"); break;
                case (char)9: builder.Append("\\t"); break;
                default:
                    builder.Append(c < 16 ? "\\u000" : "\\u001");
                    builder.Append("0123456789abcdef"[(int)c % 16]);
                    break;
            }
        }

        builder.Append((char)34);
    }
    internal static string NormalizeName(byte[] slashForm)
    {
        StringBuilder builder = new(slashForm.Length);
        bool firstByte = true;
        foreach (byte b in slashForm)
        {
            if (firstByte)
            {
                firstByte = false;
                builder.Append((char)b);
                continue;
            }

            bool delimiter = b == 35 || b == 47 || b == 40 || b == 41
                || b == 60 || b == 62 || b == 91 || b == 93
                || b == 123 || b == 125 || b == 37;
            if (b < 33 || b > 126 || delimiter)
            {
                builder.Append((char)35);
                builder.Append("0123456789abcdef"[b >> 4]);
                builder.Append("0123456789abcdef"[b & 15]);
            }
            else
            {
                builder.Append((char)b);
            }
        }

        return builder.ToString();
    }

    internal static byte[] ResolveNameBytes(byte[] source)
    {
        List<byte> resolved = new(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            if (source[i] == 35 && i + 2 < source.Length && IsHex(source[i + 1]) && IsHex(source[i + 2]))
            {
                resolved.Add((byte)((HexValue(source[i + 1]) << 4) | HexValue(source[i + 2])));
                i += 3;
            }
            else
            {
                resolved.Add(source[i]);
                i += 1;
            }
        }

        return [.. resolved];
    }

    private static bool IsHex(byte b)
    {
        return (b >= 48 && b <= 57) || (b >= 65 && b <= 70) || (b >= 97 && b <= 102);
    }

    private static int HexValue(byte b)
    {
        if (b >= 48 && b <= 57)
        {
            return b - 48;
        }

        if (b >= 65 && b <= 70)
        {
            return b - 55;
        }

        return b - 87;
    }

    internal static byte[] SlashForm(PdfName name)
    {
        byte[] raw = name.RawBytes;
        byte[] slash = new byte[raw.Length + 1];
        slash[0] = 47;
        Buffer.BlockCopy(raw, 0, slash, 1, raw.Length);
        return slash;
    }
    internal static (bool Valid, bool Plain) AnalyzeName(byte[] slashForm)
    {
        int tail = 0;
        bool tail2 = false;
        bool tail3 = false;
        bool needsEscaping = false;
        foreach (byte raw in slashForm)
        {
            int c = raw;
            if (tail > 0)
            {
                if ((c & 0xC0) != 0x80) { return (false, false); }
                if (tail2)
                {
                    if ((c & 0xE0) == 0x80) { return (false, false); }
                    tail2 = false;
                }
                else if (tail3)
                {
                    if ((c & 0xF0) == 0x80) { return (false, false); }
                    tail3 = false;
                }

                tail -= 1;
            }
            else if (c < 0x80)
            {
                if (!needsEscaping)
                {
                    needsEscaping = !((c > 34 && c != 92) || c == 32 || c == 33);
                }
            }
            else if ((c & 0xE0) == 0xC0)
            {
                if ((c & 0xFE) == 0xC0) { return (false, false); }
                tail = 1;
            }
            else if ((c & 0xF0) == 0xE0)
            {
                tail = 2;
                tail2 = c == 0xE0;
            }
            else if ((c & 0xF8) == 0xF0)
            {
                tail = 3;
                tail3 = c == 0xF0;
            }
            else
            {
                return (false, false);
            }
        }

        return (tail == 0, !needsEscaping);
    }
    internal static void AppendName(StringBuilder builder, PdfName name, int version)
    {
        if (version == 1)
        {
            AppendJsonString(builder, NormalizeName(SlashForm(name)));
            return;
        }

        byte[] slash = SlashForm(name);
        (bool valid, bool plain) = AnalyzeName(slash);
        if (valid)
        {
            if (plain)
            {
                builder.Append((char)34);
                builder.Append(Encoding.UTF8.GetString(slash));
                builder.Append((char)34);
            }
            else
            {
                AppendJsonString(builder, Encoding.UTF8.GetString(slash));
            }

            return;
        }

        AppendJsonString(builder, "n:" + NormalizeName(slash));
    }

    internal static bool UseHexString(byte[] value)
    {
        int nonAscii = 0;
        foreach (byte raw in value)
        {
            int ch = raw >= 128 ? raw - 256 : raw;
            if (raw > 126) { nonAscii += 1; }
            else if (raw >= 32) { continue; }
            else if (ch < 0 || ch >= 24) { nonAscii += 1; }
            else if (!(raw == 10 || raw == 13 || raw == 9 || raw == 8 || raw == 12))
            {
                return true;
            }
        }

        return 5 * nonAscii > value.Length;
    }
    internal static string DecodeUtf16(byte[] value, bool little)
    {
        StringBuilder output = new(value.Length / 2);
        int i = 2;
        while (i + 1 < value.Length)
        {
            int unit = little ? (value[i] | (value[i + 1] << 8)) : ((value[i] << 8) | value[i + 1]);
            i += 2;
            output.Append((char)unit);
        }

        return output.ToString();
    }

    internal static void AppendPdfString(StringBuilder builder, byte[] value, int version)
    {
        bool utf16 = value.Length >= 2
            && ((value[0] == 0xFE && value[1] == 0xFF) || (value[0] == 0xFF && value[1] == 0xFE));
        bool explicitUtf8 = value.Length >= 3 && value[0] == 0xEF && value[1] == 0xBB && value[2] == 0xBF;
        if (version == 1)
        {
            if (utf16)
            {
                AppendJsonString(builder, DecodeUtf16(value, value[0] == 0xFF));
            }
            else if (explicitUtf8)
            {
                AppendJsonString(builder, Encoding.UTF8.GetString(value, 3, value.Length - 3));
            }
            else
            {
                AppendJsonString(builder, Attachments.PdfText.Decode(value));
            }

            return;
        }

        if (utf16)
        {
            AppendJsonString(builder, "u:" + DecodeUtf16(value, value[0] == 0xFF));
        }
        else if (explicitUtf8)
        {
            AppendJsonString(builder, "u:" + Encoding.UTF8.GetString(value, 3, value.Length - 3));
        }
        else if (!UseHexString(value) && RoundTripsPdfDoc(value))
        {
            AppendJsonString(builder, "u:" + Attachments.PdfText.Decode(value));
        }
        else
        {
            AppendJsonString(builder, "b:" + Convert.ToHexStringLower(value));
        }
    }
    private static readonly int[] LowTable =
        [0x02D8, 0x02C7, 0x02C6, 0x02D9, 0x02DD, 0x02DB, 0x02DA, 0x02DC];

    private static readonly int[] HighTable =
    [
        0xFFFD, 0x2022, 0x2020, 0x2021, 0x2026, 0x2014, 0x2013, 0x0192,
        0x2044, 0x2039, 0x203A, 0x2212, 0x2030, 0x201E, 0x201C, 0x201D,
        0x2018, 0x2019, 0x201A, 0x2122, 0xFB01, 0xFB02, 0x0141, 0x0152,
        0x0160, 0x0178, 0x017D, 0x0131, 0x0142, 0x0153, 0x0161, 0x017E,
        0xFFFD, 0x20AC,
    ];

    internal static bool RoundTripsPdfDoc(byte[] value)
    {
        string decoded = Attachments.PdfText.Decode(value);
        if (decoded.Length != value.Length)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            if (EncodePdfDocChar(decoded[i]) != value[i])
            {
                return false;
            }
        }

        return true;
    }

    private static byte EncodePdfDocChar(char c)
    {
        if (c < 128)
        {
            return (byte)c;
        }

        for (int i = 0; i < LowTable.Length; i++)
        {
            if (LowTable[i] == c)
            {
                return (byte)(24 + i);
            }
        }

        for (int i = 0; i < HighTable.Length; i++)
        {
            if (HighTable[i] == c)
            {
                return (byte)(127 + i);
            }
        }

        return 63;
    }
    internal static string NormalizeReal(string text)
    {
        if (text.Length == 0)
        {
            return "0";
        }

        string value = text;
        if (value[0] == 46)
        {
            value = "0" + value;
        }
        else if (value.Length >= 2 && value[0] == 45 && value[1] == 46)
        {
            value = "-0." + value.Substring(2);
        }
        else if (value[0] == 48)
        {
            int i = 0;
            while (i < value.Length && value[i] == 48)
            {
                i += 1;
            }

            if (i == value.Length)
            {
                value = "0";
            }
            else if (value[i] == 46)
            {
                value = "0" + value.Substring(i);
            }
            else
            {
                value = value.Substring(i);
            }
        }

        if (value[value.Length - 1] == 46)
        {
            value += "0";
        }

        return value;
    }

    internal static void AppendReference(StringBuilder builder, int number, int generation)
    {
        builder.Append(number.ToString(CultureInfo.InvariantCulture));
        builder.Append((char)32);
        builder.Append(generation.ToString(CultureInfo.InvariantCulture));
        builder.Append(" R");
    }
    internal static void AppendValue(
        StringBuilder builder,
        QpdfObject value,
        int version,
        Func<PdfReference, QpdfObject?> resolver,
        ResourceGovernor governor,
        int depth,
        QpdfLimits limits)
    {
        governor.ChargeWork(1L);
        if (depth > limits.MaxNestingDepth)
        {
            throw new QpdfParseException("JSON inspection exceeds nesting budget.", null, null);
        }

        switch (value)
        {
            case PdfNull:
                builder.Append("null");
                break;
            case PdfBoolean boolean:
                builder.Append(boolean.Value ? "true" : "false");
                break;
            case PdfInteger integer:
                builder.Append(integer.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case PdfReal real:
                builder.Append(NormalizeReal(real.Text));
                break;
            case PdfByteString text:
                AppendPdfString(builder, text.Value, version);
                break;
            case PdfName name:
                AppendName(builder, name, version);
                break;
            case PdfReference reference:
                builder.Append((char)34);
                AppendReference(builder, reference.ObjectNumber, reference.Generation);
                builder.Append((char)34);
                break;
            case PdfArray array:
                builder.Append((char)91);
                QpdfObject[] items = array.Items;
                for (int i = 0; i < items.Length; i++)
                {
                    if (i > 0)
                    {
                        builder.Append((char)44);
                    }

                    AppendValue(builder, items[i], version, resolver, governor, depth + 1, limits);
                }

                builder.Append((char)93);
                break;
            case PdfDictionary dictionary:
                AppendDictionary(builder, dictionary, version, resolver, governor, depth + 1, limits);
                break;
            case PdfStream stream:
                AppendStream(builder, stream, version, resolver, governor, depth + 1, limits);
                break;
            default:
                throw new QpdfParseException("Unsupported object in JSON inspection.", null, null);
        }
    }

    internal static void AppendDictionary(
        StringBuilder builder,
        PdfDictionary dictionary,
        int version,
        Func<PdfReference, QpdfObject?> resolver,
        ResourceGovernor governor,
        int depth,
        QpdfLimits limits)
    {
        List<KeyValuePair<byte[], QpdfObject>> entries = new(dictionary.Count);
        foreach (PdfName key in dictionary.Keys)
        {
            if (dictionary.TryGet(key, out QpdfObject? found) && found is not null && found is not PdfNull)
            {
                byte[] raw = key.RawBytes;
                byte[] slash = new byte[raw.Length + 1];
                slash[0] = 47;
                Buffer.BlockCopy(raw, 0, slash, 1, raw.Length);
                entries.Add(new KeyValuePair<byte[], QpdfObject>(slash, found));
            }
        }

        entries.Sort(static (left, right) =>
        {
            int width = Math.Min(left.Key.Length, right.Key.Length);
            for (int i = 0; i < width; i++)
            {
                if (left.Key[i] != right.Key[i])
                {
                    return left.Key[i] - right.Key[i];
                }
            }

            return left.Key.Length - right.Key.Length;
        });

        builder.Append((char)123);
        bool first = true;
        foreach (KeyValuePair<byte[], QpdfObject> entry in entries)
        {
            if (!first)
            {
                builder.Append((char)44);
            }

            first = false;
            byte[] form = entry.Key;
            if (version == 1)
            {
                AppendJsonString(builder, NormalizeName(form));
            }
            else
            {
                byte[] slash = form;
                (bool valid, bool plain) = AnalyzeName(slash);
                if (valid)
                {
                    if (plain)
                    {
                        builder.Append((char)34);
                        builder.Append(Encoding.UTF8.GetString(slash));
                        builder.Append((char)34);
                    }
                    else
                    {
                        AppendJsonString(builder, Encoding.UTF8.GetString(slash));
                    }
                }
                else
                {
                    AppendJsonString(builder, "n:" + NormalizeName(form));
                }
            }

            builder.Append(": ");
            AppendValue(builder, entry.Value, version, resolver, governor, depth, limits);
        }

        builder.Append((char)125);
    }
    internal static void AppendStream(
        StringBuilder builder,
        PdfStream stream,
        int version,
        Func<PdfReference, QpdfObject?> resolver,
        ResourceGovernor governor,
        int depth,
        QpdfLimits limits)
    {
        if (version == 1)
        {
            AppendDictionary(builder, stream.Dictionary, version, resolver, governor, depth, limits);
            return;
        }

        builder.Append("{\"stream\": {\"dict\": ");
        AppendDictionary(builder, stream.Dictionary, version, resolver, governor, depth, limits);
        builder.Append("}}");
    }
}