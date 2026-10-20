using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayContentStorageTests
{
    [Fact]
    public void RawChunksBorrowFullArraysAndAdmitSliceAdaptersBeforeCopying()
    {
        byte[] payload = new byte[8 * 1024 * 1024];
        PdfDictionary header = new(new Dictionary<PdfName, QpdfObject>());
        PdfStream full = PdfStream.FromOwnedData(header, payload);
        PdfStream slice = PdfStream.FromImmutableSlice(header, payload.AsMemory(1));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, CancellationToken.None);
        PoolResolver resolver = new(new Dictionary<int, QpdfObject>());
        Assert.True(chunks.TryAdd(full, resolver, QpdfLimits.Default));
        Assert.Same(payload, Assert.Single(chunks.Parts));
        long baseline = governor.Snapshot().LiveMemoryBytes;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => chunks.TryAdd(slice, resolver, QpdfLimits.Default));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < payload.Length);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void FilteredChunksRetireAfterTheirJoinedResultTransfers()
    {
        byte[] encoded;
        using (MemoryStream bytes = new())
        {
            using (System.IO.Compression.ZLibStream zip = new(bytes, System.IO.Compression.CompressionLevel.Fastest, true))
            { zip.Write(new byte[1024 * 1024]); }
            encoded = bytes.ToArray();
        }
        PdfStream stream = PdfStream.FromOwnedData(new(new Dictionary<PdfName, QpdfObject>
            { [PdfName.FromAscii("Filter")] = PdfName.FromAscii("FlateDecode") }), encoded);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope resultMemory = ParserMemoryScope.Create(governor);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        byte[] result;
        using (AnalysisChunks chunks = AnalysisChunks.Create(governor, CancellationToken.None))
        {
            Assert.True(chunks.TryAdd(stream, new PoolResolver(new Dictionary<int, QpdfObject>()), QpdfLimits.Default));
            using JoinedContent joined = JoinedContent.Create(chunks.Parts, governor, CancellationToken.None);
            result = joined.TakeBytes(resultMemory);
        }
        Assert.Equal(1024 * 1024, result.Length);
        Assert.InRange(governor.Snapshot().LiveMemoryBytes, baseline + result.Length, baseline + result.Length + 128L);
        resultMemory.Discard(); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }
}
