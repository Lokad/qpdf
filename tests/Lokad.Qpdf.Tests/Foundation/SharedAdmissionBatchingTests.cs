using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class SharedAdmissionBatchingTests
{
    private sealed class Host(IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(8192)]
    [InlineData(16352)]
    [InlineData(16384)]
    [InlineData(16416)]
    [InlineData(32768)]
    public void RepeatedSmallScratchAvoidsHostChurnWithBoundedSlack(long steady)
    {
        RecordingMemoryBudget root = new(1048576, false);
        using IMemoryScope scope = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new Host(scope));
        governor.ReserveMemory(steady);
        int before = root.ReserveCalls + root.ResizeCalls;
        for (int index = 0; index < 10000; index++) { governor.ReserveMemory(32); governor.ReleaseMemory(32); }
        Assert.InRange(root.ReserveCalls + root.ResizeCalls - before, 0, 1);
        Assert.Equal(steady, governor.Snapshot().LiveMemoryBytes);
        Assert.InRange(root.ReservedBytes, steady, steady + 2L * SharedMemoryOwner.BlockBytes - 1L);
        governor.ReleaseMemory(steady); Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public void SignificantReleaseReturnsCapacityForASiblingImmediately()
    {
        RecordingMemoryBudget root = new(1048576);
        using IMemoryScope scope = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new Host(scope));
        governor.ReserveMemory(1048576);
        governor.ReleaseMemory(1048576 - 16384);
        Assert.Equal(16384L, root.ReservedBytes);
        using IMemoryScope sibling = root.CreateScope();
        Assert.True(sibling.TryReserve(1048576 - 16384, out IMemoryReservation? available));
        available.Dispose(); governor.ReleaseMemory(16384); Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public void CompletionTrimsHysteresisWithoutClosingTheCallerOwner()
    {
        RecordingMemoryBudget root = new(1048576);
        using (IMemoryScope scope = root.CreateScope())
        {
            SharedMemoryOwner owner = new(scope, 1048576);
            owner.Ensure(16384); owner.Ensure(16416); owner.ReleaseTo(16384);
            Assert.Equal(32768L, root.ReservedBytes);
            owner.Complete(16384); Assert.Equal(16384L, root.ReservedBytes);
            Assert.True(scope.TryReserve(1, out IMemoryReservation? proof)); proof.Dispose();
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public void HysteresisDoesNotRaiseLocalLimitsOrRetryRefusedSharedGrowth()
    {
        RecordingMemoryBudget root = new(32767);
        using IMemoryScope scope = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new Host(scope));
        governor.ReserveMemory(16384);
        Assert.Throws<QpdfMemoryAdmissionException>(() => governor.ReserveMemory(32));
        int calls = root.ReserveCalls + root.ResizeCalls;
        Assert.Throws<QpdfMemoryAdmissionException>(() => governor.ReserveMemory(1));
        Assert.Equal(calls, root.ReserveCalls + root.ResizeCalls);
        Assert.Equal(16384L, governor.Snapshot().LiveMemoryBytes); governor.ReleaseMemory(16384);
        ResourceGovernor limited = new(QpdfLimits.Default with { MaxMemoryBytes = 16384 }, CancellationToken.None, new Host(scope));
        limited.ReserveMemory(16384); calls = root.ReserveCalls + root.ResizeCalls;
        Assert.Throws<QpdfLimitException>(() => limited.ReserveMemory(1));
        Assert.Equal(calls, root.ReserveCalls + root.ResizeCalls); limited.ReleaseMemory(16384);
        Assert.Equal(0L, root.ReservedBytes);
    }
}
