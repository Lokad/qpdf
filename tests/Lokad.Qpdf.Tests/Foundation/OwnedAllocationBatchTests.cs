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
            batch.Add(160L);
        }
        return values;
    }

    [Fact]
    public void EmptyBatchAdmitsItsStorageAndClosesWithoutOpeningALedger()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        OwnedAllocationBatch batch = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        Assert.Equal(128L, governor.Snapshot().LiveMemoryBytes);
        batch.Retain(); batch.Dispose(); batch.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => batch.Memory);
    }

    [Fact]
    public void ExtentFailuresPreserveEarlierByteAccounting()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using OwnedAllocationBatch batch = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        batch.Memory.Reserve(48L); batch.Add(24L);
        long live = governor.Snapshot().LiveMemoryBytes, bytes = batch.Bytes;
        Assert.Throws<InvalidOperationException>(() => batch.Add(batch.Memory.ReservedBytes));
        Assert.Throws<InvalidOperationException>(() => batch.UpdateBytes(24L, long.MaxValue));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(bytes, batch.Bytes);
    }

    [Fact]
    public void AtomicTransferDoesNotVisitCallerFundedValueGraphs()
    {
        PdfByteString borrowed = new(new byte[2 * 1024 * 1024], false);
        QpdfObject[] items = [borrowed];
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        using OwnedAllocationBatch batch = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        batch.Memory.Reserve(24L); PdfArray wrapper = PdfArray.FromOwnedItems(items); batch.Add(24L);
        batch.Retain(); Assert.Equal(24L, batch.Bytes);
        Assert.Same(borrowed, wrapper[0]); Assert.Same(items, wrapper.AllocationCarrier);
        Assert.True(governor.Snapshot().LiveMemoryBytes < 8192L); GC.KeepAlive(wrapper);
    }

    [Fact]
    public void FreshEstimatesDoNotRequireAnObjectIdentityIndex()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor);
        prior.Reserve(24L); object borrowed = new(); prior.RetainAllocation(24L);
        using OwnedAllocationBatch batch = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        batch.Memory.Reserve(48L); object fresh = new(); batch.Add(24L); batch.Add(24L);
        long live = governor.Snapshot().LiveMemoryBytes, reserved = batch.Memory.ReservedBytes;
        batch.Retain();
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(reserved - batch.Bytes, batch.Memory.ReservedBytes);
        GC.KeepAlive(fresh);
        Assert.Equal(0L, prior.ReservedBytes); GC.KeepAlive(borrowed);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(48L)]
    [InlineData(96L)]
    [InlineData(144L)]
    [InlineData(192L)]
    public void RetentionUsesAdmittedCapacityWithoutAdditionalMetadataAdmission(long headroom)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 8192L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor);
        prior.Reserve(96L); byte[] borrowed = new byte[64]; borrowed[17] = 91; prior.RetainAllocation(96L);
        using OwnedAllocationBatch batch = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        byte[][] values = Populate(batch, 4);
        using ParserMemoryScope pressure = ParserMemoryScope.Create(governor);
        pressure.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - headroom);
        long live = governor.Snapshot().LiveMemoryBytes, reserved = batch.Memory.ReservedBytes;
        batch.Retain();
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(91, borrowed[17]); Assert.Equal(0L, prior.ReservedBytes);
        pressure.Dispose();
        Assert.Equal(reserved - 640L, batch.Memory.ReservedBytes);
        for (int index = 0; index < values.Length; index++) { Assert.Equal(index, values[index][0]); }
        Assert.Throws<InvalidOperationException>(() => batch.Add(0L));
        GC.KeepAlive(borrowed); GC.KeepAlive(values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherPreCanceledRouteStopsBeforeTransferWorkOrMetadata(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using OwnedAllocationBatch batch = OwnedAllocationBatch.Create(governor, governorToken ? CancellationToken.None : cancellation.Token);
        byte[][] values = Populate(batch, 4);
        long live = governor.Snapshot().LiveMemoryBytes;
        cancellation.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => batch.Retain());
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);         Assert.Equal(3, values[3][0]); GC.KeepAlive(values);
    }

    private sealed class Completed(OwnedAllocationBatch batch, byte[][] values, WeakReference[] metadata)
    { internal readonly OwnedAllocationBatch Batch = batch; internal readonly byte[][] Values = values; internal readonly WeakReference[] Metadata = metadata; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Completed Complete()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        OwnedAllocationBatch batch = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        byte[][] values = Populate(batch, 4);
        WeakReference[] metadata = [new(governor), new(batch.Memory)];
        batch.Retain(); batch.Dispose(); return new(batch, values, metadata);
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
