using System.Text.RegularExpressions;

namespace Lokad.Qpdf.Tests;

public sealed class LaneSummaryTests
{
    [Fact]
    public void EveryLaneCaseAppearsInTheSummary()
    {
        string? directory = AppContext.BaseDirectory;
        string lane = string.Empty;
        for (int i = 0; i < 8 && directory is not null; i++)
        {
            string candidate = Path.Combine(directory, "tools", "run-differential-checks.ps1");
            if (File.Exists(candidate))
            {
                lane = File.ReadAllText(candidate);
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotEqual(string.Empty, lane);
        string summary = Assert.Single(lane.Split('\n'), line => line.Contains("Differential lane passed", StringComparison.Ordinal));
        List<string> missing = new();
        foreach (Match match in Regex.Matches(lane, "New-Case '([^']+)'"))
        {
            if (!summary.Contains(match.Groups[1].Value, StringComparison.Ordinal))
            {
                missing.Add(match.Groups[1].Value);
            }
        }

        Assert.True(missing.Count == 0, "Lane cases missing from the summary: " + string.Join(", ", missing));
    }
}