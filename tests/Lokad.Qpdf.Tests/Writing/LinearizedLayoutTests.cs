using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LinearizedLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoredOffsetsLengthsAndMembersStayFundedUntilDisposal(bool streamed)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope priorOwner = ParserMemoryScope.Create(governor);
        priorOwner.Reserve(97L);
        long prior = governor.Snapshot().LiveMemoryBytes;
        using LinearizedLayout layout = LinearizedLayout.Create(governor, streamed, CancellationToken.None);
        for (int index = 0; index < 512; index++)
        {
            int number = index * 1024 + 1;
            layout.RecordStart(number, (1L << 40) + index);
            layout.RecordLength(number, (1L << 33) + index);
            if (streamed) { layout.RecordMember(number, (index + 7, index)); }
        }
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.True(live >= prior + 512L * 16L * (streamed ? 3 : 2));
        Assert.True(governor.Snapshot().TotalWork > 512L * 512L / 2L);
        for (int index = 0; index < 512; index++)
        {
            int number = index * 1024 + 1;
            Assert.Equal((1L << 40) + index, layout.Starts[number]);
            Assert.Equal((1L << 33) + index, layout.Lengths[number]);
            Assert.Equal(streamed, layout.TryGetMember(number, out (int Container, int Index) member));
            if (streamed) { Assert.Equal((index + 7, index), member); }
        }
        layout.Dispose();
        Assert.Equal(prior, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => layout.RecordStart(1, 0L));
        Assert.Throws<ObjectDisposedException>(() => layout.RecordLength(1, 0L));
        Assert.Throws<ObjectDisposedException>(() => layout.TryGetMember(1, out _));
        layout.Dispose();
        Assert.Equal(prior, governor.Snapshot().LiveMemoryBytes);
        priorOwner.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, 600L)]
    [InlineData(true, 740L)]
    public void FactoryAdmissionFailurePreservesPriorOwner(bool streamed, long ceiling)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = ceiling };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope priorOwner = ParserMemoryScope.Create(governor);
        priorOwner.Reserve(97L);
        long prior = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
            LinearizedLayout.Create(governor, streamed, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(prior, governor.Snapshot().LiveMemoryBytes);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, prior + 1L, ceiling);
        priorOwner.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedGrowthPreservesOldBackingAndCanRetry(bool streamed)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope priorOwner = ParserMemoryScope.Create(governor);
        priorOwner.Reserve(97L);
        using LinearizedLayout layout = LinearizedLayout.Create(governor, streamed, CancellationToken.None);
        for (int number = 1; number <= 4; number++)
        {
            layout.RecordStart(number, number * 100L);
            layout.RecordLength(number, number * 17L);
            if (streamed) { layout.RecordMember(number, (9, number)); }
        }
        long filler = limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - 223L;
        priorOwner.Reserve(filler);
        long prior = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => layout.RecordStart(5, 500L));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(prior, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(100L, layout.Starts[1]);
        Assert.Equal(400L, layout.Starts[4]);
        Assert.False(layout.Starts.ContainsKey(5));
        Assert.Equal(68L, layout.Lengths[4]);
        if (streamed)
        {
            Assert.True(layout.TryGetMember(4, out (int Container, int Index) member));
            Assert.Equal((9, 4), member);
        }
        priorOwner.Release(filler);
        layout.RecordStart(5, 500L);
        Assert.Equal(500L, layout.Starts[5]);
        layout.Dispose();
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollisionLookupFailsAtFiniteWorkAndCleanupPreservesPriorOwner(bool streamed)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 65536L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope priorOwner = ParserMemoryScope.Create(governor);
        priorOwner.Reserve(97L);
        using LinearizedLayout layout = LinearizedLayout.Create(governor, streamed, CancellationToken.None);
        for (int index = 0; index < 128; index++) { layout.RecordStart(index * 1024 + 1, index); }
        long live = governor.Snapshot().LiveMemoryBytes;
        governor.ChargeWork(limits.MaxWork - governor.Snapshot().TotalWork - 2L);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => layout.Starts.TryGetValue(128 * 1024 + 1, out _));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        Assert.Equal(limits.MaxWork, governor.Snapshot().TotalWork);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        layout.Dispose();
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterObservedIndexWorkStopsEitherTokenRoute(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default,
            governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope priorOwner = ParserMemoryScope.Create(governor);
        priorOwner.Reserve(97L);
        using LinearizedLayout layout = LinearizedLayout.Create(governor, true,
            governorToken ? CancellationToken.None : cancellation.Token);
        for (int index = 0; index < 512; index++) { layout.RecordMember(index * 1024 + 1, (7, index)); }
        long threshold = governor.Snapshot().TotalWork + 1024L;
        Task worker = Task.Run(() => { while (true) { layout.TryGetMember(512 * 1024 + 1, out _); } });
        try
        {
            long started = Stopwatch.GetTimestamp();
            while (governor.Snapshot().TotalWork < threshold && !worker.IsCompleted
                && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10)) { await Task.Yield(); }
            Assert.True(governor.Snapshot().TotalWork >= threshold);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            layout.Dispose();
            Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
        }
        finally
        {
            cancellation.Cancel();
            try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlreadyCanceledFactoryDoesNoWorkOrAdmission(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default,
            governorToken ? cancellation.Token : CancellationToken.None);
        Assert.ThrowsAny<OperationCanceledException>(() => LinearizedLayout.Create(governor, true,
            governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().TotalWork);
    }

    [Fact]
    public void ClassicLayoutsRejectCompressedMembersWithoutChangingCounters()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using LinearizedLayout layout = LinearizedLayout.Create(governor, false, CancellationToken.None);
        QpdfExecutionCounters before = governor.Snapshot();
        Assert.False(layout.TryGetMember(1, out _));
        Assert.Throws<InvalidOperationException>(() => layout.RecordMember(1, (9, 0)));
        Assert.Equal(before, governor.Snapshot());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IDisposable Owner, WeakReference[] Arrays) Filled(ResourceGovernor governor, bool streamed)
    {
        LinearizedLayout layout = LinearizedLayout.Create(governor, streamed, CancellationToken.None);
        for (int number = 1; number <= 512; number++)
        {
            layout.RecordStart(number, number);
            layout.RecordLength(number, number * 2L);
            if (streamed) { layout.RecordMember(number, (7, number)); }
        }
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        List<WeakReference> arrays = new();
        foreach (string field in new[] { "_starts", "_lengths", "_members" })
        {
            object? map = typeof(LinearizedLayout).GetField(field, instance)?.GetValue(layout);
            if (map is null) { continue; }
            object backing = map.GetType().GetField("_entries", instance)?.GetValue(map)
                ?? throw new InvalidOperationException("Expected admitted index backing.");
            arrays.Add(new WeakReference(backing));
        }
        return (layout, arrays.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedDisposedLayoutsReleaseBackingArrays(bool streamed)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        (IDisposable owner, WeakReference[] arrays) = Filled(governor, streamed);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.All(arrays, value => Assert.True(value.IsAlive));
        Assert.True(governor.Snapshot().LiveMemoryBytes > 512L * 16L);
        owner.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.All(arrays, value => Assert.False(value.IsAlive));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(owner);
    }
}
