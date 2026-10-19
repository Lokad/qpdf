using System.Globalization;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
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
        ParserMemoryScope memory,
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

        limits.Validate(); memory.CheckOwner(governor);
        JsonDocument? parsed = null;
        IQpdfInput input = await GovernedHost.OpenInputAsync(host, jsonPath, governor, cancellationToken).ConfigureAwait(false);
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

            memory.Reserve(checked(input.Length + 32L));
            byte[] bytes = await QpdfInputReader.ReadAdmittedAsync(input, 0L, (int)input.Length, governor, cancellationToken).ConfigureAwait(false);
            JsonDocumentOptions options = new()
            {
                MaxDepth = 128,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            };
            try
            {
                JsonImportMemory.AdmitDocument(memory, bytes, governor, cancellationToken);
                parsed = JsonDocument.Parse(bytes, options);
            }
            catch (JsonException ex)
            {
                throw new QpdfParseException("Invalid qpdf JSON: " + ex.Message + ".", 0L, null, ex);
            }
        }
        finally
        {
            try { await input.DisposeAsync().ConfigureAwait(false); }
            catch { parsed?.Dispose(); throw; }
        }
        return parsed ?? throw new InvalidOperationException("Missing parsed JSON document.");
    }
    internal static async ValueTask<ImportedModel> BuildCreateModelAsync(
        IQpdfHost host,
        JsonDocument document,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope memory,
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

        memory.CheckOwner(governor);
        JsonImportMemory.AdmitModel(memory, qpdf, governor, cancellationToken);
        PdfVersion header = ReadHeaderVersion(qpdf[0], true, governor);
        Dictionary<int, QpdfObject> objects = new(qpdf[1].GetPropertyCount());
        PdfDictionary? trailer = null;
        bool sawTrailer = false;
        foreach (JsonProperty property in qpdf[1].EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (property.Name == "trailer")
            {
                sawTrailer = true;
                trailer = ReadTrailer(property.Value, governor, cancellationToken);
                continue;
            }

            int number = ParseObjectKey(property.Name);
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("qpdf JSON object must be a dictionary.", 0L, null);
            }

            objects[number] = await ReadObjectAsync(host, property.Value, true, false, null, limits, governor, memory, cancellationToken).ConfigureAwait(false);
        }

        if (!sawTrailer)
        {
            throw new QpdfParseException("qpdf JSON trailer was not seen.", 0L, null);
        }

        return new ImportedModel(header, objects, trailer ?? new PdfDictionary(new Dictionary<PdfName, QpdfObject>()));
    }

    internal static async ValueTask<ImportedModel> BuildUpdateModelAsync(
        IQpdfHost host,
        JsonDocument document,
        QpdfDocument pdf,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope memory,
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

        if (pdf is null)
        {
            throw new ArgumentNullException(nameof(pdf));
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

        memory.CheckOwner(governor);
        JsonImportMemory.AdmitModel(memory, qpdf, governor, cancellationToken);
        PdfVersion? updateVersion = ReadUpdateMeta(qpdf[0], governor);
        DocumentEngine engine = pdf.Engine;
        memory.Reserve(checked(128L + 64L * engine.XRefEntries.Count));
        Dictionary<int, QpdfObject> merged = new(engine.XRefEntries.Count);
        foreach (KeyValuePair<int, XrefEntry> entry in engine.XRefEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Value.Kind == XrefEntryKind.Free)
            {
                continue;
            }

            governor.NoteOperation();
            merged[entry.Key] = engine.Load(entry.Key);
        }

        PdfDictionary trailer = engine.Trailer;
        foreach (JsonProperty property in qpdf[1].EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (property.Name == "trailer")
            {
                trailer = ReadTrailer(property.Value, governor, cancellationToken);
                continue;
            }

            int number = ParseObjectKey(property.Name);
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("qpdf JSON object must be a dictionary.", 0L, null);
            }

            bool existingIsStream = merged.TryGetValue(number, out QpdfObject? existing) && existing is PdfStream;
            PdfStream? existingStream = existing as PdfStream;
            merged[number] = await ReadObjectAsync(host, property.Value, false, existingIsStream, existingStream, limits, governor, memory, cancellationToken).ConfigureAwait(false);
        }

        return new ImportedModel(updateVersion ?? pdf.HeaderVersion, merged, trailer);
    }

    private static PdfVersion? ReadUpdateMeta(JsonElement meta, ResourceGovernor governor)
    {
        PdfVersion? version = null;
        bool sawJsonVersion = false;
        foreach (JsonProperty property in meta.EnumerateObject())
        {
            governor.CheckCancellation();
            if (property.Name == "jsonversion")
            {
                sawJsonVersion = true;
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out int number) || number != 2)
                {
                    throw new QpdfParseException("qpdf JSON version must be numeric value 2.", 0L, null);
                }

                continue;
            }

            if (property.Name == "pdfversion")
            {
                if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not string text)
                {
                    throw new QpdfParseException("qpdf JSON pdfversion must be a string.", 0L, null);
                }

                version = ParsePdfVersion(text, governor);
                continue;
            }

            if (property.Name == "pushedinheritedpageresources")
            {
                if (property.Value.ValueKind != JsonValueKind.True && property.Value.ValueKind != JsonValueKind.False)
                {
                    throw new QpdfParseException("qpdf JSON pushedinheritedpageresources must be a boolean.", 0L, null);
                }

                if (property.Value.ValueKind == JsonValueKind.True)
                {
                    throw new QpdfParseException("qpdf JSON pushedinheritedpageresources is outside the narrow profile.", 0L, null);
                }

                continue;
            }

            if (property.Name == "calledgetallpages")
            {
                if (property.Value.ValueKind != JsonValueKind.True && property.Value.ValueKind != JsonValueKind.False)
                {
                    throw new QpdfParseException("qpdf JSON calledgetallpages must be a boolean.", 0L, null);
                }

                continue;
            }
        }

        if (!sawJsonVersion)
        {
            throw new QpdfParseException("qpdf JSON jsonversion was not seen.", 0L, null);
        }

        return version;
    }

    internal static PdfVersion ReadHeaderVersion(JsonElement meta, bool required, ResourceGovernor governor)
    {
        if (meta.ValueKind != JsonValueKind.Object)
        {
            throw new QpdfParseException("qpdf JSON qpdf[0] must be a dictionary.", 0L, null);
        }

        bool sawJsonVersion = false;
        bool sawPdfVersion = false;
        PdfVersion? header = null;
        foreach (JsonProperty property in meta.EnumerateObject())
        {
            governor.CheckCancellation();
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

                header = ParsePdfVersion(text, governor);
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

        return header ?? governor.OwnVersion(1, 4);
    }

    internal static PdfVersion ParsePdfVersion(string text, ResourceGovernor governor)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        int separator = text.IndexOf('.');
        if (separator < 0 || text.AsSpan(separator + 1).Contains('.'))
        { throw new QpdfParseException("Invalid PDF version in qpdf JSON (must be x.y).", 0L, null); }
        if (!TryParseVersionPart(text.AsSpan(0, separator), out int major)
            || !TryParseVersionPart(text.AsSpan(separator + 1), out int minor))
        { throw new QpdfParseException("Invalid PDF version in qpdf JSON (must be x.y).", 0L, null); }

        if ((major == 1 && minor > 7) || (major == 2 && minor != 0) || (major != 1 && major != 2))
        {
            throw new QpdfParseException("Unsupported PDF version " + major.ToString(CultureInfo.InvariantCulture) + "." + minor.ToString(CultureInfo.InvariantCulture) + ".", 0L, null);
        }

        return governor.OwnVersion(major, minor);
    }

    private static bool TryParseVersionPart(ReadOnlySpan<char> text, out int value)
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

        if (!TryReadReference(key.AsSpan(4), out int number, out int generation))
        { throw new QpdfParseException("qpdf JSON object key should be trailer or obj:n n R.", 0L, null); }
        if (generation != 0)
        { throw new QpdfParseException("qpdf JSON object generations other than 0 are outside the narrow profile.", 0L, null); }

        return number;
    }

    private static PdfDictionary ReadTrailer(JsonElement value, ResourceGovernor governor, CancellationToken token)
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
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (property.Name == "value")
            {
                sawValue = true;
                QpdfObject decoded = DecodeValue(property.Value, 0, governor, token);
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
        PdfStream? existingStream,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope memory,
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
            governor.NoteOperation();
            if (property.Name == "value")
            {
                sawValue = true;
                QpdfObject decoded = DecodeValue(property.Value, 0, governor, cancellationToken);
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
                    governor.NoteOperation();
                    if (inner.Name == "dict")
                    {
                        QpdfObject decoded = DecodeValue(inner.Value, 0, governor, cancellationToken);
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

                        // Model admission covers decoded text and base64 overlap.
                        try { inlineData = Convert.FromBase64String(text); }
                        catch (FormatException ex)
                        { throw new QpdfParseException("qpdf JSON stream data must be base64.", 0L, null, ex); }
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
                data = await ReadSidecarAsync(host, datafile ?? throw new QpdfParseException("qpdf JSON stream datafile must be a string containing a file name.", 0L, null), limits, governor, memory, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return existingStream is null
                    ? PdfStream.FromOwnedData((PdfDictionary)streamDict, [])
                    : existingStream.WithDictionary((PdfDictionary)streamDict);
            }

            return PdfStream.FromOwnedData((PdfDictionary)streamDict, data);
        }

        return result ?? PdfNull.Instance;
    }

    private static async ValueTask<byte[]> ReadSidecarAsync(
        IQpdfHost host,
        string name,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        if (name.Length == 0 || name.Length > QpdfPath.MaxLength || name.Contains((char)0))
        {
            throw new QpdfParseException("qpdf JSON stream datafile is not a valid logical path.", 0L, null);
        }

        QpdfPath path = QpdfPath.From(name);
        IQpdfInput input = await GovernedHost.OpenInputAsync(host, path, governor, cancellationToken).ConfigureAwait(false);
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

            memory.Reserve(checked(input.Length + 32L));
            byte[] bytes = await QpdfInputReader.ReadAdmittedAsync(input, 0L, (int)input.Length, governor, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
        finally
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }
    }
    private static QpdfObject DecodeValue(JsonElement value, int depth, ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (depth > governor.MaxNestingDepth)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth, governor.MaxNestingDepth); }

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
                return DecodeNumber(value, governor);
            case JsonValueKind.String:
                return DecodeString(value.GetString() ?? string.Empty, governor, token);
            case JsonValueKind.Array:
            {
                QpdfObject[] items = new QpdfObject[value.GetArrayLength()];
                int index = 0;
                foreach (JsonElement item in value.EnumerateArray())
                { items[index++] = DecodeValue(item, depth + 1, governor, token); }
                return PdfArray.FromOwnedItems(items);
            }

            case JsonValueKind.Object:
            {
                Dictionary<PdfName, QpdfObject> entries = new(value.GetPropertyCount());
                foreach (JsonProperty property in value.EnumerateObject())
                { entries[DecodeName(property.Name, governor, token)] = DecodeValue(property.Value, depth + 1, governor, token); }
                return PdfDictionary.FromOwnedEntries(entries);
            }

            default:
                throw new QpdfParseException("Unsupported qpdf JSON value.", 0L, null);
        }
    }

    private static QpdfObject DecodeNumber(JsonElement value, ResourceGovernor governor)
    {
        string raw = value.GetRawText();
        governor.NoteOperation();
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

    private static QpdfObject DecodeString(string text, ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (IsIndirectReference(text, out int number, out int generation)) { return new PdfReference(number, generation); }
        ReadOnlySpan<char> span = text.AsSpan();
        if (span.StartsWith("u:", StringComparison.Ordinal))
        { return PdfByteString.FromOwnedBytes(EncodeTextString(span[2..], governor, token), false); }
        if (span.StartsWith("b:", StringComparison.Ordinal))
        {
            ReadOnlySpan<char> hex = span[2..];
            if (hex.Length % 2 != 0)
            { throw new QpdfParseException("qpdf JSON binary string must have an even hex length.", 0L, null); }
            byte[] bytes = new byte[hex.Length / 2];
            for (int index = 0; index < bytes.Length; index++)
            {
                CheckTextCancellation(index, governor, token);
                char high = hex[index * 2], low = hex[index * 2 + 1];
                if (!IsHexChar(high) || !IsHexChar(low))
                { throw new QpdfParseException("qpdf JSON binary string must be hexadecimal.", 0L, null); }
                bytes[index] = (byte)((HexValue(high) << 4) | HexValue(low));
            }
            return PdfByteString.FromOwnedBytes(bytes, true);
        }
        if (span.StartsWith("/", StringComparison.Ordinal))
        { return PdfName.FromOwnedBytes(EncodeUtf8(span[1..], governor, token)); }
        if (span.StartsWith("n:", StringComparison.Ordinal))
        {
            if (span.Length < 3 || span[2] != '/')
            { throw new QpdfParseException("qpdf JSON unrecognized string value.", 0L, null); }
            return PdfName.FromOwnedBytes(ResolveNameSyntax(span[3..], governor, token));
        }
        throw new QpdfParseException("qpdf JSON unrecognized string value.", 0L, null);
    }

    private static PdfName DecodeName(string key, ResourceGovernor governor, CancellationToken token)
    {
        ReadOnlySpan<char> span = key.AsSpan();
        if (span.StartsWith("n:", StringComparison.Ordinal))
        {
            if (span.Length < 3 || span[2] != '/')
            { throw new QpdfParseException("qpdf JSON unrecognized name value.", 0L, null); }
            return PdfName.FromOwnedBytes(ResolveNameSyntax(span[3..], governor, token));
        }
        if (span.StartsWith("/", StringComparison.Ordinal)) { span = span[1..]; }
        return PdfName.FromOwnedBytes(EncodeUtf8(span, governor, token));
    }

    internal static bool IsIndirectReference(string text, out int number, out int generation)
        => TryReadReference(text.AsSpan(), out number, out generation);

    private static bool TryReadReference(ReadOnlySpan<char> text, out int number, out int generation)
    {
        number = 0; generation = 0;
        if (text.IsEmpty || text[0] == ' ' || text[^1] == ' ') { return false; }
        int split = text.IndexOf(' ');
        if (split <= 0 || !TryParseDigits(text[..split], out number) || number <= 0) { return false; }
        ReadOnlySpan<char> rest = text[(split + 1)..].TrimStart(' ');
        split = rest.IndexOf(' ');
        return split > 0 && TryParseDigits(rest[..split], out generation)
            && rest[(split + 1)..].TrimStart(' ').SequenceEqual("R");
    }

    private static bool TryParseDigits(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        if (text.IsEmpty || text.Length > 10) { return false; }
        long parsed = 0L;
        foreach (char character in text)
        {
            if (character < '0' || character > '9') { return false; }
            parsed = parsed * 10L + character - '0';
        }
        if (parsed > int.MaxValue) { return false; }
        value = (int)parsed; return true;
    }

    private static byte[] EncodeUtf8(ReadOnlySpan<char> text, ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        byte[] result = new byte[Encoding.UTF8.GetByteCount(text)];
        Encoding.UTF8.GetBytes(text, result);
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        return result;
    }

    private static byte[] ResolveNameSyntax(ReadOnlySpan<char> body, ResourceGovernor governor, CancellationToken token)
    {
        byte[] source = EncodeUtf8(body, governor, token);
        int written = 0;
        for (int index = 0; index < source.Length; index++)
        {
            CheckTextCancellation(written, governor, token);
            byte value = source[index];
            if (value == '#')
            {
                if (index + 2 >= source.Length || !IsHexChar((char)source[index + 1]) || !IsHexChar((char)source[index + 2]))
                { throw new QpdfParseException("qpdf JSON name has an invalid escape.", 0L, null); }
                value = (byte)((HexValue((char)source[index + 1]) << 4) | HexValue((char)source[index + 2]));
                index += 2;
            }
            source[written++] = value;
        }
        return written == source.Length ? source : source.AsSpan(0, written).ToArray();
    }

    private static void CheckTextCancellation(int index, ResourceGovernor governor, CancellationToken token)
    {
        if ((index & 255) != 0) { return; }
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
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

    private static byte[] EncodeTextString(ReadOnlySpan<char> text, ResourceGovernor governor, CancellationToken token)
    {
        bool pdfDoc = true;
        for (int index = 0; index < text.Length; index++)
        {
            CheckTextCancellation(index, governor, token);
            int unit = char.IsHighSurrogate(text[index]) && index + 1 < text.Length
                ? char.ConvertToUtf32(text[index], text[index + 1]) : text[index];
            if (!TryEncodePdfDoc(unit, out _)) { pdfDoc = false; break; }
        }
        byte[] result = new byte[pdfDoc ? text.Length : checked(text.Length * 2 + 2)];
        if (!pdfDoc) { result[0] = 0xFE; result[1] = 0xFF; }
        for (int index = 0; index < text.Length; index++)
        {
            CheckTextCancellation(index, governor, token);
            if (pdfDoc) { TryEncodePdfDoc(text[index], out result[index]); }
            else { result[index * 2 + 2] = (byte)(text[index] >> 8); result[index * 2 + 3] = (byte)text[index]; }
        }
        return result;
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
        ParserMemoryScope memory,
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
        if ((long)model.Objects.Count > limits.MaxContainerSize)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), model.Objects.Count, limits.MaxContainerSize);
        }

        if (model.Trailer.TryGet(PdfName.FromAscii("Encrypt"), out QpdfObject? encrypt) && encrypt is not null && encrypt is not PdfNull)
        {
            throw new QpdfCryptoException("/Encrypt");
        }

        if (!model.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject? rootRaw) || rootRaw is null || rootRaw is PdfNull)
        {
            throw new QpdfParseException("Imported trailer must declare /Root.", 0L, null);
        }

        memory.CheckOwner(governor);
        memory.Reserve(checked(512L + 192L * model.Objects.Count));
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

        GovernedCollections.SortNumbers(memory, governor, rest);
        for (int i = 0; i < rest.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
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
            governor.NoteOperation();
            mapped[renumber[entry.Key]] = Writing.ObjectRemap.RemapImported(entry.Value, renumber, memory, governor, cancellationToken);
        }

        if (infoDirect && infoDict is not null)
        {
            mapped[renumber[-2]] = Writing.ObjectRemap.RemapImported(infoDict, renumber, memory, governor, cancellationToken);
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
        GovernedCollections.SortNumbers(memory, governor, numbers);
        foreach (int number in numbers)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            ordered.Add(new Writing.NumberedObject(number, mapped[number]));
        }

        int pages = CountPages(mapped, governor, limits, cancellationToken);
        return new RenumberedModel(ordered, infoNew, pages);
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
        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        List<QpdfObject> stack = Syntax.ParserContainers.CreateList<QpdfObject>(traversal);
        Syntax.ParserContainers.AddItem(traversal, stack, pagesDict);
        traversal.Reserve(96L);
        HashSet<QpdfObject> visited = new(ReferenceEqualityComparer.Instance);
        Syntax.ParserContainers.AddSetItem(traversal, visited, pagesDict, IntPtr.Size);
        int depth = 0;
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (depth > limits.MaxNestingDepth)
            {
                return count;
            }

            depth += 1;
            List<QpdfObject> next = Syntax.ParserContainers.CreateList<QpdfObject>(traversal);
            foreach (QpdfObject node in stack)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (node is not PdfDictionary dictionary)
                {
                    continue;
                }

                if (dictionary.TryGet(PdfName.FromAscii("Type"), out QpdfObject? typeRaw) && typeRaw is PdfName type && type.RawSpan.SequenceEqual("Page"u8))
                {
                    count += 1;
                    continue;
                }

                if (dictionary.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw) && kidsRaw is not null && kidsRaw is not PdfNull)
                {
                    QpdfObject kids = ResolvePool(kidsRaw, pool);
                    if (kids is PdfArray array)
                    {
                        for (int index = 0; index < array.Count; index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                            QpdfObject target = ResolvePool(array[index], pool);
                            if (target is PdfDictionary && Syntax.ParserContainers.AddSetItem(traversal, visited, target, IntPtr.Size))
                            { Syntax.ParserContainers.AddItem(traversal, next, target); }
                        }
                    }
                }
            }

            Syntax.ParserContainers.ReleaseList(traversal, stack);
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
