using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Runtime;
using System.Diagnostics.CodeAnalysis;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class CooperativeInputMemoryTests
{
    [Theory]
    [InlineData(20000)]
    [InlineData(17)]
    public async Task EngineReadsChargeOnlyActualAssemblyOverlap(int chunkSize)
    {
        RecordingMemoryBudget root = new(65536L);
        using IMemoryScope caller = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new BudgetHost(caller));
        governor.ReserveMemory(20032L);
        byte[] expected = Enumerable.Range(0, 20000).Select(value => (byte)value).ToArray();
        byte[] read = await QpdfInputReader.ReadAdmittedAsync(new Input(expected, chunkSize), 0L,
            expected.Length, governor, CancellationToken.None);
        Assert.Equal(expected, read);
        Assert.Equal(20032L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(chunkSize == expected.Length ? 20032L : 40064L, governor.Snapshot().PeakMemoryBytes);
        // An active execution may retain one spare block to avoid scratch churn.
        Assert.InRange(root.ReservedBytes, 20032L, 20032L + 2L * SharedMemoryOwner.BlockBytes - 1L);
        Assert.Equal(1, root.ReserveCalls);
        Assert.InRange(root.ResizeCalls, 0, 2);
        governor.ReleaseMemory(20032L);
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task EngineAssemblyRefusalPreservesThePreAdmittedDestination()
    {
        RecordingMemoryBudget root = new(32768L);
        using IMemoryScope caller = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new BudgetHost(caller));
        governor.ReserveMemory(20032L);
        Input input = new(new byte[20000], 17);
        await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => QpdfInputReader.ReadAdmittedAsync(
            input, 0L, 20000, governor, CancellationToken.None).AsTask());
        Assert.Equal(1, input.Reads);
        Assert.Equal(20032L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(32768L, root.ReservedBytes);
        governor.ReleaseMemory(20032L);
        Assert.Equal(0L, root.ReservedBytes);
    }

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

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    public async Task AdmissionCancellationRefundsTheReadWithoutClosingCallerInput(int phase, int reads)
    {
        RecordingMemoryBudget root = new(4096L);
        using IMemoryScope caller = root.CreateScope();
        Assert.True(caller.TryReserve(128L, out IMemoryReservation? held));
        using (held)
        {
            using CancellationTokenSource cancellation = new();
            CancelingBudget budget = new(caller, cancellation, phase);
            Input input = new(new byte[] { 1, 2, 3, 4, 5 }, 2);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QpdfInputReader.ReadExactlyAsync(
                input, 0L, 5, budget, cancellation.Token).AsTask());
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(reads, input.Reads);
            Assert.Equal(128L, root.ReservedBytes);
            Assert.False(input.Disposed);
            byte[] next = await QpdfInputReader.ReadExactlyAsync(input, 0L, 5, caller, CancellationToken.None);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, next);
            Assert.Equal(165L, root.ReservedBytes);
            caller.Dispose();
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, next);
            Assert.Equal(0L, root.ReservedBytes);
        }
    }

    [Theory]
    [InlineData(false, 1, 20000)]
    [InlineData(true, 1, 20000)]
    [InlineData(false, 2, 65536)]
    [InlineData(true, 2, 65536)]
    public async Task EngineAssemblyAdmissionChecksBothCancellationTokens(bool cancelGovernor, int phase, int count)
    {
        RecordingMemoryBudget root = new(262144L);
        using IMemoryScope caller = root.CreateScope();
        using CancellationTokenSource cancellation = new();
        CancelingBudget budget = new(caller, cancellation, phase);
        ResourceGovernor governor = new(QpdfLimits.Default,
            cancelGovernor ? cancellation.Token : CancellationToken.None, new BudgetHost(budget));
        governor.ReserveMemory(count + 32L);
        Input input = new(new byte[count], 17000);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QpdfInputReader.ReadAdmittedAsync(
            input, 0L, count, governor, cancelGovernor ? CancellationToken.None : cancellation.Token).AsTask());
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(phase == 1 ? 1 : 4, input.Reads);
        Assert.False(input.Disposed);
        Assert.Equal(count + 32L, governor.Snapshot().LiveMemoryBytes);
        governor.ReleaseMemory(count + 32L);
        Assert.Equal(0L, root.ReservedBytes);
    }

    private sealed class CancelingBudget(IMemoryBudget inner, CancellationTokenSource cancellation, int phase) : IMemoryBudget
    {
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            if (!inner.TryReserve(bytes, out IMemoryReservation? admitted)) { reservation = null; return false; }
            reservation = new Reservation(admitted, cancellation, phase);
            if (phase == 0) { cancellation.Cancel(); }
            return true;
        }

        private sealed class Reservation(IMemoryReservation inner, CancellationTokenSource cancellation, int phase) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes)
            {
                long previous = inner.Bytes;
                bool accepted = inner.TryResize(bytes);
                if (accepted && (phase == 1 && bytes > previous || phase == 2 && bytes < previous)) { cancellation.Cancel(); }
                return accepted;
            }
            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class BudgetHost(IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
            => throw new NotSupportedException();
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
