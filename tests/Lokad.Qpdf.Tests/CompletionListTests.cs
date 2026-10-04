using System.Text.RegularExpressions;

namespace Lokad.Qpdf.Tests;

public sealed class CompletionListTests
{
    [Fact]
    public void CompletionListsOnlyRecognizedFlags()
    {
        string? directory = AppContext.BaseDirectory;
        string sources = string.Empty;
        for (int i = 0; i < 8 && directory is not null; i++)
        {
            string candidate = Path.Combine(directory, "src", "Lokad.Qpdf");
            if (Directory.Exists(candidate))
            {
                sources = candidate;
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotEqual(string.Empty, sources);
        HashSet<string> literals = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), "\"(--[a-z0-9][a-z0-9-]*)\""))
            {
                literals.Add(match.Groups[1].Value);
            }
        }

        List<string> unknown = new();
        foreach (string flag in Lokad.Qpdf.Commands.HelpText.CompletionFlags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!literals.Contains(flag))
            {
                unknown.Add(flag);
            }
        }

        Assert.True(unknown.Count == 0, "Completion flags without a parser literal: " + string.Join(", ", unknown));
    }
}