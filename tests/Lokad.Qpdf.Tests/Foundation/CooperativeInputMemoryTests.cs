using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class CooperativeInputMemoryTests
{
    [Fact]
    public async Task RefusalPrecedesTheFirstReadAndLeavesOtherChargesIntact()
    {
        RecordingMemoryBudget root = new(4096L);
        using IMemoryScope parent = root.CreateScope();
        Assert.True(parent.TryReserve(256L, out IMemoryReservation? held));
        using (held)
        {
            Input input = new(new byte[4096], 4096);
            QpdfMemoryAdmissionException failure = await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(
                () => QpdfInputReader.ReadExactlyAsync(input, 0L, 4096, parent, CancellationToken.None).AsTask());
            Assert.Equal(4128L, failure.RequestedBytes);
            Assert.Equal(0, input.Reads);
            Assert.Equal(256L, root.ReservedBytes);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(4096, false)]
    [InlineData(17, false)]
    [InlineData(4096, true)]
    public async Task SuccessfulReadRetainsOnlyTheResultAndBatchesAdmission(int chunkSize, bool sliced)
    {
        byte[] expected = Enumerable.Range(0, 4096).Select(index => (byte)index).ToArray();
        RecordingMemoryBudget root = new(8256L);
        IMemoryScope caller = root.CreateScope();
        Input input = new(expected, chunkSize) { Sliced = sliced };
        byte[] result = await QpdfInputReader.ReadExactlyAsync(input, 0L, expected.Length, caller, CancellationToken.None);
        Assert.Equal(expected, result);
        Assert.Equal(4128L, root.ReservedBytes);
        Assert.Equal(1, root.ReserveCalls);
        Assert.Equal(chunkSize < expected.Length || sliced ? 2 : 0, root.ResizeCalls);
        Assert.Equal(chunkSize < expected.Length || sliced ? 8256L : 4128L, root.PeakBytes);
        Assert.False(input.Disposed);
        if (!sliced && chunkSize == expected.Length) { Assert.Same(input.LastArray, result); }
        caller.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
        caller.Dispose();
    }

    [Fact]
    public async Task AssemblyRefusalDoesNotRetryAndRefundsTheFirstRead()
    {
        RecordingMemoryBudget root = new(4128L);
        using IMemoryScope caller = root.CreateScope();
        Input input = new(new byte[4096], 17);
        QpdfMemoryAdmissionException failure = await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(
            () => QpdfInputReader.ReadExactlyAsync(input, 0L, 4096, caller, CancellationToken.None).AsTask());
        Assert.Equal(8256L, failure.RequestedBytes);
        Assert.Equal(1, input.Reads);
        Assert.Equal(new long[] { 4128L, 8256L }, root.Requests);
        Assert.Equal(0L, root.ReservedBytes);
        Assert.False(input.Disposed);
    }

    [Fact]
    public async Task CanceledPendingReadKeepsItsChargeUntilTheInputFinishes()
    {
        RecordingMemoryBudget root = new(4128L);
        using IMemoryScope caller = root.CreateScope();
        using CancellationTokenSource cancellation = new();
        PendingInput input = new();
        Task<byte[]> read = QpdfInputReader.ReadExactlyAsync(input, 0L, 4096, caller, cancellation.Token).AsTask();
        await input.Started.Task;
        cancellation.Cancel();
        Assert.False(read.IsCompleted);
        Assert.Equal(4128L, root.ReservedBytes);
        input.Completion.SetResult(new byte[4096]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task ReadFailureRefundsAdmissionAndAllowsTheNextCall()
    {
        RecordingMemoryBudget root = new(4128L);
        using IMemoryScope caller = root.CreateScope();
        Input broken = new(new byte[4096], 0);
        await Assert.ThrowsAsync<QpdfHostException>(
            () => QpdfInputReader.ReadExactlyAsync(broken, 0L, 4096, caller, CancellationToken.None).AsTask());
        Assert.Equal(0L, root.ReservedBytes);
        byte[] result = await QpdfInputReader.ReadExactlyAsync(new Input(new byte[4096], 4096),
            0L, 4096, caller, CancellationToken.None);
        Assert.Equal(4096, result.Length);
        Assert.Equal(4128L, root.ReservedBytes);
    }

    [Fact]
    public async Task IndependentOwnersShareTheRootAndAncestorDisposalInvalidatesDescendants()
    {
        RecordingMemoryBudget root = new(4128L);
        IMemoryScope first = root.CreateScope();
        using IMemoryScope second = root.CreateScope();
        await QpdfInputReader.ReadExactlyAsync(new Input(new byte[4096], 4096),
            0L, 4096, first, CancellationToken.None);
        await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => QpdfInputReader.ReadExactlyAsync(
            new Input(new byte[4096], 4096), 0L, 4096, second, CancellationToken.None).AsTask());
        IMemoryScope descendant = first.CreateScope();
        Assert.True(descendant.TryReserve(0L, out IMemoryReservation? zero));
        first.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
        Assert.Equal(0L, zero.Bytes);
        Assert.Throws<ObjectDisposedException>(() => zero.TryResize(1L));
        Assert.Throws<ObjectDisposedException>(() => descendant.TryReserve(0L, out _));
        zero.Dispose(); descendant.Dispose();
        await QpdfInputReader.ReadExactlyAsync(new Input(new byte[4096], 4096),
            0L, 4096, second, CancellationToken.None);
        Assert.Equal(4128L, root.ReservedBytes);
    }

    [Fact]
    public async Task InvalidAndEmptyRangesDoNotAcquireStorage()
    {
        RecordingMemoryBudget root = new(0L);
        using IMemoryScope caller = root.CreateScope();
        Input input = new(new byte[1], 1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => QpdfInputReader.ReadExactlyAsync(
            input, 0L, -1, caller, CancellationToken.None).AsTask());
        byte[] empty = await QpdfInputReader.ReadExactlyAsync(input, 0L, 0, caller, CancellationToken.None);
        Assert.Empty(empty);
        Assert.Equal(0, input.Reads);
        Assert.Equal(0, root.ReserveCalls);
    }

    private sealed class Input(byte[] data, int chunkSize) : IQpdfInput
    {
        public QpdfPath Path => QpdfPath.From("input");
        public string Identity => "input";
        public long Length => data.Length;
        internal int Reads { get; private set; }
        internal byte[]? LastArray { get; private set; }
        internal bool Sliced { get; init; }
        internal bool Disposed { get; private set; }

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            int take = Math.Min(count, chunkSize);
            byte[] result = new byte[take + (Sliced ? 8 : 0)];
            data.AsSpan((int)offset, take).CopyTo(result.AsSpan(Sliced ? 8 : 0));
            LastArray = result;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(result.AsMemory(Sliced ? 8 : 0, take));
        }

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class PendingInput : IQpdfInput
    {
        public QpdfPath Path => QpdfPath.From("pending");
        public string Identity => "pending";
        public long Length => 4096L;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ReadOnlyMemory<byte>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellationToken)
        {
            Started.SetResult();
            return await Completion.Task;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
