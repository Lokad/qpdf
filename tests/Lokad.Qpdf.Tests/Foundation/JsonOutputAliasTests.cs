using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class JsonOutputAliasTests
{
    private static readonly QpdfPath Input = QpdfPath.From("source.pdf"), Secondary = QpdfPath.From("secondary.pdf"),
        Report = QpdfPath.From("report.json"), First = QpdfPath.From("side-4"), Last = QpdfPath.From("side-5");
    private static readonly byte[] Prior = [17, 23, 41];
    private static readonly QpdfLimits SingleHandle = QpdfLimits.Default with { MaxOpenHandles = 1 };
    private static readonly QpdfJsonOptions WithSidecars = new(2, ["qpdf"], "file", "side", [], false);

    [Fact]
    public async Task ResultOnlyJsonRejectsALateSidecarAliasingTheSource()
    {
        using RecordingJobHost host = await Prepare(null, null);
        IQpdfHost aliases = new OutputAliasHost(host, Last, Input);

        QpdfParseException error = await Assert.ThrowsAsync<QpdfParseException>(() =>
            Qpdf.GetDocumentJsonAsync(aliases, Input, WithSidecars, SingleHandle, CancellationToken.None).AsTask());

        Assert.Contains(Last.Value, error.Message, StringComparison.Ordinal);
        Assert.Contains("aliases an input", error.Message, StringComparison.Ordinal);
        Assert.Equal([Input, First, Input], host.OpenedPaths);
        AssertIntact(host, null);
    }

    [Fact]
    public async Task SelectedJsonRejectsAReportAliasingASecondarySource()
    {
        using RecordingJobHost host = await Prepare(null, null);
        host.Inner.AddInput(Secondary, DocumentBytes(), QpdfInputOwnership.Transfer);
        await SeedPrior(host.Inner, Secondary);
        IQpdfHost aliases = new OutputAliasHost(host, Report, Secondary);

        QpdfExecutionResult result = await Qpdf.ExecuteAsync(aliases, new([
            Input.Value, Report.Value, "--pages", Secondary.Value, "1", "--", "--json=2"]), CancellationToken.None);

        AssertFailure(result, Report, "aliases an input");
        Assert.Equal([Input, Secondary, Secondary], host.OpenedPaths);
        AssertPrior(host.Inner, Secondary);
        AssertIntact(host, result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JobJsonRejectsALateSidecarAliasingAnEarlierTarget(bool aliasesReport)
    {
        using RecordingJobHost host = await Prepare(null, null);
        QpdfPath existing = aliasesReport ? Report : First;
        IQpdfHost aliases = new OutputAliasHost(host, Last, existing);

        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(aliases,
            """{"inputFile":"source.pdf","outputFile":"report.json","json":"2","jsonStreamData":"file","jsonStreamPrefix":"side"}""",
            new QpdfExecutionOptions(SingleHandle), CancellationToken.None);

        AssertFailure(result, Last, "collide");
        Assert.Equal([Input, Report, First, existing], host.OpenedPaths);
        AssertIntact(host, result);
    }

    [Fact]
    public async Task TargetProbeCancellationLeavesEveryTargetIntact()
    {
        using RecordingJobHost host = await Prepare("open", Last);
        using CancellationTokenSource cancel = new();
        Task<QpdfJsonResult> execution = Qpdf.GetDocumentJsonAsync(host, Input, Report, WithSidecars, SingleHandle, cancel.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.Empty(host.CreatedPaths);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            AssertIntact(host, null);
        }
        finally
        {
            cancel.Cancel();
            host.Resume();
            await ((Task)execution).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WritesAreAllowedForDistinctStorageAndWriteOnlyTargets(bool writeOnly)
    {
        using RecordingJobHost host = await Prepare(null, null);
        WriteOnlyTargets output = new(host);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(writeOnly ? output : host,
            Input, Report, WithSidecars, SingleHandle, CancellationToken.None);

        if (writeOnly)
        {
            Assert.Equal([Report, First, Last], output.Attempts);
            Assert.Equal([Input], host.OpenedPaths);
            Assert.Equal(host.HostCalls + output.Attempts.Count, result.Counters.HostOperations);
        }
        else { Assert.Equal([Input, Report, First, Last], host.OpenedPaths); }
        Assert.Equal([Report, First, Last], host.CreatedPaths);
        Assert.Equal(3, host.Committed.Count);
        Assert.True(host.Inner.TryGetOutput(Report, out byte[] report));
        using JsonDocument json = JsonDocument.Parse(report);
        Assert.Equal("side-5", json.RootElement.GetProperty("qpdf")[1].GetProperty("obj:5 0 R")
            .GetProperty("stream").GetProperty("datafile").GetString());
        Assert.True(host.Inner.TryGetOutput(First, out byte[] first));
        Assert.True(host.Inner.TryGetOutput(Last, out byte[] last));
        Assert.Equal("q\n"u8.ToArray(), first);
        Assert.Equal("Q\n"u8.ToArray(), last);
        ExecutionJobFixtures.AssertClosed(host, writeOnly ? null : result);
    }

    private static async Task<RecordingJobHost> Prepare(string? boundary, QpdfPath? pausePath)
    {
        RecordingJobHost host = new(boundary, pausePath, "success", true);
        byte[] pdf = DocumentBytes();
        host.Inner.AddInput(Input, pdf, QpdfInputOwnership.Transfer);
        foreach (QpdfPath target in new[] { Report, First, Last })
        {
            // Equal readable bytes still have distinct storage identities.
            host.Inner.AddInput(target, pdf, QpdfInputOwnership.Copy);
            await SeedPrior(host.Inner, target);
        }
        await SeedPrior(host.Inner, Input);
        return host;
    }

    private static byte[] DocumentBytes() => PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents [4 0 R 5 0 R] >>", null),
        (4, "<< /Length 2 >>", "q\n"u8.ToArray()), (5, "<< /Length 2 >>", "Q\n"u8.ToArray())], 1);

    private static async Task SeedPrior(InMemoryQpdfHost storage, QpdfPath path)
    {
        await using IQpdfOutputTransaction target = await storage.CreateOutputAsync(path, CancellationToken.None);
        await target.WriteAsync(Prior, CancellationToken.None);
        await target.CommitAsync(CancellationToken.None);
    }

    private static void AssertPrior(InMemoryQpdfHost storage, QpdfPath path)
    {
        Assert.True(storage.TryGetOutput(path, out byte[] bytes));
        Assert.Equal(Prior, bytes);
    }

    private static void AssertFailure(QpdfExecutionResult result, QpdfPath target, string reason)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        string message = Assert.Single(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.InvalidArguments).Message;
        Assert.Contains(target.Value, message, StringComparison.Ordinal);
        Assert.Contains(reason, message, StringComparison.Ordinal);
        Assert.Equal(0L, result.Counters.TotalOutputBytes);
    }

    private static void AssertIntact(RecordingJobHost host, QpdfExecutionResult? result)
    {
        Assert.Empty(host.CreatedPaths);
        Assert.Empty(host.Committed);
        foreach (QpdfPath path in new[] { Input, Report, First, Last }) { AssertPrior(host.Inner, path); }
        ExecutionJobFixtures.AssertClosed(host, result);
    }

    private sealed class WriteOnlyTargets(RecordingJobHost host) : IQpdfHost, IQpdfMemoryHost
    {
        internal List<QpdfPath> Attempts { get; } = [];
        public IMemoryBudget MemoryBudget => host.Budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            if (path == Input) { return host.OpenInputAsync(path, cancellationToken); }
            Attempts.Add(path);
            throw new QpdfHostException(path, QpdfHostFailureReason.Unsupported);
        }

        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
            => host.CreateOutputAsync(path, cancellationToken);
    }
}
