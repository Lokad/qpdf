using System.Globalization;
using System.Text;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class FlattenResourceStorageTests
{
    private static DocumentEngine Engine()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary trailer = new(new Dictionary<PdfName, QpdfObject>());
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
                governor, new(governor), QpdfRecoveryMode.Strict, 32L, valuesEscape: true, hostInput: false, snapshotBorrow: null);
            fixtureStorage.Retain();
        }
        catch { fixtureTable.Dispose(); throw; }
        return fixtureEngine;
    }

    private static PdfDictionary Dict(params (string Key, QpdfObject Value)[] pairs)
        => new(pairs.ToDictionary(pair => PdfName.FromAscii(pair.Key), pair => pair.Value));

    private static PdfDictionary Page(GraphCopier copier, int annotations, int resources, bool damaged)
    {
        PdfArray box = new(new QpdfObject[] { new PdfInteger("0", 0), new PdfInteger("0", 0), new PdfInteger("20", 20), new PdfInteger("20", 20) });
        PdfStream appearance = PdfStream.FromOwnedData(Dict(("BBox", box)), "q Q"u8.ToArray());
        copier.AddIndirectObjectWithId(appearance, 2);
        PdfDictionary annot = Dict(("Subtype", PdfName.FromAscii("Text")), ("Rect", box), ("AP", Dict(("N", new PdfReference(2, 0)))));
        PdfDictionary xobjects = new(Enumerable.Range(0, resources).ToDictionary(
            i => PdfName.FromAscii("Existing" + i.ToString(CultureInfo.InvariantCulture)), _ => (QpdfObject)new PdfReference(2, 0)));
        PdfDictionary page = Dict(("Resources", Dict(("XObject", xobjects))),
            ("Annots", new PdfArray(Enumerable.Repeat<QpdfObject>(annot, annotations).ToArray())),
            ("Contents", damaged ? new PdfInteger("42", 42) : PdfNull.Instance));
        copier.AddIndirectObjectWithId(page, 1); return page;
    }

    [Fact]
    public void LargeResourceRefusalPrecedesMaterializationAndPreservesThePage()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 8192L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using DocumentEngine engine = Engine(); using ParserMemoryScope copierMemory = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(engine, governor, limits, 1, copierMemory);
        PdfDictionary page = Page(copier, 1, 16384, false);
        long baseline = governor.Snapshot().LiveMemoryBytes, allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => FlattenAnnotations.Apply([(copier, 1)], "all", false, false, null, limits, governor, new(governor), CancellationToken.None, 3));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 512 * 1024L);
        Assert.Same(page, copier.CopiedObjects[1]); Assert.Equal(2, copier.CopiedObjects.Count);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void DamagedContentDoesNotPromoteAppearancesOrInsertUnusedStreams()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using DocumentEngine engine = Engine(); using ParserMemoryScope copierMemory = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(engine, governor, QpdfLimits.Default, 1, copierMemory);
        PdfDictionary page = Page(copier, 1, 0, true); QpdfObject appearance = copier.CopiedObjects[2];
        DiagnosticBag diagnostics = new(governor);
        Assert.Equal(3, FlattenAnnotations.Apply([(copier, 1)], "all", false, false, null, QpdfLimits.Default, governor, diagnostics, CancellationToken.None, 3));
        Assert.Same(page, copier.CopiedObjects[1]); Assert.Same(appearance, copier.CopiedObjects[2]);
        Assert.Equal(2, copier.CopiedObjects.Count); Assert.Contains(diagnostics.Entries, d => d.Message.Contains("keeping the page as is", StringComparison.Ordinal));
    }

    [Fact]
    public void ResourceConstructionRemainsLinearAndDoesNotMutateSharedResources()
    {
        _ = Allocations(4); long smaller = Allocations(128), larger = Allocations(512);
        Assert.True(larger < smaller * 6L + 65536L, $"Resource rebuilding regressed: {smaller} versus {larger} bytes.");
    }

    private static long Allocations(int count)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None) { OwnsDocumentCounters = true };
        using DocumentEngine engine = Engine(); using ParserMemoryScope copierMemory = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(engine, governor, QpdfLimits.Default, 1, copierMemory);
        PdfDictionary original = Page(copier, count, 1, false); copier.AddIndirectObjectWithId(original, 3);
        long before = GC.GetAllocatedBytesForCurrentThread();
        FlattenAnnotations.Apply([(copier, 1)], "all", false, false, null, QpdfLimits.Default, governor, new(governor), CancellationToken.None, 4);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Same(original, copier.CopiedObjects[3]);
        PdfDictionary result = Assert.IsType<PdfDictionary>(copier.CopiedObjects[1]);
        Assert.True(result.TryGet(PdfName.FromAscii("Resources"), out QpdfObject raw));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(raw);
        Assert.True(resources.TryGet(PdfName.FromAscii("XObject"), out QpdfObject xobjects));
        Assert.Equal(count + 1, Assert.IsType<PdfDictionary>(xobjects).Count);
        governor.AcceptDocument(); governor.CompletePrivateStorage();
        Assert.True(governor.CallerStorageBytes > 0L); GC.KeepAlive(result); return allocated;
    }

    internal static byte[] Fixture(int count, bool indirectContents)
    {
        byte[] appearance = Encoding.ASCII.GetBytes(new string(' ', 65536) + "q Q");
        List<(int, string, byte[]?)> objects = Inspection.CheckTests.SinglePage("<< /Length 3 >>", "q Q"u8.ToArray());
        objects[2] = (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents " + (indirectContents ? "9" : "4") + " 0 R /Resources << /XObject << /Fxo1 5 0 R >> >> /Annots 6 0 R >>", null);
        objects.Add((5, "<< /BBox [0 0 20 20] /Length " + appearance.Length + " >>", appearance));
        objects.Add((6, "[" + string.Concat(Enumerable.Repeat("7 0 R ", count)) + "8 0 R]", null));
        objects.Add((7, "<< /Subtype /Text /Rect [10 10 30 30] /AP << /N 5 0 R >> >>", null));
        objects.Add((8, "<< /Subtype /Text /Rect [0 0 10 10] /Contents (kept) >>", null));
        if (indirectContents) { objects.Add((9, "[4 0 R]", null)); }
        return Inspection.CheckTests.Assemble(objects, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicFlatteningKeepsAnnotationsAndExistingNamesAfterSourceClosure(bool indirectContents)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(128, indirectContents), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--flatten-annotations=all", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        PdfStream escaped;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
        {
            PdfDictionary page = document.PageReferences[0].Page;
            Assert.True(page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject annots)); Assert.Single(Assert.IsType<PdfArray>(annots).Items);
            Assert.True(page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject resources));
            Assert.True(Assert.IsType<PdfDictionary>(resources).TryGet(PdfName.FromAscii("XObject"), out QpdfObject xobjects));
            PdfDictionary forms = Assert.IsType<PdfDictionary>(xobjects); Assert.Equal(129, forms.Count);
            Assert.True(forms.TryGet(PdfName.FromAscii("Fxo2"), out QpdfObject link));
            escaped = Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(link).ObjectNumber));
        }
        Assert.True(escaped.RawMemory.Length > 0L); Assert.NotNull(escaped.Dictionary);
    }
}
