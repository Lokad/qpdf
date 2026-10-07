using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Commands;

/// <summary>Preserves pages context before main options are extracted.</summary>
internal static class PagesArgumentBlock
{
    internal static void Validate(IReadOnlyList<string> argv, ResourceGovernor? governor)
    {
        for (int index = 0; index < argv.Count; index++)
        {
            Visit(governor);
            string token = argv[index];
            if (token == "--pages")
            {
                index = FindEnd(argv, index, governor);
            }
            else if (token is "--global" or "--overlay" or "--underlay"
                or "--add-attachment" or "--copy-attachments-from" or "--set-page-labels")
            {
                // Each of these has its own parser. Its contents cannot open
                // a main-context pages block, even when that block is invalid.
                while (++index < argv.Count)
                {
                    Visit(governor);
                    if (argv[index] == "--") { break; }
                }
            }
        }
    }

    internal static int FindEnd(IReadOnlyList<string> argv, int pagesAt, ResourceGovernor? governor)
    {
        for (int index = pagesAt + 1; index < argv.Count; index++)
        {
            Visit(governor);
            string token = argv[index];
            if (token == "--") { return index; }
            CheckToken(token, index);
        }
        throw new QpdfParseException("Missing -- terminator after the --pages selection.", pagesAt, null);
    }

    internal static void CheckToken(string token, int index)
    {
        if (token is null) { throw new QpdfParseException("Null argument.", index, null); }
        if (token is "--file" or "--range")
        {
            string parameter = token == "--file" ? "file" : "page-range";
            throw new QpdfParseException($"{token} must be given as {token}={parameter}.", index, null);
        }
        if (!token.StartsWith("--", StringComparison.Ordinal)
            || token.StartsWith("--file=", StringComparison.Ordinal)
            || token.StartsWith("--range=", StringComparison.Ordinal)) { return; }
        string name = ArgvParser.OptionName(token);
        if (ArgvParser.IsCryptoOption(name)) { throw new QpdfCryptoException("--" + name); }
        throw new QpdfParseException(
            $"Unrecognized argument {token} (pages options must be terminated with --).", index, null);
    }

    private static void Visit(ResourceGovernor? governor)
    {
        governor?.CheckCancellation();
        governor?.ChargeWork(1L);
    }
}
