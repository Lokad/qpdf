using System.Text.Json;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Jobs;

/// <summary>
/// Shared semantic planner for selection, writer, content, overlay, attachment, inspection,
/// and linearization keys with upstream field names. Primary passwords support inspection and PDF transformations;
/// other unimplemented cryptographic keys reject as crypto;
/// everything outside the supported subset fails explicitly.
/// </summary>
internal static partial class JobPlanner
{
    // JSON objects keep the last value per key. Configuration layers repeat
    // setters; exclusive input/output/page declarations still reject.
    internal static JobPlan ParseProperties(
        IEnumerable<JobSetting> properties,
        ResourceGovernor governor, ConfigurationText textStorage)
    {
        CollationPreparation storage = CollationPreparation.Create(governor);
        textStorage.Collation = storage;
        try
        {
            JobPlan plan = ParsePropertiesCore(properties, governor, textStorage);
            if (!storage.HasStorage) { storage.Dispose(); return plan; }
            return plan with { CollationStorage = storage };
        }
        catch { storage.Dispose(); throw; }
        finally { textStorage.Collation = null; }
    }

    private static JobPlan ParsePropertiesCore(
        IEnumerable<JobSetting> properties,
        ResourceGovernor governor, ConfigurationText textStorage)
    {
            string? input = null;
            string? passwordText = null;
            List<PasswordAssignment>? passwordAssignments = null;
            string? passwordMode = null;
            bool suppressPasswordRecovery = false;
            bool isEncrypted = false;
            bool requiresPassword = false;
            bool showEncryption = false;
            bool decrypt = false;
            bool removeRestrictions = false;
            string? copyEncryption = null, encryptionFilePassword = null;
            bool encrypt = false;
            string encryptionUser = string.Empty, encryptionOwner = string.Empty;
            int encryptionPermissions = -4;
            bool encryptMetadata = true;
            bool empty = false;
            bool seenInput = false;
            bool seenOutput = false;
            bool seenPages = false;
            string? output = null;
            bool replaceInput = false;
            bool seenReplaceInput = false;
            int? splitPages = null;
            bool seenSplitPages = false;
            JobValue pages = default;
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
            bool ignoreXrefStreams = false;
            bool reportMemoryUsage = false;
            bool verbose = false;
            bool progress = false;
            bool rawStreamData = false;
            bool seenRawStreamData = false;
            bool filteredStreamData = false;
            bool seenFilteredStreamData = false;
            bool testJsonSchema = false;
            bool seenTestJsonSchema = false;
            long? parserMaxNesting = null;
            long? maxContainerSize = null;
            long? maxContainerSizeDamaged = null;
            long? parserMaxErrors = null;
            bool recompressFlate = false;
            bool seenRecompressFlate = false;
            string? compressionLevel = null;
            bool seenCompressionLevel = false;
            bool warningExit0 = false;
            bool noWarn = false;
            bool suppressRecovery = false;
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
            foreach (JobSetting property in properties)
            {
                governor.CheckCancellation();
                if (IsCryptoKey(property.Name) && property.Name is not ("password" or "passwordFile" or "passwordMode" or "suppressPasswordRecovery" or "decrypt" or "encrypt" or "copyEncryption" or "encryptionFilePassword" or "removeRestrictions"))
                { throw new QpdfCryptoException(property.Name); }
                switch (property.Name)
                {
                    case "removeRestrictions":
                        // Every PDF publication already disables active signatures.
                        removeRestrictions = RequireBareFlag(property.Value, "removeRestrictions", textStorage);
                        break;
                    case "decrypt":
                        decrypt = RequireBareFlag(property.Value, "decrypt", textStorage);
                        if (decrypt) { encrypt = false; copyEncryption = null; }
                        break;
                    case "encrypt":
                        if (seenDeterministic) { throw new QpdfParseException("Deterministic IDs cannot be combined with new encryption.", 0L, null); }
                        ParseEncryption(property.Value, ref encryptionUser, ref encryptionOwner,
                            ref encryptionPermissions, ref encryptMetadata, governor, textStorage);
                        encrypt = true;
                        decrypt = false;
                        copyEncryption = null;
                        break;
                    case "copyEncryption":
                        if (seenDeterministic) { throw new QpdfParseException("Deterministic IDs cannot be combined with copied encryption.", 0L, null); }
                        copyEncryption = RequireString(property.Value, "copyEncryption", textStorage);
                        _ = SelectionPlan.CheckedPath(copyEncryption);
                        encrypt = false; decrypt = false;
                        break;
                    case "encryptionFilePassword":
                        encryptionFilePassword = RequireString(property.Value, "encryptionFilePassword", textStorage);
                        break;
                    case "isEncrypted":
                        isEncrypted = RequireBareFlag(property.Value, "isEncrypted", textStorage);
                        break;
                    case "requiresPassword":
                        requiresPassword = RequireBareFlag(property.Value, "requiresPassword", textStorage);
                        break;
                    case "password":
                        passwordText = RequireString(property.Value, "password", textStorage);
                        if (passwordAssignments is not null)
                        { ParserContainers.AddItem(textStorage.Memory, passwordAssignments, new(passwordText, false), 16L); }
                        break;
                    case "passwordFile":
                        string passwordFile = RequireString(property.Value, "passwordFile", textStorage);
                        _ = SelectionPlan.CheckedPath(passwordFile);
                        if (passwordAssignments is null)
                        {
                            passwordAssignments = ParserContainers.CreateList<PasswordAssignment>(textStorage.Memory);
                            if (passwordText is not null)
                            { ParserContainers.AddItem(textStorage.Memory, passwordAssignments, new(passwordText, false), 16L); }
                        }
                        ParserContainers.AddItem(textStorage.Memory, passwordAssignments, new(passwordFile, true), 16L);
                        break;
                    case "suppressPasswordRecovery":
                        suppressPasswordRecovery = RequireBareFlag(property.Value, "suppressPasswordRecovery", textStorage);
                        break;
                    case "passwordMode":
                        passwordMode = RequireString(property.Value, "passwordMode", textStorage);
                        if (passwordMode is not ("auto" or "bytes" or "unicode" or "hex-bytes"))
                        { throw new QpdfParseException("Password mode must be auto, bytes, unicode or hex-bytes.", 0L, null); }
                        break;
                    case "showEncryption":
                        showEncryption = RequireBareFlag(property.Value, "showEncryption", textStorage);
                        break;
                    case "empty":
                        empty = RequireBareFlag(property.Value, "empty", textStorage);
                        if (empty && input is not null) { throw new QpdfParseException("Empty input cannot follow an input file.", 0L, null); }
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
                        seenCollate = true;
                        collate = RequireString(property.Value, "collate", textStorage);
                        collationSizes = ArgvParser.ParseCollateValue(collate, 0L, textStorage.Collation ?? throw new InvalidOperationException("Missing collation owner."));
                        break;
                    case "rotate":
                        seenRotate = true;
                        foreach (OutputRotation rotation in ParseRotateArray(property.Value, textStorage))
                        {
                            rotations.Add(rotation);
                        }
                        break;
                    case "setPageLabels":
                        seenLabels = true;
                        labels.Clear();
                        labels.AddRange(ParseLabelArray(property.Value, textStorage));
                        break;
                    case "removePageLabels":
                        seenRemoveLabels = true;
                        if (property.Value.ValueKind != JsonValueKind.True && property.Value.ValueKind != JsonValueKind.False)
                        {
                            throw new QpdfParseException("Job \"removePageLabels\" must be a boolean.", 0L, null);
                        }

                        removeLabels = property.Value.ValueKind == JsonValueKind.True;
                        break;
                    case "minVersion":
                        seenMin = true;
                        minVersion = RequireString(property.Value, "minVersion", textStorage);
                        break;
                    case "forceVersion":
                        seenForce = true;
                        forceVersion = RequireString(property.Value, "forceVersion", textStorage);
                        break;
                    case "deterministicId":
                        if (encrypt || copyEncryption is not null) { throw new QpdfParseException("Deterministic IDs cannot be combined with new or copied encryption.", 0L, null); }
                        seenDeterministic = true;
                        RequireBareFlag(property.Value, "deterministicId", textStorage);
                        break;
                    case "staticId":
                        seenStatic = true;
                        staticId = RequireBareFlag(property.Value, "staticId", textStorage);
                        break;
                    case "newlineBeforeEndstream":
                        seenNewline = true;
                        newlineBeforeEndstream = RequireBareFlag(property.Value, "newlineBeforeEndstream", textStorage);
                        break;
                    case "objectStreams":
                        seenObjectStreams = true;
                        objectStreams = RequireObjectStreams(property.Value, textStorage);
                        break;
                    case "preserveUnreferenced":
                        seenPreserveUnreferenced = true;
                        preserveUnreferenced = RequireBareFlag(property.Value, "preserveUnreferenced", textStorage);
                        break;
                    case "streamData":
                        seenStreamData = true;
                        streamData = RequireString(property.Value, "streamData", textStorage);
                        compressionSettings = compressionSettings.WithStreamData(streamData);
                        break;
                    case "compressStreams":
                        seenCompressStreams = true;
                        compressStreams = RequireString(property.Value, "compressStreams", textStorage);
                        compressionSettings = compressionSettings.WithCompressStreams(compressStreams);
                        break;
                    case "decodeLevel":
                        seenDecodeLevel = true;
                        decodeLevel = RequireString(property.Value, "decodeLevel", textStorage);
                        compressionSettings = compressionSettings.WithDecodeLevel(decodeLevel);
                        break;
                    case "global":
                        if (property.Value.ValueKind != JsonValueKind.Object)
                        {
                            throw new QpdfParseException("Job \u0027global\u0027 must be an object.", 0L, null);
                        }

                        foreach (JobSetting globalProperty in EffectiveProperties(property.Value))
                        {
                            switch (globalProperty.Name)
                            {
                                case "maxStreamFilters":
                                    maxStreamFilters = RequireMaxStreamFilters(globalProperty.Value, textStorage);
                                    break;
                                case "parserMaxNesting":
                                    parserMaxNesting = RequireParserMaxNesting(globalProperty.Value, textStorage);
                                    break;
                                case "noDefaultLimits":
                                    noDefaultLimits = RequireBareFlag(globalProperty.Value, "global.noDefaultLimits", textStorage);
                                    break;
                                case "parserMaxContainerSize":
                                    maxContainerSize = RequireContainerSize(globalProperty.Value, "parserMaxContainerSize", textStorage);
                                    break;
                                case "parserMaxContainerSizeDamaged":
                                    maxContainerSizeDamaged = RequireContainerSize(globalProperty.Value, "parserMaxContainerSizeDamaged", textStorage);
                                    break;
                                case "parserMaxErrors":
                                    parserMaxErrors = RequireParserMaxErrors(globalProperty.Value, textStorage);
                                    break;
                                default:
                                    throw new QpdfParseException("Unsupported job key \u0027global." + ArgvParser.ErrorPreview(globalProperty.Name) + "\u0027 in the narrow selection profile.", 0L, null);
                            }
                        }

                        break;
                    case "recompressFlate":
                        seenRecompressFlate = true;
                        recompressFlate = RequireBareFlag(property.Value, "recompressFlate", textStorage);
                        break;
                    case "compressionLevel":
                        seenCompressionLevel = true;
                        compressionLevel = RequireString(property.Value, "compressionLevel", textStorage);
                        CompressionRequest.ParseLevel(compressionLevel, "compressionLevel");
                        break;
                    case "removeInfo":
                        seenRemoveInfo = true;
                        removeInfo = RequireBareFlag(property.Value, "removeInfo", textStorage);
                        break;
                    case "removeMetadata":
                        seenRemoveMetadata = true;
                        removeMetadata = RequireBareFlag(property.Value, "removeMetadata", textStorage);
                        break;
                    case "coalesceContents":
                        seenCoalesceContents = true;
                        coalesceContents = RequireBareFlag(property.Value, "coalesceContents", textStorage);
                        break;
                    case "normalizeContent":
                        seenNormalizeContent = true;
                        normalizeContent = RequireNormalizeContent(property.Value, textStorage);
                        break;
                    case "externalizeInlineImages":
                        seenExternalizeInlineImages = true;
                        externalizeInlineImages = RequireBareFlag(property.Value, "externalizeInlineImages", textStorage);
                        break;
                    case "iiMinBytes":
                        seenIiMinBytes = true;
                        iiMinBytes = RequireIiMinBytes(property.Value, textStorage);
                        break;
                    case "removeUnreferencedResources":
                        seenRemoveUnreferencedResources = true;
                        removeUnreferencedResources = RequireString(property.Value, "removeUnreferencedResources", textStorage);
                        if (removeUnreferencedResources != "auto" && removeUnreferencedResources != "yes" && removeUnreferencedResources != "no")
                        {
                            throw new QpdfParseException("Job \u0027removeUnreferencedResources\u0027 must be one of: auto, yes, no.", 0L, null);
                        }

                        break;

                    case "preserveUnreferencedResources":
                        if (RequireBareFlag(property.Value, "preserveUnreferencedResources", textStorage))
                        {
                            removeUnreferencedResources = "no";
                        }

                        break;
                    case "overlay":
                        seenOverlay = true;
                        overlays.AddRange(ParseOverlayArray(property.Value, "overlay", textStorage));
                        break;
                    case "underlay":
                        seenUnderlay = true;
                        underlays.AddRange(ParseOverlayArray(property.Value, "underlay", textStorage));
                        break;
                    case "flattenRotation":
                        seenFlattenRotation = true;
                        flattenRotation = RequireBareFlag(property.Value, "flattenRotation", textStorage);
                        break;
                    case "flattenAnnotations":
                        seenFlattenAnnotations = true;
                        flattenAnnotations = RequireString(property.Value, "flattenAnnotations", textStorage);
                        if (flattenAnnotations != "all" && flattenAnnotations != "print" && flattenAnnotations != "screen")
                        {
                            throw new QpdfParseException("Job flattenAnnotations must be one of: all, print, screen.", 0L, null);
                        }

                        break;
                    case "generateAppearances":
                        seenGenerateAppearances = true;
                        generateAppearances = RequireBareFlag(property.Value, "generateAppearances", textStorage);
                        break;
                    case "qdf":
                        seenQdf = true;
                        qdf = RequireBareFlag(property.Value, "qdf", textStorage);
                        if (qdf) { compressionSettings = compressionSettings.WithQdf(); }
                        break;
                    case "linearize":
                        seenLinearize = true;
                        linearize = RequireBareFlag(property.Value, "linearize", textStorage);
                        break;
                    case "linearizePass1":
                        seenLinearizePass1 = true;
                        linearizePass1 = RequireString(property.Value, "linearizePass1", textStorage);
                        if (linearizePass1.Length == 0)
                        {
                            throw new QpdfParseException("Job \"linearizePass1\" must not be empty.", 0L, null);
                        }

                        break;
                    case "check":
                        if (property.Value.ValueKind != JsonValueKind.String || property.Value.ReadString(textStorage) != string.Empty)
                        {
                            throw new QpdfParseException("Job check must be an empty string.", 0L, null);
                        }

                        check = true;
                        break;
                    case "checkLinearization":
                        seenCheckLinearization = true;
                        checkLinearization = RequireBareFlag(property.Value, "checkLinearization", textStorage);
                        break;
                    case "showLinearization":
                        seenShowLinearization = true;
                        showLinearization = RequireBareFlag(property.Value, "showLinearization", textStorage);
                        break;
                    case "showNpages":
                        seenShowNpages = true;
                        showNpages = RequireBareFlag(property.Value, "showNpages", textStorage);
                        break;
                    case "showPages":
                        seenShowPages = true;
                        showPages = RequireBareFlag(property.Value, "showPages", textStorage);
                        break;
                    case "withImages":
                        seenWithImages = true;
                        withImages = RequireBareFlag(property.Value, "withImages", textStorage);
                        break;
                    case "showObject":
                        showObject = RequireString(property.Value, "showObject", textStorage);
                        var selector = SelectionPlan.ParseInspectionSelector(showObject, showObjectGeneration);
                        showObjectGeneration = selector.Generation;
                        showObject = selector.Trailer ? "trailer"
                            : selector.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
                                + selector.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "showXref":
                        seenShowXref = true;
                        showXref = RequireBareFlag(property.Value, "showXref", textStorage);
                        break;
                    case "removeAcroform":
                        removeAcroform = RequireBareFlag(property.Value, "removeAcroform", textStorage);
                        break;
                    case "removeStructure":
                        removeStructure = RequireBareFlag(property.Value, "removeStructure", textStorage);
                        break;
                    case "noOriginalObjectIds":
                        seenNoOriginalObjectIds = true;
                        noOriginalObjectIds = RequireBareFlag(property.Value, "noOriginalObjectIds", textStorage);
                        break;
                    case "optimizeImages":
                        seenOptimizeImages = true;
                        optimizeImages = RequireBareFlag(property.Value, "optimizeImages", textStorage);
                        break;
                    case "oiMinWidth":
                        seenOiMinWidth = true;
                        oiMinWidth = RequireOiMin(property.Value, "oiMinWidth", textStorage);
                        break;
                    case "oiMinHeight":
                        seenOiMinHeight = true;
                        oiMinHeight = RequireOiMin(property.Value, "oiMinHeight", textStorage);
                        break;
                    case "oiMinArea":
                        seenOiMinArea = true;
                        oiMinArea = RequireOiMin(property.Value, "oiMinArea", textStorage);
                        break;
                    case "keepInlineImages":
                        seenKeepInlineImages = true;
                        keepInlineImages = RequireBareFlag(property.Value, "keepInlineImages", textStorage);
                        break;
                    case "jpegQuality":
                        seenJpegQuality = true;
                        jpegQuality = RequireJpegQuality(property.Value, textStorage);
                        break;
                    case "listAttachments":
                        seenListAttachments = true;
                        listAttachments = RequireBareFlag(property.Value, "listAttachments", textStorage);
                        break;
                    case "ignoreXrefStreams":
                        ignoreXrefStreams = RequireBareFlag(property.Value, "ignoreXrefStreams", textStorage);
                        break;
                    case "reportMemoryUsage":
                        reportMemoryUsage = RequireBareFlag(property.Value, "reportMemoryUsage", textStorage);
                        break;
                    case "verbose":
                        verbose = RequireBareFlag(property.Value, "verbose", textStorage);
                        break;
                    case "progress":
                        progress = RequireBareFlag(property.Value, "progress", textStorage);
                        break;
                    case "keepFilesOpen":
                        ValidateKeepFilesOpen(property.Value, textStorage);
                        break;
                    case "keepFilesOpenThreshold":
                        ValidateKeepFilesOpenThreshold(property.Value, textStorage);
                        break;
                    case "rawStreamData":
                        seenRawStreamData = true;
                        rawStreamData = RequireBareFlag(property.Value, "rawStreamData", textStorage);
                        break;
                    case "filteredStreamData":
                        seenFilteredStreamData = true;
                        filteredStreamData = RequireBareFlag(property.Value, "filteredStreamData", textStorage);
                        break;
                    case "testJsonSchema":
                        seenTestJsonSchema = true;
                        testJsonSchema = RequireBareFlag(property.Value, "testJsonSchema", textStorage);
                        break;
                    case "showAttachment":
                        showAttachment = RequireString(property.Value, "showAttachment", textStorage);
                        break;
                    case "addAttachment":
                        seenAddAttachment = true;
                        attachmentsToAdd.AddRange(ParseAddAttachmentArray(property.Value, textStorage));
                        break;
                    case "removeAttachment":
                        seenRemoveAttachment = true;
                        attachmentsToRemove.AddRange(ParseRemoveAttachmentArray(property.Value, textStorage));
                        break;
                    case "copyAttachmentsFrom":
                        seenCopyAttachments = true;
                        attachmentsToCopy.AddRange(ParseCopyAttachmentsArray(property.Value, textStorage));
                        break;
                    case "json":
                        seenJson = true;
                        jsonVersion = ParseJsonVersion(property.Value, textStorage);
                        break;
                    case "jsonKey":
                        seenJsonKey = true;
                        jsonKeys.AddRange(ParseJsonKeyArray(property.Value, textStorage));
                        break;
                    case "jsonObject":
                        seenJsonObject = true;
                        jsonObjects.AddRange(ParseJsonObjectArray(property.Value, textStorage));
                        break;
                    case "jsonStreamData":
                        seenJsonStreamData = true;
                        jsonStreamData = RequireString(property.Value, "jsonStreamData", textStorage);
                        if (jsonStreamData != "none" && jsonStreamData != "inline" && jsonStreamData != "file")
                        {
                            throw new QpdfParseException("Job \"jsonStreamData\" must be one of: none, inline, file.", 0L, null);
                        }

                        break;
                    case "jsonStreamPrefix":
                        seenJsonStreamPrefix = true;
                        jsonStreamPrefix = RequireString(property.Value, "jsonStreamPrefix", textStorage);
                        if (jsonStreamPrefix.Length > QpdfPath.MaxLength || jsonStreamPrefix.Contains((char)0))
                        {
                            throw new QpdfParseException("Job \"jsonStreamPrefix\" is not a valid logical path.", 0L, null);
                        }

                        break;
                    case "jsonOutput":
                        seenJsonOutput = true;
                        jsonOutput = ParseJsonOutput(property.Value, textStorage);
                        break;
                    case "jsonInput":
                        seenJsonInput = true;
                        jsonInput = RequireBareFlag(property.Value, "jsonInput", textStorage);
                        break;
                    case "updateFromJson":
                        seenUpdateFromJson = true;
                        updateFromJson = RequireString(property.Value, "updateFromJson", textStorage);
                        if (updateFromJson.Length == 0)
                        {
                            throw new QpdfParseException("Job \"updateFromJson\" must not be empty.", 0L, null);
                        }

                        break;
                    case "warningExit0":
                        warningExit0 = RequireBareFlag(property.Value, "warningExit0", textStorage);
                        break;
                    case "noWarn":
                        noWarn = RequireBareFlag(property.Value, "noWarn", textStorage);
                        break;
                    case "suppressRecovery":
                        suppressRecovery = RequireBareFlag(property.Value, "suppressRecovery", textStorage);
                        recovery = suppressRecovery ? QpdfRecoveryMode.Strict : QpdfRecoveryMode.BoundedRepair;
                        break;
                    default:
                        throw new QpdfParseException($"Unsupported job key '{ArgvParser.ErrorPreview(property.Name)}' in the narrow selection profile.", 0L, null);
                }
            }

            OutputRotation.NormalizeCommandSettings(rotations, governor);

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

            QpdfPassword? password = null;
            CommandPasswordFiles? passwordFiles = null;
            if (passwordAssignments is not null)
            {
                textStorage.Memory.Reserve(32L);
                passwordFiles = new(passwordAssignments, passwordMode == "hex-bytes", !suppressPasswordRecovery);
            }
            else if (passwordText is not null)
            {
                textStorage.Memory.Reserve(48L);
                password = passwordMode == "hex-bytes" ? QpdfPassword.FromHexText(passwordText, !suppressPasswordRecovery)
                    : QpdfPassword.FromText(passwordText, !suppressPasswordRecovery);
            }
            if (isEncrypted && requiresPassword)
            { throw new QpdfParseException("isEncrypted and requiresPassword are mutually exclusive.", 0L, null); }
            PrepareOverlayPasswords(overlays, passwordMode, suppressPasswordRecovery, textStorage, governor);
            PrepareOverlayPasswords(underlays, passwordMode, suppressPasswordRecovery, textStorage, governor);
            for (int index = 0; index < attachmentsToCopy.Count; index++)
            {
                governor.CheckCancellation();
                AttachmentCopy copy = attachmentsToCopy[index];
                if (copy.PasswordText is not string sourcePassword) { continue; }
                textStorage.Memory.Reserve(48L);
                attachmentsToCopy[index] = copy with { PasswordText = null,
                    Password = ParseSourcePassword(sourcePassword, passwordMode, suppressPasswordRecovery, textStorage) };
            }
            if (isEncrypted || requiresPassword)
            {
                (QpdfPath securityInput, bool securityEmpty) = ResolveInspectionInput(input, empty, "security inspection");
                return new SecurityPlan(securityInput, recovery, requiresPassword, password)
                {
                    PasswordFiles = passwordFiles,
                    EmptyDocument = securityEmpty,
                    IgnoreXrefStreams = ignoreXrefStreams,
                    MaxStreamFilters = maxStreamFilters,
                    ParserMaxNesting = parserMaxNesting,
                    MaxContainerSize = maxContainerSize,
                    MaxContainerSizeDamaged = maxContainerSizeDamaged,
                    ParserMaxErrors = parserMaxErrors,
                    NoDefaultLimits = noDefaultLimits,
                    NoWarn = noWarn,
                };
            }

            if (removeRestrictions && (seenJson || seenJsonOutput || check || showEncryption
                || showAttachment is not null || showObject is not null || showNpages || showPages
                || showXref || listAttachments || checkLinearization || showLinearization))
            {
                throw new QpdfParseException("removeRestrictions requires PDF publication; inspections retain original signature values.", 0L, null);
            }

            bool wantAttachments = listAttachments || showAttachment is not null;
            if (check || showEncryption || (output is null && (showAttachment is not null || showObject is not null
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
                    List<(string File, string? Range, string? Password)> inspectionEntries = ParsePagesEntries(pages, textStorage);
                    foreach ((string file, string? _, string? _) in inspectionEntries)
                    {
                        if (empty && file == ".") { throw new QpdfParseException("Cannot select from '.' with empty input.", 0L, null); }
                    }
                    inspectionSources = ParsePageSources(inspectionEntries, input, passwordMode, suppressPasswordRecovery, textStorage, governor);
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
                    PasswordFiles = passwordFiles,
                    Password = password,
                    ShowEncryption = showEncryption,
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
                    CheckLinearization = checkLinearization,
                    ShowLinearization = showLinearization,
                    WarningExitZero = warningExit0,
                    NoWarn = noWarn,
                    Verbose = verbose,
                    Progress = progress,
                    ReportMemoryUsage = reportMemoryUsage,
                    IgnoreXrefStreams = ignoreXrefStreams,
                    MaxStreamFilters = maxStreamFilters,
                    ParserMaxNesting = parserMaxNesting,
                    MaxContainerSize = maxContainerSize,
                    MaxContainerSizeDamaged = maxContainerSizeDamaged,
                    ParserMaxErrors = parserMaxErrors,
                    NoDefaultLimits = noDefaultLimits,
                    NormalizeContent = normalizeContent || (qdf && !seenNormalizeContent),
                };
            }

            QpdfOutputEncryption? outputEncryption = decrypt ? QpdfOutputEncryption.Remove : null;
            if (copyEncryption is not null)
            {
                textStorage.Memory.Reserve(64L);
                QpdfPassword? templatePassword = encryptionFilePassword is null ? null
                    : ParseSourcePassword(encryptionFilePassword, passwordMode, suppressPasswordRecovery, textStorage);
                outputEncryption = QpdfOutputEncryption.CopyFrom(SelectionPlan.CheckedPath(copyEncryption), templatePassword);
            }
            if (encrypt)
            {
                textStorage.Memory.Reserve(160L);
                QpdfPassword user = passwordMode == "hex-bytes" ? QpdfPassword.FromHexText(encryptionUser, false) : QpdfPassword.FromText(encryptionUser, false);
                QpdfPassword owner = passwordMode == "hex-bytes" ? QpdfPassword.FromHexText(encryptionOwner, false) : QpdfPassword.FromText(encryptionOwner, false);
                outputEncryption = QpdfOutputEncryption.CreateAes256(user, owner, encryptionPermissions, encryptMetadata);
            }
            if (wantAttachments && hasPages)
            {
                throw new QpdfParseException("Job attachment keys cannot be combined with \"pages\".", 0L, null);
            }

            if (wantAttachments && output is string attachmentOutput)
            {
                if (showAttachment is null)
                {
                    throw new QpdfParseException("Job \"outputFile\" must not be given without \"showAttachment\".", 0L, null);
                }

                (QpdfPath attachmentInput, bool attachmentEmpty) = ResolveInspectionInput(input, empty, "attachment inspection");
                return new AttachmentPlan(attachmentInput, SelectionPlan.CheckedPath(attachmentOutput), showAttachment, listAttachments, recovery)
                {
                    TransformationPassword = password,
                    PasswordFiles = passwordFiles,
                    EmptyDocument = attachmentEmpty,
                    IgnoreXrefStreams = ignoreXrefStreams,
                    ReportMemoryUsage = reportMemoryUsage,
                    Verbose = verbose,
                    Progress = progress,
                    NoWarn = noWarn,
                    WarningExitZero = warningExit0,
                    MaxStreamFilters = maxStreamFilters,
                    ParserMaxNesting = parserMaxNesting,
                    MaxContainerSize = maxContainerSize,
                    MaxContainerSizeDamaged = maxContainerSizeDamaged,
                    ParserMaxErrors = parserMaxErrors,
                    NoDefaultLimits = noDefaultLimits,
                };
            }

            // Output-free inspections returned through CheckPlan above. Here an
            // output target remains; only binary object/attachment extraction accepts it.
            if (checkLinearization || showLinearization)
            {
                throw new QpdfParseException("Job linearization inspection cannot be combined with transformation or other inspection keys.", 0L, null);
            }

            if (showNpages)
            {
                throw new QpdfParseException("Job showNpages cannot be combined with page, output, transformation or other inspection keys.", 0L, null);
            }

            if (showPages)
            {
                throw new QpdfParseException("Job showPages cannot be combined with page, output, transformation or other inspection keys.", 0L, null);
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
                    TransformationPassword = password,
                    PasswordFiles = passwordFiles,
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
                throw new QpdfParseException("Job showXref cannot be combined with page, output, transformation or other inspection keys.", 0L, null);
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

            // Complete validation before any restricted JSON-plan return. These
            // plans cannot run geometry/layout operations; ordinary checking and
            // document-JSON writer ignores retain their separate policies.
            bool importJson = jsonInput || seenUpdateFromJson;
            if ((importJson || seenJson || seenJsonOutput)
                && (replaceInput || splitPages is not null || seenCollate || rotations.Count > 0
                    || flattenRotation || flattenAnnotations is not null || generateAppearances
                    || qdf || linearize || overlays.Count > 0 || underlays.Count > 0))
            {
                throw new QpdfParseException("JSON operations cannot be combined with page transformations or PDF layout settings.", 0L, null);
            }

            if (importJson && (objectStreams is not null || streamData is not null || compressStreams is not null
                || decodeLevel is not null || maxStreamFilters is not null || noDefaultLimits || testJsonSchema
                || parserMaxNesting is not null || maxContainerSize is not null || maxContainerSizeDamaged is not null
                || parserMaxErrors is not null || recompressFlate || compressionLevel is not null || preserveUnreferenced
                || removeInfo || removeMetadata || removeAcroform || removeStructure || labels.Count > 0 || removeLabels
                || coalesceContents || normalizeContent || externalizeInlineImages || iiMinBytes != 1024L
                || removeUnreferencedResources is not (null or "no") || noOriginalObjectIds || linearizePass1 is not null
                || optimizeImages || oiMinWidth != 128L || oiMinHeight != 128L || oiMinArea != 16384L
                || keepInlineImages || jpegQuality is not null || rawStreamData || filteredStreamData
                || showObject is not null || showNpages || showPages || checkLinearization || showLinearization))
            {
                throw new QpdfParseException("Stream, metadata, content, image and PDF layout settings cannot be combined with JSON import.", 0L, null);
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

                return new JsonInputPlan(SelectionPlan.CheckedPath(input ?? throw new QpdfParseException("Job JSON requires \"inputFile\".", 0L, null)), SelectionPlan.CheckedPath(output), recovery) with
                {
                    PasswordFiles = passwordFiles,
                    OutputEncryption = outputEncryption,
                    WarningExitZero = warningExit0,
                    ReportMemoryUsage = reportMemoryUsage,
                    Verbose = verbose,
                    Progress = progress,
                    IgnoreXrefStreams = ignoreXrefStreams,
                    NoWarn = noWarn,
                    MinVersion = minVersion,
                    ForceVersion = forceVersion,
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

                return new JsonUpdatePlan(SelectionPlan.CheckedPath(input ?? throw new QpdfParseException("Job JSON requires \"inputFile\".", 0L, null)), SelectionPlan.CheckedPath(updateFromJson!), SelectionPlan.CheckedPath(output), recovery) with
                {
                    TransformationPassword = password,
                    PasswordFiles = passwordFiles,
                    OutputEncryption = outputEncryption,
                    WarningExitZero = warningExit0,
                    ReportMemoryUsage = reportMemoryUsage,
                    Verbose = verbose,
                    Progress = progress,
                    IgnoreXrefStreams = ignoreXrefStreams,
                    NoWarn = noWarn,
                    MinVersion = minVersion,
                    ForceVersion = forceVersion,
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
                HashSet<(bool Trailer, int Number, int Generation)>? wantedObjects = Qpdf.ValidateJsonObjects(jsonObjects, 0L, textStorage);
                string jsonDecode = decodeLevel ?? (seenJsonOutput ? "none" : "generalized");
                if (jsonDecode != "none" && jsonDecode != "generalized" && jsonDecode != "specialized" && jsonDecode != "all")
                {
                    throw new QpdfParseException("Job \"decodeLevel\" must be one of: none, generalized, specialized, all.", 0L, null);
                }

                string jsonStreams = seenJsonStreamData ? jsonStreamData! : (seenJsonOutput ? "inline" : "none");
                (QpdfPath inspectionInput, bool emptyDocument) = ResolveInspectionInput(input, empty, "json inspection");
                return new JsonPlan(inspectionInput, output is null ? null : SelectionPlan.CheckedPath(output), jsonNumber, effectiveKeys, recovery) with
                {
                    PasswordFiles = passwordFiles,
                    Password = password,
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

            if (empty && (overlays.Count > 0 || underlays.Count > 0))
            {
                throw new QpdfParseException("Overlay and underlay blocks cannot be combined with empty input.", 0L, null);
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
                    TransformationPassword = password,
                    PasswordFiles = passwordFiles,
                    OutputEncryption = outputEncryption,
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

            List<(string File, string? Range, string? Password)> entries = ParsePagesEntries(pages, textStorage);
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
                foreach ((string file, string? _, string? _) in entries)
                {
                    if (file == ".")
                    {
                        throw new QpdfParseException("Cannot select from \u0027.\u0027 with empty input.", 0L, null);
                    }
                }

            }

            if (entries.Count == 1 && !empty && collation is null && splitPages is null && SelectionPlan.IsPlainPageNumber(entries[0].Range)
                && (entries[0].File == "." || entries[0].File == input))
            {
                int page = SelectionPlan.ParsePageNumber(entries[0].Range);
                return new SelectionPlan(SelectionPlan.CheckedPath(input ?? throw new QpdfParseException("Job JSON requires \"inputFile\".", 0L, null)), SelectionPlan.CheckedPath(output ?? input), page, recovery) with
                {
                    TransformationPassword = password,
                    PasswordFiles = passwordFiles,
                    OutputEncryption = outputEncryption,
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

            List<PdfPageSource> sources = ParsePageSources(entries, input, passwordMode, suppressPasswordRecovery, textStorage, governor);

            string primary = input ?? entries[0].File;
            return new MergePlan(SelectionPlan.CheckedPath(primary), SelectionPlan.CheckedPath(output ?? primary), sources, collation, recovery) with
            {
                TransformationPassword = password,
                PasswordFiles = passwordFiles,
                OutputEncryption = outputEncryption,
                EmptyInput = empty,
                ReplaceInput = replaceInput,
                Rotations = rotations,
                Labels = labels,
                RemoveLabels = removeLabels,
                SplitPages = splitPages,
                MinVersion = minVersion,
                ForceVersion = forceVersion,
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
        => name is "encrypt" or "decrypt" or "password" or "passwordFile" or "passwordMode" or "passwordIsHexKey" or "suppressPasswordRecovery" or "encryptionFilePassword" or "allowWeakCrypto" or "copyEncryption" or "showEncryptionKey" or "staticAesIv" or "removeRestrictions";

    internal static IEnumerable<JobSetting> EffectiveProperties(JobValue value)
    {
        Dictionary<string, JobSetting> properties = new(StringComparer.Ordinal);
        foreach (JobSetting property in value.EnumerateObject()) { properties[property.Name] = property; }
        return properties.Values;
    }

    private static List<OutputRotation> ParseRotateArray(JobValue value, ConfigurationText textStorage)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job \"rotate\" must be an array of rotation specifications.", 0L, null);
        }

        List<OutputRotation> rotations = new();
        foreach (JobValue entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                throw new QpdfParseException("Job \"rotate\" entries must be strings.", 0L, null);
            }

            string? specification = entry.ReadString(textStorage);
            if (specification is null)
            {
                throw new QpdfParseException("Job \"rotate\" entries must be strings.", 0L, null);
            }

            rotations.Add(OutputRotation.Parse(specification, 0L, textStorage));
        }

        return rotations;
    }

    private static List<PageLabelSpec> ParseLabelArray(JobValue value, ConfigurationText textStorage)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job \"setPageLabels\" must be an array of label specifications.", 0L, null);
        }

        List<PageLabelSpec> labels = new();
        foreach (JobValue entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                throw new QpdfParseException("Job \"setPageLabels\" entries must be strings.", 0L, null);
            }

            string? specification = entry.ReadString(textStorage);
            if (specification is null)
            {
                throw new QpdfParseException("Job \"setPageLabels\" entries must be strings.", 0L, null);
            }

            labels.Add(PageLabelSpec.Parse(specification, 0L, textStorage));
        }

        return labels;
    }

    private static List<AttachmentAdd> ParseAddAttachmentArray(JobValue value, ConfigurationText textStorage)
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
        foreach (JobValue entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("Job addAttachment entries must be objects.", 0L, null);
            }

            adds.Add(ParseAddAttachmentEntry(entry, textStorage));
        }

        return adds;
    }

