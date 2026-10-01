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
        if (checkAt >= 0 && pagesAt >= 0)
        {
            throw new QpdfParseException("Options --check and --pages cannot be combined.", checkAt, null);
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
            };
        }

        if (pagesAt < 0)
        {
            throw new QpdfParseException("No operation: the narrow profile requires --pages with one file and range, or --check.", 0L, null);
        }

        int terminatorAt;
        if (globals.ReplaceInput)
        {
            foreach (string token in argv)
            {
                if (token == "--")
                {
                    throw new QpdfParseException("No output file may be given with --replace-input.", pagesAt, null);
                }
            }

            terminatorAt = argv.Count;
        }
        else
        {
            terminatorAt = FindTerminator(argv, pagesAt);
        }

        string input = ParseInputArea(argv, pagesAt, "--pages");
        (List<PageGroup> groups, List<OutputRotation> rotations, List<PageLabelSpec> labels, List<int> collate) = ParsePagesArea(argv, pagesAt, terminatorAt);
        if (globals.ReplaceInput)
        {
            return ParseReplace(input, groups, rotations, labels, collate, recovery, globals);
        }

        string output = ParseOutputArea(argv, terminatorAt);
        if (groups.Count > 1 || collate.Count > 0)
        {
            return ParseMerge(input, output, groups, rotations, labels, collate, recovery, globals);
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

    private sealed record GlobalOptions(QpdfRecoveryMode Recovery, bool WarningExitZero, bool NoWarn, int? SplitPages, bool ReplaceInput, string? MinVersion, string? ForceVersion, bool DeterministicId, bool StaticId, bool NewlineBeforeEndstream, string? ObjectStreams, bool PreserveUnreferenced);

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
        return new GlobalOptions(recovery, warningExitZero, noWarn, splitPages, replaceInput, minVersion, forceVersion, deterministicId, staticId, newlineBeforeEndstream, objectStreams, preserveUnreferenced);
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
