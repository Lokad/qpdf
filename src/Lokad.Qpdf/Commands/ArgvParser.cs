namespace Lokad.Qpdf.Commands;

/// <summary>
/// Narrow argv parser for selection and inspection. Accepts the upstream
/// spelling subset `qpdf input --pages [--file=]file [--range=]n -- output`
/// (positional file/range forms included) and `qpdf input --check`;
/// `--suppress-recovery` selects strict opening, otherwise damaged inputs
/// repair within bounds; everything else fails explicitly.
/// Unknown `--*` tokens map to the documented crypto set (rejected as crypto)
/// or to unsupported-option errors. Response files, `--empty`,
/// `--replace-input`, stdin/stdout `-`, and flexible operand order arrive later.
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
            return ParseCheck(argv, checkAt, recovery) with
            {
                WarningExitZero = globals.WarningExitZero,
                NoWarn = globals.NoWarn,
            };
        }

        if (pagesAt < 0)
        {
            throw new QpdfParseException("No operation: the narrow profile requires --pages with one file and range, or --check.", 0L, null);
        }

        int terminatorAt = FindTerminator(argv, pagesAt);
        string input = ParseInputArea(argv, pagesAt, "--pages");
        (string file, string range, List<OutputRotation> rotations) = ParsePagesArea(argv, pagesAt, terminatorAt);
        string output = ParseOutputArea(argv, terminatorAt);
        string resolvedFile = ResolveFile(file, input);
        int page = SelectionPlan.ParsePageNumber(range);
        return new SelectionPlan(SelectionPlan.CheckedPath(input), SelectionPlan.CheckedPath(output), page, recovery) with
        {
            WarningExitZero = globals.WarningExitZero,
            NoWarn = globals.NoWarn,
            Rotations = rotations,
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

    private sealed record GlobalOptions(QpdfRecoveryMode Recovery, bool WarningExitZero, bool NoWarn);

    private static GlobalOptions ExtractGlobalOptions(ref IReadOnlyList<string> argv)
    {
        List<string> kept = new(argv.Count);
        QpdfRecoveryMode recovery = QpdfRecoveryMode.BoundedRepair;
        bool warningExitZero = false;
        bool noWarn = false;
        bool seenSuppress = false;
        bool seenWarningExitZero = false;
        bool seenNoWarn = false;
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

            kept.Add(argv[i]);
        }

        argv = kept;
        return new GlobalOptions(recovery, warningExitZero, noWarn);
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

    private static (string File, string Range, List<OutputRotation> Rotations) ParsePagesArea(IReadOnlyList<string> argv, int pagesAt, int terminatorAt)
    {
        string? file = null;
        string? range = null;
        bool fileTaken = false;
        bool rangeTaken = false;
        List<OutputRotation> rotations = new();
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
                    if (fileTaken)
                    {
                        throw new QpdfParseException("Duplicate file specification in --pages.", i, null);
                    }

                    file = TakeParameter(argv, terminatorAt, ref i, "--file");
                    fileTaken = true;
                    continue;
                }

                if (name == "range")
                {
                    if (!fileTaken)
                    {
                        throw new QpdfParseException("--range must follow the file specification.", i, null);
                    }

                    if (rangeTaken)
                    {
                        throw new QpdfParseException("Duplicate range specification in --pages.", i, null);
                    }

                    range = TakeParameter(argv, terminatorAt, ref i, "--range");
                    rangeTaken = true;
                    continue;
                }

                if (name == "rotate")
                {
                    string specification = TakeParameter(argv, terminatorAt, ref i, "--rotate");
                    rotations.Add(OutputRotation.Parse(specification, i));
                    continue;
                }

                throw new QpdfParseException($"Unsupported option '--{name}' in the narrow selection profile.", i, null);
            }

            if (!fileTaken)
            {
                file = token;
                fileTaken = true;
            }
            else if (!rangeTaken)
            {
                range = token;
                rangeTaken = true;
            }
            else
            {
                throw new QpdfParseException($"Unexpected argument '{token}' in --pages.", i, null);
            }

            i += 1;
        }

        if (!fileTaken || file is null)
        {
            throw new QpdfParseException("The --pages selection requires one file.", pagesAt, null);
        }

        if (!rangeTaken || range is null)
        {
            throw new QpdfParseException("The --pages selection requires one range in this profile.", pagesAt, null);
        }

        return (file, range, rotations);
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
