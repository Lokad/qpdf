namespace Lokad.Qpdf.Commands;

/// <summary>Argv token, value and scoped-block syntax shared by the operation adapters.</summary>
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

    internal static void RejectCryptoOptions(IReadOnlyList<string> argv, Runtime.ResourceGovernor governor)
    {
        for (int index = 0; index < argv.Count; index++)
        {
            governor.CheckCancellation();
            governor.NoteOperation();
            string token = argv[index];
            if (token == "--encrypt")
            {
                index = EncryptionArgumentBlock.FindEnd(argv, index, governor);
                continue;
            }
            if (token is not null && token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2
                && IsCryptoOption(OptionName(token)) && OptionName(token) is not ("password" or "password-file" or "password-mode" or "suppress-password-recovery" or "decrypt" or "copy-encryption" or "encryption-file-password" or "remove-restrictions"))
            {
                throw new QpdfCryptoException($"--{OptionName(token)}");
            }
        }
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
}
