namespace Lokad.Qpdf.Commands;

/// <summary>
/// Argv parser for writing, selection, PDF JSON and shared option-block syntax.
/// Inspection and layered job files use the job loader. Bare input/output pairs use
/// the same whole-file plan as job JSON, with an optional -- output separator.
/// --suppress-recovery selects strict opening; otherwise damaged inputs repair
/// within bounds. Unsupported arguments fail explicitly.
/// Unknown `--*` tokens map to the documented crypto set (rejected as crypto)
/// or to unsupported-option errors. Writer controls cover versions, IDs,
/// streams, object streams, QDF and linearization.
/// </summary>
internal static class ArgvParser
{
    internal static bool IsCryptoOption(ReadOnlySpan<char> name)
        => name is "password" or "password-file" or "password-mode" or "password-is-hex-key"
            or "suppress-password-recovery" or "encryption-file-password" or "user-password"
            or "owner-password" or "bits" or "allow-weak-crypto" or "allow-insecure" or "use-aes"
            or "force-V4" or "force-R5" or "cleartext-metadata" or "static-aes-iv"
            or "accessibility" or "annotate" or "assemble" or "extract" or "form" or "modify-other"
            or "modify" or "print" or "show-crypto" or "show-encryption-key"
            or "remove-restrictions" or "encrypt"
            or "decrypt" or "copy-encryption";

    internal static JobPlan Parse(IReadOnlyList<string> argv, Runtime.ResourceGovernor governor)
    {
        if (argv is null)
        {
            throw new ArgumentNullException(nameof(argv));
        }

        if (argv.Count == 0)
        {
            throw new QpdfParseException("No command arguments were supplied.", 0L, null);
        }

        using ConfigurationText textStorage = ConfigurationText.Create(governor);
        CollationPreparation collationStorage = CollationPreparation.Create(governor);
        textStorage.Collation = collationStorage;
        try
        {
            argv = ConfigurationText.Wrap(argv, textStorage);
            RejectCryptoOptions(argv, governor);
            PagesArgumentBlock.Validate(argv, governor);
            using ArgumentFiltering filtering = ArgumentFiltering.Create(governor);
            argv = StripGlobalBlocks(argv, filtering);
            GlobalOptions globals = ExtractGlobalOptions(ref argv, filtering, governor);
            JobPlan plan = ParseCore(argv, globals) with
            {
                CompressionState = globals.CompressionState,
                MaxStreamFilters = globals.MaxStreamFilters,
                ParserMaxNesting = globals.ParserMaxNesting,
                MaxContainerSize = globals.MaxContainerSize,
                MaxContainerSizeDamaged = globals.MaxContainerSizeDamaged,
                ParserMaxErrors = globals.ParserMaxErrors,
                NoDefaultLimits = globals.NoDefaultLimits,
                CollationStorage = collationStorage,
            };
            textStorage.RetainForExecution();
            return plan;
        }
        catch { collationStorage.Dispose(); throw; }
    }

    internal static void RejectCryptoOptions(IReadOnlyList<string> argv, Runtime.ResourceGovernor governor)
        => RejectCryptoOptions(argv, governor, false);

    internal static void RejectCryptoOptions(IReadOnlyList<string> argv, Runtime.ResourceGovernor governor, bool supportedSecurityOptions)
    {
        for (int index = 0; index < argv.Count; index++)
        {
            governor.CheckCancellation();
            governor.NoteOperation();
            string token = argv[index];
            if (supportedSecurityOptions && token == "--encrypt")
            {
                index = EncryptionArgumentBlock.FindEnd(argv, index, governor);
                continue;
            }
            if (token is not null && token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2
                && IsCryptoOption(OptionName(token)) && !(supportedSecurityOptions && OptionName(token) is "password" or "password-file" or "password-mode" or "suppress-password-recovery" or "decrypt" or "copy-encryption" or "encryption-file-password"))
            {
                throw new QpdfCryptoException($"--{OptionName(token)}");
            }
        }
    }

