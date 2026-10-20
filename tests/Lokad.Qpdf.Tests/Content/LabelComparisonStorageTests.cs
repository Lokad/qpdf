using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class LabelComparisonStorageTests
{
    [Fact]
    public async Task LabelSourceRefusalRefundsAdmittedTreeScratch()
    {
        var objects = Inspection.CheckTests.SinglePage("<< /Length 3 >>", "q Q"u8.ToArray());
        string numbers = string.Join(" ", Enumerable.Range(0, 512).Select(index => index + " << /S /D >>"));
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /PageLabels << /Nums [" + numbers + "] >> >>", null);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Inspection.CheckTests.Assemble(objects, 1), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        GraphCopier copier = new(document.Engine, construction, QpdfLimits.Default, 1);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 1024 }, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => PageLabelMerge.ReadSource(document.Engine, copier, QpdfLimits.Default,
            governor, CancellationToken.None));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 65536L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Single(document.PageReferences);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LookupCancellationPreservesPriorAdmission(bool caller)
    {
        SourceLabels source = new(true, [(0, new(null, null, PdfName.FromAscii("D"), null, 1))]);
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); long baseline = governor.Snapshot().LiveMemoryBytes;
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PageLabelMerge.Merge([(source, 1)], QpdfLimits.Default,
            governor, caller ? cancellation.Token : CancellationToken.None, memory));
        memory.Discard(); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ManyBreakpointsCompressWithBoundedLookupStorage()
    {
        PdfName style = PdfName.FromAscii("D");
        SourceLabels source = new(true, Enumerable.Range(0, 16384)
            .Select(index => (index, new SourceLabelEntry(null, null, style, null, index + 1L))).ToArray());
        var occurrences = Enumerable.Range(1, source.Entries.Length).Select(page => (source, page)).ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192 }, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Single(PageLabelMerge.Merge(occurrences, QpdfLimits.Default, governor, CancellationToken.None, memory));
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(bytes < 65536L, $"Label merging allocated {bytes} bytes.");
    }

    [Fact]
    public void PredecessorLookupPreservesArbitrarySelectionsAndRunCompression()
    {
        PdfName style = PdfName.FromAscii("D");
        PdfByteString a = new("A"u8.ToArray(), false), b = new("B"u8.ToArray(), false), c = new("C"u8.ToArray(), false);
        SourceLabels source = new(true, [(0, new(null, null, style, a, 10)), (2, new(null, null, style, b, 20)), (4, new(null, null, style, c, 7))]);
        var occurrences = new[] { 1, 5, 3, 4, 2, 1 }.Select(page => (source, page)).ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        var merged = PageLabelMerge.Merge(occurrences, QpdfLimits.Default, governor, CancellationToken.None, memory);
        Assert.Equal(new[] { 0, 1, 2, 4, 5 }, merged.Select(entry => entry.Index));
        Assert.Equal(new long[] { 10, 7, 20, 11, 10 }, merged.Select(entry =>
        {
            Assert.True(entry.Label.TryGet(PdfName.FromAscii("St"), out QpdfObject value)); return Assert.IsType<PdfInteger>(value).Value;
        }));
        Assert.Equal(new[] { a, c, b, a, a }, merged.Select(entry =>
        {
            Assert.True(entry.Label.TryGet(PdfName.FromAscii("P"), out QpdfObject value)); return Assert.IsType<PdfByteString>(value);
        }));
    }

    [Fact]
    public void LargeEqualPrefixesCompressWithoutComparisonBuffers()
    {
        PdfByteString first = new(new byte[262144], false), second = new(new byte[262144], true);
        PdfName style = PdfName.FromAscii("D");
        SourceLabels a = new(true, [(0, new(null, null, style, first, 1L))]);
        SourceLabels b = new(true, [(0, new(null, null, style, second, 1L))]);
        var occurrences = Enumerable.Range(1, 8).Select(i => (i % 2 == 0 ? b : a, i)).ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None) { OwnsDocumentCounters = true };
        PdfDictionary result;
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var merged = PageLabelMerge.Merge(occurrences, QpdfLimits.Default, governor, CancellationToken.None, memory);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 65536L);
            result = Assert.Single(merged).Label;
            Assert.True(result.TryGet(PdfName.FromAscii("P"), out QpdfObject prefix)); Assert.Same(first, prefix);
            memory.RetainGraph();
        }
        governor.AcceptDocument(); governor.CompletePrivateStorage();
        Assert.True(governor.CallerStorageBytes > 0L); GC.KeepAlive(result);
    }

    [Fact]
    public void ComparisonPreservesReferenceAndLexicalIdentityAndChecksCancellation()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.True(PageLabelMerge.SameValue(new PdfReference(7, 0), new PdfReference(7, 0), governor, CancellationToken.None));
        Assert.False(PageLabelMerge.SameValue(new PdfReference(7, 0), new PdfReference(8, 0), governor, CancellationToken.None));
        Assert.False(PageLabelMerge.SameValue(new PdfReference(7, 0), PdfName.FromAscii("D"), governor, CancellationToken.None));
        Assert.False(PageLabelMerge.SameValue(new PdfReal("1.0", 1), new PdfReal("1.00", 1), governor, CancellationToken.None));
        Assert.True(PageLabelMerge.SameValue(null, PdfNull.Instance, governor, CancellationToken.None));
        PdfByteString a = new(new byte[1048576], false), b = new(new byte[1048576], true);
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PageLabelMerge.SameValue(a, b, governor, cancel.Token));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void RefusedLabelConstructionPreservesPriorAdmission()
    {
        SourceLabels source = new(true, [(0, new(null, null, PdfName.FromAscii("D"), new PdfByteString(new byte[262144], false), 1L))]);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 512L }, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => PageLabelMerge.Merge([(source, 1)], QpdfLimits.Default, governor, CancellationToken.None, memory));
        memory.Discard(); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }
}
