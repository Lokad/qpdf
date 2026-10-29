using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayFormStorageTests
{
    private static PdfDictionary Dict(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));

    private static DocumentEngine Engine(IReadOnlyDictionary<int, QpdfObject> objects, PdfDictionary form)
    {
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary trailer = Dict(("Root", Dict(("AcroForm", form))));
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
                construction, new(construction), QpdfRecoveryMode.Strict, 32L, valuesEscape: true, hostInput: false, snapshotBorrow: null);
            fixtureStorage.Retain();
        }
        catch { fixtureTable.Dispose(); throw; }
        DocumentEngine engine = fixtureEngine;
        engine.SetInspectionObjects(objects); return engine;
    }

    [Fact]
    public void RepeatedFieldsNeedOnlyBoundedTraversalStorage()
    {
        PdfArray fields = new(Enumerable.Repeat<QpdfObject>(new PdfReference(2, 0), 262144).ToArray());
        using DocumentEngine engine = Engine(new Dictionary<int, QpdfObject> { [2] = Dict() }, Dict(("Fields", fields)));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        using (ParserMemoryScope planning = ParserMemoryScope.Create(governor))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            AcroFormCopy.OverlaySourceForm? form = AcroFormCopy.TryReadOverlaySourceForm(engine, QpdfLimits.Default, governor, planning, CancellationToken.None);
            Assert.NotNull(form); Assert.Equal(2, Assert.Single(form.ReachedObjects));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 65536L);
            Assert.True(planning.ReservedBytes > 72L); Assert.False(form.FieldsNotArray); Assert.False(form.HasDirectNodes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void DistinctFieldsRefuseBeforeLargeGrowthAndRefundTheCandidate()
    {
        PdfArray fields = new(Enumerable.Range(2, 4096).Select(i => (QpdfObject)new PdfReference(i, 0)).ToArray());
        IReadOnlyDictionary<int, QpdfObject> objects = Enumerable.Range(2, 4096).ToDictionary(i => i, _ => (QpdfObject)Dict());
        using DocumentEngine engine = Engine(objects, Dict(("Fields", fields)));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        using ParserMemoryScope planning = ParserMemoryScope.Create(governor); planning.Reserve(17L);
        long baseline = governor.Snapshot().LiveMemoryBytes, before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => AcroFormCopy.TryReadOverlaySourceForm(engine, QpdfLimits.Default, governor, planning, CancellationToken.None));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 65536L);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(17L, planning.ReservedBytes);
    }

    private sealed class CancelingObjects(Action cancel) : Dictionary<int, QpdfObject>, IReadOnlyDictionary<int, QpdfObject>
    {
        bool IReadOnlyDictionary<int, QpdfObject>.TryGetValue(int key, [MaybeNullWhen(false)] out QpdfObject value)
        { if (key == 2) { cancel(); } return TryGetValue(key, out value); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringResolutionRefundsTraversalAndRetainedCandidate(bool caller)
    {
        using CancellationTokenSource cancellation = new();
        CancelingObjects objects = new(cancellation.Cancel) { [2] = Dict() };
        using DocumentEngine engine = Engine(objects, Dict(("Fields", new PdfArray([new PdfReference(2, 0)]))));
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope planning = ParserMemoryScope.Create(governor); planning.Reserve(17L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.ThrowsAny<OperationCanceledException>(() => AcroFormCopy.TryReadOverlaySourceForm(engine, QpdfLimits.Default,
            governor, planning, caller ? cancellation.Token : CancellationToken.None));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(17L, planning.ReservedBytes);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(16, 4)]
    public void TraversalPreservesCycleBoundsAndDirectNodeReporting(int depth, int expected)
    {
        Dictionary<int, QpdfObject> objects = new()
        {
            [2] = Dict(("Kids", new PdfArray([new PdfReference(3, 0)]))),
            [3] = Dict(("Kids", new PdfArray([new PdfReference(5, 0)]))),
            [5] = Dict(("Kids", new PdfArray([new PdfReference(2, 0)]))),
            [4] = Dict(("Kids", new PdfInteger("1", 1)))
        };
        PdfDictionary source = Dict(("Fields", new PdfArray([new PdfReference(2, 0), new PdfReference(4, 0), Dict()])));
        using DocumentEngine engine = Engine(objects, source);
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = depth };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor);
        AcroFormCopy.OverlaySourceForm? form = AcroFormCopy.TryReadOverlaySourceForm(engine, limits, governor, owner, CancellationToken.None);
        Assert.NotNull(form); Assert.True(form.HasDirectNodes); Assert.False(form.FieldsNotArray);
        Assert.Equal(expected, form.ReachedObjects.Count);
    }

    [Fact]
    public void MalformedFieldsStillReportTheirShape()
    {
        using DocumentEngine engine = Engine(new Dictionary<int, QpdfObject>(), Dict(("Fields", new PdfInteger("7", 7))));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope planning = ParserMemoryScope.Create(governor);
        AcroFormCopy.OverlaySourceForm? form = AcroFormCopy.TryReadOverlaySourceForm(engine, QpdfLimits.Default, governor, planning, CancellationToken.None);
        Assert.NotNull(form); Assert.True(form.HasFields); Assert.True(form.FieldsNotArray); Assert.Empty(form.ReachedObjects);
    }

    private static PdfDictionary LargeField(int count)
        => new(Enumerable.Range(0, count).ToDictionary(i => PdfName.FromAscii("Key" + i), _ => (QpdfObject)new PdfInteger("1", 1)));

    [Theory]
    [InlineData("none")]
    [InlineData("da")]
    [InlineData("q")]
    [InlineData("dr")]
    [InlineData("parent")]
    [InlineData("direct-kid")]
    [InlineData("indirect-kid")]
    [InlineData("kids-array")]
    [InlineData("resource-union")]
    public void FixupsBorrowUnchangedHeadersAndRefuseLargeEditsBeforeAllocation(string edit)
    {
        PdfDictionary large = LargeField(16384);
        PdfDictionary field = edit switch
        {
            "direct-kid" => Dict(("Kids", new PdfArray([large]))),
            "indirect-kid" => Dict(("Kids", new PdfArray([new PdfReference(11, 0)]))),
            "kids-array" => Dict(("Kids", new PdfArray(Enumerable.Repeat<QpdfObject>(Dict(), 16384).ToArray()))),
            "dr" or "parent" => new(large.Keys.ToDictionary(key => key, key => large.TryGet(key, out QpdfObject value) ? value : PdfNull.Instance)
                .Append(new KeyValuePair<PdfName, QpdfObject>(PdfName.FromAscii(edit == "dr" ? "DR" : "Parent"), Dict())).ToDictionary(pair => pair.Key, pair => pair.Value)),
            _ => large
        };
        using DocumentEngine engine = Engine(new Dictionary<int, QpdfObject>(), Dict());
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 10, copierMemory); copier.AddIndirectObjectWithId(field, 10);
        if (edit == "indirect-kid") { copier.AddIndirectObjectWithId(large, 11); }
        CopiedForm form = new([new PdfReference(10, 0)], true, false, false, edit == "resource-union" ? large : null, false, [], new Dictionary<PdfName, QpdfObject>());
        PdfByteString appearance = new("/Helv 12 Tf"u8.ToArray(), false);
        AcroFormCopy.OverlayFieldImport import = new(copier, form, appearance, edit == "q" ? 1 : 0);
        Dictionary<int, QpdfObject> pool = new(copier.CopiedObjects); int allocator = 20;
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor) { SuppressWarningStorage = true };
        Action fix = () => AcroFormCopy.ApplyOverlayImports(form, [import], edit == "da" ? null : appearance, 0, copier, pool,
            ref allocator, QpdfLimits.Default, governor, diagnostics, CancellationToken.None);
        long before = GC.GetAllocatedBytesForCurrentThread();
        if (edit == "none") { fix(); }
        else { Assert.Throws<QpdfLimitException>(fix); }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 131072L);
        Assert.Same(field, copier.CopiedObjects[10]);
        if (edit == "indirect-kid") { Assert.Same(large, copier.CopiedObjects[11]); }
        governor.CompletePrivateStorage(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void SuccessfulFixupsKeepBorrowedDefaultsAndPayloadsAlive()
    {
        PdfStream payload = PdfStream.FromOwnedData(Dict(), new byte[8 * 1024 * 1024]);
        PdfByteString appearance = new("/H#65lv 12 Tf"u8.ToArray(), false);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None) { OwnsDocumentCounters = true };
        PdfDictionary escaped;
        using (DocumentEngine engine = Engine(new Dictionary<int, QpdfObject>(), Dict()))
        {
            ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
            using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
            GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 10, copierMemory);
            PdfDictionary source = Dict(("Payload", payload)); copier.AddIndirectObjectWithId(source, 10);
            CopiedForm form = new([new PdfReference(10, 0)], true, false, false, null, false, [], new Dictionary<PdfName, QpdfObject>());
            AcroFormCopy.OverlayFieldImport import = new(copier, form, appearance, 1);
            int allocator = 20; Dictionary<int, QpdfObject> pool = new(copier.CopiedObjects);
            long before = GC.GetAllocatedBytesForCurrentThread();
            AcroFormCopy.ApplyOverlayImports(form, [import], null, 0, copier, pool, ref allocator,
                QpdfLimits.Default, governor, new(governor), CancellationToken.None);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 131072L);
            escaped = Assert.IsType<PdfDictionary>(copier.CopiedObjects[10]);
        }
        governor.AcceptDocument(); governor.CompletePrivateStorage(); Assert.True(governor.CallerStorageBytes > 0L);
        Assert.True(escaped.TryGet(PdfName.FromAscii("DA"), out QpdfObject da)); Assert.Same(appearance, da);
        Assert.True(escaped.TryGet(PdfName.FromAscii("Payload"), out QpdfObject stored)); Assert.Same(payload, stored);
        Assert.Equal(8 * 1024 * 1024, Assert.IsType<PdfStream>(stored).RawMemory.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectKidRepairRebuildsOnlyItsChangedArrayAndPreservesOtherEntries(bool indirectArray)
    {
        PdfDictionary kid = Dict(); PdfInteger scalar = new("7", 7);
        PdfArray kids = new([PdfNull.Instance, kid, scalar]);
        using DocumentEngine engine = Engine(new Dictionary<int, QpdfObject>(), Dict());
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 10, copierMemory);
        copier.AddIndirectObjectWithId(Dict(("Kids", indirectArray ? new PdfReference(11, 0) : kids)), 10);
        if (indirectArray) { copier.AddIndirectObjectWithId(kids, 11); }
        CopiedForm form = new([new PdfReference(10, 0)], true, false, false, null, false, [], new Dictionary<PdfName, QpdfObject>());
        AcroFormCopy.OverlayFieldImport import = new(copier, form, null, null);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        int allocator = 20; Dictionary<int, QpdfObject> pool = new(copier.CopiedObjects);
        AcroFormCopy.ApplyOverlayImports(form, [import], null, null, copier, pool, ref allocator,
            QpdfLimits.Default, governor, diagnostics, CancellationToken.None);
        PdfDictionary output = Assert.IsType<PdfDictionary>(copier.CopiedObjects[10]);
        Assert.True(output.TryGet(PdfName.FromAscii("Kids"), out QpdfObject raw)); PdfArray repaired = Assert.IsType<PdfArray>(raw);
        Assert.NotSame(kids, repaired); Assert.Same(PdfNull.Instance, repaired[0]); Assert.Same(scalar, repaired[2]);
        PdfDictionary fixedKid = Assert.IsType<PdfDictionary>(repaired[1]); Assert.NotSame(kid, fixedKid);
        Assert.True(fixedKid.TryGet(PdfName.FromAscii("Parent"), out QpdfObject parent)); Assert.Equal(10, Assert.IsType<PdfReference>(parent).ObjectNumber);
        Assert.False(kid.TryGet(PdfName.FromAscii("Parent"), out _));
        if (indirectArray) { Assert.Same(kids, copier.CopiedObjects[11]); }
        Assert.Contains(diagnostics.Entries, d => d.Message.Contains("missing its parent", StringComparison.Ordinal));
        governor.CompletePrivateStorage();
    }

    private sealed class CancelingBudget(IMemoryBudget inner, Action cancel) : IMemoryBudget
    {
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            if (!inner.TryReserve(bytes, out IMemoryReservation? admitted)) { reservation = null; return false; }
            reservation = new Reservation(admitted, cancel); return true;
        }
        private sealed class Reservation(IMemoryReservation inner, Action cancel) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public void Dispose() => inner.Dispose();
            public bool TryResize(long bytes)
            {
                bool growth = bytes > inner.Bytes, accepted = inner.TryResize(bytes);
                if (growth && accepted) { cancel(); } return accepted;
            }
        }
    }

    private sealed class MemoryHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringHeaderGrowthPreservesSourceAndPriorAdmission(bool caller)
    {
        using DocumentEngine engine = Engine(new Dictionary<int, QpdfObject>(), Dict());
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 10, copierMemory);
        PdfDictionary field = LargeField(16384); copier.AddIndirectObjectWithId(field, 10);
        CopiedForm form = new([new PdfReference(10, 0)], true, false, false, null, false, [], new Dictionary<PdfName, QpdfObject>());
        AcroFormCopy.OverlayFieldImport import = new(copier, form, new("/Helv 12 Tf"u8.ToArray(), false), 0);
        RecordingMemoryBudget root = new(16L * 1024 * 1024);
        using (IMemoryScope owner = root.CreateScope())
        using (CancellationTokenSource cancellation = new())
        using (InMemoryQpdfHost storage = new())
        {
            ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token,
                new MemoryHost(storage, new CancelingBudget(owner, cancellation.Cancel)));
            using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(31L);
            long baseline = governor.Snapshot().LiveMemoryBytes; int allocator = 20;
            Dictionary<int, QpdfObject> pool = new(copier.CopiedObjects);
            Assert.ThrowsAny<OperationCanceledException>(() => AcroFormCopy.ApplyOverlayImports(form, [import], null, 0, copier, pool,
                ref allocator, QpdfLimits.Default, governor, new(governor), caller ? cancellation.Token : CancellationToken.None));
            Assert.True(root.GrowthCalls >= 2); Assert.True(cancellation.IsCancellationRequested);
            Assert.Same(field, copier.CopiedObjects[10]); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    internal static byte[] Fixture() => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /DA (/Helv 12 Tf) /Q 1 /DR << /Font << /Helv 8 0 R >> >> >> >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 50 50] /Annots [5 0 R] >>", null),
        (4, "null", null),
        (5, "<< /Subtype /Widget /Parent 6 0 R /Rect [0 0 10 10] /AP << /N 7 0 R >> >>", null),
        (6, "<< /FT /Tx /T (Field) /Kids [5 0 R] /DR << /Font << /Helv 8 0 R >> >> "
            + string.Concat(Enumerable.Range(0, 512).Select(i => "/Key" + i + " 1 ")) + ">>", null),
        (7, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length 3 >>", "q Q"u8.ToArray()),
        (8, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null)], 1);
}
