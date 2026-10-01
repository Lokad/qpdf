using System.Reflection;

namespace Lokad.Qpdf.Commands;

/// <summary>Help and version text for the narrow supported subset.</summary>
internal static class HelpText
{
    internal const string Help = """
        Lokad.qpdf: managed non-cryptographic PDF structural processor.
        Usage:
          qpdf input --pages [--file=]file [--range=]n -- output
          qpdf input --check
          qpdf --job-json-file file
        Options:
          --suppress-recovery   fail on damaged cross-references instead of bounded repair
          --warning-exit-0      exit 0 despite warnings
          --no-warn             suppress warning diagnostics
          @file                 read arguments from a host file, one per line
          --rotate=[+|-]angle[:range] rotate output pages (n, n-m, comma lists, z)
          --split-pages[=n]         write page groups to separate files
          --replace-input             overwrite the single input in place
          --collate[=n[,m]]         interleave multi-file page groups
          --set-page-labels specs  number output pages explicitly
          --min-version=version     raise output version when lower
          --force-version=version   set output version exactly
          --deterministic-id        stable content-hashed document ID
          --static-id               fixed test document ID
          --newline-before-endstream  extra newline before endstream
          --object-streams=mode     disable, preserve (default), or generate
          --preserve-unreferenced       keep unreferenced input objects
          --stream-data=mode            compress, preserve, or uncompress streams
          --compress-streams=[y|n]      compress uncompressed streams
          --decode-level=level          none, generalized, specialized, or all
          --recompress-flate            recompress already-flate streams
          --compression-level=1..9      flate effort for compressed streams
          --remove-info               drop info except modification date
          --remove-metadata           drop the document metadata stream
          --coalesce-contents         combine page content streams
          --normalize-content=[y|n]     normalize newlines in content streams
          --remove-unreferenced-resources=parameter  drop unused page resources
          --list-attachments          list embedded files
          --show-attachment=key         write one attachment to the output file
          --add-attachment file [options] --  embed host files
          --remove-attachment=key       drop an embedded file
          --copy-attachments-from file [--prefix=] --  copy embedded files
          --json[=1|2|latest] [--json-key=key] --  write document JSON
          --json-object={trailer|obj[,gen]}  limit JSON objects
          --json-stream-data={none|inline|file}  stream payloads in JSON
          --json-stream-prefix=file-prefix  prefix for JSON stream files
          --json-output[=2|latest]  minimal JSON for serialization
          --json-input  build a PDF from qpdf JSON
          --update-from-json=file  update a PDF from qpdf JSON
          --remove-page-labels      drop explicit page numbers (accepted no-op)
          --help                show this help
          --version             show version and compatibility target
        """;

    internal static string Version()
    {
        string version = typeof(HelpText).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        return $"Lokad.qpdf {version} (qpdf 12.4.2 compatibility target, non-cryptographic subset)";
    }

    internal static bool TryMatchHelp(IReadOnlyList<string> argv, out string text)
    {
        foreach (string token in argv)
        {
            if (token == "--help")
            {
                text = Help;
                return true;
            }
        }

        text = string.Empty;
        return false;
    }

    internal static bool TryMatchVersion(IReadOnlyList<string> argv, out string text)
    {
        foreach (string token in argv)
        {
            if (token == "--version")
            {
                text = Version();
                return true;
            }
        }

        text = string.Empty;
        return false;
    }

    internal static bool TryMatchZopfli(IReadOnlyList<string> argv, out string text)
    {
        foreach (string token in argv)
        {
            if (token == "--zopfli")
            {
                text = "Zopfli support is not enabled in this build.";
                return true;
            }
        }

        text = string.Empty;
        return false;
    }
}
