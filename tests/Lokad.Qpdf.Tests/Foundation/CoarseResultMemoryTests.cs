using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class CoarseResultMemoryTests
{
    private sealed class MemoryHost(IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => throw new InvalidOperationException("This fixture performs no storage I/O.");
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => throw new InvalidOperationException("This fixture performs no storage I/O.");
    }

    [Fact]
    public void MetadataUsesAggregateCapacityWithoutFinalizerCarriers()
    {
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope scope = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new MemoryHost(scope));
        List<object> values = [];
        for (int index = 0; index < 1000; index++)
        {
            values.Add(governor.OwnVersion(1, 7));
            values.Add(governor.OwnedSnapshot());
        }
        Assert.Equal(120000L, governor.Snapshot().LiveMemoryBytes);
        Assert.InRange(root.ReservedBytes, 120000L, 120000L + SharedMemoryOwner.BlockBytes);
        Assert.Equal(1, root.ReserveCalls);
        Assert.InRange(root.ResizeCalls, 1, 9);
        Assert.Null(typeof(Qpdf).Assembly.GetType("Lokad.Qpdf.Runtime.ExecutionResultMemory"));
        Assert.Null(typeof(Qpdf).Assembly.GetType("Lokad.Qpdf.Runtime.VersionMemory"));
        // Returning the allowance follows explicit ownership, without GC.
        values.Clear();
        scope.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (QpdfJsonResult Result, WeakReference Governor) MakeResult(IMemoryBudget budget)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new MemoryHost(budget));
        governor.PrepareTypedResult(80L);
        QpdfJsonResult result = new JsonInspectionOutcome("{}", Array.Empty<QpdfDiagnostic>()).Present(governor);
        governor.FinishTypedCounters();
        return (result, new(governor));
    }

    [Fact]
    public void ReturnedAliasesDoNotRetainJobsOrRefundTheirCallerOwnerOnCollection()
    {
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope scope = root.CreateScope();
        (QpdfJsonResult result, WeakReference governor) = MakeResult(scope);
        QpdfJsonResult copy = result with { };
        long retained = root.ReservedBytes;
        Assert.True(retained > 0L);
        for (int attempt = 0; attempt < 3 && governor.IsAlive; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Assert.False(governor.IsAlive);
        Assert.Equal(retained, root.ReservedBytes);
        Assert.Equal("{}", result.Json);
        Assert.Equal(result, copy);
        Assert.Same(result.Counters, copy.Counters);
        GC.KeepAlive(result); GC.KeepAlive(copy);
        scope.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public void AbandonedTypedPreparationRefundsCapacityWithoutCollection()
    {
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope scope = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new MemoryHost(scope));
        governor.PrepareTypedResult(80L);
        Assert.True(root.ReservedBytes > 0L);
        governor.AbandonTypedCounters();
        Assert.Equal(0L, root.ReservedBytes);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResultConstructionUsesAdmittedCapacityAfterPublication(bool typed)
    {
        using CancellationTokenSource cancellation = new();
        RecordingMemoryBudget root = new(168L) { RefuseOnGrowth = 2 };
        using IMemoryScope scope = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default with
        { MaxMemoryBytes = 168L, CancellationCheckInterval = 1L },
            cancellation.Token, new MemoryHost(scope));
        if (typed) { governor.PrepareTypedResult(80L); }
        else { governor.ExpectExecutionResult(); }
        cancellation.Cancel();
        QpdfExecutionCounters counters;
        if (typed)
        {
            QpdfJsonResult result = new JsonInspectionOutcome("{}", Array.Empty<QpdfDiagnostic>()).Present(governor);
            governor.FinishTypedCounters();
            counters = result.Counters;
        }
        else
        {
            QpdfExecutionResult result = governor.FinishExecutionResult(ExecutionOutcome.Completed(Array.Empty<QpdfDiagnostic>()));
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            counters = result.Counters;
        }
        Assert.Equal(168L, counters.LiveMemoryBytes);
        Assert.Equal(1, root.GrowthCalls);
        Assert.Equal(168L, root.ReservedBytes);
    }
}
