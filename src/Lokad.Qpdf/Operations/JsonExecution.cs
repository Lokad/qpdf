using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;
using static Lokad.Qpdf.Operations.PageAssembly;
using static Lokad.Qpdf.Writing.OutputCatalogPreparation;
using static Lokad.Qpdf.Writing.OutputPublication;
using static Lokad.Qpdf.Writing.OutputSecurityPreparation;
using static Lokad.Qpdf.Writing.OutputVersionPolicy;
using static Lokad.Qpdf.Writing.ObjectStreamPreparation;

namespace Lokad.Qpdf.Operations;

internal readonly record struct JsonImportWriterSettings(
    string? MinVersion, string? ForceVersion, bool StaticId, bool NewlineBeforeEndstream,
    string ObjectStreams, bool Linearize, QpdfOutputEncryption Encryption,
    DocumentPublicationPolicy Publication, CompressionRequest? Compression, QdfOptions Qdf, bool NormalizeContent,
    bool CoalesceContents, int? SplitPages, IReadOnlyList<PageLabelSpec> Labels)
{
    internal static JsonImportWriterSettings ForTyped(QpdfJsonImportOptions options, QpdfOutputEncryption encryption)
        => new(null, null, false, false, options.ObjectStreams, options.Linearize, encryption, new(false, false),
            null, QdfOptions.Disabled, false, false, null, []);
}

