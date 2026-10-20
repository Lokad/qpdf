using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentStorageTests
{
    private static DocumentEngine Engine()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary trailer = new(new Dictionary<PdfName, QpdfObject>());
        return new(ReadOnlyMemory<byte>.Empty, new XRefTable(0L, new Dictionary<int, XrefEntry>(), trailer, [trailer]),
            QpdfLimits.Default, governor, new(governor), QpdfRecoveryMode.Strict);
    }

    [Fact]
    public void RawExtractionBorrowsDocumentStorageWithoutPayloadCopies()
    {
        byte[] bytes = new byte[16 * 1024 * 1024];
        PdfStream stream = PdfStream.FromOwnedData(new(new Dictionary<PdfName, QpdfObject>()), bytes);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 1024 }, CancellationToken.None);
        using DocumentEngine engine = Engine();
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        ReadOnlyMemory<byte> result = EmbeddedFiles.ExtractData(stream, 1, engine, QpdfLimits.Default,
            governor, new(governor), memory, CancellationToken.None);
        Assert.Equal(stream.RawMemory, result);
        Assert.Equal(0L, memory.ReservedBytes);
        GC.KeepAlive(stream); // The caller keeps the source document alive through consumption.
    }

    [Fact]
    public void SlicedFilteredInputIsAdmittedBeforeMaterialization()
    {
        byte[] bytes = new byte[16 * 1024 * 1024];
        PdfStream stream = PdfStream.FromImmutableSlice(new(new Dictionary<PdfName, QpdfObject>
            { [PdfName.FromAscii("Filter")] = PdfName.FromAscii("ASCIIHexDecode") }), bytes.AsMemory(1));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using DocumentEngine engine = Engine();
        long before = GC.GetAllocatedBytesForCurrentThread();
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            Assert.Throws<QpdfLimitException>(() => EmbeddedFiles.ExtractData(stream, 1, engine, QpdfLimits.Default,
                governor, new(governor), memory, CancellationToken.None));
        }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < bytes.Length);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void DecodedStorageStaysFundedThroughConsumptionAndRefundsOnExit()
    {
        PdfStream stream = PdfStream.FromOwnedData(new(new Dictionary<PdfName, QpdfObject>
            { [PdfName.FromAscii("Filter")] = PdfName.FromAscii("ASCIIHexDecode") }), "616263>"u8.ToArray());
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using DocumentEngine engine = Engine();
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            ReadOnlyMemory<byte> result = EmbeddedFiles.ExtractData(stream, 1, engine, QpdfLimits.Default,
                governor, new(governor), memory, CancellationToken.None);
            Assert.Equal("abc"u8.ToArray(), result.ToArray());
            Assert.True(memory.ReservedBytes >= result.Length + 32L);
            Assert.True(governor.Snapshot().LiveMemoryBytes >= result.Length + 32L);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void MalformedFilterFallsBackToBorrowedRawAndCancellationPropagates()
    {
        PdfStream stream = PdfStream.FromOwnedData(new(new Dictionary<PdfName, QpdfObject>
            { [PdfName.FromAscii("Filter")] = PdfName.FromAscii("Unsupported") }), "raw"u8.ToArray());
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using DocumentEngine engine = Engine();
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        DiagnosticBag diagnostics = new(governor);
        Assert.Equal(stream.RawMemory, EmbeddedFiles.ExtractData(stream, 1, engine, QpdfLimits.Default,
            governor, diagnostics, memory, CancellationToken.None));
        Assert.Contains("extracted raw", Assert.Single(diagnostics.Entries).Message);
        using CancellationTokenSource source = new(); source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => EmbeddedFiles.ExtractData(stream, 1, engine,
            QpdfLimits.Default, governor, diagnostics, memory, source.Token));
    }
}
