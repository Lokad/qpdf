using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class BufferedSinkTests
{
    [Fact]
    public async Task CopiesHaveSeparateOwnersAndSurviveCaptureDisposal()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor);
        prior.Reserve(97L);
        using ParserMemoryScope copies = ParserMemoryScope.Create(governor);
        long before = governor.Snapshot().LiveMemoryBytes;
        BufferedSink capture = BufferedSink.Create(QpdfLimits.Default, governor, CancellationToken.None);
        byte[] source = new byte[10000];
        new Random(71).NextBytes(source);
        await capture.AppendAsync(source.AsMemory(0, 1000), CancellationToken.None);
        await capture.AppendAsync(source.AsMemory(1000), CancellationToken.None);
        byte[] first = capture.ToArray(copies, CancellationToken.None);
        byte[] second = capture.ToArray(copies, CancellationToken.None);
        Assert.Equal(source, first);
        Assert.Equal(source, second);
        Assert.NotSame(source, first);
        Assert.NotSame(first, second);
        first[0] ^= 255;
        Assert.Equal(source, second);
        capture.Dispose(); capture.Dispose();
        Assert.Equal(before + 2L * (source.Length + 32L), governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(2L * (source.Length + 32L), copies.ReservedBytes);
        Assert.Equal(source, second);
        copies.Dispose();
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task EmptyCaptureBorrowsTheSharedEmptyArray()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copies = ParserMemoryScope.Create(governor);
        using BufferedSink capture = BufferedSink.Create(QpdfLimits.Default, governor, CancellationToken.None);
        await capture.AppendAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        Assert.Same(Array.Empty<byte>(), capture.ToArray(copies, CancellationToken.None));
        Assert.Equal(0L, copies.ReservedBytes);
        capture.Dispose(); copies.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void LateFactoryAdmissionFailureRestoresAnExistingOwner()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 416L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor);
        prior.Reserve(97L);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => BufferedSink.Create(limits, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
    }

    [Fact]
    public async Task GrowthAdmissionFailurePreservesBytesAndCanRetry()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 50000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope copies = ParserMemoryScope.Create(governor);
        using BufferedSink capture = BufferedSink.Create(limits, governor, CancellationToken.None);
        byte[] first = Enumerable.Repeat((byte)17, 1024).ToArray();
        byte[] next = Enumerable.Repeat((byte)29, 8192).ToArray();
        await capture.AppendAsync(first, CancellationToken.None);
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        filler.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - (16384L + 31L));
        long live = governor.Snapshot().LiveMemoryBytes;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() =>
            capture.AppendAsync(next, CancellationToken.None).AsTask());
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.True(allocated < 16384L, $"Rejected growth allocated {allocated} bytes.");
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(first.Length, capture.Length);
        filler.Dispose();
        Assert.Equal(first, capture.ToArray(copies, CancellationToken.None));
        await capture.AppendAsync(next, CancellationToken.None);
        Assert.Equal(first.Concat(next).ToArray(), capture.ToArray(copies, CancellationToken.None));
        capture.Dispose(); copies.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task FailedGrowthCopyRetiresOverlapAndPreservesTheOldBacking()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 36864L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using BufferedSink capture = BufferedSink.Create(limits, governor, CancellationToken.None);
        byte[] source = Enumerable.Repeat((byte)73, 8192).ToArray();
        await capture.AppendAsync(source, CancellationToken.None);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object stream = typeof(BufferedSink).GetField("_buffer", flags)?.GetValue(capture)
            ?? throw new InvalidOperationException("Expected stream.");
        byte[] backing = stream.GetType().GetField("_buffer", flags)?.GetValue(stream) as byte[]
            ?? throw new InvalidOperationException("Expected backing.");
        long live = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() =>
            capture.AppendAsync(new byte[1], CancellationToken.None).AsTask());
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().PeakMemoryBytes >= live + 16384L + 32L);
        Assert.Equal(8192L, capture.Length);
        Assert.Same(backing, stream.GetType().GetField("_buffer", flags)?.GetValue(stream));
        Assert.Equal(source, backing);
        capture.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task ReturnedCopyAdmitsOverlapBeforeAllocationAndCanRetry()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 200000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope copies = ParserMemoryScope.Create(governor);
        copies.Reserve(41L);
        using BufferedSink capture = BufferedSink.Create(limits, governor, CancellationToken.None);
        byte[] source = new byte[65536];
        new Random(83).NextBytes(source);
        await capture.AppendAsync(source, CancellationToken.None);
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        filler.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - (source.Length + 31L));
        long live = governor.Snapshot().LiveMemoryBytes;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => capture.ToArray(copies, CancellationToken.None));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.True(allocated < source.Length / 2L, $"Rejected copy allocated {allocated} bytes.");
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(41L, copies.ReservedBytes);
        filler.Dispose();
        Assert.Equal(source, capture.ToArray(copies, CancellationToken.None));
        capture.Dispose();
        Assert.Equal(41L + source.Length + 32L, copies.ReservedBytes);
    }

    [Fact]
    public async Task CopyWorkExhaustionRollsBackOnlyItsOwnReceipt()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 204800L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope copies = ParserMemoryScope.Create(governor);
        copies.Reserve(41L);
        using BufferedSink capture = BufferedSink.Create(limits, governor, CancellationToken.None);
        await capture.AppendAsync(new byte[65536], CancellationToken.None);
        long live = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => capture.ToArray(copies, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        Assert.Equal(limits.MaxWork, governor.Snapshot().TotalWork);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(41L, copies.ReservedBytes);
        Assert.Equal(65536L, capture.Length);
        capture.Dispose(); copies.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task OutputQuotaRejectsWithoutGrowingOrChangingTheCapture()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using BufferedSink capture = BufferedSink.Create(limits, governor, CancellationToken.None);
        await capture.AppendAsync(new byte[512], CancellationToken.None);
        long live = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() =>
            capture.AppendAsync(new byte[513], CancellationToken.None).AsTask());
        Assert.Equal(nameof(QpdfLimits.MaxOutputBytes), failure.LimitName);
        Assert.Equal(512L, capture.Length);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task CopyCannotTransferIntoAnotherJobOrADisposedOwner()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        ResourceGovernor other = new(QpdfLimits.Default, CancellationToken.None);
        using BufferedSink capture = BufferedSink.Create(QpdfLimits.Default, governor, CancellationToken.None);
        await capture.AppendAsync(new byte[37], CancellationToken.None);
        using ParserMemoryScope wrong = ParserMemoryScope.Create(other);
        using ParserMemoryScope disposed = ParserMemoryScope.Create(governor);
        disposed.Dispose();
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<InvalidOperationException>(() => capture.ToArray(wrong, CancellationToken.None));
        Assert.Throws<ObjectDisposedException>(() => capture.ToArray(disposed, CancellationToken.None));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, wrong.ReservedBytes);
        Assert.Equal(64L, other.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("governor")]
    [InlineData("factory")]
    public void AlreadyCanceledFactoryDoesNotAdmitOrAllocateStorage(string route)
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, route == "governor" ? cancellation.Token : CancellationToken.None);
        Assert.ThrowsAny<OperationCanceledException>(() => BufferedSink.Create(QpdfLimits.Default, governor,
            route == "factory" ? cancellation.Token : CancellationToken.None));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().TotalWork);
    }

    [Theory]
    [InlineData("governor", false)]
    [InlineData("factory", false)]
    [InlineData("operation", false)]
    [InlineData("governor", true)]
    [InlineData("factory", true)]
    [InlineData("operation", true)]
    public async Task CancellationAfterObservedByteWorkStopsEveryTokenRoute(string route, bool copying)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, route == "governor" ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor);
        prior.Reserve(97L);
        using BufferedSink capture = BufferedSink.Create(QpdfLimits.Default, governor,
            route == "factory" ? cancellation.Token : CancellationToken.None);
        byte[] chunk = new byte[8192];
        if (copying) { await capture.AppendAsync(chunk, CancellationToken.None); }
        CancellationToken token = route == "operation" ? cancellation.Token : CancellationToken.None;
        long threshold = governor.Snapshot().TotalWork + 65536L;
        Task worker = Task.Run(async () =>
        {
            while (true)
            {
                if (copying)
                {
                    using ParserMemoryScope owner = ParserMemoryScope.Create(governor);
                    _ = capture.ToArray(owner, token);
                }
                else { await capture.AppendAsync(chunk, token); }
            }
        });
        try
        {
            long started = Stopwatch.GetTimestamp();
            while (governor.Snapshot().TotalWork < threshold && !worker.IsCompleted
                && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10)) { await Task.Yield(); }
            Assert.True(governor.Snapshot().TotalWork >= threshold);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            capture.Dispose();
            Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
        }
        finally
        {
            cancellation.Cancel();
            try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(BufferedSink Sink, WeakReference Buffer)> Filled(ResourceGovernor governor)
    {
        BufferedSink sink = BufferedSink.Create(QpdfLimits.Default, governor, CancellationToken.None);
        await sink.AppendAsync(new byte[65536], CancellationToken.None);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object stream = typeof(BufferedSink).GetField("_buffer", flags)?.GetValue(sink)
            ?? throw new InvalidOperationException("Expected capture buffer.");
        object backing = stream.GetType().GetField("_buffer", flags)?.GetValue(stream)
            ?? throw new InvalidOperationException("Expected byte backing.");
        return (sink, new WeakReference(backing));
    }

    [Fact]
    public async Task RetainedDisposedSinkDropsItsBackingAndRejectsFurtherUse()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        (BufferedSink sink, WeakReference backing) = await Filled(governor);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(backing.IsAlive);
        sink.Dispose(); sink.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(backing.IsAlive);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor);
        Assert.Throws<ObjectDisposedException>(() => sink.Length);
        Assert.Throws<ObjectDisposedException>(() => sink.ToArray(owner, CancellationToken.None));
        Assert.Throws<ObjectDisposedException>(() => sink.AppendAsync(new byte[1], CancellationToken.None));
        GC.KeepAlive(sink);
    }
}
