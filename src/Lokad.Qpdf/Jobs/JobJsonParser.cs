using System.Text.Json;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Jobs;

/// <summary>
/// Job-JSON parser covering selection, writer, content, overlay, attachment, inspection,
/// and linearization keys with upstream field names. Cryptographic keys reject as crypto;
/// everything outside the supported subset fails explicitly.
/// </summary>
internal static class JobJsonParser
{
    private const int MaxDepth = 32;

    internal static JobPlan Parse(string jobJson, QpdfLimits limits, ResourceGovernor governor)
    {
        if (jobJson is null)
        {
            throw new ArgumentNullException(nameof(jobJson));
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
        long byteCount = System.Text.Encoding.UTF8.GetByteCount(jobJson);
        if (byteCount > limits.MaxInputBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), byteCount, limits.MaxInputBytes);
        }

        governor.CheckCancellation();
        governor.ChargeWork(checked(byteCount + 1L));

        // Admit the transient document model before constructing it: the parsed
        // document retains roughly the input size plus object overhead.
        long modelBudget = checked(byteCount * 2L + 1024L);
        if (modelBudget > limits.MaxMemoryBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxMemoryBytes), modelBudget, limits.MaxMemoryBytes);
        }

        using ConfigurationText textStorage = ConfigurationText.Create(governor);
        textStorage.AdmitJsonCopies(ConfigurationText.JsonCopyBytes(byteCount));
        governor.ReserveMemory(modelBudget);
        JsonDocumentOptions options = new()
        {
            MaxDepth = MaxDepth,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        };
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(jobJson, options);
        }
        catch (JsonException ex)
        {
            governor.ReleaseMemory(modelBudget);
            throw new QpdfParseException($"Invalid job JSON: {ex.Message}.", ex.BytePositionInLine, null, ex);
        }

        try
        {
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("Job JSON must be an object.", 0L, null);
            }

            return ParseProperties(EffectiveProperties(document.RootElement), false, limits, governor, textStorage);
        }
        }
        finally
        {
            governor.ReleaseMemory(modelBudget);
        }
    }

    // JSON objects keep the last value per key. Configuration layers repeat
    // setters; exclusive input/output/page declarations still reject.
    internal static JobPlan ParseProperties(
        IEnumerable<JsonProperty> properties,
        bool configurationLayers,
        QpdfLimits limits,
        ResourceGovernor governor, ConfigurationText textStorage)
    {
        CollationPreparation storage = CollationPreparation.Create(governor);
        textStorage.Collation = storage;
        try
        {
            JobPlan plan = ParsePropertiesCore(properties, configurationLayers, limits, governor, textStorage);
            if (!storage.HasStorage) { storage.Dispose(); return plan; }
            return plan with { CollationStorage = storage };
        }
        catch { storage.Dispose(); throw; }
        finally { textStorage.Collation = null; }
    }

    private static JobPlan ParsePropertiesCore(
        IEnumerable<JsonProperty> properties,
        bool configurationLayers,
        QpdfLimits limits,
        ResourceGovernor governor, ConfigurationText textStorage)
    {
            string? input = null;
            bool empty = false;
            bool seenEmpty = false;
            bool seenInput = false;
            bool seenOutput = false;
            bool seenPages = false;
            string? output = null;
            bool replaceInput = false;
            bool seenReplaceInput = false;
            int? splitPages = null;
            bool seenSplitPages = false;
            JsonElement pages = default;
            bool hasPages = false;
            string? collate = null;
            List<int>? collationSizes = null;
            bool seenCollate = false;
            List<OutputRotation> rotations = new();
            bool seenRotate = false;
            List<PageLabelSpec> labels = new();
            bool seenLabels = false;
            bool seenRemoveLabels = false;
            bool removeLabels = false;
            string? minVersion = null;
            bool seenMin = false;
            string? forceVersion = null;
            bool seenForce = false;
            bool deterministicId = false;
            bool seenDeterministic = false;
            bool staticId = false;
            bool seenStatic = false;
            bool newlineBeforeEndstream = false;
            bool seenNewline = false;
            string? objectStreams = null;
            bool seenObjectStreams = false;
            bool preserveUnreferenced = false;
            bool seenPreserveUnreferenced = false;
            CompressionSettings compressionSettings = CompressionSettings.NativeDefault;
            string? streamData = null;
            bool seenStreamData = false;
            string? compressStreams = null;
            bool seenCompressStreams = false;
            string? decodeLevel = null;
            bool seenDecodeLevel = false;
            long? maxStreamFilters = null;
            bool noDefaultLimits = false;
            bool seenNoDefaultLimits = false;
            bool seenMaxStreamFilters = false;
            bool ignoreXrefStreams = false;
            bool seenIgnoreXrefStreams = false;
            bool reportMemoryUsage = false;
            bool seenReportMemoryUsage = false;
            bool verbose = false;
            bool seenVerbose = false;
            bool progress = false;
            bool seenProgress = false;
            bool rawStreamData = false;
            bool seenRawStreamData = false;
            bool filteredStreamData = false;
            bool seenFilteredStreamData = false;
            string? keepFilesOpen = null;
            bool seenKeepFilesOpen = false;
            long keepFilesOpenThreshold = 200L;
            bool seenKeepFilesOpenThreshold = false;
            bool testJsonSchema = false;
            bool seenTestJsonSchema = false;
            long? parserMaxNesting = null;
            bool seenParserMaxNesting = false;
            long? maxContainerSize = null;
            bool seenMaxContainerSize = false;
            long? maxContainerSizeDamaged = null;
            long? parserMaxErrors = null;
            bool seenParserMaxErrors = false;
            bool seenMaxContainerSizeDamaged = false;
            bool seenGlobal = false;
            bool recompressFlate = false;
            bool seenRecompressFlate = false;
            string? compressionLevel = null;
            bool seenCompressionLevel = false;
            bool warningExit0 = false;
            bool seenWarningExit0 = false;
            bool noWarn = false;
            bool seenNoWarn = false;
            bool suppressRecovery = false;
            bool seenSuppressRecovery = false;
            QpdfRecoveryMode recovery = QpdfRecoveryMode.BoundedRepair;
            bool removeInfo = false;
            bool seenRemoveInfo = false;
            bool removeAcroform = false;
            bool removeStructure = false;
            bool removeMetadata = false;
            bool seenRemoveMetadata = false;
            bool coalesceContents = false;
            bool seenCoalesceContents = false;
            bool externalizeInlineImages = false;
            bool seenExternalizeInlineImages = false;
            long iiMinBytes = 1024L;
            bool seenIiMinBytes = false;
            bool normalizeContent = false;
            bool seenNormalizeContent = false;
            string? removeUnreferencedResources = null;
            bool seenRemoveUnreferencedResources = false;
            bool seenPreserveUnreferencedResources = false;
            List<AttachmentAdd> attachmentsToAdd = new();
            bool seenAddAttachment = false;
            List<AttachmentCopy> attachmentsToCopy = new();
            bool seenCopyAttachments = false;
            List<string> attachmentsToRemove = new();
            bool seenRemoveAttachment = false;
            List<OverlaySpec> overlays = new();
            bool seenOverlay = false;
            List<OverlaySpec> underlays = new();
            bool seenUnderlay = false;
            bool flattenRotation = false;
            bool seenFlattenRotation = false;
            string? flattenAnnotations = null;
            bool seenFlattenAnnotations = false;
            bool generateAppearances = false;
            bool seenGenerateAppearances = false;
            bool qdf = false;
            bool seenQdf = false;
            bool linearize = false;
            bool seenLinearize = false;
            string? linearizePass1 = null;
            bool seenLinearizePass1 = false;
            bool checkLinearization = false;
            bool seenCheckLinearization = false;
            bool check = false;
            bool seenCheck = false;
            bool showLinearization = false;
            bool seenShowLinearization = false;
            bool showNpages = false;
            bool seenShowNpages = false;
            bool showPages = false;
            bool seenShowPages = false;
            bool withImages = false;
            bool seenWithImages = false;
            string? showObject = null;
            int showObjectGeneration = 0;
            bool seenShowObject = false;
            bool showXref = false;
            bool seenShowXref = false;
            bool seenRemoveAcroform = false;
            bool seenRemoveStructure = false;
            bool noOriginalObjectIds = false;
            bool seenNoOriginalObjectIds = false;
            bool optimizeImages = false;
            bool seenOptimizeImages = false;
            long oiMinWidth = 128L;
            bool seenOiMinWidth = false;
            long oiMinHeight = 128L;
            bool seenOiMinHeight = false;
            long oiMinArea = 16384L;
            bool seenOiMinArea = false;
            bool keepInlineImages = false;
            bool seenKeepInlineImages = false;
            int? jpegQuality = null;
            bool seenJpegQuality = false;
            string? jsonVersion = null;
            bool seenJson = false;
            List<string> jsonKeys = new();
            bool seenJsonKey = false;
            bool jsonInput = false;
            bool seenJsonInput = false;
            string? updateFromJson = null;
            bool seenUpdateFromJson = false;
            List<string> jsonObjects = new();
            bool seenJsonObject = false;
            string? jsonStreamData = null;
            bool seenJsonStreamData = false;
            string? jsonStreamPrefix = null;
            bool seenJsonStreamPrefix = false;
            string? jsonOutput = null;
            bool seenJsonOutput = false;
            bool listAttachments = false;
            bool seenListAttachments = false;
            string? showAttachment = null;
            bool seenShowAttachment = false;
            foreach (JsonProperty property in properties)
            {
                governor.CheckCancellation();
                governor.ChargeWork(1L);
                if (IsCryptoKey(property.Name)) { throw new QpdfCryptoException(property.Name); }
                switch (property.Name)
                {
                    case "empty":
                        if (seenEmpty && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key empty.", 0L, null);
                        }

                        seenEmpty = true;
                        if (input is not null) { throw new QpdfParseException("Empty input cannot follow an input file.", 0L, null); }
                        empty = RequireBareFlag(property.Value, "empty", textStorage);
                        break;
                    case "inputFile":
                        if (seenInput)
                        {
                            throw new QpdfParseException("Duplicate job key 'inputFile'.", 0L, null);
                        }

                        seenInput = true;
                        input = RequireString(property.Value, "inputFile", textStorage);
                        empty = false;
                        break;
                    case "outputFile":
                        if (seenOutput)
                        {
                            throw new QpdfParseException("Duplicate job key 'outputFile'.", 0L, null);
                        }

                        seenOutput = true;
                        output = RequireString(property.Value, "outputFile", textStorage);
                        break;
                    case "replaceInput":
                        if (seenReplaceInput)
                        {
                            throw new QpdfParseException("Duplicate job key 'replaceInput'.", 0L, null);
                        }

                        seenReplaceInput = true;
                        replaceInput = RequireBareFlag(property.Value, "replaceInput", textStorage);
                        break;
                    case "splitPages":
                        if (seenSplitPages && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key 'splitPages'.", 0L, null);
                        }

                        seenSplitPages = true;
                        splitPages = Commands.ArgvParser.ParseSplitPagesValue(RequireString(property.Value, "splitPages", textStorage), 0L);
                        break;
                    case "pages":
                        if (seenPages)
                        {
                            throw new QpdfParseException("Duplicate job key 'pages'.", 0L, null);
                        }

                        seenPages = true;
                        pages = property.Value;
                        hasPages = true;
                        break;
                    case "collate":
                        if (seenCollate && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key 'collate'.", 0L, null);
                        }

                        seenCollate = true;
                        collate = RequireString(property.Value, "collate", textStorage);
                        collationSizes = ArgvParser.ParseCollateValue(collate, 0L, textStorage.Collation);
                        break;
                    case "rotate":
                        if (seenRotate && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key 'rotate'.", 0L, null);
                        }

                        seenRotate = true;
                        foreach (OutputRotation rotation in ParseRotateArray(property.Value, textStorage))
                        {
                            if (configurationLayers)
                            {
                                rotations.RemoveAll(previous => string.Equals(previous.Range, rotation.Range, StringComparison.Ordinal));
                            }
                            rotations.Add(rotation);
                        }
                        break;
                    case "setPageLabels":
                        if (seenLabels && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key 'setPageLabels'.", 0L, null);
                        }

                        seenLabels = true;
                        if (configurationLayers) { labels.Clear(); }
                        labels.AddRange(ParseLabelArray(property.Value, textStorage));
                        break;
                    case "removePageLabels":
                        if (seenRemoveLabels && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key 'removePageLabels'.", 0L, null);
                        }

                        seenRemoveLabels = true;
                        if (property.Value.ValueKind != JsonValueKind.True && property.Value.ValueKind != JsonValueKind.False)
                        {
                            throw new QpdfParseException("Job \"removePageLabels\" must be a boolean.", 0L, null);
                        }

                        removeLabels = property.Value.ValueKind == JsonValueKind.True;
                        break;
                    case "minVersion":
                        if (seenMin && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027minVersion\u0027.", 0L, null);
                        }

                        seenMin = true;
                        minVersion = RequireString(property.Value, "minVersion", textStorage);
                        break;
                    case "forceVersion":
                        if (seenForce && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027forceVersion\u0027.", 0L, null);
                        }

                        seenForce = true;
                        forceVersion = RequireString(property.Value, "forceVersion", textStorage);
                        break;
                    case "deterministicId":
                        if (seenDeterministic && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027deterministicId\u0027.", 0L, null);
                        }

                        seenDeterministic = true;
                        deterministicId = RequireBareFlag(property.Value, "deterministicId", textStorage);
                        break;
                    case "staticId":
                        if (seenStatic && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027staticId\u0027.", 0L, null);
                        }

                        seenStatic = true;
                        staticId = RequireBareFlag(property.Value, "staticId", textStorage);
                        break;
                    case "newlineBeforeEndstream":
                        if (seenNewline && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027newlineBeforeEndstream\u0027.", 0L, null);
                        }

                        seenNewline = true;
                        newlineBeforeEndstream = RequireBareFlag(property.Value, "newlineBeforeEndstream", textStorage);
                        break;
                    case "objectStreams":
                        if (seenObjectStreams && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027objectStreams\u0027.", 0L, null);
                        }

                        seenObjectStreams = true;
                        objectStreams = RequireObjectStreams(property.Value, textStorage);
                        break;
                    case "preserveUnreferenced":
                        if (seenPreserveUnreferenced && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027preserveUnreferenced\u0027.", 0L, null);
                        }

                        seenPreserveUnreferenced = true;
                        preserveUnreferenced = RequireBareFlag(property.Value, "preserveUnreferenced", textStorage);
                        break;
                    case "streamData":
                        if (seenStreamData && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027streamData\u0027.", 0L, null);
                        }

                        seenStreamData = true;
                        streamData = RequireString(property.Value, "streamData", textStorage);
                        compressionSettings = compressionSettings.WithStreamData(streamData);
                        break;
                    case "compressStreams":
                        if (seenCompressStreams && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027compressStreams\u0027.", 0L, null);
                        }

                        seenCompressStreams = true;
                        compressStreams = RequireString(property.Value, "compressStreams", textStorage);
                        compressionSettings = compressionSettings.WithCompressStreams(compressStreams);
                        break;
                    case "decodeLevel":
                        if (seenDecodeLevel && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027decodeLevel\u0027.", 0L, null);
                        }

                        seenDecodeLevel = true;
                        decodeLevel = RequireString(property.Value, "decodeLevel", textStorage);
                        compressionSettings = compressionSettings.WithDecodeLevel(decodeLevel);
                        break;
                    case "global":
                        if (seenGlobal && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027global\u0027.", 0L, null);
                        }

                        seenGlobal = true;
                        if (property.Value.ValueKind != JsonValueKind.Object)
                        {
                            throw new QpdfParseException("Job \u0027global\u0027 must be an object.", 0L, null);
                        }

                        foreach (JsonProperty globalProperty in EffectiveProperties(property.Value))
                        {
                            switch (globalProperty.Name)
                            {
                                case "maxStreamFilters":
                                    if (seenMaxStreamFilters && !configurationLayers)
                                    {
                                        throw new QpdfParseException("Duplicate job key \u0027global.maxStreamFilters\u0027.", 0L, null);
                                    }

                                    seenMaxStreamFilters = true;
                                    maxStreamFilters = RequireMaxStreamFilters(globalProperty.Value, textStorage);
                                    break;
                                case "parserMaxNesting":
                                    if (seenParserMaxNesting && !configurationLayers)
                                    {
                                        throw new QpdfParseException("Duplicate job key \u0027global.parserMaxNesting\u0027.", 0L, null);
                                    }

                                    seenParserMaxNesting = true;
                                    parserMaxNesting = RequireParserMaxNesting(globalProperty.Value, textStorage);
                                    break;
                                case "noDefaultLimits":
                                    if (seenNoDefaultLimits && !configurationLayers)
                                    {
                                        throw new QpdfParseException("Duplicate job key \u0027global.noDefaultLimits\u0027.", 0L, null);
                                    }

                                    seenNoDefaultLimits = true;
                                    noDefaultLimits = RequireBareFlag(globalProperty.Value, "global.noDefaultLimits", textStorage);
                                    break;
                                case "parserMaxContainerSize":
                                    if (seenMaxContainerSize && !configurationLayers)
                                    {
                                        throw new QpdfParseException("Duplicate job key \u0027global.parserMaxContainerSize\u0027.", 0L, null);
                                    }

                                    seenMaxContainerSize = true;
                                    maxContainerSize = RequireContainerSize(globalProperty.Value, "parserMaxContainerSize", textStorage);
                                    break;
                                case "parserMaxContainerSizeDamaged":
                                    if (seenMaxContainerSizeDamaged && !configurationLayers)
                                    {
                                        throw new QpdfParseException("Duplicate job key \u0027global.parserMaxContainerSizeDamaged\u0027.", 0L, null);
                                    }

                                    seenMaxContainerSizeDamaged = true;
                                    maxContainerSizeDamaged = RequireContainerSize(globalProperty.Value, "parserMaxContainerSizeDamaged", textStorage);
                                    break;
                                case "parserMaxErrors":
                                    if (seenParserMaxErrors && !configurationLayers)
                                    {
                                        throw new QpdfParseException("Duplicate job key \u0027global.parserMaxErrors\u0027.", 0L, null);
                                    }

                                    seenParserMaxErrors = true;
                                    parserMaxErrors = RequireParserMaxErrors(globalProperty.Value, textStorage);
                                    break;
                                default:
                                    throw new QpdfParseException("Unsupported job key \u0027global." + globalProperty.Name + "\u0027 in the narrow selection profile.", 0L, null);
                            }
                        }

                        break;
                    case "recompressFlate":
                        if (seenRecompressFlate && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027recompressFlate\u0027.", 0L, null);
                        }

                        seenRecompressFlate = true;
                        recompressFlate = RequireBareFlag(property.Value, "recompressFlate", textStorage);
                        break;
                    case "compressionLevel":
                        if (seenCompressionLevel && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027compressionLevel\u0027.", 0L, null);
                        }

                        seenCompressionLevel = true;
                        compressionLevel = RequireString(property.Value, "compressionLevel", textStorage);
                        CompressionRequest.ParseLevel(compressionLevel, "compressionLevel");
                        break;
                    case "removeInfo":
                        if (seenRemoveInfo && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027removeInfo\u0027.", 0L, null);
                        }

                        seenRemoveInfo = true;
                        removeInfo = RequireBareFlag(property.Value, "removeInfo", textStorage);
                        break;
                    case "removeMetadata":
                        if (seenRemoveMetadata && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027removeMetadata\u0027.", 0L, null);
                        }

                        seenRemoveMetadata = true;
                        removeMetadata = RequireBareFlag(property.Value, "removeMetadata", textStorage);
                        break;
                    case "coalesceContents":
                        if (seenCoalesceContents && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027coalesceContents\u0027.", 0L, null);
                        }

                        seenCoalesceContents = true;
                        coalesceContents = RequireBareFlag(property.Value, "coalesceContents", textStorage);
                        break;
                    case "normalizeContent":
                        if (seenNormalizeContent && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027normalizeContent\u0027.", 0L, null);
                        }

                        seenNormalizeContent = true;
                        normalizeContent = RequireNormalizeContent(property.Value, textStorage);
                        break;
                    case "externalizeInlineImages":
                        if (seenExternalizeInlineImages && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key " + (char)39 + "externalizeInlineImages" + (char)39 + ".", 0L, null);
                        }

                        seenExternalizeInlineImages = true;
                        externalizeInlineImages = RequireBareFlag(property.Value, "externalizeInlineImages", textStorage);
                        break;
                    case "iiMinBytes":
                        if (seenIiMinBytes && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key " + (char)39 + "iiMinBytes" + (char)39 + ".", 0L, null);
                        }

                        seenIiMinBytes = true;
                        iiMinBytes = RequireIiMinBytes(property.Value, textStorage);
                        break;
                    case "removeUnreferencedResources":
                        if (seenRemoveUnreferencedResources && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027removeUnreferencedResources\u0027.", 0L, null);
                        }

                        seenRemoveUnreferencedResources = true;
                        removeUnreferencedResources = RequireString(property.Value, "removeUnreferencedResources", textStorage);
                        if (removeUnreferencedResources != "auto" && removeUnreferencedResources != "yes" && removeUnreferencedResources != "no")
                        {
                            throw new QpdfParseException("Job \u0027removeUnreferencedResources\u0027 must be one of: auto, yes, no.", 0L, null);
                        }

                        break;

                    case "preserveUnreferencedResources":
                        if (seenPreserveUnreferencedResources && !configurationLayers)
                        {
                                throw new QpdfParseException("Duplicate job key preserveUnreferencedResources.", 0L, null);
                        }

                        seenPreserveUnreferencedResources = true;
                        if (RequireBareFlag(property.Value, "preserveUnreferencedResources", textStorage))
                        {
                            removeUnreferencedResources = "no";
                        }

                        break;
                    case "overlay":
                        if (seenOverlay && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key overlay.", 0L, null);
                        }

                        seenOverlay = true;
                        overlays.AddRange(ParseOverlayArray(property.Value, "overlay", textStorage));
                        break;
                    case "underlay":
                        if (seenUnderlay && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key underlay.", 0L, null);
                        }

                        seenUnderlay = true;
                        underlays.AddRange(ParseOverlayArray(property.Value, "underlay", textStorage));
                        break;
                    case "flattenRotation":
                        if (seenFlattenRotation && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key flattenRotation.", 0L, null);
                        }

                        seenFlattenRotation = true;
                        flattenRotation = RequireBareFlag(property.Value, "flattenRotation", textStorage);
                        break;
                    case "flattenAnnotations":
                        if (seenFlattenAnnotations && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key flattenAnnotations.", 0L, null);
                        }

                        seenFlattenAnnotations = true;
                        flattenAnnotations = RequireString(property.Value, "flattenAnnotations", textStorage);
                        if (flattenAnnotations != "all" && flattenAnnotations != "print" && flattenAnnotations != "screen")
                        {
                            throw new QpdfParseException("Job flattenAnnotations must be one of: all, print, screen.", 0L, null);
                        }

                        break;
                    case "generateAppearances":
                        if (seenGenerateAppearances && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key generateAppearances.", 0L, null);
                        }

                        seenGenerateAppearances = true;
                        generateAppearances = RequireBareFlag(property.Value, "generateAppearances", textStorage);
                        break;
                    case "qdf":
                        if (seenQdf && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key qdf.", 0L, null);
                        }

                        seenQdf = true;
                        qdf = RequireBareFlag(property.Value, "qdf", textStorage);
                        if (qdf) { compressionSettings = compressionSettings.WithQdf(); }
                        break;
                    case "linearize":
                        if (seenLinearize && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key linearize.", 0L, null);
                        }

                        seenLinearize = true;
                        linearize = RequireBareFlag(property.Value, "linearize", textStorage);
                        break;
                    case "linearizePass1":
                        if (seenLinearizePass1 && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key linearizePass1.", 0L, null);
                        }

                        seenLinearizePass1 = true;
                        linearizePass1 = RequireString(property.Value, "linearizePass1", textStorage);
                        if (linearizePass1.Length == 0)
                        {
                            throw new QpdfParseException("Job \"linearizePass1\" must not be empty.", 0L, null);
                        }

                        break;
                    case "check":
                        if (seenCheck && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key check.", 0L, null);
                        }

                        seenCheck = true;
                        if (property.Value.ValueKind != JsonValueKind.String || textStorage.ReadJsonString(property.Value) != string.Empty)
                        {
                            throw new QpdfParseException("Job check must be an empty string.", 0L, null);
                        }

                        check = true;
                        break;
                    case "checkLinearization":
                        if (seenCheckLinearization && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key checkLinearization.", 0L, null);
                        }

                        seenCheckLinearization = true;
                        checkLinearization = RequireBareFlag(property.Value, "checkLinearization", textStorage);
                        break;
                    case "showLinearization":
                        if (seenShowLinearization && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key showLinearization.", 0L, null);
                        }

                        seenShowLinearization = true;
                        showLinearization = RequireBareFlag(property.Value, "showLinearization", textStorage);
                        break;
                    case "showNpages":
                        if (seenShowNpages && !configurationLayers)
                        {
                                throw new QpdfParseException("Duplicate job key showNpages.", 0L, null);
                        }

                        seenShowNpages = true;
                        showNpages = RequireBareFlag(property.Value, "showNpages", textStorage);
                        break;
                    case "showPages":
                        if (seenShowPages && !configurationLayers)
                        {
                                throw new QpdfParseException("Duplicate job key showPages.", 0L, null);
                        }

                        seenShowPages = true;
                        showPages = RequireBareFlag(property.Value, "showPages", textStorage);
                        break;
                    case "withImages":
                        if (seenWithImages && !configurationLayers)
                        {
                                throw new QpdfParseException("Duplicate job key withImages.", 0L, null);
                        }

                        seenWithImages = true;
                        withImages = RequireBareFlag(property.Value, "withImages", textStorage);
                        break;
                    case "showObject":
                        if (seenShowObject && !configurationLayers)
                        {
                                throw new QpdfParseException("Duplicate job key showObject.", 0L, null);
                        }

                        seenShowObject = true;
                        showObject = RequireString(property.Value, "showObject", textStorage);
                        var selector = SelectionPlan.ParseInspectionSelector(showObject, showObjectGeneration);
                        showObjectGeneration = selector.Generation;
                        showObject = selector.Trailer ? "trailer"
                            : selector.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
                                + selector.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "showXref":
                        if (seenShowXref && !configurationLayers)
                        {
                                throw new QpdfParseException("Duplicate job key showXref.", 0L, null);
                        }

                        seenShowXref = true;
                        showXref = RequireBareFlag(property.Value, "showXref", textStorage);
                        break;
                    case "removeAcroform":
                        if (seenRemoveAcroform && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key removeAcroform.", 0L, null);
                        }

                        seenRemoveAcroform = true;
                        removeAcroform = RequireBareFlag(property.Value, "removeAcroform", textStorage);
                        break;
                    case "removeStructure":
                        if (seenRemoveStructure && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key removeStructure.", 0L, null);
                        }

                        seenRemoveStructure = true;
                        removeStructure = RequireBareFlag(property.Value, "removeStructure", textStorage);
                        break;
                    case "noOriginalObjectIds":
                        if (seenNoOriginalObjectIds && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key noOriginalObjectIds.", 0L, null);
                        }

                        seenNoOriginalObjectIds = true;
                        noOriginalObjectIds = RequireBareFlag(property.Value, "noOriginalObjectIds", textStorage);
                        break;
                    case "optimizeImages":
                        if (seenOptimizeImages && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key optimizeImages.", 0L, null);
                        }

                        seenOptimizeImages = true;
                        optimizeImages = RequireBareFlag(property.Value, "optimizeImages", textStorage);
                        break;
                    case "oiMinWidth":
                        if (seenOiMinWidth && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key oiMinWidth.", 0L, null);
                        }

                        seenOiMinWidth = true;
                        oiMinWidth = RequireOiMin(property.Value, "oiMinWidth", textStorage);
                        break;
                    case "oiMinHeight":
                        if (seenOiMinHeight && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key oiMinHeight.", 0L, null);
                        }

                        seenOiMinHeight = true;
                        oiMinHeight = RequireOiMin(property.Value, "oiMinHeight", textStorage);
                        break;
                    case "oiMinArea":
                        if (seenOiMinArea && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key oiMinArea.", 0L, null);
                        }

                        seenOiMinArea = true;
                        oiMinArea = RequireOiMin(property.Value, "oiMinArea", textStorage);
                        break;
                    case "keepInlineImages":
                        if (seenKeepInlineImages && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key keepInlineImages.", 0L, null);
                        }

                        seenKeepInlineImages = true;
                        keepInlineImages = RequireBareFlag(property.Value, "keepInlineImages", textStorage);
                        break;
                    case "jpegQuality":
                        if (seenJpegQuality && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key jpegQuality.", 0L, null);
                        }

                        seenJpegQuality = true;
                        jpegQuality = RequireJpegQuality(property.Value, textStorage);
                        break;
                    case "listAttachments":
                        if (seenListAttachments && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027listAttachments\u0027.", 0L, null);
                        }

                        seenListAttachments = true;
                        listAttachments = RequireBareFlag(property.Value, "listAttachments", textStorage);
                        break;
                    case "ignoreXrefStreams":
                        if (seenIgnoreXrefStreams && !configurationLayers)
                        {
                                throw new QpdfParseException("Duplicate job key ignoreXrefStreams.", 0L, null);
                        }

                        seenIgnoreXrefStreams = true;
                        ignoreXrefStreams = RequireBareFlag(property.Value, "ignoreXrefStreams", textStorage);
                        break;
                    case "reportMemoryUsage":
                        if (seenReportMemoryUsage && !configurationLayers)
                        {
                                throw new QpdfParseException("Duplicate job key reportMemoryUsage.", 0L, null);
                        }

                        seenReportMemoryUsage = true;
                        reportMemoryUsage = RequireBareFlag(property.Value, "reportMemoryUsage", textStorage);
                        break;
                    case "verbose":
                        if (seenVerbose && !configurationLayers)
                        {
                                throw new QpdfParseException("Duplicate job key verbose.", 0L, null);
                        }

                        seenVerbose = true;
                        verbose = RequireBareFlag(property.Value, "verbose", textStorage);
                        break;
                    case "progress":
                        if (seenProgress && !configurationLayers)
                        {
                                throw new QpdfParseException("Duplicate job key progress.", 0L, null);
                        }

                        seenProgress = true;
                        progress = RequireBareFlag(property.Value, "progress", textStorage);
                        break;
                    case "keepFilesOpen":
                        if (seenKeepFilesOpen && !configurationLayers)
                        {
                                throw new QpdfParseException("Duplicate job key keepFilesOpen.", 0L, null);
                        }

                        seenKeepFilesOpen = true;
                        keepFilesOpen = RequireKeepFilesOpen(property.Value, textStorage);
                        break;
                    case "keepFilesOpenThreshold":
                        if (seenKeepFilesOpenThreshold && !configurationLayers)
                        {
                                throw new QpdfParseException("Duplicate job key keepFilesOpenThreshold.", 0L, null);
                        }

                        seenKeepFilesOpenThreshold = true;
                        keepFilesOpenThreshold = RequireKeepFilesOpenThreshold(property.Value, textStorage);
                        break;
                    case "rawStreamData":
                        if (seenRawStreamData && !configurationLayers)
                        {
                                throw new QpdfParseException("Duplicate job key rawStreamData.", 0L, null);
                        }

                        seenRawStreamData = true;
                        rawStreamData = RequireBareFlag(property.Value, "rawStreamData", textStorage);
                        break;
                    case "filteredStreamData":
                        if (seenFilteredStreamData && !configurationLayers)
                        {
                                throw new QpdfParseException("Duplicate job key filteredStreamData.", 0L, null);
                        }

                        seenFilteredStreamData = true;
                        filteredStreamData = RequireBareFlag(property.Value, "filteredStreamData", textStorage);
                        break;
                    case "testJsonSchema":
                        if (seenTestJsonSchema && !configurationLayers)
                        {
                                throw new QpdfParseException("Duplicate job key testJsonSchema.", 0L, null);
                        }

                        seenTestJsonSchema = true;
                        testJsonSchema = RequireBareFlag(property.Value, "testJsonSchema", textStorage);
                        break;
                    case "showAttachment":
                        if (seenShowAttachment && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027showAttachment\u0027.", 0L, null);
                        }

                        seenShowAttachment = true;
                        showAttachment = RequireString(property.Value, "showAttachment", textStorage);
                        if (showAttachment.Length == 0)
                        {
                            throw new QpdfParseException("Job \"showAttachment\" must not be empty.", 0L, null);
                        }

                        break;
                    case "addAttachment":
                        if (seenAddAttachment && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027addAttachment\u0027.", 0L, null);
                        }

                        seenAddAttachment = true;
                        attachmentsToAdd.AddRange(ParseAddAttachmentArray(property.Value, textStorage));
                        break;
                    case "removeAttachment":
                        if (seenRemoveAttachment && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027removeAttachment\u0027.", 0L, null);
                        }

                        seenRemoveAttachment = true;
                        attachmentsToRemove.AddRange(ParseRemoveAttachmentArray(property.Value, textStorage));
                        break;
                    case "copyAttachmentsFrom":
                        if (seenCopyAttachments && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key copyAttachmentsFrom.", 0L, null);
                        }

                        seenCopyAttachments = true;
                        attachmentsToCopy.AddRange(ParseCopyAttachmentsArray(property.Value, textStorage));
                        break;
                    case "json":
                        if (seenJson && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key 'json'.", 0L, null);
                        }

                        seenJson = true;
                        jsonVersion = ParseJsonVersion(property.Value, textStorage);
                        break;
                    case "jsonKey":
                        if (seenJsonKey && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key 'jsonKey'.", 0L, null);
                        }

                        seenJsonKey = true;
                        jsonKeys.AddRange(ParseJsonKeyArray(property.Value, textStorage));
                        break;
                    case "jsonObject":
                        if (seenJsonObject && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key 'jsonObject'.", 0L, null);
                        }

                        seenJsonObject = true;
                        jsonObjects.AddRange(ParseJsonObjectArray(property.Value, textStorage));
                        break;
                    case "jsonStreamData":
                        if (seenJsonStreamData && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key 'jsonStreamData'.", 0L, null);
                        }

                        seenJsonStreamData = true;
                        jsonStreamData = RequireString(property.Value, "jsonStreamData", textStorage);
                        if (jsonStreamData != "none" && jsonStreamData != "inline" && jsonStreamData != "file")
                        {
                            throw new QpdfParseException("Job \"jsonStreamData\" must be one of: none, inline, file.", 0L, null);
                        }

                        break;
                    case "jsonStreamPrefix":
                        if (seenJsonStreamPrefix && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key 'jsonStreamPrefix'.", 0L, null);
                        }

                        seenJsonStreamPrefix = true;
                        jsonStreamPrefix = RequireString(property.Value, "jsonStreamPrefix", textStorage);
                        if (jsonStreamPrefix.Length > QpdfPath.MaxLength || jsonStreamPrefix.Contains((char)0))
                        {
                            throw new QpdfParseException("Job \"jsonStreamPrefix\" is not a valid logical path.", 0L, null);
                        }

                        break;
                    case "jsonOutput":
                        if (seenJsonOutput && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key 'jsonOutput'.", 0L, null);
                        }

                        seenJsonOutput = true;
                        jsonOutput = ParseJsonOutput(property.Value, textStorage);
                        break;
                    case "jsonInput":
                        if (seenJsonInput && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key 'jsonInput'.", 0L, null);
                        }

                        seenJsonInput = true;
                        jsonInput = RequireBareFlag(property.Value, "jsonInput", textStorage);
                        break;
                    case "updateFromJson":
                        if (seenUpdateFromJson && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key 'updateFromJson'.", 0L, null);
                        }

                        seenUpdateFromJson = true;
                        updateFromJson = RequireString(property.Value, "updateFromJson", textStorage);
                        if (updateFromJson.Length == 0)
                        {
                            throw new QpdfParseException("Job \"updateFromJson\" must not be empty.", 0L, null);
                        }

                        break;
                    case "warningExit0":
                        if (seenWarningExit0 && !configurationLayers)
                        {
                                throw new QpdfParseException("Duplicate job key warningExit0.", 0L, null);
                        }

                        seenWarningExit0 = true;
                        warningExit0 = RequireBareFlag(property.Value, "warningExit0", textStorage);
                        break;
                    case "noWarn":
                        if (seenNoWarn && !configurationLayers)
                        {
                                throw new QpdfParseException("Duplicate job key noWarn.", 0L, null);
                        }

                        seenNoWarn = true;
                        noWarn = RequireBareFlag(property.Value, "noWarn", textStorage);
                        break;
                    case "suppressRecovery":
                        if (seenSuppressRecovery && !configurationLayers)
                        {
                            throw new QpdfParseException("Duplicate job key suppressRecovery.", 0L, null);
                        }

                        seenSuppressRecovery = true;
                        suppressRecovery = RequireBareFlag(property.Value, "suppressRecovery", textStorage);
                        recovery = suppressRecovery ? QpdfRecoveryMode.Strict : QpdfRecoveryMode.BoundedRepair;
                        break;
                    default:
                        throw new QpdfParseException($"Unsupported job key '{property.Name}' in the narrow selection profile.", 0L, null);
                }
            }

            if (configurationLayers)
            {
                rotations.Sort(static (left, right) => string.CompareOrdinal(left.Range, right.Range));
            }

            if (seenWithImages && !showPages)
            {
                throw new QpdfParseException("Job withImages requires showPages.", 0L, null);
            }

            if (showObject is not null && output is not null)
            {
                var selector = SelectionPlan.ParseInspectionSelector(showObject, showObjectGeneration);
                if (!selector.Trailer && selector.Number <= 0)
                {
                    throw new QpdfParseException("An output file cannot accompany a disabled object inspection.", 0L, null);
                }
            }

            if (input == "-")
            {
                throw new QpdfParseException("Standard input is not a seekable PDF input.", 0L, null);
            }

            if (output == "-")
            {
                throw new QpdfParseException("Stdout output requires an explicit streaming contract.", 0L, null);
            }

            if (input is null && !empty)
            {
                throw new QpdfParseException("Job JSON requires \"inputFile\".", 0L, null);
            }

            if (empty && input is not null)
            {
                throw new QpdfParseException("Job \"empty\" cannot be combined with \"inputFile\".", 0L, null);
            }

            bool wantAttachments = listAttachments || showAttachment is not null;
            if (check || (output is null && (showAttachment is not null || showObject is not null
                || showNpages || showPages || showXref || listAttachments || checkLinearization || showLinearization)))
            {
                if (output is not null || seenReplaceInput || seenSplitPages
                    || seenLabels || seenRemoveLabels
                    || seenFlattenRotation || seenFlattenAnnotations || seenGenerateAppearances
                    || seenLinearizePass1
                    || seenOptimizeImages || seenOiMinWidth || seenOiMinHeight || seenOiMinArea || seenKeepInlineImages || seenJpegQuality
                    || seenAddAttachment || seenRemoveAttachment || seenCopyAttachments
                    || seenJson || seenJsonKey || seenJsonObject || seenJsonStreamData || seenJsonStreamPrefix || seenJsonOutput || seenJsonInput || seenUpdateFromJson
                    || seenTestJsonSchema)
                {
                    throw new QpdfParseException("Job inspection cannot be combined with output or these transformation/JSON settings.", 0L, null);
                }

                List<PdfPageSource> inspectionSources = new();
                if (hasPages)
                {
                    foreach ((string file, string? range) in ParsePagesEntries(pages, textStorage))
                    {
                        if (empty && file == ".") { throw new QpdfParseException("Cannot select from '.' with empty input.", 0L, null); }
                        inspectionSources.Add(new PdfPageSource(SelectionPlan.CheckedPath(file), PdfPageSelectionOptions.FromRange(range)));
                    }
                }
                PdfPageCollation? inspectionCollation = null;
                if (seenCollate && hasPages)
                {
                    List<int> sizes = collationSizes ?? throw new InvalidOperationException("Missing parsed collation sizes.");
                    if (sizes.Count != 1 && sizes.Count != inspectionSources.Count)
                    {
                        throw new QpdfParseException("Job collate must hold one value or one per pages entry.", 0L, null);
                    }
                    inspectionCollation = (textStorage.Collation ?? throw new InvalidOperationException("Missing collation owner.")).CreatePolicy(sizes);
                }
                (QpdfPath checkInput, bool checkEmpty) = ResolveInspectionInput(input, empty, "check");
                return new CheckPlan(checkInput, recovery)
                {
                    EmptyDocument = checkEmpty,
                    Sources = inspectionSources,
                    Collation = inspectionCollation,
                    Rotations = rotations,
                    RunCheck = check,
                    Overlays = overlays,
                    Underlays = underlays,
                    AttachmentKey = showAttachment,
                    RawStreamData = rawStreamData,
                    FilteredStreamData = filteredStreamData,
                    ShowNpages = showNpages,
                    ShowPages = showPages,
                    ShowXref = showXref,
                    ObjectSelector = showObject,
                    ListAttachments = listAttachments,
                    WithImages = withImages,
                    Qdf = qdf,
                    Linearize = linearize,
                    NoOriginalObjectIds = noOriginalObjectIds,
                    CheckLinearization = checkLinearization,
                    ShowLinearization = showLinearization,
                    WarningExitZero = warningExit0,
                    NoWarn = noWarn,
                    Verbose = verbose,
                    Progress = progress,
                    ReportMemoryUsage = reportMemoryUsage,
                    IgnoreXrefStreams = ignoreXrefStreams,
                    MinVersion = minVersion,
                    ForceVersion = forceVersion,
                    DeterministicId = deterministicId,
                    StaticId = staticId,
                    NewlineBeforeEndstream = newlineBeforeEndstream,
                    ObjectStreams = objectStreams,
                    PreserveUnreferenced = preserveUnreferenced,
                    CompressionState = compressionSettings.State,
                    StreamData = streamData,
                    CompressStreams = compressStreams,
                    DecodeLevel = decodeLevel,
                    MaxStreamFilters = maxStreamFilters,
                    ParserMaxNesting = parserMaxNesting,
                    MaxContainerSize = maxContainerSize,
                    MaxContainerSizeDamaged = maxContainerSizeDamaged,
                    ParserMaxErrors = parserMaxErrors,
                    NoDefaultLimits = noDefaultLimits,
                    RecompressFlate = recompressFlate,
                    CompressionLevel = compressionLevel,
                    RemoveInfo = removeInfo,
                    RemoveAcroform = seenRemoveAcroform,
                    RemoveStructure = seenRemoveStructure,
                    RemoveMetadata = removeMetadata,
                    CoalesceContents = coalesceContents,
                    NormalizeContent = normalizeContent || (qdf && !seenNormalizeContent),
                    RemoveUnreferencedResources = removeUnreferencedResources,
                    ExternalizeInlineImages = externalizeInlineImages,
                    IiMinBytes = iiMinBytes,
                };
            }

            if (wantAttachments && hasPages)
            {
                throw new QpdfParseException("Job attachment keys cannot be combined with \"pages\".", 0L, null);
            }

            if (wantAttachments)
            {
                if (showAttachment is not null)
                {
                    if (output is null)
                    {
                        throw new QpdfParseException("Job JSON requires \"outputFile\" with \"showAttachment\".", 0L, null);
                    }

                    (QpdfPath attachmentInput, bool attachmentEmpty) = ResolveInspectionInput(input, empty, "attachment inspection");
                    return new AttachmentPlan(attachmentInput, SelectionPlan.CheckedPath(output), showAttachment, listAttachments, recovery) with { EmptyDocument = attachmentEmpty, IgnoreXrefStreams = ignoreXrefStreams, ReportMemoryUsage = reportMemoryUsage, Verbose = verbose, Progress = progress };
                }

                if (output is not null)
                {
                    throw new QpdfParseException("Job \"outputFile\" must not be given without \"showAttachment\".", 0L, null);
                }

                (QpdfPath attachmentListInput, bool attachmentListEmpty) = ResolveInspectionInput(input, empty, "attachment inspection");
                return new AttachmentPlan(attachmentListInput, null, null, true, recovery) with { EmptyDocument = attachmentListEmpty, IgnoreXrefStreams = ignoreXrefStreams, ReportMemoryUsage = reportMemoryUsage, Verbose = verbose, Progress = progress };
            }

            if (checkLinearization || showLinearization)
            {
                if (hasPages || output is not null
                    || wantAttachments || attachmentsToAdd.Count > 0 || attachmentsToRemove.Count > 0 || attachmentsToCopy.Count > 0
                    || seenRotate || seenLabels || seenRemoveLabels
                    || seenMin || seenForce || seenDeterministic || seenStatic || seenNewline
                    || seenObjectStreams || seenPreserveUnreferenced
                    || seenStreamData || seenCompressStreams || seenDecodeLevel || seenMaxStreamFilters || seenNoDefaultLimits || seenTestJsonSchema || seenParserMaxNesting || seenMaxContainerSize || seenMaxContainerSizeDamaged || seenParserMaxErrors || seenRecompressFlate || seenCompressionLevel
                    || seenRemoveInfo || seenRemoveMetadata
                    || seenCoalesceContents || seenNormalizeContent || seenRemoveUnreferencedResources || seenExternalizeInlineImages || seenIiMinBytes
                    || seenOverlay || seenUnderlay
                    || seenFlattenRotation || seenFlattenAnnotations || seenGenerateAppearances
                    || seenQdf || seenLinearize || seenNoOriginalObjectIds
                    || seenOptimizeImages || seenOiMinWidth || seenOiMinHeight || seenOiMinArea || seenKeepInlineImages || seenJpegQuality
                    || seenJson || seenJsonKey || seenJsonObject || seenJsonStreamData || seenJsonStreamPrefix || seenJsonOutput || seenJsonInput || seenUpdateFromJson)
                {
                    throw new QpdfParseException("Job linearization inspection cannot be combined with transformation or other inspection keys.", 0L, null);
                }

                (QpdfPath inspectionInput, bool emptyDocument) = ResolveInspectionInput(input, empty, "linearization inspection");
                return new LinearizationPlan(inspectionInput, recovery)
                {
                    Check = checkLinearization,
                    Show = showLinearization,
                    EmptyDocument = emptyDocument,
                    ReportMemoryUsage = reportMemoryUsage,
                    Verbose = verbose,
                    Progress = progress,
                    IgnoreXrefStreams = ignoreXrefStreams,
                };
            }

            if (showNpages)
            {
                if (hasPages || output is not null
                    || wantAttachments || attachmentsToAdd.Count > 0 || attachmentsToRemove.Count > 0 || attachmentsToCopy.Count > 0
                    || seenRotate || seenLabels || seenRemoveLabels
                    || seenMin || seenForce || seenDeterministic || seenStatic || seenNewline
                    || seenObjectStreams || seenPreserveUnreferenced
                    || seenStreamData || seenCompressStreams || seenDecodeLevel || seenTestJsonSchema || seenRecompressFlate || seenCompressionLevel
                    || seenRemoveInfo || seenRemoveMetadata
                    || seenCoalesceContents || seenNormalizeContent || seenRemoveUnreferencedResources || seenExternalizeInlineImages || seenIiMinBytes
                    || seenOverlay || seenUnderlay
                    || seenFlattenRotation || seenFlattenAnnotations || seenGenerateAppearances
                    || seenQdf || seenLinearize || seenNoOriginalObjectIds
                    || seenOptimizeImages || seenOiMinWidth || seenOiMinHeight || seenOiMinArea || seenKeepInlineImages || seenJpegQuality
                    || seenJson || seenJsonKey || seenJsonObject || seenJsonStreamData || seenJsonStreamPrefix || seenJsonOutput || seenJsonInput || seenUpdateFromJson
                    || seenCheckLinearization || seenShowLinearization || seenShowPages || seenShowObject || seenShowXref
                    || seenListAttachments || showAttachment is not null)
                {
                    throw new QpdfParseException("Job showNpages cannot be combined with page, output, transformation or other inspection keys.", 0L, null);
                }

                (QpdfPath inspectionInput, bool emptyDocument) = ResolveInspectionInput(input, empty, "showNpages");
                return new ShowNpagesPlan(inspectionInput, recovery)
                {
                    EmptyDocument = emptyDocument,
                    WarningExitZero = warningExit0,
                    ReportMemoryUsage = reportMemoryUsage,
                    Verbose = verbose,
                    Progress = progress,
                    IgnoreXrefStreams = ignoreXrefStreams,
                    NoWarn = noWarn,
                    MaxStreamFilters = maxStreamFilters,
                    ParserMaxNesting = parserMaxNesting,
                    MaxContainerSize = maxContainerSize,
                    MaxContainerSizeDamaged = maxContainerSizeDamaged,
                    ParserMaxErrors = parserMaxErrors,
                    NoDefaultLimits = noDefaultLimits,
                };
            }

            if (showPages)
            {
                if (hasPages || output is not null
                    || wantAttachments || attachmentsToAdd.Count > 0 || attachmentsToRemove.Count > 0 || attachmentsToCopy.Count > 0
                    || seenRotate || seenLabels || seenRemoveLabels
                    || seenMin || seenForce || seenDeterministic || seenStatic || seenNewline
                    || seenObjectStreams || seenPreserveUnreferenced
                    || seenStreamData || seenCompressStreams || seenDecodeLevel || seenTestJsonSchema || seenRecompressFlate || seenCompressionLevel
                    || seenRemoveInfo || seenRemoveMetadata
                    || seenCoalesceContents || seenNormalizeContent || seenRemoveUnreferencedResources || seenExternalizeInlineImages || seenIiMinBytes
                    || seenOverlay || seenUnderlay
                    || seenFlattenRotation || seenFlattenAnnotations || seenGenerateAppearances
                    || seenQdf || seenLinearize || seenNoOriginalObjectIds
                    || seenOptimizeImages || seenOiMinWidth || seenOiMinHeight || seenOiMinArea || seenKeepInlineImages || seenJpegQuality
                    || seenJson || seenJsonKey || seenJsonObject || seenJsonStreamData || seenJsonStreamPrefix || seenJsonOutput || seenJsonInput || seenUpdateFromJson
                    || seenCheckLinearization || seenShowLinearization || seenShowNpages || seenShowObject || seenShowXref
                    || seenListAttachments || showAttachment is not null)
                {
                    throw new QpdfParseException("Job showPages cannot be combined with page, output, transformation or other inspection keys.", 0L, null);
                }

                (QpdfPath inspectionInput, bool emptyDocument) = ResolveInspectionInput(input, empty, "showPages");
                return new ShowPagesPlan(inspectionInput, recovery)
                {
                    EmptyDocument = emptyDocument,
                    WarningExitZero = warningExit0,
                    ReportMemoryUsage = reportMemoryUsage,
                    Verbose = verbose,
                    Progress = progress,
                    IgnoreXrefStreams = ignoreXrefStreams,
                    WithImages = withImages,
                    NoWarn = noWarn,
                    MaxStreamFilters = maxStreamFilters,
                    ParserMaxNesting = parserMaxNesting,
                    MaxContainerSize = maxContainerSize,
                    MaxContainerSizeDamaged = maxContainerSizeDamaged,
                    ParserMaxErrors = parserMaxErrors,
                    NoDefaultLimits = noDefaultLimits,
                };
            }

            if (showObject is not null)
            {
                if (hasPages || (output is not null && !seenRawStreamData && !seenFilteredStreamData)
                    || wantAttachments || attachmentsToAdd.Count > 0 || attachmentsToRemove.Count > 0 || attachmentsToCopy.Count > 0
                    || seenRotate || seenLabels || seenRemoveLabels
                    || seenMin || seenForce || seenDeterministic || seenStatic || seenNewline
                    || seenObjectStreams || seenPreserveUnreferenced
                    || seenStreamData || seenCompressStreams || seenDecodeLevel || seenTestJsonSchema || seenRecompressFlate || seenCompressionLevel
                    || seenRemoveInfo || seenRemoveMetadata
                    || seenCoalesceContents || seenNormalizeContent || seenRemoveUnreferencedResources || seenExternalizeInlineImages || seenIiMinBytes
                    || seenOverlay || seenUnderlay
                    || seenFlattenRotation || seenFlattenAnnotations || seenGenerateAppearances
                    || seenQdf || seenLinearize || seenNoOriginalObjectIds
                    || seenOptimizeImages || seenOiMinWidth || seenOiMinHeight || seenOiMinArea || seenKeepInlineImages || seenJpegQuality
                    || seenJson || seenJsonKey || seenJsonObject || seenJsonStreamData || seenJsonStreamPrefix || seenJsonOutput || seenJsonInput || seenUpdateFromJson
                    || seenCheckLinearization || seenShowLinearization || seenShowNpages || seenShowPages || seenShowXref
                    || seenListAttachments || showAttachment is not null)
                {
                    throw new QpdfParseException("Job showObject cannot be combined with page, output, transformation or other inspection keys.", 0L, null);
                }

                (QpdfPath inspectionInput, bool emptyDocument) = ResolveInspectionInput(input, empty, "showObject");
                return new ShowObjectPlan(inspectionInput, recovery, showObject)
                {
                    EmptyDocument = emptyDocument,
                    WarningExitZero = warningExit0,
                    ReportMemoryUsage = reportMemoryUsage,
                    Verbose = verbose,
                    Progress = progress,
                    IgnoreXrefStreams = ignoreXrefStreams,
                    NoWarn = noWarn,
                    Output = output is null ? null : SelectionPlan.CheckedPath(output),
                    RawStreamData = rawStreamData,
                    FilteredStreamData = filteredStreamData,
                    MaxStreamFilters = maxStreamFilters,
                    ParserMaxNesting = parserMaxNesting,
                    MaxContainerSize = maxContainerSize,
                    MaxContainerSizeDamaged = maxContainerSizeDamaged,
                    ParserMaxErrors = parserMaxErrors,
                    NoDefaultLimits = noDefaultLimits,
                };
            }

            if (showXref)
            {
                if (hasPages || output is not null
                    || wantAttachments || attachmentsToAdd.Count > 0 || attachmentsToRemove.Count > 0 || attachmentsToCopy.Count > 0
                    || seenRotate || seenLabels || seenRemoveLabels
                    || seenMin || seenForce || seenDeterministic || seenStatic || seenNewline
                    || seenObjectStreams || seenPreserveUnreferenced
                    || seenStreamData || seenCompressStreams || seenDecodeLevel || seenTestJsonSchema || seenRecompressFlate || seenCompressionLevel
                    || seenRemoveInfo || seenRemoveMetadata
                    || seenCoalesceContents || seenNormalizeContent || seenRemoveUnreferencedResources || seenExternalizeInlineImages || seenIiMinBytes
                    || seenOverlay || seenUnderlay
                    || seenFlattenRotation || seenFlattenAnnotations || seenGenerateAppearances
                    || seenQdf || seenLinearize || seenNoOriginalObjectIds
                    || seenOptimizeImages || seenOiMinWidth || seenOiMinHeight || seenOiMinArea || seenKeepInlineImages || seenJpegQuality
                    || seenJson || seenJsonKey || seenJsonObject || seenJsonStreamData || seenJsonStreamPrefix || seenJsonOutput || seenJsonInput || seenUpdateFromJson
                    || seenCheckLinearization || seenShowLinearization || seenShowNpages || seenShowPages || seenShowObject
                    || seenListAttachments || showAttachment is not null)
                {
                    throw new QpdfParseException("Job showXref cannot be combined with page, output, transformation or other inspection keys.", 0L, null);
                }

                (QpdfPath inspectionInput, bool emptyDocument) = ResolveInspectionInput(input, empty, "showXref");
                return new ShowXrefPlan(inspectionInput, recovery)
                {
                    EmptyDocument = emptyDocument,
                    WarningExitZero = warningExit0,
                    ReportMemoryUsage = reportMemoryUsage,
                    Verbose = verbose,
                    Progress = progress,
                    IgnoreXrefStreams = ignoreXrefStreams,
                    NoWarn = noWarn,
                    MaxStreamFilters = maxStreamFilters,
                    ParserMaxNesting = parserMaxNesting,
                    MaxContainerSize = maxContainerSize,
                    MaxContainerSizeDamaged = maxContainerSizeDamaged,
                    ParserMaxErrors = parserMaxErrors,
                    NoDefaultLimits = noDefaultLimits,
                };
            }

            if (replaceInput)
            {
                if (output is not null)
                {
                    throw new QpdfParseException("Job \"replaceInput\" cannot be combined with \"outputFile\".", 0L, null);
                }

                if (empty)
                {
                    throw new QpdfParseException("Job \"replaceInput\" cannot be combined with \"empty\".", 0L, null);
                }
                if (!hasPages) { output = input; }
            }
            else if (output is null && !seenJson && !seenJsonOutput)
            {
                throw new QpdfParseException("Job JSON requires \"outputFile\".", 0L, null);
            }

            if ((seenJson || seenJsonOutput) && hasPages)
            {
                throw new QpdfParseException("Job \"json\" cannot be combined with \"pages\".", 0L, null);
            }

            if ((seenJson || seenJsonOutput) && (wantAttachments || attachmentsToAdd.Count > 0 || attachmentsToRemove.Count > 0 || attachmentsToCopy.Count > 0))
            {
                throw new QpdfParseException("Job \"json\" cannot be combined with attachment keys.", 0L, null);
            }

            if (jsonInput && seenUpdateFromJson)
            {
                throw new QpdfParseException("Job \"jsonInput\" and \"updateFromJson\" cannot be combined.", 0L, null);
            }

            if (jsonInput)
            {
                if (hasPages)
                {
                    throw new QpdfParseException("Job \"jsonInput\" cannot be combined with \"pages\".", 0L, null);
                }

                if (wantAttachments || attachmentsToAdd.Count > 0 || attachmentsToRemove.Count > 0 || attachmentsToCopy.Count > 0)
                {
                    throw new QpdfParseException("Job \"jsonInput\" cannot be combined with attachment keys.", 0L, null);
                }

                if (seenJson || seenJsonOutput || seenJsonKey || seenJsonObject || seenJsonStreamData || seenJsonStreamPrefix)
                {
                    throw new QpdfParseException("Job \"jsonInput\" cannot be combined with JSON inspection.", 0L, null);
                }

                if (output is null)
                {
                    throw new QpdfParseException("Job JSON requires \"outputFile\" with \"jsonInput\".", 0L, null);
                }

                if (objectStreams is not null || streamData is not null || compressStreams is not null
                    || decodeLevel is not null || maxStreamFilters is not null || noDefaultLimits || testJsonSchema || parserMaxNesting is not null || maxContainerSize is not null || maxContainerSizeDamaged is not null || parserMaxErrors is not null || recompressFlate || compressionLevel is not null
                    || removeInfo || removeMetadata || preserveUnreferenced)
                {
                    throw new QpdfParseException("Stream and metadata keys cannot be combined with \"jsonInput\".", 0L, null);
                }

                return new JsonInputPlan(SelectionPlan.CheckedPath(input ?? throw new QpdfParseException("Job JSON requires \"inputFile\".", 0L, null)), SelectionPlan.CheckedPath(output), recovery) with
                {
                    WarningExitZero = warningExit0,
                    ReportMemoryUsage = reportMemoryUsage,
                    Verbose = verbose,
                    Progress = progress,
                    IgnoreXrefStreams = ignoreXrefStreams,
                    NoWarn = noWarn,
                    MinVersion = minVersion,
                    ForceVersion = forceVersion,
                    DeterministicId = deterministicId,
                    StaticId = staticId,
                    NewlineBeforeEndstream = newlineBeforeEndstream,
                };
            }

            if (seenUpdateFromJson)
            {
                if (hasPages)
                {
                    throw new QpdfParseException("Job \"updateFromJson\" cannot be combined with \"pages\".", 0L, null);
                }

                if (wantAttachments || attachmentsToAdd.Count > 0 || attachmentsToRemove.Count > 0 || attachmentsToCopy.Count > 0)
                {
                    throw new QpdfParseException("Job \"updateFromJson\" cannot be combined with attachment keys.", 0L, null);
                }

                if (seenJson || seenJsonOutput || seenJsonKey || seenJsonObject || seenJsonStreamData || seenJsonStreamPrefix)
                {
                    throw new QpdfParseException("Job \"updateFromJson\" cannot be combined with JSON inspection.", 0L, null);
                }

                if (output is null)
                {
                    throw new QpdfParseException("Job JSON requires \"outputFile\" with \"updateFromJson\".", 0L, null);
                }

                if (objectStreams is not null || streamData is not null || compressStreams is not null
                    || decodeLevel is not null || maxStreamFilters is not null || noDefaultLimits || testJsonSchema || parserMaxNesting is not null || maxContainerSize is not null || maxContainerSizeDamaged is not null || parserMaxErrors is not null || recompressFlate || compressionLevel is not null
                    || removeInfo || removeMetadata || preserveUnreferenced)
                {
                    throw new QpdfParseException("Stream and metadata keys cannot be combined with \"updateFromJson\".", 0L, null);
                }

                return new JsonUpdatePlan(SelectionPlan.CheckedPath(input ?? throw new QpdfParseException("Job JSON requires \"inputFile\".", 0L, null)), SelectionPlan.CheckedPath(updateFromJson!), SelectionPlan.CheckedPath(output), recovery) with
                {
                    WarningExitZero = warningExit0,
                    ReportMemoryUsage = reportMemoryUsage,
                    Verbose = verbose,
                    Progress = progress,
                    IgnoreXrefStreams = ignoreXrefStreams,
                    NoWarn = noWarn,
                    MinVersion = minVersion,
                    ForceVersion = forceVersion,
                    DeterministicId = deterministicId,
                    StaticId = staticId,
                    NewlineBeforeEndstream = newlineBeforeEndstream,
                };
            }

            bool wantJsonOutput = seenJsonOutput || seenJson;
            if ((seenJsonObject || seenJsonStreamData || seenJsonStreamPrefix) && !wantJsonOutput)
            {
                throw new QpdfParseException("JSON stream and object keys require \"json\" or \"jsonOutput\".", 0L, null);
            }

            if (seenJsonOutput || seenJson)
            {
                int jsonNumber = 2;
                if (seenJson && jsonVersion is not null && jsonVersion != "latest")
                {
                    jsonNumber = int.Parse(jsonVersion, System.Globalization.CultureInfo.InvariantCulture);
                }

                if (seenJsonOutput && jsonNumber == 1)
                {
                    throw new QpdfParseException("Job \"jsonOutput\" requires JSON version 2.", 0L, null);
                }

                List<string> effectiveKeys = new(jsonKeys);
                if (seenJsonOutput && !effectiveKeys.Contains("qpdf"))
                {
                    effectiveKeys.Add("qpdf");
                }

                Qpdf.ValidateJsonKeys(jsonNumber, effectiveKeys);
                List<string> wantedObjects = Qpdf.ValidateJsonObjects(jsonObjects, 0L, textStorage);
                string jsonDecode = decodeLevel ?? (seenJsonOutput ? "none" : "generalized");
                if (jsonDecode != "none" && jsonDecode != "generalized" && jsonDecode != "specialized" && jsonDecode != "all")
                {
                    throw new QpdfParseException("Job \"decodeLevel\" must be one of: none, generalized, specialized, all.", 0L, null);
                }

                string jsonStreams = seenJsonStreamData ? jsonStreamData! : (seenJsonOutput ? "inline" : "none");
                (QpdfPath inspectionInput, bool emptyDocument) = ResolveInspectionInput(input, empty, "json inspection");
                return new JsonPlan(inspectionInput, output is null ? null : SelectionPlan.CheckedPath(output), jsonNumber, effectiveKeys, recovery) with
                {
                    EmptyDocument = emptyDocument,
                    DecodeLevel = jsonDecode,
                    ReportMemoryUsage = reportMemoryUsage,
                    Verbose = verbose,
                    Progress = progress,
                    TestJsonSchema = testJsonSchema,
                    MaxStreamFilters = maxStreamFilters,
                    NoDefaultLimits = noDefaultLimits,
                    ParserMaxNesting = parserMaxNesting,
                    MaxContainerSize = maxContainerSize,
                    MaxContainerSizeDamaged = maxContainerSizeDamaged,
                    ParserMaxErrors = parserMaxErrors,
                    IgnoreXrefStreams = ignoreXrefStreams,
                    StreamDataMode = jsonStreams,
                    StreamPrefix = jsonStreamPrefix,
                    Objects = wantedObjects,
                    MinimalOutput = seenJsonOutput,
                    NoWarn = noWarn,
                    WarningExitZero = warningExit0,
                };
            }

            if (seenJsonKey && !seenJson)
            {
                throw new QpdfParseException("Job \"jsonKey\" requires \"json\".", 0L, null);
            }

            if (!hasPages)
            {
                if (output is null)
                {
                    throw new QpdfParseException("Job JSON requires \"outputFile\".", 0L, null);
                }

                QpdfPath wholeInput = empty ? QpdfPath.From(JobPlan.EmptyDocumentInput)
                    : SelectionPlan.CheckedPath(input ?? throw new QpdfParseException("Job JSON requires \"inputFile\".", 0L, null));
                return new WholeFilePlan(wholeInput, SelectionPlan.CheckedPath(output), recovery) with
                {
                    EmptyDocument = empty,
                    ReplaceInput = replaceInput,
                    Rotations = rotations,
                    Labels = labels,
                    WarningExitZero = warningExit0,
                    RemoveLabels = removeLabels,
                    SplitPages = splitPages,
                    ReportMemoryUsage = reportMemoryUsage,
                    Verbose = verbose,
                    Progress = progress,
                    IgnoreXrefStreams = ignoreXrefStreams,
                    NoWarn = noWarn,
                    MinVersion = minVersion,
                    ForceVersion = forceVersion,
                    DeterministicId = deterministicId,
                    StaticId = staticId,
                    NewlineBeforeEndstream = newlineBeforeEndstream,
                    ObjectStreams = objectStreams,
                    PreserveUnreferenced = preserveUnreferenced,
                    CompressionState = compressionSettings.State,
                    StreamData = streamData,
                    CompressStreams = compressStreams,
                    DecodeLevel = decodeLevel,
                    MaxStreamFilters = maxStreamFilters,
                    NoDefaultLimits = noDefaultLimits,
                    ParserMaxNesting = parserMaxNesting,
                    MaxContainerSize = maxContainerSize,
                    MaxContainerSizeDamaged = maxContainerSizeDamaged,
                    ParserMaxErrors = parserMaxErrors,
                    RecompressFlate = recompressFlate,
                    CompressionLevel = compressionLevel,
                    RemoveInfo = removeInfo,
                    RemoveAcroform = removeAcroform,
                    RemoveStructure = removeStructure,
                    RemoveMetadata = removeMetadata,
                    CoalesceContents = coalesceContents,
                    NormalizeContent = normalizeContent || (qdf && !seenNormalizeContent),
                    RemoveUnreferencedResources = removeUnreferencedResources,
                    ExternalizeInlineImages = externalizeInlineImages,
                    IiMinBytes = iiMinBytes,
                    AttachmentsToAdd = attachmentsToAdd,
                    AttachmentsToRemove = attachmentsToRemove,
                    AttachmentsToCopy = attachmentsToCopy,
                    Overlays = overlays,
                    Underlays = underlays,
                    FlattenRotation = flattenRotation,
                    FlattenAnnotations = flattenAnnotations,
                    GenerateAppearances = generateAppearances,
                    Qdf = qdf,
                    Linearize = linearize,
                    LinearizePass1 = linearizePass1 is null ? null : SelectionPlan.CheckedPath(linearizePass1),
                    NoOriginalObjectIds = noOriginalObjectIds,
                    OptimizeImages = optimizeImages,
                    OiMinWidth = oiMinWidth,
                    OiMinHeight = oiMinHeight,
                    OiMinArea = oiMinArea,
                    KeepInlineImages = keepInlineImages,
                    JpegQuality = jpegQuality,
                };
            }

            if (removeLabels && labels.Count > 0)
            {
                throw new QpdfParseException("Job \"setPageLabels\" cannot be combined with \"removePageLabels\".", 0L, null);
            }

            List<(string File, string? Range)> entries = ParsePagesEntries(pages, textStorage);
            PdfPageCollation? collation = null;
            if (seenCollate)
            {
                List<int> groupSizes = collationSizes ?? throw new InvalidOperationException("Missing parsed collation sizes.");
                if (groupSizes.Count != 1 && groupSizes.Count != entries.Count)
                {
                    throw new QpdfParseException("Job \"collate\" must hold one value or one per \"pages\" entry.", 0L, null);
                }

                collation = (textStorage.Collation ?? throw new InvalidOperationException("Missing collation owner.")).CreatePolicy(groupSizes);
            }
            if (empty)
            {
                foreach ((string file, string? _) in entries)
                {
                    if (file == ".")
                    {
                        throw new QpdfParseException("Cannot select from \u0027.\u0027 with empty input.", 0L, null);
                    }
                }

                if (overlays.Count > 0 || underlays.Count > 0)
                {
                    throw new QpdfParseException("Overlay and underlay blocks cannot be combined with empty input.", 0L, null);
                }
            }

            if (entries.Count == 1 && !empty && collation is null && splitPages is null && SelectionPlan.IsPlainPageNumber(entries[0].Range))
            {
                string resolved = ResolveFile(entries[0].File, input ?? throw new QpdfParseException("Job JSON requires \"inputFile\".", 0L, null));
                int page = SelectionPlan.ParsePageNumber(entries[0].Range);
                return new SelectionPlan(SelectionPlan.CheckedPath(input ?? throw new QpdfParseException("Job JSON requires \"inputFile\".", 0L, null)), SelectionPlan.CheckedPath(output ?? input), page, recovery) with
                {
                    ReplaceInput = replaceInput,
                    Rotations = rotations,
                    Labels = labels,
                    RemoveLabels = removeLabels,
                    WarningExitZero = warningExit0,
                    ReportMemoryUsage = reportMemoryUsage,
                    Verbose = verbose,
                    Progress = progress,
                    IgnoreXrefStreams = ignoreXrefStreams,
                    NoWarn = noWarn,
                    MinVersion = minVersion,
                    ForceVersion = forceVersion,
                    DeterministicId = deterministicId,
                    StaticId = staticId,
                    NewlineBeforeEndstream = newlineBeforeEndstream,
                    ObjectStreams = objectStreams,
                    PreserveUnreferenced = preserveUnreferenced,
                    CompressionState = compressionSettings.State,
                    StreamData = streamData,
                    CompressStreams = compressStreams,
                    DecodeLevel = decodeLevel,
                    MaxStreamFilters = maxStreamFilters,
                    NoDefaultLimits = noDefaultLimits,
                    ParserMaxNesting = parserMaxNesting,
                    MaxContainerSize = maxContainerSize,
                    MaxContainerSizeDamaged = maxContainerSizeDamaged,
                    ParserMaxErrors = parserMaxErrors,
                    RecompressFlate = recompressFlate,
                    CompressionLevel = compressionLevel,
                    RemoveInfo = removeInfo,
                    RemoveAcroform = removeAcroform,
                    RemoveStructure = removeStructure,
                    RemoveMetadata = removeMetadata,
                    CoalesceContents = coalesceContents,
                    NormalizeContent = normalizeContent || (qdf && !seenNormalizeContent),
                    RemoveUnreferencedResources = removeUnreferencedResources,
                    ExternalizeInlineImages = externalizeInlineImages,
                    IiMinBytes = iiMinBytes,
                    AttachmentsToAdd = attachmentsToAdd,
                    AttachmentsToRemove = attachmentsToRemove,
                    AttachmentsToCopy = attachmentsToCopy,
                    Overlays = overlays,
                    Underlays = underlays,
                    FlattenRotation = flattenRotation,
                    FlattenAnnotations = flattenAnnotations,
                    GenerateAppearances = generateAppearances,
                    Qdf = qdf,
                    Linearize = linearize,
                    LinearizePass1 = linearizePass1 is null ? null : SelectionPlan.CheckedPath(linearizePass1),
                    NoOriginalObjectIds = noOriginalObjectIds,
                    OptimizeImages = optimizeImages,
                    OiMinWidth = oiMinWidth,
                    OiMinHeight = oiMinHeight,
                    OiMinArea = oiMinArea,
                    KeepInlineImages = keepInlineImages,
                    JpegQuality = jpegQuality,
                };
            }

            List<PdfPageSource> sources = new(entries.Count);
            foreach ((string file, string? range) in entries)
            {
                sources.Add(new PdfPageSource(SelectionPlan.CheckedPath(file), PdfPageSelectionOptions.FromRange(range)));
            }

            string primary = input ?? entries[0].File;
            return new MergePlan(SelectionPlan.CheckedPath(primary), SelectionPlan.CheckedPath(output ?? primary), sources, collation, recovery) with
            {
                EmptyInput = empty,
                ReplaceInput = replaceInput,
                Rotations = rotations,
                Labels = labels,
                RemoveLabels = removeLabels,
                SplitPages = splitPages,
                MinVersion = minVersion,
                ForceVersion = forceVersion,
                DeterministicId = deterministicId,
                StaticId = staticId,
                NewlineBeforeEndstream = newlineBeforeEndstream,
                    ObjectStreams = objectStreams,
                    PreserveUnreferenced = preserveUnreferenced,
                    CompressionState = compressionSettings.State,
                    StreamData = streamData,
                    CompressStreams = compressStreams,
                    DecodeLevel = decodeLevel,
                    MaxStreamFilters = maxStreamFilters,
                    NoDefaultLimits = noDefaultLimits,
                    ParserMaxNesting = parserMaxNesting,
                    MaxContainerSize = maxContainerSize,
                    MaxContainerSizeDamaged = maxContainerSizeDamaged,
                    ParserMaxErrors = parserMaxErrors,
                    RecompressFlate = recompressFlate,
                    CompressionLevel = compressionLevel,
                    RemoveInfo = removeInfo,
                    RemoveAcroform = removeAcroform,
                    RemoveStructure = removeStructure,
                    RemoveMetadata = removeMetadata,
                    CoalesceContents = coalesceContents,
                    NormalizeContent = normalizeContent || (qdf && !seenNormalizeContent),
                    RemoveUnreferencedResources = removeUnreferencedResources,
                    ExternalizeInlineImages = externalizeInlineImages,
                    IiMinBytes = iiMinBytes,
                    AttachmentsToAdd = attachmentsToAdd,
                    AttachmentsToRemove = attachmentsToRemove,
                    AttachmentsToCopy = attachmentsToCopy,
                    Overlays = overlays,
                    Underlays = underlays,
                    FlattenRotation = flattenRotation,
                    FlattenAnnotations = flattenAnnotations,
                    GenerateAppearances = generateAppearances,
                    Qdf = qdf,
                    Linearize = linearize,
                    LinearizePass1 = linearizePass1 is null ? null : SelectionPlan.CheckedPath(linearizePass1),
                    NoOriginalObjectIds = noOriginalObjectIds,
                    OptimizeImages = optimizeImages,
                    OiMinWidth = oiMinWidth,
                    OiMinHeight = oiMinHeight,
                    OiMinArea = oiMinArea,
                    KeepInlineImages = keepInlineImages,
                    JpegQuality = jpegQuality,
                    WarningExitZero = warningExit0,
                    ReportMemoryUsage = reportMemoryUsage,
                    Verbose = verbose,
                    Progress = progress,
                    IgnoreXrefStreams = ignoreXrefStreams,
                    NoWarn = noWarn,
            };
    }

    internal static bool IsCryptoKey(string name)
        => name is "encrypt" or "decrypt" or "password" or "passwordFile" or "passwordMode" or "passwordIsHexKey" or "suppressPasswordRecovery" or "encryptionFilePassword" or "allowWeakCrypto" or "copyEncryption" or "isEncrypted" or "requiresPassword" or "showEncryption" or "showEncryptionKey" or "staticAesIv" or "removeRestrictions";

    internal static IEnumerable<JsonProperty> EffectiveProperties(JsonElement value)
    {
        Dictionary<string, JsonProperty> properties = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject()) { properties[property.Name] = property; }
        return properties.Values;
    }

    private static List<OutputRotation> ParseRotateArray(JsonElement value, ConfigurationText textStorage)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job \"rotate\" must be an array of rotation specifications.", 0L, null);
        }

        List<OutputRotation> rotations = new();
        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                throw new QpdfParseException("Job \"rotate\" entries must be strings.", 0L, null);
            }

            string? specification = textStorage.ReadJsonString(entry);
            if (specification is null)
            {
                throw new QpdfParseException("Job \"rotate\" entries must be strings.", 0L, null);
            }

            rotations.Add(OutputRotation.Parse(specification, 0L, textStorage));
        }

        return rotations;
    }

    private static List<PageLabelSpec> ParseLabelArray(JsonElement value, ConfigurationText textStorage)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job \"setPageLabels\" must be an array of label specifications.", 0L, null);
        }

        List<PageLabelSpec> labels = new();
        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                throw new QpdfParseException("Job \"setPageLabels\" entries must be strings.", 0L, null);
            }

            string? specification = textStorage.ReadJsonString(entry);
            if (specification is null)
            {
                throw new QpdfParseException("Job \"setPageLabels\" entries must be strings.", 0L, null);
            }

            labels.Add(PageLabelSpec.Parse(specification, 0L, textStorage));
        }

        return labels;
    }

    private static List<AttachmentAdd> ParseAddAttachmentArray(JsonElement value, ConfigurationText textStorage)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job addAttachment must be an array.", 0L, null);
        }

        if (value.GetArrayLength() == 0)
        {
            throw new QpdfParseException("Job addAttachment must not be empty.", 0L, null);
        }

        List<AttachmentAdd> adds = new();
        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("Job addAttachment entries must be objects.", 0L, null);
            }

            adds.Add(ParseAddAttachmentEntry(entry, textStorage));
        }

        return adds;
    }

    private static AttachmentAdd ParseAddAttachmentEntry(JsonElement entry, ConfigurationText textStorage)
    {
        string? file = null;
        string? key = null;
        string? fileName = null;
        string? creationDate = null;
        string? modDate = null;
        string? mimeType = null;
        string? description = null;
        bool replace = false;
        foreach (JsonProperty property in EffectiveProperties(entry))
        {
            switch (property.Name)
            {
                case "file":
                    if (file is not null)
                    {
                        throw new QpdfParseException("Duplicate addAttachment key.", 0L, null);
                    }

                    file = RequireString(property.Value, "addAttachment-file", textStorage);
                    break;
                case "key":
                    if (key is not null)
                    {
                        throw new QpdfParseException("Duplicate addAttachment key.", 0L, null);
                    }

                    key = RequireString(property.Value, "addAttachment-key", textStorage);
                    break;
                case "filename":
                    if (fileName is not null)
                    {
                        throw new QpdfParseException("Duplicate addAttachment key.", 0L, null);
                    }

                    fileName = RequireString(property.Value, "addAttachment-filename", textStorage);
                    break;
                case "creationdate":
                    if (creationDate is not null)
                    {
                        throw new QpdfParseException("Duplicate addAttachment key.", 0L, null);
                    }

                    creationDate = RequireString(property.Value, "addAttachment-creationdate", textStorage);
                    break;
                case "moddate":
                    if (modDate is not null)
                    {
                        throw new QpdfParseException("Duplicate addAttachment key.", 0L, null);
                    }

                    modDate = RequireString(property.Value, "addAttachment-moddate", textStorage);
                    break;
                case "mimetype":
                    if (mimeType is not null)
                    {
                        throw new QpdfParseException("Duplicate addAttachment key.", 0L, null);
                    }

                    mimeType = RequireString(property.Value, "addAttachment-mimetype", textStorage);
                    break;
                case "description":
                    if (description is not null)
                    {
                        throw new QpdfParseException("Duplicate addAttachment key.", 0L, null);
                    }

                    description = RequireString(property.Value, "addAttachment-description", textStorage);
                    break;
                case "replace":
                    replace = RequireBareFlag(property.Value, "addAttachment-replace", textStorage);
                    break;
                default:
                    throw new QpdfParseException("Unsupported addAttachment key.", 0L, null);
            }
        }

        if (file is null || file.Length == 0)
        {
            throw new QpdfParseException("Job addAttachment entries require a file.", 0L, null);
        }

        if (key is not null && key.Length == 0)
        {
            throw new QpdfParseException("Job addAttachment key must not be empty.", 0L, null);
        }

        if (fileName is not null && fileName.Length == 0)
        {
            throw new QpdfParseException("Job addAttachment filename must not be empty.", 0L, null);
        }

        return new AttachmentAdd(
            file,
            key,
            fileName,
            creationDate is null || creationDate.Length == 0 ? null : creationDate,
            modDate is null || modDate.Length == 0 ? null : modDate,
            mimeType is null || mimeType.Length == 0 ? null : mimeType,
            description is null || description.Length == 0 ? null : description,
            replace);
    }

    private static List<string> ParseRemoveAttachmentArray(JsonElement value, ConfigurationText textStorage)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job removeAttachment must be an array.", 0L, null);
        }

        if (value.GetArrayLength() == 0)
        {
            throw new QpdfParseException("Job removeAttachment must not be empty.", 0L, null);
        }

        List<string> removes = new();
        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                throw new QpdfParseException("Job removeAttachment entries must be strings.", 0L, null);
            }

            string? key = textStorage.ReadJsonString(entry);
            if (key is null || key.Length == 0)
            {
                throw new QpdfParseException("Job removeAttachment entries must be non-empty.", 0L, null);
            }

            removes.Add(key);
        }

        return removes;
    }

    private static List<AttachmentCopy> ParseCopyAttachmentsArray(JsonElement value, ConfigurationText textStorage)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job copyAttachmentsFrom must be an array.", 0L, null);
        }

        if (value.GetArrayLength() == 0)
        {
            throw new QpdfParseException("Job copyAttachmentsFrom must not be empty.", 0L, null);
        }

        List<AttachmentCopy> copies = new();
        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("Job copyAttachmentsFrom entries must be objects.", 0L, null);
            }

            copies.Add(ParseCopyAttachmentsEntry(entry, textStorage));
        }

        return copies;
    }

    private static AttachmentCopy ParseCopyAttachmentsEntry(JsonElement entry, ConfigurationText textStorage)
    {
        string? file = null;
        string? prefix = null;
        foreach (JsonProperty property in EffectiveProperties(entry))
        {
            switch (property.Name)
            {
                case "file":
                    if (file is not null)
                    {
                        throw new QpdfParseException("Duplicate copyAttachmentsFrom key.", 0L, null);
                    }

                    file = RequireString(property.Value, "copyAttachmentsFrom-file", textStorage);
                    break;
                case "prefix":
                    if (prefix is not null)
                    {
                        throw new QpdfParseException("Duplicate copyAttachmentsFrom key.", 0L, null);
                    }

                    prefix = RequireString(property.Value, "copyAttachmentsFrom-prefix", textStorage);
                    break;
                case "password":
                    throw new QpdfCryptoException("password");
                default:
                    throw new QpdfParseException("Unsupported copyAttachmentsFrom key.", 0L, null);
            }
        }

        if (file is null || file.Length == 0)
        {
            throw new QpdfParseException("Job copyAttachmentsFrom entries require a file.", 0L, null);
        }

        return new AttachmentCopy(file, prefix is null || prefix.Length == 0 ? null : prefix);
    }

    private static string ParseJsonVersion(JsonElement value, ConfigurationText textStorage)
    {
        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            && (number == 1 || number == 2))
        {
            return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string? text = textStorage.ReadJsonString(value);
            if (text == "1" || text == "2" || text == "latest")
            {
                return text;
            }
        }

        throw new QpdfParseException("Job \"json\" must be one of: 1, 2, latest.", 0L, null);
    }

    private static List<string> ParseJsonKeyArray(JsonElement value, ConfigurationText textStorage)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job \"jsonKey\" must be an array of key names.", 0L, null);
        }

        List<string> keys = new();
        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || textStorage.ReadJsonString(entry) is not string text)
            {
                throw new QpdfParseException("Job \"jsonKey\" entries must be strings.", 0L, null);
            }

            keys.Add(text);
        }

        return keys;
    }

    private static string ParseJsonOutput(JsonElement value, ConfigurationText textStorage)
    {
        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            && number == 2)
        {
            return "2";
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string? text = textStorage.ReadJsonString(value);
            if (text == "2" || text == "latest")
            {
                return text;
            }
        }

        throw new QpdfParseException("Job \"jsonOutput\" must be one of: 2, latest.", 0L, null);
    }

    private static List<string> ParseJsonObjectArray(JsonElement value, ConfigurationText textStorage)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job \"jsonObject\" must be an array of selectors.", 0L, null);
        }

        List<string> selectors = new();
        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || textStorage.ReadJsonString(entry) is not string text)
            {
                throw new QpdfParseException("Job \"jsonObject\" entries must be strings.", 0L, null);
            }

            selectors.Add(text);
        }

        return selectors;
    }

    private static long RequireOiMin(JsonElement element, string role, ConfigurationText textStorage)
    {
        string? text = null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long number))
        {
            if (number < 0L)
            {
                throw new QpdfParseException("Job " + (char)34 + role + (char)34 + " must be a non-negative pixel count.", 0L, null);
            }

            return number;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            text = textStorage.ReadJsonString(element);
        }

        if (text is null || text.Length == 0 || text.Length > 10)
        {
            throw new QpdfParseException("Job " + (char)34 + role + (char)34 + " must be a non-negative pixel count.", 0L, null);
        }

        long size = 0L;
        foreach (char digit in text)
        {
            if (digit < (char)48 || digit > (char)57)
            {
                throw new QpdfParseException("Job " + (char)34 + role + (char)34 + " must be a non-negative pixel count.", 0L, null);
            }

            size = checked(size * 10L + (digit - (char)48));
        }

        return size;
    }

    private static int RequireJpegQuality(JsonElement element, ConfigurationText textStorage)
    {
        long number;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long direct))
        {
            number = direct;
        }
        else if (element.ValueKind == JsonValueKind.String && textStorage.ReadJsonString(element) is string text
            && text.Length > 0 && text.Length <= 3)
        {
            number = 0L;
            foreach (char digit in text)
            {
                if (digit < (char)48 || digit > (char)57)
                {
                    throw new QpdfParseException("Job jpegQuality must be an integer 0..100.", 0L, null);
                }

                number = checked(number * 10L + (digit - (char)48));
            }
        }
        else
        {
            throw new QpdfParseException("Job jpegQuality must be an integer 0..100.", 0L, null);
        }

        if (number < 0L || number > 100L)
        {
            throw new QpdfParseException("Job jpegQuality must be an integer 0..100.", 0L, null);
        }

        return (int)number;
    }
    private static long RequireIiMinBytes(JsonElement element, ConfigurationText textStorage)
    {
        string? text = null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long number))
        {
            if (number < 0L)
            {
                throw new QpdfParseException("Job " + (char)34 + "iiMinBytes" + (char)34 + " must be a non-negative byte count.", 0L, null);
            }

            return number;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            text = textStorage.ReadJsonString(element);
        }

        if (text is null || text.Length == 0 || text.Length > 10)
        {
            throw new QpdfParseException("Job " + (char)34 + "iiMinBytes" + (char)34 + " must be a non-negative byte count.", 0L, null);
        }

        long size = 0L;
        foreach (char digit in text)
        {
            if (digit < (char)48 || digit > (char)57)
            {
                throw new QpdfParseException("Job " + (char)34 + "iiMinBytes" + (char)34 + " must be a non-negative byte count.", 0L, null);
            }

            size = checked(size * 10L + (digit - (char)48));
        }

        return size;
    }

    private static long RequireContainerSize(JsonElement element, string role, ConfigurationText textStorage)
    {
        string? text = null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long number))
        {
            if (number < 0L)
            {
                throw new QpdfParseException("Job " + (char)34 + role + (char)34 + " must be a non-negative object count.", 0L, null);
            }

            return number;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            text = textStorage.ReadJsonString(element);
        }

        if (text is null || text.Length == 0 || text.Length > 10)
        {
            throw new QpdfParseException("Job " + (char)34 + role + (char)34 + " must be a non-negative object count.", 0L, null);
        }

        long count = 0L;
        foreach (char digit in text)
        {
            if (digit < (char)48 || digit > (char)57)
            {
                throw new QpdfParseException("Job " + (char)34 + role + (char)34 + " must be a non-negative object count.", 0L, null);
            }

            count = checked(count * 10L + (digit - (char)48));
        }

        return count;
    }
    private static long RequireParserMaxErrors(JsonElement element, ConfigurationText textStorage)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long number))
        {
            if (number < 0L)
            {
                throw new QpdfParseException("Job " + (char)34 + "parserMaxErrors" + (char)34 + " must be a non-negative error count (0 removes the threshold).", 0L, null);
            }

            return number;
        }

        string? text = null;
        if (element.ValueKind == JsonValueKind.String)
        {
            text = textStorage.ReadJsonString(element);
        }

        if (text is null || text.Length == 0 || text.Length > 10)
        {
            throw new QpdfParseException("Job " + (char)34 + "parserMaxErrors" + (char)34 + " must be a non-negative error count (0 removes the threshold).", 0L, null);
        }

        long count = 0L;
        foreach (char digit in text)
        {
            if (digit < (char)48 || digit > (char)57)
            {
                throw new QpdfParseException("Job " + (char)34 + "parserMaxErrors" + (char)34 + " must be a non-negative error count (0 removes the threshold).", 0L, null);
            }

            count = checked(count * 10L + (digit - (char)48));
        }

        return count;
    }
    private static long RequireParserMaxNesting(JsonElement element, ConfigurationText textStorage)
    {
        string? text = null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long number))
        {
            text = number.ToString();
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            text = textStorage.ReadJsonString(element);
        }

        if (text is null || text.Length == 0 || text.Length > 10)
        {
            throw new QpdfParseException("Job " + (char)34 + "parserMaxNesting" + (char)34 + " must be a non-negative nesting depth within 499.", 0L, null);
        }

        long depth = 0L;
        foreach (char digit in text)
        {
            if (digit < (char)48 || digit > (char)57)
            {
                throw new QpdfParseException("Job " + (char)34 + "parserMaxNesting" + (char)34 + " must be a non-negative nesting depth within 499.", 0L, null);
            }

            depth = checked(depth * 10L + (digit - (char)48));
        }

        if (depth > QpdfLimits.ParserNestingCeiling)
        {
            throw new QpdfParseException("Job " + (char)34 + "parserMaxNesting" + (char)34 + " must be a non-negative nesting depth within 499.", 0L, null);
        }

        return depth;
    }
    private static string RequireKeepFilesOpen(JsonElement element, ConfigurationText textStorage)
    {
        if (element.ValueKind == JsonValueKind.String && textStorage.ReadJsonString(element) is string text && (text == "y" || text == "n"))
        {
            return text;
        }

        throw new QpdfParseException("Job " + (char)34 + "keepFilesOpen" + (char)34 + " must be \"y\" or \"n\".", 0L, null);
    }

    private static long RequireKeepFilesOpenThreshold(JsonElement element, ConfigurationText textStorage)
    {
        string? text = null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long number))
        {
            if (number < 0L)
            {
                throw new QpdfParseException("Job " + (char)34 + "keepFilesOpenThreshold" + (char)34 + " must be a non-negative file count.", 0L, null);
            }

            return number;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            text = textStorage.ReadJsonString(element);
        }

        if (text is null || text.Length == 0 || text.Length > 10)
        {
            throw new QpdfParseException("Job " + (char)34 + "keepFilesOpenThreshold" + (char)34 + " must be a non-negative file count.", 0L, null);
        }

        long count = 0L;
        foreach (char digit in text)
        {
            if (digit < (char)48 || digit > (char)57)
            {
                throw new QpdfParseException("Job " + (char)34 + "keepFilesOpenThreshold" + (char)34 + " must be a non-negative file count.", 0L, null);
            }

            count = checked(count * 10L + (digit - (char)48));
        }

        return count;
    }
    private static long RequireMaxStreamFilters(JsonElement element, ConfigurationText textStorage)
    {
        string? text = null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long number))
        {
            if (number < 0L)
            {
                throw new QpdfParseException("Job " + (char)34 + "maxStreamFilters" + (char)34 + " must be a non-negative filter count.", 0L, null);
            }

            return number;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            text = textStorage.ReadJsonString(element);
        }

        if (text is null || text.Length == 0 || text.Length > 10)
        {
            throw new QpdfParseException("Job " + (char)34 + "maxStreamFilters" + (char)34 + " must be a non-negative filter count.", 0L, null);
        }

        long count = 0L;
        foreach (char digit in text)
        {
            if (digit < (char)48 || digit > (char)57)
            {
                throw new QpdfParseException("Job " + (char)34 + "maxStreamFilters" + (char)34 + " must be a non-negative filter count.", 0L, null);
            }

            count = checked(count * 10L + (digit - (char)48));
        }

        return count;
    }
    private static bool RequireNormalizeContent(JsonElement element, ConfigurationText textStorage)
    {
        if (element.ValueKind == JsonValueKind.String && textStorage.ReadJsonString(element) is string text)
        {
            if (text == "y")
            {
                return true;
            }

            if (text == "n")
            {
                return false;
            }
        }

        throw new QpdfParseException("Job \"normalizeContent\" must be one of: y, n.", 0L, null);
    }

    private static List<OverlaySpec> ParseOverlayArray(JsonElement value, string role, ConfigurationText textStorage)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            return [ParseOverlayEntry(value, role, textStorage)];
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job overlay value must be an object or array.", 0L, null);
        }

        List<OverlaySpec> specs = new();
        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("Job overlay entries must be objects.", 0L, null);
            }

            specs.Add(ParseOverlayEntry(entry, role, textStorage));
        }

        return specs;
    }

    private static OverlaySpec ParseOverlayEntry(JsonElement entry, string role, ConfigurationText textStorage)
    {
        string? file = null;
        string? to = null;
        string? from = null;
        string? repeat = null;
        foreach (JsonProperty property in EffectiveProperties(entry))
        {
            switch (property.Name)
            {
                case "file":
                    if (file is not null)
                    {
                        throw new QpdfParseException("Duplicate overlay key file.", 0L, null);
                    }

                    file = RequireString(property.Value, role + ".file", textStorage);
                    break;
                case "to":
                    if (to is not null)
                    {
                        throw new QpdfParseException("Duplicate overlay key to.", 0L, null);
                    }

                    to = RequireString(property.Value, role + ".to", textStorage);
                    OverlayRanges.ValidateSyntax(to, "--to", 0L);
                    break;
                case "from":
                    if (from is not null)
                    {
                        throw new QpdfParseException("Duplicate overlay key from.", 0L, null);
                    }

                    from = RequireString(property.Value, role + ".from", textStorage);
                    OverlayRanges.ValidateSyntax(from, "--from", 0L);
                    break;
                case "repeat":
                    if (repeat is not null)
                    {
                        throw new QpdfParseException("Duplicate overlay key repeat.", 0L, null);
                    }

                    repeat = RequireString(property.Value, role + ".repeat", textStorage);
                    OverlayRanges.ValidateSyntax(repeat, "--repeat", 0L);
                    break;
                case "password":
                    throw new QpdfCryptoException("password");
                default:
                    throw new QpdfParseException("Unsupported overlay key.", 0L, null);
            }
        }

        if (file is null)
        {
            throw new QpdfParseException("Job overlay entries require file.", 0L, null);
        }

        return new OverlaySpec(file, to, from, repeat);
    }

    private static List<(string File, string? Range)> ParsePagesEntries(JsonElement pages, ConfigurationText textStorage)
    {
        if (pages.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job \"pages\" must be a non-empty array.", 0L, null);
        }

        if (pages.GetArrayLength() == 0)
        {
            throw new QpdfParseException("Job \"pages\" must hold at least one entry.", 0L, null);
        }

        List<(string File, string? Range)> entries = new();
        foreach (JsonElement entry in pages.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("Job \"pages\" entries must be objects.", 0L, null);
            }

            entries.Add(ParsePagesEntry(entry, textStorage));
        }

        return entries;
    }

    private static (string File, string? Range) ParsePagesEntry(JsonElement entry, ConfigurationText textStorage)
    {
        string? file = null;
        string? range = null;
        foreach (JsonProperty property in EffectiveProperties(entry))
        {
            switch (property.Name)
            {
                case "file":
                    if (file is not null)
                    {
                        throw new QpdfParseException("Duplicate pages key 'file'.", 0L, null);
                    }
                    file = RequireString(property.Value, "pages[].file", textStorage);
                    break;
                case "range":
                    if (range is not null)
                    {
                        throw new QpdfParseException("Duplicate pages key 'range'.", 0L, null);
                    }
                    range = RequireString(property.Value, "pages[].range", textStorage);
                    break;
                case "password":
                    throw new QpdfCryptoException("password");
                default:
                    throw new QpdfParseException($"Unsupported pages key '{property.Name}' in the narrow selection profile.", 0L, null);
            }
        }

        if (file is null)
        {
            throw new QpdfParseException("Job pages entries require \"file\".", 0L, null);
        }

        return (file, range);
    }

    private static string RequireString(JsonElement element, string role, ConfigurationText textStorage)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new QpdfParseException($"Job \"{role}\" must be a string.", 0L, null);
        }

        string? value = textStorage.ReadJsonString(element);
        if (value is null)
        {
            throw new QpdfParseException($"Job \"{role}\" must be a string.", 0L, null);
        }

        return value;
    }

    private static string RequireObjectStreams(JsonElement element, ConfigurationText textStorage)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new QpdfParseException("Job \"objectStreams\" must be disable, preserve, or generate.", 0L, null);
        }

        string? value = textStorage.ReadJsonString(element);
        if (value != "disable" && value != "preserve" && value != "generate")
        {
            throw new QpdfParseException("Job \"objectStreams\" must be disable, preserve, or generate.", 0L, null);
        }

        return value;
    }

    private static (QpdfPath Path, bool EmptyDocument) ResolveInspectionInput(string? input, bool empty, string role)
    {
        if (empty)
        {
            if (input is not null)
            {
                throw new QpdfParseException("Job \"empty\" cannot be combined with \"inputFile\" for " + role + ".", 0L, null);
            }

            return (SelectionPlan.CheckedPath(Commands.SelectionPlan.EmptyDocumentInput), true);
        }

        if (input is null)
        {
            throw new QpdfParseException("Job JSON requires \"inputFile\".", 0L, null);
        }

        return (SelectionPlan.CheckedPath(input), false);
    }

    private static bool RequireBareFlag(JsonElement element, string role, ConfigurationText textStorage)
    {
        if (element.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.String && textStorage.ReadJsonString(element) == string.Empty)
        {
            return true;
        }

        throw new QpdfParseException("Job \"" + role + "\" must be an empty string or a boolean.", 0L, null);
    }

    private static string ResolveFile(string file, string input)
    {
        if (file == ".")
        {
            return input;
        }

        if (!string.Equals(file, input, StringComparison.Ordinal))
        {
            throw new QpdfParseException("Multiple page sources are outside the narrow selection profile.", 0L, null);
        }

        return file;
    }
}
