using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class ScopedHelpTests
{
    [Fact]
    public async Task OptionTopicAndAllDescribeDistinctScopes()
    {
        using InMemoryQpdfHost host = new();
        string option = await Help(host, "--help=--split-pages");
        Assert.Contains("defaults to 1", option, StringComparison.Ordinal);
        Assert.Contains("%d", option, StringComparison.Ordinal);
        Assert.DoesNotContain("--object-streams", option, StringComparison.Ordinal);
        string topic = await Help(host, "--help=transformation");
        Assert.Contains("--linearize", topic, StringComparison.Ordinal);
        Assert.Contains("--object-streams", topic, StringComparison.Ordinal);
        Assert.DoesNotContain("--copy-attachments-from", topic, StringComparison.Ordinal);
        string all = await Help(host, "--help=all");
        Assert.Contains("transformation:", all, StringComparison.Ordinal);
        Assert.Contains("page-ranges:", all, StringComparison.Ordinal);
        Assert.Contains(option, all, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--help=unknown")]
    [InlineData("--help=--unknown")]
    [InlineData("--help=")]
    public async Task UnknownSubjectsFailWithoutAccessingTheHost(string argument)
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([argument]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0L, result.Counters.HostOperations);
    }

    [Fact]
    public async Task ScopedHelpUsesResponseExpansionAndDiagnosticAdmission()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("help.args"), "--help=--pages\n"u8.ToArray(), QpdfInputOwnership.Transfer);
        Assert.Contains("--file", await Help(host, "@help.args"), StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
        QpdfExecutionResult budget = await Qpdf.ExecuteAsync(host, new(["--help=--pages"]),
            new(QpdfLimits.Default with { MaxDiagnosticBytes = 1 }), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, budget.ExitCode);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(budget.Diagnostics).Code);
        QpdfExecutionResult mixed = await Qpdf.ExecuteAsync(host, new(["--empty", "--help=--pages"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(mixed.Diagnostics).Code);
    }

    [Fact]
    public async Task EveryImplementedInventoryFlagHasOptionHelp()
    {
        using JsonDocument inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Compatibility", "options.json")));
        using InMemoryQpdfHost host = new();
        IEnumerable<string> flags = inventory.RootElement.GetProperty("options").EnumerateArray()
            .Where(option => option.GetProperty("status").GetString() is "complete" or "partial")
            .Select(option => option.GetProperty("flag").GetString())
            .OfType<string>()
            .Where(flag => flag.StartsWith("--", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);
        foreach (string flag in flags)
        {
            Assert.StartsWith(flag, await Help(host, "--help=" + flag), StringComparison.Ordinal);
        }
    }

    private static async Task<string> Help(InMemoryQpdfHost host, string argument)
    {
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([argument]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.HelpText, diagnostic.Code);
        return diagnostic.Message;
    }
}
