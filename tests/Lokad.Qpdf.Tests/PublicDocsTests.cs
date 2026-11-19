using System.Text.RegularExpressions;

namespace Lokad.Qpdf.Tests;

public sealed class PublicDocsTests
{
    [Fact]
    public void PublicFilesCarryNoMachineSpecificPaths()
    {
        string root = TestSourceTree.Root;
        List<string> files = new() { Path.Combine(root, "README.md"), Path.Combine(root, "CHANGELOG.md") };
        files.AddRange(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md"));
        List<string> offending = new();
        foreach (string file in files)
        {
            int number = 0;
            foreach (string line in File.ReadLines(file))
            {
                number += 1;
                if (Regex.IsMatch(line, @"(?<![A-Za-z0-9(/])[A-Za-z]:[\\/]") || line.Contains("/home/", StringComparison.Ordinal) || line.Contains("/Users/", StringComparison.Ordinal))
                {
                    offending.Add(Path.GetFileName(file) + ":" + number);
                }
            }
        }

        Assert.True(offending.Count == 0, "Machine-specific paths in public files: " + string.Join(", ", offending));
    }
}
