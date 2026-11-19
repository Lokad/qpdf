using System.Text.Json;
using Lokad.Qpdf.Commands;

namespace Lokad.Qpdf.Tests;

public sealed class CompletionListTests
{
    [Fact]
    public void CompletionMatchesTheReviewedTopLevelOptions()
    {
        using JsonDocument inventory = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Compatibility", "options.json")));
        string[] expected = inventory.RootElement.GetProperty("options").EnumerateArray()
            .Where(entry => entry.GetProperty("context").GetString() is "main" or "help" or "global" or "parser")
            .Where(entry => entry.GetProperty("status").GetString() is "complete" or "partial")
            .Select(entry => entry.GetProperty("flag").GetString())
            .OfType<string>()
            // The Zopfli response only reports that the codec is unavailable.
            .Where(flag => flag.StartsWith("--", StringComparison.Ordinal) && flag != "--zopfli")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] actual = ScopedHelp.CompletionFlags.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }
}
