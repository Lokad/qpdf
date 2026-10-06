using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Jobs;

/// <summary>Reads argv syntax as ordered setters for the shared operation parser.</summary>
internal static class ArgvConfigurationReader
{
    internal static async ValueTask ReadAsync(
        IReadOnlyList<string> argv, Action<string, object> setting,
        Func<string, ValueTask> file, ResourceGovernor governor)
    {
        argv = new GovernedArguments(argv, governor);
        // Excluded operations reject before any configuration/PDF/password input.
        foreach (string token in argv)
        {
            governor.CheckCancellation();
            governor.ChargeWork(1L);
            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2
                && ArgvParser.IsCryptoOption(ArgvParser.OptionName(token)))
            {
                throw new QpdfCryptoException("--" + ArgvParser.OptionName(token));
            }
        }
        bool gaveInput = false;
        bool gaveOutput = false;
        bool global = false;
        int cursor = 0;
        while (cursor < argv.Count)
        {
            governor.CheckCancellation();
            governor.ChargeWork(1L);
            string token = argv[cursor];
            if (token == "--") { global = false; cursor++; continue; }
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
            {
                if (global) { throw Invalid("Only global options may appear in --global.", cursor); }
                if (!gaveInput) { setting("inputFile", token); gaveInput = true; }
                else if (!gaveOutput) { setting("outputFile", token); gaveOutput = true; }
                else { throw Invalid("Unexpected positional argument.", cursor); }
                cursor++;
                continue;
            }
            string name = ArgvParser.OptionName(token);
            if (global && !IsGlobal(name)) { throw Invalid("Only global options may appear in --global.", cursor); }
            if (IsGlobal(name))
            {
                object value = name == "no-default-limits" ? Bare(token, cursor) : Value(argv, ref cursor, null);
                setting("global", new Dictionary<string, object> { [Key(name)] = value });
                cursor++;
                continue;
            }
            switch (name)
            {
                case "global":
                    _ = Bare(token, cursor);
                    global = true;
                    cursor++;
                    continue;
                case "job-json-file":
                    string path = Value(argv, ref cursor, null);
                    await file(path).ConfigureAwait(false);
                    cursor++;
                    continue;
                case "empty":
                    setting("empty", Bare(token, cursor));
                    gaveInput = true;
                    cursor++;
                    continue;
                case "pages":
                    _ = Bare(token, cursor);
                    int end = Terminator(argv, cursor);
                    for (int index = cursor + 1; index < end; index++)
                    {
                        governor.CheckCancellation();
                        governor.ChargeWork(1L);
                        if (argv[index].StartsWith("--", StringComparison.Ordinal)
                            && ArgvParser.OptionName(argv[index]) is not ("file" or "range"))
                        {
                            throw Invalid("Unsupported option in --pages.", index);
                        }
                    }
                    var pages = ArgvParser.ParsePagesArea(argv, cursor, end);
                    setting("pages", pages.Groups.Select(group => Optional(
                        new Dictionary<string, object> { ["file"] = group.File }, "range", group.Range)).ToArray());
                    cursor = end + 1;
                    continue;
                case "overlay":
                case "underlay":
                    _ = Bare(token, cursor);
                    OverlaySpec overlay = ArgvParser.ParseOverlayBlock(argv, ref cursor, name == "underlay");
                    Dictionary<string, object> overlayFields = new() { ["file"] = overlay.File };
                    Optional(overlayFields, "to", overlay.To);
                    Optional(overlayFields, "from", overlay.From);
                    Optional(overlayFields, "repeat", overlay.Repeat);
                    setting(name, new[] { overlayFields });
                    continue;
                case "add-attachment":
                    _ = Bare(token, cursor);
                    AttachmentAdd add = ArgvParser.ParseAddBlock(argv, ref cursor);
                    Dictionary<string, object> addFields = new() { ["file"] = add.File };
                    Optional(addFields, "key", add.Key);
                    Optional(addFields, "filename", add.FileName);
                    Optional(addFields, "creationdate", add.CreationDate);
                    Optional(addFields, "moddate", add.ModDate);
                    Optional(addFields, "mimetype", add.MimeType);
                    Optional(addFields, "description", add.Description);
                    if (add.Replace) { addFields["replace"] = string.Empty; }
                    setting("addAttachment", new[] { addFields });
                    continue;
                case "copy-attachments-from":
                    _ = Bare(token, cursor);
                    AttachmentCopy copy = ArgvParser.ParseCopyBlock(argv, ref cursor);
                    setting("copyAttachmentsFrom", new[] { Optional(new Dictionary<string, object> { ["file"] = copy.File }, "prefix", copy.Prefix) });
                    continue;
                case "set-page-labels":
                    _ = Bare(token, cursor);
                    int labelsEnd = Terminator(argv, cursor);
                    List<string> labels = new();
                    for (int index = cursor + 1; index < labelsEnd; index++)
                    {
                        governor.CheckCancellation();
                        governor.ChargeWork(1L);
                        labels.Add(argv[index]);
                    }
                    setting("setPageLabels", labels);
                    cursor = labelsEnd + 1;
                    continue;
                case "rotate":
                case "json-object":
                case "json-key":
                case "remove-attachment":
                    setting(Key(name), new[] { Value(argv, ref cursor, null) });
                    cursor++;
                    continue;
                case "collate":
                case "split-pages":
                    setting(Key(name), Value(argv, ref cursor, "1"));
                    cursor++;
                    continue;
                case "json":
                case "json-output":
                    setting(Key(name), Value(argv, ref cursor, "latest"));
                    cursor++;
                    continue;
            }
            if (IsBare(name))
            {
                object value = Bare(token, cursor);
                if (name == "remove-page-labels") { value = true; }
                setting(Key(name), value);
                cursor++;
                continue;
            }
            if (HasValue(name))
            {
                setting(Key(name), Value(argv, ref cursor, null));
                cursor++;
                continue;
            }
            ArgvParser.CheckOptionToken(token, cursor);
        }
        if (global) { throw Invalid("Missing -- terminator after --global.", cursor); }
    }

    private static Dictionary<string, object> Optional(Dictionary<string, object> fields, string name, string? value)
    {
        if (value is not null) { fields[name] = value; }
        return fields;
    }

    private static string Bare(string token, int cursor)
    {
        if (token.Contains('=', StringComparison.Ordinal)) { throw Invalid("This option takes no value.", cursor); }
        return string.Empty;
    }

    private static int Terminator(IReadOnlyList<string> argv, int cursor)
    {
        for (int index = cursor + 1; index < argv.Count; index++)
        {
            if (argv[index] == "--") { return index; }
        }
        throw Invalid("Missing -- terminator after option block.", cursor);
    }

    private static string Value(IReadOnlyList<string> argv, ref int cursor, string? fallback)
    {
        string token = argv[cursor];
        int equals = token.IndexOf('=');
        if (equals >= 0)
        {
            string value = token.Substring(equals + 1);
            if (value.Length == 0 && fallback is null) { throw Invalid("Empty option parameter.", cursor); }
            return value.Length == 0 ? fallback ?? string.Empty : value;
        }
        if (fallback is not null) { return fallback; }
        if (cursor + 1 >= argv.Count || argv[cursor + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw Invalid("Missing option parameter.", cursor);
        }
        cursor++;
        if (argv[cursor].Length == 0) { throw Invalid("Empty option parameter.", cursor); }
        return argv[cursor];
    }

    private static string Key(string name)
    {
        char[] key = new char[name.Length];
        int count = 0;
        bool upper = false;
        foreach (char character in name)
        {
            if (character == '-') { upper = true; continue; }
            key[count++] = upper ? char.ToUpperInvariant(character) : character;
            upper = false;
        }
        return new string(key, 0, count);
    }

    private static bool IsGlobal(string name)
        => name is "max-stream-filters" or "parser-max-nesting" or "parser-max-errors"
            or "parser-max-container-size" or "parser-max-container-size-damaged" or "no-default-limits";

    private static bool HasValue(string name)
        => name is "compression-level" or "jpeg-quality" or "force-version" or "ii-min-bytes"
            or "keep-files-open-threshold" or "min-version" or "oi-min-area" or "oi-min-height" or "oi-min-width"
            or "show-attachment" or "show-object" or "linearize-pass1" or "update-from-json" or "json-stream-prefix"
            or "compress-streams" or "decode-level" or "flatten-annotations" or "json-stream-data"
            or "keep-files-open" or "normalize-content" or "object-streams" or "remove-unreferenced-resources" or "stream-data";

    private static bool IsBare(string name)
        => name is "check" or "check-linearization" or "coalesce-contents" or "deterministic-id"
            or "externalize-inline-images" or "filtered-stream-data" or "flatten-rotation" or "generate-appearances"
            or "ignore-xref-streams" or "json-input" or "keep-inline-images" or "linearize" or "list-attachments"
            or "newline-before-endstream" or "no-original-object-ids" or "no-warn" or "optimize-images"
            or "preserve-unreferenced" or "preserve-unreferenced-resources" or "progress" or "qdf" or "raw-stream-data"
            or "recompress-flate" or "remove-acroform" or "remove-info" or "remove-metadata" or "remove-page-labels"
            or "remove-structure" or "replace-input" or "report-memory-usage" or "show-linearization" or "show-npages"
            or "show-pages" or "show-xref" or "static-id" or "suppress-recovery" or "test-json-schema"
            or "verbose" or "warning-exit-0" or "with-images";

    private static QpdfParseException Invalid(string message, int cursor) => new(message, cursor, null);

    private sealed class GovernedArguments(IReadOnlyList<string> inner, ResourceGovernor governor) : IReadOnlyList<string>
    {
        public int Count => inner.Count;
        public string this[int index]
        {
            get
            {
                governor.CheckCancellation();
                governor.ChargeWork(1L);
                return inner[index];
            }
        }
        public IEnumerator<string> GetEnumerator()
        {
            for (int index = 0; index < Count; index++) { yield return this[index]; }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
