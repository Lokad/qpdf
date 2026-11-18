using System.Globalization;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Json;

/// <summary>Imported objects and original packing in the normalized ID space.</summary>
internal sealed record ImportedModel(PdfVersion HeaderVersion, Dictionary<int, QpdfObject> Objects,
    PdfDictionary Trailer, HashSet<int>? CompressedObjects)
{
    internal IReadOnlyDictionary<(int Number, int Generation), int>? OriginalPairs { get; init; }
}

/// <summary>
/// qpdf JSON to PDF object model import. Mirrors the upstream import reactor:
/// only the top-level qpdf array matters, object keys are strict, stream
/// payloads come from inline base64 or host sidecar files, and unknown keys
/// are ignored for forward compatibility.
/// </summary>
internal static partial class JsonImport
{
    private const string ReplacementAlias = "The JSON replacement target must differ from every patch and sidecar input.";

    internal static async ValueTask<JsonDocument> ReadJsonAsync(
        IQpdfHost host,
        QpdfPath jsonPath,
        string? replacementIdentity,
        SplitInputAliases? splitInputs,
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
            CheckReplacementAlias(input, replacementIdentity);
            splitInputs?.Remember(jsonPath, input.Path, input.Identity);
            byte[] bytes = await QpdfInputReader.ReadWholeAdmittedAsync(input, limits, memory, governor, cancellationToken).ConfigureAwait(false);
            JsonDocumentOptions options = new()
            {
                MaxDepth = JsonImportMemory.SyntaxMaxDepth,
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

    private static void CheckReplacementAlias(IQpdfInput input, string? identity)
    {
        if (identity is not null && string.Equals(input.Identity, identity, StringComparison.Ordinal))
        { throw new QpdfParseException(ReplacementAlias, 0L, null); }
    }
    internal static async ValueTask<ImportedModel> BuildCreateModelAsync(
        IQpdfHost host,
        JsonDocument document,
        bool retainOriginalPairs,
        SplitInputAliases? splitInputs,
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
        JsonElement qpdf = ReadEnvelope(document, memory, governor, cancellationToken);
        Metadata metadata = ReadMetadata(qpdf[0], true, governor);
        PdfVersion header = metadata.Version ?? throw new InvalidOperationException("Missing validated PDF version.");
        using ParserMemoryScope bindingMemory = ParserMemoryScope.Create(governor);
        Dictionary<(int Number, int Generation), int> bindings = ReadObjectBindings(qpdf[1], null, null,
            retainOriginalPairs ? memory : bindingMemory, governor, cancellationToken);
        Dictionary<int, QpdfObject> objects = new(bindings.Count);
        using SidecarReader sidecars = SidecarReader.Create(host, limits, governor, memory, bindingMemory, null, splitInputs);
        PdfDictionary? trailer = null;
        foreach (JsonProperty property in qpdf[1].EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            string propertyName = ReadPropertyName(property);
            if (propertyName == "trailer")
            {
                trailer = (PdfDictionary)Writing.ObjectRemap.RemapImported(ReadTrailer(property.Value, limits, governor, memory, cancellationToken),
                    bindings, memory, governor, cancellationToken);
                continue;
            }

            int number = bindings[ParseObjectKey(propertyName)];
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("qpdf JSON object must be a dictionary.", 0L, null);
            }

            QpdfObject value = await ReadObjectAsync(sidecars, property.Value, null, limits, governor, memory, cancellationToken).ConfigureAwait(false);
            objects[number] = Writing.ObjectRemap.RemapImported(value, bindings, memory, governor, cancellationToken);
        }

        if (trailer is null)
        {
            throw new QpdfParseException("qpdf JSON trailer was not seen.", 0L, null);
        }

        return new ImportedModel(header, objects, trailer, null) { OriginalPairs = retainOriginalPairs ? bindings : null };
    }

    internal static async ValueTask<ImportedModel> BuildUpdateModelAsync(
        IQpdfHost host,
        JsonDocument document,
        DocumentEngine engine,
        QpdfDocument.VersionParts header,
        bool preserveEncryption,
        bool preserveObjectStreams,
        bool retainOriginalPairs,
        (QpdfPath Path, string Identity)? replacementTarget,
        SplitInputAliases? splitInputs,
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

        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
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
        JsonElement qpdf = ReadEnvelope(document, memory, governor, cancellationToken);
        Metadata metadata = ReadMetadata(qpdf[0], false, governor);
        using ParserMemoryScope bindingMemory = ParserMemoryScope.Create(governor);
        PageTreePreparation.Prepared? prepared = metadata.CalledAllPages || metadata.PushedInherited
            ? PageTreePreparation.Prepare(engine, metadata.PushedInherited, limits, memory, bindingMemory, governor, cancellationToken) : null;
        Dictionary<(int Number, int Generation), int> bindings = ReadObjectBindings(qpdf[1], engine.XRefEntries,
            prepared?.AddedReferences, retainOriginalPairs ? memory : bindingMemory, governor, cancellationToken);
        memory.Reserve(checked(128L + 64L * bindings.Count));
        Dictionary<int, QpdfObject> merged = new(bindings.Count);
        HashSet<int>? compressed = preserveObjectStreams ? ParserContainers.CreateSet<int>(memory) : null;
        foreach (KeyValuePair<int, XrefEntry> entry in engine.XRefEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Value.Kind == XrefEntryKind.Free)
            {
                continue;
            }

            governor.NoteOperation();
            int number = bindings[(entry.Key, entry.Value.Kind == XrefEntryKind.Compressed ? 0 : entry.Value.Second)];
            if (entry.Value.Kind == XrefEntryKind.Compressed && compressed is not null)
            { ParserContainers.AddSetItem(memory, compressed, number, sizeof(int)); }
            QpdfObject value = prepared is not null && prepared.Objects.TryGetValue(entry.Key, out QpdfObject? replacement)
                ? replacement : engine.Load(entry.Key);
            merged[number] = Writing.ObjectRemap.NormalizeSource(value, engine.XRefEntries, bindings,
                prepared?.AddedReferences, memory, governor, cancellationToken);
        }

        if (prepared is not null)
        {
            foreach (PdfReference reference in prepared.AddedReferences)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                int number = bindings[(reference.ObjectNumber, reference.Generation)];
                merged[number] = Writing.ObjectRemap.NormalizeSource(prepared.Objects[reference.ObjectNumber], engine.XRefEntries,
                    bindings, prepared.AddedReferences, memory, governor, cancellationToken);
            }
        }
        PdfDictionary trailer = (PdfDictionary)Writing.ObjectRemap.NormalizeSource(prepared?.Trailer ?? engine.Trailer,
            engine.XRefEntries, bindings, prepared?.AddedReferences, memory, governor, cancellationToken);
        PdfDictionary originalTrailer = trailer;
        using SidecarReader sidecars = SidecarReader.Create(host, limits, governor, memory, bindingMemory, replacementTarget, splitInputs);
        QpdfObject originalEncryption = PdfNull.Instance;
        if (engine.Security is not null && PdfDictionaryLookup.TryGet(trailer, "Encrypt"u8, governor, cancellationToken, out QpdfObject declaration))
        {
            originalEncryption = declaration is PdfReference reference
                ? merged.GetValueOrDefault(reference.ObjectNumber, PdfNull.Instance) : declaration;
        }
        foreach (JsonProperty property in qpdf[1].EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            string propertyName = ReadPropertyName(property);
            if (propertyName == "trailer")
            {
                trailer = (PdfDictionary)Writing.ObjectRemap.RemapImported(ReadTrailer(property.Value, limits, governor, memory, cancellationToken),
                    bindings, memory, governor, cancellationToken);
                continue;
            }

            int number = bindings[ParseObjectKey(propertyName)];
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("qpdf JSON object must be a dictionary.", 0L, null);
            }

