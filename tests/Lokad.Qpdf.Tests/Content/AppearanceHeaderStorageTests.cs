using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceHeaderStorageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlayHeadersAndClonesBorrowFundedPayloads(bool clone)
    {
        PdfStream source = PdfStream.FromOwnedData(new(new Dictionary<PdfName, QpdfObject>()), new byte[8 * 1024 * 1024]);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 8192L };
        ResourceGovernor governor = new(limits, CancellationToken.None) { OwnsDocumentCounters = true };
        using DocumentEngine engine = Engine(); using ParserMemoryScope copierMemory = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(engine, governor, limits, 1, copierMemory);
        PdfReference link = copier.AddIndirectObjectWithId(source, 1);
        PdfStream result;
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            if (clone)
            {
                int allocator = 2, nullId = -1;
                result = Assert.IsType<PdfStream>(Overlays.BuildOverlayContainer(source, engine, copier, copier, new(), memory,
                    ref allocator, ref nullId, limits, governor, memory, CancellationToken.None, 0, false));
                memory.RetainGraph();
            }
            else
            {
                using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
                int allocator = 2;
                PdfReference placed = Overlays.CopyOverlayAppearanceStream(copier, link, [0.5, 0, 0, 0.5, 2, 3], null, graph, new(governor), ref allocator);
                result = Assert.IsType<PdfStream>(copier.CopiedObjects[placed.ObjectNumber]);
                Assert.Same(source, copier.CopiedObjects[link.ObjectNumber]); graph.Retain();
            }
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < source.RawMemory.Length);
            Assert.Same(source.AllocationCarrier, result.AllocationCarrier);
        }
        governor.AcceptDocument(); governor.CompletePrivateStorage();
        Assert.True(governor.CallerStorageBytes > 0L);
        Assert.Equal(source.RawMemory.Length, result.RawMemory.Length); GC.KeepAlive(result);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("caller")]
    [InlineData("governor")]
    public void OverlayHeaderRefusalOrCancellationPreservesSource(string failure)
    {
        PdfStream source = PdfStream.FromOwnedData(new(Enumerable.Range(0, 512).ToDictionary(
            i => PdfName.FromAscii(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), _ => (QpdfObject)PdfNull.Instance)), new byte[1048576]);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        using CancellationTokenSource operation = new();
        ResourceGovernor governor = new(limits, operation.Token);
        using DocumentEngine engine = Engine(); using ParserMemoryScope copierMemory = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(engine, governor, limits, 1, copierMemory);
        copier.AddIndirectObjectWithId(source, 1); long baseline = governor.Snapshot().LiveMemoryBytes;
        using CancellationTokenSource cancellation = new();
        if (failure == "caller") { cancellation.Cancel(); }
        if (failure == "governor") { operation.Cancel(); }
        Action rewrite = () =>
        {
            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellation.Token);
            int allocator = 2;
            Overlays.CopyOverlayAppearanceStream(copier, new(1, 0), [0.5, 0, 0, 0.5, 0, 0], null, graph, new(governor), ref allocator);
        };
        if (failure == "memory") { Assert.Throws<QpdfLimitException>(rewrite); }
        else { Assert.ThrowsAny<OperationCanceledException>(rewrite); }
        Assert.Same(source, copier.CopiedObjects[1]); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    private static DocumentEngine Engine()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary trailer = new(new Dictionary<PdfName, QpdfObject>());
        return TestDocumentEngine.Create(trailer, governor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpaqueMatrixKeepsItsHeaderAndLargePayloadWhenReportingOrRefusing(bool overflow)
    {
        PdfStream source = PdfStream.FromOwnedData(new(new Dictionary<PdfName, QpdfObject>
            { [PdfName.FromAscii("Matrix")] = new PdfInteger("42", 42) }), new byte[8 * 1048576]);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 8192L, MaxDiagnostics = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        using DocumentEngine engine = Engine(); using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(engine, governor, limits, 1, memory); copier.AddIndirectObjectWithId(source, 1);
        if (overflow) { diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Info, "prior"); }
        long before = governor.Snapshot().LiveMemoryBytes, allocated = GC.GetAllocatedBytesForCurrentThread();
        void Place()
        {
            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
            int allocator = 2;
            PdfReference placed = Overlays.CopyOverlayAppearanceStream(copier, new(1, 0), [0.5, 0, 0, 0.5, 2, 3], null, graph, diagnostics, ref allocator);
            Assert.Equal(1, placed.ObjectNumber);
        }
        if (overflow)
        {
            QpdfLimitException error = Assert.Throws<QpdfLimitException>(Place); Assert.Equal(nameof(QpdfLimits.MaxDiagnostics), error.LimitName);
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        }
        else { Place(); Assert.Single(diagnostics.Entries); }
        Assert.Same(source, copier.CopiedObjects[1]); Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 32768L);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FlatteningPromotesOrReusesAppearanceWithoutCopyingItsPayload(bool form, bool indirect)
    {
        byte[] payload = new byte[8 * 1024 * 1024]; payload[17] = 91;
        PdfDictionary header = new(new Dictionary<PdfName, QpdfObject>
            { [PdfName.FromAscii("Subtype")] = PdfName.FromAscii(form ? "Form" : "Other") });
        PdfStream source = PdfStream.FromOwnedData(header, payload);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 8192L };
        ResourceGovernor governor = new(limits, CancellationToken.None) { OwnsDocumentCounters = true };
        using DocumentEngine engine = Engine(); using ParserMemoryScope copierMemory = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(engine, governor, limits, 1, copierMemory);
        PdfReference? link = indirect ? copier.AddIndirectObjectWithId(source, 1) : null;
        int allocator = 2;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        PdfReference result = FlattenAnnotations.EnsureAppearanceObject(copier, source, link, new(copier.CopiedObjects), governor, CancellationToken.None, ref allocator);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < payload.Length);
        PdfStream replacement = Assert.IsType<PdfStream>(copier.CopiedObjects[result.ObjectNumber]);
        Assert.Same(source.AllocationCarrier, replacement.AllocationCarrier);
        Assert.Equal(91, replacement.RawMemory.Span[17]);
        Assert.True(replacement.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject subtype));
        Assert.Equal("Form", Assert.IsType<PdfName>(subtype).DisplayText);
        Assert.Equal(form && indirect ? 2 : 3, allocator);
        if (indirect)
        { Assert.Same(source, copier.CopiedObjects[1]); Assert.Equal(form, result.ObjectNumber == 1); }
        governor.AcceptDocument(); governor.CompletePrivateStorage();
        if (!form) { Assert.True(governor.CallerStorageBytes > 0L); }
        GC.KeepAlive(replacement); GC.KeepAlive(source);
    }

    [Fact]
    public void PageLocalAppearanceCopiesBorrowPayloadAndShareWithinPage()
    {
        PdfStream source = PdfStream.FromOwnedData(new(new Dictionary<PdfName, QpdfObject>()), new byte[8 * 1024 * 1024]);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 8192L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using DocumentEngine engine = Engine(); using ParserMemoryScope copierMemory = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(engine, governor, limits, 1, copierMemory);
        PdfReference link = copier.AddIndirectObjectWithId(source, 1);
        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        Dictionary<(int, AnnotationCopyKind, bool), PdfReference> copies = ParserContainers.CreateDictionary<(int, AnnotationCopyKind, bool), PdfReference>(traversal);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        int allocator = 2;
        PdfReference copy = FlattenRotation.CopyRotatedAppearance(copier, link, [0, -1, 1, 0, 0, 612], copies, traversal, graph, ref allocator);
        PdfStream replacement = Assert.IsType<PdfStream>(copier.CopiedObjects[copy.ObjectNumber]);
        Assert.Same(source, copier.CopiedObjects[1]); Assert.NotEqual(link, copy);
        Assert.Same(source.AllocationCarrier, replacement.AllocationCarrier);
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Equal(copy, FlattenRotation.CopyRotatedAppearance(copier, link, [0, -1, 1, 0, 0, 612], copies, traversal, graph, ref allocator));
        Assert.Same(replacement, copier.CopiedObjects[copy.ObjectNumber]); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(3, allocator); graph.Retain();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeaderRefusalLeavesSourceAndPriorAdmissionIntact(bool rotation)
    {
        PdfStream source = PdfStream.FromOwnedData(new(Enumerable.Range(0, 512).ToDictionary(
            i => PdfName.FromAscii(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), _ => (QpdfObject)PdfNull.Instance)), new byte[1048576]);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using DocumentEngine engine = Engine(); using ParserMemoryScope copierMemory = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(engine, governor, limits, 1, copierMemory);
        PdfReference link = copier.AddIndirectObjectWithId(source, 1); long baseline = governor.Snapshot().LiveMemoryBytes;
        int allocator = 2;
        Assert.Throws<QpdfLimitException>(() =>
        {
            if (rotation)
            {
                using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
                Dictionary<(int, AnnotationCopyKind, bool), PdfReference> copies = ParserContainers.CreateDictionary<(int, AnnotationCopyKind, bool), PdfReference>(traversal);
                using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
                FlattenRotation.CopyRotatedAppearance(copier, link, [1, 0, 0, 1, 0, 0], copies, traversal, graph, ref allocator);
            }
            else { FlattenAnnotations.EnsureAppearanceObject(copier, source, link, new(copier.CopiedObjects), governor, CancellationToken.None, ref allocator); }
        });
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Same(source, copier.CopiedObjects[1]);
        Assert.Equal(2, allocator);
    }
}
