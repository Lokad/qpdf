using System.Text.RegularExpressions;

namespace Lokad.Qpdf.Tests;

public sealed class CompletionListTests
{
    [Fact]
    public void CompletionListsOnlyRecognizedFlags()
    {
        string sources = Path.Combine(TestSourceTree.Root, "src", "Lokad.Qpdf");
        HashSet<string> literals = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source, "\"(--[a-z0-9][a-z0-9-]*)\""))
            {
                literals.Add(match.Groups[1].Value);
            }
            // The layered argv reader dispatches on OptionName, which has
            // already removed the leading dashes.
            if (Path.GetFileName(file) == "ArgvConfigurationReader.cs")
            {
                foreach (Match match in Regex.Matches(source, "\"([a-z][a-z0-9-]*)\""))
                { literals.Add("--" + match.Groups[1].Value); }
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
