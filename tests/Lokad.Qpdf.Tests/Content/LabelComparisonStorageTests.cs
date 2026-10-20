using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class LabelComparisonStorageTests
{
    [Fact]
    public void LargeEqualPrefixesCompressWithoutComparisonBuffers()
    {
        PdfByteString first = new(new byte[262144], false), second = new(new byte[262144], true);
        PdfName style = PdfName.FromAscii("D");
        SourceLabels a = new(true, new() { [0] = new(null, null, style, first, 1L) });
        SourceLabels b = new(true, new() { [0] = new(null, null, style, second, 1L) });
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
        SourceLabels source = new(true, new() { [0] = new(null, null, PdfName.FromAscii("D"), new PdfByteString(new byte[262144], false), 1L) });
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 512L }, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => PageLabelMerge.Merge([(source, 1)], QpdfLimits.Default, governor, CancellationToken.None, memory));
        memory.Discard(); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }
}
