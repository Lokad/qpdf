using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class BufferedSinkTests
{
    [Fact]
    public async Task EmptyCaptureHasNoWrittenBytes()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copies = ParserMemoryScope.Create(governor);
        using BufferedSink capture = BufferedSink.Create(QpdfLimits.Default, governor, CancellationToken.None);
        await capture.AppendAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        Assert.True(capture.WrittenMemory.IsEmpty);
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
        Assert.Equal(first, capture.WrittenMemory.ToArray());
        await capture.AppendAsync(next, CancellationToken.None);
        Assert.Equal(first.Concat(next).ToArray(), capture.WrittenMemory.ToArray());
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
    }

    [Theory]
    [InlineData("governor")]
    [InlineData("factory")]
    [InlineData("operation")]
    public async Task CancellationAfterObservedByteWorkStopsEveryTokenRoute(string route)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, route == "governor" ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor);
        prior.Reserve(97L);
        using BufferedSink capture = BufferedSink.Create(QpdfLimits.Default, governor,
            route == "factory" ? cancellation.Token : CancellationToken.None);
        byte[] chunk = new byte[8192];
        CancellationToken token = route == "operation" ? cancellation.Token : CancellationToken.None;
        TaskCompletionSource observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task worker = Task.Run(async () =>
        {
            while (true)
            {
                await PauseAfterStorageOperations();
                await capture.AppendAsync(chunk, token);
            }
        });
        async ValueTask PauseAfterStorageOperations()
        {
            if (capture.Length >= 65536L)
            {
                observed.TrySetResult();
                await resume.Task;
            }
        }
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            resume.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            capture.Dispose();
            Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
        }
        finally
        {
            cancellation.Cancel();
            resume.TrySetResult();
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
        Assert.Throws<ObjectDisposedException>(() => sink.Length);
        Assert.Throws<ObjectDisposedException>(() => sink.WrittenMemory);
        Assert.Throws<ObjectDisposedException>(() => sink.AppendAsync(new byte[1], CancellationToken.None));
        GC.KeepAlive(sink);
    }
}
