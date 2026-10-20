using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(WriterOrderCollection))]
public sealed class WriterIndexTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public void CapacityHintPreservesSparseKeysDuplicatesAndGrowth(int hint)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterIndex<long> owner = WriterIndex<long>.Create(hint, sizeof(long), governor, CancellationToken.None);
        Dictionary<int, long> expected = new();
        for (int index = 0; index < 140; index++)
        {
            int key = index % 2 == 0 ? int.MinValue + index * 129 : int.MaxValue - index * 129;
            long value = long.MaxValue - index;
            owner.Add(key, value); expected.Add(key, value);
            Assert.False(owner.TryAdd(key, -1L));
            Assert.Equal(value, owner[key]);
        }
        List<KeyValuePair<int, long>> actual = [];
        foreach (KeyValuePair<int, long> entry in owner) { actual.Add(entry); }
        Assert.Equal(expected.ToArray(), actual);
        owner.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void UnaffordableHintKeepsSmallJobsUsable()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using WriterIndex<long> owner = WriterIndex<long>.Create(100000, sizeof(long), governor, CancellationToken.None);
        owner.Add(int.MinValue, 17L); owner.Add(int.MaxValue, 19L);
        Assert.Equal(17L, owner[int.MinValue]); Assert.Equal(19L, owner[int.MaxValue]);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 0L, limits.MaxMemoryBytes);
        owner.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(512)]
    [InlineData(1025)]
    public void GrowingIndexPreservesSignedKeysValuesAndInsertionOrder(int count)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterIndex<long> owner = WriterIndex<long>.Create(sizeof(long), governor, CancellationToken.None);
        Dictionary<int, long> expected = new();
        Assert.Equal(272L, governor.Snapshot().LiveMemoryBytes);
        for (int index = 0; index < count; index++)
        {
            int key = unchecked(index * 1048576 + int.MinValue);
            expected.Add(key, long.MaxValue - index); owner.Add(key, long.MaxValue - index);
        }
        foreach (KeyValuePair<int, long> entry in expected)
        {
            Assert.True(owner.ContainsKey(entry.Key)); Assert.Equal(entry.Value, owner[entry.Key]);
            Assert.False(owner.TryAdd(entry.Key, -1L));
        }
        List<KeyValuePair<int, long>> actual = new();
        foreach (KeyValuePair<int, long> entry in owner) { actual.Add(entry); }
        Assert.Equal(expected.ToArray(), actual); Assert.Equal(count, owner.Count);
        owner.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void AssignmentPreservesBorrowedValuesAndDuplicateAddDoesNotReplace()
    {
        PdfName first = new([65,255]), replacement = new([66,254]);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterIndex<PdfName> owner = WriterIndex<PdfName>.Create(IntPtr.Size, governor, CancellationToken.None);
        owner.Add(-1, first);
        Assert.Throws<ArgumentException>(() => owner.Add(-1, replacement)); Assert.Same(first, owner[-1]);
        owner[-1] = replacement; Assert.Same(replacement, owner[-1]); Assert.Equal(1, owner.Count);
        owner.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(new byte[] {65,255}, first.RawBytes); Assert.Equal(new byte[] {66,254}, replacement.RawBytes);
    }

    [Fact]
    public void CreationAdmissionRejectsOneByteShortAndPreservesRetry()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        filler.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - 271L);
        long live = governor.Snapshot().LiveMemoryBytes, allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => WriterIndex<int>.Create(sizeof(int), governor, CancellationToken.None));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 32768L);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
        filler.Dispose();
        using (WriterIndex<int> retry = WriterIndex<int>.Create(sizeof(int), governor, CancellationToken.None))
        { retry.Add(int.MinValue, 7); Assert.Equal(7, retry[int.MinValue]); }
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrowthAdmissionFundsOverlapAndPreservesPriorBacking(bool references)
    {
        if (references) { GrowthAdmission<PdfName>(new([65,255]), IntPtr.Size); }
        else { GrowthAdmission<long>(long.MaxValue, sizeof(long)); }
    }

    private static void GrowthAdmission<T>(T value, long width)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using WriterIndex<T> owner = WriterIndex<T>.Create(width, governor, CancellationToken.None);
        for (int index = 0; index < 4; index++) { owner.Add(index, value); }
        object indexOwner = Index(owner); object entries = Field(indexOwner, "_entries"), buckets = Field(indexOwner, "_buckets");
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        filler.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - 223L);
        long live = governor.Snapshot().LiveMemoryBytes, allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => owner.Add(4, value));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 32768L);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(4, owner.Count);
        Assert.Same(entries, Field(indexOwner, "_entries")); Assert.Same(buckets, Field(indexOwner, "_buckets"));
        for (int index = 0; index < 4; index++) { Assert.Equal(value, owner[index]); }
        filler.Dispose(); owner.Add(4, value);
        Assert.Equal(657L, governor.Snapshot().LiveMemoryBytes); Assert.NotSame(entries, Field(indexOwner, "_entries"));
        owner.Dispose(); Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(value); GC.KeepAlive(entries); GC.KeepAlive(buckets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherCanceledTokenRejectsCreationBeforeWork(bool governorToken)
    {
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        Assert.ThrowsAny<OperationCanceledException>(() => WriterIndex<int>.Create(sizeof(int), governor,
            governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterObservedVisitsPreservesBorrowedValues(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using WriterIndex<PdfName> owner = WriterIndex<PdfName>.Create(IntPtr.Size, governor,
            governorToken ? CancellationToken.None : cancellation.Token);
        PdfName value = new([65,255]); for (int key = 0; key < 512; key++) { owner.Add(key, value); }
        int lookups = 0;
        using ManualResetEventSlim reached = new(), resume = new();
        Task worker = Task.Factory.StartNew(() =>
        {
            for (int key = 0; ; key = (key + 1) % 512)
            {
                _ = owner[key];
                if (++lookups >= 1024)
                { reached.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Index observation did not resume."); } }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10)));             cancellation.Cancel(); resume.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(512, owner.Count); owner.Dispose(); Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(new byte[] {65,255}, value.RawBytes); GC.KeepAlive(worker); GC.KeepAlive(value);
        }
        finally
        {
            cancellation.Cancel(); resume.Set();
            try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public void EnumeratorChecksMutationAndDisposedOwnerWithoutKeepingCurrentValues()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterIndex<int> owner = WriterIndex<int>.Create(sizeof(int), governor, CancellationToken.None);
        owner.Add(7, 9); WriterIndex<int>.Enumerator entries = owner.GetEnumerator();
        Assert.Throws<InvalidOperationException>(() => entries.Current); Assert.True(entries.MoveNext()); Assert.Equal(9, entries.Current.Value);
        owner[7] = 10; Assert.Throws<InvalidOperationException>(() => entries.MoveNext()); Assert.Throws<InvalidOperationException>(() => entries.Current);
        entries = owner.GetEnumerator(); Assert.True(entries.MoveNext()); owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => entries.MoveNext()); Assert.Throws<ObjectDisposedException>(() => entries.Current);
        Assert.Throws<ObjectDisposedException>(() => owner.Count); Assert.Throws<ObjectDisposedException>(() => owner.ContainsKey(7));
        Assert.Throws<ObjectDisposedException>(() => owner[7]); Assert.Throws<ObjectDisposedException>(() => owner.Add(9, 7));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void WarmedConcreteEnumerationAllocatesNoPerVisitStorage()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterIndex<long> owner = WriterIndex<long>.Create(sizeof(long), governor, CancellationToken.None);
        for (int index = 0; index < 512; index++) { owner.Add(index, index); }
        long sum = 0L;
        for (int pass = 0; pass < 4; pass++) { foreach (KeyValuePair<int,long> entry in owner) { sum += entry.Value; } }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int pass = 0; pass < 16; pass++) { foreach (KeyValuePair<int,long> entry in owner) { sum += entry.Value; } }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated); Assert.Equal(20L * 511L * 512L / 2L, sum);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WriterIndex<PdfName> Owner, WriterIndex<PdfName>.Enumerator Enumerator, WeakReference Buckets,
        WeakReference Entries, WeakReference Governor) Disposed(PdfName value)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        WriterIndex<PdfName> owner = WriterIndex<PdfName>.Create(IntPtr.Size, governor, CancellationToken.None);
        for (int index = 0; index < 512; index++) { owner.Add(index, value); }
        object map = Index(owner);
        WeakReference buckets = new(Field(map,"_buckets")), entries = new(Field(map,"_entries")), budget = new(governor);
        WriterIndex<PdfName>.Enumerator iterator = owner.GetEnumerator(); Assert.True(iterator.MoveNext()); _ = iterator.Current;
        owner.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        return (owner,iterator,buckets,entries,budget);
    }

    [Fact]
    public void RootedDisposedOwnerAndEnumeratorReleaseBackingAndCompletedGovernor()
    {
        PdfName value = new([65,255]); var state = Disposed(value);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(state.Buckets.IsAlive); Assert.False(state.Entries.IsAlive); Assert.False(state.Governor.IsAlive);
        Assert.Equal(new byte[] {65,255}, value.RawBytes); GC.KeepAlive(state); GC.KeepAlive(value);
    }

    private static object Index<T>(WriterIndex<T> owner) => Field(owner,"_index");
    private static object Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
        ?? throw new InvalidOperationException("Expected funded writer index field.");
}