    private static JobPlan ParseCore(IReadOnlyList<string> argv, GlobalOptions globals)
    {
        QpdfRecoveryMode recovery = globals.Recovery;
        int pagesAt = FindFlag(argv, "--pages");
        int firstOverlayAt = FirstOverlayBlock(argv, 0, argv.Count);
        int jsonAt = FindJson(argv);
        int jsonOutputAt = FindJsonOutput(argv);
        int jsonEntryAt = jsonAt >= 0 && jsonOutputAt >= 0 ? (jsonAt < jsonOutputAt ? jsonAt : jsonOutputAt) : (jsonAt >= 0 ? jsonAt : jsonOutputAt);
        int jsonInputAt = FindFlag(argv, "--json-input");
        int updateFromJsonAt = FindUpdateFromJson(argv);
        int jsonImportAt = jsonInputAt >= 0 && updateFromJsonAt >= 0 ? (jsonInputAt < updateFromJsonAt ? jsonInputAt : updateFromJsonAt) : (jsonInputAt >= 0 ? jsonInputAt : updateFromJsonAt);
        if (globals.WithImages)
        {
            throw new QpdfParseException("Option --with-images requires --show-pages.", 0, null);
        }

        if (globals.TestJsonSchema && jsonEntryAt < 0 && jsonOutputAt < 0)
        {
            throw new QpdfParseException("Option --test-json-schema requires --json.", 0, null);
        }

        if (globals.Collate.Count > 0 && pagesAt < 0 && (jsonEntryAt >= 0 || jsonImportAt >= 0))
        {
            throw new QpdfParseException("Option --collate requires --pages.", 0, null);
        }

        if (globals.Rotations.Count > 0 && pagesAt < 0 && (jsonEntryAt >= 0 || jsonImportAt >= 0))
        {
            throw new QpdfParseException("Option --rotate requires --pages.", 0, null);
        }

        if (jsonEntryAt >= 0)
        {
            if (pagesAt >= 0)
            {
                throw new QpdfParseException("Option --json cannot be combined with --check or --pages.", jsonEntryAt, null);
            }
            if (HasAttachmentEdit(argv, 0, argv.Count))
            {
                throw new QpdfParseException("Option --json cannot be combined with attachment editing.", jsonEntryAt, null);
            }

            if (firstOverlayAt >= 0)
            {
                throw new QpdfParseException("Option --json cannot be combined with --overlay or --underlay.", jsonEntryAt, null);
            }

            if (globals.FlattenRotation)
            {
                throw new QpdfParseException("Option --json cannot be combined with --flatten-rotation.", jsonEntryAt, null);
            }

            if (globals.FlattenAnnotations is not null)
            {
                throw new QpdfParseException("Option --json cannot be combined with --flatten-annotations.", jsonEntryAt, null);
            }

            if (globals.GenerateAppearances)
            {
                throw new QpdfParseException("Option --json cannot be combined with --generate-appearances.", jsonEntryAt, null);
            }
            if (globals.Qdf)
            {
                throw new QpdfParseException("Option --json cannot be combined with --qdf.", jsonEntryAt, null);
            }
            if (globals.Linearize)
            {
                throw new QpdfParseException("Option --json cannot be combined with --linearize.", jsonEntryAt, null);
            }

            if (jsonImportAt >= 0)
            {
                throw new QpdfParseException("Option --json cannot be combined with JSON import.", jsonEntryAt, null);
            }

            if (globals.SplitPages.HasValue || globals.ReplaceInput)
            {
                throw new QpdfParseException("Options --split-pages and --replace-input need a --pages selection.", jsonEntryAt, null);
            }

            return ParseJson(argv, jsonEntryAt, recovery, globals);
        }

        if (jsonImportAt >= 0)
        {
            if (pagesAt >= 0)
            {
                throw new QpdfParseException("JSON import cannot be combined with --check or --pages.", jsonImportAt, null);
            }
            if (HasAttachmentEdit(argv, 0, argv.Count))
            {
                throw new QpdfParseException("JSON import cannot be combined with attachment editing.", jsonImportAt, null);
            }

            if (firstOverlayAt >= 0)
            {
                throw new QpdfParseException("JSON import cannot be combined with --overlay or --underlay.", jsonImportAt, null);
            }

            if (globals.FlattenRotation)
            {
                throw new QpdfParseException("JSON import cannot be combined with --flatten-rotation.", jsonImportAt, null);
            }

            if (globals.FlattenAnnotations is not null)
            {
                throw new QpdfParseException("JSON import cannot be combined with --flatten-annotations.", jsonImportAt, null);
            }


            if (globals.GenerateAppearances)
            {
                throw new QpdfParseException("JSON import cannot be combined with --generate-appearances.", jsonImportAt, null);
            }
            if (globals.Qdf)
            {
                throw new QpdfParseException("JSON import cannot be combined with --qdf.", jsonImportAt, null);
            }
            if (globals.Linearize)
            {
                throw new QpdfParseException("JSON import cannot be combined with --linearize.", jsonImportAt, null);
            }
            if (jsonEntryAt >= 0)
            {
                throw new QpdfParseException("JSON import cannot be combined with JSON inspection.", jsonImportAt, null);
            }

            if (globals.SplitPages.HasValue || globals.ReplaceInput)
            {
                throw new QpdfParseException("Options --split-pages and --replace-input need a --pages selection.", jsonImportAt, null);
            }

            return ParseJsonImport(argv, jsonImportAt, jsonInputAt, updateFromJsonAt, recovery, globals);
        }

        int strayJson = FindStrayJsonOption(argv);
        if (strayJson >= 0)
        {
            throw new QpdfParseException("JSON options require --json or --json-output.", strayJson, null);
        }

        if (pagesAt < 0 && !globals.ReplaceInput && !HasAttachmentEdit(argv, 0, argv.Count) && firstOverlayAt < 0)
        {
            int terminator = FindFlag(argv, "--");
            int inputEnd = terminator >= 0 ? terminator : Math.Min(1, argv.Count);
            bool empty = IsEmptyDocumentInput(argv, inputEnd);
            string wholeInput = empty ? JobPlan.EmptyDocumentInput : ParseInputArea(argv, inputEnd, "whole-file rewriting", null);
            string wholeOutput = ParseOutputArea(argv, terminator >= 0 ? terminator : 0, null);
            return NewWholeFilePlan(wholeInput, wholeOutput, recovery, globals,
                AttachmentEdits.Empty, new List<OverlaySpec>(), new List<OverlaySpec>());
        }

        string input;
        List<PageGroup> groups = new();
        OperationBlocks trailing = new(new(), new(), new(), new(), new());
        int cursor;
        bool emptyInput;
        if (pagesAt >= 0)
        {
            input = ParseInputArea(argv, pagesAt, "--pages", trailing);
            emptyInput = input == JobPlan.EmptyDocumentInput;
            if (emptyInput && globals.ReplaceInput)
            {
                throw new QpdfParseException("No input file to replace.", 0, null);
            }
            int blockAt = FirstAttachmentEdit(argv, pagesAt + 1, argv.Count);
            int overlayAt = FirstOverlayBlock(argv, pagesAt + 1, argv.Count);
            if (globals.ReplaceInput)
            {
                int areaEnd = FindTerminator(argv, pagesAt);
                groups = ParsePagesArea(argv, pagesAt, areaEnd);
                cursor = areaEnd + 1;
            }
            else
            {
                int termAt = FindTerminator(argv, pagesAt);
                int areaEnd = termAt;
                if (blockAt >= 0 && blockAt < areaEnd)
                {
                    areaEnd = blockAt;
                }
                if (overlayAt >= 0 && overlayAt < areaEnd)
                {
                    areaEnd = overlayAt;
                }
                groups = ParsePagesArea(argv, pagesAt, areaEnd);
                if (emptyInput)
                {
                    foreach (PageGroup group in groups)
                    {
                        if (group.File == ".")
                        {
                            throw new QpdfParseException("Cannot select from \u0027.\u0027 with --empty: the empty input has no pages.", pagesAt, null);
                        }
                    }

                    input = groups[0].File;
                }
                cursor = areaEnd;
            }
        }
        else
        {
            if (globals.ReplaceInput)
            {
                throw new QpdfParseException("No operation: the narrow profile requires --pages with one file and range, --check, --json, or attachment editing.", 0L, null);
            }

            cursor = 0;
            while (cursor < argv.Count && TryParseOperationBlock(argv, ref cursor, trailing)) { }
            if (cursor >= argv.Count)
            {
                throw new QpdfParseException("A positional input file is required for attachment editing.", cursor, null);
            }
            input = argv[cursor];
            CheckInputToken(input, cursor, "attachment editing", true);
            cursor++;
            emptyInput = input == JobPlan.EmptyDocumentInput;
        }

        ParseOperationBlocks(argv, ref cursor, trailing);
        List<OverlaySpec> overlays = trailing.Overlays;
        List<OverlaySpec> underlays = trailing.Underlays;
        if (globals.ReplaceInput)
        {
            if (cursor != argv.Count)
            {
                throw new QpdfParseException("No output file may be given with --replace-input.", pagesAt, null);
            }

            return ParseReplace(input, groups, recovery, globals) with
            {
                AttachmentsToAdd = trailing.Adds,
                AttachmentsToRemove = trailing.Removes,
                AttachmentsToCopy = trailing.Copies,
                Overlays = overlays,
                Underlays = underlays,
            };
        }

        if (cursor >= argv.Count)
        {
            throw new QpdfParseException("An output file is required after --.", cursor, null);
        }

        string output = ParseOutputArea(argv, argv[cursor] == "--" ? cursor : cursor - 1, trailing);
        AttachmentEdits edits = new(trailing.Adds, trailing.Removes, trailing.Copies);
        if (emptyInput && (overlays.Count > 0 || underlays.Count > 0))
        {
            throw new QpdfParseException("Options --overlay and --underlay cannot be combined with --empty.", pagesAt, null);
        }

        if (groups.Count == 0)
        {
            return NewWholeFilePlan(input, output, recovery, globals, edits, overlays, underlays);
        }

        if (emptyInput || groups.Count > 1 || globals.Collate.Count > 0
            || (groups.Count == 1 && (!SelectionPlan.IsPlainPageNumber(groups[0].Range)
                || (groups[0].File != "." && groups[0].File != input))))
        {
            return ParseMerge(input, output, groups, recovery, globals, emptyInput) with
            {
                AttachmentsToAdd = edits.Adds,
                AttachmentsToRemove = edits.Removes,
                AttachmentsToCopy = edits.Copies,
                Overlays = overlays,
                Underlays = underlays,
            };
        }

        int page = SelectionPlan.ParsePageNumber(groups[0].Range);
        return new SelectionPlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output), page, recovery) with
        {
            WarningExitZero = globals.WarningExitZero,
            Verbose = globals.Verbose,
            Progress = globals.Progress,
            ReportMemoryUsage = globals.ReportMemoryUsage,
            IgnoreXrefStreams = globals.IgnoreXrefStreams,
            NoWarn = globals.NoWarn,
            Rotations = globals.Rotations,
            SplitPages = globals.SplitPages,
            MinVersion = globals.MinVersion,
            ForceVersion = globals.ForceVersion,
            StaticId = globals.StaticId,
            NewlineBeforeEndstream = globals.NewlineBeforeEndstream,
            ObjectStreams = globals.ObjectStreams,
            PreserveUnreferenced = globals.PreserveUnreferenced,
            StreamData = globals.StreamData,
            CompressStreams = globals.CompressStreams,
            DecodeLevel = globals.DecodeLevel,
            MaxStreamFilters = globals.MaxStreamFilters,
            ParserMaxNesting = globals.ParserMaxNesting,
            NoDefaultLimits = globals.NoDefaultLimits,
            MaxContainerSize = globals.MaxContainerSize,
            MaxContainerSizeDamaged = globals.MaxContainerSizeDamaged,
            ParserMaxErrors = globals.ParserMaxErrors,
            RecompressFlate = globals.RecompressFlate,
            CompressionLevel = globals.CompressionLevel,
            RemoveInfo = globals.RemoveInfo,
            RemoveAcroform = globals.RemoveAcroform,
            RemoveStructure = globals.RemoveStructure,
            RemoveMetadata = globals.RemoveMetadata,
            CoalesceContents = globals.CoalesceContents,
            NormalizeContent = globals.NormalizeContent,
            RemoveUnreferencedResources = globals.RemoveUnreferencedResources,
            ExternalizeInlineImages = globals.ExternalizeInlineImages,
            IiMinBytes = globals.IiMinBytes,
            FlattenRotation = globals.FlattenRotation,
            FlattenAnnotations = globals.FlattenAnnotations,
            GenerateAppearances = globals.GenerateAppearances,
            OptimizeImages = globals.OptimizeImages,
            OiMinWidth = globals.OiMinWidth,
            OiMinHeight = globals.OiMinHeight,
            OiMinArea = globals.OiMinArea,
            KeepInlineImages = globals.KeepInlineImages,
            JpegQuality = globals.JpegQuality,
            Qdf = globals.Qdf,
            Linearize = globals.Linearize,
            LinearizePass1 = ParseLinearizePass1(globals),
            NoOriginalObjectIds = globals.NoOriginalObjectIds,
            AttachmentsToAdd = edits.Adds,
            AttachmentsToRemove = edits.Removes,
            AttachmentsToCopy = edits.Copies,
                Overlays = overlays,
                Underlays = underlays,
        };
    }

    internal static ReadOnlySpan<char> OptionName(string token)
    {
        int equals = token.IndexOf('=');
        return equals < 0 ? token.AsSpan(2) : token.AsSpan(2, equals - 2);
    }

    // Bound caller text before interpolation, including terminal failures that
    // remain available after ordinary diagnostic storage has been exhausted.
    internal static string ErrorPreview(ReadOnlySpan<char> value)
    {
        const int maximum = 128;
        if (value.Length <= maximum) { return value.ToString(); }
        int length = char.IsHighSurrogate(value[maximum - 1]) ? maximum - 1 : maximum;
        return string.Concat(value[..length], "...");
    }

    internal static void CheckOptionToken(string token, long index)
    {
        ReadOnlySpan<char> name = OptionName(token);
        if (IsCryptoOption(name))
        {
            throw new QpdfCryptoException($"--{name}");
        }

        throw new QpdfParseException($"Unsupported option '--{ErrorPreview(name)}' in the narrow selection profile.", index, null);
    }

    private static int FindFlag(IReadOnlyList<string> argv, string flag)
    {
        int found = -1;
        for (int i = 0; i < argv.Count; i++)
        {
            if (argv[i] == flag)
            {
                if (found >= 0)
                {
                    throw new QpdfParseException($"Duplicate {flag} is outside the narrow profile.", i, null);
                }

                found = i;
            }
        }

        return found;
    }

    private sealed class GlobalOptions
    {
        internal required QpdfRecoveryMode Recovery { get; init; }
        internal required bool WarningExitZero { get; init; }
        internal required bool NoWarn { get; init; }
        internal required int? SplitPages { get; init; }
        internal required bool ReplaceInput { get; init; }
        internal required string? MinVersion { get; init; }
        internal required string? ForceVersion { get; init; }
        internal required bool StaticId { get; init; }
        internal required bool NewlineBeforeEndstream { get; init; }
        internal required string? ObjectStreams { get; init; }
        internal required bool PreserveUnreferenced { get; init; }
        internal required string? StreamData { get; init; }
        internal required string? CompressStreams { get; init; }
        internal required string? DecodeLevel { get; init; }
        internal required bool RecompressFlate { get; init; }
        internal required string? CompressionLevel { get; init; }
        internal required bool RemoveInfo { get; init; }
        internal required bool RemoveMetadata { get; init; }
        internal required bool CoalesceContents { get; init; }
        internal required bool NormalizeContent { get; init; }
        internal required string? RemoveUnreferencedResources { get; init; }
        internal required bool ExternalizeInlineImages { get; init; }
        internal required long IiMinBytes { get; init; }
        internal required bool FlattenRotation { get; init; }
        internal required string? FlattenAnnotations { get; init; }
        internal required bool GenerateAppearances { get; init; }
        internal required bool OptimizeImages { get; init; }
        internal required long OiMinWidth { get; init; }
        internal required long OiMinHeight { get; init; }
        internal required long OiMinArea { get; init; }
        internal required bool KeepInlineImages { get; init; }
        internal required int? JpegQuality { get; init; }
        internal required bool Qdf { get; init; }
        internal required bool NoOriginalObjectIds { get; init; }
        internal required bool Linearize { get; init; }
        internal required string? LinearizePass1 { get; init; }
        internal required long? MaxStreamFilters { get; init; }
        internal required long? ParserMaxNesting { get; init; }
        internal required bool WithImages { get; init; }
        internal required bool IgnoreXrefStreams { get; init; }
        internal required long? MaxContainerSize { get; init; }
        internal required long? MaxContainerSizeDamaged { get; init; }
        internal required bool ReportMemoryUsage { get; init; }
        internal required bool TestJsonSchema { get; init; }
        internal required bool Verbose { get; init; }
        internal required bool Progress { get; init; }
        internal required bool RemoveAcroform { get; init; }
        internal required bool RemoveStructure { get; init; }
        internal required List<int> Collate { get; init; }
        internal required List<OutputRotation> Rotations { get; init; }
        internal required bool NoDefaultLimits { get; init; }
        internal required long? ParserMaxErrors { get; init; }

        internal byte CompressionState { get; init; }
        internal required CollationPreparation CollationStorage { get; init; }
    }

    private static bool IsGlobalOptionToken(string token)
    {
        if (token == "--no-default-limits")
        {
            return true;
        }

        return token.StartsWith("--parser-max-container-size=", StringComparison.Ordinal)
            || token == "--parser-max-container-size"
            || token.StartsWith("--parser-max-container-size-damaged=", StringComparison.Ordinal)
            || token == "--parser-max-container-size-damaged"
            || token.StartsWith("--parser-max-errors=", StringComparison.Ordinal)
            || token == "--parser-max-errors"
            || token.StartsWith("--parser-max-nesting=", StringComparison.Ordinal)
            || token == "--parser-max-nesting"
            || token.StartsWith("--max-stream-filters=", StringComparison.Ordinal)
            || token == "--max-stream-filters";
    }

    private static bool IsScopedBlockStart(string token)
        => token is "--pages" or "--overlay" or "--underlay" or "--add-attachment"
            or "--copy-attachments-from" or "--set-page-labels";

    private static int ScopedBlockEnd(IReadOnlyList<string> argv, int start, ArgumentFiltering filtering)
    {
        for (int index = start + 1; index < argv.Count; index++)
        {
            filtering.Visit();
            if (argv[index] == "--") { return index + 1; }
        }
        // Preserve malformed blocks for their own parser, including its
        // unsupported-option versus missing-terminator error ordering.
        return argv.Count;
    }

    private static IReadOnlyList<string> StripGlobalBlocks(IReadOnlyList<string> argv, ArgumentFiltering filtering)
    {
        List<int>? remove = null;
        int i = 0;
        while (i < argv.Count)
        {
            filtering.Visit();
            if (IsScopedBlockStart(argv[i]))
            {
                i = ScopedBlockEnd(argv, i, filtering);
                continue;
            }
            if (argv[i] != "--global")
            {
                i += 1;
                continue;
            }

            int openAt = i;
            i += 1;
            bool closed = false;
            while (i < argv.Count)
            {
                filtering.Visit();
                string token = argv[i];
                if (token == "--")
                {
                    closed = true;
                    break;
                }

                if (token is not null && token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2 && !IsGlobalOptionToken(token))
                {
                    throw new QpdfParseException("Only global options may appear inside --global.", i, null);
                }

                i += 1;
            }

            if (!closed)
            {
                throw new QpdfParseException("Missing -- terminator after --global.", openAt, null);
            }

            remove ??= filtering.CreateMarkers();
            filtering.Add(remove, openAt); filtering.Add(remove, i);
            i += 1;
        }

        if (remove is null)
        {
            return argv;
        }

        List<string> kept = filtering.CreateStripped();
        int next = 0;
        for (int j = 0; j < argv.Count; j++)
        {
            filtering.Visit();
            // Markers were appended in source order. Walk them once instead
            // of scanning the entire marker vector for every argument.
            if (next < remove.Count && remove[next] == j) { next++; continue; }
            filtering.Add(kept, argv[j]);
        }

        return ConfigurationText.Preserve(argv, kept);
    }

    private static GlobalOptions ExtractGlobalOptions(ref IReadOnlyList<string> argv, ArgumentFiltering filtering,
        Runtime.ResourceGovernor governor)
    {
        List<string> kept = filtering.CreateKept();
        QpdfRecoveryMode recovery = QpdfRecoveryMode.BoundedRepair;
        bool warningExitZero = false;
        bool noWarn = false;
        bool seenSuppress = false;
        bool seenRemoveAcroform = false;
        bool seenRemoveStructure = false;
        bool seenWarningExitZero = false;
        bool seenNoWarn = false;
        bool seenSplit = false;
        bool seenReplace = false;
        int? splitPages = null;
        bool replaceInput = false;
        List<int> collate = NewCollationVector(argv);
        List<OutputRotation> mainRotations = new();
        bool seenMin = false;
        bool seenForce = false;
        bool seenDeterministic = false;
        bool seenStatic = false;
        bool seenNewline = false;
        string? minVersion = null;
        string? forceVersion = null;
        bool seenObjectStreams = false;
        string? objectStreams = null;
        bool seenPreserveUnreferenced = false;
        bool preserveUnreferenced = false;
        CompressionSettings compressionSettings = CompressionSettings.NativeDefault;
        string? streamData = null;
        string? compressStreams = null;
        string? decodeLevel = null;
        bool recompressFlate = false;
        string? compressionLevel = null;
        bool removeAcroform = false;
        bool removeStructure = false;
        bool seenRemoveInfo = false;
        bool removeInfo = false;
        bool seenRemoveMetadata = false;
        bool removeMetadata = false;
        bool seenCoalesceContents = false;
        bool coalesceContents = false;
        bool seenNormalizeContent = false;
        bool normalizeContent = false;
        bool seenRemoveUnreferencedResources = false;
        string? removeUnreferencedResources = null;
        bool seenPreserveUnreferencedResources = false;
        bool seenExternalizeInlineImages = false;
        bool externalizeInlineImages = false;
        bool seenIiMinBytes = false;
        long iiMinBytes = 1024L;
        bool seenFlattenRotation = false;
        bool flattenRotation = false;
        bool seenFlattenAnnotations = false;
        string? flattenAnnotations = null;
        bool seenGenerateAppearances = false;
        bool generateAppearances = false;
        bool seenOptimizeImages = false;
        bool optimizeImages = false;
        bool seenOiMinWidth = false;
        long oiMinWidth = 128L;
        bool seenOiMinHeight = false;
        long oiMinHeight = 128L;
        bool seenOiMinArea = false;
        long oiMinArea = 16384L;
        bool seenKeepInlineImages = false;
        bool keepInlineImages = false;
        bool seenJpegQuality = false;
        int? jpegQuality = null;
        bool seenQdf = false;
        bool qdf = false;
        bool seenNoOriginalObjectIds = false;
        bool noOriginalObjectIds = false;
        bool seenLinearize = false;
        bool linearize = false;
        bool seenLinearizePass1 = false;
        string? linearizePass1 = null;
        bool seenNoDefaultLimits = false;
        bool noDefaultLimits = false;
        long? maxStreamFilters = null;
        bool seenParserMaxNesting = false;
        long? parserMaxNesting = null;
        bool seenMaxContainerSize = false;
        long? maxContainerSize = null;
        bool seenMaxContainerSizeDamaged = false;
        long? maxContainerSizeDamaged = null;
        bool seenParserMaxErrors = false;
        long? parserMaxErrors = null;
        bool seenWithImages = false;
        bool seenIgnoreXrefStreams = false;
        bool seenReportMemoryUsage = false;
        bool reportMemoryUsage = false;
        bool seenTestJsonSchema = false;
        bool seenVerbose = false;
        bool verbose = false;
        bool seenProgress = false;
        bool progress = false;
        bool seenKeepFilesOpen = false;
        bool seenKeepFilesOpenThreshold = false;
        bool testJsonSchema = false;
        bool ignoreXrefStreams = false;
        bool withImages = false;
        bool staticId = false;
        bool newlineBeforeEndstream = false;
        for (int i = 0; i < argv.Count; i++)
        {
            filtering.Visit();
            if (IsScopedBlockStart(argv[i]))
            {
                int end = ScopedBlockEnd(argv, i, filtering);
                for (int index = i; index < end; index++) { filtering.Add(kept, argv[index]); }
                i = end - 1;
                continue;
            }
            if (argv[i] == "--suppress-recovery")
            {
                if (seenSuppress)
                {
                    throw new QpdfParseException("Duplicate --suppress-recovery is outside the narrow profile.", i, null);
                }

                seenSuppress = true;
                recovery = QpdfRecoveryMode.Strict;
                continue;
            }

            if (argv[i] == "--remove-acroform")
            {
                if (seenRemoveAcroform)
                {
                    throw new QpdfParseException("Duplicate --remove-acroform is outside the narrow profile.", i, null);
                }

                seenRemoveAcroform = true;
                removeAcroform = true;
                continue;
            }

            if (argv[i] == "--remove-structure")
            {
                if (seenRemoveStructure)
                {
                    throw new QpdfParseException("Duplicate --remove-structure is outside the narrow profile.", i, null);
                }

                seenRemoveStructure = true;
                removeStructure = true;
                continue;
            }

            if (argv[i] == "--warning-exit-0")
            {
                if (seenWarningExitZero)
                {
                    throw new QpdfParseException("Duplicate --warning-exit-0 is outside the narrow profile.", i, null);
                }

                seenWarningExitZero = true;
                warningExitZero = true;
                continue;
            }

            if (argv[i] == "--no-warn")
            {
                if (seenNoWarn)
                {
                    throw new QpdfParseException("Duplicate --no-warn is outside the narrow profile.", i, null);
                }

                seenNoWarn = true;
                noWarn = true;
                continue;
            }

            if (argv[i] == "--collate" || argv[i].StartsWith("--collate=", StringComparison.Ordinal))
            {
                ReadOnlySpan<char> collateValue = default;
                if (argv[i].StartsWith("--collate=", StringComparison.Ordinal))
                {
                    collateValue = argv[i].AsSpan("--collate=".Length);
                }

                CollationPreparation owner = ConfigurationText.OwnerOf(argv)?.Collation
                    ?? throw new InvalidOperationException("Missing collation owner.");
                List<int> parsed = ParseCollateValue(collateValue, i, owner);
                owner.Append(collate, parsed);
                continue;
            }

            if (argv[i] == "--rotate" || argv[i].StartsWith("--rotate=", StringComparison.Ordinal))
            {
                string? specification = null;
                if (argv[i].StartsWith("--rotate=", StringComparison.Ordinal))
                {
                    specification = ConfigurationText.Slice(argv, argv[i], "--rotate=".Length);
                    if (specification.Length == 0)
                    {
                        throw new QpdfParseException("Empty parameter for --rotate.", i, null);
                    }
                }
                else if (i + 1 < argv.Count)
                {
                    string next = argv[i + 1];
                    if (next is null || next == "--" || (next.StartsWith("--", StringComparison.Ordinal) && next.Length > 2))
                    {
                        throw new QpdfParseException("Missing parameter for --rotate.", i, null);
                    }

                    specification = next;
                    i += 1;
                }
                else
                {
                    throw new QpdfParseException("Missing parameter for --rotate.", i, null);
                }

                mainRotations.Add(OutputRotation.Parse(specification, i, ConfigurationText.OwnerOf(argv)));
                continue;
            }

            if (argv[i] == "--split-pages" || argv[i].StartsWith("--split-pages=", StringComparison.Ordinal))
            {
                if (seenSplit)
                {
                    throw new QpdfParseException("Duplicate --split-pages is outside the narrow profile.", i, null);
                }

                seenSplit = true;
                splitPages = ParseSplitPages(argv, ref i);
                continue;
            }

            if (argv[i] == "--replace-input")
            {
                if (seenReplace)
                {
                    throw new QpdfParseException("Duplicate --replace-input is outside the narrow profile.", i, null);
                }

                seenReplace = true;
                replaceInput = true;
                continue;
            }

            if (argv[i] == "--deterministic-id")
            {
                if (seenDeterministic)
                {
                    throw new QpdfParseException("Duplicate --deterministic-id is outside the narrow profile.", i, null);
                }

                seenDeterministic = true;
                continue;
            }

            if (argv[i].StartsWith("--deterministic-id=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --deterministic-id takes no value.", i, null);
            }

            if (argv[i] == "--static-id")
            {
                if (seenStatic)
                {
                    throw new QpdfParseException("Duplicate --static-id is outside the narrow profile.", i, null);
                }

                seenStatic = true;
                staticId = true;
                continue;
            }

            if (argv[i].StartsWith("--static-id=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --static-id takes no value.", i, null);
            }

            if (argv[i] == "--newline-before-endstream")
            {
                if (seenNewline)
                {
                    throw new QpdfParseException("Duplicate --newline-before-endstream is outside the narrow profile.", i, null);
                }

                seenNewline = true;
                newlineBeforeEndstream = true;
                continue;
            }

            if (argv[i].StartsWith("--newline-before-endstream=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --newline-before-endstream takes no value.", i, null);
            }

            if (argv[i] == "--min-version" || argv[i].StartsWith("--min-version=", StringComparison.Ordinal))
            {
                if (seenMin)
                {
                    throw new QpdfParseException("Duplicate --min-version is outside the narrow profile.", i, null);
                }

                seenMin = true;
                minVersion = ParseRequiredVersion(argv, ref i, "--min-version");
                continue;
            }

            if (argv[i] == "--force-version" || argv[i].StartsWith("--force-version=", StringComparison.Ordinal))
            {
                if (seenForce)
                {
                    throw new QpdfParseException("Duplicate --force-version is outside the narrow profile.", i, null);
                }

                seenForce = true;
                forceVersion = ParseRequiredVersion(argv, ref i, "--force-version");
                continue;
            }

            if (argv[i] == "--preserve-unreferenced")
            {
                if (seenPreserveUnreferenced)
                {
                    throw new QpdfParseException("Duplicate --preserve-unreferenced is outside the narrow profile.", i, null);
                }

                seenPreserveUnreferenced = true;
                preserveUnreferenced = true;
                continue;
            }

            if (argv[i].StartsWith("--preserve-unreferenced=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --preserve-unreferenced takes no value.", i, null);
            }

            if (argv[i] == "--stream-data" || argv[i].StartsWith("--stream-data=", StringComparison.Ordinal))
            {
                streamData = ParseStreamChoice(argv, ref i, "--stream-data", new string[] { "compress", "preserve", "uncompress" });
                compressionSettings = compressionSettings.WithStreamData(streamData);
                continue;
            }

            if (argv[i] == "--compress-streams" || argv[i].StartsWith("--compress-streams=", StringComparison.Ordinal))
            {
                compressStreams = ParseOptionalChoice(argv, ref i, "--compress-streams", new string[] { "y", "n" }, "y");
                compressionSettings = compressionSettings.WithCompressStreams(compressStreams);
                continue;
            }

            if (argv[i] == "--decode-level" || argv[i].StartsWith("--decode-level=", StringComparison.Ordinal))
            {
                decodeLevel = ParseStreamChoice(argv, ref i, "--decode-level", new string[] { "none", "generalized", "specialized", "all" });
                compressionSettings = compressionSettings.WithDecodeLevel(decodeLevel);
                continue;
            }

            if (argv[i] == "--recompress-flate")
            {
                recompressFlate = true;
                continue;
            }

            if (argv[i].StartsWith("--recompress-flate=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --recompress-flate takes no value.", i, null);
            }

            if (argv[i] == "--compression-level" || argv[i].StartsWith("--compression-level=", StringComparison.Ordinal))
            {
                compressionLevel = ParseRequiredChoiceValue(argv, ref i, "--compression-level");
                CompressionRequest.ParseLevel(compressionLevel, "--compression-level");
                continue;
            }

            if (argv[i] == "--remove-info")
            {
                if (seenRemoveInfo)
                {
                    throw new QpdfParseException("Duplicate --remove-info is outside the narrow profile.", i, null);
                }

                seenRemoveInfo = true;
                removeInfo = true;
                continue;
            }

            if (argv[i].StartsWith("--remove-info=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --remove-info takes no value.", i, null);
            }

            if (argv[i] == "--remove-metadata")
            {
                if (seenRemoveMetadata)
                {
                    throw new QpdfParseException("Duplicate --remove-metadata is outside the narrow profile.", i, null);
                }

                seenRemoveMetadata = true;
                removeMetadata = true;
                continue;
            }

            if (argv[i].StartsWith("--remove-metadata=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --remove-metadata takes no value.", i, null);
            }

            if (argv[i] == "--coalesce-contents")
            {
                if (seenCoalesceContents)
                {
                    throw new QpdfParseException("Duplicate --coalesce-contents is outside the narrow profile.", i, null);
                }

                seenCoalesceContents = true;
                coalesceContents = true;
                continue;
            }

            if (argv[i].StartsWith("--coalesce-contents=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --coalesce-contents takes no value.", i, null);
            }

            if (argv[i] == "--normalize-content" || argv[i].StartsWith("--normalize-content=", StringComparison.Ordinal))
            {
                if (seenNormalizeContent)
                {
                    throw new QpdfParseException("Duplicate --normalize-content is outside the narrow profile.", i, null);
                }

                seenNormalizeContent = true;
                normalizeContent = ParseStreamChoice(argv, ref i, "--normalize-content", new string[] { "y", "n" }) == "y";
                continue;
            }

            if (argv[i] == "--remove-unreferenced-resources" || argv[i].StartsWith("--remove-unreferenced-resources=", StringComparison.Ordinal))
            {
                if (seenRemoveUnreferencedResources)
                {
                    throw new QpdfParseException("Duplicate --remove-unreferenced-resources is outside the narrow profile.", i, null);
                }

                seenRemoveUnreferencedResources = true;
                removeUnreferencedResources = ParseStreamChoice(argv, ref i, "--remove-unreferenced-resources", new string[] { "auto", "yes", "no" });
                continue;
            }

            if (argv[i] == "--preserve-unreferenced-resources")
            {
                if (seenPreserveUnreferencedResources)
                {
                    throw new QpdfParseException("Duplicate --preserve-unreferenced-resources is outside the narrow profile.", i, null);
                }

                seenPreserveUnreferencedResources = true;
                removeUnreferencedResources = "no";
                continue;
            }

            if (argv[i].StartsWith("--preserve-unreferenced-resources=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --preserve-unreferenced-resources takes no value.", i, null);
            }

            if (argv[i] == "--externalize-inline-images")
            {
                if (seenExternalizeInlineImages)
                {
                    throw new QpdfParseException("Duplicate --externalize-inline-images is outside the narrow profile.", i, null);
                }

                seenExternalizeInlineImages = true;
                externalizeInlineImages = true;
                continue;
            }

            if (argv[i].StartsWith("--externalize-inline-images=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --externalize-inline-images takes no value.", i, null);
            }

            if (argv[i] == "--ii-min-bytes" || argv[i].StartsWith("--ii-min-bytes=", StringComparison.Ordinal))
            {
                if (seenIiMinBytes)
                {
                    throw new QpdfParseException("Duplicate --ii-min-bytes is outside the narrow profile.", i, null);
                }

                seenIiMinBytes = true;
                iiMinBytes = ParseIiMinBytes(argv, ref i);
                continue;
            }

            if (argv[i] == "--flatten-rotation")
            {
                if (seenFlattenRotation)
                {
                    throw new QpdfParseException("Duplicate --flatten-rotation is outside the narrow profile.", i, null);
                }

                seenFlattenRotation = true;
                flattenRotation = true;
                continue;
            }

            if (argv[i].StartsWith("--flatten-rotation=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --flatten-rotation takes no value.", i, null);
            }

            if (argv[i] == "--flatten-annotations" || argv[i].StartsWith("--flatten-annotations=", StringComparison.Ordinal))
            {
                if (seenFlattenAnnotations)
                {
                    throw new QpdfParseException("Duplicate --flatten-annotations is outside the narrow profile.", i, null);
                }

                seenFlattenAnnotations = true;
                flattenAnnotations = ParseStreamChoice(argv, ref i, "--flatten-annotations", new string[] { "all", "print", "screen" });
                continue;
            }

            if (argv[i] == "--generate-appearances")
            {
                if (seenGenerateAppearances)
                {
                    throw new QpdfParseException("Duplicate --generate-appearances is outside the narrow profile.", i, null);
                }

                seenGenerateAppearances = true;
                generateAppearances = true;
                continue;
            }

            if (argv[i].StartsWith("--generate-appearances=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --generate-appearances takes no value.", i, null);
            }
            if (argv[i] == "--optimize-images")
            {
                if (seenOptimizeImages)
                {
                    throw new QpdfParseException("Duplicate --optimize-images is outside the narrow profile.", i, null);
                }
                seenOptimizeImages = true;
                optimizeImages = true;
                continue;
            }

            if (argv[i].StartsWith("--optimize-images=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --optimize-images takes no value.", i, null);
            }

            if (argv[i] == "--oi-min-width" || argv[i].StartsWith("--oi-min-width=", StringComparison.Ordinal))
            {
                if (seenOiMinWidth)
                {
                    throw new QpdfParseException("Duplicate --oi-min-width is outside the narrow profile.", i, null);
                }
                seenOiMinWidth = true;
                oiMinWidth = ParseOiMin(argv, ref i, "--oi-min-width");
                continue;
            }

            if (argv[i] == "--oi-min-height" || argv[i].StartsWith("--oi-min-height=", StringComparison.Ordinal))
            {
                if (seenOiMinHeight)
                {
                    throw new QpdfParseException("Duplicate --oi-min-height is outside the narrow profile.", i, null);
                }
                seenOiMinHeight = true;
                oiMinHeight = ParseOiMin(argv, ref i, "--oi-min-height");
                continue;
            }

            if (argv[i] == "--oi-min-area" || argv[i].StartsWith("--oi-min-area=", StringComparison.Ordinal))
            {
                if (seenOiMinArea)
                {
                    throw new QpdfParseException("Duplicate --oi-min-area is outside the narrow profile.", i, null);
                }
                seenOiMinArea = true;
                oiMinArea = ParseOiMin(argv, ref i, "--oi-min-area");
                continue;
            }

            if (argv[i] == "--keep-inline-images")
            {
                if (seenKeepInlineImages)
                {
                    throw new QpdfParseException("Duplicate --keep-inline-images is outside the narrow profile.", i, null);
                }
                seenKeepInlineImages = true;
                keepInlineImages = true;
                continue;
            }

            if (argv[i].StartsWith("--keep-inline-images=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --keep-inline-images takes no value.", i, null);
            }

            if (argv[i] == "--jpeg-quality" || argv[i].StartsWith("--jpeg-quality=", StringComparison.Ordinal))
            {
                if (seenJpegQuality)
                {
                    throw new QpdfParseException("Duplicate --jpeg-quality is outside the narrow profile.", i, null);
                }
                seenJpegQuality = true;
                jpegQuality = ParseJpegQuality(argv, ref i);
                continue;
            }
            if (argv[i] == "--qdf")
            {
                if (seenQdf)
                {
                    throw new QpdfParseException("Duplicate --qdf is outside the narrow profile.", i, null);
                }

                seenQdf = true;
                qdf = true;
                compressionSettings = compressionSettings.WithQdf();
                continue;
            }

            if (argv[i].StartsWith("--qdf=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --qdf takes no value.", i, null);
            }

            if (argv[i] == "--no-original-object-ids")
            {
                if (seenNoOriginalObjectIds)
                {
                    throw new QpdfParseException("Duplicate --no-original-object-ids is outside the narrow profile.", i, null);
                }

                seenNoOriginalObjectIds = true;
                noOriginalObjectIds = true;
                continue;
            }

            if (argv[i] == "--linearize-pass1" || argv[i].StartsWith("--linearize-pass1=", StringComparison.Ordinal))
            {
                if (seenLinearizePass1)
                {
                    throw new QpdfParseException("Duplicate --linearize-pass1 is outside the narrow profile.", i, null);
                }

                seenLinearizePass1 = true;
                linearizePass1 = ParseLinearizePass1Value(argv, ref i);
                continue;
            }

            if (argv[i] == "--max-stream-filters" || argv[i].StartsWith("--max-stream-filters=", StringComparison.Ordinal))
            {
                maxStreamFilters = ParseMaxStreamFilters(argv, ref i);
                continue;
            }

            if (argv[i] == "--parser-max-nesting" || argv[i].StartsWith("--parser-max-nesting=", StringComparison.Ordinal))
            {
                if (seenParserMaxNesting)
                {
                    throw new QpdfParseException("Duplicate --parser-max-nesting is outside the narrow profile.", i, null);
                }

                seenParserMaxNesting = true;
                parserMaxNesting = ParseParserMaxNesting(argv, ref i);
                continue;
            }

            if (argv[i] == "--no-default-limits")
            {
                if (seenNoDefaultLimits)
                {
                    throw new QpdfParseException("Duplicate --no-default-limits is outside the narrow profile.", i, null);
                }
                seenNoDefaultLimits = true;
                noDefaultLimits = true;
                continue;
            }

            if (argv[i].StartsWith("--no-default-limits=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --no-default-limits takes no value.", i, null);
            }

            if (argv[i] == "--parser-max-container-size" || argv[i].StartsWith("--parser-max-container-size=", StringComparison.Ordinal))
            {
                if (seenMaxContainerSize)
                {
                    throw new QpdfParseException("Duplicate --parser-max-container-size is outside the narrow profile.", i, null);
                }

                seenMaxContainerSize = true;
                maxContainerSize = ParseContainerSize(argv, ref i, "--parser-max-container-size", "object count");
                continue;
            }

            if (argv[i] == "--parser-max-container-size-damaged" || argv[i].StartsWith("--parser-max-container-size-damaged=", StringComparison.Ordinal))
            {
                if (seenMaxContainerSizeDamaged)
                {
                    throw new QpdfParseException("Duplicate --parser-max-container-size-damaged is outside the narrow profile.", i, null);
                }

                seenMaxContainerSizeDamaged = true;
                maxContainerSizeDamaged = ParseContainerSize(argv, ref i, "--parser-max-container-size-damaged", "object count");
                continue;
            }

            if (argv[i] == "--parser-max-errors" || argv[i].StartsWith("--parser-max-errors=", StringComparison.Ordinal))
            {
                if (seenParserMaxErrors)
                {
                    throw new QpdfParseException("Duplicate --parser-max-errors is outside the narrow profile.", i, null);
                }
                seenParserMaxErrors = true;
                parserMaxErrors = ParseParserMaxErrors(argv, ref i);
                continue;
            }

            if (argv[i] == "--linearize")
            {
                if (seenLinearize)
                {
                    throw new QpdfParseException("Duplicate --linearize is outside the narrow profile.", i, null);
                }

                seenLinearize = true;
                linearize = true;
                continue;
            }

            if (argv[i].StartsWith("--linearize=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --linearize takes no value.", i, null);
            }

            if (argv[i] == "--with-images")
            {
                if (seenWithImages)
                {
                    throw new QpdfParseException("Duplicate --with-images is outside the narrow profile.", i, null);
                }

                seenWithImages = true;
                withImages = true;
                continue;
            }

            if (argv[i].StartsWith("--with-images=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --with-images takes no value.", i, null);
            }

            if (argv[i] == "--ignore-xref-streams")
            {
                if (seenIgnoreXrefStreams)
                {
                    throw new QpdfParseException("Duplicate --ignore-xref-streams is outside the narrow profile.", i, null);
                }

                seenIgnoreXrefStreams = true;
                ignoreXrefStreams = true;
                continue;
            }

            if (argv[i] == "--report-memory-usage")
            {
                if (seenReportMemoryUsage)
                {
                    throw new QpdfParseException("Duplicate --report-memory-usage is outside the narrow profile.", i, null);
                }

                seenReportMemoryUsage = true;
                reportMemoryUsage = true;
                continue;
            }

            if (argv[i].StartsWith("--report-memory-usage=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --report-memory-usage takes no value.", i, null);
            }

            if (argv[i] == "--test-json-schema")
            {
                if (seenTestJsonSchema)
                {
                    throw new QpdfParseException("Duplicate --test-json-schema is outside the narrow profile.", i, null);
                }

                seenTestJsonSchema = true;
                testJsonSchema = true;
                continue;
            }

            if (argv[i] == "--verbose")
            {
                if (seenVerbose)
                {
                    throw new QpdfParseException("Duplicate --verbose is outside the narrow profile.", i, null);
                }

                seenVerbose = true;
                verbose = true;
                continue;
            }

            if (argv[i].StartsWith("--verbose=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --verbose takes no value.", i, null);
            }

            if (argv[i] == "--progress")
            {
                if (seenProgress)
                {
                    throw new QpdfParseException("Duplicate --progress is outside the narrow profile.", i, null);
                }

                seenProgress = true;
                progress = true;
                continue;
            }

            if (argv[i].StartsWith("--progress=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --progress takes no value.", i, null);
            }

            if (argv[i] == "--keep-files-open" || argv[i].StartsWith("--keep-files-open=", StringComparison.Ordinal))
            {
                if (seenKeepFilesOpen)
                {
                    throw new QpdfParseException("Duplicate --keep-files-open is outside the narrow profile.", i, null);
                }

                seenKeepFilesOpen = true;
                _ = ParseKeepFilesOpen(argv, ref i);
                continue;
            }

            if (argv[i] == "--keep-files-open-threshold" || argv[i].StartsWith("--keep-files-open-threshold=", StringComparison.Ordinal))
            {
                if (seenKeepFilesOpenThreshold)
                {
                    throw new QpdfParseException("Duplicate --keep-files-open-threshold is outside the narrow profile.", i, null);
                }

                seenKeepFilesOpenThreshold = true;
                _ = ParseContainerSize(argv, ref i, "--keep-files-open-threshold", "file count");
                continue;
            }

            if (argv[i].StartsWith("--test-json-schema=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --test-json-schema takes no value.", i, null);
            }

            if (argv[i].StartsWith("--ignore-xref-streams=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --ignore-xref-streams takes no value.", i, null);
            }

            if (argv[i].StartsWith("--no-original-object-ids=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --no-original-object-ids takes no value.", i, null);
            }

            if (argv[i] == "--object-streams" || argv[i].StartsWith("--object-streams=", StringComparison.Ordinal))
            {
                if (seenObjectStreams)
                {
                    throw new QpdfParseException("Duplicate --object-streams is outside the narrow profile.", i, null);
                }

                seenObjectStreams = true;
                objectStreams = ParseObjectStreamsMode(argv, ref i);
                continue;
            }

            filtering.Add(kept, argv[i]);
        }

        argv = ConfigurationText.Preserve(argv, kept);
        OutputRotation.NormalizeCommandSettings(mainRotations, governor);
        bool resolvedNormalize = normalizeContent || (qdf && !seenNormalizeContent);
        return new GlobalOptions
        {
            Recovery = recovery,
            WarningExitZero = warningExitZero,
            NoWarn = noWarn,
            SplitPages = splitPages,
            ReplaceInput = replaceInput,
            MinVersion = minVersion,
            ForceVersion = forceVersion,
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
            NormalizeContent = resolvedNormalize,
            RemoveUnreferencedResources = removeUnreferencedResources,
            ExternalizeInlineImages = externalizeInlineImages,
            IiMinBytes = iiMinBytes,
            FlattenRotation = flattenRotation,
            FlattenAnnotations = flattenAnnotations,
            GenerateAppearances = generateAppearances,
            OptimizeImages = optimizeImages,
            OiMinWidth = oiMinWidth,
            OiMinHeight = oiMinHeight,
            OiMinArea = oiMinArea,
            KeepInlineImages = keepInlineImages,
            JpegQuality = jpegQuality,
            Qdf = qdf,
            NoOriginalObjectIds = noOriginalObjectIds,
            Linearize = linearize,
            LinearizePass1 = linearizePass1,
            MaxStreamFilters = maxStreamFilters,
            ParserMaxNesting = parserMaxNesting,
            WithImages = withImages,
            IgnoreXrefStreams = ignoreXrefStreams,
            MaxContainerSize = maxContainerSize,
            MaxContainerSizeDamaged = maxContainerSizeDamaged,
            ReportMemoryUsage = reportMemoryUsage,
            TestJsonSchema = testJsonSchema,
            Verbose = verbose,
            Progress = progress,
            RemoveAcroform = removeAcroform,
            RemoveStructure = removeStructure,
            Collate = collate,
            Rotations = mainRotations,
            NoDefaultLimits = noDefaultLimits,
            ParserMaxErrors = parserMaxErrors,

            CompressionState = compressionSettings.State,
            CollationStorage = ConfigurationText.OwnerOf(argv)?.Collation ?? throw new InvalidOperationException("Missing collation owner."),
        };
    }

    private static long ParseIiMinBytes(IReadOnlyList<string> argv, ref int i)
    {
        string token = argv[i];
        int equals = token.IndexOf((char)61);
        string? value = null;
        if (equals >= 0)
        {
            value = ConfigurationText.Slice(argv, token, equals + 1);
        }
        else if (i + 1 < argv.Count)
        {
            string next = argv[i + 1];
            if (next is not null && next != "--" && !(next.StartsWith("--", StringComparison.Ordinal) && next.Length > 2))
            {
                value = next;
                i += 1;
            }
        }

        if (value is null || value.Length == 0 || value.Length > 10)
        {
            throw new QpdfParseException($"Invalid --ii-min-bytes value '{ErrorPreview(value)}' : expected a non-negative byte count.", i, null);
        }

        long size = 0L;
        foreach (char digit in value)
        {
            if (digit < (char)48 || digit > (char)57)
            {
                throw new QpdfParseException($"Invalid --ii-min-bytes value '{ErrorPreview(value)}' : expected a non-negative byte count.", i, null);
            }

            size = checked(size * 10L + (digit - (char)48));
        }

        return size;
    }

    private static long ParseOiMin(IReadOnlyList<string> argv, ref int i, string option)
    {
        string token = argv[i];
        int equals = token.IndexOf((char)61);
        string? value = null;
        if (equals >= 0)
        {
            value = ConfigurationText.Slice(argv, token, equals + 1);
        }
        else if (i + 1 < argv.Count)
        {
            string next = argv[i + 1];
            if (next is not null && next != "--" && !(next.StartsWith("--", StringComparison.Ordinal) && next.Length > 2))
            {
                value = next;
                i += 1;
            }
        }

        if (value is null || value.Length == 0 || value.Length > 10)
        {
            throw new QpdfParseException($"Invalid {option} value '{ErrorPreview(value)}' : expected a non-negative pixel count.", i, null);
        }

        long size = 0L;
        foreach (char digit in value)
        {
            if (digit < (char)48 || digit > (char)57)
            {
                throw new QpdfParseException($"Invalid {option} value '{ErrorPreview(value)}' : expected a non-negative pixel count.", i, null);
            }

            size = checked(size * 10L + (digit - (char)48));
        }

        return size;
    }

    private static long ParseParserMaxNesting(IReadOnlyList<string> argv, ref int i)
    {
        string token = argv[i];
        int equals = token.IndexOf((char)61);
        string? value = null;
        if (equals >= 0)
        {
            value = ConfigurationText.Slice(argv, token, equals + 1);
        }
        else if (i + 1 < argv.Count)
        {
            string next = argv[i + 1];
            if (next is not null && next != "--" && !(next.StartsWith("--", StringComparison.Ordinal) && next.Length > 2))
            {
                value = next;
                i += 1;
            }
        }

        if (value is null || value.Length == 0 || value.Length > 10)
        {
            throw new QpdfParseException($"Invalid --parser-max-nesting value \u0027{ErrorPreview(value)}\u0027: expected a nesting depth within 1..{QpdfLimits.ParserNestingCeiling}.", i, null);
        }

        long depth = 0L;
        foreach (char digit in value)
        {
            if (digit < (char)48 || digit > (char)57)
            {
                throw new QpdfParseException($"Invalid --parser-max-nesting value \u0027{ErrorPreview(value)}\u0027: expected a nesting depth within 1..{QpdfLimits.ParserNestingCeiling}.", i, null);
            }

            depth = checked(depth * 10L + (digit - (char)48));
        }

        if (depth < 1L || depth > QpdfLimits.ParserNestingCeiling)
        {
            throw new QpdfParseException($"Invalid --parser-max-nesting value \u0027{ErrorPreview(value)}\u0027: expected a nesting depth within 1..{QpdfLimits.ParserNestingCeiling}.", i, null);
        }

        return depth;
    }

    internal static string ParseKeepFilesOpen(IReadOnlyList<string> argv, ref int i)
    {
        string token = argv[i];
        int equals = token.IndexOf((char)61);
        string? value = equals >= 0 ? ConfigurationText.Slice(argv, token, equals + 1) : null;
        if (value != "y" && value != "n")
        {
            throw new QpdfParseException("Option --keep-files-open must be --keep-files-open=y or --keep-files-open=n.", i, null);
        }
        return value;
    }

    private static long ParseContainerSize(IReadOnlyList<string> argv, ref int i, string option, string unit)
    {
        string token = argv[i];
        int equals = token.IndexOf((char)61);
        string? value = null;
        if (equals >= 0)
        {
            value = ConfigurationText.Slice(argv, token, equals + 1);
        }
        else if (i + 1 < argv.Count)
        {
            string next = argv[i + 1];
            if (next is not null && next != "--" && !(next.StartsWith("--", StringComparison.Ordinal) && next.Length > 2))
            {
                value = next;
                i += 1;
            }
        }

        if (value is null || value.Length == 0 || value.Length > 10)
        {
            throw new QpdfParseException($"Invalid {option} value \u0027{ErrorPreview(value)}\u0027: expected a non-negative {unit}.", i, null);
        }

        long count = 0L;
        foreach (char digit in value)
        {
            if (digit < (char)48 || digit > (char)57)
            {
                throw new QpdfParseException($"Invalid {option} value \u0027{ErrorPreview(value)}\u0027: expected a non-negative {unit}.", i, null);
            }

            count = checked(count * 10L + (digit - (char)48));
        }

        return count;
    }

    private static long ParseMaxStreamFilters(IReadOnlyList<string> argv, ref int i)
    {
        string token = argv[i];
        int equals = token.IndexOf((char)61);
        string? value = null;
        if (equals >= 0)
        {
            value = ConfigurationText.Slice(argv, token, equals + 1);
        }
        else if (i + 1 < argv.Count)
        {
            string next = argv[i + 1];
            if (next is not null && next != "--" && !(next.StartsWith("--", StringComparison.Ordinal) && next.Length > 2))
            {
                value = next;
                i += 1;
            }
        }

        if (value is null || value.Length == 0 || value.Length > 10)
        {
            throw new QpdfParseException($"Invalid --max-stream-filters value \u0027{ErrorPreview(value)}\u0027: expected a non-negative filter count.", i, null);
        }

        long count = 0L;
        foreach (char digit in value)
        {
            if (digit < (char)48 || digit > (char)57)
            {
                throw new QpdfParseException($"Invalid --max-stream-filters value \u0027{ErrorPreview(value)}\u0027: expected a non-negative filter count.", i, null);
            }

            count = checked(count * 10L + (digit - (char)48));
        }

        return count;
    }

    private static long ParseParserMaxErrors(IReadOnlyList<string> argv, ref int i)
    {
        string token = argv[i];
        int equals = token.IndexOf((char)61);
        string? value = null;
        if (equals >= 0)
        {
            value = ConfigurationText.Slice(argv, token, equals + 1);
        }
        else if (i + 1 < argv.Count)
        {
            string next = argv[i + 1];
            if (next is not null && next != "--" && !(next.StartsWith("--", StringComparison.Ordinal) && next.Length > 2))
            {
                value = next;
                i += 1;
            }
        }

        if (value is null || value.Length == 0 || value.Length > 10)
        {
            throw new QpdfParseException($"Invalid --parser-max-errors value \u0027{ErrorPreview(value)}\u0027: expected a non-negative error count (0 removes the threshold).", i, null);
        }

        long count = 0L;
        foreach (char digit in value)
        {
            if (digit < (char)48 || digit > (char)57)
            {
                throw new QpdfParseException($"Invalid --parser-max-errors value \u0027{ErrorPreview(value)}\u0027: expected a non-negative error count (0 removes the threshold).", i, null);
            }

            count = checked(count * 10L + (digit - (char)48));
        }

        return count;
    }

    private static int ParseJpegQuality(IReadOnlyList<string> argv, ref int i)
    {
        string token = argv[i];
        int equals = token.IndexOf((char)61);
        string? value = null;
        if (equals >= 0)
        {
            value = ConfigurationText.Slice(argv, token, equals + 1);
        }
        else if (i + 1 < argv.Count)
        {
            string next = argv[i + 1];
            if (next is not null && next != "--" && !(next.StartsWith("--", StringComparison.Ordinal) && next.Length > 2))
            {
                value = next;
                i += 1;
            }
        }

        if (value is null || value.Length == 0 || value.Length > 3)
        {
            throw new QpdfParseException($"Invalid --jpeg-quality value '{ErrorPreview(value)}' : expected an integer 0..100.", i, null);
        }

        long quality = 0L;
        foreach (char digit in value)
        {
            if (digit < (char)48 || digit > (char)57)
            {
                throw new QpdfParseException($"Invalid --jpeg-quality value '{ErrorPreview(value)}' : expected an integer 0..100.", i, null);
            }

            quality = checked(quality * 10L + (digit - (char)48));
        }

        if (quality > 100L)
        {
            throw new QpdfParseException($"Invalid --jpeg-quality value '{ErrorPreview(value)}' : expected an integer 0..100.", i, null);
        }

        return (int)quality;
    }
    internal static int ParseSplitPages(IReadOnlyList<string> argv, ref int i)
    {
        string token = argv[i];
        int equals = token.IndexOf('=');
        string? value = null;
        if (equals >= 0)
        {
            value = ConfigurationText.Slice(argv, token, equals + 1);
        }
        else if (i + 1 < argv.Count)
        {
            string next = argv[i + 1];
            if (next is not null && next != "--" && !(next.StartsWith("--", StringComparison.Ordinal) && next.Length > 2))
            {
                value = next;
                i += 1;
            }
        }

        return ParseSplitPagesValue(value, i);
    }

    internal static int ParseSplitPagesValue(string? value, long offset)
    {
        if (value is null || value.Length == 0)
        {
            return 1;
        }

        if (value.Length > 10)
        {
            throw new QpdfParseException($"Invalid --split-pages value '{ErrorPreview(value)}' : expected a positive group size.", offset, null);
        }

        long size = 0L;
        foreach (char digit in value)
        {
            if (digit < '0' || digit > '9')
            {
                throw new QpdfParseException($"Invalid --split-pages value '{ErrorPreview(value)}' : expected a positive group size.", offset, null);
            }

            size = checked(size * 10L + (digit - '0'));
        }

        if (size < 1L || size > int.MaxValue)
        {
            throw new QpdfParseException($"Invalid --split-pages value '{ErrorPreview(value)}' : expected a positive group size.", offset, null);
        }

        return (int)size;
    }

    private static string ParseLinearizePass1Value(IReadOnlyList<string> argv, ref int i)
    {
        string token = argv[i];
        int equals = token.IndexOf((char)61);
        if (equals >= 0)
        {
            string inline = ConfigurationText.Slice(argv, token, equals + 1);
            if (inline.Length == 0)
            {
                throw new QpdfParseException("Empty parameter for --linearize-pass1.", i, null);
            }

            return inline;
        }

        if (i + 1 >= argv.Count)
        {
            throw new QpdfParseException("Missing parameter for --linearize-pass1.", i, null);
        }

        string next = argv[i + 1];
        if (next is null || next == "--" || (next.StartsWith("--", StringComparison.Ordinal) && next.Length > 2))
        {
            throw new QpdfParseException("Missing parameter for --linearize-pass1.", i, null);
        }

        i += 1;
        return next;
    }

    private static string ParseObjectStreamsMode(IReadOnlyList<string> argv, ref int i)
    {
        string token = argv[i];
        int equals = token.IndexOf((char)61);
        string? value = null;
        if (equals >= 0)
        {
            value = ConfigurationText.Slice(argv, token, equals + 1);
        }
        else if (i + 1 < argv.Count)
        {
            string next = argv[i + 1];
            if (next is null || next == "--" || (next.StartsWith("--", StringComparison.Ordinal) && next.Length > 2))
            {
                throw new QpdfParseException("--object-streams requires a mode: disable, preserve, or generate.", i, null);
            }

            value = next;
            i += 1;
        }
        else
        {
            throw new QpdfParseException("--object-streams requires a mode: disable, preserve, or generate.", i, null);
        }

        if (value != "disable" && value != "preserve" && value != "generate")
        {
            throw new QpdfParseException("--object-streams mode must be disable, preserve, or generate.", i, null);
        }

        return value;
    }

    private static string ParseStreamChoice(IReadOnlyList<string> argv, ref int i, string option, string[] choices)
    {
        if (choices is null)
        {
            throw new ArgumentNullException(nameof(choices));
        }

        string? value = TakeChoiceValue(argv, ref i, option);
        foreach (string choice in choices)
        {
            if (value == choice)
            {
                return value;
            }
        }

        throw new QpdfParseException(option + " must be one of: " + string.Join(", ", choices) + ".", i, null);
    }

    internal static string ParseOptionalChoice(IReadOnlyList<string> argv, ref int i, string option, string[] choices, string bareDefault)
    {
        if (choices is null)
        {
            throw new ArgumentNullException(nameof(choices));
        }

        if (bareDefault is null)
        {
            throw new ArgumentNullException(nameof(bareDefault));
        }

        string token = argv[i];
        int equals = token.IndexOf((char)61);
        if (equals < 0 && (i + 1 >= argv.Count || argv[i + 1] is null || argv[i + 1] == "--" || (argv[i + 1].StartsWith("--", StringComparison.Ordinal) && argv[i + 1].Length > 2)))
        {
            return bareDefault;
        }

        string? value = TakeChoiceValue(argv, ref i, option);
        foreach (string choice in choices)
        {
            if (value == choice)
            {
                return value;
            }
        }

        throw new QpdfParseException(option + " must be one of: " + string.Join(", ", choices) + ".", i, null);
    }

    private static string ParseRequiredChoiceValue(IReadOnlyList<string> argv, ref int i, string option)
    {
        return TakeChoiceValue(argv, ref i, option) ?? throw new QpdfParseException(option + " requires a value.", i, null);
    }

    private static string? TakeChoiceValue(IReadOnlyList<string> argv, ref int i, string option)
    {
        string token = argv[i];
        int equals = token.IndexOf((char)61);
        if (equals >= 0)
        {
            return ConfigurationText.Slice(argv, token, equals + 1);
        }

        if (i + 1 >= argv.Count)
        {
            throw new QpdfParseException(option + " requires a value.", i, null);
        }

        string next = argv[i + 1];
        if (next is null || next == "--" || (next.StartsWith("--", StringComparison.Ordinal) && next.Length > 2))
        {
            throw new QpdfParseException(option + " requires a value.", i, null);
        }

        i += 1;
        return next;
    }

    private static string ParseRequiredVersion(IReadOnlyList<string> argv, ref int i, string option)
    {
        string token = argv[i];
        int equals = token.IndexOf((char)61);
        string? value = null;
        if (equals >= 0)
        {
            value = ConfigurationText.Slice(argv, token, equals + 1);
        }
        else if (i + 1 < argv.Count)
        {
            string next = argv[i + 1];
            if (next is null || next == "--" || (next.StartsWith("--", StringComparison.Ordinal) && next.Length > 2))
            {
                throw new QpdfParseException(option + " requires a version such as 1.5.", i, null);
            }

            value = next;
            i += 1;
        }
        else
        {
            throw new QpdfParseException(option + " requires a version such as 1.5.", i, null);
        }

        if (value is null || value.Length == 0 || value.Length > 32)
        {
            throw new QpdfParseException(option + " requires a version such as 1.5.", i, null);
        }

        return value;
    }

    private static QpdfPath? ParseLinearizePass1(GlobalOptions globals)
    {
        if (globals.LinearizePass1 is null)
        {
            return null;
        }

        return SelectionPlan.CheckedPath(globals.LinearizePass1);
    }

    private static bool IsEmptyDocumentInput(IReadOnlyList<string> argv, int boundaryAt)
    {
        if (boundaryAt != 1)
        {
            return false;
        }

        return argv.Count > 0 && argv[0] == JobPlan.EmptyDocumentInput;
    }

    private static bool HasAttachmentEdit(IReadOnlyList<string> argv, int start, int end)
    {
        return FirstAttachmentEdit(argv, start, end) >= 0;
    }

    private static bool IsJsonFlag(string token)
    {
        return token == "--json" || token.StartsWith("--json=", StringComparison.Ordinal);
    }

    private static int FindJson(IReadOnlyList<string> argv)
    {
        int found = -1;
        for (int i = 0; i < argv.Count; i++)
        {
            if (argv[i] is not null && IsJsonFlag(argv[i]))
            {
                if (found >= 0)
                {
                    throw new QpdfParseException("Duplicate --json is outside the narrow profile.", i, null);
                }

                found = i;
            }
        }

        return found;
    }

    private static int FindStrayJsonOption(IReadOnlyList<string> argv)
    {
        for (int i = 0; i < argv.Count; i++)
        {
            string token = argv[i];
            if (token is null)
            {
                continue;
            }

            if (token == "--json-key" || token.StartsWith("--json-key=", StringComparison.Ordinal)
                || token == "--json-object" || token.StartsWith("--json-object=", StringComparison.Ordinal)
                || token == "--json-stream-data" || token.StartsWith("--json-stream-data=", StringComparison.Ordinal)
                || token == "--json-stream-prefix" || token.StartsWith("--json-stream-prefix=", StringComparison.Ordinal)
                || token == "--json-output" || token.StartsWith("--json-output=", StringComparison.Ordinal)
                || token == "--json-help" || token.StartsWith("--json-help=", StringComparison.Ordinal)
                || token == "--json-input")
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindJsonOutput(IReadOnlyList<string> argv)
    {
        int found = -1;
        for (int i = 0; i < argv.Count; i++)
        {
            if (argv[i] is not null && (argv[i] == "--json-output" || argv[i].StartsWith("--json-output=", StringComparison.Ordinal)))
            {
                if (found >= 0)
                {
                    throw new QpdfParseException("Duplicate --json-output is outside the narrow profile.", i, null);
                }

                found = i;
            }
        }

        return found;
    }

    private static JobPlan ParseJson(IReadOnlyList<string> argv, int entryAt, QpdfRecoveryMode recovery, GlobalOptions globals)
    {
        string input = IsEmptyDocumentInput(argv, entryAt) ? JobPlan.EmptyDocumentInput : ParseInputArea(argv, entryAt, "--json", null);
        int cursor = entryAt;
        bool seenJsonVersion = false;
        int jsonVersion = 0;
        List<string> keys = new();
        HashSet<(bool Trailer, int Number, int Generation)>? objects = null;
        bool seenStreamData = false;
        string? streamData = null;
        string? streamPrefix = null;
        bool seenStreamPrefix = false;
        bool minimalOutput = false;
        int outputVersion = 0;
        while (cursor < argv.Count && argv[cursor] != "--")
        {
            string token = argv[cursor];
            if (token is null)
            {
                throw new QpdfParseException("Null argument.", cursor, null);
            }

            if (IsJsonFlag(token))
            {
                if (seenJsonVersion)
                {
                    throw new QpdfParseException("Duplicate --json is outside the narrow profile.", cursor, null);
                }

                bool explicitVersion = token.StartsWith("--json=", StringComparison.Ordinal)
                    || (cursor + 1 < argv.Count && argv[cursor + 1] is not null && argv[cursor + 1] != "--"
                        && !(argv[cursor + 1].StartsWith("--", StringComparison.Ordinal) && argv[cursor + 1].Length > 2));
                string versionText = ParseOptionalChoice(argv, ref cursor, "--json", new string[] { "1", "2", "latest" }, "latest");
                seenJsonVersion = explicitVersion;
                jsonVersion = versionText == "1" ? 1 : 2;
                cursor += 1;
                continue;
            }

            if (token == "--json-key" || token.StartsWith("--json-key=", StringComparison.Ordinal))
            {
                keys.Add(ParseStreamChoice(argv, ref cursor, "--json-key", new string[] { "acroform", "attachments", "encrypt", "objectinfo", "objects", "outlines", "pagelabels", "pages", "qpdf" }));
                cursor += 1;
                continue;
            }

            if (token == "--json-object" || token.StartsWith("--json-object=", StringComparison.Ordinal))
            {
                string selector = TakeParameter(argv, argv.Count, ref cursor, "--json-object");
                objects = Qpdf.AddJsonObject(objects, selector, cursor, ConfigurationText.OwnerOf(argv)
                    ?? throw new InvalidOperationException("Missing JSON selection owner."));
                continue;
            }

            if (token == "--json-stream-data" || token.StartsWith("--json-stream-data=", StringComparison.Ordinal))
            {
                if (seenStreamData)
                {
                    throw new QpdfParseException("Duplicate --json-stream-data is outside the narrow profile.", cursor, null);
                }

                seenStreamData = true;
                streamData = ParseStreamChoice(argv, ref cursor, "--json-stream-data", new string[] { "none", "inline", "file" });
                cursor += 1;
                continue;
            }

            if (token == "--json-stream-prefix" || token.StartsWith("--json-stream-prefix=", StringComparison.Ordinal))
            {
                if (seenStreamPrefix)
                {
                    throw new QpdfParseException("Duplicate --json-stream-prefix is outside the narrow profile.", cursor, null);
                }

                seenStreamPrefix = true;
                streamPrefix = TakeParameter(argv, argv.Count, ref cursor, "--json-stream-prefix");
                if (streamPrefix.Length > QpdfPath.MaxLength || streamPrefix.Contains((char)0))
                {
                    throw new QpdfParseException("Option --json-stream-prefix is not a valid logical path.", cursor, null);
                }

                continue;
            }

            if (token == "--json-output" || token.StartsWith("--json-output=", StringComparison.Ordinal))
            {
                if (minimalOutput)
                {
                    throw new QpdfParseException("Duplicate --json-output is outside the narrow profile.", cursor, null);
                }

                minimalOutput = true;
                ParseOptionalChoice(argv, ref cursor, "--json-output", new string[] { "2", "latest" }, "latest");
                outputVersion = 2;
                cursor += 1;
                continue;
            }

            if (token == "--json-help" || token.StartsWith("--json-help=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --json-help is outside the narrow profile.", cursor, null);
            }

            if (token == "--json-input")
            {
                throw new QpdfParseException("Option --json-input is outside the narrow profile.", cursor, null);
            }

            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                CheckOptionToken(token, cursor);
            }

            throw new QpdfParseException("Unexpected argument '" + token + "' after --json.", cursor, null);
        }

        int version = seenJsonVersion ? jsonVersion : outputVersion;
        if (version == 0)
        {
            version = 2;
        }

        if (minimalOutput && version == 1)
        {
            throw new QpdfParseException("Option --json-output requires JSON version 2.", entryAt, null);
        }

        if (minimalOutput && !keys.Contains("qpdf"))
        {
            keys.Add("qpdf");
        }

        Qpdf.ValidateJsonKeys(version, keys);
        if (cursor < argv.Count && argv[cursor] != "--")
        {
            throw new QpdfParseException("An output file is required after --.", cursor, null);
        }

        QpdfPath? output = cursor >= argv.Count || cursor == argv.Count - 1
            ? null : SelectionPlan.CheckedPath(ParseOutputArea(argv, cursor, null));
        return new JsonPlan(SelectionPlan.CheckedPath(input), output, version, keys, recovery) with
        {
            EmptyDocument = string.Equals(input, JobPlan.EmptyDocumentInput, StringComparison.Ordinal),
            DecodeLevel = globals.DecodeLevel ?? (minimalOutput ? "none" : "generalized"),
            ReportMemoryUsage = globals.ReportMemoryUsage,
            TestJsonSchema = globals.TestJsonSchema,
            MaxStreamFilters = globals.MaxStreamFilters,
            ParserMaxNesting = globals.ParserMaxNesting,
            NoDefaultLimits = globals.NoDefaultLimits,
            MaxContainerSize = globals.MaxContainerSize,
            MaxContainerSizeDamaged = globals.MaxContainerSizeDamaged,
            ParserMaxErrors = globals.ParserMaxErrors,
            StreamDataMode = seenStreamData ? streamData! : (minimalOutput ? "inline" : "none"),
            StreamPrefix = streamPrefix,
            Objects = objects,
            IgnoreXrefStreams = globals.IgnoreXrefStreams,
            Verbose = globals.Verbose,
            Progress = globals.Progress,
            MinimalOutput = minimalOutput,
            NoWarn = globals.NoWarn,
            WarningExitZero = globals.WarningExitZero,
        };
    }

    private static int FindUpdateFromJson(IReadOnlyList<string> argv)
    {
        int found = -1;
        for (int i = 0; i < argv.Count; i++)
        {
            if (argv[i] is not null && (argv[i] == "--update-from-json" || argv[i].StartsWith("--update-from-json=", StringComparison.Ordinal)))
            {
                if (found >= 0)
                {
                    throw new QpdfParseException("Duplicate --update-from-json is outside the narrow profile.", i, null);
                }

                found = i;
            }
        }

        return found;
    }

    private static JobPlan ParseJsonImport(
        IReadOnlyList<string> argv,
        int entryAt,
        int inputAt,
        int updateAt,
        QpdfRecoveryMode recovery,
        GlobalOptions globals)
    {
        if (inputAt >= 0 && updateAt >= 0)
        {
            throw new QpdfParseException("Options --json-input and --update-from-json cannot be combined.", entryAt, null);
        }

        if (globals.ObjectStreams is not null || globals.StreamData is not null || globals.CompressStreams is not null
            || globals.DecodeLevel is not null || globals.MaxStreamFilters is not null || globals.ParserMaxNesting is not null || globals.MaxContainerSize is not null || globals.MaxContainerSizeDamaged is not null || globals.NoDefaultLimits || globals.ParserMaxErrors is not null || globals.TestJsonSchema || globals.RecompressFlate || globals.CompressionLevel is not null
            || globals.RemoveInfo || globals.RemoveMetadata || globals.PreserveUnreferenced
            || globals.RemoveAcroform || globals.RemoveStructure || globals.CoalesceContents || globals.NormalizeContent
            || globals.ExternalizeInlineImages || globals.IiMinBytes != 1024L
            || globals.RemoveUnreferencedResources is not (null or "no")
            || globals.OptimizeImages || globals.OiMinWidth != 128L || globals.OiMinHeight != 128L
            || globals.OiMinArea != 16384L || globals.KeepInlineImages || globals.JpegQuality is not null
            || globals.NoOriginalObjectIds || globals.LinearizePass1 is not null)
        {
            throw new QpdfParseException("Stream, metadata, content, image and PDF layout settings cannot be combined with JSON import.", entryAt, null);
        }

        if (updateAt >= 0)
        {
            string pdfInput = ParseInputArea(argv, entryAt, "--update-from-json", null);
            int updateCursor = updateAt;
            string updateFile = TakeParameter(argv, argv.Count, ref updateCursor, "--update-from-json");
            if (updateCursor >= argv.Count || argv[updateCursor] != "--")
            {
                if (updateCursor < argv.Count && argv[updateCursor] is not null && argv[updateCursor] != "--")
                {
                    throw new QpdfParseException("Unexpected argument '" + argv[updateCursor] + "' after --update-from-json.", updateCursor, null);
                }

                throw new QpdfParseException("An output file is required after --.", updateCursor, null);
            }

            string updateOutput = ParseOutputArea(argv, updateCursor, null);
            return new JsonUpdatePlan(SelectionPlan.CheckedPath(pdfInput), SelectionPlan.CheckedPath(updateFile), SelectionPlan.CheckedPath(updateOutput), recovery) with
            {
                WarningExitZero = globals.WarningExitZero,
                Verbose = globals.Verbose,
            Progress = globals.Progress,
                ReportMemoryUsage = globals.ReportMemoryUsage,
                IgnoreXrefStreams = globals.IgnoreXrefStreams,
                NoWarn = globals.NoWarn,
                MinVersion = globals.MinVersion,
                ForceVersion = globals.ForceVersion,
                StaticId = globals.StaticId,
                NewlineBeforeEndstream = globals.NewlineBeforeEndstream,
            };
        }

        string input = ParseInputArea(argv, entryAt, "--json-input", null);
        int cursor = entryAt + 1;
        if (cursor >= argv.Count || argv[cursor] != "--")
        {
            if (cursor < argv.Count && argv[cursor] is not null && argv[cursor] != "--")
            {
                throw new QpdfParseException("Unexpected argument '" + argv[cursor] + "' after --json-input.", cursor, null);
            }

            throw new QpdfParseException("An output file is required after --.", cursor, null);
        }

        string output = ParseOutputArea(argv, cursor, null);
        return new JsonInputPlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output)) with
        {
            WarningExitZero = globals.WarningExitZero,
            Verbose = globals.Verbose,
            Progress = globals.Progress,
            ReportMemoryUsage = globals.ReportMemoryUsage,
            IgnoreXrefStreams = globals.IgnoreXrefStreams,
            NoWarn = globals.NoWarn,
            MinVersion = globals.MinVersion,
            ForceVersion = globals.ForceVersion,
            StaticId = globals.StaticId,
            NewlineBeforeEndstream = globals.NewlineBeforeEndstream,
        };
    }

    private static int FirstAttachmentEdit(IReadOnlyList<string> argv, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            string token = argv[i];
            if (token is null)
            {
                continue;
            }

            if (token == "--add-attachment"
                || token.StartsWith("--remove-attachment=", StringComparison.Ordinal)
                || token == "--remove-attachment"
                || token == "--copy-attachments-from"
                || token.StartsWith("--copy-attachments-from=", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsTrailingBlockStart(string? token)
    {
        return token == "--overlay"
            || token == "--underlay"
            || token == "--add-attachment"
            || token == "--copy-attachments-from"
            || (token is not null && (token.StartsWith("--remove-attachment=", StringComparison.Ordinal) || token.StartsWith("--copy-attachments-from=", StringComparison.Ordinal)));
    }

    private static int FirstOverlayBlock(IReadOnlyList<string> argv, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (argv[i] == "--overlay" || argv[i] == "--underlay")
            {
                return i;
            }
        }

        return -1;
    }

    private sealed record OperationBlocks(List<AttachmentAdd> Adds, List<string> Removes,
        List<AttachmentCopy> Copies, List<OverlaySpec> Overlays, List<OverlaySpec> Underlays);

    private static void ParseOperationBlocks(IReadOnlyList<string> argv, ref int cursor, OperationBlocks blocks)
    {
        while (cursor < argv.Count)
        {
            if (TryParseOperationBlock(argv, ref cursor, blocks)) { continue; }
            string token = argv[cursor];
            if (token == "--")
            {
                if (cursor + 1 < argv.Count && IsTrailingBlockStart(argv[cursor + 1]))
                {
                    cursor += 1;
                    continue;
                }

                break;
            }

            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                CheckOptionToken(token, cursor);
                continue;
            }

            // A completed trailing block returns to main positional context.
            // ParseOutputArea validates the remaining output arguments.
            break;
        }

    }

    private static bool TryParseOperationBlock(IReadOnlyList<string> argv, ref int cursor, OperationBlocks blocks)
    {
        string token = argv[cursor];
        if (token is null) { throw new QpdfParseException("Null argument.", cursor, null); }
        if (token == "--overlay" || token == "--underlay")
        {
            bool underlay = token == "--underlay";
            OverlaySpec spec = ParseOverlayBlock(argv, ref cursor, underlay);
            (underlay ? blocks.Underlays : blocks.Overlays).Add(spec);
            return true;
        }
        if (token == "--add-attachment")
        {
            blocks.Adds.Add(ParseAddBlock(argv, ref cursor));
            return true;
        }
        if (token.StartsWith("--remove-attachment=", StringComparison.Ordinal))
        {
            blocks.Removes.Add(ConfigurationText.Slice(argv, token, "--remove-attachment=".Length));
            cursor++;
            return true;
        }
        if (token == "--remove-attachment")
        {
            throw new QpdfParseException("Option --remove-attachment requires =key.", cursor, null);
        }
        if (token == "--copy-attachments-from")
        {
            blocks.Copies.Add(ParseCopyBlock(argv, ref cursor));
            return true;
        }
        if (token.StartsWith("--copy-attachments-from=", StringComparison.Ordinal))
        {
            throw new QpdfParseException("Option --copy-attachments-from takes no value.", cursor, null);
        }
        if (token.StartsWith("--overlay=", StringComparison.Ordinal) || token.StartsWith("--underlay=", StringComparison.Ordinal))
        {
            throw new QpdfParseException($"Option {ErrorPreview(OptionName(token))} takes no value.", cursor, null);
        }
        return false;
    }

    internal static OverlaySpec ParseOverlayBlock(IReadOnlyList<string> argv, ref int cursor, bool underlay)
    {
        string role = underlay ? "--underlay" : "--overlay";
        cursor += 1;
        string? file = null;
        string? to = null;
        string? from = null;
        string? repeat = null;
        string? password = null;
        bool seenFile = false;
        bool seenTo = false;
        bool seenFrom = false;
        bool seenRepeat = false;
        while (cursor < argv.Count && argv[cursor] != "--")
        {
            string token = argv[cursor];
            if (token is null)
            {
                throw new QpdfParseException("Null argument.", cursor, null);
            }

            if (token.StartsWith("--password=", StringComparison.Ordinal))
            {
                password = ConfigurationText.Slice(argv, token, "--password=".Length);
                cursor++;
                continue;
            }
            if (token == "--password")
            { throw new QpdfParseException("Option --password requires =password.", cursor, null); }

            if (token == "--file")
            {
                throw new QpdfParseException("Option --file requires =file.", cursor, null);
            }

            if (token.StartsWith("--file=", StringComparison.Ordinal))
            {
                if (seenFile)
                {
                    throw new QpdfParseException("Duplicate --file is outside the narrow profile.", cursor, null);
                }

                seenFile = true;
                file = TakeParameter(argv, argv.Count, ref cursor, "--file");
                continue;
            }

            if (token == "--to")
            {
                throw new QpdfParseException("Option --to requires =range.", cursor, null);
            }

            if (token.StartsWith("--to=", StringComparison.Ordinal))
            {
                if (seenTo)
                {
                    throw new QpdfParseException("Duplicate --to is outside the narrow profile.", cursor, null);
                }

                seenTo = true;
                to = TakeOverlayRange(argv, ref cursor);
                OverlayRanges.ValidateSyntax(to, "--to", cursor);
                continue;
            }

            if (token == "--from")
            {
                throw new QpdfParseException("Option --from requires =range.", cursor, null);
            }

            if (token.StartsWith("--from=", StringComparison.Ordinal))
            {
                if (seenFrom)
                {
                    throw new QpdfParseException("Duplicate --from is outside the narrow profile.", cursor, null);
                }

                seenFrom = true;
                from = TakeOverlayFrom(argv, ref cursor);
                OverlayRanges.ValidateSyntax(from, "--from", cursor);
                continue;
            }

            if (token == "--repeat")
            {
                throw new QpdfParseException("Option --repeat requires =range.", cursor, null);
            }

            if (token.StartsWith("--repeat=", StringComparison.Ordinal))
            {
                if (seenRepeat)
                {
                    throw new QpdfParseException("Duplicate --repeat is outside the narrow profile.", cursor, null);
                }

                seenRepeat = true;
                repeat = TakeOverlayRange(argv, ref cursor);
                OverlayRanges.ValidateSyntax(repeat, "--repeat", cursor);
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                ReadOnlySpan<char> name = OptionName(token);
                if (IsCryptoOption(name))
                {
                    throw new QpdfCryptoException($"--{name}");
                }

                throw new QpdfParseException("Unsupported option in " + role + " block.", cursor, null);
            }

            if (seenFile)
            {
                throw new QpdfParseException("Unexpected argument in " + role + " block.", cursor, null);
            }

            seenFile = true;
            file = token;
            cursor += 1;
        }

        if (cursor >= argv.Count)
        {
            throw new QpdfParseException("Missing -- terminator after " + role + " block.", cursor, null);
        }

        if (file is null)
        {
            throw new QpdfParseException("Option " + role + " requires a file.", cursor, null);
        }

        cursor += 1;
        return new OverlaySpec(file, to, from, repeat) { PasswordText = password };
    }

    private static string TakeOverlayRange(IReadOnlyList<string> argv, ref int cursor)
    {
        string token = argv[cursor];
        int equals = token.IndexOf("=", StringComparison.Ordinal);
        cursor += 1;
        return ConfigurationText.Slice(argv, token, equals + 1);
    }

    private static string TakeOverlayFrom(IReadOnlyList<string> argv, ref int cursor)
    {
        return TakeOverlayRange(argv, ref cursor);
    }

    internal static AttachmentCopy ParseCopyBlock(IReadOnlyList<string> argv, ref int cursor)
    {
        cursor += 1;
        if (cursor >= argv.Count || argv[cursor] is null || argv[cursor] == "--" || (argv[cursor].StartsWith("--", StringComparison.Ordinal) && argv[cursor].Length > 2))
        {
            throw new QpdfParseException("Option --copy-attachments-from requires a file.", cursor, null);
        }

        string file = argv[cursor];
        if (file.Length == 0)
        {
            throw new QpdfParseException("Option --copy-attachments-from requires a file.", cursor, null);
        }

        cursor += 1;
        string? prefix = null;
        string? copyPassword = null;
        while (cursor < argv.Count && argv[cursor] != "--")
        {
            string token = argv[cursor];
            if (token is null)
            {
                throw new QpdfParseException("Null argument.", cursor, null);
            }

            if (token == "--prefix" || token.StartsWith("--prefix=", StringComparison.Ordinal))
            {
                prefix = TakeParameter(argv, argv.Count, ref cursor, "--prefix");
                continue;
            }

            if (token == "--password") { throw new QpdfParseException("Attachment password must use --password=value.", cursor, null); }
            if (token.StartsWith("--password=", StringComparison.Ordinal))
            {
                copyPassword = ConfigurationText.Slice(argv, token, token.IndexOf('=') + 1);
                cursor++;
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                ReadOnlySpan<char> name = OptionName(token);
                if (IsCryptoOption(name))
                {
                    throw new QpdfCryptoException($"--{name}");
                }

                throw new QpdfParseException($"Unsupported option '--{ErrorPreview(name)}' in the narrow selection profile.", cursor, null);
            }

            throw new QpdfParseException($"Unexpected argument '{ErrorPreview(token)}' inside --copy-attachments-from.", cursor, null);
        }

        if (cursor >= argv.Count)
        {
            throw new QpdfParseException("Missing -- terminator after --copy-attachments-from.", cursor, null);
        }

        cursor += 1;
        return new AttachmentCopy(file, prefix is null || prefix.Length == 0 ? null : prefix) { PasswordText = copyPassword };
    }

    internal static AttachmentAdd ParseAddBlock(IReadOnlyList<string> argv, ref int cursor)
    {
        cursor += 1;
        if (cursor >= argv.Count || argv[cursor] is null || argv[cursor] == "--" || (argv[cursor].StartsWith("--", StringComparison.Ordinal) && argv[cursor].Length > 2))
        {
            throw new QpdfParseException("Option --add-attachment requires a file.", cursor, null);
        }

        string file = argv[cursor];
        if (file.Length == 0)
        {
            throw new QpdfParseException("Option --add-attachment requires a file.", cursor, null);
        }

        cursor += 1;
        string? key = null;
        string? fileName = null;
        string? creationDate = null;
        string? modDate = null;
        string? mimeType = null;
        string? description = null;
        bool replace = false;
        while (cursor < argv.Count && argv[cursor] != "--")
        {
            string token = argv[cursor];
            if (token is null)
            {
                throw new QpdfParseException("Null argument.", cursor, null);
            }

            if (token == "--key" || token.StartsWith("--key=", StringComparison.Ordinal)
                || token == "--filename" || token.StartsWith("--filename=", StringComparison.Ordinal)
                || token == "--creationdate" || token.StartsWith("--creationdate=", StringComparison.Ordinal)
                || token == "--moddate" || token.StartsWith("--moddate=", StringComparison.Ordinal)
                || token == "--mimetype" || token.StartsWith("--mimetype=", StringComparison.Ordinal)
                || token == "--description" || token.StartsWith("--description=", StringComparison.Ordinal))
            {
                ReadOnlySpan<char> name = OptionName(token);
                string value = TakeParameter(argv, argv.Count, ref cursor, $"--{name}");
                if (name is "key")
                {
                    key = value;
                }
                else if (name is "filename")
                {
                    fileName = value;
                }
                else if (name is "creationdate")
                {
                    creationDate = value;
                }
                else if (name is "moddate")
                {
                    modDate = value;
                }
                else if (name is "mimetype")
                {
                    mimeType = value;
                }
                else
                {
                    description = value;
                }

                continue;
            }

            if (token == "--replace")
            {
                replace = true;
                cursor += 1;
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                ReadOnlySpan<char> name = OptionName(token);
                if (IsCryptoOption(name))
                {
                    throw new QpdfCryptoException($"--{name}");
                }

                if (name is "pages" || name is "check" || name is "add-attachment" || name is "copy-attachments-from"
                    || name is "remove-attachment" || name is "list-attachments" || name is "prefix"
                    || name.StartsWith("show-attachment", StringComparison.Ordinal))
                {
                    throw new QpdfParseException($"Option '--{ErrorPreview(name)}' cannot appear inside --add-attachment.", cursor, null);
                }

                throw new QpdfParseException($"Unsupported option '--{ErrorPreview(name)}' in the narrow selection profile.", cursor, null);
            }

            throw new QpdfParseException($"Unexpected argument '{ErrorPreview(token)}' inside --add-attachment.", cursor, null);
        }

        if (cursor >= argv.Count)
        {
            throw new QpdfParseException("Missing -- terminator after --add-attachment.", cursor, null);
        }

        cursor += 1;
        if (key is not null && key.Length == 0)
        {
            throw new QpdfParseException("Option --key requires a non-empty key.", cursor, null);
        }

        if (fileName is not null && fileName.Length == 0)
        {
            throw new QpdfParseException("Option --filename requires a non-empty name.", cursor, null);
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

    private static int FindTerminator(IReadOnlyList<string> argv, int pagesAt)
    {
        for (int i = pagesAt + 1; i < argv.Count; i++)
        {
            if (argv[i] == "--")
            {
                return i;
            }
        }

        throw new QpdfParseException("Missing -- terminator after the --pages selection.", pagesAt, null);
    }

    private static string ParseInputArea(IReadOnlyList<string> argv, int boundaryAt, string operation, OperationBlocks? blocks)
    {
        string? input = null;
        for (int i = 0; i < boundaryAt; i++)
        {
            if (blocks is not null && TryParseOperationBlock(argv, ref i, blocks)) { i--; continue; }
            string token = argv[i];
            CheckInputToken(token, i, operation, blocks is not null);
            if (input is not null)
            {
                throw new QpdfParseException(token == "--empty" && input == JobPlan.EmptyDocumentInput
                    ? "Duplicate --empty is outside the narrow profile."
                    : $"A single positional input must precede {operation}.", i, null);
            }

            input = token;
        }

        if (input is null)
        {
            throw new QpdfParseException($"A positional input file is required before {operation}.", boundaryAt, null);
        }

        return input;
    }

    private static void CheckInputToken(string token, int index, string operation, bool allowEmpty)
    {
        if (token is null) { throw new QpdfParseException("Null argument.", index, null); }
        if (token == "--") { throw new QpdfParseException($"Misplaced -- terminator before {operation}.", index, null); }
        if (token == "--empty")
        {
            if (allowEmpty) { return; }
            throw new QpdfParseException("--empty is outside the narrow selection profile.", index, null);
        }
        if (token == "--replace-input") { throw new QpdfParseException("--replace-input is outside the narrow selection profile.", index, null); }
        if (token == "-") { throw new QpdfParseException("Standard input is not a seekable PDF input.", index, null); }
        if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2) { CheckOptionToken(token, index); }
    }

    internal static List<PageGroup> ParsePagesArea(IReadOnlyList<string> argv, int pagesAt, int terminatorAt)
    {
        List<PageGroup> groups = new();
        string? file = null;
        string? pagePassword = null;
        bool namedFile = false;
        int i = pagesAt + 1;
        while (i < terminatorAt)
        {
            string token = argv[i];
            if (token is null)
            {
                throw new QpdfParseException("Null argument.", i, null);
            }

            PagesArgumentBlock.CheckToken(token, i);

            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                ReadOnlySpan<char> name = OptionName(token);
                if (name is "password")
                {
                    string value = ConfigurationText.Slice(argv, token, token.IndexOf('=') + 1);
                    if (file is not null)
                    {
                        if (pagePassword is not null) { throw new QpdfParseException("Page password already specified for this file entry.", i, null); }
                        pagePassword = value;
                    }
                    else if (groups.Count > 0)
                    {
                        if (groups[^1].Password is not null) { throw new QpdfParseException("Page password already specified for this file entry.", i, null); }
                        groups[^1] = groups[^1] with { Password = value };
                    }
                    else { throw new QpdfParseException("In pages, --password must follow a file name.", i, null); }
                    i++;
                    continue;
                }
                if (IsCryptoOption(name))
                {
                    throw new QpdfCryptoException($"--{name}");
                }

                if (name is "file")
                {
                    if (file is not null)
                    {
                        groups.Add(new PageGroup(file, null) { Password = pagePassword });
                    }

                    file = TakeParameter(argv, terminatorAt, ref i, "--file");
                    pagePassword = null;
                    namedFile = true;
                    continue;
                }

                if (name is "range")
                {
                    if (file is null)
                    {
                        throw new QpdfParseException("--range must follow the file specification.", i, null);
                    }

                    string range = TakeParameter(argv, terminatorAt, ref i, "--range");
                    groups.Add(new PageGroup(file, range) { Password = pagePassword });
                    file = null;
                    pagePassword = null;
                    continue;
                }

                throw new QpdfParseException($"Unsupported option '--{ErrorPreview(name)}' in the narrow selection profile.", i, null);
            }

            if (file is null)
            {
                file = token;
                pagePassword = null;
                namedFile = false;
            }
            else if (namedFile)
            {
                groups.Add(new PageGroup(file, null) { Password = pagePassword });
                file = token;
                pagePassword = null;
                namedFile = false;
            }
            else
            {
                groups.Add(new PageGroup(file, token) { Password = pagePassword });
                file = null;
                pagePassword = null;
            }

            i += 1;
        }

        if (file is not null)
        {
            groups.Add(new PageGroup(file, null) { Password = pagePassword });
        }

        if (groups.Count == 0)
        {
            throw new QpdfParseException("The --pages selection requires one file.", pagesAt, null);
        }

        return groups;
    }

    private static JobPlan ParseReplace(
        string input,
        List<PageGroup> groups,
        QpdfRecoveryMode recovery,
        GlobalOptions globals)
    {
        if (groups.Count > 1 || globals.Collate.Count > 0
            || (groups.Count == 1 && (!SelectionPlan.IsPlainPageNumber(groups[0].Range)
                || (groups[0].File != "." && groups[0].File != input))))
        {
            return ParseMerge(input, input, groups, recovery, globals, false) with
            {
                ReplaceInput = true,
            };
        }

        int page = SelectionPlan.ParsePageNumber(groups[0].Range);
        return new SelectionPlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(input), page, recovery) with
        {
            WarningExitZero = globals.WarningExitZero,
            Verbose = globals.Verbose,
            Progress = globals.Progress,
            ReportMemoryUsage = globals.ReportMemoryUsage,
            IgnoreXrefStreams = globals.IgnoreXrefStreams,
            NoWarn = globals.NoWarn,
            Rotations = globals.Rotations,
            SplitPages = globals.SplitPages,
            ReplaceInput = true,
            MinVersion = globals.MinVersion,
            ForceVersion = globals.ForceVersion,
            StaticId = globals.StaticId,
            NewlineBeforeEndstream = globals.NewlineBeforeEndstream,
            ObjectStreams = globals.ObjectStreams,
            PreserveUnreferenced = globals.PreserveUnreferenced,
            StreamData = globals.StreamData,
            CompressStreams = globals.CompressStreams,
            DecodeLevel = globals.DecodeLevel,
            MaxStreamFilters = globals.MaxStreamFilters,
            ParserMaxNesting = globals.ParserMaxNesting,
            NoDefaultLimits = globals.NoDefaultLimits,
            MaxContainerSize = globals.MaxContainerSize,
            MaxContainerSizeDamaged = globals.MaxContainerSizeDamaged,
            ParserMaxErrors = globals.ParserMaxErrors,
            RecompressFlate = globals.RecompressFlate,
            CompressionLevel = globals.CompressionLevel,
            RemoveInfo = globals.RemoveInfo,
            RemoveAcroform = globals.RemoveAcroform,
            RemoveStructure = globals.RemoveStructure,
            RemoveMetadata = globals.RemoveMetadata,
            CoalesceContents = globals.CoalesceContents,
            NormalizeContent = globals.NormalizeContent,
            RemoveUnreferencedResources = globals.RemoveUnreferencedResources,
            ExternalizeInlineImages = globals.ExternalizeInlineImages,
            IiMinBytes = globals.IiMinBytes,
            FlattenRotation = globals.FlattenRotation,
            FlattenAnnotations = globals.FlattenAnnotations,
            GenerateAppearances = globals.GenerateAppearances,
            OptimizeImages = globals.OptimizeImages,
            OiMinWidth = globals.OiMinWidth,
            OiMinHeight = globals.OiMinHeight,
            OiMinArea = globals.OiMinArea,
            KeepInlineImages = globals.KeepInlineImages,
            JpegQuality = globals.JpegQuality,
            Qdf = globals.Qdf,
            Linearize = globals.Linearize,
            LinearizePass1 = ParseLinearizePass1(globals),
            NoOriginalObjectIds = globals.NoOriginalObjectIds,
        };
    }

    private static JobPlan ParseMerge(
        string input,
        string output,
        List<PageGroup> groups,
        QpdfRecoveryMode recovery,
        GlobalOptions globals,
        bool emptyInput)
    {
        CollationPreparation owner = globals.CollationStorage;
        List<int> sizes = globals.Collate;
        if (sizes.Count != 0 && sizes.Count != 1 && sizes.Count != groups.Count)
        {
            throw new QpdfParseException("--pages: if --collate has more than one value, it must have one value per page specification.", 0L, null);
        }

        List<PdfPageSource> sources = new(groups.Count);
        foreach (PageGroup group in groups)
        {
            sources.Add(new PdfPageSource(SelectionPlan.CheckedPath(group.File), PdfPageSelectionOptions.FromRange(group.Range)));
        }

        PdfPageCollation? collation = sizes.Count == 0 ? null
            : owner.CreatePolicy(sizes);
        return new MergePlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output), sources, collation, recovery) with
        {
            EmptyInput = emptyInput,
            WarningExitZero = globals.WarningExitZero,
            Verbose = globals.Verbose,
            Progress = globals.Progress,
            ReportMemoryUsage = globals.ReportMemoryUsage,
            IgnoreXrefStreams = globals.IgnoreXrefStreams,
            NoWarn = globals.NoWarn,
            Rotations = globals.Rotations,
            SplitPages = globals.SplitPages,
            MinVersion = globals.MinVersion,
            ForceVersion = globals.ForceVersion,
            StaticId = globals.StaticId,
            NewlineBeforeEndstream = globals.NewlineBeforeEndstream,
            ObjectStreams = globals.ObjectStreams,
            PreserveUnreferenced = globals.PreserveUnreferenced,
            StreamData = globals.StreamData,
            CompressStreams = globals.CompressStreams,
            DecodeLevel = globals.DecodeLevel,
            MaxStreamFilters = globals.MaxStreamFilters,
            ParserMaxNesting = globals.ParserMaxNesting,
            NoDefaultLimits = globals.NoDefaultLimits,
            MaxContainerSize = globals.MaxContainerSize,
            MaxContainerSizeDamaged = globals.MaxContainerSizeDamaged,
            ParserMaxErrors = globals.ParserMaxErrors,
            RecompressFlate = globals.RecompressFlate,
            CompressionLevel = globals.CompressionLevel,
            RemoveInfo = globals.RemoveInfo,
            RemoveAcroform = globals.RemoveAcroform,
            RemoveStructure = globals.RemoveStructure,
            RemoveMetadata = globals.RemoveMetadata,
            CoalesceContents = globals.CoalesceContents,
            NormalizeContent = globals.NormalizeContent,
            RemoveUnreferencedResources = globals.RemoveUnreferencedResources,
            ExternalizeInlineImages = globals.ExternalizeInlineImages,
            IiMinBytes = globals.IiMinBytes,
            FlattenRotation = globals.FlattenRotation,
            FlattenAnnotations = globals.FlattenAnnotations,
            GenerateAppearances = globals.GenerateAppearances,
            OptimizeImages = globals.OptimizeImages,
            OiMinWidth = globals.OiMinWidth,
            OiMinHeight = globals.OiMinHeight,
            OiMinArea = globals.OiMinArea,
            KeepInlineImages = globals.KeepInlineImages,
            JpegQuality = globals.JpegQuality,
            Qdf = globals.Qdf,
            Linearize = globals.Linearize,
            LinearizePass1 = ParseLinearizePass1(globals),
            NoOriginalObjectIds = globals.NoOriginalObjectIds,
        };
    }

    internal static List<int> ParseCollateValue(string? value, long offset, CollationPreparation owner)
        => ParseCollateValue(value.AsSpan(), offset, owner);

    private static List<int> ParseCollateValue(ReadOnlySpan<char> value, long offset, CollationPreparation owner)
    {
        List<int> sizes = owner.CreateVector();
        if (value.Length == 0)
        {
            owner.Add(sizes, 1);
            return sizes;
        }

        int start = 0;
        while (start <= value.Length)
        {
            int end = start;
            while (end < value.Length && value[end] != ',') { owner.Visit(); end++; }
            owner.Visit();
            ReadOnlySpan<char> item = value.Slice(start, end - start);
            if (item.Length == 0 || item.Length > 10)
            {
                throw new QpdfParseException($"Invalid --collate value '{ErrorPreview(value)}': expected n[,m,...].", offset, null);
            }

            long size = 0L;
            foreach (char digit in item)
            {
                if (digit < '0' || digit > '9')
                {
                    throw new QpdfParseException($"Invalid --collate value '{ErrorPreview(value)}': expected n[,m,...].", offset, null);
                }

                size = checked(size * 10L + (digit - '0'));
            }

            if (size > int.MaxValue)
            {
                throw new QpdfParseException($"Invalid --collate value '{ErrorPreview(value)}': group size is too large.", offset, null);
            }

            owner.Add(sizes, (int)size);
            if (end == value.Length) { break; }
            start = end + 1;
        }

        return sizes;
    }

    private static List<int> NewCollationVector(IReadOnlyList<string> argv)
        => ConfigurationText.OwnerOf(argv)?.Collation?.CreateVector() ?? new();

    private static WholeFilePlan NewWholeFilePlan(
        string input,
        string output,
        QpdfRecoveryMode recovery,
        GlobalOptions globals,
        AttachmentEdits edits,
        List<OverlaySpec> overlays,
        List<OverlaySpec> underlays)
    {
        return new WholeFilePlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output), recovery)
            {
                EmptyDocument = input == JobPlan.EmptyDocumentInput,
                WarningExitZero = globals.WarningExitZero,
                Verbose = globals.Verbose,
            Progress = globals.Progress,
                ReportMemoryUsage = globals.ReportMemoryUsage,
                IgnoreXrefStreams = globals.IgnoreXrefStreams,
                NoWarn = globals.NoWarn,
                Rotations = globals.Rotations,
                SplitPages = globals.SplitPages,
                MinVersion = globals.MinVersion,
                ForceVersion = globals.ForceVersion,
                StaticId = globals.StaticId,
                NewlineBeforeEndstream = globals.NewlineBeforeEndstream,
                ObjectStreams = globals.ObjectStreams,
                PreserveUnreferenced = globals.PreserveUnreferenced,
                StreamData = globals.StreamData,
                CompressStreams = globals.CompressStreams,
                DecodeLevel = globals.DecodeLevel,
                MaxStreamFilters = globals.MaxStreamFilters,
                ParserMaxNesting = globals.ParserMaxNesting,
                NoDefaultLimits = globals.NoDefaultLimits,
                MaxContainerSize = globals.MaxContainerSize,
                MaxContainerSizeDamaged = globals.MaxContainerSizeDamaged,
                ParserMaxErrors = globals.ParserMaxErrors,
                RecompressFlate = globals.RecompressFlate,
                CompressionLevel = globals.CompressionLevel,
                RemoveInfo = globals.RemoveInfo,
                RemoveAcroform = globals.RemoveAcroform,
            RemoveStructure = globals.RemoveStructure,
                RemoveMetadata = globals.RemoveMetadata,
                CoalesceContents = globals.CoalesceContents,
                NormalizeContent = globals.NormalizeContent,
                RemoveUnreferencedResources = globals.RemoveUnreferencedResources,
                ExternalizeInlineImages = globals.ExternalizeInlineImages,
                IiMinBytes = globals.IiMinBytes,
            FlattenRotation = globals.FlattenRotation,
            FlattenAnnotations = globals.FlattenAnnotations,
            GenerateAppearances = globals.GenerateAppearances,
            OptimizeImages = globals.OptimizeImages,
            OiMinWidth = globals.OiMinWidth,
            OiMinHeight = globals.OiMinHeight,
            OiMinArea = globals.OiMinArea,
            KeepInlineImages = globals.KeepInlineImages,
            JpegQuality = globals.JpegQuality,
            Qdf = globals.Qdf,
            Linearize = globals.Linearize,
            LinearizePass1 = ParseLinearizePass1(globals),
            NoOriginalObjectIds = globals.NoOriginalObjectIds,
                AttachmentsToAdd = edits.Adds,
                AttachmentsToRemove = edits.Removes,
                AttachmentsToCopy = edits.Copies,
                Overlays = overlays,
                Underlays = underlays,
            };
        }
    private static string TakeParameter(IReadOnlyList<string> argv, int terminatorAt, ref int i, string option)
    {
        string token = argv[i];
        int equals = token.IndexOf('=');
        if (equals >= 0)
        {
            string inline = ConfigurationText.Slice(argv, token, equals + 1);
            if (inline.Length == 0)
            {
                throw new QpdfParseException($"Empty parameter for {option}.", i, null);
            }

            i += 1;
            return inline;
        }

        if (i + 1 >= terminatorAt)
        {
            throw new QpdfParseException($"Missing parameter for {option}.", i, null);
        }

        string next = argv[i + 1];
        if (next is null || next == "--" || (next.StartsWith("--", StringComparison.Ordinal) && next.Length > 2))
        {
            throw new QpdfParseException($"Missing parameter for {option}.", i, null);
        }

        i += 2;
        return next;
    }

    private static string ParseOutputArea(IReadOnlyList<string> argv, int terminatorAt, OperationBlocks? blocks)
    {
        string? output = null;
        for (int i = terminatorAt + 1; i < argv.Count; i++)
        {
            if (blocks is not null && TryParseOperationBlock(argv, ref i, blocks)) { i--; continue; }
            string token = argv[i];
            if (token is null)
            {
                throw new QpdfParseException("Null argument.", i, null);
            }

            if (token == "--")
            {
                throw new QpdfParseException("Unexpected -- terminator.", i, null);
            }

            if (token == "--replace-input")
            {
                throw new QpdfParseException("--replace-input is outside the narrow selection profile.", i, null);
            }

            if (token == "-")
            {
                throw new QpdfParseException("Stdout output requires an explicit streaming contract.", i, null);
            }

            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                CheckOptionToken(token, i);
            }

            if (output is not null)
            {
                throw new QpdfParseException("A single positional output must follow --.", i, null);
            }

            output = token;
        }

        if (output is null)
        {
            throw new QpdfParseException("A positional output file is required after --.", terminatorAt, null);
        }

        return output;
    }

}
