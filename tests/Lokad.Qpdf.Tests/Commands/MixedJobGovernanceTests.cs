using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class MixedJobGovernanceTests
{
    [Theory]
    [InlineData("bytes")]
    [InlineData("memory")]
    [InlineData("work")]
    [InlineData("host")]
    public async Task CumulativeLoadingLimitsPreserveTargetsAndCloseLeases(string budget)
    {
        InMemoryQpdfHost source = MixedJobArgvTests.Host();
        MixedJobArgvTests.Add(source, "first.json", """{"inputFile":"inputs/a.pdf"}""" + new string(' ', 1024));
        MixedJobArgvTests.Add(source, "second.json", """{"showNpages":""}""" + new string(' ', 1024));
        await PriorAsync(source);
        TrackingHost host = new(source, "normal");
        QpdfLimits limits = budget switch
        {
            "bytes" => QpdfLimits.Default with { MaxInputBytes = 1500L },
            "memory" => QpdfLimits.Default with { MaxMemoryBytes = 60000L },
            "work" => QpdfLimits.Default with { MaxWork = 2000L },
            _ => QpdfLimits.Default with { MaxHostOperations = 2L },
        };
        QpdfExecutionResult failed = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["--job-json-file=first.json", "--job-json-file=second.json"]), new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, failed.Status);
        Assert.Contains(failed.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(failed.Counters.TotalWork > 0 && failed.Counters.PeakMemoryBytes > 0);
        Assert.NotEmpty(host.Opens);
        Assert.DoesNotContain(QpdfPath.From("inputs/a.pdf"), host.Opens);
        Assert.Equal(0L, failed.Counters.OpenHandles);
        AssertRetainedNames(failed, host);
        Assert.Equal(0L, source.OpenLeaseCount);
        Assert.Equal(0, host.Outputs);
        AssertPrior(source);
        QpdfExecutionResult control = await Qpdf.ExecuteAsync(source,
            new QpdfCommandInvocation(["--job-json-file=first.json", "--job-json-file=second.json"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, control.Status);
        Assert.Equal(0L, source.OpenLeaseCount);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("invalid-utf8")]
    [InlineData("no-progress")]
    [InlineData("missing")]
    public async Task AFailedSecondFileDoesNotAccessPdfOrPublish(string mode)
    {
        InMemoryQpdfHost source = MixedJobArgvTests.Host();
        MixedJobArgvTests.Add(source, "first.json", """{"inputFile":"inputs/a.pdf"}""");
        if (mode == "invalid-utf8") { source.AddInput(QpdfPath.From("second.json"), new byte[] { 0xff, 0xfe }, QpdfInputOwnership.Copy); }
        else if (mode != "missing") { MixedJobArgvTests.Add(source, "second.json", mode == "malformed" ? "{" : """{"showNpages":""}"""); }
        await PriorAsync(source);
        TrackingHost host = new(source, mode);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["--job-json-file=first.json", "--job-json-file=second.json"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(QpdfPath.From("first.json"), host.Opens);
        Assert.Contains(QpdfPath.From("second.json"), host.Opens);
        Assert.DoesNotContain(QpdfPath.From("inputs/a.pdf"), host.Opens);
        Assert.Equal(0L, result.Counters.OpenHandles);
        AssertRetainedNames(result, host);
        Assert.Equal(0L, source.OpenLeaseCount);
        Assert.Equal(0, host.Outputs);
        AssertPrior(source);
    }

    [Fact]
    public async Task ShortReadsAndIndependentDepthUseSequentialLeases()
    {
        InMemoryQpdfHost source = new();
        MixedJobArgvTests.Add(source, "first.json", """{"empty":""}""");
        MixedJobArgvTests.Add(source, "second.json", """{"showNpages":""}""");
        TrackingHost host = new(source, "short");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["--job-json-file=first.json", "--job-json-file=second.json"]),
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxNestingDepth = 1L, MaxOpenHandles = 1L }), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.EndsWith(": 0", Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount).Message, StringComparison.Ordinal);
        Assert.Equal(1L, host.PeakLeases);
        Assert.True(host.Reads > 2);
        Assert.Equal(0L, source.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Fact]
    public async Task WholeRewriteClosesItsPreliminaryLeaseBeforePublication()
    {
        InMemoryQpdfHost source = MixedJobArgvTests.Host();
        TrackingHost host = new(source, "normal");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["inputs/a.pdf", "outputs/a.pdf", "--job-json-file=settings.json"]),
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxOpenHandles = 1L }), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(1L, host.PeakLeases);
        Assert.Equal(0L, source.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(source.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out _));
    }

    [Fact]
    public async Task CancellationDuringTheSecondFileReleasesLoadedModelsAndLeases()
    {
        InMemoryQpdfHost source = MixedJobArgvTests.Host();
        MixedJobArgvTests.Add(source, "first.json", """{"forceVersion":"1.5"}""");
        MixedJobArgvTests.Add(source, "second.json", """{"minVersion":"1.6"}""");
        await PriorAsync(source);
        TrackingHost host = new(source, "blocking");
        using CancellationTokenSource cancellation = new();
        Task<QpdfExecutionResult> running = Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["inputs/a.pdf", "outputs/a.pdf", "--job-json-file=first.json", "--job-json-file=second.json"]), cancellation.Token).AsTask();
        Assert.Same(host.SecondReadStarted.Task, await Task.WhenAny(running, host.SecondReadStarted.Task).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains(QpdfPath.From("first.json"), host.Opens);
        cancellation.Cancel();
        QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QpdfExecutionStatus.Canceled, result.Status);
        Assert.True(result.Counters.TotalWork > 0 && result.Counters.PeakMemoryBytes > 0);
        Assert.Equal(0L, result.Counters.OpenHandles);
        AssertRetainedNames(result, host);
        Assert.Equal(0L, source.OpenLeaseCount);
        Assert.Equal(0, host.Outputs);
        AssertPrior(source);
    }

    [Fact]
    public async Task NestedCycleInARepeatedFileStillRejects()
    {
        InMemoryQpdfHost host = MixedJobArgvTests.Host();
        MixedJobArgvTests.Add(host, "first.json", """{"jobJsonFile":"second.json"}""");
        MixedJobArgvTests.Add(host, "second.json", """{"jobJsonFile":"first.json"}""");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["--job-json-file=settings.json", "--job-json-file=first.json", "--show-npages"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Contains("cycle", result.Diagnostics[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    private static void AssertRetainedNames(QpdfExecutionResult result, TrackingHost host)
    {
        // The host owns its retained identifiers after callbacks complete.
        Assert.All(host.Opens, path => Assert.InRange(path.Value.Length, 1, QpdfPath.MaxLength));
        Assert.InRange(result.Counters.LiveMemoryBytes, ExecutionResultAdmission.MemoryBytes, result.Counters.PeakMemoryBytes);
    }

    private static async Task PriorAsync(InMemoryQpdfHost source)
    {
        await using IQpdfOutputTransaction transaction = await source.CreateOutputAsync(QpdfPath.From("outputs/a.pdf"), CancellationToken.None);
        await transaction.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static void AssertPrior(InMemoryQpdfHost source)
    {
        Assert.True(source.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] prior));
        Assert.Equal("prior"u8.ToArray(), prior);
    }

    private sealed class TrackingHost(InMemoryQpdfHost source, string mode) : IQpdfHost
    {
        internal List<QpdfPath> Opens { get; } = new();
        internal int Outputs { get; private set; }
        internal int Reads { get; private set; }
        internal long PeakLeases { get; private set; }
        internal TaskCompletionSource SecondReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            Opens.Add(path);
            IQpdfInput input = await source.OpenInputAsync(path, cancellationToken);
            PeakLeases = Math.Max(PeakLeases, source.OpenLeaseCount);
            return new TrackingInput(input, this, mode);
        }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            Outputs++;
            Assert.Equal(0L, source.OpenLeaseCount);
            return source.CreateOutputAsync(path, cancellationToken);
        }
        private sealed class TrackingInput(IQpdfInput inner, TrackingHost host, string mode) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellationToken)
            {
                host.Reads++;
                if (Path.Value == "second.json")
                {
                    host.SecondReadStarted.TrySetResult();
                    if (mode == "blocking") { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                    if (mode == "no-progress") { return ReadOnlyMemory<byte>.Empty; }
                }
                return await inner.ReadAsync(offset, mode == "short" ? Math.Min(count, 3) : count, cancellationToken);
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
