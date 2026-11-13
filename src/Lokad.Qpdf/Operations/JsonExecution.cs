using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;
using static Lokad.Qpdf.Operations.PageAssembly;
using static Lokad.Qpdf.Writing.OutputCatalogPreparation;
using static Lokad.Qpdf.Writing.OutputPublication;
using static Lokad.Qpdf.Writing.OutputSecurityPreparation;
using static Lokad.Qpdf.Writing.OutputVersionPolicy;

namespace Lokad.Qpdf.Operations;

internal static class JsonExecution
{
    internal static void CheckJsonSchema(
        string json,
        long utf8Bytes,
        int version,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(80L);
        using GovernedBufferStream bytes = new(governor, Array.MaxLength, nameof(QpdfLimits.MaxOutputBytes), cancellationToken, 1024);
        List<string> described = ParserContainers.CreateList<string>(memory);
        EncodeSchemaJson(version == 1 ? HelpText.JsonHelpV1 : HelpText.JsonHelpV2, bytes, governor, cancellationToken);
        System.Text.Json.Utf8JsonReader help = new(bytes.WrittenSpan);
        while (help.Read())
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (help.TokenType == System.Text.Json.JsonTokenType.PropertyName && help.CurrentDepth == 1)
            {
                ParserContainers.AddItem(memory, described, ReadSchemaKey(ref help, memory), IntPtr.Size);
            }
        }

