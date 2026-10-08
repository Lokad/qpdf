using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(NameTableCollection))]
public sealed class OwnedAllocationBatchTests
{
    private static byte[][] Populate(OwnedAllocationBatch batch, int count)
    {
        byte[][] values = new byte[count][];
        for (int index = 0; index < count; index++)
        {
            batch.Memory.Reserve(160L); values[index] = new byte[128]; values[index][0] = (byte)index;
            batch.Add(values[index], 160L);
        }
        return values;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(11)]
    public void EmptyBatchAdmitsItsStorageAndClosesWithoutOpeningALedger(int capacity)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        OwnedAllocationBatch batch = OwnedAllocationBatch.Create(governor, capacity, CancellationToken.None);
        Assert.Equal(144L + (capacity == 0 ? 0L : 32L + 16L * capacity), governor.Snapshot().LiveMemoryBytes);
        batch.Retain(); batch.Dispose(); batch.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => batch.Memory);
    }

    [Fact]
    public void CapacityAndExtentFailuresKeepAllEarlierCarriersAndFunding()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using OwnedAllocationBatch batch = OwnedAllocationBatch.Create(governor, 1, CancellationToken.None);
        batch.Memory.Reserve(48L); object value = new(); int index = batch.Add(value, 24L);
        long live = governor.Snapshot().LiveMemoryBytes, bytes = batch.Bytes;
        Assert.Throws<InvalidOperationException>(() => batch.Add(new object(), 24L));
        Assert.Throws<InvalidOperationException>(() => batch.UpdateBytes(index, long.MaxValue));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(bytes, batch.Bytes);
        Assert.Same(value, batch.Get(index).Carrier); Assert.Equal(1, batch.Count);
    }

    [Fact]
    public void AtomicTransferDoesNotVisitCallerFundedValueGraphs()
    {
        PdfByteString borrowed = new(new byte[2 * 1024 * 1024], false);
        QpdfObject[] items = [borrowed];
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        using OwnedAllocationBatch batch = OwnedAllocationBatch.Create(governor, 1, CancellationToken.None);
        batch.Memory.Reserve(24L); PdfArray wrapper = PdfArray.FromOwnedItems(items); batch.Add(wrapper, 24L);
        batch.Retain(); Assert.Equal(24L, batch.Bytes);
        Assert.Same(borrowed, wrapper[0]); Assert.Same(items, wrapper.AllocationCarrier);
        Assert.True(governor.Snapshot().LiveMemoryBytes < 8192L); GC.KeepAlive(wrapper);
    }

    [Fact]
    public void DuplicateExistingCarrierRollsBackOnlyFreshReceipts()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor);
        prior.Reserve(24L); object borrowed = new(); prior.RetainAllocation(borrowed, 24L);
        using OwnedAllocationBatch batch = OwnedAllocationBatch.Create(governor, 2, CancellationToken.None);
        batch.Memory.Reserve(48L); object fresh = new(); batch.Add(fresh, 24L); batch.Add(borrowed, 24L);
        long live = governor.Snapshot().LiveMemoryBytes, reserved = batch.Memory.ReservedBytes;
        Assert.Throws<InvalidOperationException>(() => batch.Retain());
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(reserved, batch.Memory.ReservedBytes);
        Assert.Same(fresh, batch.Get(0).Carrier); Assert.Same(borrowed, batch.Get(1).Carrier);
        Assert.Equal(0L, prior.ReservedBytes); GC.KeepAlive(borrowed);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(48L)]
    [InlineData(96L)]
    [InlineData(144L)]
    [InlineData(192L)]
    public void FailedMetadataAdmissionKeepsScopePriorAliasesAndRetriesTheSameTransfer(long headroom)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 8192L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor);
        prior.Reserve(96L); byte[] borrowed = new byte[64]; borrowed[17] = 91; prior.RetainAllocation(borrowed, 96L);
        using OwnedAllocationBatch batch = OwnedAllocationBatch.Create(governor, 4, CancellationToken.None);
        byte[][] values = Populate(batch, 4);
        using ParserMemoryScope pressure = ParserMemoryScope.Create(governor);
        pressure.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - headroom);
        long live = governor.Snapshot().LiveMemoryBytes, reserved = batch.Memory.ReservedBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => batch.Retain());
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(reserved, batch.Memory.ReservedBytes);
        Assert.Equal(91, borrowed[17]); Assert.Equal(0L, prior.ReservedBytes);
        pressure.Dispose(); batch.Retain();
        Assert.Equal(reserved - 640L, batch.Memory.ReservedBytes);
        for (int index = 0; index < values.Length; index++) { Assert.Same(values[index], batch.Get(index).Carrier); Assert.Equal(index, values[index][0]); }
        Assert.Throws<InvalidOperationException>(() => batch.Add(new object(), 0L));
        GC.KeepAlive(borrowed); GC.KeepAlive(values);
    }

    [Theory]
    [InlineData(10L)]
    [InlineData(20L)]
    public void WorkFailureActivatesNoReceiptAndKeepsItsFreshCarriersFunded(long maximum)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxWork = maximum }, CancellationToken.None);
        using OwnedAllocationBatch batch = OwnedAllocationBatch.Create(governor, 4, CancellationToken.None);
        byte[][] values = Populate(batch, 4);
        long live = governor.Snapshot().LiveMemoryBytes, reserved = batch.Memory.ReservedBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => batch.Retain());
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(reserved, batch.Memory.ReservedBytes);
        Assert.Equal(4, batch.Count); Assert.Equal(3, values[3][0]); GC.KeepAlive(values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherPreCanceledRouteStopsBeforeTransferWorkOrMetadata(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using OwnedAllocationBatch batch = OwnedAllocationBatch.Create(governor, 4, governorToken ? CancellationToken.None : cancellation.Token);
        byte[][] values = Populate(batch, 4);
        long live = governor.Snapshot().LiveMemoryBytes, work = governor.Snapshot().TotalWork;
        cancellation.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => batch.Retain());
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(work, governor.Snapshot().TotalWork);
        Assert.Equal(3, values[3][0]); GC.KeepAlive(values);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 4)]
    [InlineData(true, 1)]
    [InlineData(true, 4)]
    public void ObservedCancellationAfterRegisteredVisitsRollsBackBeforeAnyActivation(bool governorToken, int stop)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
        using OwnedAllocationBatch batch = OwnedAllocationBatch.Create(governor, 4, governorToken ? CancellationToken.None : cancellation.Token);
        byte[][] values = Populate(batch, 4);
        long live = governor.Snapshot().LiveMemoryBytes, reserved = batch.Memory.ReservedBytes; int visits = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => batch.RetainObserved(() => { if (++visits == stop) { cancellation.Cancel(); } }));
        Assert.Equal(stop, visits); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(reserved, batch.Memory.ReservedBytes); Assert.Equal(61L, prior.ReservedBytes);
        Assert.Equal(3, values[3][0]); GC.KeepAlive(values);
    }

    private sealed class Completed(OwnedAllocationBatch batch, byte[][] values, WeakReference[] metadata)
    { internal readonly OwnedAllocationBatch Batch = batch; internal readonly byte[][] Values = values; internal readonly WeakReference[] Metadata = metadata; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Completed Complete()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        OwnedAllocationBatch batch = OwnedAllocationBatch.Create(governor, 4, CancellationToken.None);
        byte[][] values = Populate(batch, 4); object state = new(); Action observer = () => GC.KeepAlive(state);
        WeakReference[] metadata = [new(governor), new(batch.Memory), new(observer), new(state)];
        batch.RetainObserved(observer); batch.Dispose(); return new(batch, values, metadata);
    }

    [Fact]
    public void RootedDisposedBatchAndEscapingCarriersDoNotKeepCompletedExecutionState()
    {
        Completed state = Complete();
        for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(state.Metadata, witness => Assert.False(witness.IsAlive)); Assert.Equal(3, state.Values[3][0]);
        Assert.Throws<ObjectDisposedException>(() => state.Batch.Memory); GC.KeepAlive(state);
    }
}
