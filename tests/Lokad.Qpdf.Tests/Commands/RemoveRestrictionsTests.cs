using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class RemoveRestrictionsTests
{
    [Theory]
    [InlineData("--remove-restrictions=x")]
    [InlineData("--check")]
    [InlineData("--show-object=1")]
    [InlineData("--json")]
    public async Task InvalidOrReadOnlyCombinationsRejectBeforeHostAccess(string option)
    {
        using InMemoryQpdfHost host = new();
        List<string> argv = ["missing.pdf", "--remove-restrictions", option];
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0L, result.Counters.HostOperations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationAndBudgetFailurePreservePriorOutput(bool job, bool canceled)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input.pdf"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "signature-incremental.pdf")), QpdfInputOwnership.Transfer);
        QpdfPath output = QpdfPath.From("output.pdf");
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfLimits limits = QpdfLimits.Default with { MaxInputBytes = 1 };
        CancellationToken token = new(canceled);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
                { inputFile = "input.pdf", outputFile = "output.pdf", removeRestrictions = "" }), new(limits), token)
            : await Qpdf.ExecuteAsync(host, new(["input.pdf", "output.pdf", "--remove-restrictions"]), new(limits), token);
        Assert.Equal(canceled ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        if (!canceled) { Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(result.Diagnostics).Code); }
        Assert.True(host.TryGetOutput(output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("{\"removeRestrictions\":\"x\"}")]
    [InlineData("{\"removeRestrictions\":\"\",\"json\":\"2\"}")]
    public async Task JobRejectsMalformedOrReadOnlySettings(string settings)
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, settings, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0L, result.Counters.HostOperations);
    }
}
