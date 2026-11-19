namespace Lokad.Qpdf.Commands;

/// <summary>Standalone help for the managed qpdf commands.</summary>
internal static class ScopedHelp
{
    private static readonly Dictionary<string, string> Options = BuildOptions();
    private static readonly Dictionary<string, string> Topics = BuildTopics();

    internal static bool TryGet(ReadOnlySpan<char> subject, out string text)
    {
        Dictionary<string, string> entries = subject.StartsWith("--", StringComparison.Ordinal) ? Options : Topics;
        if (subject.Length <= 64 && entries.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(subject, out string? found))
        {
            text = found;
            return true;
        }
        text = string.Empty;
        return false;
    }

    private static Dictionary<string, string> BuildOptions()
    {
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        foreach (string line in HelpText.Help.Split('\n'))
        {
            ReadOnlySpan<char> item = line.AsSpan().Trim();
            if (!item.StartsWith("--", StringComparison.Ordinal)) { continue; }
            int end = 2;
            while (end < item.Length && (char.IsAsciiLetterOrDigit(item[end]) || item[end] == '-')) { end++; }
            options.Add(item[..end].ToString(), item.ToString());
        }
        options["--pages"] = "--pages [--file=]file [--password=text] [--range=]range ... --\nSelect and assemble pages in the specified source order. Use . for the primary input.";
        options["--file"] = "--file=path  Start a source inside a --pages block.";
        options["--range"] = "--range=range  Set the current source's page range inside a --pages block.";
        options["--split-pages"] = "--split-pages[=n]\nWrite consecutive groups of n pages. n defaults to 1 and must be positive. " +
            "Replace %d in the target name with the page or page range. When there is no %d, insert the range before a .pdf suffix or append it to the target name.";
        options["--to"] = "--to=range  Choose destination pages for an overlay or underlay.";
        options["--from"] = "--from=range  Choose source pages for an overlay or underlay.";
        options["--repeat"] = "--repeat=range  Repeat these placement source pages when the initial range runs out.";
        options["--check"] = "--check  Check document structure, streams and supported page content. Skipped checks are reported separately.";
        options["--empty"] = "--empty  Start with a document containing zero pages.";
        options["--job-json-file"] = "--job-json-file=file  Read equivalent job settings from a host input.";
        options["--json-key"] = "--json-key=key  Include the named top-level section in a JSON report.";
        options["--key"] = "--key=key  Set the attachment key in an add block. An empty key uses the payload basename.";
        options["--filename"] = "--filename=name  Set the stored attachment filename. An empty name uses the payload basename.";
        options["--description"] = "--description=text  Set the attachment description.";
        options["--mimetype"] = "--mimetype=type/subtype  Set the media type of the attachment.";
        options["--creationdate"] = "--creationdate=date  Set the attachment creation date in PDF date format.";
        options["--moddate"] = "--moddate=date  Set the attachment modification date in PDF date format.";
        options["--replace"] = "--replace  Permit replacing an existing attachment with the same key.";
        options["--prefix"] = "--prefix=text  Prepend text to keys in a copy-attachments block.";
        options["--user-password"] = "--user-password=text  Set the user password in the named --encrypt form.";
        options["--owner-password"] = "--owner-password=text  Set the owner password in the named --encrypt form.";
        options["--bits"] = "--bits=256  Select AES-256 output in the named --encrypt form.";
        options["--print"] = "--print=none|low|full  Set printing permissions for fresh password protection.";
        options["--modify"] = "--modify=none|assembly|form|annotate|all  Set modification permissions for fresh password protection.";
        options["--extract"] = "--extract=y|n  Control general extraction permissions for fresh password protection.";
        options["--accessibility"] = "--accessibility=y|n  Accepted in fresh password protection. AES-256 R6 always permits accessibility.";
        options["--annotate"] = "--annotate=y|n  Control annotation changes under fresh password protection.";
        options["--assemble"] = "--assemble=y|n  Control document assembly under fresh password protection.";
        options["--form"] = "--form=y|n  Control form filling under fresh password protection.";
        options["--modify-other"] = "--modify-other=y|n  Control other document edits under fresh password protection.";
        options["--zopfli"] = "--zopfli  Zopfli compression is unavailable. Ordinary Flate compression is supported.";
        return options;
    }

