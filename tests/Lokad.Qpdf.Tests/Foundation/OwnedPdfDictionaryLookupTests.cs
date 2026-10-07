using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(NameTableCollection))]
public sealed class OwnedPdfDictionaryLookupTests
{
    private static byte[] Bytes(byte last)
    { byte[] raw = new byte[8192]; Array.Fill(raw, (byte)97); raw[^1] = last; return raw; }

    private static PdfDictionary Complete(ParserMemoryScope memory, ResourceGovernor owner,
        PdfName key, QpdfObject value)
    {
        PdfNameTable<QpdfObject> names = PdfNameTable<QpdfObject>.Create(memory, owner, 8L, CancellationToken.None);
        Assert.True(names.TryAdd(memory, owner, key, value, CancellationToken.None)); names.Freeze(memory, owner, CancellationToken.None);
        return PdfDictionary.FromOwnedEntries(names);
    }

    [Fact]
    public void OwnedBackingPreservesBinaryEmptyMissingAndNullValues()
    {
        ResourceGovernor owner = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(owner);
        PdfNameTable<QpdfObject> names = PdfNameTable<QpdfObject>.Create(memory, owner, 8L, CancellationToken.None);
        PdfArray value = new([PdfNull.Instance]);
        Assert.True(names.TryAdd(memory, owner, new PdfName([0, 255, 35]), value, CancellationToken.None));
        Assert.True(names.TryAdd(memory, owner, new PdfName([]), PdfNull.Instance, CancellationToken.None));
        names.Freeze(memory, owner, CancellationToken.None); PdfDictionary dictionary = PdfDictionary.FromOwnedEntries(names);
        long before = owner.Snapshot().LiveMemoryBytes; ResourceGovernor lookup = new(QpdfLimits.Default, CancellationToken.None);
        Assert.True(PdfDictionaryLookup.TryGet(dictionary, [0, 255, 35], lookup, CancellationToken.None, out QpdfObject found)); Assert.Same(value, found);
        Assert.True(PdfDictionaryLookup.TryGet(dictionary, [], lookup, CancellationToken.None, out found)); Assert.Same(PdfNull.Instance, found);
        Assert.False(PdfDictionaryLookup.TryGet(dictionary, [0, 255, 36], lookup, CancellationToken.None, out found)); Assert.Same(PdfNull.Instance, found);
        Assert.Equal(before, owner.Snapshot().LiveMemoryBytes); Assert.Equal(0L, lookup.Snapshot().LiveMemoryBytes); Assert.Equal(2, dictionary.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedDispatchChargesTheTableLookupWithoutScanningItsKeyView(bool missing)
    {
        ResourceGovernor owner = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(owner);
        PdfNameTable<QpdfObject> names = PdfNameTable<QpdfObject>.Create(memory, owner, 8L, CancellationToken.None);
        for (int index = 0; index < 512; index++)
        { Assert.True(names.TryAdd(memory, owner, PdfName.FromAscii(index.ToString(System.Globalization.CultureInfo.InvariantCulture)), PdfNull.Instance, CancellationToken.None)); }
        names.Freeze(memory, owner, CancellationToken.None); PdfDictionary dictionary = PdfDictionary.FromOwnedEntries(names);
        byte[] query = missing ? new byte[64] : "123"u8.ToArray();
        ResourceGovernor direct = new(QpdfLimits.Default, CancellationToken.None), lookup = new(QpdfLimits.Default, CancellationToken.None);
        bool expected = names.TryGetValue(query.AsSpan(), direct, CancellationToken.None, out QpdfObject? expectedValue);
        Assert.Equal(expected, PdfDictionaryLookup.TryGet(dictionary, query, lookup, CancellationToken.None, out QpdfObject value));
        if (expected) { Assert.Same(expectedValue, value); } else { Assert.Same(PdfNull.Instance, value); }
        Assert.Equal(direct.Snapshot().TotalWork, lookup.Snapshot().TotalWork); Assert.Equal(0L, lookup.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedLookupHonorsEitherPreCanceledRouteAndKeepsPriorFunding(bool governorToken)
    {
        ResourceGovernor owner = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(owner);
        PdfDictionary dictionary = Complete(memory, owner, new PdfName([0, 255]), PdfNull.Instance); long before = owner.Snapshot().LiveMemoryBytes;
        using CancellationTokenSource cancellation = new(); ResourceGovernor lookup = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PdfDictionaryLookup.TryGet(dictionary, new byte[] { 0, 255 }, lookup,
            governorToken ? CancellationToken.None : cancellation.Token, out _));
        Assert.Equal(0L, lookup.Snapshot().TotalWork); Assert.Equal(before, owner.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void OwnedLongLookupDoesNotAllocateAndPreservesItsBorrowedValue()
    {
        byte[] raw = Bytes(97); PdfArray value = new([PdfNull.Instance]); ResourceGovernor owner = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(owner); PdfDictionary dictionary = Complete(memory, owner, new PdfName(raw), value);
        ResourceGovernor lookup = new(QpdfLimits.Default, CancellationToken.None);
        for (int index = 0; index < 32; index++) { PdfDictionaryLookup.TryGet(dictionary, raw, lookup, CancellationToken.None, out _); }
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        bool result = PdfDictionaryLookup.TryGet(dictionary, raw, lookup, CancellationToken.None, out QpdfObject found);
        long delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(result); Assert.Same(value, found); Assert.Equal(0L, delta); Assert.Equal(0L, lookup.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void OwnedLookupWorkExhaustionKeepsBackingUsableByAnotherCaller()
    {
        byte[] raw = Bytes(97); PdfArray value = new([PdfNull.Instance]); ResourceGovernor owner = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(owner); PdfDictionary dictionary = Complete(memory, owner, new PdfName(raw), value);
        long before = owner.Snapshot().LiveMemoryBytes;
        ResourceGovernor lookup = new(QpdfLimits.Default with { MaxWork = 128L }, CancellationToken.None);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => PdfDictionaryLookup.TryGet(dictionary, raw, lookup, CancellationToken.None, out _));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName); Assert.Equal(before, owner.Snapshot().LiveMemoryBytes);
        ResourceGovernor retry = new(QpdfLimits.Default, CancellationToken.None);
        Assert.True(PdfDictionaryLookup.TryGet(dictionary, raw, retry, CancellationToken.None, out QpdfObject found)); Assert.Same(value, found);
        Assert.Equal(0L, retry.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedDispatchHonorsEitherCancellationRouteAfterCompletedComparisonBytes(bool governorToken)
    {
        byte[] raw = Bytes(97), query = Bytes(98); PdfArray value = new([PdfNull.Instance]); ResourceGovernor owner = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(owner); PdfDictionary dictionary = Complete(memory, owner, new PdfName(raw), value);
        long before = owner.Snapshot().LiveMemoryBytes;
        using CancellationTokenSource cancellation = new(); ResourceGovernor lookup = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ManualResetEventSlim reached = new(), resume = new();
        Task worker = Task.Factory.StartNew(() => PdfDictionaryLookup.TryGetObserved(dictionary, query, lookup,
            governorToken ? CancellationToken.None : cancellation.Token,
            () => { reached.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Owned lookup observation did not resume."); } }, out _),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10))); Assert.True(lookup.Snapshot().TotalWork >= 256L);
            cancellation.Cancel(); resume.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(before, owner.Snapshot().LiveMemoryBytes); Assert.True(dictionary.TryGet(new PdfName(raw), out QpdfObject found)); Assert.Same(value, found);
        }
        finally { cancellation.Cancel(); resume.Set(); try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { } }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PdfDictionary Dictionary, WeakReference Governor, WeakReference Observer) CompleteLookup()
    {
        byte[] raw = Bytes(97); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); PdfDictionary dictionary = Complete(memory, governor, new PdfName(raw), PdfNull.Instance);
        object state = new(); Action observe = () => GC.KeepAlive(state);
        Assert.True(PdfDictionaryLookup.TryGetObserved(dictionary, raw, governor, CancellationToken.None, observe, out _));
        return (dictionary, new WeakReference(governor), new WeakReference(observe));
    }

    [Fact]
    public void OwnedLookupDoesNotAttachCallerOrCallbackToPublishedBacking()
    {
        var state = CompleteLookup(); for (int round = 0; round < 3; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(state.Governor.IsAlive); Assert.False(state.Observer.IsAlive); Assert.Equal(1, state.Dictionary.Count); GC.KeepAlive(state);
    }
}
