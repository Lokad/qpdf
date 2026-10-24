using System.Buffers;
using System.Text;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Json;

/// <summary>Encodes PDF JSON into shared admitted text chunks without private value copies.</summary>
internal static class PdfJsonWriter
{
    internal static void AppendJsonString(DisplayBuffer builder, string text)
    {
        builder.Append('"');
        AppendEscaped(builder, text.AsSpan());
        builder.Append('"');
    }

    internal static void AppendEscaped(DisplayBuffer builder, ReadOnlySpan<char> text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            CheckChunkCancellation(builder.Governor, index);
            AppendEscapedChar(builder, text[index]);
        }
    }

    private static void AppendEscapedChar(DisplayBuffer builder, char value)
    {
        if ((value > 34 && value != 92) || value is ' ' or '!') { builder.Append(value); return; }
        switch (value)
        {
            case '\\': builder.Append("\\\\"); break;
            case '"': builder.Append("\\\""); break;
            case '\b': builder.Append("\\b"); break;
            case '\f': builder.Append("\\f"); break;
            case '\n': builder.Append("\\n"); break;
            case '\r': builder.Append("\\r"); break;
            case '\t': builder.Append("\\t"); break;
            default:
                builder.Append(value < 16 ? "\\u000" : "\\u001");
                builder.Append("0123456789abcdef"[value % 16]);
                break;
        }
    }

    private static void CheckChunkCancellation(ResourceGovernor governor, int position)
    {
        if ((position & 255) == 0)
        { governor.CheckCancellation(); }
    }

    internal static void AppendName(DisplayBuffer builder, PdfName name, int version)
    {
        ReadOnlySpan<byte> raw = name.RawSpan;
        bool valid = version != 1 && IsUtf8Name(raw, builder.Governor);
        builder.Append('"');
        if (version != 1 && !valid) { builder.Append("n:"); }
        builder.Append('/');
        if (valid) { AppendUtf8(builder, raw); }
        else
        {
            for (int index = 0; index < raw.Length; index++)
            {
                CheckChunkCancellation(builder.Governor, index);
                byte value = raw[index];
                if (value == 0) { builder.Append('#'); }
                else if (value < 33 || value > 126 || value is 35 or 47 or 40 or 41 or 60 or 62 or 91 or 93 or 123 or 125 or 37)
                {
                    builder.Append('#');
                    builder.Append("0123456789abcdef"[value >> 4]);
                    builder.Append("0123456789abcdef"[value & 15]);
                }
                else { AppendEscapedChar(builder, (char)value); }
            }
        }
        builder.Append('"');
    }

    internal static void AppendAsciiName(DisplayBuffer builder, PdfName name)
    {
        builder.Append('"'); builder.Append('/');
        ReadOnlySpan<byte> raw = name.RawSpan;
        for (int index = 0; index < raw.Length; index++)
        {
            CheckChunkCancellation(builder.Governor, index);
            AppendEscapedChar(builder, raw[index] < 128 ? (char)raw[index] : '?');
        }
        builder.Append('"');
    }

    private static void AppendUtf8(DisplayBuffer builder, ReadOnlySpan<byte> value)
    {
        // Rune uses the same UTF-8 replacement rules as Encoding.UTF8. Keeping
        // each decode below four bytes avoids a decoder object or joined string.
        Span<char> chars = stackalloc char[2];
        while (!value.IsEmpty)
        {
            builder.Governor.NoteOperation();
            builder.Governor.CheckCancellation();
            OperationStatus status = Rune.DecodeFromUtf8(value, out Rune rune, out int consumed);
            if (status == OperationStatus.NeedMoreData) { consumed = value.Length; }
            if (status != OperationStatus.Done) { rune = Rune.ReplacementChar; }
            int written = rune.EncodeToUtf16(chars);
            for (int index = 0; index < written; index++) { AppendEscapedChar(builder, chars[index]); }
            value = value[consumed..];
        }
    }

    internal static void AppendPdfString(DisplayBuffer builder, ReadOnlySpan<byte> value, int version)
    {
        bool utf16 = value.Length >= 2 && ((value[0] == 0xFE && value[1] == 0xFF) || (value[0] == 0xFF && value[1] == 0xFE));
        bool utf8 = value.Length >= 3 && value[0] == 0xEF && value[1] == 0xBB && value[2] == 0xBF;
        bool binary = version != 1 && !utf16 && !utf8
            && (UseHexString(value, builder.Governor) || !RoundTripsPdfDoc(value, builder.Governor));
        builder.Append('"');
        if (version != 1) { builder.Append(binary ? "b:" : "u:"); }
        if (utf16)
        {
            for (int index = 2; index + 1 < value.Length; index += 2)
            {
                CheckChunkCancellation(builder.Governor, index - 2);
                char unit = (char)(value[0] == 0xFF ? value[index] | value[index + 1] << 8 : value[index] << 8 | value[index + 1]);
                AppendEscapedChar(builder, unit);
            }
        }
        else if (utf8) { AppendUtf8(builder, value[3..]); }
        else
        {
            for (int index = 0; index < value.Length; index++)
            {
                CheckChunkCancellation(builder.Governor, index);
                byte unit = value[index];
                if (binary)
                {
                    builder.Append("0123456789abcdef"[unit >> 4]);
                    builder.Append("0123456789abcdef"[unit & 15]);
                }
                else { AppendEscapedChar(builder, PdfText.DecodePdfDocByte(unit)); }
            }
        }
        builder.Append('"');
    }

    private static bool RoundTripsPdfDoc(ReadOnlySpan<byte> value, ResourceGovernor governor)
    {
        for (int index = 0; index < value.Length; index++)
        {
            CheckChunkCancellation(governor, index);
            if (!PdfText.TryEncodePdfDocByte(PdfText.DecodePdfDocByte(value[index]), out byte encoded) || encoded != value[index]) { return false; }
        }
        return true;
    }

    internal static void AppendBase64(DisplayBuffer builder, ReadOnlySpan<byte> value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        builder.Append('"');
        for (int index = 0; index < value.Length; index += 3)
        {
            builder.Governor.NoteOperation(); builder.Governor.CheckCancellation();
            int second = index + 1 < value.Length ? value[index + 1] : 0;
            int third = index + 2 < value.Length ? value[index + 2] : 0;
            builder.Append(alphabet[value[index] >> 2]);
            builder.Append(alphabet[(value[index] & 3) << 4 | second >> 4]);
            builder.Append(index + 1 < value.Length ? alphabet[(second & 15) << 2 | third >> 6] : '=');
            builder.Append(index + 2 < value.Length ? alphabet[third & 63] : '=');
        }
        builder.Append('"');
    }

    private static void AppendReal(DisplayBuffer builder, string text)
    {
        if (text.Length == 0) { builder.Append('0'); return; }
        ReadOnlySpan<char> value = text;
        if (value[0] is '+' or '-')
        {
            if (value[0] == '-') { builder.Append('-'); }
            value = value[1..];
        }
        int index = 0;
        while (index < value.Length && value[index] == '0')
        { CheckChunkCancellation(builder.Governor, index); index++; }
        value = value[index..];
        if (value.IsEmpty) { builder.Append('0'); return; }
        if (value[0] == '.') { builder.Append('0'); }
        builder.AppendSpan(value);
        if (value[^1] == '.') { builder.Append('0'); }
    }

    internal static void AppendReference(DisplayBuffer builder, int number, int generation)
    {
        builder.Governor.NoteOperation(); builder.Governor.CheckCancellation();
        builder.AppendInteger(number); builder.Append(' '); builder.AppendInteger(generation); builder.Append(" R");
    }

    internal static void AppendObjectKey(DisplayBuffer builder, int number, int generation, bool canonical)
    {
        builder.Append('"');
        if (canonical) { builder.Append("obj:"); }
        builder.AppendInteger(number); builder.Append(' '); builder.AppendInteger(generation);
        if (canonical) { builder.Append(" R"); }
        builder.Append('"');
    }

    internal static void AppendValue(DisplayBuffer builder, QpdfObject value, int version,
        ResourceGovernor governor, int depth, QpdfLimits limits)
    {
        governor.CheckCancellation();
        if (depth > limits.MaxNestingDepth) { throw new QpdfParseException("JSON inspection exceeds nesting budget.", null, null); }
        switch (value)
        {
            case PdfNull: builder.Append("null"); break;
            case PdfBoolean boolean: builder.Append(boolean.Value ? "true" : "false"); break;
            case PdfInteger integer: builder.AppendInteger(integer.Value); break;
            case PdfReal real: AppendReal(builder, real.Text); break;
            case PdfByteString text: AppendPdfString(builder, text.ValueSpan, version); break;
            case PdfName name: AppendName(builder, name, version); break;
            case PdfReference reference:
                builder.Append('"'); AppendReference(builder, reference.ObjectNumber, reference.Generation); builder.Append('"'); break;
            case PdfArray array:
                builder.Append('[');
                for (int index = 0; index < array.Count; index++)
                {
                    if (index > 0) { builder.Append(','); }
                    AppendValue(builder, array[index], version, governor, depth + 1, limits);
                }
                builder.Append(']'); break;
            case PdfDictionary dictionary: AppendDictionary(builder, dictionary, version, governor, depth + 1, limits); break;
            case PdfStream stream:
                if (version != 1) { builder.Append("{\"stream\": {\"dict\": "); }
                AppendDictionary(builder, stream.Dictionary, version, governor, depth + 1, limits);
                if (version != 1) { builder.Append("}}"); }
                break;
            default: throw new QpdfParseException("Unsupported object in JSON inspection.", null, null);
        }
    }

    internal static void AppendDictionary(DisplayBuffer builder, PdfDictionary dictionary, int version,
        ResourceGovernor governor, int depth, QpdfLimits limits)
        => AppendDictionaryCore(builder, dictionary, version, governor, depth, limits, false, false, null);

    internal static void AppendLabelDictionary(DisplayBuffer builder, PdfDictionary dictionary, int version,
        ResourceGovernor governor, QpdfLimits limits, IIndirectResolver resolver)
        => AppendDictionaryCore(builder, dictionary, version, governor, 1, limits, false, false, resolver);

    internal static void AppendStreamDictionary(DisplayBuffer builder, PdfDictionary dictionary, int version,
        ResourceGovernor governor, QpdfLimits limits, bool decoded)
        => AppendDictionaryCore(builder, dictionary, version, governor, 0, limits, true, decoded, null);

    private static void AppendDictionaryCore(DisplayBuffer builder, PdfDictionary dictionary, int version,
        ResourceGovernor governor, int depth, QpdfLimits limits, bool omitLength, bool decoded, IIndirectResolver? labelResolver)
    {
        long reserved = dictionary.Count == 0 ? 0L : checked(32L + 16L * dictionary.Count);
        governor.CheckCancellation(); governor.ReserveMemory(reserved);
        try
        {
            KeyValuePair<PdfName, QpdfObject>[] entries = dictionary.Count == 0
                ? Array.Empty<KeyValuePair<PdfName, QpdfObject>>() : new KeyValuePair<PdfName, QpdfObject>[dictionary.Count];
            int count = 0;
            using PdfDictionary.EntryEnumerator source = dictionary.GetEntryEnumerator();
            while (source.MoveNext())
            {
                governor.CheckCancellation();
                KeyValuePair<PdfName, QpdfObject> entry = source.Current;
                if (entry.Value is PdfNull) { continue; }
                if (omitLength && entry.Key.RawSpan.SequenceEqual("Length"u8)) { continue; }
                if (decoded && (entry.Key.RawSpan.SequenceEqual("Filter"u8) || entry.Key.RawSpan.SequenceEqual("DecodeParms"u8))) { continue; }
                if (labelResolver is not null && entry.Key.RawSpan.SequenceEqual("St"u8) && entry.Value is PdfReference reference
                    && PdfObjectLookup.TryResolve(reference, labelResolver, governor, CancellationToken.None, out QpdfObject? start)
                    && start is PdfInteger) { entry = new(entry.Key, start); }
                entries[count++] = entry;
            }
            GovernedSort.Sort<KeyValuePair<PdfName, QpdfObject>, RawKeyComparer>(entries.AsSpan(0, count), new(governor), governor, CancellationToken.None);
            builder.Append('{');
            for (int index = 0; index < count; index++)
            {
                governor.CheckCancellation();
                if (index > 0) { builder.Append(','); }
                AppendName(builder, entries[index].Key, version); builder.Append(": ");
                AppendValue(builder, entries[index].Value, version, governor, depth, limits);
            }
            builder.Append('}');
        }
        finally { governor.ReleaseMemory(reserved); }
    }

    private readonly struct RawKeyComparer(ResourceGovernor governor) : IComparer<KeyValuePair<PdfName, QpdfObject>>
    {
        public int Compare(KeyValuePair<PdfName, QpdfObject> left, KeyValuePair<PdfName, QpdfObject> right)
        {
            ReadOnlySpan<byte> a = left.Key.RawSpan, b = right.Key.RawSpan;
            int width = Math.Min(a.Length, b.Length);
            for (int index = 0; index < width; index++)
            {
                CheckChunkCancellation(governor, index);
                if (a[index] != b[index]) { return a[index] - b[index]; }
            }
            return a.Length - b.Length;
        }
    }
    private static bool IsUtf8Name(ReadOnlySpan<byte> value, ResourceGovernor governor)
    {
        while (!value.IsEmpty)
        {
            governor.CheckCancellation();
            if (Rune.DecodeFromUtf8(value, out _, out int consumed) != OperationStatus.Done) { return false; }
            value = value[consumed..];
        }
        return true;
    }

    internal static bool UseHexString(ReadOnlySpan<byte> value, ResourceGovernor governor)
    {
        governor.CheckCancellation();
        int nonAscii = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if ((i & 255) == 0)
            {
                governor.NoteOperation();
                governor.CheckCancellation();
            }
            byte raw = value[i];
            if (raw is >= 127 or >= 24 and < 32) { nonAscii += 1; }
            else if (raw < 24 && raw is not (8 or 9 or 10 or 12 or 13))
            {
                return true;
            }
        }

        return 5L * nonAscii > value.Length;
    }
}
