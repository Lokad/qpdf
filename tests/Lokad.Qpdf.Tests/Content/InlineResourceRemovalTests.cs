using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class InlineResourceRemovalTests
{
    private static PdfDictionary Source(PdfByteString borrowed)
    {
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Resources")] = PdfNull.Instance,
            [new PdfName(new byte[] { 255, 0, 35 })] = borrowed
        };
        for (int index = 0; index < 512; index++)
        { entries.Add(PdfName.FromAscii("K" + index), borrowed); }
        return new(entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureDuringRemovalPreservesBorrowedStorageAndPriorAdmission(bool work)
    {
        PdfByteString borrowed = new(new byte[2 * 1024 * 1024], false);
        PdfDictionary source = Source(borrowed);
        QpdfLimits limits = work ? QpdfLimits.Default with { MaxWork = 200L }
            : QpdfLimits.Default with { MaxMemoryBytes = 2048L };
        for (int attempt = 0; attempt < 8; attempt++)
        {
            ResourceGovernor governor = new(limits, CancellationToken.None);
            using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
            long baseline = governor.Snapshot().LiveMemoryBytes;
            using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, 4, CancellationToken.None))
            {
                QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
                    PdfDictionaryRebuild.RemoveEntry(graph, source, "Resources"u8));
                Assert.Equal(work ? nameof(QpdfLimits.MaxWork) : nameof(QpdfLimits.MaxMemoryBytes), error.LimitName);
            }
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(61L, prior.ReservedBytes); Assert.Equal(514, source.Count);
        }
        GC.KeepAlive(source); GC.KeepAlive(borrowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBeforeRemovalPreservesPriorAdmission(bool caller)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        PdfDictionary source = Source(new(new byte[1], false)); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Remove(governor, source, caller ? cancellation.Token : CancellationToken.None));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(514, source.Count);
    }

    private static void Remove(ResourceGovernor governor, PdfDictionary source, CancellationToken token)
    {
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, 4, token);
        _ = PdfDictionaryRebuild.RemoveEntry(graph, source, "Resources"u8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAtPromotionPreservesScopeUntilDisposal(bool caller)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, 4, caller ? cancellation.Token : CancellationToken.None);
        PdfDictionary source = Source(new(new byte[1], false));
        PdfDictionary output = PdfDictionaryRebuild.RemoveEntry(graph, source, "Resources"u8);
        long live = governor.Snapshot().LiveMemoryBytes;
        cancellation.Cancel(); Assert.Throws<OperationCanceledException>(() => graph.Retain());
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        graph.Dispose(); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(513, output.Count); Assert.Equal(514, source.Count); GC.KeepAlive(output);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Capture(ResourceGovernor governor, PdfDictionary source, PdfByteString borrowed)
    {
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, 4, CancellationToken.None);
        PdfDictionary output = PdfDictionaryRebuild.RemoveEntry(graph, source, "Resources"u8);
        Assert.False(output.TryGet(PdfName.FromAscii("Resources"), out _));
        using PdfDictionary.EntryEnumerator entries = source.GetEntryEnumerator();
        while (entries.MoveNext(governor, CancellationToken.None))
        {
            if (entries.Current.Key.RawSpan.SequenceEqual("Resources"u8)) { continue; }
            Assert.True(output.TryGet(entries.Current.Key, out QpdfObject value)); Assert.Same(borrowed, value);
            Assert.Same(entries.Current.Key, output.Keys.Single(key => key.Equals(entries.Current.Key)));
        }
        graph.Retain(); Assert.InRange(governor.Snapshot().LiveMemoryBytes, 1L, 65536L);
        return new(output);
    }

    [Fact]
    public void RemovedResourcesRetireWhileBorrowedNamesAndPayloadRemainAlive()
    {
        PdfByteString borrowed = new(new byte[2 * 1024 * 1024], false); PdfDictionary source = Source(borrowed);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 65536L }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        WeakReference output = Capture(governor, source, borrowed);
        for (int round = 0; round < 4; round++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); governor.ReserveMemory(0L); }
        Assert.False(output.IsAlive); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(514, source.Count); GC.KeepAlive(source); GC.KeepAlive(borrowed);
    }
}
