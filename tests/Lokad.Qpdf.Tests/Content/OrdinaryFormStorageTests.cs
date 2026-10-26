using System.Collections;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OrdinaryFormStorageTests
{
    private static PdfDictionary Dict(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));

    private static CopiedForm Form(IReadOnlyDictionary<PdfName, QpdfObject> extras, IReadOnlyList<QpdfObject> order)
        => new([new PdfReference(10, 0)], true, true, true, null, true, order, extras);

    private static Dictionary<PdfName, QpdfObject> Extras(int count)
        => Enumerable.Range(0, count).ToDictionary(i => PdfName.FromAscii("K" + i), _ => (QpdfObject)PdfNull.Instance);

    internal static byte[] Fixture(int count)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DA 9 0 R /DR << /Font << /Helv 8 0 R >> >> /CO ["
                + string.Concat(Enumerable.Repeat("5 0 R ", count)) + "] "
                + string.Concat(Enumerable.Range(0, count).Select(i => "/Key" + i + " 1 ")) + ">> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [5 0 R] >>", null),
            (4, "null", null), (5, "<< /Subtype /Widget /FT /Tx /T (Field) /Rect [0 0 10 10] >>", null),
            (6, "null", null), (7, "null", null),
            (8, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null), (9, "(/Helv 12 Tf)", null),
        ];
        return CheckTests.Assemble(objects, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeCatalogStorageRefusesBeforeMaterializationAndRefunds(bool calculationOrder)
    {
        CopiedForm form = Form(Extras(calculationOrder ? 0 : 262144), calculationOrder
            ? Enumerable.Repeat<QpdfObject>(new PdfReference(10, 0), 262144).ToArray() : []);
        Dictionary<int, QpdfObject> pool = new() { [10] = Dict(("Subtype", PdfName.FromAscii("Widget"))) };
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(31L);
        long baseline = governor.Snapshot().LiveMemoryBytes, allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => AcroFormCopy.BuildCatalogForm(form, pool, [10], false, QpdfLimits.Default, governor, CancellationToken.None));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 131072L);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(31L, prior.ReservedBytes);
        Assert.Equal(calculationOrder ? 262144 : 0, form.CalculationOrder.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CombinationBorrowsOnePartAndAdmitsMultipleParts(bool calculationOrder)
    {
        CopiedForm large = Form(Extras(calculationOrder ? 0 : 16384), calculationOrder
            ? Enumerable.Repeat<QpdfObject>(new PdfReference(10, 0), 16384).ToArray() : []);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor);
        Assert.Same(large, AcroFormCopy.Combine([null, large], governor, owner, CancellationToken.None));
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => AcroFormCopy.Combine([large, large], governor, owner, CancellationToken.None));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 131072L);
        Assert.Equal(0L, owner.ReservedBytes); Assert.Equal(64L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("fields")]
    [InlineData("widgets")]
    [InlineData("references")]
    public void TraversalCapacityRefusesAndRefunds(string kind)
    {
        PdfReference[] refs = Enumerable.Range(10, 16384).Select(i => new PdfReference(i, 0)).ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor);
        Dictionary<int, QpdfObject> pool = new() { [10] = Dict(("Kids", new PdfArray(refs.Skip(1).ToArray()))) };
        if (kind == "widgets")
        {
            foreach (PdfReference link in refs.Skip(1)) { pool[link.ObjectNumber] = Dict(("Subtype", PdfName.FromAscii("Widget"))); }
        }
        PdfArray references = new(refs);
        Action run = kind == "references"
            ? () => AcroFormCopy.CollectRefNumbers(references, governor, owner, CancellationToken.None)
            : () => AcroFormCopy.CollectKeptClosure(pool, kind == "fields" ? refs : [refs[0]], [], false, QpdfLimits.Default, governor, owner, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(run);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 131072L);
        Assert.Equal(0L, owner.ReservedBytes); Assert.Equal(64L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class CancelingOrder(Action cancel, int cancelAt) : IReadOnlyList<QpdfObject>
    {
        public int Count => 128;
        public QpdfObject this[int index] => new PdfReference(10, 0);
        public IEnumerator<QpdfObject> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) { if (i == cancelAt) { cancel(); } yield return this[i]; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourcePreparationAdmitsMetadataAndRefundsAfterRefusal(bool manyFields)
    {
        Dictionary<PdfName, QpdfObject> entries = manyFields ? []
            : Enumerable.Range(0, 16384).ToDictionary(i => PdfName.FromAscii("K" + i), _ => (QpdfObject)new PdfInteger("1", 1));
        entries.Add(PdfName.FromAscii("Fields"), new PdfArray(Enumerable.Repeat<QpdfObject>(new PdfReference(10, 0), manyFields ? 16384 : 1).ToArray()));
        PdfDictionary trailer = Dict(("Root", Dict(("AcroForm", new PdfDictionary(entries)))));
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope fixtureStorage = ParserMemoryScope.Create(construction);
        Dictionary<int, XrefEntry> fixtureEntries = ParserContainers.CreateDictionary<int, XrefEntry>(fixtureStorage);
        using ParserMemoryScope fixtureValues = ParserMemoryScope.Create(construction);
        List<PdfDictionary> fixtureTrailers = ParserContainers.CreateList<PdfDictionary>(fixtureStorage, 1, IntPtr.Size);
        fixtureTrailers.Add(trailer);
        XRefTable fixtureTable = XRefTable.CreateOwned(0L, fixtureEntries, trailer, fixtureTrailers,
            construction, fixtureStorage, fixtureValues, false);
        DocumentEngine fixtureEngine;
        try
        {
            fixtureStorage.Reserve(32L);
            fixtureEngine = DocumentEngine.CreateOwned(ReadOnlyMemory<byte>.Empty, fixtureTable, QpdfLimits.Default,
                construction, new(construction), QpdfRecoveryMode.Strict, 32L);
            fixtureStorage.Retain();
        }
        catch { fixtureTable.Dispose(); throw; }
        using DocumentEngine engine = fixtureEngine;
        engine.SetInspectionObjects(new Dictionary<int, QpdfObject> { [10] = Dict() });
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            GraphCopier copier = GraphCopier.CreateOwned(engine, governor, QpdfLimits.Default, 20, owner);
            long prior = owner.ReservedBytes, allocated = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<QpdfLimitException>(() => AcroFormCopy.TryCopySourceForm(engine, copier, [], false, QpdfLimits.Default, governor, owner, CancellationToken.None));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 131072L);
            Assert.True(owner.ReservedBytes >= prior); // Accepted copied objects still belong to the enclosing copier.
        }
        governor.CompletePrivateStorage(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, 64)]
    [InlineData(true, 64)]
    [InlineData(false, 200)]
    [InlineData(true, 200)]
    public void CancellationInterruptsCalculationOrderAndRefunds(bool caller, int cancelAt)
    {
        using CancellationTokenSource cancellation = new();
        int visits = 0;
        CancelingOrder order = new(() => { if (++visits == (cancelAt < 128 ? 1 : 2)) { cancellation.Cancel(); } }, cancelAt % 128);
        CopiedForm form = Form(Extras(0), order);
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        Dictionary<int, QpdfObject> pool = new() { [10] = Dict(("Subtype", PdfName.FromAscii("Widget"))) };
        Assert.ThrowsAny<OperationCanceledException>(() => AcroFormCopy.BuildCatalogForm(form, pool, [10], false, QpdfLimits.Default, governor,
            caller ? cancellation.Token : CancellationToken.None));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ClosurePreservesCyclesDepthBoundsMalformedKidsAndRegistrationOrder()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor);
        Dictionary<int, QpdfObject> pool = new()
        {
            [10] = Dict(("Kids", new PdfArray([new PdfReference(11, 0)]))),
            [11] = Dict(("Kids", new PdfArray([new PdfReference(10, 0), new PdfReference(12, 0)]))),
            [12] = Dict(("Subtype", PdfName.FromAscii("Widget"))),
            [13] = Dict(("Kids", new PdfInteger("1", 1))),
        };
        PdfReference[] fields = [new(13, 0), new(10, 0), new(10, 0)];
        var selected = AcroFormCopy.CollectKeptClosure(pool, fields, [12], true, QpdfLimits.Default, governor, owner, CancellationToken.None);
        Assert.Equal(new[] { 10, 10 }, selected.Kept.Select(link => link.ObjectNumber));
        Assert.Equal(new[] { 10, 11, 12 }, selected.Closure.Order());
        var whole = AcroFormCopy.CollectKeptClosure(pool, fields, [], false, QpdfLimits.Default, governor, owner, CancellationToken.None);
        Assert.Equal(13, Assert.Single(whole.Kept).ObjectNumber);
        var shallow = AcroFormCopy.CollectKeptClosure(pool, fields, [12], true, QpdfLimits.Default with { MaxNestingDepth = 1 }, governor, owner, CancellationToken.None);
        Assert.Equal(2, shallow.Kept.Count); // Widgets are recognized before their children are depth-limited.
        Assert.Throws<QpdfLimitException>(() => AcroFormCopy.CollectKeptClosure(pool, [fields[1]], [12], true,
            QpdfLimits.Default with { MaxContainerSize = 2 }, governor, owner, CancellationToken.None));
    }

    [Fact]
    public void CatalogArraysAndBorrowedPayloadsEscapeTemporaryMetadata()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None) { OwnsDocumentCounters = true };
        PdfByteString payload = new(new byte[] { 0, 255, 128 }, true);
        CopiedForm form = Form(new Dictionary<PdfName, QpdfObject> { [new PdfName(new byte[] { 0, 255 })] = payload },
            [new PdfReference(999, 0), new PdfReference(10, 0), new PdfInteger("7", 7), new PdfReference(10, 0)]);
        PdfDictionary escaped;
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            CopiedForm combined = Assert.IsType<CopiedForm>(AcroFormCopy.Combine([form, form], governor, owner, CancellationToken.None));
            escaped = Assert.IsType<PdfDictionary>(AcroFormCopy.BuildCatalogForm(combined,
                new Dictionary<int, QpdfObject> { [10] = Dict(("Subtype", PdfName.FromAscii("Widget"))) }, [10], true, QpdfLimits.Default, governor, CancellationToken.None));
        }
        governor.AcceptDocument(); governor.CompletePrivateStorage(); Assert.True(governor.CallerStorageBytes > 0L);
        Assert.True(escaped.TryGet(PdfName.FromAscii("Fields"), out QpdfObject fields)); Assert.Equal(2, Assert.IsType<PdfArray>(fields).Count);
        Assert.True(escaped.TryGet(PdfName.FromAscii("CO"), out QpdfObject order)); Assert.Equal(6, Assert.IsType<PdfArray>(order).Count);
        Assert.True(escaped.TryGet(new PdfName(new byte[] { 0, 255 }), out QpdfObject extra)); Assert.Same(payload, extra);
        Assert.Equal(new byte[] { 0, 255, 128 }, Assert.IsType<PdfByteString>(extra).Value);
    }
}
