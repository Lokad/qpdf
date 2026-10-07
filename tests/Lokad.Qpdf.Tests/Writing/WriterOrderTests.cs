using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[CollectionDefinition(nameof(WriterOrderCollection), DisableParallelization = true)]
public sealed class WriterOrderCollection { }

[Collection(nameof(WriterOrderCollection))]
public sealed class WriterOrderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(256)]
    [InlineData(1025)]
    public void HeapOrderingPreservesDuplicatesAndIntegerExtremes(int count)
    {
        int[] input = Enumerable.Range(0, count).Select(index => index % 3 == 0 ? int.MaxValue : index % 3 == 1 ? int.MinValue : -index).ToArray();
        int[] original = (int[])input.Clone();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterOrder<int> order = WriterOrder<int>.Create(count, sizeof(int), governor, CancellationToken.None);
        foreach (int number in input) { order.Add(number); }
        order.Sort(new WriterNumberOrder());
        int[] actual = new int[count];
        for (int index = 0; index < count; index++) { actual[index] = order[index]; }
        Assert.Equal(original.OrderBy(value => value), actual); Assert.Equal(original, input);
        Assert.Equal(128L + (count == 0 ? 0L : 32L + 4L * count), governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void NameOrderingMatchesOrdinalDisplayAcrossAllBytesAndEscapedPrefixes()
    {
        PdfName[] input = Enumerable.Range(0, 256).Select(value => new PdfName([(byte)value])).Concat([
            new PdfName([]), new PdfName([35, 50, 51]), new PdfName([65, 0]), new PdfName([65]), new PdfName([65, 32]), new PdfName([35, 0])]).ToArray();
        PdfName[] expected = input.OrderBy(name => name.DisplayText, StringComparer.Ordinal).ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterOrder<PdfName> order = WriterOrder<PdfName>.Create(input.Length, IntPtr.Size, governor, CancellationToken.None);
        foreach (PdfName name in input) { order.Add(name); }
        order.Sort(new WriterNameOrder(governor, CancellationToken.None));
        for (int index = 0; index < expected.Length; index++) { Assert.Same(expected[index], order[index]); }
        Assert.Equal(input.Length, order.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdmissionFailureAllocatesNoBackingAndPreservesRetry(bool references)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        long bytes = 65536L * (references ? IntPtr.Size : sizeof(int));
        filler.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - (160L + bytes - 1L));
        long live = governor.Snapshot().LiveMemoryBytes, before = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = references
            ? Assert.Throws<QpdfLimitException>(() => WriterOrder<PdfName>.Create(65536, IntPtr.Size, governor, CancellationToken.None))
            : Assert.Throws<QpdfLimitException>(() => WriterOrder<int>.Create(65536, sizeof(int), governor, CancellationToken.None));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 32768L);
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        filler.Dispose();
        using (WriterOrder<int> retry = WriterOrder<int>.Create(65536, sizeof(int), governor, CancellationToken.None)) { Assert.Equal(0, retry.Count); }
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ZeroingWorkRejectsBeforeAllocation()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 65536L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        long before = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => WriterOrder<int>.Create(65536, sizeof(int), governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 32768L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void EscapedComparisonAndSortingAllocateNoDisplayOrComparerObjects()
    {
        byte[] common = Enumerable.Repeat((byte)35, 4096).ToArray();
        PdfName first = new(common), second = new(common.Concat(new byte[] { 65 }).ToArray());
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterOrder<PdfName> order = WriterOrder<PdfName>.Create(2, IntPtr.Size, governor, CancellationToken.None);
        order.Add(second); order.Add(first); order.Sort(new WriterNameOrder(governor, CancellationToken.None));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 16; iteration++) { order.Sort(new WriterNameOrder(governor, CancellationToken.None)); }
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Same(first, order[0]); Assert.Same(second, order[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedSortPreservesBorrowedInputAndPriorStorage(bool names)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 10000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        if (names)
        {
            PdfName left = new(new byte[8192]), right = new(new byte[8192]);
            using (WriterOrder<PdfName> order = WriterOrder<PdfName>.Create(2, IntPtr.Size, governor, CancellationToken.None))
            {
                order.Add(left); order.Add(right);
                QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => order.Sort(new WriterNameOrder(governor, CancellationToken.None)));
                Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
                Assert.Equal(new byte[8192], left.RawBytes); Assert.Equal(new byte[8192], right.RawBytes);
            }
        }
        else
        {
            using WriterOrder<int> order = WriterOrder<int>.Create(1024, sizeof(int), governor, CancellationToken.None);
            for (int value = 1024; value > 0; value--) { order.Add(value); }
            Assert.Throws<QpdfLimitException>(() => order.Sort(new WriterNumberOrder()));
            order.Dispose();
        }
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes); Assert.True(governor.Snapshot().TotalWork > 8192L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothTokensStopComparisonWithoutWrapping(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using WriterOrder<int> order = WriterOrder<int>.Create(4, sizeof(int), governor, governorToken ? CancellationToken.None : cancellation.Token);
        order.Add(4); order.Add(3); order.Add(2); order.Add(1); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => order.Sort(new WriterNumberOrder()));
        order.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationAfterObservedComparisonsRetiresOnlyOrdering(bool names, bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim start = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        CancellationToken token = governorToken ? CancellationToken.None : cancellation.Token;
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        WriterOrder<int>? numbers = null;
        WriterOrder<PdfName>? keys = null;
        if (names)
        {
            keys = WriterOrder<PdfName>.Create(512, IntPtr.Size, governor, token);
            byte[] raw = new byte[4098]; Array.Fill(raw, (byte)35);
            for (int index = 511; index >= 0; index--)
            {
                raw[^2] = (byte)(index >> 8); raw[^1] = (byte)index; keys.Add(new PdfName(raw));
            }
        }
        else
        {
            numbers = WriterOrder<int>.Create(65536, sizeof(int), governor, token);
            for (int value = 65536; value > 0; value--) { numbers.Add(value); }
        }
        long threshold = governor.Snapshot().TotalWork + 65536L;
        using ManualResetEventSlim reached = new();
        using ManualResetEventSlim resume = new();
        ComparisonGate gate = new(governor, threshold, reached, resume);
        Task worker = Task.Factory.StartNew(() =>
        {
            start.Wait();
            try
            {
                if (keys is not null) { keys.Sort(new ObservedNameOrder(gate, governor, token)); }
                else { (numbers ?? throw new InvalidOperationException("Expected numbers.")).Sort(new ObservedNumberOrder(gate)); }
            }
            finally { keys?.Dispose(); numbers?.Dispose(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            start.Set();
            bool observed = reached.Wait(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.True(observed); Assert.True(governor.Snapshot().TotalWork >= threshold);
            resume.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(keys); GC.KeepAlive(numbers);
        }
        finally
        {
            start.Set(); cancellation.Cancel(); resume.Set();
            try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class ComparisonGate(ResourceGovernor governor, long threshold, ManualResetEventSlim reached, ManualResetEventSlim resume)
    {
        private bool _paused;
        internal void BeforeComparison()
        {
            if (_paused || governor.Snapshot().TotalWork < threshold) { return; }
            _paused = true; reached.Set(); resume.Wait();
        }
    }

    private readonly struct ObservedNumberOrder(ComparisonGate gate) : IComparer<int>
    {
        public int Compare(int left, int right) { gate.BeforeComparison(); return new WriterNumberOrder().Compare(left, right); }
    }

    private readonly struct ObservedNameOrder(ComparisonGate gate, ResourceGovernor governor, CancellationToken token) : IComparer<PdfName>
    {
        public int Compare(PdfName? left, PdfName? right)
        { gate.BeforeComparison(); return new WriterNameOrder(governor, token).Compare(left, right); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Backing(WriterOrder<int> order) => new(typeof(WriterOrder<int>)
        .GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(order) ?? throw new InvalidOperationException("Expected ordering backing."));

    [Fact]
    public void DisposedRootedOwnerAndEnumeratorDropExclusiveArray()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        WriterOrder<int> order = WriterOrder<int>.Create(65536, sizeof(int), governor, CancellationToken.None); order.Add(1);
        WriterOrder<int>.Enumerator enumerator = order.GetEnumerator(); Assert.True(enumerator.MoveNext());
        WeakReference array = Backing(order);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Assert.True(array.IsAlive);
        order.Dispose(); order.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Assert.False(array.IsAlive);
        Assert.Throws<ObjectDisposedException>(() => order.Count);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(order); GC.KeepAlive(enumerator);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WriterOrder<int> Owner, WriterOrder<int>.Enumerator Enumerator, WeakReference Governor) DetachedOwner()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        WriterOrder<int> order = WriterOrder<int>.Create(32, sizeof(int), governor, CancellationToken.None); order.Add(1);
        WriterOrder<int>.Enumerator enumerator = order.GetEnumerator(); Assert.True(enumerator.MoveNext());
        order.Dispose(); return (order, enumerator, new WeakReference(governor));
    }

    [Fact]
    public void RootedDisposedOwnerAndEnumeratorReleaseCompletedGovernor()
    {
        (WriterOrder<int> owner, WriterOrder<int>.Enumerator enumerator, WeakReference governor) = DetachedOwner();
        for (int round = 0; round < 3; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(governor.IsAlive); Assert.Throws<ObjectDisposedException>(() => owner.Count);
        GC.KeepAlive(owner); GC.KeepAlive(enumerator);
    }
}
