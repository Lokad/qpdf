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
        Assert.True(map.TryGetValue(0, out int value));
        Assert.Equal(0, value);
        Assert.True(map.ContainsKey(128));
        Assert.False(map.ContainsKey(160));
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

    [Fact]
    public void EnumerationChargesDeletedSlotsAndRejectsMutation()
    {
        ResourceGovernor governor = Governor(QpdfLimits.Default);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        GovernedIntSet set = GovernedIntSet.Create(memory, governor, CancellationToken.None);
        for (int key = 0; key < 4; key++) { set.Add(memory, key); }
        for (int key = 0; key < 4; key++) { set.Remove(key); }
        int count = 0;
        foreach (int key in set) { count++; }
        Assert.Equal(0, count);
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
    }
}
