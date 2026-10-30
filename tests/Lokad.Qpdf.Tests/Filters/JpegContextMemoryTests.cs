using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class JpegContextMemoryTests
{
    [Fact]
    public void NestedOwnersRefundTheirSubtreesAndPreserveSiblingStorage()
    {
        RecordingMemoryBudget root = new(100000L);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, root);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
        QpdfJpegCodecContext context = new(governor);
        using IMemoryScope first = context.MemoryBudget.CreateScope(), second = context.MemoryBudget.CreateScope();
        using IMemoryScope nested = first.CreateScope();
        Assert.Equal(0, root.ReserveCalls);
        Assert.True(first.TryReserve(1000L, out IMemoryReservation? a));
        Assert.True(nested.TryReserve(2000L, out IMemoryReservation? b));
        Assert.True(second.TryReserve(3000L, out IMemoryReservation? c));
        Assert.True(a.TryResize(60000L)); Assert.True(b.TryResize(0L)); Assert.True(b.TryResize(4000L));
        Assert.Equal(67000L, governor.Snapshot().LiveMemoryBytes);
        first.Dispose(); first.Dispose();
        Assert.Equal(0L, a.Bytes); Assert.Equal(0L, b.Bytes); Assert.Equal(3000L, c.Bytes);
        Assert.Throws<ObjectDisposedException>(() => nested.TryReserve(1L, out _));
        Assert.Throws<ObjectDisposedException>(() => b.TryResize(1L));
        a.Dispose(); b.Dispose(); c.Dispose(); context.Close();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalAndSharedRefusalLeaveExistingReservationsUnchanged(bool shared)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = shared ? 100000L : 16000L };
        RecordingMemoryBudget root = new(shared ? 16000L : 100000L);
        using InMemoryQpdfHost host = new(limits, null, root);
        ResourceGovernor governor = new(limits, CancellationToken.None, host);
        QpdfJpegCodecContext context = new(governor);
        using IMemoryScope owner = context.MemoryBudget.CreateScope();
        Assert.True(owner.TryReserve(1000L, out IMemoryReservation? receipt));
        long before = root.ReservedBytes;
        Assert.False(receipt.TryResize(32000L));
        Assert.False(owner.TryReserve(32000L, out IMemoryReservation? refused)); Assert.Null(refused);
        Assert.Equal(1000L, receipt.Bytes); Assert.Equal(before, root.ReservedBytes);
        Assert.Equal(1000L, governor.Snapshot().LiveMemoryBytes);
        Assert.True(owner.TryReserve(1L, out _));
        Assert.True(receipt.TryResize(500L)); receipt.Dispose(); context.Close();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringAdmissionRefundsOnlyTheAttemptedIncrease(bool growth)
    {
        using CancellationTokenSource cancellation = new();
        RecordingMemoryBudget root = new(100000L);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, new CancelingBudget(root, cancellation, growth));
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token, host);
        QpdfJpegCodecContext context = new(governor);
        if (growth)
        {
            Assert.True(context.MemoryBudget.TryReserve(1000L, out IMemoryReservation? receipt));
            Assert.ThrowsAny<OperationCanceledException>(() => receipt.TryResize(64000L));
            Assert.Equal(1000L, receipt.Bytes); Assert.Equal(1000L, governor.Snapshot().LiveMemoryBytes);
            Assert.True(receipt.TryResize(0L)); receipt.Dispose();
        }
        else { Assert.ThrowsAny<OperationCanceledException>(() => context.MemoryBudget.TryReserve(64000L, out _)); }
        Assert.True(cancellation.IsCancellationRequested);
        context.Close(); Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public void CancellationStillAllowsShrinkAndCleanup()
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        QpdfJpegCodecContext context = new(governor);
        Assert.True(context.MemoryBudget.TryReserve(2000L, out IMemoryReservation? receipt));
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => receipt.TryResize(3000L));
        Assert.ThrowsAny<OperationCanceledException>(() => context.MemoryBudget.TryReserve(0L, out _));
        Assert.True(receipt.TryResize(2000L)); Assert.True(receipt.TryResize(1000L));
        Assert.Equal(1000L, governor.Snapshot().LiveMemoryBytes);
        receipt.Dispose(); context.Close(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ClosedOwnersInvalidateEscapedHandlesWithoutRefundingOtherStorage()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfJpegCodecContext context = new(governor);
        IMemoryBudget borrowed = context.MemoryBudget;
        IMemoryScope child = borrowed.CreateScope();
        Assert.True(child.TryReserve(1000L, out IMemoryReservation? receipt));
        Assert.Throws<ArgumentOutOfRangeException>(() => receipt.TryResize(-1L));
        Assert.Throws<ArgumentOutOfRangeException>(() => child.TryReserve(-1L, out _));
        Assert.True(receipt.TryResize(6000L));
        context.Close(); context.Close(); governor.ReserveMemory(777L);
        Assert.Equal(0L, receipt.Bytes);
        Assert.Throws<ObjectDisposedException>(() => receipt.TryResize(1L));
        Assert.Throws<ObjectDisposedException>(() => child.CreateScope());
        Assert.Throws<ObjectDisposedException>(() => borrowed.TryReserve(1L, out _));
        Assert.Throws<ObjectDisposedException>(() => context.MemoryBudget);
        receipt.Dispose(); receipt.Dispose(); child.Dispose();
        Assert.Equal(777L, governor.Snapshot().LiveMemoryBytes); governor.ReleaseMemory(777L);
    }

    [Fact]
    public void ConcurrentBudgetUsersFinishBeforeOwnerClosure()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfJpegCodecContext context = new(governor);
        IMemoryScope owner = context.MemoryBudget.CreateScope();
        Parallel.For(0, 100, _ =>
        {
            using IMemoryScope child = owner.CreateScope();
            Assert.True(child.TryReserve(1000L, out IMemoryReservation? receipt));
            Assert.True(receipt.TryResize(2000L)); Assert.True(receipt.TryResize(100L));
            Assert.Equal(100L, receipt.Bytes);
        });
        owner.Dispose(); context.Close();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(134217728L)]
    [InlineData(long.MaxValue)]
    public void OversizedEstimatesRefuseWithoutChangingExistingReceipts(long ceiling)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = ceiling }, CancellationToken.None);
        QpdfJpegCodecContext context = new(governor);
        Assert.True(context.MemoryBudget.TryReserve(1L, out IMemoryReservation? first));
        Assert.True(context.MemoryBudget.TryReserve(1L, out IMemoryReservation? second));
        Assert.False(first.TryResize(long.MaxValue));
        Assert.False(context.MemoryBudget.TryReserve(long.MaxValue, out IMemoryReservation? refused));
        Assert.Null(refused);
        Assert.Equal(1L, first.Bytes); Assert.Equal(1L, second.Bytes);
        Assert.True(first.TryResize(0L)); Assert.True(first.TryResize(1L));
        context.Close(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class CancelingBudget(IMemoryBudget inner, CancellationTokenSource cancellation, bool growth) : IMemoryBudget
    {
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            if (!inner.TryReserve(bytes, out IMemoryReservation? admitted)) { reservation = null; return false; }
            reservation = new Receipt(admitted, cancellation);
            if (!growth) { cancellation.Cancel(); }
            return true;
        }
        private sealed class Receipt(IMemoryReservation inner, CancellationTokenSource cancellation) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes)
            {
                long previous = inner.Bytes; bool accepted = inner.TryResize(bytes);
                if (accepted && bytes > previous) { cancellation.Cancel(); }
                return accepted;
            }
            public void Dispose() => inner.Dispose();
        }
    }
}
