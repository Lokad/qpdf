using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Commands;

internal static class EncryptionArgumentBlock
{
    internal static int FindEnd(IReadOnlyList<string> argv, int start, ResourceGovernor governor)
    {
        // Positional passwords can themselves be "--" or look like options.
        int firstOption = start + 1;
        if (firstOption < argv.Count && !argv[firstOption].StartsWith("--", StringComparison.Ordinal))
        { firstOption += 3; }
        for (int index = firstOption; index < argv.Count; index++)
        {
            governor.CheckCancellation();
            if (argv[index] == "--") { return index; }
        }
        throw new QpdfParseException("Missing -- terminator after encryption options.", start, null);
    }
}
