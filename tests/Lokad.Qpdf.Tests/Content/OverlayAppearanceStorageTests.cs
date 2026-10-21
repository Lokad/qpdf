using System.Text;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayAppearanceStorageTests
{
    [Fact]
    public async Task CatalogDefaultsBorrowFundedBytesAndHonorCancellation()
    {
        byte[] raw = new byte[2 * 1024 * 1024]; Array.Fill(raw, (byte)' ');
        var objects = Inspection.CheckTests.SinglePage("<< /Length 3 >>", "q Q"u8.ToArray());
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /DA <" + Convert.ToHexString(raw) + "> >> >>", null);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Inspection.CheckTests.Assemble(objects, 1), QpdfInputOwnership.Transfer);
        PdfByteString borrowed;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None))
        {
            _ = document.GetObject(1);
            QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L };
            ResourceGovernor governor = new(limits, CancellationToken.None);
            using ParserMemoryScope planning = ParserMemoryScope.Create(governor);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            AcroFormCopy.OverlaySourceForm? source = AcroFormCopy.TryReadOverlaySourceForm(document.Engine, limits, governor, planning, CancellationToken.None);
            Assert.NotNull(source); borrowed = Assert.IsType<PdfByteString>(source.DefaultAppearance);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 65536L);
            Assert.True(governor.Snapshot().LiveMemoryBytes > 0L);
            CopiedForm combined = new([], false, false, false, null, false, [], new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("DA")] = borrowed });
            Assert.Same(borrowed, AcroFormCopy.ReadMergedDefaults(combined).Appearance);
            using CancellationTokenSource cancellation = new(); cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => AcroFormCopy.TryReadOverlaySourceForm(document.Engine, limits, governor, planning, cancellation.Token));
        }
        Assert.Equal(raw, borrowed.ValueSpan.ToArray());
    }

    private static PdfNameTable<PdfName> Map(ParserMemoryScope memory, ResourceGovernor governor)
    {
        PdfNameTable<PdfName> map = PdfNameTable<PdfName>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
        map.Assign(memory, governor, PdfName.FromAscii("F1"), PdfName.FromAscii("F1_1"), CancellationToken.None);
        return map;
    }

    [Theory]
    [InlineData("/Keep 12 Tf", null)]
    [InlineData("/F1 12 Tf (unfinished", null)]
    [InlineData("/F1 12 Tf 14 Tf /Keep 9 Tf", "/F1_1 12 Tf 14 Tf /Keep 9 Tf")]
    public void StreamingRewriteKeepsOriginalBytesAndMalformedFallback(string text, string? expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<PdfName> map = Map(memory, governor);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        PdfByteString? result = AcroFormCopy.RewriteOverlayDa(new(Encoding.ASCII.GetBytes(text), false), map,
            QpdfLimits.Default, graph);
        if (expected is null) { Assert.Null(result); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); }
        else
        {
            graph.Retain(); Assert.NotNull(result); Assert.Equal(expected, Encoding.ASCII.GetString(result.ValueSpan));
            Assert.True(governor.Snapshot().LiveMemoryBytes >= baseline + result.ValueSpan.Length);
            governor.CompletePrivateStorage(); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        }
    }

    [Fact]
    public void LargeUnchangedInputRequiresNoTokenSizedStorage()
    {
        PdfByteString input = new(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("/Keep 12 Tf ", 20000))), false);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<PdfName> map = Map(memory, governor);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.Null(AcroFormCopy.RewriteOverlayDa(input, map, limits, graph));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusalOrCancellationPreservesEarlierAdmission(bool cancel)
    {
        PdfByteString input = new(Encoding.ASCII.GetBytes("/F1 12 Tf " + new string(' ', 32768)), false);
        using CancellationTokenSource source = new();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 2048L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<PdfName> map = Map(memory, governor);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, source.Token);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        if (cancel)
        {
            source.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => AcroFormCopy.RewriteOverlayDa(input, map, limits, graph));
        }
        else { Assert.Throws<QpdfLimitException>(() => AcroFormCopy.RewriteOverlayDa(input, map, limits, graph)); }
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }
}
