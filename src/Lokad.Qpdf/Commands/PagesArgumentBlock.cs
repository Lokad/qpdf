using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Commands;

/// <summary>Validates the boundaries and tokens of a pages block.</summary>
internal static class PagesArgumentBlock
{
    internal static int FindEnd(IReadOnlyList<string> argv, int pagesAt, ResourceGovernor governor)
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
        if (token is "--file" or "--range" or "--password")
        {
            string parameter = token == "--file" ? "file" : token == "--password" ? "password" : "page-range";
            throw new QpdfParseException($"{token} must be given as {token}={parameter}.", index, null);
        }
        if (!token.StartsWith("--", StringComparison.Ordinal)
            || token.StartsWith("--file=", StringComparison.Ordinal)
            || token.StartsWith("--range=", StringComparison.Ordinal)
            || token.StartsWith("--password=", StringComparison.Ordinal)) { return; }
        ReadOnlySpan<char> name = ArgvParser.OptionName(token);
        if (ArgvParser.IsCryptoOption(name)) { throw new QpdfCryptoException($"--{name}"); }
        throw new QpdfParseException(
            $"Unrecognized argument {ArgvParser.ErrorPreview(token)} (pages options must be terminated with --).", index, null);
    }

    private static void Visit(ResourceGovernor governor)
    {
        governor.CheckCancellation();
        governor.NoteOperation();
    }
}