internal static class JsonExecution
{
    internal static void CheckJsonSchema(
        string json,
        long utf8Bytes,
        int version,
        IReadOnlyList<string> keys,
        bool minimalOutput,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(80L + 64L * (2L + (Json.DocumentJson.SyntaxMaxDepth + 63L) / 64L));
        if (utf8Bytes > Array.MaxLength)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), utf8Bytes, Array.MaxLength); }
        using GovernedBufferStream bytes = new(governor, Array.MaxLength, nameof(QpdfLimits.MaxOutputBytes), cancellationToken, 1024);
        bytes.PrepareCapacity(checked((int)utf8Bytes), cancellationToken);
        EncodeSchemaJson(json, bytes, governor, cancellationToken);
        if (bytes.Length != utf8Bytes) { throw new InvalidOperationException("JSON UTF-8 sizing disagrees with encoding."); }
        Json.JsonFormatValidator.Check(bytes.WrittenSpan, version == 1 ? HelpText.JsonHelpV1 : HelpText.JsonHelpV2,
            version, keys, minimalOutput, governor, diagnostics, cancellationToken);
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
        using ParserMemoryScope? targetMemory = outputPath.HasValue || (inspection.Version == 2 && inspection.StreamData == "file")
            ? ParserMemoryScope.Create(governor) : null;
        using PublicationInputAliases? targetAliases = targetMemory is null ? null : PublicationInputAliases.Begin(targetMemory, governor, "JSON");
        if (targetAliases is not null)
        {
            targetAliases.RememberPath(inputPath.Value);
            if (inspection.GraphPlan is IInspectionGraphPlan graphPlan)
            {
                foreach (PdfPageSource source in graphPlan.Sources)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    targetAliases.RememberPath(source.Input.Value == "." ? inputPath.Value : source.Input.Value);
                }
            }
        }
        if (outputPath is QpdfPath report && targetAliases is not null && targetAliases.ContainsPath(report.Value))
        {
            throw new QpdfParseException("The output file must differ from every input file.", 0L, null);
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
        using InspectionGraphPreparation.InspectionGraph? graph = inspection.GraphPlan is null ? null
            : await InspectionGraphPreparation.PrepareInspectionGraphAsync(host, document, inspection.GraphPlan, limits,
                governor, diagnostics, cancellationToken).ConfigureAwait(false);
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
            graph?.Dispose();
            if (!testJsonSchema) { await document.DisposeAsync().ConfigureAwait(false); }
            using GovernedText text = builder.Finish();
            if (testJsonSchema)
            {
                // Preserve the schema note's acceptance before input close.
                CheckJsonSchema(text.Value, text.Utf8Bytes, inspection.Version, request.Keys, request.MinimalOutput, governor, diagnostics, cancellationToken);
                await document.DisposeAsync().ConfigureAwait(false);
            }
            output.Prepare(text, outputPath.HasValue, escapeText, limits);
        }
        List<Json.JsonPayload> sidecars = output.Sidecars;

        long anticipatedBytes = mainBytes;
        if (targetAliases is not null && targetMemory is not null && (outputPath.HasValue || sidecars.Count != 0))
        {
            int targetCount = checked(sidecars.Count + (outputPath.HasValue ? 1 : 0));
            List<string> names = ParserContainers.CreateList<string>(targetMemory, targetCount, IntPtr.Size);
            if (outputPath is QpdfPath path) { ParserContainers.AddItem(targetMemory, names, path.Value, IntPtr.Size); }
            foreach (Json.JsonPayload sidecar in sidecars)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (string.Equals(sidecar.Name, outputPath?.Value, StringComparison.Ordinal))
                { throw new QpdfParseException("A JSON stream file must differ from the main JSON output.", 0L, null); }
                ParserContainers.AddItem(targetMemory, names, sidecar.Name, IntPtr.Size);
                anticipatedBytes = checked(anticipatedBytes + sidecar.Data.Length);
            }
            await targetAliases.ValidateTargetsAsync(host, names, governor, cancellationToken).ConfigureAwait(false);
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

    internal static async ValueTask<JsonImportOutcome> ImportCoreAsync(
        IQpdfHost host,
        QpdfPath? sourcePdf,
        QpdfPath jsonPath,
        QpdfPath outputPath,
        bool replaceInput,
        QpdfPassword? password,
        JsonImportWriterSettings writer,
        QpdfRecoveryMode recovery,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        using ResultWarningSnapshot? resultWarnings = ResultWarningSnapshot.Create(governor, diagnostics, cancellationToken);
        if (replaceInput && sourcePdf != outputPath)
        { throw new InvalidOperationException("The JSON replacement target must be the source PDF."); }
        if (!replaceInput && sourcePdf == outputPath)
        {
            throw new QpdfParseException("The output file must differ from the input file.", 0L, null);
        }

        if (string.Equals(jsonPath.Value, outputPath.Value, StringComparison.Ordinal))
        {
            string message = sourcePdf.HasValue ? "The output file must differ from the JSON file."
                : "The output file must differ from the input file.";
            throw new QpdfParseException(message, 0L, null);
        }

        using ParserMemoryScope? splitMemory = writer.SplitPages.HasValue ? ParserMemoryScope.Create(governor) : null;
        using PublicationInputAliases? splitInputs = splitMemory is null ? null : PublicationInputAliases.Begin(splitMemory, governor, "Split");
        await using QpdfDocument? document = sourcePdf is QpdfPath input
            ? await QpdfDocument.OpenInspectionCore(host, input, password, limits, governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false)
            : null;
        using ParserMemoryScope importMemory = ParserMemoryScope.Create(governor);
        (QpdfPath Path, string Identity)? replacement = replaceInput && document is not null
            ? (outputPath, document.InputIdentity) : null;
        using System.Text.Json.JsonDocument parsed = await Json.JsonImport.ReadJsonAsync(host, jsonPath, replacement?.Identity,
            limits, governor, importMemory, cancellationToken).ConfigureAwait(false);
        bool retainOriginalPairs = writer.Qdf.Enabled && !writer.Qdf.NoOriginalIds && !writer.Linearize;
        Json.ImportedModel model;
        if (document is null)
        {
            model = await Json.JsonImport.BuildCreateModelAsync(host, parsed, retainOriginalPairs, limits, governor,
                importMemory, cancellationToken).ConfigureAwait(false);
            if (!writer.Encryption.PreservesInput)
            { model = Json.JsonImport.RemoveEncryptionDeclaration(model, governor, cancellationToken); }
        }
        else
        {
            model = await Json.JsonImport.BuildUpdateModelAsync(host, parsed, document.Engine, document.HeaderVersionParts, writer.Encryption.PreservesInput,
                writer.ObjectStreams == "preserve", retainOriginalPairs, replacement,
                limits, governor, importMemory, cancellationToken).ConfigureAwait(false);
        }

        byte[]? originalId = document is null ? null : TryGetOriginalId(document.Engine);
        var preparedSecurity = await PrepareOutputSecurityAsync(host, writer.Encryption, document, originalId, importMemory,
            governor, limits, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
        using OutputSecurity? security = preparedSecurity.Security;
        PreparedImportedOutput prepared = PrepareImportedOutput(model, writer, security, limits, importMemory, splitMemory, governor,
            diagnostics, cancellationToken);
        IReadOnlyDictionary<int, (int Number, int Generation)>? originalIds = OriginalObjectIds.PrepareImportedIds(
            model.OriginalPairs, prepared.Graph.Mapping, importMemory, governor, cancellationToken);
        if ((replaceInput || splitInputs is not null) && document is not null)
        { await document.ReleaseCompleteInputAsync().ConfigureAwait(false); }
        if (writer.SplitPages is int groupSize)
        {
            if (splitInputs is null || prepared.Pages is null)
            { throw new InvalidOperationException("Missing bound JSON split preparation."); }
            await JsonSplitPublication.PublishAsync(host, outputPath, groupSize, writer, prepared.Version, prepared.Graph,
                prepared.Pages, prepared.Labels, prepared.PreservedPacking, preparedSecurity.Identifier, originalIds, security, splitInputs,
                limits, governor, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await PublishModelAsync(host, prepared.Version, prepared.Output.Model, prepared.Output.ObjectStreams, false, null,
                prepared.Graph.InfoNumber, preparedSecurity.Identifier, writer.StaticId, writer.NewlineBeforeEndstream,
                writer.Qdf, originalIds, writer.Linearize, null, outputPath, limits, governor, cancellationToken,
                security, prepared.Output.EncryptionObject, prepared.Graph.MetadataNumber).ConfigureAwait(false);
        }
        return new JsonImportOutcome(prepared.Graph.PageCount, ResultWarningSnapshot.Read(resultWarnings, diagnostics));
    }

    private readonly record struct PreparedImportedOutput(PdfVersion Version, Json.JsonImport.RenumberedModel Graph,
        PreparedOutputModel Output, IReadOnlyList<PageLocation>? Pages, HashSet<int>? PreservedPacking,
        List<(int Index, PdfDictionary Label)>? Labels);

    private static PreparedImportedOutput PrepareImportedOutput(Json.ImportedModel model, JsonImportWriterSettings writer,
        OutputSecurity? security, QpdfLimits limits,
        ParserMemoryScope memory, ParserMemoryScope? splitMemory, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        DocumentPublicationPolicy policy = writer.Labels.Count == 0 ? writer.Publication
            : writer.Publication with { RemoveLabels = true };
        model = ImportedPublication.Apply(model, policy, limits, memory, governor, token);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        IIndirectResolver resolver = Content.PoolResolver.Create(graph.Memory, model.Objects);
        model.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject root);
        PdfDictionary? catalog = Content.CatalogCopy.ResolveDictionary(root, resolver, governor, token);
        int level = Content.CatalogCopy.ReadExtensionLevel(catalog, resolver, governor, token);
        (PdfVersion version, int extensionLevel) = NegotiateVersion(model.HeaderVersion, level, writer.MinVersion, writer.ForceVersion, governor);
        RequireEncryptionVersion(security, ref version, ref extensionLevel, writer.ForceVersion, governor);
        using ParserMemoryScope? normalizationMemory = writer.NormalizeContent ? ParserMemoryScope.Create(governor) : null;
        Json.JsonImport.RenumberedModel renumbered = Json.JsonImport.Renumber(model, limits, governor, diagnostics, memory,
            splitMemory ?? normalizationMemory, writer.CoalesceContents, out IReadOnlyList<PageLocation>? pages, token);
        string mode = ResolveObjectStreamMode(writer.ObjectStreams);
        if (writer.ForceVersion is not null && PdfVersion.CompareParts(version.Major, version.Minor, 1, 5) < 0) { mode = "disable"; }
        List<(IReadOnlyDictionary<int, int> Map, HashSet<int> CompressedOrig)> sources = ParserContainers.CreateList<(IReadOnlyDictionary<int, int>, HashSet<int>)>(memory);
        if (mode == "preserve" && model.CompressedObjects is not null)
        {
            ParserContainers.AddItem(memory, sources, (renumbered.Mapping, model.CompressedObjects), 2L * IntPtr.Size);
        }
        PdfDictionary mappedCatalog = (PdfDictionary)renumbered.Objects[1];
        List<(int Index, PdfDictionary Label)>? splitLabels = null;
        if (writer.Labels.Count > 0)
        {
            using ParserMemoryScope labelMemory = ParserMemoryScope.Create(governor);
            List<(int Index, PdfDictionary Label)> labels;
            try
            {
                labels = PageLabelAssembly.ResolveLabelItems(writer.Labels, renumbered.PageCount, governor, splitMemory ?? labelMemory);
            }
            catch (ArgumentOutOfRangeException ex)
            { throw new QpdfParseException(ex.Message, null, null, ex); }
            if (writer.ForceVersion is null)
            { IncludeVersionRequirement(ref version, ref extensionLevel, AuthoredVersionFloor(false, false, true), 0, governor); }
            if (splitMemory is null)
            {
                PdfDictionary labelTree = PageLabelAssembly.NumbersDictionary(labels, governor);
                mappedCatalog = PdfDictionaryRebuild.ReplaceEntry(graph, mappedCatalog, "PageLabels"u8, labelTree);
                renumbered.Objects[1] = mappedCatalog;
                renumbered.Model[0] = new(1, mappedCatalog);
            }
            else { splitLabels = labels; }
        }
        int pagesNumber = PageTreeRootNumber(mappedCatalog, governor, token);
        // Generated packing uses the completed flat split graph, including
        // occurrence-local page and widget headers created during splitting.
        bool generateSplitPacking = splitMemory is not null && mode == "generate";
        HashSet<int>? compressed = generateSplitPacking ? null
            : ResolveCompressedSet(mode, renumbered.Objects, sources, 1, pagesNumber, memory, governor, token);
        bool hasPacking = compressed is not null ? compressed.Count != 0 : renumbered.PageCount != 0;
        (version, extensionLevel) = RequireObjectStreamVersion(version, extensionLevel, hasPacking, governor);
        IIndirectResolver mappedResolver = Content.PoolResolver.Create(graph.Memory, renumbered.Objects);
        PdfDictionaryLookup.TryGet(mappedCatalog, "Extensions"u8, governor, token, out QpdfObject originalExtensions);
        QpdfObject? extensions = PrepareExtensions(originalExtensions, mappedResolver, version, extensionLevel, graph);
        if (!ReferenceEquals(extensions, originalExtensions))
        {
            PdfDictionary replacement = extensions is null ? PdfDictionaryRebuild.RemoveEntry(graph, mappedCatalog, "Extensions"u8)
                : PdfDictionaryRebuild.ReplaceEntry(graph, mappedCatalog, "Extensions"u8, extensions);
            renumbered.Objects[1] = replacement; renumbered.Model[0] = new(1, replacement);
        }
        graph.Retain();
        PreparedOutputModel output;
        if (splitMemory is null)
        {
            output = OutputModelPreparation.Finish(renumbered.Model,
                compressed ?? throw new InvalidOperationException("Missing JSON import packing policy."), security, memory, governor, token);
        }
        else
        {
            if (security is not null)
            {
                foreach (NumberedObject item in renumbered.Model)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    if (item.Value is not PdfStream stream || !StreamCryptFilter.MayContain(stream, governor)) { continue; }
                    OutputModelPreparation.NormalizeIdentityCrypt(renumbered.Model, memory, governor);
                    break;
                }
            }
            output = new(renumbered.Model, null, 0);
        }
        // Protected output preparation consumes supported Crypt stages before ordinary
        // stream decoding. The serializer applies ciphertext after compression.
        if (writer.Compression is CompressionRequest compression)
        {
            using ParserMemoryScope resolverMemory = ParserMemoryScope.Create(governor);
            Content.PoolResolver streamResolver = Content.PoolResolver.Create(resolverMemory, renumbered.Objects);
            Dictionary<int, (PdfDictionary? Resources, bool Ambiguous)>? contexts =
                normalizationMemory is not null && pages is not null
                    ? Content.ContentTransforms.FindPageContentContexts(pages, streamResolver, normalizationMemory,
                        limits, governor, diagnostics, token) : null;
            StreamPreparation.TransformNumberedStreams(output.Model, streamResolver, renumbered.MetadataNumber,
                contexts, compression, limits, governor, diagnostics, token);
        }
        if (splitMemory is not null)
        {
            foreach (NumberedObject item in output.Model)
            { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); renumbered.Objects[item.Number] = item.Value; }
        }
        return new(version, renumbered, output, splitMemory is null ? null : pages, compressed, splitLabels);
    }

}
