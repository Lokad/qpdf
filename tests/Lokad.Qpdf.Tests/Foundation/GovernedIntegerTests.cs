using System.Reflection;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class GovernedIntegerTests
{
    private static ResourceGovernor Governor(QpdfLimits limits) => new(limits, CancellationToken.None);

    [Fact]
    public void CollisionChainsPreserveUpdatesRemovalReuseAndEveryIntegerKey()
    {
        ResourceGovernor governor = Governor(QpdfLimits.Default);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        GovernedIntDictionary<string> map = GovernedIntDictionary<string>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
        int[] keys = [0, 32, 64, 96, 128, int.MinValue, int.MaxValue, -1];
        foreach (int key in keys) { Assert.True(map.TryAdd(memory, key, key.ToString())); }
        Assert.False(map.TryAdd(memory, 32, "duplicate"));
        map.Assign(memory, 64, "updated");
        Assert.Equal("updated", map[64]);
        Assert.True(map.Remove(32));
        Assert.True(map.Remove(0));
        Assert.True(map.Remove(128));
        Assert.False(map.Remove(128));
        Assert.True(map.TryAdd(memory, 160, "new"));
        Assert.True(map.TryAdd(memory, 192, "newer"));
        for (int key = 256; key < 2048; key += 32) { map.TryAdd(memory, key, key.ToString()); }
        Assert.Equal("updated", map[64]);
        Assert.False(map.ContainsKey(32));
        Assert.Equal(int.MinValue.ToString(), map[int.MinValue]);
        Assert.Equal(int.MaxValue.ToString(), map[int.MaxValue]);
        Assert.Equal("-1", map[-1]);
        Assert.Equal(map.Count, map.Select(item => item.Key).Distinct().Count());
        Assert.Equal("newer", map[192]);
    }

    [Fact]
    public void ActualCollisionVisitsEnterWorkCounters()
    {
        ResourceGovernor governor = Governor(QpdfLimits.Default);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        GovernedIntDictionary<int> map = GovernedIntDictionary<int>.Create(memory, governor, sizeof(int), CancellationToken.None);
        foreach (int key in new[] { 0, 32, 64, 96, 128 }) { map.TryAdd(memory, key, key); }
        long before = governor.Snapshot().TotalWork;
        Assert.True(map.TryGetValue(0, out int value));
        Assert.Equal(0, value);
        Assert.Equal(6L, governor.Snapshot().TotalWork - before);
        before = governor.Snapshot().TotalWork;
        Assert.True(map.ContainsKey(128));
        Assert.Equal(2L, governor.Snapshot().TotalWork - before);
        before = governor.Snapshot().TotalWork;
        Assert.False(map.ContainsKey(160));
        Assert.Equal(6L, governor.Snapshot().TotalWork - before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollidingSetLookupOrRemovalStopsAtTheWorkCeiling(bool remove)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 1000L };
        ResourceGovernor governor = Governor(limits);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        GovernedIntSet set = GovernedIntSet.Create(memory, governor, CancellationToken.None);
        foreach (int key in new[] { 0, 32, 64, 96, 128 }) { set.Add(memory, key); }
        governor.ChargeWork(limits.MaxWork - governor.Snapshot().TotalWork - 3L);
        long admitted = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => { if (remove) { set.Remove(0); } else { set.Contains(0); } });
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        Assert.Equal(limits.MaxWork, governor.Snapshot().TotalWork);
        Assert.Equal(5, set.Count);
        Assert.Equal(admitted, governor.Snapshot().LiveMemoryBytes);
        memory.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void FailedGrowthPreservesOldBackingAndCallerAdmissionThenRetries()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L };
        ResourceGovernor governor = Governor(limits);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(17L);
        GovernedIntDictionary<int> map = GovernedIntDictionary<int>.Create(memory, governor, sizeof(int), CancellationToken.None);
        for (int key = 0; key < 4; key++) { map.TryAdd(memory, key, key); }
        object backing = Backing(map);
        long previous = memory.ReservedBytes;
        long filler = limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - 223L;
        governor.ReserveMemory(filler);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => map.TryAdd(memory, 4, 4));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Same(backing, Backing(map));
        Assert.Equal(previous, memory.ReservedBytes);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(4, map.Count);
        governor.ReleaseMemory(filler);
        Assert.True(map.TryAdd(memory, 4, 4));
        for (int key = 0; key < 5; key++) { Assert.Equal(key, map[key]); }
        memory.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private static object Backing<T>(GovernedIntDictionary<T> map)
        => typeof(GovernedIntDictionary<T>).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(map)
            ?? throw new InvalidOperationException("Missing backing array.");

    [Theory]
    [InlineData(21L)]
    [InlineData(22L)]
    public void WorkFailureDuringRebuildReturnsGrowthOverlapAndLeavesOldEntries(long remaining)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 1000L };
        ResourceGovernor governor = Governor(limits);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        GovernedIntDictionary<int> map = GovernedIntDictionary<int>.Create(memory, governor, sizeof(int), CancellationToken.None);
        for (int key = 0; key < 4; key++) { map.TryAdd(memory, key, key); }
        object backing = Backing(map);
        long admitted = governor.Snapshot().LiveMemoryBytes;
        governor.ChargeWork(limits.MaxWork - governor.Snapshot().TotalWork - remaining);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => map.TryAdd(memory, 4, 4));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        Assert.Equal(limits.MaxWork, governor.Snapshot().TotalWork);
        Assert.True(governor.Snapshot().PeakMemoryBytes > admitted);
        Assert.Equal(admitted, governor.Snapshot().LiveMemoryBytes);
        Assert.Same(backing, Backing(map));
        Assert.Equal(4, map.Count);
    }

    [Fact]
    public void LastRebuildVisitCanConsumeTheRemainingWorkAndCommitWithoutAnotherCheck()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 1000L };
        ResourceGovernor governor = Governor(limits);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        GovernedIntDictionary<int> map = GovernedIntDictionary<int>.Create(memory, governor, sizeof(int), CancellationToken.None);
        for (int key = 0; key < 4; key++) { map.TryAdd(memory, key, key); }
        object before = Backing(map);
        governor.ChargeWork(limits.MaxWork - governor.Snapshot().TotalWork - 23L);
        Assert.True(map.TryAdd(memory, 4, 4));
        Assert.Equal(5, map.Count);
        Assert.NotSame(before, Backing(map));
        Assert.Equal(limits.MaxWork, governor.Snapshot().TotalWork);
        memory.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void EnumerationChargesDeletedSlotsAndRejectsMutation()
    {
        ResourceGovernor governor = Governor(QpdfLimits.Default);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        GovernedIntSet set = GovernedIntSet.Create(memory, governor, CancellationToken.None);
        for (int key = 0; key < 4; key++) { set.Add(memory, key); }
        for (int key = 0; key < 4; key++) { set.Remove(key); }
        long before = governor.Snapshot().TotalWork;
        int count = 0;
        foreach (int key in set) { count++; }
        Assert.Equal(0, count);
        Assert.Equal(4L, governor.Snapshot().TotalWork - before);
        GovernedIntSet.Enumerator enumerator = set.GetEnumerator();
        set.Add(memory, 7);
        Assert.Throws<InvalidOperationException>(() => enumerator.MoveNext());
    }

    [Fact]
    public void WarmedLookupsAndConcreteEnumerationAllocateNoManagedStorage()
    {
        ResourceGovernor governor = Governor(QpdfLimits.Default);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        GovernedIntSet set = GovernedIntSet.Create(memory, governor, CancellationToken.None);
        for (int key = 0; key < 32; key++) { set.Add(memory, key * 32); }
        for (int warm = 0; warm < 256; warm++) { foreach (int key in set) { set.Contains(key); } }
        long before = GC.GetAllocatedBytesForCurrentThread();
        int found = 0;
        for (int iteration = 0; iteration < 128; iteration++) { foreach (int key in set) { if (set.Contains(key)) { found++; } } }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(128 * 32, found);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void ForeignOwnerRejectsBeforeStorageOrWorkChanges()
    {
        ResourceGovernor governor = Governor(QpdfLimits.Default);
        ResourceGovernor foreign = Governor(QpdfLimits.Default);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using ParserMemoryScope other = ParserMemoryScope.Create(foreign);
        GovernedIntSet set = GovernedIntSet.Create(memory, governor, CancellationToken.None);
        QpdfExecutionCounters before = governor.Snapshot();
        QpdfExecutionCounters otherBefore = foreign.Snapshot();
        Assert.Throws<InvalidOperationException>(() => set.Add(other, 7));
        Assert.Equal(before, governor.Snapshot());
        Assert.Equal(otherBefore, foreign.Snapshot());
    }

    [Fact]
    public void DifferentScopeOfTheSameGovernorCannotReleaseOldBacking()
    {
        ResourceGovernor governor = Governor(QpdfLimits.Default);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using ParserMemoryScope other = ParserMemoryScope.Create(governor);
        GovernedIntDictionary<int> map = GovernedIntDictionary<int>.Create(memory, governor, sizeof(int), CancellationToken.None);
        for (int key = 0; key < 4; key++) { map.TryAdd(memory, key, key); }
        QpdfExecutionCounters before = governor.Snapshot();
        Assert.Throws<InvalidOperationException>(() => map.TryAdd(other, 4, 4));
        Assert.Equal(before, governor.Snapshot());
        Assert.Equal(4, map.Count);
        for (int key = 0; key < 4; key++) { Assert.Equal(key, map[key]); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FactoryAdmissionFailurePreservesPriorScopeStorage(bool set)
    {
        long wrapper = GovernedIntDictionary<int>.WrapperBytes + (set ? 32L : 0L);
        ResourceGovernor governor = Governor(QpdfLimits.Default with { MaxMemoryBytes = 64L + 17L + wrapper - 1L });
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(17L);
        long before = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
        {
            if (set) { GovernedIntSet.Create(memory, governor, CancellationToken.None); }
            else { GovernedIntDictionary<int>.Create(memory, governor, sizeof(int), CancellationToken.None); }
        });
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(17L, memory.ReservedBytes);
        Assert.Equal(0L, governor.Snapshot().TotalWork);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterObservedCollisionWorkStopsEitherTokenRoute(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxWork = long.MaxValue },
            governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        GovernedIntSet set = GovernedIntSet.Create(memory, governor, governorToken ? CancellationToken.None : cancellation.Token);
        for (int key = 0; key < 512; key++) { set.Add(memory, key * 1024); }
        long threshold = governor.Snapshot().TotalWork + 1024L;
        Task worker = Task.Run(() => { while (true) { set.Contains(512 * 1024); } });
        try
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            while (governor.Snapshot().TotalWork < threshold && !worker.IsCompleted
                && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10)) { Thread.Yield(); }
            Assert.True(governor.Snapshot().TotalWork >= threshold);
            long requested = System.Diagnostics.Stopwatch.GetTimestamp();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(requested) < TimeSpan.FromSeconds(10));
            Assert.Equal(512, set.Count);
        }
        finally
        {
            cancellation.Cancel();
            try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
        memory.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
