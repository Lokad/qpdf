using System.Reflection;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(NameTableCollection))]
public sealed class PdfNameTableTests
{
    [Fact]
    public void ValueConversionPreservesOrderAndSourceOnCancellation()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<ParsedValue> source = PdfNameTable<ParsedValue>.Create(memory, governor, 16L, CancellationToken.None);
        PdfName[] keys = Enumerable.Range(0, 5).Select(index => Key(index, 512)).ToArray();
        QpdfObject[] values = [new PdfInteger("1", 1L), PdfNull.Instance, new PdfInteger("3", 3L), PdfNull.Instance, new PdfInteger("5", 5L)];
        for (int index = 0; index < keys.Length; index++)
        { source.Assign(memory, governor, keys[index], new ParsedValue(values[index], 0L), CancellationToken.None); }
        source.Assign(memory, governor, Key(0, 512), new ParsedValue(values[4], 0L), CancellationToken.None);
        values[0] = values[4];
        using CancellationTokenSource cancellation = new();
        long before = memory.ReservedBytes; int converted = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => source.TryMoveValues(memory, governor, IntPtr.Size, value =>
        {
            if (++converted == 2) { cancellation.Cancel(); }
            return value.Value;
        }, cancellation.Token, out _));
        Assert.Equal(before, memory.ReservedBytes);
        Assert.Equal(keys.Length, source.Count);
        for (int index = 0; index < keys.Length; index++) { Assert.Same(values[index], source[Key(index, 512)].Value); }
        Assert.True(source.TryMoveValues(memory, governor, IntPtr.Size, static value => value.Value,
            CancellationToken.None, out PdfNameTable<QpdfObject>? result));
        PdfNameTable<QpdfObject> kept = Assert.IsType<PdfNameTable<QpdfObject>>(result);
        KeyValuePair<PdfName, QpdfObject>[] rows = Enumerable.ToArray<KeyValuePair<PdfName, QpdfObject>>(kept);
        for (int index = 0; index < keys.Length; index++)
        { Assert.Same(keys[index], rows[index].Key); Assert.Same(values[index], rows[index].Value); Assert.Same(values[index], kept[Key(index, 512)]); }
        Assert.Throws<InvalidOperationException>(() => kept.Assign(memory, governor, keys[0], PdfNull.Instance, CancellationToken.None));
    }

    [Fact]
    public void ValueConversionRejectsOverlapWithoutDiscardingSource()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 700L }, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<ParsedValue> source = PdfNameTable<ParsedValue>.Create(memory, governor, 16L, CancellationToken.None);
        for (int index = 0; index < 5; index++)
        { source.Assign(memory, governor, PdfName.FromAscii("K" + index), new ParsedValue(PdfNull.Instance, 0L), CancellationToken.None); }
        long before = memory.ReservedBytes;
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => source.TryMoveValues(memory, governor,
            IntPtr.Size, static value => value.Value, CancellationToken.None, out _));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), error.LimitName);
        Assert.Equal(before, memory.ReservedBytes);
        Assert.Equal(5, source.Count);
        for (int index = 0; index < 5; index++) { Assert.Same(PdfNull.Instance, source[PdfName.FromAscii("K" + index)].Value); }
    }

    internal static PdfName Key(int number, int length)
    {
        byte[] bytes = Enumerable.Repeat((byte)97, length).ToArray();
        for (int index = 0; index < 4; index++) { bytes[length - 1 - index] = (byte)((uint)number >> (index * 8)); }
        return new PdfName(bytes);
    }

    private static object Field(object table, string name) => table.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(table)
        ?? throw new InvalidOperationException("Expected name storage field.");

    [Theory]
    [InlineData(16)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(511)]
    public void HashIncludesBytesBeyondTheOldPrefixAndAcrossChunkBoundaries(int position)
    {
        HashSet<int> hashes = [];
        for (int value = 0; value < 256; value++)
        {
            byte[] raw = new byte[512]; raw[position] = (byte)value;
            hashes.Add(new PdfName(raw).GetHashCode());
        }
        Assert.True(hashes.Count > 128, $"Only {hashes.Count} hashes include byte {position}.");
    }

    [Fact]
    public void CommonPrefixesBorrowBinaryKeysAndValuesAndPreserveLastWinsOrder()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<object> table = PdfNameTable<object>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
        PdfName[] keys = Enumerable.Range(0, 32).Select(number => Key(number, 512)).ToArray(); object[] values = keys.Select(_ => new object()).ToArray();
        Assert.True(keys.Select(key => key.GetHashCode()).Distinct().Count() > keys.Length / 2);
        for (int index = 0; index < keys.Length; index++)
        {
            Assert.True(table.TryAdd(memory, governor, keys[index], values[index], CancellationToken.None, out object? previous));
            Assert.Null(previous);
        }
        for (int index = 0; index < keys.Length; index++) { Assert.True(table.TryGetValue(Key(index, 512), governor, CancellationToken.None, out object? found)); Assert.Same(values[index], found); }
        Assert.False(table.TryAdd(memory, governor, Key(7, 512), new object(), CancellationToken.None, out object? duplicate));
        Assert.Same(values[7], duplicate);
        Assert.Same(values[7], table[keys[7]]);
        object replacement = new(); table.Assign(memory, governor, Key(7, 512), replacement, CancellationToken.None);
        Assert.Equal(32, table.Count); Assert.Same(replacement, table[keys[7]]);
        KeyValuePair<PdfName, object>[] rows = Enumerable.ToArray<KeyValuePair<PdfName, object>>(table); Assert.Equal(keys.Length, rows.Length);
        for (int index = 0; index < keys.Length; index++) { Assert.Same(keys[index], rows[index].Key); }
