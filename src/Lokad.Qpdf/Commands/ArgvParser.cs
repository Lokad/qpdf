namespace Lokad.Qpdf.Commands;

/// <summary>
/// Narrow argv parser for selection and inspection. Accepts the upstream
/// spelling subset `qpdf input --pages [--file=]file [--range=]n -- output`
/// (positional file/range forms included) and `qpdf input --check`;
/// `--suppress-recovery` selects strict opening, otherwise damaged inputs
/// repair within bounds; everything else fails explicitly.
/// Unknown `--*` tokens map to the documented crypto set (rejected as crypto)
/// or to unsupported-option errors. Writer controls cover versions, IDs,
/// and stream newlines; object-stream modes arrive later.
/// </summary>
internal static class ArgvParser
{
    private static readonly HashSet<string> CryptoOptions = new(StringComparer.Ordinal)
    {
        "password", "password-file", "password-mode", "password-is-hex-key",
        "suppress-password-recovery", "encryption-file-password", "user-password",
        "owner-password", "bits", "allow-weak-crypto", "allow-insecure", "use-aes",
        "force-V4", "force-R5", "cleartext-metadata", "static-aes-iv",
        "accessibility", "annotate", "assemble", "extract", "form", "modify-other",
        "modify", "print", "show-crypto", "show-encryption", "show-encryption-key",
        "is-encrypted", "requires-password", "remove-restrictions", "encrypt",
        "decrypt", "copy-encryption",
    };

