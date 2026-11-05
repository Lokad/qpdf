using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class LabelComparisonStorageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownLabelContainerTypeDoesNotAllocateItsPresentation(bool escaped)
    {
        const int length = 262144;
        string name = escaped ? string.Concat(Enumerable.Repeat("#FF", length)) : new string('Z', length);
        var objects = Inspection.CheckTests.SinglePage("<< /Length 3 >>", "q Q"u8.ToArray());
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /PageLabels << /Nums [0 << /S /D /P << /Type /"
            + name + " /Private (keep) >> >>] >> >>", null);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Inspection.CheckTests.Assemble(objects, 1), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, QpdfLimits.Default, 1, memory);

        long before = GC.GetAllocatedBytesForCurrentThread();
        SourceLabels labels = PageLabelMerge.ReadSource(document.Engine, copier, QpdfLimits.Default, governor, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 65536L, $"Label classification allocated {allocated} bytes.");
        PdfDictionary prefix = Assert.IsType<PdfDictionary>(Assert.Single(labels.Entries).Entry.Prefix);
        Assert.True(prefix.TryGet(PdfName.FromAscii("Type"), out QpdfObject type));
        Assert.Equal(length, Assert.IsType<PdfName>(type).RawSpan.Length);
        Assert.True(prefix.TryGet(PdfName.FromAscii("Private"), out QpdfObject value));
        Assert.Equal("keep"u8.ToArray(), Assert.IsType<PdfByteString>(value).ValueSpan.ToArray());
    }

    [Fact]
    public async Task LabelSourceRefusalRefundsAdmittedTreeScratch()
    {
        var objects = Inspection.CheckTests.SinglePage("<< /Length 3 >>", "q Q"u8.ToArray());
        string numbers = string.Join(" ", Enumerable.Range(0, 512).Select(index => index + " << /S /D >>"));
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /PageLabels << /Nums [" + numbers + "] >> >>", null);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Inspection.CheckTests.Assemble(objects, 1), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(document.Engine, construction, QpdfLimits.Default, 1, copierMemory);
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
        SourceLabels source = new(true, [(0, new(PdfName.FromAscii("D"), null, 1))]);
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); long baseline = governor.Snapshot().LiveMemoryBytes;
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PageLabelMerge.Merge([(source, 1)], new Dictionary<int, QpdfObject>(), QpdfLimits.Default,
            governor, caller ? cancellation.Token : CancellationToken.None, memory));
        memory.Discard(); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ManyBreakpointsCompressWithBoundedLookupStorage()
    {
        PdfName style = PdfName.FromAscii("D");
        SourceLabels source = new(true, Enumerable.Range(0, 16384)
            .Select(index => (index, new SourceLabelEntry(style, null, index + 1L))).ToArray());
        var occurrences = Enumerable.Range(1, source.Entries.Length).Select(page => (source, page)).ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192 }, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Single(PageLabelMerge.Merge(occurrences, new Dictionary<int, QpdfObject>(), QpdfLimits.Default, governor, CancellationToken.None, memory));
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(bytes < 65536L, $"Label merging allocated {bytes} bytes.");
    }

    [Fact]
    public void PredecessorLookupPreservesArbitrarySelectionsAndRunCompression()
    {
        PdfName style = PdfName.FromAscii("D");
        PdfByteString a = new("A"u8.ToArray(), false), b = new("B"u8.ToArray(), false), c = new("C"u8.ToArray(), false);
        SourceLabels source = new(true, [(0, new(style, a, 10)), (2, new(style, b, 20)), (4, new(style, c, 7))]);
        var occurrences = new[] { 1, 5, 3, 4, 2, 1 }.Select(page => (source, page)).ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        var merged = PageLabelMerge.Merge(occurrences, new Dictionary<int, QpdfObject>(), QpdfLimits.Default, governor, CancellationToken.None, memory);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeEqualPrefixesCompressWithoutComparisonBuffers(bool indirect)
    {
        PdfByteString first = new(new byte[262144], false), second = new(new byte[262144], true);
        PdfName style = PdfName.FromAscii("D");
        Dictionary<int, QpdfObject> pool = new() { [1] = first, [2] = second };
        SourceLabels a = new(true, [(0, new(style, indirect ? new PdfReference(1, 0) : first, 1L))]);
        SourceLabels b = new(true, [(0, new(style, indirect ? new PdfReference(2, 0) : second, 1L))]);
        var occurrences = Enumerable.Range(1, 8).Select(i => (i % 2 == 0 ? b : a, i)).ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None) { OwnsDocumentCounters = true };
        PdfDictionary result;
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var merged = PageLabelMerge.Merge(occurrences, pool, QpdfLimits.Default, governor, CancellationToken.None, memory);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 65536L);
            result = Assert.Single(merged).Label;
            Assert.True(result.TryGet(PdfName.FromAscii("P"), out QpdfObject prefix)); Assert.Same(a.Entries[0].Entry.Prefix, prefix);
            memory.RetainGraph();
        }
        governor.AcceptDocument(); governor.CompletePrivateStorage();
        Assert.True(governor.CallerStorageBytes > 0L); GC.KeepAlive(result);
    }

    [Fact]
    public void ComparisonPreservesReferenceAndLexicalIdentityAndChecksCancellation()
    {
        Dictionary<int, QpdfObject> pool = [];
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.True(PageLabelMerge.SameValue(new PdfReference(7, 0), new PdfReference(7, 0), pool, false, governor, CancellationToken.None));
        Assert.False(PageLabelMerge.SameValue(new PdfReference(7, 0), new PdfReference(8, 0), pool, false, governor, CancellationToken.None));
        Assert.False(PageLabelMerge.SameValue(new PdfReference(7, 0), PdfName.FromAscii("D"), pool, false, governor, CancellationToken.None));
        Assert.False(PageLabelMerge.SameValue(new PdfReal("1.0", 1), new PdfReal("1.00", 1), pool, false, governor, CancellationToken.None));
        Assert.True(PageLabelMerge.SameValue(null, PdfNull.Instance, pool, false, governor, CancellationToken.None));
        PdfByteString a = new(new byte[1048576], false), b = new(new byte[1048576], true);
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PageLabelMerge.SameValue(a, b, pool, true, governor, cancel.Token));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void RefusedLabelConstructionPreservesPriorAdmission()
    {
        SourceLabels source = new(true, [(0, new(PdfName.FromAscii("D"), new PdfByteString(new byte[262144], false), 1L))]);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 512L }, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => PageLabelMerge.Merge([(source, 1)], new Dictionary<int, QpdfObject>(), QpdfLimits.Default, governor, CancellationToken.None, memory));
        memory.Discard(); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ResolvedComparisonChecksEitherCancellationAfterPoolLookup(bool caller, bool prefix)
    {
        using CancellationTokenSource cancellation = new();
        QpdfObject value = prefix ? new PdfByteString("P-"u8.ToArray(), false) : PdfName.FromAscii("D");
        CancelingObjects pool = new(cancellation.Cancel) { [1] = value };
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            memory.Reserve(128L);
            long before = governor.Snapshot().LiveMemoryBytes;
            Assert.ThrowsAny<OperationCanceledException>(() => PageLabelMerge.SameValue(new PdfReference(1, 0), value,
                pool, prefix, governor, caller ? cancellation.Token : CancellationToken.None));
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class CancelingObjects(Action cancel) : Dictionary<int, QpdfObject>, IReadOnlyDictionary<int, QpdfObject>
    {
        bool IReadOnlyDictionary<int, QpdfObject>.TryGetValue(int key, [MaybeNullWhen(false)] out QpdfObject value)
        {
            bool found = base.TryGetValue(key, out value);
            cancel(); return found;
        }
    }
}
