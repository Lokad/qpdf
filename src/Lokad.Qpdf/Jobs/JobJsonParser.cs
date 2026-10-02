using System.Text.Json;
using Lokad.Qpdf.Commands;

namespace Lokad.Qpdf.Jobs;

/// <summary>
/// Narrow job-JSON parser for the initial single-page profile. Accepts exactly
/// `{inputFile, outputFile, pages:[{file, range}]}` with upstream field names;
/// `encrypt` and entry `password` keys reject as crypto, everything else
/// outside the subset fails explicitly. Full job semantics arrive in M4.
/// </summary>
internal static class JobJsonParser
{
    private const int MaxDepth = 32;

    internal static JobPlan Parse(string jobJson, QpdfLimits limits)
    {
        if (jobJson is null)
        {
            throw new ArgumentNullException(nameof(jobJson));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        long byteCount = System.Text.Encoding.UTF8.GetByteCount(jobJson);
        if (byteCount > limits.MaxInputBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), byteCount, limits.MaxInputBytes);
        }

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
            throw new QpdfParseException($"Invalid job JSON: {ex.Message}.", ex.BytePositionInLine, null, ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("Job JSON must be an object.", 0L, null);
            }

            string? input = null;
            bool seenInput = false;
            bool seenOutput = false;
            bool seenPages = false;
            string? output = null;
            JsonElement pages = default;
            bool hasPages = false;
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
            string? streamData = null;
            bool seenStreamData = false;
            string? compressStreams = null;
            bool seenCompressStreams = false;
            string? decodeLevel = null;
            bool seenDecodeLevel = false;
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
            bool showLinearization = false;
            bool seenShowLinearization = false;
            bool showNpages = false;
            bool seenShowNpages = false;
            bool showPages = false;
            bool seenShowPages = false;
            string? showObject = null;
            bool seenShowObject = false;
            bool showXref = false;
            bool seenShowXref = false;
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
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "inputFile":
                        if (seenInput)
                        {
                            throw new QpdfParseException("Duplicate job key 'inputFile'.", 0L, null);
                        }

                        seenInput = true;
                        input = RequireString(property.Value, "inputFile");
                        break;
                    case "outputFile":
                        if (seenOutput)
                        {
                            throw new QpdfParseException("Duplicate job key 'outputFile'.", 0L, null);
                        }

                        seenOutput = true;
                        output = RequireString(property.Value, "outputFile");
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
                    case "rotate":
                        if (seenRotate)
                        {
                            throw new QpdfParseException("Duplicate job key 'rotate'.", 0L, null);
                        }

                        seenRotate = true;
                        rotations.AddRange(ParseRotateArray(property.Value));
                        break;
                    case "setPageLabels":
                        if (seenLabels)
                        {
                            throw new QpdfParseException("Duplicate job key 'setPageLabels'.", 0L, null);
                        }