    private static AttachmentAdd ParseAddAttachmentEntry(JobValue entry, ConfigurationText textStorage)
    {
        string? file = null;
        string? key = null;
        string? fileName = null;
        string? creationDate = null;
        string? modDate = null;
        string? mimeType = null;
        string? description = null;
        bool replace = false;
        foreach (JobSetting property in EffectiveProperties(entry))
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

    private static List<string> ParseRemoveAttachmentArray(JobValue value, ConfigurationText textStorage)
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
        foreach (JobValue entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                throw new QpdfParseException("Job removeAttachment entries must be strings.", 0L, null);
            }

            string? key = entry.ReadString(textStorage);
            if (key is null)
            {
                throw new QpdfParseException("Job removeAttachment entries must be strings.", 0L, null);
            }

            removes.Add(key);
        }

        return removes;
    }

    private static List<AttachmentCopy> ParseCopyAttachmentsArray(JobValue value, ConfigurationText textStorage)
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
        foreach (JobValue entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("Job copyAttachmentsFrom entries must be objects.", 0L, null);
            }

            copies.Add(ParseCopyAttachmentsEntry(entry, textStorage));
        }

        return copies;
    }

    private static AttachmentCopy ParseCopyAttachmentsEntry(JobValue entry, ConfigurationText textStorage)
    {
        string? file = null;
        string? prefix = null;
        string? password = null;
        foreach (JobSetting property in EffectiveProperties(entry))
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
                    password = RequireString(property.Value, "copyAttachmentsFrom-password", textStorage);
                    break;
                default:
                    throw new QpdfParseException("Unsupported copyAttachmentsFrom key.", 0L, null);
            }
        }

        if (file is null || file.Length == 0)
        {
            throw new QpdfParseException("Job copyAttachmentsFrom entries require a file.", 0L, null);
        }

        return new AttachmentCopy(file, prefix is null || prefix.Length == 0 ? null : prefix) { PasswordText = password };
    }

    private static string ParseJsonVersion(JobValue value, ConfigurationText textStorage)
    {
        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            && (number == 1 || number == 2))
        {
            return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string? text = value.ReadString(textStorage);
            if (text == "1" || text == "2" || text == "latest")
            {
                return text;
            }
        }

        throw new QpdfParseException("Job \"json\" must be one of: 1, 2, latest.", 0L, null);
    }

    private static List<string> ParseJsonKeyArray(JobValue value, ConfigurationText textStorage)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job \"jsonKey\" must be an array of key names.", 0L, null);
        }

        List<string> keys = new();
        foreach (JobValue entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || entry.ReadString(textStorage) is not string text)
            {
                throw new QpdfParseException("Job \"jsonKey\" entries must be strings.", 0L, null);
            }

            keys.Add(text);
        }

        return keys;
    }

    private static string ParseJsonOutput(JobValue value, ConfigurationText textStorage)
    {
        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            && number == 2)
        {
            return "2";
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string? text = value.ReadString(textStorage);
            if (text == "2" || text == "latest")
            {
                return text;
            }
        }

        throw new QpdfParseException("Job \"jsonOutput\" must be one of: 2, latest.", 0L, null);
    }

    private static List<string> ParseJsonObjectArray(JobValue value, ConfigurationText textStorage)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job \"jsonObject\" must be an array of selectors.", 0L, null);
        }

        List<string> selectors = new();
        foreach (JobValue entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || entry.ReadString(textStorage) is not string text)
            {
                throw new QpdfParseException("Job \"jsonObject\" entries must be strings.", 0L, null);
            }

            selectors.Add(text);
        }

        return selectors;
    }

    private static long RequireOiMin(JobValue element, string role, ConfigurationText textStorage)
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
            text = element.ReadString(textStorage);
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

    private static int RequireJpegQuality(JobValue element, ConfigurationText textStorage)
    {
        long number;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long direct))
        {
            number = direct;
        }
        else if (element.ValueKind == JsonValueKind.String && element.ReadString(textStorage) is string text
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
    private static long RequireIiMinBytes(JobValue element, ConfigurationText textStorage)
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
            text = element.ReadString(textStorage);
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

    private static long RequireContainerSize(JobValue element, string role, ConfigurationText textStorage)
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
            text = element.ReadString(textStorage);
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
    private static long RequireParserMaxErrors(JobValue element, ConfigurationText textStorage)
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
            text = element.ReadString(textStorage);
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
    private static long RequireParserMaxNesting(JobValue element, ConfigurationText textStorage)
    {
        string? text = null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long number))
        {
            text = number.ToString();
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            text = element.ReadString(textStorage);
        }

        if (text is null || text.Length == 0 || text.Length > 10)
        {
            throw new QpdfParseException("Job " + (char)34 + "parserMaxNesting" + (char)34 + " must be a nesting depth within 1..499.", 0L, null);
        }

        long depth = 0L;
        foreach (char digit in text)
        {
            if (digit < (char)48 || digit > (char)57)
            {
                throw new QpdfParseException("Job " + (char)34 + "parserMaxNesting" + (char)34 + " must be a nesting depth within 1..499.", 0L, null);
            }

            depth = checked(depth * 10L + (digit - (char)48));
        }

        if (depth < 1L || depth > QpdfLimits.ParserNestingCeiling)
        {
            throw new QpdfParseException("Job " + (char)34 + "parserMaxNesting" + (char)34 + " must be a nesting depth within 1..499.", 0L, null);
        }

        return depth;
    }
    private static void ValidateKeepFilesOpen(JobValue element, ConfigurationText textStorage)
    {
        if (element.ValueKind == JsonValueKind.String && element.ReadString(textStorage) is string text && (text == "y" || text == "n"))
        {
            return;
        }

        throw new QpdfParseException("Job " + (char)34 + "keepFilesOpen" + (char)34 + " must be \"y\" or \"n\".", 0L, null);
    }

    private static void ValidateKeepFilesOpenThreshold(JobValue element, ConfigurationText textStorage)
    {
        string? text = null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long number))
        {
            if (number < 0L)
            {
                throw new QpdfParseException("Job " + (char)34 + "keepFilesOpenThreshold" + (char)34 + " must be a non-negative file count.", 0L, null);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            text = element.ReadString(textStorage);
        }

        if (text is null || text.Length == 0 || text.Length > 10)
        {
            throw new QpdfParseException("Job " + (char)34 + "keepFilesOpenThreshold" + (char)34 + " must be a non-negative file count.", 0L, null);
        }

        foreach (char digit in text)
        {
            if (digit < (char)48 || digit > (char)57)
            {
                throw new QpdfParseException("Job " + (char)34 + "keepFilesOpenThreshold" + (char)34 + " must be a non-negative file count.", 0L, null);
            }
        }
    }
    private static long RequireMaxStreamFilters(JobValue element, ConfigurationText textStorage)
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
            text = element.ReadString(textStorage);
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
    private static bool RequireNormalizeContent(JobValue element, ConfigurationText textStorage)
    {
        if (element.ValueKind == JsonValueKind.String && element.ReadString(textStorage) is string text)
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

    private static List<OverlaySpec> ParseOverlayArray(JobValue value, string role, ConfigurationText textStorage)
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
        foreach (JobValue entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("Job overlay entries must be objects.", 0L, null);
            }

            specs.Add(ParseOverlayEntry(entry, role, textStorage));
        }

        return specs;
    }

    private static OverlaySpec ParseOverlayEntry(JobValue entry, string role, ConfigurationText textStorage)
    {
        string? file = null;
        string? to = null;
        string? from = null;
        string? repeat = null;
        string? password = null;
        foreach (JobSetting property in EffectiveProperties(entry))
        {
            switch (property.Name)
            {
                case "file":
                    file = RequireString(property.Value, role + ".file", textStorage);
                    break;
                case "to":
                    to = RequireString(property.Value, role + ".to", textStorage);
                    OverlayRanges.ValidateSyntax(to, "--to", 0L);
                    break;
                case "from":
                    from = RequireString(property.Value, role + ".from", textStorage);
                    OverlayRanges.ValidateSyntax(from, "--from", 0L);
                    break;
                case "repeat":
                    repeat = RequireString(property.Value, role + ".repeat", textStorage);
                    OverlayRanges.ValidateSyntax(repeat, "--repeat", 0L);
                    break;
                case "password":
                    password = RequireString(property.Value, role + ".password", textStorage);
                    break;
                default:
                    throw new QpdfParseException("Unsupported overlay key.", 0L, null);
            }
        }

        if (file is null)
        {
            throw new QpdfParseException("Job overlay entries require file.", 0L, null);
        }

        return new OverlaySpec(file, to, from, repeat) { PasswordText = password };
    }

    private static void PrepareOverlayPasswords(List<OverlaySpec> specs, string? mode, bool suppressRecovery,
        ConfigurationText textStorage, ResourceGovernor governor)
    {
        for (int index = 0; index < specs.Count; index++)
        {
            governor.CheckCancellation();
            OverlaySpec spec = specs[index];
            if (spec.PasswordText is not string password) { continue; }
            textStorage.Memory.Reserve(64L);
            specs[index] = spec with { PasswordText = null, Password = ParseSourcePassword(password, mode, suppressRecovery, textStorage) };
        }
    }

    private static List<PdfPageSource> ParsePageSources(List<(string File, string? Range, string? Password)> entries,
        string? primary, string? mode, bool suppressRecovery, ConfigurationText textStorage, ResourceGovernor governor)
    {
        Dictionary<string, (QpdfPassword Password, bool Empty)> passwords = ParserContainers.CreateDictionary<string, (QpdfPassword, bool)>(textStorage.Memory);
        foreach ((string file, string? _, string? text) in entries)
        {
            governor.CheckCancellation();
            if (text is null) { continue; }
            string path = file == "." ? primary ?? file : file;
            QpdfPassword password = ParseSourcePassword(text, mode, suppressRecovery, textStorage);
            if (passwords.TryGetValue(path, out var previous))
            {
                if (!previous.Empty && !previous.Password.HasSameInput(password, governor))
                { throw new QpdfParseException("Different page passwords were specified for the same file.", 0L, null); }
            }
            else { ParserContainers.EnsureEntryCapacity(textStorage.Memory, passwords, 16L); }
            passwords[path] = (password, text.Length == 0);
        }
        List<PdfPageSource> sources = new(entries.Count);
        foreach ((string file, string? range, string? _) in entries)
        {
            governor.CheckCancellation();
            string path = file == "." ? primary ?? file : file;
            QpdfPassword? password = passwords.TryGetValue(path, out var provided) ? provided.Password : null;
            sources.Add(new PdfPageSource(SelectionPlan.CheckedPath(file), PdfPageSelectionOptions.FromRange(range), password));
        }
        return sources;
    }

    private static QpdfPassword ParseSourcePassword(string text, string? mode, bool suppressRecovery, ConfigurationText textStorage)
    {
        textStorage.Memory.Reserve(48L);
        return mode == "hex-bytes" ? QpdfPassword.FromHexText(text, !suppressRecovery) : QpdfPassword.FromText(text, !suppressRecovery);
    }

    private static List<(string File, string? Range, string? Password)> ParsePagesEntries(JobValue pages, ConfigurationText textStorage)
    {
        if (pages.ValueKind != JsonValueKind.Array)
        {
            throw new QpdfParseException("Job \"pages\" must be a non-empty array.", 0L, null);
        }

        if (pages.GetArrayLength() == 0)
        {
            throw new QpdfParseException("Job \"pages\" must hold at least one entry.", 0L, null);
        }

        List<(string File, string? Range, string? Password)> entries = new();
        foreach (JobValue entry in pages.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new QpdfParseException("Job \"pages\" entries must be objects.", 0L, null);
            }

            entries.Add(ParsePagesEntry(entry, textStorage));
        }

        return entries;
    }

    private static (string File, string? Range, string? Password) ParsePagesEntry(JobValue entry, ConfigurationText textStorage)
    {
        string? file = null;
        string? range = null;
        string? password = null;
        foreach (JobSetting property in EffectiveProperties(entry))
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
                    password = RequireString(property.Value, "pages[].password", textStorage);
                    break;
                default:
                    throw new QpdfParseException($"Unsupported pages key '{ArgvParser.ErrorPreview(property.Name)}' in the narrow selection profile.", 0L, null);
            }
        }

        if (file is null)
        {
            throw new QpdfParseException("Job pages entries require \"file\".", 0L, null);
        }

        return (file, range, password);
    }

    private static string RequireString(JobValue element, string role, ConfigurationText textStorage)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new QpdfParseException($"Job \"{role}\" must be a string.", 0L, null);
        }

        string? value = element.ReadString(textStorage);
        if (value is null)
        {
            throw new QpdfParseException($"Job \"{role}\" must be a string.", 0L, null);
        }

        return value;
    }

    private static string RequireObjectStreams(JobValue element, ConfigurationText textStorage)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new QpdfParseException("Job \"objectStreams\" must be disable, preserve, or generate.", 0L, null);
        }

        string? value = element.ReadString(textStorage);
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

    private static bool RequireBareFlag(JobValue element, string role, ConfigurationText textStorage)
    {
        if (element.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.String && element.ReadString(textStorage) == string.Empty)
        {
            return true;
        }

        throw new QpdfParseException("Job \"" + role + "\" must be an empty string or a boolean.", 0L, null);
    }

}
