using System.Globalization;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Json;

/// <summary>An imported document model: header version plus numbered objects.</summary>
internal sealed record ImportedModel(PdfVersion HeaderVersion, Dictionary<int, QpdfObject> Objects, PdfDictionary Trailer);

/// <summary>
/// qpdf JSON to PDF object model import. Mirrors the upstream import reactor:
/// only the top-level qpdf array matters, object keys are strict, stream
/// payloads come from inline base64 or host sidecar files, and unknown keys
/// are ignored for forward compatibility.
/// </summary>
internal static class JsonImport
{
    internal static async ValueTask<JsonDocument> ReadJsonAsync(
        IQpdfHost host,
        QpdfPath jsonPath,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        limits.Validate();
        IQpdfInput input = await host.OpenInputAsync(jsonPath, cancellationToken).ConfigureAwait(false);
        governor.NoteHostOperation();
        try
        {
            if (input.Length > limits.MaxInputBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), input.Length, limits.MaxInputBytes);
            }

            if (input.Length > int.MaxValue)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), input.Length, int.MaxValue);
            }

            governor.ReserveMemory(input.Length);
            byte[] bytes = await QpdfInputReader.ReadExactlyAsync(input, 0L, (int)input.Length, cancellationToken).ConfigureAwait(false);
            governor.NoteHostOperation();
            JsonDocumentOptions options = new()
            {
                MaxDepth = 128,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            };
            try
            {
                return JsonDocument.Parse(bytes, options);
            }
            catch (JsonException ex)
            {
                throw new QpdfParseException("Invalid qpdf JSON: " + ex.Message + ".", 0L, null, ex);
            }
        }
        finally
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }
    }
    internal static async ValueTask<ImportedModel> BuildCreateModelAsync(
        IQpdfHost host,
        JsonDocument document,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        limits.Validate();
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new QpdfParseException("qpdf JSON must be a dictionary.", 0L, null);
        }

        if (!root.TryGetProperty("qpdf", out JsonElement qpdf))
        {
            throw new QpdfParseException("qpdf JSON object was not seen.", 0L, null);
        }

        if (qpdf.ValueKind != JsonValueKind.Array || qpdf.GetArrayLength() != 2)
        {
            throw new QpdfParseException("qpdf JSON qpdf must have two elements.", 0L, null);
        }

        if (qpdf[0].ValueKind != JsonValueKind.Object)
        {
            throw new QpdfParseException("qpdf JSON qpdf[0] must be a dictionary.", 0L, null);
        }

        if (qpdf[1].ValueKind != JsonValueKind.Object)
        {
            throw new QpdfParseException("qpdf JSON qpdf[1] must be a dictionary.", 0L, null);
        }

        PdfVersion header = ReadHeaderVersion(qpdf[0], true);
        Dictionary<int, QpdfObject> objects = new();
        PdfDictionary? trailer = null;
        bool sawTrailer = false;
        foreach (JsonProperty property in qpdf[1].EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (property.Name == "trailer")
            {
                sawTrailer = true;
                trailer = ReadTrailer(property.Value);
                continue;
            }

            int number = ParseObjectKey(property.Name);
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("qpdf JSON object must be a dictionary.", 0L, null);
            }

            objects[number] = await ReadObjectAsync(host, property.Value, true, false, null, limits, governor, cancellationToken).ConfigureAwait(false);
        }

        if (!sawTrailer)
        {
            throw new QpdfParseException("qpdf JSON trailer was not seen.", 0L, null);
        }

        return new ImportedModel(header, objects, trailer ?? new PdfDictionary(new Dictionary<PdfName, QpdfObject>()));
    }

    internal static PdfVersion ReadHeaderVersion(JsonElement meta, bool required)
    {
        if (meta.ValueKind != JsonValueKind.Object)
        {
            throw new QpdfParseException("qpdf JSON qpdf[0] must be a dictionary.", 0L, null);
        }

        bool sawJsonVersion = false;
        bool sawPdfVersion = false;
        PdfVersion header = new(1, 4);
        foreach (JsonProperty property in meta.EnumerateObject())
        {
            if (property.Name == "jsonversion")
            {
                sawJsonVersion = true;
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out int version) || version != 2)
                {
                    throw new QpdfParseException("qpdf JSON version must be numeric value 2.", 0L, null);
                }

                continue;
            }

            if (property.Name == "pdfversion")
            {
                sawPdfVersion = true;
                if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not string text)
                {
                    throw new QpdfParseException("qpdf JSON pdfversion must be a string.", 0L, null);
                }

                header = ParsePdfVersion(text);
                continue;
            }

            if (property.Name == "pushedinheritedpageresources" || property.Name == "calledgetallpages")
            {
                if (property.Value.ValueKind != JsonValueKind.True && property.Value.ValueKind != JsonValueKind.False)
                {
                    throw new QpdfParseException("qpdf JSON " + property.Name + " must be a boolean.", 0L, null);
                }

                continue;
            }
        }

        if (!sawJsonVersion)
        {
            throw new QpdfParseException("qpdf JSON jsonversion was not seen.", 0L, null);
        }

        if (required && !sawPdfVersion)
        {
            throw new QpdfParseException("qpdf JSON pdfversion was not seen.", 0L, null);
        }

        return header;
    }

    internal static PdfVersion ParsePdfVersion(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        string[] parts = text.Split((char)46);
        if (parts.Length != 2)
        {
            throw new QpdfParseException("Invalid PDF version in qpdf JSON (must be x.y).", 0L, null);
        }

        if (!TryParseVersionPart(parts[0], out int major) || !TryParseVersionPart(parts[1], out int minor))
        {
            throw new QpdfParseException("Invalid PDF version in qpdf JSON (must be x.y).", 0L, null);
        }

        if ((major == 1 && minor > 7) || (major == 2 && minor != 0) || (major != 1 && major != 2))
        {
            throw new QpdfParseException("Unsupported PDF version " + major.ToString(CultureInfo.InvariantCulture) + "." + minor.ToString(CultureInfo.InvariantCulture) + ".", 0L, null);
        }

        return new PdfVersion(major, minor);
    }

    private static bool TryParseVersionPart(string text, out int value)
    {
        value = 0;
        if (text.Length == 0 || text.Length > 4)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c < (char)48 || c > (char)57)
            {
                return false;
            }
        }

        value = int.Parse(text, CultureInfo.InvariantCulture);
        return true;
    }

    internal static int ParseObjectKey(string key)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        if (!key.StartsWith("obj:", StringComparison.Ordinal))
        {
            throw new QpdfParseException("qpdf JSON object key should be trailer or obj:n n R.", 0L, null);
        }

        string rest = key.Substring(4);
        if (rest.Length == 0 || rest[0] == (char)32 || rest[rest.Length - 1] == (char)32)
        {
            throw new QpdfParseException("qpdf JSON object key should be trailer or obj:n n R.", 0L, null);
        }

        string[] parts = rest.Split((char)32, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[2] != "R")
        {
            throw new QpdfParseException("qpdf JSON object key should be trailer or obj:n n R.", 0L, null);
        }

        if (!TryParseObjectId(parts[0], out int number) || number <= 0)
        {
            throw new QpdfParseException("qpdf JSON object key should be trailer or obj:n n R.", 0L, null);
        }

        if (!TryParseObjectId(parts[1], out int generation))
        {
            throw new QpdfParseException("qpdf JSON object key should be trailer or obj:n n R.", 0L, null);
        }

        if (generation != 0)
        {
            throw new QpdfParseException("qpdf JSON object generations other than 0 are outside the narrow profile.", 0L, null);
        }

        return number;
    }

    private static bool TryParseObjectId(string text, out int value)
    {
        value = 0;
        if (text is null || text.Length == 0 || text.Length > 10)
        {
            return false;
        }

        long parsed = 0L;
        foreach (char c in text)
        {
            if (c < (char)48 || c > (char)57)
            {
                return false;
            }

            parsed = checked(parsed * 10L + (c - (char)48));
        }

        if (parsed > (long)int.MaxValue)
        {
            return false;
        }

        value = (int)parsed;
        return true;
    }
    private static PdfDictionary ReadTrailer(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new QpdfParseException("qpdf JSON object must be a dictionary.", 0L, null);
        }

        bool sawValue = false;
        bool sawStream = false;
        PdfDictionary trailer = new(new Dictionary<PdfName, QpdfObject>());
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Name == "value")
            {
                sawValue = true;
                QpdfObject decoded = DecodeValue(property.Value, 0);
                if (decoded is not PdfDictionary dictionary)
                {
                    throw new QpdfParseException("qpdf JSON trailer value must be a dictionary.", 0L, null);
                }

                trailer = dictionary;
                continue;
            }

            if (property.Name == "stream")
            {
                sawStream = true;
                throw new QpdfParseException("qpdf JSON trailer may not be a stream.", 0L, null);
            }
        }

        if (!sawValue || sawStream)
        {
            throw new QpdfParseException("qpdf JSON trailer is missing value.", 0L, null);
        }

        return trailer;
    }

    private static async ValueTask<QpdfObject> ReadObjectAsync(
        IQpdfHost host,
        JsonElement value,
        bool requireComplete,
        bool existingIsStream,
        byte[]? existingData,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        bool sawValue = false;
        bool sawStream = false;
        QpdfObject? result = null;
        QpdfObject? streamDict = null;
        bool sawData = false;
        bool sawDatafile = false;
        string? datafile = null;
        byte[]? inlineData = null;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (property.Name == "value")
            {
                sawValue = true;
                QpdfObject decoded = DecodeValue(property.Value, 0);
                if (decoded is PdfReference)
                {
                    throw new QpdfParseException("qpdf JSON object value may not be an indirect object reference.", 0L, null);
                }

                result = decoded;
                continue;
            }

            if (property.Name == "stream")
            {
                sawStream = true;
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new QpdfParseException("qpdf JSON stream must be a dictionary.", 0L, null);
                }

                foreach (JsonProperty inner in property.Value.EnumerateObject())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    governor.ChargeWork(1L);
                    if (inner.Name == "dict")
                    {
                        QpdfObject decoded = DecodeValue(inner.Value, 0);
                        if (decoded is not PdfDictionary dictionary)
                        {
                            throw new QpdfParseException("qpdf JSON stream dict must be a dictionary.", 0L, null);
                        }

                        streamDict = dictionary;
                        continue;
                    }

                    if (inner.Name == "data")
                    {
                        sawData = true;
                        if (inner.Value.ValueKind != JsonValueKind.String || inner.Value.GetString() is not string text)
                        {
                            throw new QpdfParseException("qpdf JSON stream data must be a string.", 0L, null);
                        }

                        try
                        {
                            inlineData = Convert.FromBase64String(text);
                        }
                        catch (FormatException ex)
                        {
                            throw new QpdfParseException("qpdf JSON stream data must be base64.", 0L, null, ex);
                        }

                        governor.ReserveMemory(inlineData.Length);
                        continue;
                    }

                    if (inner.Name == "datafile")
                    {
                        sawDatafile = true;
                        if (inner.Value.ValueKind != JsonValueKind.String || inner.Value.GetString() is not string name)
                        {
                            throw new QpdfParseException("qpdf JSON stream datafile must be a string containing a file name.", 0L, null);
                        }

                        datafile = name;
                        continue;
                    }
                }

                continue;
            }
        }

        if (sawValue == sawStream)
        {
            throw new QpdfParseException("qpdf JSON object must have exactly one of value or stream.", 0L, null);
        }

        if (sawStream)
        {
            if (streamDict is null)
            {
                throw new QpdfParseException("qpdf JSON stream is missing dict.", 0L, null);
            }

            bool needsData = requireComplete || !existingIsStream;
            if ((sawData && sawDatafile) || (needsData && sawData == sawDatafile))
            {
                if (needsData)
                {
                    throw new QpdfParseException("qpdf JSON new stream must have exactly one of data or datafile.", 0L, null);
                }

                throw new QpdfParseException("qpdf JSON stream may have at most one of data or datafile.", 0L, null);
            }

            byte[] data;
            if (sawData)
            {
                data = inlineData ?? [];
            }
            else if (sawDatafile)
            {
                data = await ReadSidecarAsync(host, datafile!, limits, governor, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                data = existingData ?? [];
            }

            return new PdfStream((PdfDictionary)streamDict, data);
        }

        return result ?? PdfNull.Instance;
    }

    private static async ValueTask<byte[]> ReadSidecarAsync(
        IQpdfHost host,
        string name,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (name.Length == 0 || name.Length > QpdfPath.MaxLength || name.Contains((char)0))
        {
            throw new QpdfParseException("qpdf JSON stream datafile is not a valid logical path.", 0L, null);
        }

        QpdfPath path = QpdfPath.From(name);
        IQpdfInput input = await host.OpenInputAsync(path, cancellationToken).ConfigureAwait(false);
        governor.NoteHostOperation();
        try
        {
            if (input.Length > limits.MaxInputBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), input.Length, limits.MaxInputBytes);
            }

            if (input.Length > int.MaxValue)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), input.Length, int.MaxValue);
            }

            governor.ReserveMemory(input.Length);
            byte[] bytes = await QpdfInputReader.ReadExactlyAsync(input, 0L, (int)input.Length, cancellationToken).ConfigureAwait(false);
            governor.NoteHostOperation();
            return bytes;
        }
        finally
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }
    }
    internal static QpdfObject DecodeValue(JsonElement value, int depth)
    {
        if (depth > 256)
        {
            throw new QpdfParseException("qpdf JSON exceeds nesting budget.", 0L, null);
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return PdfNull.Instance;
            case JsonValueKind.True:
                return PdfBoolean.True;
            case JsonValueKind.False:
                return PdfBoolean.False;
            case JsonValueKind.Number:
                return DecodeNumber(value);
            case JsonValueKind.String:
                return DecodeString(value.GetString() ?? string.Empty);
            case JsonValueKind.Array:
            {
                List<QpdfObject> items = new();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    items.Add(DecodeValue(item, depth + 1));
                }

                return new PdfArray(items);
            }

            case JsonValueKind.Object:
            {
                Dictionary<PdfName, QpdfObject> entries = new();
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    entries[DecodeName(property.Name)] = DecodeValue(property.Value, depth + 1);
                }

                return new PdfDictionary(entries);
            }

            default:
                throw new QpdfParseException("Unsupported qpdf JSON value.", 0L, null);
        }
    }

    private static QpdfObject DecodeNumber(JsonElement value)
    {
        string raw = value.GetRawText();
        if (IsLongText(raw) && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
        {
            return new PdfInteger(raw, integer);
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double real))
        {
            return new PdfReal(raw, real);
        }

        throw new QpdfParseException("qpdf JSON number is not a PDF number.", 0L, null);
    }

    private static bool IsLongText(string raw)
    {
        if (raw.Length == 0)
        {
            return false;
        }

        int start = raw[0] == (char)45 ? 1 : 0;
        if (start == 1 && raw.Length == 1)
        {
            return false;
        }

        for (int i = start; i < raw.Length; i++)
        {
            if (raw[i] < (char)48 || raw[i] > (char)57)
            {
                return false;
            }
        }

        return true;
    }

    private static QpdfObject DecodeString(string text)
    {
        if (IsIndirectReference(text, out int number, out int generation))
        {
            return new PdfReference(number, generation);
        }

        if (text.StartsWith("u:", StringComparison.Ordinal))
        {
            return new PdfByteString(EncodeTextString(text.Substring(2)), false);
        }

        if (text.StartsWith("b:", StringComparison.Ordinal))
        {
            string hex = text.Substring(2);
            if (hex.Length % 2 != 0)
            {
                throw new QpdfParseException("qpdf JSON binary string must have an even hex length.", 0L, null);
            }

            foreach (char c in hex)
            {
                if (!IsHexChar(c))
                {
                    throw new QpdfParseException("qpdf JSON binary string must be hexadecimal.", 0L, null);
                }
            }

            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)((HexValue(hex[i * 2]) << 4) | HexValue(hex[i * 2 + 1]));
            }

            return new PdfByteString(bytes, true);
        }

        if (text.StartsWith("/", StringComparison.Ordinal))
        {
            return new PdfName(Encoding.UTF8.GetBytes(text.Substring(1)));
        }

        if (text.StartsWith("n:", StringComparison.Ordinal))
        {
            string rest = text.Substring(2);
            if (!rest.StartsWith("/", StringComparison.Ordinal))
            {
                throw new QpdfParseException("qpdf JSON unrecognized string value.", 0L, null);
            }

            return new PdfName(ResolveNameSyntax(rest.Substring(1)));
        }

        throw new QpdfParseException("qpdf JSON unrecognized string value.", 0L, null);
    }

    private static PdfName DecodeName(string key)
    {
        if (key.StartsWith("n:", StringComparison.Ordinal))
        {
            string rest = key.Substring(2);
            if (!rest.StartsWith("/", StringComparison.Ordinal))
            {
                throw new QpdfParseException("qpdf JSON unrecognized name value.", 0L, null);
            }

            return new PdfName(ResolveNameSyntax(rest.Substring(1)));
        }

        if (key.StartsWith("/", StringComparison.Ordinal))
        {
            return new PdfName(Encoding.UTF8.GetBytes(key.Substring(1)));
        }

        return new PdfName(Encoding.UTF8.GetBytes(key));
    }

    internal static bool IsIndirectReference(string text, out int number, out int generation)
    {
        number = 0;
        generation = 0;
        if (text is null)
        {
            return false;
        }

        string[] parts = text.Split((char)32, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[2] != "R")
        {
            return false;
        }

        if (text.Length == 0 || text[0] == (char)32 || text[text.Length - 1] == (char)32)
        {
            return false;
        }

        if (!TryParseDigits(parts[0], out number) || number <= 0)
        {
            return false;
        }

        if (!TryParseDigits(parts[1], out generation))
        {
            return false;
        }

        return true;
    }

    private static bool TryParseDigits(string text, out int value)
    {
        value = 0;
        if (text.Length == 0 || text.Length > 10)
        {
            return false;
        }

        long parsed = 0L;
        foreach (char c in text)
        {
            if (c < (char)48 || c > (char)57)
            {
                return false;
            }

            parsed = checked(parsed * 10L + (c - (char)48));
        }

        if (parsed > (long)int.MaxValue)
        {
            return false;
        }

        value = (int)parsed;
        return true;
    }
    private static byte[] ResolveNameSyntax(string body)
    {
        byte[] source = Encoding.UTF8.GetBytes(body);
        List<byte> resolved = new(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            if (source[i] == 35)
            {
                if (i + 2 >= source.Length || !IsHexChar((char)source[i + 1]) || !IsHexChar((char)source[i + 2]))
                {
                    throw new QpdfParseException("qpdf JSON name has an invalid escape.", 0L, null);
                }

                resolved.Add((byte)((HexValue((char)source[i + 1]) << 4) | HexValue((char)source[i + 2])));
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

    private static bool IsHexChar(char c)
    {
        return (c >= (char)48 && c <= (char)57) || (c >= (char)65 && c <= (char)70) || (c >= (char)97 && c <= (char)102);
    }

    private static int HexValue(char c)
    {
        if (c >= (char)48 && c <= (char)57)
        {
            return c - (char)48;
        }

        if (c >= (char)65 && c <= (char)70)
        {
            return c - (char)55;
        }

        return c - (char)87;
    }

    private static readonly int[] PdfDocLowTable =
        [0x02D8, 0x02C7, 0x02C6, 0x02D9, 0x02DD, 0x02DB, 0x02DA, 0x02DC];

    private static readonly int[] PdfDocHighTable =
    [
        0xFFFD, 0x2022, 0x2020, 0x2021, 0x2026, 0x2014, 0x2013, 0x0192,
        0x2044, 0x2039, 0x203A, 0x2212, 0x2030, 0x201E, 0x201C, 0x201D,
        0x2018, 0x2019, 0x201A, 0x2122, 0xFB01, 0xFB02, 0x0141, 0x0152,
        0x0160, 0x0178, 0x017D, 0x0131, 0x0142, 0x0153, 0x0161, 0x017E,
        0xFFFD, 0x20AC,
    ];

    internal static byte[] EncodeTextString(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        List<byte> encoded = new(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int unit = char.ConvertToUtf32(text, i);
            i += char.IsSurrogatePair(text, i) ? 2 : 1;
            if (!TryEncodePdfDoc(unit, out byte mapped))
            {
                List<byte> utf16 = new(text.Length * 2 + 2) { 0xFE, 0xFF };
                for (int j = 0; j < text.Length; j++)
                {
                    utf16.Add((byte)(text[j] >> 8));
                    utf16.Add((byte)(text[j] & 0xFF));
                }

                return [.. utf16];
            }

            encoded.Add(mapped);
        }

        return [.. encoded];
    }

    private static bool TryEncodePdfDoc(int unit, out byte mapped)
    {
        if (unit >= 0 && unit < 128)
        {
            mapped = (byte)unit;
            return true;
        }

        for (int i = 0; i < PdfDocLowTable.Length; i++)
        {
            if (PdfDocLowTable[i] == unit)
            {
                mapped = (byte)(24 + i);
                return true;
            }
        }

        for (int i = 0; i < PdfDocHighTable.Length; i++)
        {
            if (PdfDocHighTable[i] == unit)
            {
                mapped = (byte)(127 + i);
                return true;
            }
        }

        mapped = 0;
        return false;
    }
    internal sealed record RenumberedModel(
        List<Writing.NumberedObject> Model,
        int? InfoNumber,
        int PageCount);

    internal static RenumberedModel Renumber(
        ImportedModel model,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (model is null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        limits.Validate();
        if ((long)model.Objects.Count > limits.MaxWork)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxWork), model.Objects.Count, limits.MaxWork);
        }

        if (model.Trailer.TryGet(PdfName.FromAscii("Encrypt"), out QpdfObject? encrypt) && encrypt is not null && encrypt is not PdfNull)
        {
            throw new QpdfCryptoException("/Encrypt");
        }

        if (!model.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject? rootRaw) || rootRaw is null || rootRaw is PdfNull)
        {
            throw new QpdfParseException("Imported trailer must declare /Root.", 0L, null);
        }

        Dictionary<int, QpdfObject> pool = new(model.Objects.Count + 1);
        int rootOld;
        PdfDictionary root;
        if (rootRaw is PdfDictionary directRoot)
        {
            rootOld = -1;
            root = directRoot;
        }
        else if (rootRaw is PdfReference rootReference)
        {
            rootOld = rootReference.ObjectNumber;
            if (!model.Objects.TryGetValue(rootOld, out QpdfObject? rootValue) || rootValue is not PdfDictionary rootDict)
            {
                throw new QpdfParseException("Imported catalog reference does not resolve to a dictionary.", 0L, rootOld);
            }

            root = rootDict;
        }
        else
        {
            throw new QpdfParseException("Imported trailer /Root must be a dictionary or a reference.", 0L, null);
        }

        Dictionary<int, int> renumber = new(model.Objects.Count + 1);
        renumber[rootOld] = 1;
        List<int> rest = new(model.Objects.Count);
        foreach (int number in model.Objects.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (number != rootOld)
            {
                rest.Add(number);
            }
        }

        rest.Sort();
        for (int i = 0; i < rest.Count; i++)
        {
            renumber[rest[i]] = i + 2;
        }

        int infoOld = -1;
        bool infoDirect = false;
        PdfDictionary? infoDict = null;
        if (model.Trailer.TryGet(PdfName.FromAscii("Info"), out QpdfObject? infoRaw) && infoRaw is not null && infoRaw is not PdfNull)
        {
            if (infoRaw is PdfReference infoReference)
            {
                infoOld = infoReference.ObjectNumber;
                if (!model.Objects.TryGetValue(infoOld, out QpdfObject? infoValue) || infoValue is not PdfDictionary)
                {
                    infoOld = -1;
                }
            }
            else if (infoRaw is PdfDictionary directInfo)
            {
                infoDirect = true;
                infoDict = directInfo;
            }
        }

        if (infoDirect)
        {
            renumber[-2] = rest.Count + 2;
        }

        Dictionary<int, QpdfObject> mapped = new(model.Objects.Count + 1);
        foreach (KeyValuePair<int, QpdfObject> entry in model.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            mapped[renumber[entry.Key]] = RewriteReferences(entry.Value, renumber, governor, 0, limits);
        }

        if (infoDirect && infoDict is not null)
        {
            mapped[renumber[-2]] = RewriteReferences(infoDict, renumber, governor, 0, limits);
        }

        int? infoNew = null;
        if (infoOld >= 0 && renumber.TryGetValue(infoOld, out int mappedInfo))
        {
            infoNew = mappedInfo;
        }
        else if (infoDirect)
        {
            infoNew = renumber[-2];
        }

        List<Writing.NumberedObject> ordered = new(mapped.Count);
        List<int> numbers = new(mapped.Keys);
        numbers.Sort();
        foreach (int number in numbers)
        {
            ordered.Add(new Writing.NumberedObject(number, mapped[number]));
        }

        int pages = CountPages(mapped, governor, limits, cancellationToken);
        return new RenumberedModel(ordered, infoNew, pages);
    }

    private static QpdfObject RewriteReferences(
        QpdfObject value,
        Dictionary<int, int> renumber,
        ResourceGovernor governor,
        int depth,
        QpdfLimits limits)
    {
        governor.ChargeWork(1L);
        if (depth > limits.MaxNestingDepth)
        {
            throw new QpdfParseException("Imported object exceeds nesting budget.", 0L, null);
        }

        if (value is PdfReference reference)
        {
            if (renumber.TryGetValue(reference.ObjectNumber, out int mapped))
            {
                return new PdfReference(mapped, 0);
            }

            return PdfNull.Instance;
        }

        if (value is PdfArray array)
        {
            QpdfObject[] items = array.Items;
            List<QpdfObject> rewritten = new(items.Length);
            foreach (QpdfObject item in items)
            {
                rewritten.Add(RewriteReferences(item, renumber, governor, depth + 1, limits));
            }

            return new PdfArray(rewritten);
        }

        if (value is PdfDictionary dictionary)
        {
            Dictionary<PdfName, QpdfObject> entries = new();
            foreach (PdfName key in dictionary.Keys)
            {
                if (dictionary.TryGet(key, out QpdfObject? found) && found is not null)
                {
                    entries[key] = RewriteReferences(found, renumber, governor, depth + 1, limits);
                }
            }

            return new PdfDictionary(entries);
        }

        if (value is PdfStream stream)
        {
            QpdfObject rewrittenDict = RewriteReferences(stream.Dictionary, renumber, governor, depth + 1, limits);
            return new PdfStream((PdfDictionary)rewrittenDict, stream.Data);
        }

        return value;
    }

    private static int CountPages(
        Dictionary<int, QpdfObject> pool,
        ResourceGovernor governor,
        QpdfLimits limits,
        CancellationToken cancellationToken)
    {
        if (!pool.TryGetValue(1, out QpdfObject? rootValue) || rootValue is not PdfDictionary root)
        {
            return 0;
        }

        if (!root.TryGet(PdfName.FromAscii("Pages"), out QpdfObject? pagesRaw) || pagesRaw is null || pagesRaw is PdfNull)
        {
            return 0;
        }

        QpdfObject pages = ResolvePool(pagesRaw, pool);
        if (pages is not PdfDictionary pagesDict)
        {
            return 0;
        }

        int count = 0;
        List<QpdfObject> stack = new() { pagesDict };
        HashSet<QpdfObject> visited = new(ReferenceEqualityComparer.Instance);
        visited.Add(pagesDict);
        int depth = 0;
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (depth > limits.MaxNestingDepth)
            {
                return count;
            }

            depth += 1;
            List<QpdfObject> next = new();
            foreach (QpdfObject node in stack)
            {
                if (node is not PdfDictionary dictionary)
                {
                    continue;
                }

                if (dictionary.TryGet(PdfName.FromAscii("Type"), out QpdfObject? typeRaw) && typeRaw is PdfName type && type.DisplayText == "Page")
                {
                    count += 1;
                    continue;
                }

                if (dictionary.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw) && kidsRaw is not null && kidsRaw is not PdfNull)
                {
                    QpdfObject kids = ResolvePool(kidsRaw, pool);
                    if (kids is PdfArray array)
                    {
                        foreach (QpdfObject kid in array.Items)
                        {
                            QpdfObject target = ResolvePool(kid, pool);
                            if (target is PdfDictionary && visited.Add(target))
                            {
                                next.Add(target);
                            }
                        }
                    }
                }
            }

            stack = next;
        }

        return count;
    }

    private static QpdfObject ResolvePool(QpdfObject value, Dictionary<int, QpdfObject> pool)
    {
        if (value is PdfReference reference && pool.TryGetValue(reference.ObjectNumber, out QpdfObject? resolved) && resolved is not null)
        {
            return resolved;
        }

        return value;
    }
}
