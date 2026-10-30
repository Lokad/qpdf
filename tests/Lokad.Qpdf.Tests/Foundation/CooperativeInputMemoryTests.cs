using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Runtime;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class CooperativeInputMemoryTests
{
    [Theory]
    [InlineData(20000)]
    [InlineData(17)]
    public async Task ShortReadsNeedOnlyOnePreAdmittedDestination(int chunkSize)
    {
        RecordingMemoryBudget root = new(32768L);
        using IMemoryScope caller = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new BudgetHost(caller));
        governor.ReserveMemory(20032L);
        byte[] expected = Enumerable.Range(0, 20000).Select(value => (byte)value).ToArray();
        Input input = new(expected, chunkSize);
        byte[] read = await QpdfInputReader.ReadAdmittedAsync(input, 0L, expected.Length, governor, CancellationToken.None);
        Assert.Equal(expected, read);
        Assert.Same(read, input.LastArray);
        Assert.Equal(20032L, governor.Snapshot().PeakMemoryBytes);
        Assert.Equal(1, root.ReserveCalls);
        Assert.Equal(0, root.ResizeCalls);
        governor.ReleaseMemory(20032L);
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task RefusalPrecedesTheFirstReadAndLeavesOtherChargesIntact()
    {
        RecordingMemoryBudget root = new(4096L);
        using IMemoryScope caller = root.CreateScope();
        Assert.True(caller.TryReserve(256L, out IMemoryReservation? held));
        using (held)
        {
            Input input = new(new byte[4096], 17);
            QpdfMemoryAdmissionException failure = await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(
                () => QpdfInputReader.ReadExactlyToArrayAsync(input, 0L, 4096, caller, CancellationToken.None).AsTask());
            Assert.Equal(4128L, failure.RequestedBytes);
            Assert.Equal(0, input.Reads);
            Assert.Equal(256L, root.ReservedBytes);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(17)]
    public async Task AllocatingReadRetainsOneChargeUntilCallerOwnerCloses(int chunkSize)
    {
        RecordingMemoryBudget root = new(4128L);
        IMemoryScope caller = root.CreateScope();
        byte[] expected = Enumerable.Range(0, 4096).Select(index => (byte)index).ToArray();
        Input input = new(expected, chunkSize);
        byte[] result = await QpdfInputReader.ReadExactlyToArrayAsync(input, 0L, expected.Length, caller, CancellationToken.None);
        Assert.Equal(expected, result);
        Assert.Equal(4128L, root.ReservedBytes);
        Assert.Equal(1, root.ReserveCalls);
        Assert.Equal(0, root.ResizeCalls);
        Assert.Equal(4128L, root.PeakBytes);
        Assert.Same(result, input.LastArray);
        Assert.False(input.Disposed);
        caller.Dispose(); caller.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task CanceledPendingReadKeepsItsChargeUntilTheInputFinishes()
    {
        RecordingMemoryBudget root = new(4128L);
        using IMemoryScope caller = root.CreateScope();
        using CancellationTokenSource cancellation = new();
        PendingInput input = new();
        Task<byte[]> read = QpdfInputReader.ReadExactlyToArrayAsync(input, 0L, 4096, caller, cancellation.Token).AsTask();
        await input.Started.Task;
        cancellation.Cancel();
        Assert.False(read.IsCompleted);
        Assert.Equal(4128L, root.ReservedBytes);
        input.Completion.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(0L, root.ReservedBytes);
        await input.DisposeAsync();
        Assert.True(input.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4097)]
    public async Task InvalidHostCountsAreTransportFailuresAndRefundAdmission(int count)
    {
        RecordingMemoryBudget root = new(4128L);
        using IMemoryScope caller = root.CreateScope();
        Input broken = new(new byte[4096], count) { InvalidCount = true };
        QpdfHostException error = await Assert.ThrowsAsync<QpdfHostException>(() =>
            QpdfInputReader.ReadExactlyToArrayAsync(broken, 0L, 4096, caller, CancellationToken.None).AsTask());
        Assert.Equal(QpdfHostFailureReason.NonProgressingRead, error.Reason);
        Assert.Equal(0L, root.ReservedBytes);
        byte[] result = await QpdfInputReader.ReadExactlyToArrayAsync(new Input(new byte[4096], 17),
            0L, 4096, caller, CancellationToken.None);
        Assert.Equal(4096, result.Length);
        Assert.Equal(4128L, root.ReservedBytes);
    }

    [Fact]
    public async Task CallerBufferCanBeReusedForNonsequentialReads()
    {
        Input input = new([1, 2, 3, 4, 5, 6], 1);
        byte[] destination = new byte[2];
        await QpdfInputReader.ReadExactlyAsync(input, 4, destination, CancellationToken.None);
        Assert.Equal(new byte[] { 5, 6 }, destination);
        await QpdfInputReader.ReadExactlyAsync(input, 1, destination, CancellationToken.None);
        Assert.Equal(new byte[] { 2, 3 }, destination);
        Assert.Same(destination, input.LastArray);
    }

    [Fact]
    public async Task InvalidAndEmptyRangesDoNotAcquireStorage()
    {
        RecordingMemoryBudget root = new(0L);
        using IMemoryScope caller = root.CreateScope();
        Input input = new([1], 1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => QpdfInputReader.ReadExactlyToArrayAsync(
            input, 0L, -1, caller, CancellationToken.None).AsTask());
        Assert.Empty(await QpdfInputReader.ReadExactlyToArrayAsync(input, 1, 0, caller, CancellationToken.None));
        await QpdfInputReader.ReadExactlyAsync(input, 0, Memory<byte>.Empty, CancellationToken.None);
        Assert.Equal(0, input.Reads);
        Assert.Equal(0, root.ReserveCalls);
    }

    [Fact]
    public async Task CancellationDuringAdmissionRefundsBeforeHostAcquisition()
    {
        RecordingMemoryBudget root = new(4128L);
        using IMemoryScope caller = root.CreateScope();
        using CancellationTokenSource cancellation = new();
        Input input = new(new byte[4096], 17);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QpdfInputReader.ReadExactlyToArrayAsync(
            input, 0, 4096, new CancelingBudget(caller, cancellation), cancellation.Token).AsTask());
        Assert.Equal(0, input.Reads);
        Assert.Equal(0L, root.ReservedBytes);
    }

    private sealed class CancelingBudget(IMemoryBudget inner, CancellationTokenSource cancellation) : IMemoryBudget
    {
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        { bool accepted = inner.TryReserve(bytes, out reservation); cancellation.Cancel(); return accepted; }
    }

    private sealed class BudgetHost(IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Input(byte[] data, int chunkSize) : IQpdfInput
    {
        public QpdfPath Path => QpdfPath.From("input");
        public string Identity => "input";
        public long Length => data.Length;
        internal int Reads { get; private set; }
        internal byte[]? LastArray { get; private set; }
        internal bool InvalidCount { get; init; }
        internal bool Disposed { get; private set; }
        public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++;
            if (InvalidCount) { return new(chunkSize); }
            int take = Math.Min(destination.Length, Math.Min(chunkSize, data.Length - (int)offset));
            data.AsMemory((int)offset, take).CopyTo(destination);
            Assert.True(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)destination, out ArraySegment<byte> segment));
            if (LastArray is not null) { Assert.Same(LastArray, segment.Array); }
            LastArray = segment.Array;
            return new(take);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class PendingInput : IQpdfInput
    {
        public QpdfPath Path => QpdfPath.From("pending");
        public string Identity => "pending";
        public long Length => 4096L;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Completion.Task;
            Assert.False(Disposed);
            destination.Span.Fill(42); // The host still uses the buffer after cancellation was requested.
            return destination.Length;
        }
        public ValueTask DisposeAsync() { Assert.True(Completion.Task.IsCompleted); Disposed = true; return ValueTask.CompletedTask; }
    }
}
