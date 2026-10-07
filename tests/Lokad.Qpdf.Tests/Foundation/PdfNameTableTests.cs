using System.Reflection;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(NameTableCollection))]
public sealed class PdfNameTableTests
{
    internal static PdfName Key(int number, int length)
    {
        byte[] bytes = Enumerable.Repeat((byte)97, length).ToArray();
        for (int index = 0; index < 4; index++) { bytes[length - 1 - index] = (byte)((uint)number >> (index * 8)); }
        return new PdfName(bytes);
    }

    private static object Field(object table, string name) => table.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(table)
        ?? throw new InvalidOperationException("Expected name storage field.");

    [Fact]
    public void PrefixCollisionsBorrowBinaryKeysAndValuesAndPreserveLastWinsOrder()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<object> table = PdfNameTable<object>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
        PdfName[] keys = Enumerable.Range(0, 32).Select(number => Key(number, 512)).ToArray(); object[] values = keys.Select(_ => new object()).ToArray();
        Assert.Single(keys.Select(key => key.GetHashCode()).Distinct());
        for (int index = 0; index < keys.Length; index++) { Assert.True(table.TryAdd(memory, governor, keys[index], values[index], CancellationToken.None)); }
        for (int index = 0; index < keys.Length; index++) { Assert.True(table.TryGetValue(Key(index, 512), governor, CancellationToken.None, out object? found)); Assert.Same(values[index], found); }
        Assert.False(table.TryAdd(memory, governor, Key(7, 512), new object(), CancellationToken.None));
        object replacement = new(); table.Assign(memory, governor, Key(7, 512), replacement, CancellationToken.None);
        Assert.Equal(32, table.Count); Assert.Same(replacement, table[keys[7]]);
        KeyValuePair<PdfName, object>[] rows = Enumerable.ToArray<KeyValuePair<PdfName, object>>(table); Assert.Equal(keys.Length, rows.Length);
        for (int index = 0; index < keys.Length; index++) { Assert.Same(keys[index], rows[index].Key); }
        Assert.True(governor.Snapshot().TotalWork > 32L * 512L); Assert.Equal(table.StorageBytes, memory.ReservedBytes);
    }

    [Fact]
    public void BucketVisitsWithDifferentHashesAreChargedEvenWithoutEqualityCalls()
    {
        List<PdfName> keys = new(); HashSet<int> hashes = new();
        for (int number = 0; number < 10000 && keys.Count < 4; number++)
        {
            PdfName key = PdfName.FromAscii("bucket-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture)); int hash = key.GetHashCode();
            if ((uint)hash % 3U == 0 && hashes.Add(hash)) { keys.Add(key); }
        }
        Assert.Equal(4, keys.Count);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<int> table = PdfNameTable<int>.Create(memory, governor, sizeof(int), CancellationToken.None);
        for (int index = 0; index < 3; index++) { Assert.True(table.TryAdd(memory, governor, keys[index], index, CancellationToken.None)); }
        Assert.Equal(3, table.Capacity); long before = governor.Snapshot().TotalWork;
        Assert.False(table.TryGetValue(keys[3], governor, CancellationToken.None, out _));
        Assert.Equal(Math.Min(keys[3].RawSpan.Length, 16) + 7L, governor.Snapshot().TotalWork - before);
    }

    [Fact]
    public void ExactStorageIncludesWrapperAndBothArraysAtRealCapacity()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<object> table = PdfNameTable<object>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
        Assert.Equal(160L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(96L, table.StorageBytes);
        for (int index = 0; index < 4; index++) { table.Assign(memory, governor, Key(index, 32), new object(), CancellationToken.None); }
        Assert.Equal(7, table.Capacity); Assert.Equal(64L + 96L + 64L + 28L * 7L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(table.StorageBytes, memory.ReservedBytes);
        table.Discard(memory, governor); Assert.Equal(64L, governor.Snapshot().LiveMemoryBytes); Assert.Empty((IReadOnlyDictionary<PdfName, object>)table);
    }

    [Fact]
    public void KnownCapacityAdmitsExactBackingAndPreservesDataAcrossGrowth()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<object> table = PdfNameTable<object>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
        Assert.Equal(12, table.EnsureCapacity(memory, governor, 12, CancellationToken.None));
        Assert.Equal(96L + 64L + 28L * 12L, memory.ReservedBytes);
        PdfName key = Key(1, 32); object value = new(); table.Assign(memory, governor, key, value, CancellationToken.None);
        long before = governor.Snapshot().LiveMemoryBytes;
        Assert.Equal(20, table.EnsureCapacity(memory, governor, 20, CancellationToken.None));
        Assert.Equal(before + 64L + 28L * 20L, governor.Snapshot().PeakMemoryBytes);
        Assert.Equal(96L + 64L + 28L * 20L, memory.ReservedBytes); Assert.Same(value, table[key]);
        Assert.Equal(20, table.EnsureCapacity(memory, governor, 13, CancellationToken.None));
    }

    [Fact]
    public void SmallKnownCopiesKeepFundedThreeAndSevenSlotStorage()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<object> table = PdfNameTable<object>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
        Assert.Equal(3, table.EnsureCapacity(memory, governor, 2, CancellationToken.None));
        Assert.Equal(96L + 64L + 28L * 3L, memory.ReservedBytes);
        Assert.Equal(7, table.EnsureCapacity(memory, governor, 4, CancellationToken.None));
        Assert.Equal(96L + 64L + 28L * 7L, memory.ReservedBytes);
    }

    [Fact]
    public void OneByteShortGrowthPreservesOldBackingPriorAdmissionAndRetry()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<object> table = PdfNameTable<object>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
        object borrowed = new(); for (int index = 0; index < 3; index++) { table.Assign(memory, governor, Key(index, 32), borrowed, CancellationToken.None); }
        object entries = Field(table, "_entries"), buckets = Field(table, "_buckets");
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor); const long nextArrays = 64L + 28L * 7L;
        filler.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - nextArrays + 1L);
        long before = governor.Snapshot().LiveMemoryBytes, allocated = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => table.Assign(memory, governor, Key(3, 32), borrowed, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 32768L);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes); Assert.Equal(3, table.Count);
        Assert.Same(entries, Field(table, "_entries")); Assert.Same(buckets, Field(table, "_buckets"));
        filler.Dispose(); table.Assign(memory, governor, Key(3, 32), borrowed, CancellationToken.None);
        Assert.Equal(4, table.Count); Assert.Equal(7, table.Capacity); Assert.Same(borrowed, table[Key(2, 32)]);
    }

    [Fact]
    public void ExhaustedCollisionWorkLeavesStorageAndBorrowedValuesUnchanged()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 100000L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<object> table = PdfNameTable<object>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
        object borrowed = new(); table.Assign(memory, governor, Key(1, 4096), borrowed, CancellationToken.None);
        governor.ChargeWork(limits.MaxWork - governor.Snapshot().TotalWork - 128L);
        long live = governor.Snapshot().LiveMemoryBytes, work = governor.Snapshot().TotalWork;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => table.TryGetValue(Key(2, 4096), governor, CancellationToken.None, out _));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().TotalWork > work); Assert.Single((IReadOnlyDictionary<PdfName, object>)table); Assert.Same(borrowed, table[Key(1, 4096)]);
    }

    [Fact]
    public void GrowthWorkRejectionRollsBackNewArrayReservation()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 100000L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<int> table = PdfNameTable<int>.Create(memory, governor, sizeof(int), CancellationToken.None);
        for (int index = 0; index < 3; index++) { table.Assign(memory, governor, PdfName.FromAscii(index.ToString(System.Globalization.CultureInfo.InvariantCulture)), index, CancellationToken.None); }
        PdfName missing = PdfName.FromAscii("4"); long lookupStart = governor.Snapshot().TotalWork;
        Assert.False(table.TryGetValue(missing, governor, CancellationToken.None, out _));
        long lookupWork = governor.Snapshot().TotalWork - lookupStart;
        governor.ChargeWork(limits.MaxWork - governor.Snapshot().TotalWork - lookupWork - 14L);
        long before = governor.Snapshot().LiveMemoryBytes; object entries = Field(table, "_entries");
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => table.Assign(memory, governor, missing, 4, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(before + 64L + 28L * 7L, governor.Snapshot().PeakMemoryBytes);
        Assert.Equal(3, table.Count); Assert.Same(entries, Field(table, "_entries"));
    }

    [Fact]
    public void RawByteLookupBorrowsQueryWithoutAllocatingNameOrDisplayText()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<object> table = PdfNameTable<object>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
        PdfName key = Key(1, 8192); object borrowed = new(); table.Assign(memory, governor, key, borrowed, CancellationToken.None);
        for (int warmup = 0; warmup < 32; warmup++) { Assert.True(table.TryGetValue(key.RawSpan, governor, CancellationToken.None, out _)); }
        long work = governor.Snapshot().TotalWork, allocated = GC.GetAllocatedBytesForCurrentThread();
        bool found = table.TryGetValue(key.RawSpan, governor, CancellationToken.None, out object? value);
        long delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(found); Assert.Same(borrowed, value); Assert.Equal(0L, delta);
        Assert.True(governor.Snapshot().TotalWork >= work + 8192L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignGovernorOrConstructionScopeRejectsBeforeWork(bool sameGovernor)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None), other = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); using ParserMemoryScope foreign = ParserMemoryScope.Create(sameGovernor ? governor : other);
        PdfNameTable<int> table = PdfNameTable<int>.Create(memory, governor, sizeof(int), CancellationToken.None);
        long before = governor.Snapshot().LiveMemoryBytes, work = governor.Snapshot().TotalWork;
        Assert.Throws<InvalidOperationException>(() => table.Assign(foreign, sameGovernor ? governor : other, Key(1, 32), 1, CancellationToken.None));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(work, governor.Snapshot().TotalWork); Assert.Empty((IReadOnlyDictionary<PdfName, int>)table);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherPreCanceledRouteRejectsBeforeAllocation(bool governorToken)
    {
        using CancellationTokenSource cancellation = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); cancellation.Cancel();
        long before = governor.Snapshot().LiveMemoryBytes, work = governor.Snapshot().TotalWork;
        Assert.ThrowsAny<OperationCanceledException>(() => PdfNameTable<int>.Create(memory, governor, sizeof(int), governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(work, governor.Snapshot().TotalWork);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterActualCollisionByteWorkLeavesTableUsable(bool governorToken)
    {
        using CancellationTokenSource cancellation = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<object> table = PdfNameTable<object>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
        object borrowed = new(); table.Assign(memory, governor, Key(1, 8192), borrowed, CancellationToken.None);
        long live = governor.Snapshot().LiveMemoryBytes, work = governor.Snapshot().TotalWork;
        using ManualResetEventSlim reached = new(), resume = new(); PdfName query = Key(2, 8192);
        Task worker = Task.Factory.StartNew(() => table.TryGetObserved(query, governor, governorToken ? CancellationToken.None : cancellation.Token,
            () => { reached.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Name-byte observation did not resume."); } }, out _),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10))); Assert.True(governor.Snapshot().TotalWork >= work + 256L);
            cancellation.Cancel(); resume.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Single((IReadOnlyDictionary<PdfName, object>)table); Assert.Same(borrowed, table[Key(1, 8192)]); GC.KeepAlive(worker);
        }
        finally { cancellation.Cancel(); resume.Set(); try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { } }
    }

    [Fact]
    public void EnumerationChargesVisitsAndInvalidatesOnlyWhenDataChanges()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<object> table = PdfNameTable<object>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
        PdfName key = Key(1, 32); object value = new(); table.Assign(memory, governor, key, value, CancellationToken.None);
        PdfNameTable<object>.Enumerator entries = table.GetEnumerator(); Assert.Throws<InvalidOperationException>(() => entries.Current);
        long before = governor.Snapshot().TotalWork; Assert.True(entries.MoveNext(governor, CancellationToken.None)); Assert.Same(key, entries.Current.Key); Assert.Same(value, entries.Current.Value);
        Assert.False(entries.MoveNext(governor, CancellationToken.None)); Assert.Equal(2L, governor.Snapshot().TotalWork - before); Assert.Throws<InvalidOperationException>(() => entries.Current);
        entries.Reset(); table.Freeze(memory, governor, CancellationToken.None); Assert.True(entries.MoveNext(governor, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => table.Assign(memory, governor, key, value, CancellationToken.None));
    }

}

[CollectionDefinition(nameof(NameTableCollection), DisableParallelization = true)]
public sealed class NameTableCollection { }
