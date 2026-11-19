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

internal enum JsonDetachedObjects { RetainAll, Prune, Preserve }

internal readonly record struct JsonImportWriterSettings(
    string? MinVersion, string? ForceVersion, bool StaticId, bool NewlineBeforeEndstream,
    string ObjectStreams, bool Linearize, QpdfPath? LinearizePass1, QpdfOutputEncryption Encryption,
    DocumentPublicationPolicy Publication, CompressionRequest? Compression, QdfOptions Qdf, bool NormalizeContent,
    bool CoalesceContents, int? SplitPages, IReadOnlyList<PageLabelSpec> Labels, IReadOnlyList<OutputRotation> Rotations,
    JsonDetachedObjects DetachedObjects)
{
    internal string? RemoveUnreferencedResources { get; init; }
    internal bool ExternalizeInlineImages { get; init; }
    internal long InlineImageMinimumBytes { get; init; }
    internal Content.ImageOptimizeRequest? Images { get; init; }

    internal static JsonImportWriterSettings ForTyped(QpdfJsonImportOptions options, QpdfOutputEncryption encryption)
        => new(null, null, false, false, options.ObjectStreams, options.Linearize, null, encryption, new(false, false),
            null, QdfOptions.Disabled, false, false, null, [], [], JsonDetachedObjects.RetainAll);
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
        using PublicationInputAliases? ownedAliases = targetMemory is null ? null : PublicationInputAliases.BeginIfNeeded(governor);
        PublicationInputAliases? targetAliases = targetMemory is null ? null : PublicationInputAliases.Active(governor);
        if (targetAliases is not null)
        {
            targetAliases.RememberPath(inputPath.Value);
            if (inspection.Import is ImportedInspectionInput import)
            {
                targetAliases.RememberPath(import.Json.Value);
                if (import.FollowingPatch is QpdfPath following) { targetAliases.RememberPath(following.Value); }
            }
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
        await using QpdfDocument? document = inspection.Import.HasValue ? null
            : emptyDocument
                ? await QpdfDocument.OpenEmptyDocument(inputPath, limits, governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false)
                : await QpdfDocument.OpenInspectionCore(host, inputPath, inspection.Password, limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
        using InspectionGraphPreparation.InspectionGraph? graph = inspection.GraphPlan is null || document is null ? null
            : await InspectionGraphPreparation.PrepareInspectionGraphAsync(host, document, inspection.GraphPlan, limits,
                governor, diagnostics, cancellationToken).ConfigureAwait(false);
        Json.JsonRequest request = new(inspection.Version, inspection.Keys, decodeLevel, inspection.StreamData, prefix, wanted, inspection.MinimalOutput);
        using Json.PreparedJsonOutput output = Json.PreparedJsonOutput.Create(governor);
        long mainBytes;
        using (DisplayBuffer builder = inspection.Import is ImportedInspectionInput imported
            ? await WriteImportedJsonAsync(host, inputPath, imported, inspection, request, output,
                limits, governor, diagnostics, recovery, cancellationToken).ConfigureAwait(false)
            : Json.DocumentJson.WriteDocument(document ?? throw new InvalidOperationException("Missing JSON projection document."),
                request, output, limits, governor, diagnostics, cancellationToken))
        {
            mainBytes = builder.Utf8Bytes;
            if (mainBytes > limits.MaxOutputBytes)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), mainBytes, limits.MaxOutputBytes); }
            // The chunks no longer refer to PDF values. Close the reader
            // before allocating immutable text and publication copies.
            graph?.Dispose();
            if (!testJsonSchema && document is not null) { await document.DisposeAsync().ConfigureAwait(false); }
            using GovernedText text = builder.Finish();
            if (testJsonSchema)
            {
                // Preserve the schema note's acceptance before input close.
                CheckJsonSchema(text.Value, text.Utf8Bytes, inspection.Version, request.Keys, request.MinimalOutput, governor, diagnostics, cancellationToken);
                if (document is not null) { await document.DisposeAsync().ConfigureAwait(false); }
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
            await targetAliases.ValidateTargetsAsync(host, names, null, "JSON", cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask<DisplayBuffer> WriteImportedJsonAsync(IQpdfHost host, QpdfPath path,
        ImportedInspectionInput imported, JsonInspection inspection, Json.JsonRequest request, Json.PreparedJsonOutput output,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, QpdfRecoveryMode recovery, CancellationToken token)
    {
        DisplayBuffer? rendered = null;
        try
        {
            await using IQpdfInput? input = imported.Update
                ? await GovernedHost.OpenInputAsync(host, path, governor, token).ConfigureAwait(false) : null;
            var source = input is null ? default
                : await QpdfDocument.ReadAuthenticatedPatchSourceAsync(input, inspection.Password, limits, governor,
                    diagnostics, recovery, token).ConfigureAwait(false);
            using DocumentEngine? engine = source.Engine;
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            Json.ImportedModel model = await BindInspectionModelAsync(host, imported, engine, source.Header,
                limits, governor, memory, token).ConfigureAwait(false);
            IReadOnlyList<OutputRotation> rotations = [];
            bool coalesce = false;
            if (inspection.GraphPlan is JsonPlan settings)
            {
                rotations = settings.Rotations;
                coalesce = settings.CoalesceContents;
                model = ImportedPublication.Apply(model,
                    new(settings.RemoveInfo, settings.RemoveMetadata, settings.RemoveAcroform, settings.RemoveStructure, settings.RemoveLabels),
                    limits, memory, governor, token, out _);
            }
            Content.PoolResolver resolver = Content.PoolResolver.Create(memory, model.Objects);
            _ = ReadBoundCatalog(model, resolver, governor, token);
            Dictionary<int, (int Number, int Generation)> ids = OriginalObjectIds.PrepareImportedIds(
                model.OriginalPairs, null, memory, governor, token) ?? throw new InvalidOperationException("Missing bound object identities.");
            IReadOnlyList<PageLocation> pages = [];
            if (coalesce || rotations.Count > 0 || Json.DocumentJson.RequestUsesPageTree(request))
            {
                (PdfDictionary trailer, List<PageLocation> locations) = PrepareBoundPages(model, resolver, ids, coalesce,
                    limits, memory, governor, diagnostics, token);
                ApplyBoundRotations(model.Objects, resolver, locations, rotations, limits, memory, governor, token);
                memory.Reserve(Json.ImportedModel.AllocationBytes);
                model = model with { Trailer = trailer, CalledAllPages = true };
                pages = locations;
            }
            Json.JsonDocumentView view = new(model, resolver, pages, ids, engine?.Security);
            rendered = Json.DocumentJson.WriteDocument(view, request, output, limits, governor, diagnostics, token);
        }
        catch { rendered?.Dispose(); throw; }
        return rendered;
    }

    internal static (PdfDictionary Trailer, List<PageLocation> Pages) PrepareBoundPages(
        Json.ImportedModel model, Content.PoolResolver resolver,
        Dictionary<int, (int Number, int Generation)>? ids, bool coalesce, QpdfLimits limits, ParserMemoryScope memory,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        int lastBound = 0, lastDeclared = 0;
        int objects = model.Objects.Count;
        using ParserMemoryScope temporary = ParserMemoryScope.Create(governor);
        PageTreePreparation.Prepared prepared = PageTreePreparation.PrepareBoundForInspection(resolver, model.Trailer,
            Allocate, limits, memory, temporary, governor, token);
        Json.JsonImport.ApplyPreparedPageObjects(prepared, model.Objects, memory, governor, token);
        if (prepared.AddedReferences.Count != 0)
        {
            diagnostics.Add(QpdfDiagnosticCode.RecoveryEngaged, QpdfDiagnosticSeverity.Warning,
                "Direct or repeated page tree entries were assigned independent object identities.");
        }
        List<PageLocation> pages = prepared.Pages ?? throw new InvalidOperationException("Missing prepared pages.");
        if (coalesce)
        {
            Dictionary<PdfArray, PdfReference> arrays =
                ParserContainers.CreateDictionary<PdfArray, PdfReference>(temporary, ReferenceEqualityComparer.Instance);
            temporary.Reserve(64L);
            Func<PdfStream, PdfReference?> addStream = AddStream;
            for (int index = 0; index < pages.Count; index++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                PageLocation location = pages[index];
                PdfDictionary edited = Content.ContentTransforms.CoalescePage(location.Page, resolver, addStream,
                    arrays, false, null, true, temporary, limits, governor, diagnostics, token);
                if (ReferenceEquals(edited, location.Page)) { continue; }
                PdfReference reference = location.Source ?? throw new InvalidOperationException("Missing prepared page identity.");
                model.Objects[reference.ObjectNumber] = edited;
                pages[index] = new(reference, edited);
            }
        }
        return (prepared.Trailer, pages);

        PdfReference AddStream(PdfStream stream)
        {
            int number = Allocate();
            ParserContainers.EnsureEntryCapacity(memory, model.Objects, IntPtr.Size);
            memory.Reserve(32L);
            model.Objects.Add(number, stream);
            return new PdfReference(number, 0);
        }

        int Allocate()
        {
            if (lastBound == 0)
            {
                foreach (int bound in model.Objects.Keys)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    lastBound = Math.Max(lastBound, bound);
                }
                if (ids is not null)
                {
                    foreach ((int bound, var pair) in ids)
                    {
                        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                        lastBound = Math.Max(lastBound, bound);
                        lastDeclared = Math.Max(lastDeclared, pair.Number);
                    }
                }
            }
            if (lastDeclared == int.MaxValue || lastBound == int.MaxValue)
            { throw new QpdfParseException("No object identity is available for page preparation.", null, null); }
            if (objects >= limits.MaxContainerSize)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), objects + 1L, limits.MaxContainerSize); }
            objects++;
            lastBound++;
            if (ids is not null)
            {
                var pair = (++lastDeclared, 0);
                ParserContainers.EnsureEntryCapacity(memory, ids, 2L * sizeof(int));
                ids.Add(lastBound, pair);
                if (model.OriginalPairs is { } original)
                {
                    ParserContainers.EnsureEntryCapacity(memory, original, sizeof(int));
                    original.Add(pair, lastBound);
                }
            }
            return lastBound;
        }
    }

    private static void ApplyBoundRotations(Dictionary<int, QpdfObject> objects, IIndirectResolver resolver,
        List<PageLocation> pages, IReadOnlyList<OutputRotation> rotations, QpdfLimits limits,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        if (rotations.Count == 0) { return; }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        List<List<(PdfRotationMode Mode, int Angle)>?> folds;
        try { folds = ResolveRotationFolds(rotations, pages.Count, governor, limits, scratch); }
        catch (ArgumentException error) { throw new QpdfParseException("Invalid page rotation range.", null, null, error); }
        for (int index = 0; index < pages.Count; index++)
        {
            governor.CheckCancellation();
            if (folds[index] is not { Count: > 0 } specifications) { continue; }
            PageLocation location = pages[index];
            PdfReference reference = location.Source ?? throw new InvalidOperationException("Missing prepared page identity.");
            QpdfObject? inherited = ReadInheritedRotation(resolver, location, limits, governor, token);
            PdfDictionary page = RotatePage(location.Page, inherited, specifications, index + 1, governor, memory);
            objects[reference.ObjectNumber] = page;
            pages[index] = new(reference, page);
        }
    }

    internal static async ValueTask<Json.ImportedModel> BindInspectionModelAsync(IQpdfHost host, ImportedInspectionInput imported,
        DocumentEngine? engine, QpdfDocument.VersionParts header, QpdfLimits limits, ResourceGovernor governor,
        ParserMemoryScope memory, CancellationToken token)
    {
        using System.Text.Json.JsonDocument json = await Json.JsonImport.ReadJsonAsync(host, imported.Json,
            null, limits, governor, memory, token).ConfigureAwait(false);
        using System.Text.Json.JsonDocument? patch = imported.FollowingPatch is QpdfPath path
            ? await Json.JsonImport.ReadJsonAsync(host, path, null, limits, governor, memory, token).ConfigureAwait(false) : null;
        Json.ImportedModel model = engine is null
            ? await Json.JsonImport.BuildCreateModelAsync(host, json, patch, true, limits, governor, memory, token).ConfigureAwait(false)
            : await Json.JsonImport.BuildUpdateModelAsync(host, json, engine, header, true, false, true, null, null,
                limits, governor, memory, token).ConfigureAwait(false);
        Json.JsonImport.CheckObjectCount(model, limits);
        if (PdfDictionaryLookup.TryGet(model.Trailer, "Encrypt"u8, governor, token, out QpdfObject declaration)
            && declaration is not PdfNull) { throw new QpdfCryptoException("/Encrypt"); }
        return model;
    }

    internal static PdfDictionary ReadBoundCatalog(Json.ImportedModel model, IIndirectResolver resolver,
        ResourceGovernor governor, CancellationToken token)
    {
        if (!PdfDictionaryLookup.TryGet(model.Trailer, "Root"u8, governor, token, out QpdfObject root)
            || Content.CatalogCopy.ResolveDictionary(root, resolver, governor, token) is not PdfDictionary catalog)
        { throw new QpdfParseException("Imported trailer /Root must resolve to a dictionary.", 0L, null); }
        Json.JsonImport.ValidateCatalogVersion(catalog, resolver, governor, token);
        return catalog;
    }

    internal static async ValueTask<JsonImportOutcome> ImportCoreAsync(
        IQpdfHost host,
        QpdfPath? sourcePdf,
        QpdfPath jsonPath,
        QpdfPath? creationPatch,
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

        if (jsonPath == outputPath || creationPatch == outputPath)
        {
            string message = sourcePdf.HasValue ? "The output file must differ from the JSON file."
                : "The output file must differ from the input file.";
            throw new QpdfParseException(message, 0L, null);
        }

        using ParserMemoryScope? splitMemory = writer.SplitPages.HasValue ? ParserMemoryScope.Create(governor) : null;
        using ParserMemoryScope? sourceRoleMemory = sourcePdf.HasValue && writer.DetachedObjects == JsonDetachedObjects.Preserve
            ? ParserMemoryScope.Create(governor) : null;
        PageTreeRoleSet? sourceRoles = sourceRoleMemory is null ? null
            : new(ParserContainers.CreateSet<int>(sourceRoleMemory), sourceRoleMemory);
        using PublicationInputAliases? ownedAliases = PublicationInputAliases.BeginIfNeeded(governor);
        await using QpdfDocument? document = sourcePdf is QpdfPath input
            ? await QpdfDocument.OpenInspectionCore(host, input, password, limits, governor, diagnostics, recovery,
                cancellationToken, false, replaceInput, sourceRoles).ConfigureAwait(false)
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
            using System.Text.Json.JsonDocument? patch = creationPatch is QpdfPath patchPath
                ? await Json.JsonImport.ReadJsonAsync(host, patchPath, null, limits, governor, importMemory, cancellationToken).ConfigureAwait(false)
                : null;
            model = await Json.JsonImport.BuildCreateModelAsync(host, parsed, patch, retainOriginalPairs, limits, governor,
                importMemory, cancellationToken).ConfigureAwait(false);
            if (!writer.Encryption.PreservesInput)
            {
                model = Json.JsonImport.RemoveEncryptionDeclaration(model, writer.DetachedObjects == JsonDetachedObjects.Preserve,
                    importMemory, governor, cancellationToken);
            }
        }
        else
        {
            model = await Json.JsonImport.BuildUpdateModelAsync(host, parsed, document.Engine, document.HeaderVersionParts, writer.Encryption.PreservesInput,
                writer.ObjectStreams == "preserve", retainOriginalPairs, sourceRoles, replacement,
                limits, governor, importMemory, cancellationToken).ConfigureAwait(false);
        }
        sourceRoles = null;
        sourceRoleMemory?.Dispose();

        byte[]? originalId = document is null ? null : TryGetOriginalId(document.Engine);
        var preparedSecurity = await PrepareOutputSecurityAsync(host, writer.Encryption, document, originalId, importMemory,
            governor, limits, diagnostics, recovery, cancellationToken).ConfigureAwait(false);
        using OutputSecurity? security = preparedSecurity.Security;
        PreparedImportedOutput prepared = PrepareImportedOutput(model, writer, security, limits, importMemory, splitMemory, governor,
            diagnostics, cancellationToken);
        PreparedOutputModel output = prepared.Output;
        int? info = prepared.Graph.InfoNumber;
        int metadata = prepared.Graph.MetadataNumber;
        IReadOnlyDictionary<int, (int Number, int Generation)>? originalIds;
        // Finish orders the whole-file model. Split parts number independently,
        // and typed imports keep their existing numbering.
        if (!writer.SplitPages.HasValue && writer.DetachedObjects != JsonDetachedObjects.RetainAll
            && output.Model[^1].Number != output.Model.Count)
        {
            using ParserMemoryScope numbering = ParserMemoryScope.Create(governor);
            output = OutputModelPreparation.CompactOrdered(output, importMemory, numbering,
                governor, cancellationToken, out Dictionary<int, int> compact);
            if (info.HasValue) { info = compact[info.Value]; }
            if (metadata != 0) { metadata = compact[metadata]; }
            originalIds = OriginalObjectIds.PrepareImportedIds(model.OriginalPairs, prepared.Graph.Mapping,
                compact, importMemory, governor, cancellationToken);
        }
        else
        {
            originalIds = OriginalObjectIds.PrepareImportedIds(model.OriginalPairs, prepared.Graph.Mapping,
                importMemory, governor, cancellationToken);
        }
        if ((replaceInput || writer.SplitPages.HasValue) && document is not null)
        { await document.ReleaseCompleteInputAsync().ConfigureAwait(false); }
        if (writer.SplitPages is int groupSize)
        {
            if (prepared.Pages is null)
            { throw new InvalidOperationException("Missing bound JSON split preparation."); }
            await JsonSplitPublication.PublishAsync(host, outputPath, groupSize, writer, prepared.Version, prepared.Graph,
                prepared.Pages, prepared.Labels, prepared.DetachedRoots, prepared.PreservedPacking, prepared.PruneSplitResources,
                preparedSecurity.Identifier, originalIds, security,
                PublicationInputAliases.Active(governor),
                limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await PublishModelAsync(host, prepared.Version, output.Model, output.ObjectStreams, false, null,
                info, preparedSecurity.Identifier, writer.StaticId, writer.NewlineBeforeEndstream,
                writer.Qdf, originalIds, writer.Linearize, writer.LinearizePass1, outputPath, limits, governor, cancellationToken,
                security, output.EncryptionObject, metadata).ConfigureAwait(false);
        }
        return new JsonImportOutcome(prepared.Graph.PageCount, ResultWarningSnapshot.Read(resultWarnings, diagnostics));
    }

    private readonly record struct PreparedImportedOutput(PdfVersion Version, Json.JsonImport.RenumberedModel Graph,
        PreparedOutputModel Output, IReadOnlyList<PageLocation>? Pages, HashSet<int>? PreservedPacking,
        List<(int Index, PdfDictionary Label)>? Labels, List<QpdfObject>? DetachedRoots, bool PruneSplitResources);

    private static PreparedImportedOutput PrepareImportedOutput(Json.ImportedModel model, JsonImportWriterSettings writer,
        OutputSecurity? security, QpdfLimits limits,
        ParserMemoryScope memory, ParserMemoryScope? splitMemory, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        DocumentPublicationPolicy policy = writer.Labels.Count == 0 ? writer.Publication
            : writer.Publication with { RemoveLabels = true };
        model = ImportedPublication.Apply(model, policy, limits, memory, governor, token, out _);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        Content.PoolResolver resolver = Content.PoolResolver.Create(graph.Memory, model.Objects);
        bool pruneSplitResources = splitMemory is not null
            && new Content.ContentPrunePolicy(true, writer.RemoveUnreferencedResources)
                .ShouldPrune(resolver, model.Trailer, limits, governor, token);
        bool normalizePages = writer.NormalizeContent && !writer.Qdf.Enabled;
        Content.ImageOptimizeRequest images = writer.Images ?? Content.ImageOptimizeRequest.Disabled;
        bool externalize = writer.ExternalizeInlineImages || images.Optimize && !images.KeepInline;
        bool transformContent = normalizePages || externalize || images.Optimize;
        if (writer.Rotations.Count > 0 || transformContent)
        {
            using ParserMemoryScope? retirement = transformContent && writer.DetachedObjects == JsonDetachedObjects.Preserve
                ? ParserMemoryScope.Create(governor) : null;
            HashSet<int>? before = retirement is null ? null : GraphReachability.Find(resolver, model.Trailer, retirement, governor);
            (PdfDictionary trailer, List<PageLocation> locations) = PrepareBoundPages(model, resolver, null, transformContent && writer.CoalesceContents,
                limits, memory, governor, diagnostics, token);
            ApplyBoundRotations(model.Objects, resolver, locations, writer.Rotations, limits, memory, governor, token);
            if (transformContent)
            {
                Content.ContentTransformRequest request = new(false, normalizePages, false, null, externalize, writer.InlineImageMinimumBytes)
                { BoundPageContent = true };
                ImportedPublication.TransformPagePrograms(model, resolver, locations, request, images, limits, memory, governor, diagnostics, token);
                if (retirement is not null && before is not null)
                { GraphReachability.RetireSuperseded(model.Objects, model.CompressedObjects, before, trailer, resolver, retirement, governor, token); }
            }
            memory.Reserve(Json.ImportedModel.AllocationBytes);
            model = model with { Trailer = trailer, CalledAllPages = true };
        }
        model.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject root);
        PdfDictionary? catalog = Content.CatalogCopy.ResolveDictionary(root, resolver, governor, token);
        int level = Content.CatalogCopy.ReadExtensionLevel(catalog, resolver, governor, token);
        (PdfVersion version, int extensionLevel) = NegotiateVersion(model.HeaderVersion, level, writer.MinVersion, writer.ForceVersion, governor);
        RequireEncryptionVersion(security, ref version, ref extensionLevel, writer.ForceVersion, governor);
        using ParserMemoryScope? normalizationMemory = writer.NormalizeContent && writer.Qdf.Enabled ? ParserMemoryScope.Create(governor) : null;
        Json.JsonImport.RenumberedModel renumbered = Json.JsonImport.Renumber(model, limits, governor, diagnostics, memory,
            splitMemory ?? normalizationMemory, writer.CoalesceContents && !transformContent, out IReadOnlyList<PageLocation>? pages, token);
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
        List<QpdfObject>? detachedRoots = null;
        using ParserMemoryScope? publicationWalk = writer.DetachedObjects == JsonDetachedObjects.RetainAll
            ? null : ParserMemoryScope.Create(governor);
        HashSet<int>? reachable = null;
        bool measured = publicationWalk is not null && splitMemory is null && compressed is { Count: > 0 }
            && PdfVersion.CompareParts(version.Major, version.Minor, 1, 5) < 0;
        if (measured && publicationWalk is not null && compressed is not null)
        {
            reachable = ImportedPublication.FindRetainedObjects(model, renumbered, mappedCatalog, writer.DetachedObjects,
                null, publicationWalk, governor, token, out _);
            if (reachable is not null)
            {
                hasPacking = false;
                foreach (int number in compressed)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    if (reachable.Contains(number)) { hasPacking = true; break; }
                }
            }
        }
        (version, extensionLevel) = RequireObjectStreamVersion(version, extensionLevel, hasPacking, governor);
        IIndirectResolver mappedResolver = Content.PoolResolver.Create(graph.Memory, renumbered.Objects);
        PdfDictionaryLookup.TryGet(mappedCatalog, "Extensions"u8, governor, token, out QpdfObject originalExtensions);
        QpdfObject? extensions = PrepareExtensions(originalExtensions, mappedResolver, version, extensionLevel, graph);
        bool extensionsChanged = !ReferenceEquals(extensions, originalExtensions);
        if (extensionsChanged)
        {
            PdfDictionary replacement = extensions is null ? PdfDictionaryRebuild.RemoveEntry(graph, mappedCatalog, "Extensions"u8)
                : PdfDictionaryRebuild.ReplaceEntry(graph, mappedCatalog, "Extensions"u8, extensions);
            mappedCatalog = replacement;
            renumbered.Objects[1] = replacement; renumbered.Model[0] = new(1, replacement);
        }
        if (publicationWalk is not null)
        {
            if (!measured || extensionsChanged)
            {
                reachable = ImportedPublication.FindRetainedObjects(model, renumbered, mappedCatalog, writer.DetachedObjects,
                    splitMemory, publicationWalk, governor, token, out detachedRoots);
            }
            if (reachable is not null)
            { OutputModelPreparation.RetainPublishedObjects(renumbered.Model, compressed, reachable, 1, governor, token); }
            reachable = null;
            publicationWalk.Dispose();
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
        return new(version, renumbered, output, splitMemory is null ? null : pages, compressed, splitLabels, detachedRoots, pruneSplitResources);
    }

}
