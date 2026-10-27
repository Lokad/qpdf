using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class JpegContextMemoryTests
{
    [Fact]
    public void ResizeTracksLiveCapacityAndRefundsTheResizedAmountOnce()
    {
        RecordingMemoryBudget root = new(100000L);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, root);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
        QpdfJpegCodecContext context = new(governor);
        using IDisposable first = context.ReserveMemory(1000L);
        using IDisposable second = context.ReserveMemory(2000L);
        context.ResizeMemory(first, 64000L);
        Assert.Equal(66000L, governor.Snapshot().LiveMemoryBytes);
        Assert.InRange(root.ReservedBytes, 66000L, 66000L + 16383L);
        context.ResizeMemory(first, 0L);
        Assert.Equal(2000L, governor.Snapshot().LiveMemoryBytes);
        context.ResizeMemory(first, 0L);
        context.ResizeMemory(first, 1000L);
        first.Dispose(); first.Dispose();
        Assert.Equal(2000L, governor.Snapshot().LiveMemoryBytes);
        second.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, root.ReservedBytes);
        Assert.Equal(0, root.LiveReservations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalAndSharedRefusalLeaveTheOriginalReservationIntact(bool shared)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = shared ? 100000L : 16000L };
        RecordingMemoryBudget root = new(shared ? 16000L : 100000L);
        using InMemoryQpdfHost host = new(limits, null, root);
        ResourceGovernor governor = new(limits, CancellationToken.None, host);
        QpdfJpegCodecContext context = new(governor);
        using IDisposable reservation = context.ReserveMemory(1000L);
        long previous = root.ReservedBytes;
        Exception? failure = Record.Exception(() => context.ResizeMemory(reservation, 32000L));
        if (shared) { Assert.IsType<QpdfMemoryAdmissionException>(failure); }
        else { Assert.IsType<QpdfLimitException>(failure); }
        Assert.Equal(1000L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(previous, root.ReservedBytes);
        context.ResizeMemory(reservation, 500L);
        reservation.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationFromAdmissionRefundsTheIncreaseBeforeReturning(bool growth)
    {
        using CancellationTokenSource cancellation = new();
        RecordingMemoryBudget root = new(100000L);
        CancelingBudget budget = new(root, cancellation, growth);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token, host);
        QpdfJpegCodecContext context = new(governor);
        if (growth)
        {
            using IDisposable reservation = context.ReserveMemory(1000L);
            Assert.ThrowsAny<OperationCanceledException>(() => context.ResizeMemory(reservation, 64000L));
            Assert.Equal(1000L, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(1024L, root.ReservedBytes);
            context.ResizeMemory(reservation, 0L);
        }
        else { Assert.ThrowsAny<OperationCanceledException>(() => context.ReserveMemory(64000L)); }
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public void CanceledGrowthRejectsButShrinkAndDisposalFinishCleanup()
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        QpdfJpegCodecContext context = new(governor);
        using IDisposable reservation = context.ReserveMemory(2000L);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => context.ResizeMemory(reservation, 3000L));
        Assert.ThrowsAny<OperationCanceledException>(() => context.ReserveMemory(0L));
        Assert.Equal(2000L, governor.Snapshot().LiveMemoryBytes);
        context.ResizeMemory(reservation, 2000L);
        context.ResizeMemory(reservation, 1000L);
        Assert.Equal(1000L, governor.Snapshot().LiveMemoryBytes);
        reservation.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void InvalidForeignAndClosedReservationsCannotAlterAccounting()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfJpegCodecContext context = new(governor), other = new(governor);
        using IDisposable reservation = context.ReserveMemory(1000L);
        using IDisposable foreign = other.ReserveMemory(2000L);
        using MemoryStream unrelated = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => context.ResizeMemory(reservation, -1L));
        Assert.Throws<ArgumentException>(() => context.ResizeMemory(unrelated, 0L));
        Assert.Throws<ArgumentException>(() => context.ResizeMemory(foreign, 0L));
        Assert.Equal(3000L, governor.Snapshot().LiveMemoryBytes);
        reservation.Dispose();
        Assert.Throws<ObjectDisposedException>(() => context.ResizeMemory(reservation, 1000L));
        context.Close(); context.Close();
        Assert.Throws<ObjectDisposedException>(() => context.ResizeMemory(foreign, 0L));
        Assert.Equal(2000L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ContextClosureRetiresOutstandingResizedStorageWithoutTouchingOtherOwners()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfJpegCodecContext context = new(governor);
        IDisposable first = context.ReserveMemory(1000L), second = context.ReserveMemory(100L);
        context.ResizeMemory(first, 6000L);
        context.Close(); context.Close();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        governor.ReserveMemory(777L);
        first.Dispose(); second.Dispose();
        Assert.Equal(777L, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => context.ResizeMemory(first, 1L));
        governor.ReleaseMemory(777L);
    }

    [Fact]
    public void AggregateOverflowPreservesAllExistingReceipts()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = long.MaxValue }, CancellationToken.None);
        QpdfJpegCodecContext context = new(governor);
        using IDisposable first = context.ReserveMemory(1L), second = context.ReserveMemory(1L);
        Assert.Throws<OverflowException>(() => context.ResizeMemory(first, long.MaxValue));
        Assert.Equal(2L, governor.Snapshot().LiveMemoryBytes);
        context.ResizeMemory(first, 0L);
        second.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
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
                long previous = inner.Bytes;
                bool accepted = inner.TryResize(bytes);
                if (accepted && bytes > previous) { cancellation.Cancel(); }
                return accepted;
            }
            public void Dispose() => inner.Dispose();
        }
    }
}