    private static Dictionary<string, string> BuildTopics()
    {
        Dictionary<string, string> topics = new(StringComparer.Ordinal)
        {
            ["add-attachment"] = Topic("Attach a host input under a chosen key and filename.", "--add-attachment", "--key", "--filename", "--replace"),
            ["advanced-control"] = Topic("Control recovery and stream processing within host limits.", "--suppress-recovery", "--ignore-xref-streams", "--max-stream-filters"),
            ["attachments"] = Topic("List, extract or edit embedded files.", "--list-attachments", "--show-attachment", "--remove-attachment"),
            ["completion"] = Topic("Return a shell completion command.", "--completion-bash", "--completion-zsh"),
            ["copy-attachments"] = Topic("Copy embedded files from a separate source.", "--copy-attachments-from", "--prefix"),
            ["encryption"] = Topic("Open R2–R6 inputs and preserve, remove or replace password protection.", "--password", "--decrypt", "--encrypt", "--copy-encryption"),
            ["exit-status"] = Topic("Ordinary jobs exit 0 for success, 2 for errors, and 3 for warnings. Encryption queries use their own exit codes.", "--warning-exit-0", "--is-encrypted", "--requires-password"),
            ["general"] = Topic("Read input and choose job and diagnostic settings.", "--empty", "--job-json-file", "--no-warn", "--verbose"),
            ["global"] = Topic("Set limits that apply throughout the job. Host ceilings remain binding.", "--global", "--no-default-limits", "--parser-max-nesting"),
            ["help"] = Topic("Pass --help=--option or --help=topic as a standalone request.", "--help", "--json-help", "--job-json-help"),
            ["inspection"] = Topic("Read structure and effective page data.", "--check", "--show-pages", "--show-object", "--show-xref"),
            ["json"] = Topic("Inspect a PDF, author a graph, or patch a PDF with qpdf JSON.", "--json", "--json-output", "--json-input", "--update-from-json"),
            ["modification"] = Topic("Edit pages, resources and document data.", "--pages", "--rotate", "--overlay", "--flatten-annotations", "--optimize-images"),
            ["overlay-underlay"] = Topic("Place pages from a separate input above or below the destination program.", "--overlay", "--underlay", "--to", "--from", "--repeat"),
            ["page-ranges"] = Topic("Page ranges use one-based endpoints, inclusive spans, and comma-separated selections. z selects the final page.", "--range", "--pages"),
            ["page-selection"] = Topic("Select, reorder, merge or split page sources.", "--pages", "--file", "--range", "--collate", "--split-pages"),
            ["pdf-dates"] = Topic("Write attachment dates in PDF date format beginning with D: and a four-digit year.", "--creationdate", "--moddate"),
            ["testing"] = Topic("Use stable output or inspect debug representations.", "--static-id", "--linearize-pass1", "--test-json-schema"),
            ["transformation"] = Topic("Change the PDF layout and stream encodings.", "--linearize", "--qdf", "--object-streams", "--stream-data"),
            ["usage"] = Topic("Use qpdf input [options] output for a rewrite, or qpdf input --check for a report.", "--pages", "--empty", "--help"),
        };
        topics["all"] = "Lokad.qpdf help topics\n\n" +
            string.Join("\n", topics.Select(entry => entry.Key + ": " + entry.Value.Split('\n')[0])) +
            "\n\nOptions\n" + string.Join("\n", Options.Values);
        return topics;
    }

    private static string Topic(string summary, params string[] flags)
        => summary + "\n\n" + string.Join("\n", flags.Select(flag => Options[flag]));
}
