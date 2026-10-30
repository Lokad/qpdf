using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class JpegConcurrentAccountingTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("cancel")]
    [InlineData("failure")]
    public void ConcurrentScratchAndOutputRetainExactlyTheReturnedStorage(string outcome)
    {
        const int size = 256;
        byte[] jpeg = JpegFixtures.Encode(size, size, 1, 75, (x, y, _) => (byte)(x * 37 ^ y * 139));
        RecordingMemoryBudget root = new(1048576L, false);
        using IMemoryScope invocation = root.CreateScope();
        YieldingBudget budget = new(invocation);
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token, new Host(budget))
        { JpegCodec = new ConcurrentCodec(jpeg, outcome, cancellation) };
        byte[]? result = null;
        Exception? failure = Record.Exception(() => result = JpegCodec.EncodeGray(new byte[size * size],
            size, size, 75, QpdfLimits.Default, governor));
        if (outcome == "success")
        {
            Assert.Null(failure); Assert.NotNull(result); Assert.Equal(jpeg, result);
            long retained = result.Length + JpegCodec.SampleArrayOverheadBytes;
            Assert.Equal(retained, governor.Snapshot().LiveMemoryBytes);
            Assert.InRange(root.ReservedBytes, retained, retained + 2L * SharedMemoryOwner.BlockBytes - 1L);
            governor.ReleaseMemory(retained);
        }
        else if (outcome == "cancel") { Assert.IsAssignableFrom<OperationCanceledException>(failure); }
        else { Assert.IsType<QpdfFilterException>(failure); }
        Assert.Equal(1, budget.PeakCallbacks);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0, root.LiveReservations);
    }

    private sealed class Host(IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class ConcurrentCodec(byte[] jpeg, string outcome, CancellationTokenSource cancellation) : IQpdfJpegCodec
    {
        public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
            => throw new NotSupportedException();

        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
        {
            using Barrier start = new(2);
            Task worker = Task.Factory.StartNew(() =>
            {
                using IMemoryScope parent = context.MemoryBudget.CreateScope();
                start.SignalAndWait();
                for (int i = 0; i < 128; i++)
                {
                    using IMemoryScope scratch = parent.CreateScope();
                    Assert.True(scratch.TryReserve(64000L, out IMemoryReservation? reservation));
                    Assert.True(reservation.TryResize(65568L));
                    byte[] bytes = new byte[65536]; bytes.AsSpan().Fill((byte)i);
                    Thread.Yield(); GC.KeepAlive(bytes);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                start.SignalAndWait();
                for (int offset = 0; offset < jpeg.Length; offset += 1024)
                {
                    if (outcome == "cancel" && offset >= jpeg.Length / 2) { cancellation.Cancel(); }
                    encoded.Write(jpeg.AsSpan(offset, Math.Min(1024, jpeg.Length - offset)));
                    Thread.Yield();
                }
            }
            finally { worker.GetAwaiter().GetResult(); }
            if (outcome == "failure") { throw new InvalidDataException("Injected codec failure after joining its worker."); }
        }
    }

    // Yield inside admission callbacks to expose interleavings. The assertions
    // concern accounting and cleanup, irrespective of which thread runs first.
    private sealed class YieldingBudget(IMemoryBudget inner) : IMemoryBudget
    {
        private int _active, _peak;
        internal int PeakCallbacks => Volatile.Read(ref _peak);
        public IMemoryScope CreateScope() => inner.CreateScope();
        private void Enter()
        {
            int active = Interlocked.Increment(ref _active);
            int previous = Volatile.Read(ref _peak);
            while (previous < active)
            {
                int observed = Interlocked.CompareExchange(ref _peak, active, previous);
                if (observed == previous) { break; }
                previous = observed;
            }
            Thread.Yield();
        }
        private void Exit() => Interlocked.Decrement(ref _active);
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            Enter();
            try
            {
                if (!inner.TryReserve(bytes, out IMemoryReservation? admitted)) { reservation = null; return false; }
                try { reservation = new Receipt(admitted, this); return true; }
                catch { admitted.Dispose(); throw; }
            }
            finally { Exit(); }
        }
        private sealed class Receipt(IMemoryReservation inner, YieldingBudget owner) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes)
            { owner.Enter(); try { return inner.TryResize(bytes); } finally { owner.Exit(); } }
            public void Dispose()
            { owner.Enter(); try { inner.Dispose(); } finally { owner.Exit(); } }
        }
    }
}