        bytes.ResetWrittenBytes();
        if (utf8Bytes > Array.MaxLength)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), utf8Bytes, Array.MaxLength); }
        // The serializer already counted UTF-8 bytes. Admit the final capacity
        // once instead of growing through copies of the complete output.
        bytes.PrepareCapacity(checked((int)utf8Bytes), cancellationToken);
        EncodeSchemaJson(json, bytes, governor, cancellationToken);
        if (bytes.Length != utf8Bytes) { throw new InvalidOperationException("JSON UTF-8 sizing disagrees with encoding."); }
        List<string> missing = ParserContainers.CreateList<string>(memory);
        System.Text.Json.Utf8JsonReader generated = new(bytes.WrittenSpan);
        if (!generated.Read() || generated.TokenType != System.Text.Json.JsonTokenType.StartObject)
        { throw new System.Text.Json.JsonException("JSON schema checking requires an object."); }
        while (generated.Read())
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (generated.TokenType != System.Text.Json.JsonTokenType.PropertyName || generated.CurrentDepth != 1) { continue; }
            bool found = false;
            foreach (string key in described)
            {
                // A help key is ASCII. Even fully escaped, a matching token has
                // at most six bytes per character; skip long unmatched names
                // before ValueTextEquals might materialize unescape scratch.
                if (generated.ValueSpan.Length <= 6L * key.Length && generated.ValueTextEquals(key)) { found = true; break; }
            }
            if (!found) { ParserContainers.AddItem(memory, missing, ReadSchemaKey(ref generated, memory), IntPtr.Size); }
        }

        if (missing.Count > 0)
        {
            using DisplayBuffer message = DisplayBuffer.Create(governor, true);
            message.Append("JSON output has keys missing from --json-help version "); message.AppendInteger(version); message.Append(": ");
            for (int index = 0; index < missing.Count; index++)
            {
                if (index != 0) { message.Append(", "); }
                message.Append(missing[index]);
            }
            message.Append('.');
            using GovernedText text = message.Finish();
            diagnostics.AddOwned(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error, text);
            return;
        }
        diagnostics.AddParts(QpdfDiagnosticCode.JsonSchemaCheck, QpdfDiagnosticSeverity.Info,
            ["JSON schema check passed for version ", version, "."]);
    }

    internal static string ReadSchemaKey(ref System.Text.Json.Utf8JsonReader reader, ParserMemoryScope memory)
    {
        // Fund the decoded UTF-16 string and any UTF-8 unescape scratch before
        // GetString. Names in the fixed help inventory are tiny; unknown output
        // keys remain subject to memory and diagnostic bounds.
        memory.Reserve(checked(GovernedText.StringBytes(reader.ValueSpan.Length) + 32L + 2L * reader.ValueSpan.Length));
        return reader.GetString() ?? string.Empty;
    }

    internal static void EncodeSchemaJson(string text, GovernedBufferStream bytes,
        ResourceGovernor governor, CancellationToken token)
    {
        Span<byte> encoded = stackalloc byte[1024];
        ReadOnlySpan<char> remaining = text;
        while (!remaining.IsEmpty)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            int count = Math.Min(256, remaining.Length);
            if (count < remaining.Length && char.IsHighSurrogate(remaining[count - 1]) && char.IsLowSurrogate(remaining[count])) { count--; }
            int length = System.Text.Encoding.UTF8.GetBytes(remaining[..count], encoded);
            bytes.Write(encoded[..length], token); remaining = remaining[count..];
        }
    }

    internal static readonly string[] JsonKeys =
    [
        "acroform", "attachments", "encrypt", "objectinfo", "objects", "outlines", "pagelabels", "pages", "qpdf",
    ];

    internal static void ValidateJsonKeys(int version, IReadOnlyList<string> keys)
    {
        if (keys is null)
        {
            throw new ArgumentNullException(nameof(keys));
        }

        foreach (string key in keys)
        {
            if (key is null || Array.IndexOf(JsonKeys, key) < 0)
            {
                throw new QpdfParseException("Unknown JSON key '" + ArgvParser.ErrorPreview(key ?? string.Empty) + "'.", 0L, null);
            }

            if (version == 1 && string.Equals(key, "qpdf", StringComparison.Ordinal))
            {
                throw new QpdfParseException("JSON key 'qpdf' is only valid for JSON version 2.", 0L, null);
            }

            if (version == 2 && (string.Equals(key, "objects", StringComparison.Ordinal) || string.Equals(key, "objectinfo", StringComparison.Ordinal)))
            {
                throw new QpdfParseException("JSON keys 'objects' and 'objectinfo' are only valid for JSON version 1.", 0L, null);
            }
        }
    }

    internal static HashSet<(bool Trailer, int Number, int Generation)>? ValidateJsonObjects(
        IReadOnlyList<string> objects, long index, ConfigurationText? textStorage)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (objects.Count == 0) { return null; }
        ConfigurationText owner = textStorage ?? throw new InvalidOperationException("Missing JSON selection owner.");
        HashSet<(bool Trailer, int Number, int Generation)>? selected = null;
        foreach (string spec in objects) { selected = AddJsonObject(selected, spec, index, owner); }
        return selected;
    }

    internal static HashSet<(bool Trailer, int Number, int Generation)> AddJsonObject(
        HashSet<(bool Trailer, int Number, int Generation)>? selected, string spec, long index, ConfigurationText owner)
    {
        ResourceGovernor governor = owner.Governor;
        governor.CheckCancellation();
        if (spec is null) { throw new QpdfParseException("JSON object selectors must not be null.", index, null); }
        bool trailer = string.Equals(spec, "trailer", StringComparison.Ordinal);
        int number = 0, generation = 0;
        if (!trailer)
        {
            // Two ten-digit integers and a comma are the longest valid spelling.
            if (spec.Length > 21) { throw InvalidJsonObject(spec, index, owner); }
            int comma = spec.IndexOf(',');
            if (!TryParseObjectNumber(comma < 0 ? spec.AsSpan() : spec.AsSpan(0, comma), out number)
                || comma >= 0 && !TryParseObjectNumber(spec.AsSpan(comma + 1), out generation))
            { throw InvalidJsonObject(spec, index, owner); }
        }
        selected ??= ParserContainers.CreateSet<(bool Trailer, int Number, int Generation)>(owner.Memory);
        ParserContainers.AddSetItem(owner.Memory, selected, (trailer, number, generation), 12L);
        return selected;
    }

    internal static QpdfParseException InvalidJsonObject(string spec, long index, ConfigurationText owner)
    {
        // Bound the excerpt before formatting and fund the offset suffix copy too.
        owner.Memory.Reserve(2L * GovernedText.StringBytes(256));
        owner.Governor.CheckCancellation();
        Span<char> message = stackalloc char[256];
        const string start = "JSON object '";
        start.AsSpan().CopyTo(message);
        int length = start.Length, take = Math.Min(spec.Length, 128);
        if (take < spec.Length && char.IsHighSurrogate(spec[take - 1])) { take--; }
        spec.AsSpan(0, take).CopyTo(message[length..]); length += take;
        if (take < spec.Length) { "...".AsSpan().CopyTo(message[length..]); length += 3; }
        const string end = "' must be trailer or obj[,gen].";
        end.AsSpan().CopyTo(message[length..]); length += end.Length;
        return new QpdfParseException(new string(message[..length]), index, null);
    }

    internal static bool TryParseObjectNumber(ReadOnlySpan<char> text, out int number)
    {
        number = 0;
        if (text.Length == 0 || text.Length > 10)
        {
            return false;
        }

        long value = 0L;
        foreach (char c in text)
        {
            if (c < (char)48 || c > (char)57)
            {
                return false;
            }

            value = checked(value * 10L + (c - (char)48));
        }

        if (value > (long)int.MaxValue)
        {
            return false;
        }

        number = (int)value;
        return true;
    }

    internal static async ValueTask<JsonInspectionOutcome> JsonCoreAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath? outputPath,
        JsonInspection inspection,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        bool emptyDocument,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        bool testJsonSchema,
        bool escapeText,
        CancellationToken cancellationToken)
    {
        using ResultWarningSnapshot? resultWarnings = ResultWarningSnapshot.Create(governor, diagnostics, cancellationToken);
        ValidateJsonKeys(inspection.Version, inspection.Keys);
        // The configuration owner funds this immutable selection through execution.
        HashSet<(bool Trailer, int Number, int Generation)>? wanted = inspection.Objects;
        if (outputPath.HasValue && string.Equals(inputPath.Value, outputPath.Value.Value, StringComparison.Ordinal))
        {
            throw new QpdfParseException("The output file must differ from the input file.", 0L, null);
        }

        string decodeLevel = inspection.DecodeLevel ?? (inspection.MinimalOutput ? "none" : "generalized");
        if (decodeLevel != "none" && decodeLevel != "generalized" && decodeLevel != "specialized" && decodeLevel != "all")
        {
            throw new QpdfParseException("JSON decode level must be one of: none, generalized, specialized, all.", 0L, null);
        }

        string? prefix = inspection.StreamPrefix;
        if (inspection.StreamData == "file" && prefix is null)
        {
            if (!outputPath.HasValue)
            {
                throw new QpdfParseException("JSON file stream data needs an output file or --json-stream-prefix.", 0L, null);
            }

            prefix = outputPath.Value.Value;
        }

        // JSON emits text and copied sidecar bytes. Its reader values remain
        // exclusive to this document until serialization completes.
        await using QpdfDocument document = emptyDocument
            ? await QpdfDocument.OpenEmptyDocument(inputPath, limits, governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false)
            : await QpdfDocument.OpenInspectionCore(host, inputPath, inspection.Password, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
        Json.JsonRequest request = new(inspection.Version, inspection.Keys, decodeLevel, inspection.StreamData, prefix, wanted, inspection.MinimalOutput);
        using Json.PreparedJsonOutput output = Json.PreparedJsonOutput.Create(governor);
        long mainBytes;
        using (DisplayBuffer builder = Json.DocumentJson.WriteDocument(document, request, output, limits, governor, diagnostics, cancellationToken))
        {
            mainBytes = builder.Utf8Bytes;
            if (mainBytes > limits.MaxOutputBytes)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), mainBytes, limits.MaxOutputBytes); }
            // The chunks no longer refer to PDF values. Close the reader
            // before allocating immutable text and publication copies.
            if (!testJsonSchema) { await document.DisposeAsync().ConfigureAwait(false); }
            using GovernedText text = builder.Finish();
            if (testJsonSchema)
            {
                // Preserve the schema note's acceptance before input close.
                CheckJsonSchema(text.Value, text.Utf8Bytes, inspection.Version, governor, diagnostics, cancellationToken);
                await document.DisposeAsync().ConfigureAwait(false);
            }
            output.Prepare(text, outputPath.HasValue, escapeText, limits);
        }
        List<Json.JsonPayload> sidecars = output.Sidecars;

        long anticipatedBytes = mainBytes;
        foreach (Json.JsonPayload sidecar in sidecars)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (string.Equals(sidecar.Name, inputPath.Value, StringComparison.Ordinal))
            { throw new QpdfParseException("A JSON stream file must differ from the input file.", 0L, null); }
            if (string.Equals(sidecar.Name, outputPath?.Value, StringComparison.Ordinal))
            { throw new QpdfParseException("A JSON stream file must differ from the main JSON output.", 0L, null); }
            anticipatedBytes = checked(anticipatedBytes + sidecar.Data.Length);
        }
        governor.ChargeOutputBytes(anticipatedBytes);
        using PublicationReservation publicationAdmission = PublicationReservation.Create(governor, outputPath.HasValue || sidecars.Count != 0);
        governor.PrepareExecutionPresentation();
        publicationAdmission.Begin();
        if (outputPath.HasValue)
        { await PublishPreparedBytesAsync(host, outputPath.Value, output.Main ?? throw new InvalidOperationException("Missing JSON publication."), governor, cancellationToken).ConfigureAwait(false); }
        foreach (Json.JsonPayload sidecar in sidecars)
        { await PublishPreparedBytesAsync(host, QpdfPath.From(sidecar.Name), sidecar, governor, cancellationToken).ConfigureAwait(false); }
        return new JsonInspectionOutcome(output.TransferText(), ResultWarningSnapshot.Read(resultWarnings, diagnostics));

    }

    internal static async ValueTask<JsonImportOutcome> ImportCreateCoreAsync(
        IQpdfHost host,
        QpdfPath jsonPath,
        QpdfPath outputPath,
        string? minVersion,
        string? forceVersion,
        bool staticId,
        bool newlineBeforeEndstream,
        QpdfOutputEncryption outputEncryption,
        QpdfRecoveryMode recovery,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ResultWarningSnapshot? resultWarnings = ResultWarningSnapshot.Create(governor, diagnostics, cancellationToken);
        if (string.Equals(jsonPath.Value, outputPath.Value, StringComparison.Ordinal))
        {
            throw new QpdfParseException("The output file must differ from the input file.", 0L, null);
        }

        using ParserMemoryScope importMemory = ParserMemoryScope.Create(governor);
        System.Text.Json.JsonDocument parsed = await Json.JsonImport.ReadJsonAsync(host, jsonPath, limits, governor, importMemory, cancellationToken).ConfigureAwait(false);
        using (parsed)
        {
            Json.ImportedModel model = await Json.JsonImport.BuildCreateModelAsync(host, parsed, limits, governor, importMemory, cancellationToken).ConfigureAwait(false);
            if (!outputEncryption.PreservesInput) { model = Json.JsonImport.RemoveEncryptionDeclaration(model, governor, cancellationToken); }
            var preparedSecurity = await PrepareOutputSecurityAsync(host, outputEncryption, null, null, importMemory,
                governor, limits, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
            using OutputSecurity? security = preparedSecurity.Security;
            (model, PdfVersion effective) = PrepareImportedVersion(model, minVersion, forceVersion, security, governor, cancellationToken);
            Json.JsonImport.RenumberedModel renumbered = Json.JsonImport.Renumber(model, limits, governor, diagnostics, importMemory, cancellationToken);
            int encryptionNumber = OutputModelPreparation.AddEncryptionObject(renumbered.Model, security, importMemory, governor);
            await PublishModelAsync(host, effective, renumbered.Model, null, false, null, renumbered.InfoNumber, preparedSecurity.Identifier,
                staticId, newlineBeforeEndstream, Writing.QdfOptions.Disabled, null, false, null, outputPath, limits, governor, cancellationToken,
                security, encryptionNumber, renumbered.MetadataNumber).ConfigureAwait(false);
            return new JsonImportOutcome(renumbered.PageCount, ResultWarningSnapshot.Read(resultWarnings, diagnostics));
        }
    }

    internal static async ValueTask<JsonImportOutcome> ImportUpdateCoreAsync(
        IQpdfHost host,
        QpdfPath inputPath,
        QpdfPath jsonPath,
        QpdfPath outputPath,
        string? minVersion,
        string? forceVersion,
        bool staticId,
        bool newlineBeforeEndstream,
        QpdfPassword? password,
        QpdfOutputEncryption outputEncryption,
        QpdfLimits limits,
        QpdfRecoveryMode recovery,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ResultWarningSnapshot? resultWarnings = ResultWarningSnapshot.Create(governor, diagnostics, cancellationToken);
        if (string.Equals(inputPath.Value, outputPath.Value, StringComparison.Ordinal))
        {
            throw new QpdfParseException("The output file must differ from the input file.", 0L, null);
        }

        if (string.Equals(jsonPath.Value, outputPath.Value, StringComparison.Ordinal))
        {
            throw new QpdfParseException("The output file must differ from the JSON file.", 0L, null);
        }

        await using QpdfDocument document = await QpdfDocument.OpenInspectionCore(host, inputPath, password, limits, governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false);
        using ParserMemoryScope importMemory = ParserMemoryScope.Create(governor);
        System.Text.Json.JsonDocument parsed = await Json.JsonImport.ReadJsonAsync(host, jsonPath, limits, governor, importMemory, cancellationToken).ConfigureAwait(false);
        using (parsed)
        {
            Json.ImportedModel model = await Json.JsonImport.BuildUpdateModelAsync(host, parsed, document, outputEncryption.PreservesInput,
                limits, governor, importMemory, cancellationToken).ConfigureAwait(false);
            var preparedSecurity = await PrepareOutputSecurityAsync(host, outputEncryption, document, TryGetOriginalId(document.Engine),
                importMemory, governor, limits, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
            using OutputSecurity? security = preparedSecurity.Security;
            (model, PdfVersion effective) = PrepareImportedVersion(model, minVersion, forceVersion, security, governor, cancellationToken);
            Json.JsonImport.RenumberedModel renumbered = Json.JsonImport.Renumber(model, limits, governor, diagnostics, importMemory, cancellationToken);
            int encryptionNumber = OutputModelPreparation.AddEncryptionObject(renumbered.Model, security, importMemory, governor);
            await PublishModelAsync(host, effective, renumbered.Model, null, false, null, renumbered.InfoNumber, preparedSecurity.Identifier,
                staticId, newlineBeforeEndstream, Writing.QdfOptions.Disabled, null, false, null, outputPath, limits, governor, cancellationToken,
                security, encryptionNumber, renumbered.MetadataNumber).ConfigureAwait(false);
            return new JsonImportOutcome(renumbered.PageCount, ResultWarningSnapshot.Read(resultWarnings, diagnostics));
        }
    }

    internal static (Json.ImportedModel Model, PdfVersion Version) PrepareImportedVersion(Json.ImportedModel model,
        string? minVersion, string? forceVersion, OutputSecurity? security, ResourceGovernor governor, CancellationToken token)
    {
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        IIndirectResolver resolver = Content.PoolResolver.Create(graph.Memory, model.Objects);
        model.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject root);
        PdfDictionary? catalog = Content.CatalogCopy.ResolveDictionary(root, resolver, governor, token);
        int level = Content.CatalogCopy.ReadExtensionLevel(catalog, resolver, governor, token);
        (PdfVersion version, int extensionLevel) = NegotiateVersion(model.HeaderVersion, level, minVersion, forceVersion, governor);
        RequireEncryptionVersion(security, ref version, ref extensionLevel, forceVersion, governor);
        if (catalog is not null)
        {
            PdfDictionaryLookup.TryGet(catalog, "Extensions"u8, governor, token, out QpdfObject original);
            QpdfObject? extensions = PrepareExtensions(original, resolver, version, extensionLevel, graph);
            if (!ReferenceEquals(extensions, original))
            {
                PdfDictionary replacement = extensions is null ? PdfDictionaryRebuild.RemoveEntry(graph, catalog, "Extensions"u8)
                    : PdfDictionaryRebuild.ReplaceEntry(graph, catalog, "Extensions"u8, extensions);
                if (root is PdfReference reference) { model.Objects[reference.ObjectNumber] = replacement; }
                else
                {
                    PdfDictionary trailer = PdfDictionaryRebuild.ReplaceEntry(graph, model.Trailer, "Root"u8, replacement);
                    graph.Memory.Reserve(48L); graph.Add(48L);
                    model = new(model.HeaderVersion, model.Objects, trailer);
                }
            }
        }
        graph.Retain(); return (model, version);
    }

}