            merged.TryGetValue(number, out QpdfObject? existing);
            QpdfObject value = await ReadObjectAsync(sidecars, property.Value, existing as PdfStream, limits, governor, memory, cancellationToken).ConfigureAwait(false);
            merged[number] = Writing.ObjectRemap.RemapImported(value, bindings, memory, governor, cancellationToken);
        }

        ImportedModel result = new(metadata.Version ?? governor.PrivateVersion(header.Major, header.Minor), merged, trailer, compressed)
        { OriginalPairs = retainOriginalPairs ? bindings : null };
        return PrepareUpdateEncryption(result, originalTrailer, originalEncryption, engine.Security is not null,
            preserveEncryption, governor, cancellationToken);
    }

    private static JsonElement ReadEnvelope(JsonDocument document, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
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
        return qpdf;
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

        return governor.PrivateVersion(major, minor);
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

    private static (int Number, int Generation) ParseObjectKey(string key)
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
        return (number, generation);
    }

    private static PdfDictionary ReadTrailer(JsonElement value, QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new QpdfParseException("qpdf JSON object must be a dictionary.", 0L, null);
        }

        PdfDictionary? trailer = null;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            string propertyName = ReadPropertyName(property);
            if (propertyName == "value")
            {
                QpdfObject decoded = DecodeValue(property.Value, 0, limits, governor, memory, token);
                if (decoded is not PdfDictionary dictionary)
                {
                    throw new QpdfParseException("qpdf JSON trailer value must be a dictionary.", 0L, null);
                }

                trailer = dictionary;
                continue;
            }

            if (propertyName == "stream")
            {
                throw new QpdfParseException("qpdf JSON trailer may not be a stream.", 0L, null);
            }
        }

        if (trailer is null)
        {
            throw new QpdfParseException("qpdf JSON trailer is missing value.", 0L, null);
        }

        return trailer;
    }

    private static async ValueTask<QpdfObject> ReadObjectAsync(
        SidecarReader sidecars,
        JsonElement value,
        PdfStream? existingStream,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        bool sawValue = false;
        bool sawStream = false;
        QpdfObject? result = null;
        PdfDictionary? streamDict = null;
        string? datafile = null;
        byte[]? inlineData = null;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            string propertyName = ReadPropertyName(property);
            if (propertyName == "value")
            {
                sawValue = true;
                QpdfObject decoded = DecodeValue(property.Value, 0, limits, governor, memory, cancellationToken);
                if (decoded is PdfReference)
                {
                    throw new QpdfParseException("qpdf JSON object value may not be an indirect object reference.", 0L, null);
                }

                result = decoded;
                continue;
            }

            if (propertyName == "stream")
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
                    string innerName = ReadPropertyName(inner);
                    if (innerName == "dict")
                    {
                        QpdfObject decoded = DecodeValue(inner.Value, 0, limits, governor, memory, cancellationToken);
                        if (decoded is not PdfDictionary dictionary)
                        {
                            throw new QpdfParseException("qpdf JSON stream dict must be a dictionary.", 0L, null);
                        }

                        streamDict = dictionary;
                        continue;
                    }

                    if (innerName == "data")
                    {
                        if (inner.Value.ValueKind != JsonValueKind.String || ReadString(inner.Value) is not string text)
                        {
                            throw new QpdfParseException("qpdf JSON stream data must be a string.", 0L, null);
                        }

                        // Model admission covers decoded text and base64 overlap.
                        try { inlineData = Convert.FromBase64String(text); }
                        catch (FormatException ex)
                        { throw new QpdfParseException("qpdf JSON stream data must be base64.", 0L, null, ex); }
                        continue;
                    }

                    if (innerName == "datafile")
                    {
                        if (inner.Value.ValueKind != JsonValueKind.String || ReadString(inner.Value) is not string name)
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

            if (inlineData is not null && datafile is not null)
            {
                throw new QpdfParseException(existingStream is null
                    ? "qpdf JSON new stream must have exactly one of data or datafile."
                    : "qpdf JSON stream may have at most one of data or datafile.", 0L, null);
            }

            if (inlineData is not null)
            {
                return PdfStream.FromOwnedData(streamDict, inlineData);
            }
            if (datafile is not null)
            {
                byte[] data = await sidecars.ReadAsync(datafile, cancellationToken).ConfigureAwait(false);
                return PdfStream.FromOwnedData(streamDict, data);
            }
            if (existingStream is not null)
            {
                return existingStream.WithDictionary(streamDict);
            }
            throw new QpdfParseException("qpdf JSON new stream must have exactly one of data or datafile.", 0L, null);
        }

        return result ?? PdfNull.Instance;
    }

    private sealed class SidecarReader : IDisposable
    {
        private const int Capacity = 8;
        private readonly IQpdfHost _host;
        private readonly QpdfLimits _limits;
        private readonly ResourceGovernor _governor;
        private readonly ParserMemoryScope _payloadMemory;
        private readonly ParserMemoryScope _lookupMemory;
        private readonly (QpdfPath Path, string Identity)? _replacement;
        private readonly SplitInputAliases? _splitInputs;
        private (string Name, byte[] Bytes)[]? _entries;
        private int _next;

        private SidecarReader(IQpdfHost host, QpdfLimits limits, ResourceGovernor governor,
            ParserMemoryScope payloadMemory, ParserMemoryScope lookupMemory, (QpdfPath Path, string Identity)? replacement,
            SplitInputAliases? splitInputs)
        {
            _host = host; _limits = limits; _governor = governor;
            _payloadMemory = payloadMemory; _lookupMemory = lookupMemory; _replacement = replacement;
            _splitInputs = splitInputs;
        }

        internal static SidecarReader Create(IQpdfHost host, QpdfLimits limits, ResourceGovernor governor,
            ParserMemoryScope payloadMemory, ParserMemoryScope lookupMemory, (QpdfPath Path, string Identity)? replacement,
            SplitInputAliases? splitInputs)
        {
            lookupMemory.Reserve(112L);
            return new SidecarReader(host, limits, governor, payloadMemory, lookupMemory, replacement, splitInputs);
        }

        internal async ValueTask<byte[]> ReadAsync(string name, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); _governor.CheckCancellation();
            if (name.Length == 0 || name.Length > QpdfPath.MaxLength || name.Contains((char)0))
            {
                throw new QpdfParseException("qpdf JSON stream datafile is not a valid logical path.", 0L, null);
            }
            if (_replacement is { } replacement && string.Equals(replacement.Path.Value, name, StringComparison.Ordinal))
            { throw new QpdfParseException(ReplacementAlias, 0L, null); }
            if (_entries is not null)
            {
                foreach (var entry in _entries)
                {
                    if (string.Equals(entry.Name, name, StringComparison.Ordinal)) { return entry.Bytes; }
                }
            }
            else
            {
                _lookupMemory.Reserve(32L + Capacity * 2L * IntPtr.Size);
                _entries = new (string, byte[])[Capacity];
            }

            byte[] bytes = await ReadPayloadAsync(QpdfPath.From(name), token).ConfigureAwait(false);
            // The loaded bytes are immutable and funded by the imported model.
            // Replacing an index entry leaves existing streams and owners intact.
            _entries[_next] = (name, bytes);
            _next = (_next + 1) % Capacity;
            return bytes;
        }

        private async ValueTask<byte[]> ReadPayloadAsync(QpdfPath path, CancellationToken token)
        {
            IQpdfInput input = await GovernedHost.OpenInputAsync(_host, path, _governor, token).ConfigureAwait(false);
            try
            {
                CheckReplacementAlias(input, _replacement?.Identity);
                _splitInputs?.Remember(path, input.Path, input.Identity);
                return await QpdfInputReader.ReadWholeAdmittedAsync(input, _limits, _payloadMemory, _governor, token).ConfigureAwait(false);
            }
            finally { await input.DisposeAsync().ConfigureAwait(false); }
        }

        public void Dispose() => _entries = null;
    }
    private static string? ReadString(JsonElement value)
    {
        try { return value.GetString(); }
        catch (InvalidOperationException ex) when (value.ValueKind == JsonValueKind.String)
        { throw new QpdfParseException("Invalid string encoding in qpdf JSON.", 0L, null, ex); }
    }

    private static string ReadPropertyName(JsonProperty property)
    {
        try { return property.Name; }
        catch (InvalidOperationException ex)
        { throw new QpdfParseException("Invalid string encoding in qpdf JSON.", 0L, null, ex); }
    }

    private static QpdfObject DecodeValue(JsonElement value, int depth, QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
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
                return DecodeNumber(value, governor, memory, token);
            case JsonValueKind.String:
                return DecodeString(ReadString(value) ?? string.Empty, governor, token);
            case JsonValueKind.Array:
            {
                int count = value.GetArrayLength();
                CheckContainerSize(count, limits);
                QpdfObject[] items = new QpdfObject[count];
                int index = 0;
                foreach (JsonElement item in value.EnumerateArray())
                { items[index++] = DecodeValue(item, depth + 1, limits, governor, memory, token); }
                return PdfArray.FromOwnedItems(items);
            }

            case JsonValueKind.Object:
            {
                // Like the PDF parser, check stored entries before each pair.
                // Repeated/escaped spellings can decode to the same name; raw
                // JSON property count must not determine their capacity or limit.
                int capacity = (int)Math.Min(value.GetPropertyCount(), limits.MaxContainerSize);
                Dictionary<PdfName, QpdfObject> entries = new(capacity);
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    CheckContainerSize(entries.Count + 1L, limits);
                    entries[DecodeName(ReadPropertyName(property), governor, token)] = DecodeValue(property.Value, depth + 1, limits, governor, memory, token);
                }
                return PdfDictionary.FromOwnedEntries(entries);
            }

            default:
                throw new QpdfParseException("Unsupported qpdf JSON value.", 0L, null);
        }
    }

    private static void CheckContainerSize(long count, QpdfLimits limits)
    {
        if (count > limits.MaxContainerSize)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), count, limits.MaxContainerSize); }
    }

    private static QpdfObject DecodeString(string text, ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (IsIndirectReference(text, out int number, out int generation))
        {
            return new PdfReference(number, generation);
        }
        ReadOnlySpan<char> span = text.AsSpan();
        if (span.StartsWith("u:", StringComparison.Ordinal))
        {
            ReadOnlySpan<char> value = span[2..];
            byte[] bytes = new byte[PdfText.GetEncodedLength(value, governor, token)];
            PdfText.Encode(value, bytes, governor, token);
            return PdfByteString.FromOwnedBytes(bytes, false);
        }
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
        { return PdfName.FromOwnedBytes(EncodeNameUtf8(span[1..], governor, token)); }
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
        return PdfName.FromOwnedBytes(EncodeNameUtf8(span, governor, token));
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

    private static byte[] EncodeNameUtf8(ReadOnlySpan<char> text, ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        for (int index = 0; index < text.Length; index++)
        {
            CheckTextCancellation(index, governor, token);
            if (text[index] == '\0') { throw new QpdfParseException("PDF names must not contain NUL.", 0L, null); }
        }
        byte[] result = new byte[Encoding.UTF8.GetByteCount(text)];
        Encoding.UTF8.GetBytes(text, result);
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        return result;
    }

    private static byte[] ResolveNameSyntax(ReadOnlySpan<char> body, ResourceGovernor governor, CancellationToken token)
    {
        byte[] source = EncodeNameUtf8(body, governor, token);
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
            if (value == 0) { throw new QpdfParseException("PDF names must not contain NUL.", 0L, null); }
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

    internal sealed record RenumberedModel(
        List<Writing.NumberedObject> Model,
        int? InfoNumber,
        int MetadataNumber,
        int PageCount,
        Dictionary<int, QpdfObject> Objects,
        IReadOnlyDictionary<int, int> Mapping,
        HashSet<PdfDictionary>? DisabledSignatureWidgets);

    internal static RenumberedModel Renumber(
        ImportedModel model,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        ParserMemoryScope memory,
        ParserMemoryScope? normalizationMemory,
        bool coalesceContents,
        out IReadOnlyList<PageLocation>? normalizationPages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
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
        CheckObjectCount(model, limits);

        if (model.Trailer.TryGet(PdfName.FromAscii("Encrypt"), out QpdfObject? encrypt) && encrypt is not null && encrypt is not PdfNull)
        {
            throw new QpdfCryptoException("/Encrypt");
        }

        if (!model.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject? rootRaw) || rootRaw is null || rootRaw is PdfNull)
        {
            throw new QpdfParseException("Imported trailer must declare /Root.", 0L, null);
        }

        memory.CheckOwner(governor);
        memory.Reserve(checked(536L + 192L * model.Objects.Count));
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

        if (rootOld == -1)
        {
            mapped[1] = Writing.ObjectRemap.RemapImported(root, renumber, memory, governor, cancellationToken);
        }

        // Validate the published graph, where dangling imported references have become null.
        using ParserMemoryScope pageMemory = ParserMemoryScope.Create(governor);
        Content.PoolResolver resolver = Content.PoolResolver.Create(pageMemory, mapped);
        ValidateCatalogVersion((PdfDictionary)mapped[1], resolver, governor, cancellationToken);

        if (infoDirect && infoDict is not null)
        {
            mapped[renumber[-2]] = Writing.ObjectRemap.RemapImported(infoDict, renumber, memory, governor, cancellationToken);
        }

        HashSet<PdfDictionary>? disabledWidgets = Writing.GraphCopier.DisablePublishedSignatures(mapped, 1, governor, limits, memory);
        PromoteOutlines(mapped, memory, governor, cancellationToken);

        int? infoNew = null;
        if (infoOld >= 0 && renumber.TryGetValue(infoOld, out int mappedInfo))
        {
            infoNew = mappedInfo;
        }
        else if (infoDirect)
        {
            infoNew = renumber[-2];
        }

        List<PageLocation> pages = coalesceContents
            ? CoalescePages(mapped, resolver, infoNew, limits, governor, diagnostics, memory,
                normalizationMemory ?? pageMemory, cancellationToken)
            : PageTree.Read(resolver, mapped[1], diagnostics, governor, limits,
                normalizationMemory ?? pageMemory, cancellationToken);

        // Publication orders the numbered model once, along with ordinary and
        // split output. Keep the catalog first for role-specific preparation.
        List<Writing.NumberedObject> numbered = new(mapped.Count) { new(1, mapped[1]) };
        foreach ((int number, QpdfObject value) in mapped)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (number != 1) { numbered.Add(new Writing.NumberedObject(number, value)); }
        }

        PdfDictionary catalog = (PdfDictionary)mapped[1];
        int metadata = catalog.TryGet(PdfName.FromAscii("Metadata"), out QpdfObject metadataValue) && metadataValue is PdfReference metadataReference
            ? metadataReference.ObjectNumber : 0;
        normalizationPages = normalizationMemory is null ? null : pages;
        return new RenumberedModel(numbered, infoNew, metadata, pages.Count, mapped, renumber, disabledWidgets);
    }

    private static List<PageLocation> CoalescePages(Dictionary<int, QpdfObject> objects, IIndirectResolver resolver,
        int? info, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, ParserMemoryScope memory,
        ParserMemoryScope pageMemory, CancellationToken token)
    {
        using ParserMemoryScope phase = ParserMemoryScope.Create(governor);
        phase.Reserve(160L);
        PdfArray roots = PdfArray.FromOwnedItems([new PdfReference(1, 0), info is int number ? new PdfReference(number, 0) : PdfNull.Instance]);
        HashSet<int> before = GraphReachability.Find(resolver, roots, phase, governor);
        Dictionary<PdfArray, PdfReference> arrays = ParserContainers.CreateDictionary<PdfArray, PdfReference>(phase, ReferenceEqualityComparer.Instance);
        int lastId = 0;
        foreach (int id in objects.Keys)
        { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); lastId = Math.Max(lastId, id); }
        phase.Reserve(256L);
        Func<PdfStream, PdfReference?> addStream = AddObject;
        List<PageLocation> pages = PageTree.Rewrite(objects, 1, resolver,
            page => Content.ContentTransforms.CoalescePage(page, resolver, addStream, arrays, false, null,
                true, phase, limits, governor, diagnostics, token),
            AddObject, diagnostics, governor, limits, pageMemory, memory, token);
        if (arrays.Count != 0)
        { GraphReachability.RetireSuperseded(objects, null, before, roots, resolver, phase, governor, token); }
        return pages;

        PdfReference AddObject(QpdfObject value)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            ParserContainers.EnsureEntryCapacity(memory, objects, IntPtr.Size);
            memory.Reserve(32L);
            lastId = checked(lastId + 1);
            objects.Add(lastId, value);
            return new PdfReference(lastId, 0);
        }
    }

    internal static void CheckObjectCount(ImportedModel model, QpdfLimits limits)
    {
        if ((long)model.Objects.Count > limits.MaxContainerSize)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), model.Objects.Count, limits.MaxContainerSize); }
    }

    private static void PromoteOutlines(Dictionary<int, QpdfObject> objects, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken token)
    {
        PdfDictionary catalog = (PdfDictionary)objects[1];
        if (!PdfDictionaryLookup.TryGet(catalog, "Outlines"u8, governor, token, out QpdfObject raw)
            || raw is not PdfDictionary outlines) { return; }
        int number = 0;
        foreach (int existing in objects.Keys)
        { token.ThrowIfCancellationRequested(); governor.NoteOperation(); number = Math.Max(number, existing); }
        number = checked(number + 1);
        // Remapping already owns this immutable dictionary and its references.
        // Give only the catalog role an indirect identity before packing policy.
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        graph.Memory.Reserve(32L); graph.Add(32L);
        PdfReference reference = new(number, 0);
        PdfDictionary replacement = PdfDictionaryRebuild.ReplaceEntry(graph, catalog, "Outlines"u8, reference);
        ParserContainers.EnsureEntryCapacity(memory, objects, IntPtr.Size);
        objects.Add(number, outlines); objects[1] = replacement;
        graph.Memory.TransferTo(memory, graph.Bytes);
    }

    internal static void ValidateCatalogVersion(PdfDictionary root, Syntax.IIndirectResolver resolver,
        ResourceGovernor governor, CancellationToken token)
    {
        if (PageTree.ReadType(resolver, root, governor, token) is PdfName name
            && !name.RawSpan.IsEmpty && !name.RawSpan.SequenceEqual("Catalog"u8))
        { return; }
        if (!PdfDictionaryLookup.TryGet(root, "Version"u8, governor, token, out QpdfObject raw) || raw is PdfNull)
        { return; }
        if (raw is PdfReference reference)
        {
            if (!PdfObjectLookup.TryResolve(reference, resolver, governor, token, out QpdfObject? resolved) || resolved is null)
            { throw new QpdfParseException("Unresolvable catalog /Version reference.", null, reference.ObjectNumber); }
            raw = resolved;
            if (raw is PdfNull) { return; }
        }

        QpdfDocument.ParseCatalogVersion(raw);
    }

}
