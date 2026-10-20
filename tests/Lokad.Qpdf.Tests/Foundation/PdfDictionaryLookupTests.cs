using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(NameTableCollection))]
public sealed class PdfDictionaryLookupTests
{
    private static byte[] Bytes(int last)
    {
        byte[] bytes = new byte[8192]; Array.Fill(bytes, (byte)97); bytes[^1] = (byte)last; return bytes;
    }
    private static PdfDictionary Dictionary(byte[] bytes, QpdfObject value)
        => new(new Dictionary<PdfName, QpdfObject> { [new PdfName(bytes)] = value });

    [Fact]
    public void BorrowedRawLookupDistinguishesMissingNullAndBinaryKeys()
    {
        PdfArray value = new([new PdfInteger("7", 7L)]);
        PdfDictionary dictionary = new(new Dictionary<PdfName, QpdfObject>
        { [new PdfName([0, 255, 35])] = value, [new PdfName([])] = PdfNull.Instance });
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.True(PdfDictionaryLookup.TryGet(dictionary, [0, 255, 35], governor, CancellationToken.None, out QpdfObject found)); Assert.Same(value, found);
        Assert.True(PdfDictionaryLookup.TryGet(dictionary, [], governor, CancellationToken.None, out found)); Assert.Same(PdfNull.Instance, found);
        Assert.False(PdfDictionaryLookup.TryGet(dictionary, [0, 255, 36], governor, CancellationToken.None, out found)); Assert.Same(PdfNull.Instance, found);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(2, dictionary.Count);
    }

    [Fact]
    public void LegacyScanChargesEveryVisitedEntryAndFinalMissingVisit()
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        for (int index = 0; index < 512; index++) { entries.Add(PdfName.FromAscii(index.ToString(System.Globalization.CultureInfo.InvariantCulture)), PdfNull.Instance); }
        PdfDictionary dictionary = new(entries); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.False(PdfDictionaryLookup.TryGet(dictionary, new byte[64], governor, CancellationToken.None, out _));
Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void LongLookupAndNameComparisonAllocateNoKeyEnumeratorOrDisplayText()
    {
        byte[] query = Bytes(97); PdfName name = new(query); PdfByteString borrowed = new([65, 255], false);
        PdfDictionary dictionary = Dictionary(query, borrowed); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        for (int index = 0; index < 32; index++) { PdfDictionaryLookup.TryGet(dictionary, query, governor, CancellationToken.None, out _); PdfDictionaryLookup.NameEquals(name, query, governor, CancellationToken.None); }
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        bool found = PdfDictionaryLookup.TryGet(dictionary, query, governor, CancellationToken.None, out QpdfObject value);
        bool equal = PdfDictionaryLookup.NameEquals(name, query, governor, CancellationToken.None);
        long delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(found); Assert.True(equal); Assert.Same(borrowed, value); Assert.Equal(0L, delta);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BothPreCanceledRoutesRejectBeforeAnyWork(bool governorToken, bool compare)
    {
        byte[] query = Bytes(97); PdfName name = new(query); PdfDictionary dictionary = Dictionary(query, PdfNull.Instance);
        using CancellationTokenSource cancellation = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        cancellation.Cancel(); CancellationToken token = governorToken ? CancellationToken.None : cancellation.Token;
        if (compare) { Assert.ThrowsAny<OperationCanceledException>(() => PdfDictionaryLookup.NameEquals(name, query, governor, token)); }
        else { Assert.ThrowsAny<OperationCanceledException>(() => PdfDictionaryLookup.TryGet(dictionary, query, governor, token, out _)); }
Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterCompletedComparisonBytesLeavesDictionaryUsable(bool governorToken)
    {
        byte[] stored = Bytes(97), query = Bytes(98); PdfByteString value = new([65, 255], false); PdfDictionary dictionary = Dictionary(stored, value);
        using CancellationTokenSource cancellation = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ManualResetEventSlim reached = new(), resume = new();
        Task worker = Task.Factory.StartNew(() => PdfDictionaryLookup.TryGetObserved(dictionary, query, governor,
            governorToken ? CancellationToken.None : cancellation.Token,
            () => { reached.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Name lookup observation did not resume."); } }, out _),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10)));             cancellation.Cancel(); resume.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.True(dictionary.TryGet(new PdfName(stored), out QpdfObject found)); Assert.Same(value, found); GC.KeepAlive(worker);
        }
        finally { cancellation.Cancel(); resume.Set(); try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData(new byte[] {}, new byte[] {}, true)]
    [InlineData(new byte[] { 0, 255 }, new byte[] { 0, 255 }, true)]
    [InlineData(new byte[] { 35, 48, 48 }, new byte[] { 0 }, false)]
    [InlineData(new byte[] { 65 }, new byte[] { 97 }, false)]
    public void NameComparisonUsesDecodedBytesWithOrdinalSemantics(byte[] raw, byte[] expected, bool equal)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(equal, PdfDictionaryLookup.NameEquals(new PdfName(raw), expected, governor, CancellationToken.None));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PdfDictionary Dictionary, QpdfObject Value, WeakReference Governor, WeakReference Observer) Completed()
    {
        byte[] query = Bytes(97); PdfArray value = new([PdfNull.Instance]); PdfDictionary dictionary = Dictionary(query, value);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); object callbackState = new(); Action observer = () => GC.KeepAlive(callbackState);
        Assert.True(PdfDictionaryLookup.TryGetObserved(dictionary, query, governor, CancellationToken.None, observer, out QpdfObject found)); Assert.Same(value, found);
        return (dictionary, value, new WeakReference(governor), new WeakReference(observer));
    }

    [Fact]
    public void CompletedLookupDoesNotAttachControllerOrCallbackToBorrowedDictionary()
    {
        var state = Completed();
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(state.Governor.IsAlive); Assert.False(state.Observer.IsAlive); Assert.Same(state.Value, Assert.Single(state.Dictionary.Keys.Select(key => { Assert.True(state.Dictionary.TryGet(key, out QpdfObject value)); return value; })));
        GC.KeepAlive(state);
    }
}