    internal static JobPlan Parse(IReadOnlyList<string> argv)
    {
        if (argv is null)
        {
            throw new ArgumentNullException(nameof(argv));
        }

        if (argv.Count == 0)
        {
            throw new QpdfParseException("No command arguments were supplied.", 0L, null);
        }

        GlobalOptions globals = ExtractGlobalOptions(ref argv);
        QpdfRecoveryMode recovery = globals.Recovery;
        int checkAt = FindFlag(argv, "--check");
        int pagesAt = FindFlag(argv, "--pages");
        int listAt = FindFlag(argv, "--list-attachments");
        (int firstShowAt, int showAt, string? showKey) = FindShowAttachment(argv);
        int firstAttachmentAt = listAt >= 0 ? (firstShowAt >= 0 ? Math.Min(listAt, firstShowAt) : listAt) : firstShowAt;
        int firstOverlayAt = FirstOverlayBlock(argv, 0, argv.Count);
        int jsonAt = FindJson(argv);
        int jsonOutputAt = FindJsonOutput(argv);
        int jsonEntryAt = jsonAt >= 0 && jsonOutputAt >= 0 ? (jsonAt < jsonOutputAt ? jsonAt : jsonOutputAt) : (jsonAt >= 0 ? jsonAt : jsonOutputAt);
        int jsonInputAt = FindFlag(argv, "--json-input");
        int updateFromJsonAt = FindUpdateFromJson(argv);
        int jsonImportAt = jsonInputAt >= 0 && updateFromJsonAt >= 0 ? (jsonInputAt < updateFromJsonAt ? jsonInputAt : updateFromJsonAt) : (jsonInputAt >= 0 ? jsonInputAt : updateFromJsonAt);
        if (jsonEntryAt >= 0)
        {
            if (checkAt >= 0 || pagesAt >= 0)
            {
                throw new QpdfParseException("Option --json cannot be combined with --check or --pages.", jsonEntryAt, null);
            }

            if (listAt >= 0 || showAt >= 0)
            {
                throw new QpdfParseException("Option --json cannot be combined with attachment inspection.", jsonEntryAt, null);
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
            if (checkAt >= 0 || pagesAt >= 0)
            {
                throw new QpdfParseException("JSON import cannot be combined with --check or --pages.", jsonImportAt, null);
            }

            if (listAt >= 0 || showAt >= 0)
            {
                throw new QpdfParseException("JSON import cannot be combined with attachment inspection.", jsonImportAt, null);
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

        if (listAt >= 0 || showAt >= 0)
        {
            if (checkAt >= 0 || pagesAt >= 0)
            {
                throw new QpdfParseException("Options --list-attachments and --show-attachment cannot be combined with --check or --pages.", showAt >= 0 ? showAt : listAt, null);
            }

            if (HasAttachmentEdit(argv, 0, argv.Count))
            {
                throw new QpdfParseException("Attachment inspection cannot be combined with attachment editing.", showAt >= 0 ? showAt : listAt, null);
            }

            if (firstOverlayAt >= 0)
            {
                throw new QpdfParseException("Attachment inspection cannot be combined with --overlay or --underlay.", showAt >= 0 ? showAt : listAt, null);
            }


            if (globals.FlattenRotation)
            {
                throw new QpdfParseException("Attachment inspection cannot be combined with --flatten-rotation.", showAt >= 0 ? showAt : listAt, null);
            }

            if (globals.SplitPages.HasValue || globals.ReplaceInput)
            {
                throw new QpdfParseException("Options --split-pages and --replace-input need a --pages selection.", firstAttachmentAt, null);
            }

            return ParseAttachments(argv, listAt, firstAttachmentAt, showKey, recovery, globals);
        }
        if (checkAt >= 0 && pagesAt >= 0)
        {
            throw new QpdfParseException("Options --check and --pages cannot be combined.", checkAt, null);
        }
        if (checkAt >= 0 && firstOverlayAt >= 0)
        {
            throw new QpdfParseException("Options --check and --overlay or --underlay cannot be combined.", checkAt, null);
        }
        if (checkAt >= 0 && globals.FlattenRotation)
        {
            throw new QpdfParseException("Options --check and --flatten-rotation cannot be combined.", checkAt, null);
        }

        if (checkAt >= 0)
        {
            if (globals.SplitPages.HasValue || globals.ReplaceInput)
            {
                throw new QpdfParseException("Options --split-pages and --replace-input need a --pages selection.", checkAt, null);
            }

            return ParseCheck(argv, checkAt, recovery) with
            {
                WarningExitZero = globals.WarningExitZero,
                NoWarn = globals.NoWarn,
                MinVersion = globals.MinVersion,
                ForceVersion = globals.ForceVersion,
                DeterministicId = globals.DeterministicId,
                StaticId = globals.StaticId,
                NewlineBeforeEndstream = globals.NewlineBeforeEndstream,
            ObjectStreams = globals.ObjectStreams,
            PreserveUnreferenced = globals.PreserveUnreferenced,
            StreamData = globals.StreamData,
            CompressStreams = globals.CompressStreams,
            DecodeLevel = globals.DecodeLevel,
            RecompressFlate = globals.RecompressFlate,
            CompressionLevel = globals.CompressionLevel,
            RemoveInfo = globals.RemoveInfo,
            RemoveMetadata = globals.RemoveMetadata,
            CoalesceContents = globals.CoalesceContents,
            NormalizeContent = globals.NormalizeContent,
            RemoveUnreferencedResources = globals.RemoveUnreferencedResources,
            ExternalizeInlineImages = globals.ExternalizeInlineImages,
            IiMinBytes = globals.IiMinBytes,
            };
        }

        if (pagesAt < 0 && !HasAttachmentEdit(argv, 0, argv.Count) && firstOverlayAt < 0)
        {
            throw new QpdfParseException("No operation: the narrow profile requires --pages with one file and range, --check, --json, or attachment editing.", 0L, null);
        }

        string input;
        List<PageGroup> groups = new();
        List<OutputRotation> rotations = new();
        List<PageLabelSpec> labels = new();
        List<int> collate = new();
        int cursor;
        if (pagesAt >= 0)
        {
            int blockAt = FirstAttachmentEdit(argv, pagesAt + 1, argv.Count);
            int overlayAt = FirstOverlayBlock(argv, pagesAt + 1, argv.Count);
            if (globals.ReplaceInput)
            {
                int areaEnd = argv.Count;
                if (blockAt >= 0 && blockAt < areaEnd)
                {
                    areaEnd = blockAt;
                }
                if (overlayAt >= 0 && overlayAt < areaEnd)
                {
                    areaEnd = overlayAt;
                }
                for (int i = pagesAt + 1; i < areaEnd; i++)
                {
                    if (argv[i] == "--")
                    {
                        throw new QpdfParseException("No output file may be given with --replace-input.", pagesAt, null);
                    }
                }

                input = ParseInputArea(argv, pagesAt, "--pages");
                (groups, rotations, labels, collate) = ParsePagesArea(argv, pagesAt, areaEnd);
                cursor = areaEnd;
            }
            else
            {
                int termAt = FindTerminator(argv, pagesAt);
                input = ParseInputArea(argv, pagesAt, "--pages");
                int areaEnd = termAt;
                if (blockAt >= 0 && blockAt < areaEnd)
                {
                    areaEnd = blockAt;
                }
                if (overlayAt >= 0 && overlayAt < areaEnd)
                {
                    areaEnd = overlayAt;
                }
                (groups, rotations, labels, collate) = ParsePagesArea(argv, pagesAt, areaEnd);
                cursor = areaEnd;
            }
        }
        else
        {
            if (globals.ReplaceInput)
            {
                throw new QpdfParseException("No operation: the narrow profile requires --pages with one file and range, --check, --json, or attachment editing.", 0L, null);
            }

            int blockAt = FirstAttachmentEdit(argv, 0, argv.Count);
            int overlayAt = FirstOverlayBlock(argv, 0, argv.Count);
            int blockOrOverlay = blockAt;
            if (overlayAt >= 0 && (blockOrOverlay < 0 || overlayAt < blockOrOverlay))
            {
                blockOrOverlay = overlayAt;
            }
            input = ParseInputArea(argv, blockOrOverlay, "attachment editing");
            cursor = blockOrOverlay;
        }

        TrailingBlocks trailing = ParseTrailingBlocks(argv, ref cursor);
        AttachmentEdits edits = trailing.Edits;
        List<OverlaySpec> overlays = trailing.Overlays;
        List<OverlaySpec> underlays = trailing.Underlays;
        if (globals.ReplaceInput)
        {
            if (cursor != argv.Count)
            {
                throw new QpdfParseException("No output file may be given with --replace-input.", pagesAt, null);
            }

            return ParseReplace(input, groups, rotations, labels, collate, recovery, globals) with
            {
                AttachmentsToAdd = edits.Adds,
                AttachmentsToRemove = edits.Removes,
                AttachmentsToCopy = edits.Copies,
                Overlays = overlays,
                Underlays = underlays,
            };
        }

        if (cursor >= argv.Count || argv[cursor] != "--")
        {
            throw new QpdfParseException("An output file is required after --.", cursor, null);
        }

        string output = ParseOutputArea(argv, cursor);
        if (groups.Count > 1 || collate.Count > 0)
        {
            return ParseMerge(input, output, groups, rotations, labels, collate, recovery, globals) with
            {
                AttachmentsToAdd = edits.Adds,
                AttachmentsToRemove = edits.Removes,
                AttachmentsToCopy = edits.Copies,
                Overlays = overlays,
                Underlays = underlays,
            };
        }

        if (groups.Count == 0)
        {
            return new WholeFilePlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output), recovery) with
            {
                WarningExitZero = globals.WarningExitZero,
                NoWarn = globals.NoWarn,
                Rotations = rotations,
                Labels = labels,
                SplitPages = globals.SplitPages,
                MinVersion = globals.MinVersion,
                ForceVersion = globals.ForceVersion,
                DeterministicId = globals.DeterministicId,
                StaticId = globals.StaticId,
                NewlineBeforeEndstream = globals.NewlineBeforeEndstream,
                ObjectStreams = globals.ObjectStreams,
                PreserveUnreferenced = globals.PreserveUnreferenced,
                StreamData = globals.StreamData,
                CompressStreams = globals.CompressStreams,
                DecodeLevel = globals.DecodeLevel,
                RecompressFlate = globals.RecompressFlate,
                CompressionLevel = globals.CompressionLevel,
                RemoveInfo = globals.RemoveInfo,
                RemoveMetadata = globals.RemoveMetadata,
                CoalesceContents = globals.CoalesceContents,
                NormalizeContent = globals.NormalizeContent,
                RemoveUnreferencedResources = globals.RemoveUnreferencedResources,
                ExternalizeInlineImages = globals.ExternalizeInlineImages,
                IiMinBytes = globals.IiMinBytes,
            FlattenRotation = globals.FlattenRotation,
                AttachmentsToAdd = edits.Adds,
                AttachmentsToRemove = edits.Removes,
                AttachmentsToCopy = edits.Copies,
                Overlays = overlays,
                Underlays = underlays,
            };
        }

        string file = groups[0].File;
        string range = groups[0].Range;
        string resolvedFile = ResolveFile(file, input);
        int page = SelectionPlan.ParsePageNumber(range);
        return new SelectionPlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output), page, recovery) with
        {
            WarningExitZero = globals.WarningExitZero,
            NoWarn = globals.NoWarn,
            Rotations = rotations,
            Labels = labels,
            SplitPages = globals.SplitPages,
            MinVersion = globals.MinVersion,
            ForceVersion = globals.ForceVersion,
            DeterministicId = globals.DeterministicId,
            StaticId = globals.StaticId,
            NewlineBeforeEndstream = globals.NewlineBeforeEndstream,
            ObjectStreams = globals.ObjectStreams,
            PreserveUnreferenced = globals.PreserveUnreferenced,
            StreamData = globals.StreamData,
            CompressStreams = globals.CompressStreams,
            DecodeLevel = globals.DecodeLevel,
            RecompressFlate = globals.RecompressFlate,
            CompressionLevel = globals.CompressionLevel,
            RemoveInfo = globals.RemoveInfo,
            RemoveMetadata = globals.RemoveMetadata,
            CoalesceContents = globals.CoalesceContents,
            NormalizeContent = globals.NormalizeContent,
            RemoveUnreferencedResources = globals.RemoveUnreferencedResources,
            ExternalizeInlineImages = globals.ExternalizeInlineImages,
            IiMinBytes = globals.IiMinBytes,
            FlattenRotation = globals.FlattenRotation,
            AttachmentsToAdd = edits.Adds,
            AttachmentsToRemove = edits.Removes,
            AttachmentsToCopy = edits.Copies,
                Overlays = overlays,
                Underlays = underlays,
        };
    }

    internal static string OptionName(string token)
    {
        int equals = token.IndexOf('=');
        return equals < 0 ? token.Substring(2) : token.Substring(2, equals - 2);
    }

    internal static void CheckOptionToken(string token, long index)
    {
        string name = OptionName(token);
        if (CryptoOptions.Contains(name))
        {
            throw new QpdfCryptoException("--" + name);
        }

        throw new QpdfParseException($"Unsupported option '--{name}' in the narrow selection profile.", index, null);
    }

    private static (int FirstAt, int At, string? Key) FindShowAttachment(IReadOnlyList<string> argv)
    {
        int firstAt = -1;
        int at = -1;
        string? key = null;
        for (int i = 0; i < argv.Count; i++)
        {
            string token = argv[i];
            if (token is null)
            {
                throw new QpdfParseException("Null argument.", i, null);
            }

            if (token == "--show-attachment")
            {
                throw new QpdfParseException("Option --show-attachment requires =key.", i, null);
            }

            if (token.StartsWith("--show-attachment=", StringComparison.Ordinal))
            {
                if (firstAt < 0)
                {
                    firstAt = i;
                }

                at = i;
                key = token.Substring("--show-attachment=".Length);
            }
        }

        if (at >= 0 && (key is null || key.Length == 0))
        {
            throw new QpdfParseException("Option --show-attachment requires a non-empty key.", at, null);
        }

        return (firstAt, at, key);
    }

    private static JobPlan ParseAttachments(
        IReadOnlyList<string> argv,
        int listAt,
        int firstAt,
        string? showKey,
        QpdfRecoveryMode recovery,
        GlobalOptions globals)
    {

        for (int i = 0; i < firstAt; i++)
        {
            if (argv[i] == "--")
            {
                throw new QpdfParseException("Misplaced -- terminator.", i, null);
            }
        }

        string input = ParseInputArea(argv, firstAt, "attachment inspection");
        if (showKey is not null)
        {
            int termAt = -1;
            for (int i = firstAt; i < argv.Count; i++)
            {
                if (argv[i] == "--")
                {
                    termAt = i;
                    break;
                }
            }

            ValidateAttachmentFlags(argv, firstAt, termAt < 0 ? argv.Count : termAt);
            if (termAt < 0)
            {
                throw new QpdfParseException("An output file is required after --.", firstAt, null);
            }

            string output = ParseOutputArea(argv, termAt);
            if (string.Equals(input, output, StringComparison.Ordinal))
            {
                throw new QpdfParseException("The output file must differ from the input file.", termAt, null);
            }

            return new AttachmentPlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output), showKey, listAt >= 0, recovery) with
            {
                WarningExitZero = globals.WarningExitZero,
                NoWarn = globals.NoWarn,
            };
        }

        ValidateAttachmentFlags(argv, firstAt, argv.Count);
        foreach (string token in argv)
        {
            if (token == "--")
            {
                throw new QpdfParseException("No output file may be given with --list-attachments.", listAt, null);
            }
        }

        return new AttachmentPlan(SelectionPlan.CheckedPath(input), null, null, true, recovery) with
        {
            WarningExitZero = globals.WarningExitZero,
            NoWarn = globals.NoWarn,
        };
    }

    private static void ValidateAttachmentFlags(IReadOnlyList<string> argv, int first, int end)
    {
        for (int i = first; i < end; i++)
        {
            string token = argv[i];
            if (token is null)
            {
                throw new QpdfParseException("Null argument.", i, null);
            }

            if (token == "--list-attachments" || token.StartsWith("--show-attachment=", StringComparison.Ordinal))
            {
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                CheckOptionToken(token, i);
            }
            else
            {
                throw new QpdfParseException($"Unexpected argument '{token}' in the attachment options.", i, null);
            }
        }
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

    private sealed record GlobalOptions(QpdfRecoveryMode Recovery, bool WarningExitZero, bool NoWarn, int? SplitPages, bool ReplaceInput, string? MinVersion, string? ForceVersion, bool DeterministicId, bool StaticId, bool NewlineBeforeEndstream, string? ObjectStreams, bool PreserveUnreferenced, string? StreamData, string? CompressStreams, string? DecodeLevel, bool RecompressFlate, string? CompressionLevel, bool RemoveInfo, bool RemoveMetadata, bool CoalesceContents, bool NormalizeContent, string? RemoveUnreferencedResources, bool ExternalizeInlineImages, long IiMinBytes, bool FlattenRotation);

    private static GlobalOptions ExtractGlobalOptions(ref IReadOnlyList<string> argv)
    {
        List<string> kept = new(argv.Count);
        QpdfRecoveryMode recovery = QpdfRecoveryMode.BoundedRepair;
        bool warningExitZero = false;
        bool noWarn = false;
        bool seenSuppress = false;
        bool seenWarningExitZero = false;
        bool seenNoWarn = false;
        bool seenSplit = false;
        bool seenReplace = false;
        int? splitPages = null;
        bool replaceInput = false;
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
        bool seenStreamData = false;
        string? streamData = null;
        bool seenCompressStreams = false;
        string? compressStreams = null;
        bool seenDecodeLevel = false;
        string? decodeLevel = null;
        bool seenRecompressFlate = false;
        bool recompressFlate = false;
        bool seenCompressionLevel = false;
        string? compressionLevel = null;
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
        bool seenExternalizeInlineImages = false;
        bool externalizeInlineImages = false;
        bool seenIiMinBytes = false;
        long iiMinBytes = 1024L;
        bool seenFlattenRotation = false;
        bool flattenRotation = false;
        bool deterministicId = false;
        bool staticId = false;
        bool newlineBeforeEndstream = false;
        for (int i = 0; i < argv.Count; i++)
        {
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
                deterministicId = true;
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
                if (seenStreamData)
                {
                    throw new QpdfParseException("Duplicate --stream-data is outside the narrow profile.", i, null);
                }

                seenStreamData = true;
                streamData = ParseStreamChoice(argv, ref i, "--stream-data", new string[] { "compress", "preserve", "uncompress" });
                continue;
            }

            if (argv[i] == "--compress-streams" || argv[i].StartsWith("--compress-streams=", StringComparison.Ordinal))
            {
                if (seenCompressStreams)
                {
                    throw new QpdfParseException("Duplicate --compress-streams is outside the narrow profile.", i, null);
                }

                seenCompressStreams = true;
                compressStreams = ParseOptionalChoice(argv, ref i, "--compress-streams", new string[] { "y", "n" }, "y");
                continue;
            }

            if (argv[i] == "--decode-level" || argv[i].StartsWith("--decode-level=", StringComparison.Ordinal))
            {
                if (seenDecodeLevel)
                {
                    throw new QpdfParseException("Duplicate --decode-level is outside the narrow profile.", i, null);
                }

                seenDecodeLevel = true;
                decodeLevel = ParseStreamChoice(argv, ref i, "--decode-level", new string[] { "none", "generalized", "specialized", "all" });
                continue;
            }

            if (argv[i] == "--recompress-flate")
            {
                if (seenRecompressFlate)
                {
                    throw new QpdfParseException("Duplicate --recompress-flate is outside the narrow profile.", i, null);
                }

                seenRecompressFlate = true;
                recompressFlate = true;
                continue;
            }

            if (argv[i].StartsWith("--recompress-flate=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --recompress-flate takes no value.", i, null);
            }

            if (argv[i] == "--compression-level" || argv[i].StartsWith("--compression-level=", StringComparison.Ordinal))
            {
                if (seenCompressionLevel)
                {
                    throw new QpdfParseException("Duplicate --compression-level is outside the narrow profile.", i, null);
                }

                seenCompressionLevel = true;
                compressionLevel = ParseRequiredChoiceValue(argv, ref i, "--compression-level");
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

            kept.Add(argv[i]);
        }

        argv = kept;
        return new GlobalOptions(recovery, warningExitZero, noWarn, splitPages, replaceInput, minVersion, forceVersion, deterministicId, staticId, newlineBeforeEndstream, objectStreams, preserveUnreferenced, streamData, compressStreams, decodeLevel, recompressFlate, compressionLevel, removeInfo, removeMetadata, coalesceContents, normalizeContent, removeUnreferencedResources, externalizeInlineImages, iiMinBytes, flattenRotation);
    }

    private static long ParseIiMinBytes(IReadOnlyList<string> argv, ref int i)
    {
        string token = argv[i];
        int equals = token.IndexOf((char)61);
        string? value = null;
        if (equals >= 0)
        {
            value = token.Substring(equals + 1);
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
            throw new QpdfParseException($"Invalid --ii-min-bytes value '{value}' : expected a non-negative byte count.", i, null);
        }

        long size = 0L;
        foreach (char digit in value)
        {
            if (digit < (char)48 || digit > (char)57)
            {
                throw new QpdfParseException($"Invalid --ii-min-bytes value '{value}' : expected a non-negative byte count.", i, null);
            }

            size = checked(size * 10L + (digit - (char)48));
        }

        return size;
    }

    private static int ParseSplitPages(IReadOnlyList<string> argv, ref int i)
    {
        string token = argv[i];
        int equals = token.IndexOf('=');
        string? value = null;
        if (equals >= 0)
        {
            value = token.Substring(equals + 1);
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

        if (value is null)
        {
            return 1;
        }

        if (value.Length == 0 || value.Length > 10)
        {
            throw new QpdfParseException($"Invalid --split-pages value '{value}' : expected a positive group size.", i, null);
        }

        long size = 0L;
        foreach (char digit in value)
        {
            if (digit < '0' || digit > '9')
            {
                throw new QpdfParseException($"Invalid --split-pages value '{value}' : expected a positive group size.", i, null);
            }

            size = checked(size * 10L + (digit - '0'));
        }

        if (size < 1L || size > int.MaxValue)
        {
            throw new QpdfParseException($"Invalid --split-pages value '{value}' : expected a positive group size.", i, null);
        }

        return (int)size;
    }

    private static string ParseObjectStreamsMode(IReadOnlyList<string> argv, ref int i)
    {
        string token = argv[i];
        int equals = token.IndexOf((char)61);
        string? value = null;
        if (equals >= 0)
        {
            value = token.Substring(equals + 1);
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

    private static string ParseOptionalChoice(IReadOnlyList<string> argv, ref int i, string option, string[] choices, string bareDefault)
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
            return token.Substring(equals + 1);
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
            value = token.Substring(equals + 1);
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

    private static CheckPlan ParseCheck(IReadOnlyList<string> argv, int checkAt, QpdfRecoveryMode recovery)
    {
        string input = ParseInputArea(argv, checkAt, "--check");
        if (checkAt + 1 >= argv.Count)
        {
            return new CheckPlan(SelectionPlan.CheckedPath(input), recovery);
        }

        string? token = argv[checkAt + 1];
        if (token is null)
        {
            throw new QpdfParseException("Null argument.", checkAt + 1, null);
        }

        throw new QpdfParseException($"Unexpected argument '{token}' after --check; inspection takes no output file.", checkAt + 1, null);
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
        string input = ParseInputArea(argv, entryAt, "--json");
        int cursor = entryAt;
        bool seenJsonVersion = false;
        int jsonVersion = 0;
        List<string> keys = new();
        List<string> objects = new();
        string? streamData = null;
        bool seenStreamData = false;
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
                objects.AddRange(Qpdf.ValidateJsonObjects([selector], cursor));
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
        if (cursor >= argv.Count || argv[cursor] != "--")
        {
            throw new QpdfParseException("An output file is required after --.", cursor, null);
        }

        string output = ParseOutputArea(argv, cursor);
        return new JsonPlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output), version, keys, recovery) with
        {
            DecodeLevel = globals.DecodeLevel ?? (minimalOutput ? "none" : "generalized"),
            StreamDataMode = seenStreamData ? streamData! : (minimalOutput ? "inline" : "none"),
            StreamPrefix = streamPrefix,
            Objects = objects,
            MinimalOutput = minimalOutput,
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
            || globals.DecodeLevel is not null || globals.RecompressFlate || globals.CompressionLevel is not null
            || globals.RemoveInfo || globals.RemoveMetadata || globals.PreserveUnreferenced)
        {
            throw new QpdfParseException("Stream and metadata options cannot be combined with JSON import.", entryAt, null);
        }

        if (updateAt >= 0)
        {
            string pdfInput = ParseInputArea(argv, entryAt, "--update-from-json");
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

            string updateOutput = ParseOutputArea(argv, updateCursor);
            return new JsonUpdatePlan(SelectionPlan.CheckedPath(pdfInput), SelectionPlan.CheckedPath(updateFile), SelectionPlan.CheckedPath(updateOutput), recovery) with
            {
                WarningExitZero = globals.WarningExitZero,
                NoWarn = globals.NoWarn,
                MinVersion = globals.MinVersion,
                ForceVersion = globals.ForceVersion,
                DeterministicId = globals.DeterministicId,
                StaticId = globals.StaticId,
                NewlineBeforeEndstream = globals.NewlineBeforeEndstream,
            };
        }

        string input = ParseInputArea(argv, entryAt, "--json-input");
        int cursor = entryAt + 1;
        if (cursor >= argv.Count || argv[cursor] != "--")
        {
            if (cursor < argv.Count && argv[cursor] is not null && argv[cursor] != "--")
            {
                throw new QpdfParseException("Unexpected argument '" + argv[cursor] + "' after --json-input.", cursor, null);
            }

            throw new QpdfParseException("An output file is required after --.", cursor, null);
        }

        string output = ParseOutputArea(argv, cursor);
        return new JsonInputPlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output), recovery) with
        {
            WarningExitZero = globals.WarningExitZero,
            NoWarn = globals.NoWarn,
            MinVersion = globals.MinVersion,
            ForceVersion = globals.ForceVersion,
            DeterministicId = globals.DeterministicId,
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

    private sealed record TrailingBlocks(AttachmentEdits Edits, List<OverlaySpec> Overlays, List<OverlaySpec> Underlays);

    private static TrailingBlocks ParseTrailingBlocks(IReadOnlyList<string> argv, ref int cursor)
    {
        List<AttachmentAdd> adds = new();
        List<string> removes = new();
        List<AttachmentCopy> copies = new();
        List<OverlaySpec> overlays = new();
        List<OverlaySpec> underlays = new();
        while (cursor < argv.Count)
        {
            string token = argv[cursor];
            if (token is null)
            {
                throw new QpdfParseException("Null argument.", cursor, null);
            }

            if (token == "--")
            {
                if (cursor + 1 < argv.Count && IsTrailingBlockStart(argv[cursor + 1]))
                {
                    cursor += 1;
                    continue;
                }

                break;
            }

            if (token == "--overlay" || token == "--underlay")
            {
                bool underlay = token == "--underlay";
                OverlaySpec spec = ParseOverlayBlock(argv, ref cursor, underlay);
                if (underlay)
                {
                    underlays.Add(spec);
                }
                else
                {
                    overlays.Add(spec);
                }

                continue;
            }

            if (token == "--add-attachment")
            {
                adds.Add(ParseAddBlock(argv, ref cursor));
                continue;
            }

            if (token.StartsWith("--remove-attachment=", StringComparison.Ordinal))
            {
                string key = token.Substring("--remove-attachment=".Length);
                if (key.Length == 0)
                {
                    throw new QpdfParseException("Option --remove-attachment requires a non-empty key.", cursor, null);
                }

                removes.Add(key);
                cursor += 1;
                continue;
            }

            if (token == "--remove-attachment")
            {
                throw new QpdfParseException("Option --remove-attachment requires =key.", cursor, null);
            }

            if (token == "--copy-attachments-from")
            {
                copies.Add(ParseCopyBlock(argv, ref cursor));
                continue;
            }

            if (token.StartsWith("--copy-attachments-from=", StringComparison.Ordinal))
            {
                throw new QpdfParseException("Option --copy-attachments-from takes no value.", cursor, null);
            }

            if ((token.StartsWith("--overlay=", StringComparison.Ordinal) || token.StartsWith("--underlay=", StringComparison.Ordinal))
                && token.Length > 2)
            {
                throw new QpdfParseException("Option " + OptionName(token) + " takes no value.", cursor, null);
            }

            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                CheckOptionToken(token, cursor);
                continue;
            }

            throw new QpdfParseException("Unexpected argument after the page selection.", cursor, null);
        }

        return new TrailingBlocks(new AttachmentEdits(adds, removes, copies), overlays, underlays);
    }

    private static OverlaySpec ParseOverlayBlock(IReadOnlyList<string> argv, ref int cursor, bool underlay)
    {
        string role = underlay ? "--underlay" : "--overlay";
        cursor += 1;
        string? file = null;
        string? to = null;
        string? from = null;
        string? repeat = null;
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

            if (token == "--file" || token.StartsWith("--file=", StringComparison.Ordinal))
            {
                if (seenFile)
                {
                    throw new QpdfParseException("Duplicate --file is outside the narrow profile.", cursor, null);
                }

                seenFile = true;
                file = TakeParameter(argv, argv.Count, ref cursor, "--file");
                continue;
            }

            if (token == "--to" || token.StartsWith("--to=", StringComparison.Ordinal))
            {
                if (seenTo)
                {
                    throw new QpdfParseException("Duplicate --to is outside the narrow profile.", cursor, null);
                }

                seenTo = true;
                to = TakeParameter(argv, argv.Count, ref cursor, "--to");
                OverlayRanges.ValidateSyntax(to, "--to", cursor);
                continue;
            }

            if (token == "--from" || token.StartsWith("--from=", StringComparison.Ordinal))
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

            if (token == "--repeat" || token.StartsWith("--repeat=", StringComparison.Ordinal))
            {
                if (seenRepeat)
                {
                    throw new QpdfParseException("Duplicate --repeat is outside the narrow profile.", cursor, null);
                }

                seenRepeat = true;
                repeat = TakeParameter(argv, argv.Count, ref cursor, "--repeat");
                OverlayRanges.ValidateSyntax(repeat, "--repeat", cursor);
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                string name = OptionName(token);
                if (CryptoOptions.Contains(name))
                {
                    throw new QpdfCryptoException("--" + name);
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
        return new OverlaySpec(file, to, from, repeat);
    }

    private static string TakeOverlayFrom(IReadOnlyList<string> argv, ref int cursor)
    {
        string token = argv[cursor];
        int equals = token.IndexOf("=", StringComparison.Ordinal);
        if (equals >= 0)
        {
            cursor += 1;
            return token.Substring(equals + 1);
        }

        if (cursor + 1 >= argv.Count)
        {
            throw new QpdfParseException("Missing -- terminator after --from block option.", cursor, null);
        }

        string next = argv[cursor + 1];
        if (next is null || next == "--" || (next.StartsWith("--", StringComparison.Ordinal) && next.Length > 2))
        {
            cursor += 1;
            return string.Empty;
        }

        return TakeParameter(argv, argv.Count, ref cursor, "--from");
    }

    private static AttachmentCopy ParseCopyBlock(IReadOnlyList<string> argv, ref int cursor)
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

            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                string name = OptionName(token);
                if (CryptoOptions.Contains(name))
                {
                    throw new QpdfCryptoException("--" + name);
                }

                throw new QpdfParseException($"Unsupported option '--{name}' in the narrow selection profile.", cursor, null);
            }

            throw new QpdfParseException($"Unexpected argument '{token}' inside --copy-attachments-from.", cursor, null);
        }

        if (cursor >= argv.Count)
        {
            throw new QpdfParseException("Missing -- terminator after --copy-attachments-from.", cursor, null);
        }

        cursor += 1;
        return new AttachmentCopy(file, prefix is null || prefix.Length == 0 ? null : prefix);
    }

    private static AttachmentAdd ParseAddBlock(IReadOnlyList<string> argv, ref int cursor)
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
                string name = OptionName(token);
                string value = TakeParameter(argv, argv.Count, ref cursor, "--" + name);
                if (name == "key")
                {
                    key = value;
                }
                else if (name == "filename")
                {
                    fileName = value;
                }
                else if (name == "creationdate")
                {
                    creationDate = value;
                }
                else if (name == "moddate")
                {
                    modDate = value;
                }
                else if (name == "mimetype")
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
                string name = OptionName(token);
                if (CryptoOptions.Contains(name))
                {
                    throw new QpdfCryptoException("--" + name);
                }

                if (name == "pages" || name == "check" || name == "add-attachment" || name == "copy-attachments-from"
                    || name == "remove-attachment" || name == "list-attachments" || name == "prefix"
                    || name.StartsWith("show-attachment", StringComparison.Ordinal))
                {
                    throw new QpdfParseException($"Option '--{name}' cannot appear inside --add-attachment.", cursor, null);
                }

                throw new QpdfParseException($"Unsupported option '--{name}' in the narrow selection profile.", cursor, null);
            }

            throw new QpdfParseException($"Unexpected argument '{token}' inside --add-attachment.", cursor, null);
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
        for (int i = 0; i < pagesAt; i++)
        {
            if (argv[i] == "--")
            {
                throw new QpdfParseException("Misplaced -- terminator before --pages.", i, null);
            }
        }

        for (int i = pagesAt + 1; i < argv.Count; i++)
        {
            if (argv[i] == "--")
            {
                return i;
            }
        }

        throw new QpdfParseException("Missing -- terminator after the --pages selection.", pagesAt, null);
    }

    private static string ParseInputArea(IReadOnlyList<string> argv, int boundaryAt, string operation)
    {
        string? input = null;
        for (int i = 0; i < boundaryAt; i++)
        {
            string token = argv[i];
            if (token is null)
            {
                throw new QpdfParseException("Null argument.", i, null);
            }

            if (token == "--empty")
            {
                throw new QpdfParseException("--empty is outside the narrow selection profile.", i, null);
            }

            if (token == "--replace-input")
            {
                throw new QpdfParseException("--replace-input is outside the narrow selection profile.", i, null);
            }

            if (token == "-")
            {
                throw new QpdfParseException("Standard input is not a seekable PDF input.", i, null);
            }

            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                CheckOptionToken(token, i);
            }

            if (input is not null)
            {
                throw new QpdfParseException($"A single positional input must precede {operation}.", i, null);
            }

            input = token;
        }

        if (input is null)
        {
            throw new QpdfParseException($"A positional input file is required before {operation}.", boundaryAt, null);
        }

        return input;
    }

    private static (List<PageGroup> Groups, List<OutputRotation> Rotations, List<PageLabelSpec> Labels, List<int> Collate) ParsePagesArea(IReadOnlyList<string> argv, int pagesAt, int terminatorAt)
    {
        List<PageGroup> groups = new();
        string? file = null;
        List<OutputRotation> rotations = new();
        List<PageLabelSpec> labels = new();
        bool removeLabels = false;
        List<int> collate = new();
        int i = pagesAt + 1;
        while (i < terminatorAt)
        {
            string token = argv[i];
            if (token is null)
            {
                throw new QpdfParseException("Null argument.", i, null);
            }

            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                string name = OptionName(token);
                if (CryptoOptions.Contains(name))
                {
                    throw new QpdfCryptoException("--" + name);
                }

                if (name == "file")
                {
                    if (file is not null)
                    {
                        throw new QpdfParseException("Each --pages file needs one range before the next file.", i, null);
                    }

                    file = TakeParameter(argv, terminatorAt, ref i, "--file");
                    continue;
                }

                if (name == "range")
                {
                    if (file is null)
                    {
                        throw new QpdfParseException("--range must follow the file specification.", i, null);
                    }

                    string range = TakeParameter(argv, terminatorAt, ref i, "--range");
                    groups.Add(new PageGroup(file, range));
                    file = null;
                    continue;
                }

                if (name == "collate")
                {
                    string? value = TakeOptionalParameter(argv, terminatorAt, ref i, "--collate");
                    collate.AddRange(ParseCollateValue(value, i));
                    continue;
                }

                if (name == "rotate")
                {
                    string specification = TakeParameter(argv, terminatorAt, ref i, "--rotate");
                    rotations.Add(OutputRotation.Parse(specification, i));
                    continue;
                }

                if (token == "--set-page-labels")
                {
                    if (removeLabels)
                    {
                        throw new QpdfParseException("--set-page-labels cannot be combined with --remove-page-labels.", i, null);
                    }

                    i += 1;
                    bool specified = false;
                    while (i < terminatorAt)
                    {
                        string specification = argv[i];
                        if (specification is null)
                        {
                            throw new QpdfParseException("Null argument.", i, null);
                        }

                        if (specification.StartsWith("--", StringComparison.Ordinal) && specification.Length > 2)
                        {
                            break;
                        }

                        labels.Add(PageLabelSpec.Parse(specification, i));
                        specified = true;
                        i += 1;
                    }

                    if (!specified)
                    {
                        throw new QpdfParseException("--set-page-labels requires at least one label specification.", i, null);
                    }

                    continue;
                }

                if (token == "--remove-page-labels")
                {
                    if (removeLabels)
                    {
                        throw new QpdfParseException("Duplicate --remove-page-labels is outside the narrow profile.", i, null);
                    }

                    if (labels.Count > 0)
                    {
                        throw new QpdfParseException("--set-page-labels cannot be combined with --remove-page-labels.", i, null);
                    }

                    removeLabels = true;
                    i += 1;
                    continue;
                }

                throw new QpdfParseException($"Unsupported option '--{name}' in the narrow selection profile.", i, null);
            }

            if (file is null)
            {
                file = token;
            }
            else
            {
                groups.Add(new PageGroup(file, token));
                file = null;
            }

            i += 1;
        }

        if (file is not null)
        {
            throw new QpdfParseException("The --pages selection requires one range in this profile.", pagesAt, null);
        }

        if (groups.Count == 0)
        {
            throw new QpdfParseException("The --pages selection requires one file.", pagesAt, null);
        }

        return (groups, rotations, labels, collate);
    }

    private static JobPlan ParseReplace(
        string input,
        List<PageGroup> groups,
        List<OutputRotation> rotations,
        List<PageLabelSpec> labels,
        List<int> collate,
        QpdfRecoveryMode recovery,
        GlobalOptions globals)
    {
        if (groups.Count > 1 || collate.Count > 0)
        {
            return ParseMerge(input, input, groups, rotations, labels, collate, recovery, globals) with
            {
                ReplaceInput = true,
            };
        }

        string file = groups[0].File;
        string range = groups[0].Range;
        string resolvedFile = ResolveFile(file, input);
        int page = SelectionPlan.ParsePageNumber(range);
        return new SelectionPlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(input), page, recovery) with
        {
            WarningExitZero = globals.WarningExitZero,
            NoWarn = globals.NoWarn,
            Rotations = rotations,
            Labels = labels,
            SplitPages = globals.SplitPages,
            ReplaceInput = true,
            MinVersion = globals.MinVersion,
            ForceVersion = globals.ForceVersion,
            DeterministicId = globals.DeterministicId,
            StaticId = globals.StaticId,
            NewlineBeforeEndstream = globals.NewlineBeforeEndstream,
            ObjectStreams = globals.ObjectStreams,
            PreserveUnreferenced = globals.PreserveUnreferenced,
            StreamData = globals.StreamData,
            CompressStreams = globals.CompressStreams,
            DecodeLevel = globals.DecodeLevel,
            RecompressFlate = globals.RecompressFlate,
            CompressionLevel = globals.CompressionLevel,
            RemoveInfo = globals.RemoveInfo,
            RemoveMetadata = globals.RemoveMetadata,
            CoalesceContents = globals.CoalesceContents,
            NormalizeContent = globals.NormalizeContent,
            RemoveUnreferencedResources = globals.RemoveUnreferencedResources,
            ExternalizeInlineImages = globals.ExternalizeInlineImages,
            IiMinBytes = globals.IiMinBytes,
            FlattenRotation = globals.FlattenRotation,
        };
    }

    private static JobPlan ParseMerge(
        string input,
        string output,
        List<PageGroup> groups,
        List<OutputRotation> rotations,
        List<PageLabelSpec> labels,
        List<int> collate,
        QpdfRecoveryMode recovery,
        GlobalOptions globals)
    {
        List<PdfPageSource> sources = new(groups.Count);
        foreach (PageGroup group in groups)
        {
            int page = SelectionPlan.ParsePageNumber(group.Range);
            sources.Add(new PdfPageSource(SelectionPlan.CheckedPath(group.File), new PdfPageSelectionOptions(page)));
        }

        PdfPageCollation? collation = collate.Count == 0 ? null : new PdfPageCollation(collate);
        return new MergePlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output), sources, collation, recovery) with
        {
            WarningExitZero = globals.WarningExitZero,
            NoWarn = globals.NoWarn,
            Rotations = rotations,
            Labels = labels,
            SplitPages = globals.SplitPages,
            MinVersion = globals.MinVersion,
            ForceVersion = globals.ForceVersion,
            DeterministicId = globals.DeterministicId,
            StaticId = globals.StaticId,
            NewlineBeforeEndstream = globals.NewlineBeforeEndstream,
            ObjectStreams = globals.ObjectStreams,
            PreserveUnreferenced = globals.PreserveUnreferenced,
            StreamData = globals.StreamData,
            CompressStreams = globals.CompressStreams,
            DecodeLevel = globals.DecodeLevel,
            RecompressFlate = globals.RecompressFlate,
            CompressionLevel = globals.CompressionLevel,
            RemoveInfo = globals.RemoveInfo,
            RemoveMetadata = globals.RemoveMetadata,
            CoalesceContents = globals.CoalesceContents,
            NormalizeContent = globals.NormalizeContent,
            RemoveUnreferencedResources = globals.RemoveUnreferencedResources,
            ExternalizeInlineImages = globals.ExternalizeInlineImages,
            IiMinBytes = globals.IiMinBytes,
            FlattenRotation = globals.FlattenRotation,
        };
    }

    private static string? TakeOptionalParameter(IReadOnlyList<string> argv, int terminatorAt, ref int i, string option)
    {
        string token = argv[i];
        int equals = token.IndexOf('=');
        if (equals >= 0)
        {
            i += 1;
            return token.Substring(equals + 1);
        }

        if (i + 1 >= terminatorAt)
        {
            i += 1;
            return null;
        }

        string next = argv[i + 1];
        if (next is null || next == "--" || (next.StartsWith("--", StringComparison.Ordinal) && next.Length > 2))
        {
            i += 1;
            return null;
        }

        i += 2;
        return next;
    }

    private static List<int> ParseCollateValue(string? value, long offset)
    {
        if (value is null || value.Length == 0)
        {
            return new List<int> { 1 };
        }

        List<int> sizes = new();
        foreach (string item in value.Split(','))
        {
            if (item.Length == 0 || item.Length > 10)
            {
                throw new QpdfParseException($"Invalid --collate value '{value}': expected n[,m,...].", offset, null);
            }

            long size = 0L;
            foreach (char digit in item)
            {
                if (digit < '0' || digit > '9')
                {
                    throw new QpdfParseException($"Invalid --collate value '{value}': expected n[,m,...].", offset, null);
                }

                size = checked(size * 10L + (digit - '0'));
            }

            if (size > int.MaxValue)
            {
                throw new QpdfParseException($"Invalid --collate value '{value}': group size is too large.", offset, null);
            }

            sizes.Add((int)size);
        }

        return sizes;
    }

    private static string TakeParameter(IReadOnlyList<string> argv, int terminatorAt, ref int i, string option)
    {
        string token = argv[i];
        int equals = token.IndexOf('=');
        if (equals >= 0)
        {
            string inline = token.Substring(equals + 1);
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

    private static string ParseOutputArea(IReadOnlyList<string> argv, int terminatorAt)
    {
        string? output = null;
        for (int i = terminatorAt + 1; i < argv.Count; i++)
        {
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
