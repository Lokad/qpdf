using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayAdmissionTests
{
    private static readonly double[] Identity = [1, 0, 0, 1, 0, 0];

    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));

    private static DocumentEngine Engine()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary trailer = Dictionary();
        using ParserMemoryScope fixtureStorage = ParserMemoryScope.Create(governor);
        Dictionary<int, XrefEntry> fixtureEntries = ParserContainers.CreateDictionary<int, XrefEntry>(fixtureStorage);
        using ParserMemoryScope fixtureValues = ParserMemoryScope.Create(governor);
        List<PdfDictionary> fixtureTrailers = ParserContainers.CreateList<PdfDictionary>(fixtureStorage, 1, IntPtr.Size);
        fixtureTrailers.Add(trailer);
        XRefTable fixtureTable = XRefTable.CreateOwned(0L, fixtureEntries, trailer, fixtureTrailers,
            governor, fixtureStorage, fixtureValues, false);
        DocumentEngine fixtureEngine;
        try
        {
            fixtureStorage.Reserve(32L);
            fixtureEngine = DocumentEngine.CreateOwned(ReadOnlyMemory<byte>.Empty, fixtureTable, QpdfLimits.Default,
                governor, new(governor), QpdfRecoveryMode.Strict, 32L);
            fixtureStorage.Retain();
        }
        catch { fixtureTable.Dispose(); throw; }
        return fixtureEngine;
    }

    [Theory]
    [InlineData(false, "refuse")]
    [InlineData(true, "refuse")]
    [InlineData(false, "caller")]
    [InlineData(true, "caller")]
    [InlineData(false, "governor")]
    [InlineData(true, "governor")]
    public void LargeHeaderRefusesOrCancelsBeforeCopyingAndPreservesSource(bool rectangle, string failure)
    {
        PdfDictionary source = new(Enumerable.Range(0, 16384).ToDictionary(
            i => PdfName.FromAscii(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), _ => (QpdfObject)PdfNull.Instance));
        using DocumentEngine engine = Engine();
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 1, copierMemory); copier.AddIndirectObjectWithId(source, 1);
        using CancellationTokenSource caller = new(), operation = new();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, operation.Token);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            long baseline = governor.Snapshot().LiveMemoryBytes;
            if (failure == "caller") { caller.Cancel(); }
            if (failure == "governor") { operation.Cancel(); }
            Action rewrite = rectangle
                ? () => Overlays.TransformOverlayRect(copier, 1, Identity, governor, memory, caller.Token)
                : () => Overlays.RebuildOverlayPage(source, Dictionary(), new PdfReference(2, 0), governor, memory, caller.Token);
            long before = GC.GetAllocatedBytesForCurrentThread();
            if (failure == "refuse") { Assert.Throws<QpdfLimitException>(rewrite); }
            else { Assert.ThrowsAny<OperationCanceledException>(rewrite); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 131072L, $"Allocated {allocated} bytes before {failure}.");
            Assert.Same(source, copier.CopiedObjects[1]); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("skipped")]
    [InlineData("append")]
    public async Task ImportsBorrowSourceAndOnlyAdmitDestinationStorageForRealAnnotations(string kind)
    {
        string annotations = kind == "empty" ? "[]" : kind == "append" ? "[4 0 R]" : "[" + string.Concat(Enumerable.Repeat("null ", 262144)) + "]";
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots " + annotations + " >>", null),
            (4, "<< /Subtype /Text /Rect [0 0 10 10] >>", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("source"), input, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("source"), CancellationToken.None))
        {
            ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
            using ParserMemoryScope sourceCopierMemory = ParserMemoryScope.Create(construction);
            GraphCopier sourceCopier = GraphCopier.CreateOwned(document.Engine, construction, QpdfLimits.Default, 1, sourceCopierMemory);
            Overlays.OverlaySource source = new(document, sourceCopier, 1, null, new(StringComparer.Ordinal));
            using DocumentEngine engine = Engine(); using ParserMemoryScope destinationMemory = ParserMemoryScope.Create(construction);
            GraphCopier destination = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 10, destinationMemory);
            PdfArray original = new(Enumerable.Repeat<QpdfObject>(new PdfReference(12, 0), 262144).ToArray());
            PdfDictionary page = Dictionary(("Annots", new PdfReference(11, 0)));
            destination.AddIndirectObjectWithId(page, 10); destination.AddIndirectObjectWithId(original, 11);
            destination.AddIndirectObjectWithId(Dictionary(("Subtype", PdfName.FromAscii("Text"))), 12);
            List<AcroFormCopy.OverlayFieldImport> imports = new(); int allocator = 20;
            ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
            DiagnosticBag diagnostics = new(governor) { SuppressWarningStorage = true };
            using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
            {
                Action import = () => Overlays.CopyOverlayPageAnnots(destination, 10, source, 1, Identity, imports,
                    QpdfLimits.Default, governor, memory, diagnostics, CancellationToken.None, ref allocator);
                long before = GC.GetAllocatedBytesForCurrentThread();
                if (kind == "append") { Assert.Throws<QpdfLimitException>(import); }
                else { import(); }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.True(allocated < 131072L, $"Allocated {allocated} bytes for {kind} annotations.");
                Assert.Same(page, destination.CopiedObjects[10]); Assert.Same(original, destination.CopiedObjects[11]); Assert.Empty(imports);
                if (kind != "append") { Assert.Equal(3, destination.CopiedObjects.Count); }
            }
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RebuiltHeadersKeepBorrowedValuesAliveAfterConstructionCloses(bool rectangle)
    {
        PdfStream payload = PdfStream.FromOwnedData(Dictionary(), new byte[8 * 1024 * 1024]);
        PdfDictionary source = Dictionary(("Payload", payload));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None) { OwnsDocumentCounters = true };
        PdfDictionary result;
        using (DocumentEngine engine = Engine())
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
            using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
            GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 1, copierMemory); copier.AddIndirectObjectWithId(source, 1);
            long before = GC.GetAllocatedBytesForCurrentThread();
            if (rectangle)
            {
                Overlays.TransformOverlayRect(copier, 1, Identity, governor, memory, CancellationToken.None);
                result = Assert.IsType<PdfDictionary>(copier.CopiedObjects[1]);
            }
            else
            {
                PdfDictionary resources = Dictionary(); PdfReference contents = new(2, 0);
                result = Overlays.RebuildOverlayPage(source, resources, contents, governor, memory, CancellationToken.None);
                Assert.True(result.TryGet(PdfName.FromAscii("Resources"), out QpdfObject actualResources)); Assert.Same(resources, actualResources);
                Assert.True(result.TryGet(PdfName.FromAscii("Contents"), out QpdfObject actualContents)); Assert.Same(contents, actualContents);
            }
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 131072L); memory.RetainGraph();
        }
        governor.AcceptDocument(); governor.CompletePrivateStorage(); Assert.True(governor.CallerStorageBytes > 0L);
        Assert.True(result.TryGet(PdfName.FromAscii("Payload"), out QpdfObject escaped)); Assert.Same(payload, escaped);
        Assert.Equal(8 * 1024 * 1024, Assert.IsType<PdfStream>(escaped).RawMemory.Length); GC.KeepAlive(result);
    }
}
