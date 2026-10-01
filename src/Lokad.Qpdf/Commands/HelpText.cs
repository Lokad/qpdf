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
}
