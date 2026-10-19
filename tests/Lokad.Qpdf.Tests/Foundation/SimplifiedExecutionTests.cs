using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Pages;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class SimplifiedExecutionTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output");

    [Fact]
    public void PublicFuelSurfaceIsRemovedAndCounterCopiesKeepTheSevenStorageAndIoValues()
    {
        Assert.Null(typeof(QpdfLimits).GetProperty("MaxWork"));
        Assert.Null(typeof(QpdfExecutionCounters).GetProperty("TotalWork"));
        Assert.Null(typeof(QpdfJpegCodecContext).GetMethod("ChargeWork"));
        QpdfExecutionCounters counters = new(1, 2, 3, 4, 5, 6, 7);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7 }, new long[] { counters.LiveMemoryBytes, counters.PeakMemoryBytes,
            counters.TotalDecodedBytes, counters.TotalOutputBytes, counters.HostOperations, counters.OpenHandles, counters.DiagnosticCount });
        Assert.Equal(counters, counters with { });
    }

    [Fact]
    public void CancellationCheckpointsRemainIndependentFromStorageAdmission()
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default with { CancellationCheckInterval = 4 }, cancellation.Token);
        governor.NoteOperation(); cancellation.Cancel();
        governor.NoteOperation(); governor.NoteOperation();
        Assert.ThrowsAny<OperationCanceledException>(() => governor.NoteOperation());
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ExpandedSelectionsKeepTheirStructuralCardinalityBound()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => PageRanges.Expand("1,1,1", 1, 2, governor, memory));
        Assert.Equal(nameof(QpdfLimits.MaxContainerSize), failure.LimitName);
    }

    [Fact]
    public void CountOverflowPreservesTheAcceptedPrefixWithoutAllocatingLaterText()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxDiagnostics = 1 }, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        bag.AddParts(QpdfDiagnosticCode.DuplicateKey, QpdfDiagnosticSeverity.Warning, ["prior ", 7]);
        IReadOnlyList<QpdfDiagnostic> accepted = bag.Entries;
        QpdfExecutionCounters before = governor.Snapshot();
        Assert.Throws<QpdfLimitException>(() => bag.AddParts(QpdfDiagnosticCode.DuplicateKey,
            QpdfDiagnosticSeverity.Warning, [new string('X', 32768)]));
        Assert.Equal(before, governor.Snapshot());
        Assert.Equal("prior 7", Assert.Single(accepted).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticOverflowFailsBeforePublicationAndSuppressionSkipsUnstoredWarnings(bool noWarn)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, CheckTests.Assemble(CheckTests.SinglePage("<< /Length 4 /Length 4 /Width 1 /Width 2 >>", "q Q\n"u8.ToArray()), 1), QpdfInputOwnership.Transfer);
        await Seed(host);
        QpdfCommandInvocation command = new(noWarn ? ["input", "--no-warn", "output"] : ["input", "output"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, command,
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxDiagnostics = 1 }), CancellationToken.None);
        Assert.Equal(noWarn ? QpdfExecutionStatus.Completed : QpdfExecutionStatus.Failed, result.Status);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        if (noWarn) { Assert.Empty(result.Diagnostics); Assert.NotEqual("prior"u8.ToArray(), bytes); }
        else
        {
            Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded);
            Assert.Equal("prior"u8.ToArray(), bytes);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SummaryOverflowPreservesThePriorTarget()
    {
        using InMemoryQpdfHost host = CleanHost(); await Seed(host);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["input", "--verbose", "--report-memory-usage", "output"]),
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxDiagnostics = 1 }), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CancellationAfterCommitReusesThePreparedSummariesAndKeepsPublicationSuccessful()
    {
        using InMemoryQpdfHost storage = CleanHost(); await Seed(storage);
        using CancellationTokenSource cancellation = new();
        IQpdfHost host = new CancelAfterCommitHost(storage, cancellation);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["input", "--verbose", "--progress", "--report-memory-usage", "output"]),
            cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.VerboseSummary);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.ProgressReport);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.MemoryUsage);
        Assert.True(storage.TryGetOutput(Output, out byte[] bytes));
        Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8));
        Assert.Equal(0L, storage.OpenLeaseCount);
    }

    private sealed class CancelAfterCommitHost(InMemoryQpdfHost storage, CancellationTokenSource cancellation) : IQpdfHost
    {
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Transaction(await storage.CreateOutputAsync(path, token), cancellation);

        private sealed class Transaction(IQpdfOutputTransaction inner, CancellationTokenSource cancellation) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) => inner.WriteAsync(bytes, token);
            public async ValueTask CommitAsync(CancellationToken token)
            { await inner.CommitAsync(token); cancellation.Cancel(); }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private static InMemoryQpdfHost CleanHost()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(Input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Transfer);
        return host;
    }

    private static async Task Seed(InMemoryQpdfHost host)
    {
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(Output, CancellationToken.None);
        await transaction.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }
}