                        seenLabels = true;
                        labels.AddRange(ParseLabelArray(property.Value));
                        break;
                    case "removePageLabels":
                        if (seenRemoveLabels)
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
                        if (seenMin)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027minVersion\u0027.", 0L, null);
                        }

                        seenMin = true;
                        minVersion = RequireString(property.Value, "minVersion");
                        break;
                    case "forceVersion":
                        if (seenForce)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027forceVersion\u0027.", 0L, null);
                        }

                        seenForce = true;
                        forceVersion = RequireString(property.Value, "forceVersion");
                        break;
                    case "deterministicId":
                        if (seenDeterministic)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027deterministicId\u0027.", 0L, null);
                        }

                        seenDeterministic = true;
                        deterministicId = RequireBareFlag(property.Value, "deterministicId");
                        break;
                    case "staticId":
                        if (seenStatic)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027staticId\u0027.", 0L, null);
                        }

                        seenStatic = true;
                        staticId = RequireBareFlag(property.Value, "staticId");
                        break;
                    case "newlineBeforeEndstream":
                        if (seenNewline)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027newlineBeforeEndstream\u0027.", 0L, null);
                        }

                        seenNewline = true;
                        newlineBeforeEndstream = RequireBareFlag(property.Value, "newlineBeforeEndstream");
                        break;
                    case "objectStreams":
                        if (seenObjectStreams)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027objectStreams\u0027.", 0L, null);
                        }

                        seenObjectStreams = true;
                        objectStreams = RequireObjectStreams(property.Value);
                        break;
                    case "preserveUnreferenced":
                        if (seenPreserveUnreferenced)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027preserveUnreferenced\u0027.", 0L, null);
                        }

                        seenPreserveUnreferenced = true;
                        preserveUnreferenced = RequireBareFlag(property.Value, "preserveUnreferenced");
                        break;
                    case "streamData":
                        if (seenStreamData)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027streamData\u0027.", 0L, null);
                        }

                        seenStreamData = true;
                        streamData = RequireString(property.Value, "streamData");
                        break;
                    case "compressStreams":
                        if (seenCompressStreams)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027compressStreams\u0027.", 0L, null);
                        }

                        seenCompressStreams = true;
                        compressStreams = RequireString(property.Value, "compressStreams");
                        break;
                    case "decodeLevel":
                        if (seenDecodeLevel)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027decodeLevel\u0027.", 0L, null);
                        }

                        seenDecodeLevel = true;
                        decodeLevel = RequireString(property.Value, "decodeLevel");
                        break;
                    case "recompressFlate":
                        if (seenRecompressFlate)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027recompressFlate\u0027.", 0L, null);
                        }

                        seenRecompressFlate = true;
                        recompressFlate = RequireBareFlag(property.Value, "recompressFlate");
                        break;
                    case "compressionLevel":
                        if (seenCompressionLevel)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027compressionLevel\u0027.", 0L, null);
                        }

                        seenCompressionLevel = true;
                        compressionLevel = RequireString(property.Value, "compressionLevel");
                        break;
                    case "removeInfo":
                        if (seenRemoveInfo)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027removeInfo\u0027.", 0L, null);
                        }

                        seenRemoveInfo = true;
                        removeInfo = RequireBareFlag(property.Value, "removeInfo");
                        break;
                    case "removeMetadata":
                        if (seenRemoveMetadata)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027removeMetadata\u0027.", 0L, null);
                        }

                        seenRemoveMetadata = true;
                        removeMetadata = RequireBareFlag(property.Value, "removeMetadata");
                        break;
                    case "coalesceContents":
                        if (seenCoalesceContents)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027coalesceContents\u0027.", 0L, null);
                        }

                        seenCoalesceContents = true;
                        coalesceContents = RequireBareFlag(property.Value, "coalesceContents");
                        break;
                    case "normalizeContent":
                        if (seenNormalizeContent)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027normalizeContent\u0027.", 0L, null);
                        }

                        seenNormalizeContent = true;
                        normalizeContent = RequireNormalizeContent(property.Value);
                        break;
                    case "externalizeInlineImages":
                        if (seenExternalizeInlineImages)
                        {
                            throw new QpdfParseException("Duplicate job key " + (char)39 + "externalizeInlineImages" + (char)39 + ".", 0L, null);
                        }

                        seenExternalizeInlineImages = true;
                        externalizeInlineImages = RequireBareFlag(property.Value, "externalizeInlineImages");
                        break;
                    case "iiMinBytes":
                        if (seenIiMinBytes)
                        {
                            throw new QpdfParseException("Duplicate job key " + (char)39 + "iiMinBytes" + (char)39 + ".", 0L, null);
                        }

                        seenIiMinBytes = true;
                        iiMinBytes = RequireIiMinBytes(property.Value);
                        break;
                    case "removeUnreferencedResources":
                        if (seenRemoveUnreferencedResources)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027removeUnreferencedResources\u0027.", 0L, null);
                        }

                        seenRemoveUnreferencedResources = true;
                        removeUnreferencedResources = RequireString(property.Value, "removeUnreferencedResources");
                        if (removeUnreferencedResources != "auto" && removeUnreferencedResources != "yes" && removeUnreferencedResources != "no")
                        {
                            throw new QpdfParseException("Job \u0027removeUnreferencedResources\u0027 must be one of: auto, yes, no.", 0L, null);
                        }

                        break;
                    case "overlay":
                        if (seenOverlay)
                        {
                            throw new QpdfParseException("Duplicate job key overlay.", 0L, null);
                        }

                        seenOverlay = true;
                        overlays.AddRange(ParseOverlayArray(property.Value, "overlay"));
                        break;
                    case "underlay":
                        if (seenUnderlay)
                        {
                            throw new QpdfParseException("Duplicate job key underlay.", 0L, null);
                        }

                        seenUnderlay = true;
                        underlays.AddRange(ParseOverlayArray(property.Value, "underlay"));
                        break;
                    case "flattenRotation":
                        if (seenFlattenRotation)
                        {
                            throw new QpdfParseException("Duplicate job key flattenRotation.", 0L, null);
                        }

                        seenFlattenRotation = true;
                        flattenRotation = RequireBareFlag(property.Value, "flattenRotation");
                        break;
                    case "flattenAnnotations":
                        if (seenFlattenAnnotations)
                        {
                            throw new QpdfParseException("Duplicate job key flattenAnnotations.", 0L, null);
                        }

                        seenFlattenAnnotations = true;
                        flattenAnnotations = RequireString(property.Value, "flattenAnnotations");
                        if (flattenAnnotations != "all" && flattenAnnotations != "print" && flattenAnnotations != "screen")
                        {
                            throw new QpdfParseException("Job flattenAnnotations must be one of: all, print, screen.", 0L, null);
                        }

                        break;
                    case "generateAppearances":
                        if (seenGenerateAppearances)
                        {
                            throw new QpdfParseException("Duplicate job key generateAppearances.", 0L, null);
                        }

                        seenGenerateAppearances = true;
                        generateAppearances = RequireBareFlag(property.Value, "generateAppearances");
                        break;
                    case "qdf":
                        if (seenQdf)
                        {
                            throw new QpdfParseException("Duplicate job key qdf.", 0L, null);
                        }

                        seenQdf = true;
                        qdf = RequireBareFlag(property.Value, "qdf");
                        break;
                    case "linearize":
                        if (seenLinearize)
                        {
                            throw new QpdfParseException("Duplicate job key linearize.", 0L, null);
                        }

                        seenLinearize = true;
                        linearize = RequireBareFlag(property.Value, "linearize");
                        break;
                    case "linearizePass1":
                        if (seenLinearizePass1)
                        {
                            throw new QpdfParseException("Duplicate job key linearizePass1.", 0L, null);
                        }

                        seenLinearizePass1 = true;
                        linearizePass1 = RequireString(property.Value, "linearizePass1");
                        if (linearizePass1.Length == 0)
                        {
                            throw new QpdfParseException("Job \"linearizePass1\" must not be empty.", 0L, null);
                        }

                        break;
                    case "checkLinearization":
                        if (seenCheckLinearization)
                        {
                            throw new QpdfParseException("Duplicate job key checkLinearization.", 0L, null);
                        }

                        seenCheckLinearization = true;
                        checkLinearization = RequireBareFlag(property.Value, "checkLinearization");
                        break;
                    case "showLinearization":
                        if (seenShowLinearization)
                        {
                            throw new QpdfParseException("Duplicate job key showLinearization.", 0L, null);
                        }

                        seenShowLinearization = true;
                        showLinearization = RequireBareFlag(property.Value, "showLinearization");
                        break;
                    case "showNpages":
                        if (seenShowNpages)
                        {
                                throw new QpdfParseException("Duplicate job key showNpages.", 0L, null);
                        }

                        seenShowNpages = true;
                        showNpages = RequireBareFlag(property.Value, "showNpages");
                        break;
                    case "showPages":
                        if (seenShowPages)
                        {
                                throw new QpdfParseException("Duplicate job key showPages.", 0L, null);
                        }

                        seenShowPages = true;
                        showPages = RequireBareFlag(property.Value, "showPages");
                        break;
                    case "showObject":
                        if (seenShowObject)
                        {
                                throw new QpdfParseException("Duplicate job key showObject.", 0L, null);
                        }

                        seenShowObject = true;
                        showObject = RequireString(property.Value, "showObject");
                        if (showObject.Length == 0)
                        {
                                throw new QpdfParseException("Job showObject must not be empty.", 0L, null);
                        }

                        Commands.SelectionPlan.ParseObjectSelector(showObject);
                        break;
                    case "showXref":
                        if (seenShowXref)
                        {
                                throw new QpdfParseException("Duplicate job key showXref.", 0L, null);
                        }

                        seenShowXref = true;
                        showXref = RequireBareFlag(property.Value, "showXref");
                        break;
                    case "noOriginalObjectIds":
                        if (seenNoOriginalObjectIds)
                        {
                            throw new QpdfParseException("Duplicate job key noOriginalObjectIds.", 0L, null);
                        }

                        seenNoOriginalObjectIds = true;
                        noOriginalObjectIds = RequireBareFlag(property.Value, "noOriginalObjectIds");
                        break;
                    case "optimizeImages":
                        if (seenOptimizeImages)
                        {
                            throw new QpdfParseException("Duplicate job key optimizeImages.", 0L, null);
                        }

                        seenOptimizeImages = true;
                        optimizeImages = RequireBareFlag(property.Value, "optimizeImages");
                        break;
                    case "oiMinWidth":
                        if (seenOiMinWidth)
                        {
                            throw new QpdfParseException("Duplicate job key oiMinWidth.", 0L, null);
                        }

                        seenOiMinWidth = true;
                        oiMinWidth = RequireOiMin(property.Value, "oiMinWidth");
                        break;
                    case "oiMinHeight":
                        if (seenOiMinHeight)
                        {
                            throw new QpdfParseException("Duplicate job key oiMinHeight.", 0L, null);
                        }

                        seenOiMinHeight = true;
                        oiMinHeight = RequireOiMin(property.Value, "oiMinHeight");
                        break;
                    case "oiMinArea":
                        if (seenOiMinArea)
                        {
                            throw new QpdfParseException("Duplicate job key oiMinArea.", 0L, null);
                        }

                        seenOiMinArea = true;
                        oiMinArea = RequireOiMin(property.Value, "oiMinArea");
                        break;
                    case "keepInlineImages":
                        if (seenKeepInlineImages)
                        {
                            throw new QpdfParseException("Duplicate job key keepInlineImages.", 0L, null);
                        }

                        seenKeepInlineImages = true;
                        keepInlineImages = RequireBareFlag(property.Value, "keepInlineImages");
                        break;
                    case "jpegQuality":
                        if (seenJpegQuality)
                        {
                            throw new QpdfParseException("Duplicate job key jpegQuality.", 0L, null);
                        }

                        seenJpegQuality = true;
                        jpegQuality = RequireJpegQuality(property.Value);
                        break;
                    case "listAttachments":
                        if (seenListAttachments)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027listAttachments\u0027.", 0L, null);
                        }

                        seenListAttachments = true;
                        listAttachments = RequireBareFlag(property.Value, "listAttachments");
                        break;
                    case "showAttachment":
                        if (seenShowAttachment)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027showAttachment\u0027.", 0L, null);
                        }

                        seenShowAttachment = true;
                        showAttachment = RequireString(property.Value, "showAttachment");
                        if (showAttachment.Length == 0)
                        {
                            throw new QpdfParseException("Job \"showAttachment\" must not be empty.", 0L, null);
                        }

                        break;
                    case "addAttachment":
                        if (seenAddAttachment)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027addAttachment\u0027.", 0L, null);
                        }

                        seenAddAttachment = true;
                        attachmentsToAdd.AddRange(ParseAddAttachmentArray(property.Value));
                        break;
                    case "removeAttachment":
                        if (seenRemoveAttachment)
                        {
                            throw new QpdfParseException("Duplicate job key \u0027removeAttachment\u0027.", 0L, null);
                        }

                        seenRemoveAttachment = true;
                        attachmentsToRemove.AddRange(ParseRemoveAttachmentArray(property.Value));
                        break;
                    case "copyAttachmentsFrom":
                        if (seenCopyAttachments)
                        {
                            throw new QpdfParseException("Duplicate job key copyAttachmentsFrom.", 0L, null);
                        }

                        seenCopyAttachments = true;
                        attachmentsToCopy.AddRange(ParseCopyAttachmentsArray(property.Value));
                        break;
                    case "json":
                        if (seenJson)
                        {
                            throw new QpdfParseException("Duplicate job key 'json'.", 0L, null);
                        }

                        seenJson = true;
                        jsonVersion = ParseJsonVersion(property.Value);
                        break;
                    case "jsonKey":
                        if (seenJsonKey)
                        {
                            throw new QpdfParseException("Duplicate job key 'jsonKey'.", 0L, null);
                        }

                        seenJsonKey = true;
                        jsonKeys.AddRange(ParseJsonKeyArray(property.Value));
                        break;
                    case "jsonObject":
                        if (seenJsonObject)
                        {
                            throw new QpdfParseException("Duplicate job key 'jsonObject'.", 0L, null);
                        }

                        seenJsonObject = true;
                        jsonObjects.AddRange(ParseJsonObjectArray(property.Value));
                        break;
                    case "jsonStreamData":
                        if (seenJsonStreamData)
                        {
                            throw new QpdfParseException("Duplicate job key 'jsonStreamData'.", 0L, null);
                        }

                        seenJsonStreamData = true;
                        jsonStreamData = RequireString(property.Value, "jsonStreamData");
                        if (jsonStreamData != "none" && jsonStreamData != "inline" && jsonStreamData != "file")
                        {
                            throw new QpdfParseException("Job \"jsonStreamData\" must be one of: none, inline, file.", 0L, null);
                        }

                        break;
                    case "jsonStreamPrefix":
                        if (seenJsonStreamPrefix)
                        {
                            throw new QpdfParseException("Duplicate job key 'jsonStreamPrefix'.", 0L, null);
                        }

                        seenJsonStreamPrefix = true;
                        jsonStreamPrefix = RequireString(property.Value, "jsonStreamPrefix");
                        if (jsonStreamPrefix.Length > QpdfPath.MaxLength || jsonStreamPrefix.Contains((char)0))
                        {
                            throw new QpdfParseException("Job \"jsonStreamPrefix\" is not a valid logical path.", 0L, null);
                        }

                        break;
                    case "jsonOutput":
                        if (seenJsonOutput)
                        {
                            throw new QpdfParseException("Duplicate job key 'jsonOutput'.", 0L, null);
                        }

                        seenJsonOutput = true;
                        jsonOutput = ParseJsonOutput(property.Value);
                        break;
                    case "jsonInput":
                        if (seenJsonInput)
                        {
                            throw new QpdfParseException("Duplicate job key 'jsonInput'.", 0L, null);
                        }

                        seenJsonInput = true;
                        jsonInput = RequireBareFlag(property.Value, "jsonInput");
                        break;
                    case "updateFromJson":
                        if (seenUpdateFromJson)
                        {
                            throw new QpdfParseException("Duplicate job key 'updateFromJson'.", 0L, null);
                        }

                        seenUpdateFromJson = true;
                        updateFromJson = RequireString(property.Value, "updateFromJson");
                        if (updateFromJson.Length == 0)
                        {
                            throw new QpdfParseException("Job \"updateFromJson\" must not be empty.", 0L, null);
                        }

                        break;
                    case "warningExit0":
                        if (seenWarningExit0)
                        {
                                throw new QpdfParseException("Duplicate job key warningExit0.", 0L, null);
                        }

                        seenWarningExit0 = true;
                        warningExit0 = RequireBareFlag(property.Value, "warningExit0");
                        break;
                    case "noWarn":
                        if (seenNoWarn)
                        {
                                throw new QpdfParseException("Duplicate job key noWarn.", 0L, null);
                        }

                        seenNoWarn = true;
                        noWarn = RequireBareFlag(property.Value, "noWarn");
                        break;
                    case "suppressRecovery":
                        if (seenSuppressRecovery)
                        {
                                throw new QpdfParseException("Duplicate job key suppressRecovery.", 0L, null);
                        }

                        seenSuppressRecovery = true;
                    suppressRecovery = RequireBareFlag(property.Value, "suppressRecovery");
                    recovery = QpdfRecoveryMode.Strict;
                        suppressRecovery = RequireBareFlag(property.Value, "suppressRecovery");
                        break;
                    case "encrypt":
                    case "decrypt":
                    case "password":
                    case "passwordFile":
                    case "passwordMode":
                    case "passwordIsHexKey":
                    case "suppressPasswordRecovery":
                    case "encryptionFilePassword":
                    case "allowWeakCrypto":
                    case "copyEncryption":
                    case "isEncrypted":
                    case "requiresPassword":
                    case "showEncryption":
                    case "showEncryptionKey":
                    case "staticAesIv":
                    case "removeRestrictions":
                        throw new QpdfCryptoException(property.Name);
                    default:
                        throw new QpdfParseException($"Unsupported job key '{property.Name}' in the narrow selection profile.", 0L, null);
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

            if (input is null)
            {
                throw new QpdfParseException("Job JSON requires \"inputFile\".", 0L, null);
            }

            bool wantAttachments = listAttachments || showAttachment is not null;
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

                    return new AttachmentPlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output), showAttachment, listAttachments, recovery);
                }

                if (output is not null)
                {
                    throw new QpdfParseException("Job \"outputFile\" must not be given without \"showAttachment\".", 0L, null);
                }

                return new AttachmentPlan(SelectionPlan.CheckedPath(input), null, null, true, recovery);
            }

            if (checkLinearization || showLinearization)
            {
                if (hasPages || output is not null
                    || wantAttachments || attachmentsToAdd.Count > 0 || attachmentsToRemove.Count > 0 || attachmentsToCopy.Count > 0
                    || seenRotate || seenLabels || seenRemoveLabels
                    || seenMin || seenForce || seenDeterministic || seenStatic || seenNewline
                    || seenObjectStreams || seenPreserveUnreferenced
                    || seenStreamData || seenCompressStreams || seenDecodeLevel || seenRecompressFlate || seenCompressionLevel
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

                return new LinearizationPlan(SelectionPlan.CheckedPath(input), recovery)
                {
                    Check = checkLinearization,
                    Show = showLinearization,
                };
            }

            if (showNpages)
            {
                if (hasPages || output is not null
                    || wantAttachments || attachmentsToAdd.Count > 0 || attachmentsToRemove.Count > 0 || attachmentsToCopy.Count > 0
                    || seenRotate || seenLabels || seenRemoveLabels
                    || seenMin || seenForce || seenDeterministic || seenStatic || seenNewline
                    || seenObjectStreams || seenPreserveUnreferenced
                    || seenStreamData || seenCompressStreams || seenDecodeLevel || seenRecompressFlate || seenCompressionLevel
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

                return new ShowNpagesPlan(SelectionPlan.CheckedPath(input), recovery)
                {
                    WarningExitZero = warningExit0,
                    NoWarn = noWarn,
                };
            }

            if (showPages)
            {
                if (hasPages || output is not null
                    || wantAttachments || attachmentsToAdd.Count > 0 || attachmentsToRemove.Count > 0 || attachmentsToCopy.Count > 0
                    || seenRotate || seenLabels || seenRemoveLabels
                    || seenMin || seenForce || seenDeterministic || seenStatic || seenNewline
                    || seenObjectStreams || seenPreserveUnreferenced
                    || seenStreamData || seenCompressStreams || seenDecodeLevel || seenRecompressFlate || seenCompressionLevel
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

                return new ShowPagesPlan(SelectionPlan.CheckedPath(input), recovery)
                {
                    WarningExitZero = warningExit0,
                    NoWarn = noWarn,
                };
            }

            if (showObject is not null)
            {
                if (hasPages || output is not null
                    || wantAttachments || attachmentsToAdd.Count > 0 || attachmentsToRemove.Count > 0 || attachmentsToCopy.Count > 0
                    || seenRotate || seenLabels || seenRemoveLabels
                    || seenMin || seenForce || seenDeterministic || seenStatic || seenNewline
                    || seenObjectStreams || seenPreserveUnreferenced
                    || seenStreamData || seenCompressStreams || seenDecodeLevel || seenRecompressFlate || seenCompressionLevel
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

                return new ShowObjectPlan(SelectionPlan.CheckedPath(input), recovery, showObject)
                {
                    WarningExitZero = warningExit0,
                    NoWarn = noWarn,
                };
            }

            if (showXref)
            {
                if (hasPages || output is not null
                    || wantAttachments || attachmentsToAdd.Count > 0 || attachmentsToRemove.Count > 0 || attachmentsToCopy.Count > 0
                    || seenRotate || seenLabels || seenRemoveLabels
                    || seenMin || seenForce || seenDeterministic || seenStatic || seenNewline
                    || seenObjectStreams || seenPreserveUnreferenced
                    || seenStreamData || seenCompressStreams || seenDecodeLevel || seenRecompressFlate || seenCompressionLevel
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

                return new ShowXrefPlan(SelectionPlan.CheckedPath(input), recovery)
                {
                    WarningExitZero = warningExit0,
                    NoWarn = noWarn,
                };
            }

            if (output is null)
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
                    || decodeLevel is not null || recompressFlate || compressionLevel is not null
                    || removeInfo || removeMetadata || preserveUnreferenced)
                {
                    throw new QpdfParseException("Stream and metadata keys cannot be combined with \"jsonInput\".", 0L, null);
                }

                return new JsonInputPlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output), recovery) with
                {
                    WarningExitZero = warningExit0,
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
                    || decodeLevel is not null || recompressFlate || compressionLevel is not null
                    || removeInfo || removeMetadata || preserveUnreferenced)
                {
                    throw new QpdfParseException("Stream and metadata keys cannot be combined with \"updateFromJson\".", 0L, null);
                }

                return new JsonUpdatePlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(updateFromJson!), SelectionPlan.CheckedPath(output), recovery) with
                {
                    WarningExitZero = warningExit0,
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
                if (output is null)
                {
                    throw new QpdfParseException("Job JSON requires \"outputFile\" with JSON inspection.", 0L, null);
                }

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
                List<string> wantedObjects = Qpdf.ValidateJsonObjects(jsonObjects, 0L);
                string jsonDecode = decodeLevel ?? (seenJsonOutput ? "none" : "generalized");
                if (jsonDecode != "none" && jsonDecode != "generalized" && jsonDecode != "specialized" && jsonDecode != "all")
                {
                    throw new QpdfParseException("Job \"decodeLevel\" must be one of: none, generalized, specialized, all.", 0L, null);
                }

                string jsonStreams = seenJsonStreamData ? jsonStreamData! : (seenJsonOutput ? "inline" : "none");
                return new JsonPlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output), jsonNumber, effectiveKeys, recovery) with
                {
                    DecodeLevel = jsonDecode,
                    StreamDataMode = jsonStreams,
                    StreamPrefix = jsonStreamPrefix,
                    Objects = wantedObjects,
                    MinimalOutput = seenJsonOutput,
                };
            }

            if (seenJsonKey && !seenJson)
            {
                throw new QpdfParseException("Job \"jsonKey\" requires \"json\".", 0L, null);
            }

            bool wantEdits = attachmentsToAdd.Count > 0 || attachmentsToRemove.Count > 0 || attachmentsToCopy.Count > 0;
            bool wantContent = coalesceContents || normalizeContent || removeUnreferencedResources is not null || externalizeInlineImages || seenIiMinBytes;
            bool wantOverlays = overlays.Count > 0 || underlays.Count > 0;
            bool wantFlatten = flattenRotation || flattenAnnotations is not null;
            bool wantGenerate = generateAppearances;
            bool wantQdf = qdf || seenNoOriginalObjectIds;
            bool wantOptimize = optimizeImages || seenOiMinWidth || seenOiMinHeight || seenOiMinArea || seenKeepInlineImages || seenJpegQuality;
            if (!hasPages)
            {
                if (!wantEdits && !wantContent && !wantOverlays && !wantFlatten && !wantGenerate && !wantOptimize && !wantQdf)
                {
                    throw new QpdfParseException("Job JSON requires \"pages\" with one entry.", 0L, null);
                }

                if (seenRotate || seenLabels || seenRemoveLabels)
                {
                    throw new QpdfParseException("Job rotation and label keys require \"pages\".", 0L, null);
                }

                if (output is null)
                {
                    throw new QpdfParseException("Job JSON requires \"outputFile\".", 0L, null);
                }

                return new WholeFilePlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output), recovery) with
                {
                    WarningExitZero = warningExit0,
                    NoWarn = noWarn,
                    MinVersion = minVersion,
                    ForceVersion = forceVersion,
                    DeterministicId = deterministicId,
                    StaticId = staticId,
                    NewlineBeforeEndstream = newlineBeforeEndstream,
                    ObjectStreams = objectStreams,
                    PreserveUnreferenced = preserveUnreferenced,
                    StreamData = streamData,
                    CompressStreams = compressStreams,
                    DecodeLevel = decodeLevel,
                    RecompressFlate = recompressFlate,
                    CompressionLevel = compressionLevel,
                    RemoveInfo = removeInfo,
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

            List<(string File, string Range)> entries = ParsePagesEntries(pages);
            if (entries.Count == 1)
            {
                string resolved = ResolveFile(entries[0].File, input);
                int page = SelectionPlan.ParsePageNumber(entries[0].Range);
                return new SelectionPlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output), page, recovery) with
                {
                    Rotations = rotations,
                    Labels = labels,
                    WarningExitZero = warningExit0,
                    NoWarn = noWarn,
                    MinVersion = minVersion,
                    ForceVersion = forceVersion,
                    DeterministicId = deterministicId,
                    StaticId = staticId,
                    NewlineBeforeEndstream = newlineBeforeEndstream,
                    ObjectStreams = objectStreams,
                    PreserveUnreferenced = preserveUnreferenced,
                    StreamData = streamData,
                    CompressStreams = compressStreams,
                    DecodeLevel = decodeLevel,
                    RecompressFlate = recompressFlate,
                    CompressionLevel = compressionLevel,
                    RemoveInfo = removeInfo,
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
            foreach ((string file, string range) in entries)
            {
                int page = SelectionPlan.ParsePageNumber(range);
                sources.Add(new PdfPageSource(SelectionPlan.CheckedPath(file), new PdfPageSelectionOptions(page)));
            }

            return new MergePlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output), sources, null, recovery) with
            {
                Rotations = rotations,
                Labels = labels,
                MinVersion = minVersion,
                ForceVersion = forceVersion,
                DeterministicId = deterministicId,
                StaticId = staticId,
                NewlineBeforeEndstream = newlineBeforeEndstream,
                    ObjectStreams = objectStreams,
                    PreserveUnreferenced = preserveUnreferenced,
                    StreamData = streamData,
                    CompressStreams = compressStreams,
                    DecodeLevel = decodeLevel,
                    RecompressFlate = recompressFlate,
                    CompressionLevel = compressionLevel,
                    RemoveInfo = removeInfo,
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
                    NoWarn = noWarn,
            };
        }
    }

    private static List<OutputRotation> ParseRotateArray(JsonElement value)
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

            string? specification = entry.GetString();
            if (specification is null)
            {
                throw new QpdfParseException("Job \"rotate\" entries must be strings.", 0L, null);
            }

            rotations.Add(OutputRotation.Parse(specification, 0L));
        }

        return rotations;
    }

    private static List<PageLabelSpec> ParseLabelArray(JsonElement value)
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

            string? specification = entry.GetString();
            if (specification is null)
            {
                throw new QpdfParseException("Job \"setPageLabels\" entries must be strings.", 0L, null);
            }

            labels.Add(PageLabelSpec.Parse(specification, 0L));
        }

        return labels;
    }

    private static List<AttachmentAdd> ParseAddAttachmentArray(JsonElement value)
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

            adds.Add(ParseAddAttachmentEntry(entry));
        }

        return adds;
    }

    private static AttachmentAdd ParseAddAttachmentEntry(JsonElement entry)
    {
        string? file = null;
        string? key = null;
        string? fileName = null;
        string? creationDate = null;
        string? modDate = null;
        string? mimeType = null;
        string? description = null;
        bool replace = false;
        foreach (JsonProperty property in entry.EnumerateObject())
        {
            switch (property.Name)
            {
                case "file":
                    if (file is not null)
                    {
                        throw new QpdfParseException("Duplicate addAttachment key.", 0L, null);
                    }

                    file = RequireString(property.Value, "addAttachment-file");
                    break;
                case "key":
                    if (key is not null)
                    {
                        throw new QpdfParseException("Duplicate addAttachment key.", 0L, null);
                    }

                    key = RequireString(property.Value, "addAttachment-key");
                    break;
                case "filename":
                    if (fileName is not null)
                    {
                        throw new QpdfParseException("Duplicate addAttachment key.", 0L, null);
                    }

                    fileName = RequireString(property.Value, "addAttachment-filename");
                    break;
                case "creationdate":
                    if (creationDate is not null)
                    {
                        throw new QpdfParseException("Duplicate addAttachment key.", 0L, null);
                    }

                    creationDate = RequireString(property.Value, "addAttachment-creationdate");
                    break;
                case "moddate":
                    if (modDate is not null)
                    {
                        throw new QpdfParseException("Duplicate addAttachment key.", 0L, null);
                    }

                    modDate = RequireString(property.Value, "addAttachment-moddate");
                    break;
                case "mimetype":
                    if (mimeType is not null)
                    {
                        throw new QpdfParseException("Duplicate addAttachment key.", 0L, null);
                    }

                    mimeType = RequireString(property.Value, "addAttachment-mimetype");
                    break;
                case "description":
                    if (description is not null)
                    {
                        throw new QpdfParseException("Duplicate addAttachment key.", 0L, null);
                    }

                    description = RequireString(property.Value, "addAttachment-description");
                    break;
                case "replace":
                    replace = RequireBareFlag(property.Value, "addAttachment-replace");
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

    private static List<string> ParseRemoveAttachmentArray(JsonElement value)
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

            string? key = entry.GetString();
            if (key is null || key.Length == 0)
            {
                throw new QpdfParseException("Job removeAttachment entries must be non-empty.", 0L, null);
            }

            removes.Add(key);
        }

        return removes;
    }

    private static List<AttachmentCopy> ParseCopyAttachmentsArray(JsonElement value)
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

            copies.Add(ParseCopyAttachmentsEntry(entry));
        }

        return copies;
    }

    private static AttachmentCopy ParseCopyAttachmentsEntry(JsonElement entry)
    {
        string? file = null;
        string? prefix = null;
        foreach (JsonProperty property in entry.EnumerateObject())
        {
            switch (property.Name)
            {
                case "file":
                    if (file is not null)
                    {
                        throw new QpdfParseException("Duplicate copyAttachmentsFrom key.", 0L, null);
                    }

                    file = RequireString(property.Value, "copyAttachmentsFrom-file");
                    break;
                case "prefix":
                    if (prefix is not null)
                    {
                        throw new QpdfParseException("Duplicate copyAttachmentsFrom key.", 0L, null);
                    }

                    prefix = RequireString(property.Value, "copyAttachmentsFrom-prefix");
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

    private static string ParseJsonVersion(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            && (number == 1 || number == 2))
        {
            return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            if (text == "1" || text == "2" || text == "latest")
            {
                return text;
            }
        }

        throw new QpdfParseException("Job \"json\" must be one of: 1, 2, latest.", 0L, null);
    }

    private static List<string> ParseJsonKeyArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job \"jsonKey\" must be an array of key names.", 0L, null);
        }

        List<string> keys = new();
        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not string text)
            {
                throw new QpdfParseException("Job \"jsonKey\" entries must be strings.", 0L, null);
            }

            keys.Add(text);
        }

        return keys;
    }

    private static string ParseJsonOutput(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            && number == 2)
        {
            return "2";
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            if (text == "2" || text == "latest")
            {
                return text;
            }
        }

        throw new QpdfParseException("Job \"jsonOutput\" must be one of: 2, latest.", 0L, null);
    }

    private static List<string> ParseJsonObjectArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job \"jsonObject\" must be an array of selectors.", 0L, null);
        }

        List<string> selectors = new();
        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not string text)
            {
                throw new QpdfParseException("Job \"jsonObject\" entries must be strings.", 0L, null);
            }

            selectors.Add(text);
        }

        return selectors;
    }

    private static long RequireOiMin(JsonElement element, string role)
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
            text = element.GetString();
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

    private static int RequireJpegQuality(JsonElement element)
    {
        long number;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long direct))
        {
            number = direct;
        }
        else if (element.ValueKind == JsonValueKind.String && element.GetString() is string text
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
    private static long RequireIiMinBytes(JsonElement element)
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
            text = element.GetString();
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

    private static bool RequireNormalizeContent(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String && element.GetString() is string text)
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

    private static List<OverlaySpec> ParseOverlayArray(JsonElement value, string role)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job overlay value must be an array.", 0L, null);
        }

        List<OverlaySpec> specs = new();
        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("Job overlay entries must be objects.", 0L, null);
            }

            specs.Add(ParseOverlayEntry(entry, role));
        }

        return specs;
    }

    private static OverlaySpec ParseOverlayEntry(JsonElement entry, string role)
    {
        string? file = null;
        string? to = null;
        string? from = null;
        string? repeat = null;
        foreach (JsonProperty property in entry.EnumerateObject())
        {
            switch (property.Name)
            {
                case "file":
                    if (file is not null)
                    {
                        throw new QpdfParseException("Duplicate overlay key file.", 0L, null);
                    }

                    file = RequireString(property.Value, role + ".file");
                    break;
                case "to":
                    if (to is not null)
                    {
                        throw new QpdfParseException("Duplicate overlay key to.", 0L, null);
                    }

                    to = RequireString(property.Value, role + ".to");
                    OverlayRanges.ValidateSyntax(to, "--to", 0L);
                    break;
                case "from":
                    if (from is not null)
                    {
                        throw new QpdfParseException("Duplicate overlay key from.", 0L, null);
                    }

                    from = RequireString(property.Value, role + ".from");
                    OverlayRanges.ValidateSyntax(from, "--from", 0L);
                    break;
                case "repeat":
                    if (repeat is not null)
                    {
                        throw new QpdfParseException("Duplicate overlay key repeat.", 0L, null);
                    }

                    repeat = RequireString(property.Value, role + ".repeat");
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

    private static List<(string File, string Range)> ParsePagesEntries(JsonElement pages)
    {
        if (pages.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job \"pages\" must be a non-empty array.", 0L, null);
        }

        if (pages.GetArrayLength() == 0)
        {
            throw new QpdfParseException("Job \"pages\" must hold at least one entry.", 0L, null);
        }

        List<(string File, string Range)> entries = new();
        foreach (JsonElement entry in pages.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("Job \"pages\" entries must be objects.", 0L, null);
            }

            entries.Add(ParsePagesEntry(entry));
        }

        return entries;
    }

    private static (string File, string Range) ParsePagesEntry(JsonElement entry)
    {
        string? file = null;
        string? range = null;
        foreach (JsonProperty property in entry.EnumerateObject())
        {
            switch (property.Name)
            {
                case "file":
                    if (file is not null)
                    {
                        throw new QpdfParseException("Duplicate pages key 'file'.", 0L, null);
                    }
                    file = RequireString(property.Value, "pages[].file");
                    break;
                case "range":
                    if (range is not null)
                    {
                        throw new QpdfParseException("Duplicate pages key 'range'.", 0L, null);
                    }
                    range = RequireString(property.Value, "pages[].range");
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

        if (range is null)
        {
            throw new QpdfParseException("Job pages entries require \"range\" in this profile.", 0L, null);
        }

        return (file, range);
    }

    private static string RequireString(JsonElement element, string role)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new QpdfParseException($"Job \"{role}\" must be a string.", 0L, null);
        }

        string? value = element.GetString();
        if (value is null)
        {
            throw new QpdfParseException($"Job \"{role}\" must be a string.", 0L, null);
        }

        return value;
    }

    private static string RequireObjectStreams(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new QpdfParseException("Job \"objectStreams\" must be disable, preserve, or generate.", 0L, null);
        }

        string? value = element.GetString();
        if (value != "disable" && value != "preserve" && value != "generate")
        {
            throw new QpdfParseException("Job \"objectStreams\" must be disable, preserve, or generate.", 0L, null);
        }

        return value;
    }

    private static bool RequireBareFlag(JsonElement element, string role)
    {
        if (element.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.String && element.GetString() == string.Empty)
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