Assert.Equal(table.StorageBytes, memory.ReservedBytes);
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
        Assert.Equal(3, table.Capacity);         Assert.False(table.TryGetValue(keys[3], governor, CancellationToken.None, out _));
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
    public void RawByteLookupBorrowsQueryWithoutAllocatingNameOrDisplayText()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<object> table = PdfNameTable<object>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
        PdfName key = Key(1, 8192); object borrowed = new(); table.Assign(memory, governor, key, borrowed, CancellationToken.None);
        for (int warmup = 0; warmup < 32; warmup++) { Assert.True(table.TryGetValue(key.RawSpan, governor, CancellationToken.None, out _)); }
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        bool found = table.TryGetValue(key.RawSpan, governor, CancellationToken.None, out object? value);
        long delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(found); Assert.Same(borrowed, value); Assert.Equal(0L, delta);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignGovernorOrConstructionScopeRejectsBeforeWork(bool sameGovernor)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None), other = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); using ParserMemoryScope foreign = ParserMemoryScope.Create(sameGovernor ? governor : other);
        PdfNameTable<int> table = PdfNameTable<int>.Create(memory, governor, sizeof(int), CancellationToken.None);
        long before = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<InvalidOperationException>(() => table.Assign(foreign, sameGovernor ? governor : other, Key(1, 32), 1, CancellationToken.None));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Empty((IReadOnlyDictionary<PdfName, int>)table);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherPreCanceledRouteRejectsBeforeAllocation(bool governorToken)
    {
        using CancellationTokenSource cancellation = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); cancellation.Cancel();
        long before = governor.Snapshot().LiveMemoryBytes;
        Assert.ThrowsAny<OperationCanceledException>(() => PdfNameTable<int>.Create(memory, governor, sizeof(int), governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);     }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringLongNameHashLeavesTableUsable(bool governorToken)
    {
        using CancellationTokenSource cancellation = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<object> table = PdfNameTable<object>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
        object borrowed = new(); table.Assign(memory, governor, Key(1, 8192), borrowed, CancellationToken.None);
        long live = governor.Snapshot().LiveMemoryBytes;
        using ManualResetEventSlim reached = new(), resume = new(); PdfName query = Key(2, 8192);
        Task worker = Task.Factory.StartNew(() => table.TryGetObserved(query, governor, governorToken ? CancellationToken.None : cancellation.Token,
            () => { reached.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Name-byte observation did not resume."); } }, out _),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10)));             cancellation.Cancel(); resume.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
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
Assert.True(entries.MoveNext(governor, CancellationToken.None)); Assert.Same(key, entries.Current.Key); Assert.Same(value, entries.Current.Value);
        Assert.False(entries.MoveNext(governor, CancellationToken.None)); Assert.Throws<InvalidOperationException>(() => entries.Current);
        entries.Reset(); table.Freeze(memory, governor, CancellationToken.None); Assert.True(entries.MoveNext(governor, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => table.Assign(memory, governor, key, value, CancellationToken.None));
    }

}

[CollectionDefinition(nameof(NameTableCollection), DisableParallelization = true)]
public sealed class NameTableCollection { }
