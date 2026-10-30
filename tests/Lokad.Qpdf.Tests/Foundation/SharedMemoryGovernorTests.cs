using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class SharedMemoryGovernorTests
{
    [Fact]
    public void SmallAllocationsUseAggregateGrowthWithoutPerObjectHostCalls()
    {
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope scope = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new MemoryHost(scope));
        for (int index = 0; index < 10000; index++) { governor.ReserveMemory(32L); }
        Assert.InRange(root.ReservedBytes, 320000L, 320000L + SharedMemoryOwner.BlockBytes);
        Assert.Equal(1, root.ReserveCalls);
        Assert.InRange(root.ResizeCalls, 1, 24);
        governor.ReleaseMemory(320000L);
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public void BufferReplacementAdmitsOldAndNewCapacityBeforeCopying()
    {
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope scope = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new MemoryHost(scope));
        using (GovernedBufferStream stream = new(governor, 1048576L, "output", CancellationToken.None, 65536))
        {
            stream.Write(new byte[65536]);
            stream.WriteByte(1);
            Assert.True(root.PeakBytes >= 65536L + 131072L + 64L);
            Assert.True(root.ReservedBytes >= 131072L + 32L);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public void SharedRefusalPreservesLocalAndSharedStateAndDoesNotRetry()
    {
        RecordingMemoryBudget root = new(16384L);
        using IMemoryScope scope = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new MemoryHost(scope));
        governor.ReserveMemory(256L);
        Assert.Throws<QpdfMemoryAdmissionException>(() => governor.ReserveMemory(65536L));
        Assert.Equal(256L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(1024L, root.ReservedBytes);
        int calls = root.ReserveCalls + root.ResizeCalls;
        Assert.Throws<QpdfMemoryAdmissionException>(() => governor.ReserveMemory(1L));
        Assert.Equal(calls, root.ReserveCalls + root.ResizeCalls);
        governor.ReleaseMemory(256L);
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public void LocalRejectionMakesNoSharedAdmission()
    {
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope scope = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 1024L },
            CancellationToken.None, new MemoryHost(scope));
        Assert.Throws<QpdfLimitException>(() => governor.ReserveMemory(2048L));
        Assert.Equal(0, root.ReserveCalls);
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TinySharedBudgetProducesAnExplicitCommandFailureBeforeHostCalls(bool job)
    {
        RecordingMemoryBudget root = new(0L);
        using IMemoryScope scope = root.CreateScope();
        MemoryHost host = new(scope);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","showNpages":""}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--show-npages"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.Equal(0, host.Opens);
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedCommandStorageStaysUnderItsCallerOwner(bool job)
    {
        RecordingMemoryBudget root = new(1048576L);
        IMemoryScope scope = root.CreateScope();
        MemoryHost host = new(scope);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","showNpages":""}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--show-npages"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageCount);
        Assert.True(root.PeakBytes >= result.Counters.PeakMemoryBytes);
        Assert.True(root.ReservedBytes > 0L);
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        scope.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task ParentStorageCanRefuseAChildAndTheNextChildCanProceed()
    {
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope parent = root.CreateScope();
        if (!parent.TryReserve(950000L, out IMemoryReservation? held)) { throw new InvalidOperationException(); }
        using (held)
        {
            using (IMemoryScope command = parent.CreateScope())
            {
                MemoryHost host = new(command);
                QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
                    new QpdfCommandInvocation(["input", "output"]), CancellationToken.None);
                Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
                Assert.False(host.Inner.TryGetOutput(QpdfPath.From("output"), out _));
                Assert.Equal(0L, host.Inner.OpenLeaseCount);
            }
            Assert.Equal(950000L, root.ReservedBytes);
            using IMemoryScope next = parent.CreateScope();
            MemoryHost nextHost = new(next);
            QpdfExecutionResult recovered = await Qpdf.ExecuteAsync(nextHost,
                new QpdfCommandInvocation(["input", "--show-npages"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, recovered.Status);
            Assert.True(root.PeakBytes <= root.LimitBytes);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task PublicDocumentRefusesBeforeReadingAndDoesNotDisposeTheBorrowedOwner()
    {
        RecordingMemoryBudget root = new(0L);
        using IMemoryScope scope = root.CreateScope();
        MemoryHost host = new(scope);
        await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(
            () => QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None).AsTask());
        Assert.Equal(0, host.Opens);
        Assert.True(scope.TryReserve(0L, out IMemoryReservation? zero));
        zero.Dispose();
    }

    [Fact]
    public async Task CompanionRefusalUsesTheCommandOutcome()
    {
        RecordingMemoryBudget root = new(0L);
        using IMemoryScope scope = root.CreateScope();
        MemoryHost host = new(scope);
        QpdfExecutionResult qdf = await Qpdf.FixQdfAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), CancellationToken.None);
        QpdfExecutionResult flate = await Qpdf.RunZlibFlateAsync(host, QpdfPath.From("input"), QpdfPath.From("output"),
            new QpdfZlibFlateOptions(QpdfZlibFlateMode.Compress, null), CancellationToken.None);
        Assert.All(new[] { qdf, flate }, result =>
        {
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
        });
        Assert.Equal(0, host.Opens);
    }

    [Theory]
    [InlineData("rewrite")]
    [InlineData("linearize")]
    [InlineData("json")]
    [InlineData("check")]
    [InlineData("config")]
    public async Task RefusalAtEachGrowthStopsThePipelineAndReleasesItsLeases(string operation)
    {
        string[] arguments = operation switch
        {
            "rewrite" => ["input", "output"],
            "linearize" => ["input", "--linearize", "--object-streams=generate", "output"],
            "json" => ["input", "--json"],
            "check" => ["input", "--check"],
            "config" => ["--job-json-file=config"],
            _ => throw new InvalidOperationException(),
        };
        RecordingMemoryBudget baseline = new(16777216L);
        int growths;
        using (IMemoryScope scope = baseline.CreateScope())
        {
            MemoryHost host = new(scope);
            host.Inner.AddInput(QpdfPath.From("config"), "{\"inputFile\":\"input\",\"outputFile\":\"output\"}"u8.ToArray(), QpdfInputOwnership.Transfer);
            QpdfExecutionResult successful = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(arguments), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, successful.Status);
            growths = baseline.GrowthCalls;
        }
        Assert.True(growths > 1);
        for (int failureIndex = 1; failureIndex <= growths; failureIndex++)
        {
            RecordingMemoryBudget root = new(16777216L) { RefuseOnGrowth = failureIndex };
            using (IMemoryScope scope = root.CreateScope())
            {
                MemoryHost host = new(scope);
                host.Inner.AddInput(QpdfPath.From("config"), "{\"inputFile\":\"input\",\"outputFile\":\"output\"}"u8.ToArray(), QpdfInputOwnership.Transfer);
                QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(arguments), CancellationToken.None);
                Assert.True(result.Status == QpdfExecutionStatus.Failed,
                    operation + " growth " + failureIndex + ": " + result.Status);
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
                Assert.Equal(failureIndex, root.GrowthCalls);
                Assert.Equal(0L, host.Inner.OpenLeaseCount);
            }
            Assert.Equal(0L, root.ReservedBytes);
        }
    }

    [Fact]
    public async Task OverlappingJobsShareAdmissionAndCancellationWaitsForPendingInput()
    {
        RecordingMemoryBudget root = new(16777216L);
        using IMemoryScope parent = root.CreateScope();
        IMemoryScope firstOwner = parent.CreateScope();
        using CancellationTokenSource cancellation = new();
        MemoryHost pending = new(firstOwner) { PauseReads = true };
        Task<QpdfExecutionResult> first = Qpdf.ExecuteAsync(pending,
            new QpdfCommandInvocation(["input", "--show-npages"]), cancellation.Token).AsTask();
        await pending.ReadStarted.Task;
        long outstanding = root.ReservedBytes;
        Assert.True(outstanding > 0L);
        if (!parent.TryReserve(root.LimitBytes - outstanding, out IMemoryReservation? held))
        { throw new InvalidOperationException(); }
        using (held)
        {
            using IMemoryScope secondOwner = parent.CreateScope();
            QpdfExecutionResult second = await Qpdf.ExecuteAsync(new MemoryHost(secondOwner),
                new QpdfCommandInvocation(["input", "--show-npages"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, second.Status);
            Assert.Contains(second.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
            cancellation.Cancel();
            Assert.False(first.IsCompleted);
            Assert.Equal(root.LimitBytes, root.ReservedBytes);
        }
        Assert.Equal(outstanding, root.ReservedBytes);
        pending.ContinueRead.SetResult();
        QpdfExecutionResult canceled = await first;
        Assert.Equal(QpdfExecutionStatus.Canceled, canceled.Status);
        Assert.Equal(0L, pending.Inner.OpenLeaseCount);
        firstOwner.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
        using IMemoryScope nextOwner = parent.CreateScope();
        QpdfExecutionResult next = await Qpdf.ExecuteAsync(new MemoryHost(nextOwner),
            new QpdfCommandInvocation(["input", "--show-npages"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, next.Status);
    }

    private sealed class MemoryHost : IQpdfHost, IQpdfMemoryHost
    {
        internal MemoryHost(IMemoryBudget budget)
        {
            MemoryBudget = budget;
            Inner.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Transfer);
        }

        internal InMemoryQpdfHost Inner { get; } = new();
        internal int Opens { get; private set; }
        internal bool PauseReads { get; init; }
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ContinueRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IMemoryBudget MemoryBudget { get; }

        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            Opens++;
            IQpdfInput input = await Inner.OpenInputAsync(path, cancellationToken);
            return PauseReads ? new PausedInput(input, this) : input;
        }

        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
            => Inner.CreateOutputAsync(path, cancellationToken);

        private sealed class PausedInput(IQpdfInput inner, MemoryHost host) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;

            public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
            {
                host.ReadStarted.TrySetResult();
                await host.ContinueRead.Task;
                return await inner.ReadAsync(offset, destination, cancellationToken);
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
