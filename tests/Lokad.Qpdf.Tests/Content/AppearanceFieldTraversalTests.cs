using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceFieldTraversalTests
{
    private sealed class CancelingGrowth(IMemoryBudget inner, CancellationTokenSource cancellation) : IMemoryBudget
    {
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            bool admitted = inner.TryReserve(bytes, out IMemoryReservation? storage);
            reservation = storage is null ? null : new Reservation(storage, cancellation); return admitted;
        }
        private sealed class Reservation(IMemoryReservation inner, CancellationTokenSource cancellation) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes)
            {
                bool admitted = inner.TryResize(bytes);
                if (bytes >= 16384) { cancellation.Cancel(); }
                return admitted;
            }
            public void Dispose() => inner.Dispose();
        }
    }

    private static PdfDictionary Dict(params (string Key, QpdfObject Value)[] pairs)
        => new(pairs.ToDictionary(p => PdfName.FromAscii(p.Key), p => p.Value));

    private static Dictionary<GraphCopier, CopiedForm?> Registered(GraphCopier copier, int root)
        => new() { [copier] = new([new(root, 0)], true, false, false, null, false, [], new Dictionary<PdfName, QpdfObject>()) };

    private static DocumentEngine Engine()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary trailer = Dict();
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

    [Theory]
    [InlineData("kids")]
    [InlineData("direct-kids")]
    [InlineData("parent")]
    [InlineData("broken")]
    [InlineData("broken-without-kids")]
    [InlineData("parent-cycle")]
    [InlineData("kids-cycle")]
    [InlineData("value-cycle")]
    public void ParentFallbackAndDamagedReferencesRemainBounded(string kind)
    {
        using DocumentEngine engine = Engine(); ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 1, copierMemory);
        PdfDictionary widget = kind is "kids" or "direct-kids" or "kids-cycle" ? Dict(("Subtype", PdfName.FromAscii("Widget")))
            : Dict(("Subtype", PdfName.FromAscii("Widget")), ("Parent", new PdfReference(kind.StartsWith("broken", StringComparison.Ordinal) ? 99 : 2, 0)));
        QpdfObject child = kind == "direct-kids" ? widget : new PdfReference(3, 0);
        PdfDictionary field = Dict(("FT", PdfName.FromAscii("Btn")), ("V", kind == "value-cycle" ? new PdfReference(4, 0) : PdfName.FromAscii("Off")),
            ("Kids", new PdfArray(kind is "parent" or "broken-without-kids" ? [] : [child])),
            ("Parent", kind is "parent-cycle" or "kids-cycle" ? new PdfReference(3, 0) : PdfNull.Instance));
        PdfDictionary page = Dict(("Annots", new PdfArray([child])));
        copier.AddIndirectObjectWithId(page, 1); copier.AddIndirectObjectWithId(field, 2); copier.AddIndirectObjectWithId(widget, 3);
        copier.AddIndirectObjectWithId(new PdfReference(4, 0), 4);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        (int next, _) = GenerateAppearances.Apply([(copier, 1)], Registered(copier, 2), QpdfLimits.Default,
            governor, diagnostics, CancellationToken.None, new HashSet<int> { 3 }, kind == "direct-kids", null, 5);
        Assert.Equal(5, next);
        if (kind is "broken-without-kids" or "value-cycle") { Assert.Same(widget, copier.CopiedObjects[3]); }
        else
        {
            PdfDictionary result = kind == "direct-kids"
                ? Assert.IsType<PdfDictionary>(Assert.IsType<PdfArray>(Get(Assert.IsType<PdfDictionary>(copier.CopiedObjects[1]), "Annots"))[0])
                : Assert.IsType<PdfDictionary>(copier.CopiedObjects[3]);
            Assert.Equal(PdfName.FromAscii("Off"), Get(result, "AS"));
        }
    }

    [Fact]
    public void LargeFallbackIndexRefusesBeforeCopyingKidsAndRefunds()
    {
        using DocumentEngine engine = Engine(); ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 1, copierMemory);
        PdfDictionary widget = Dict(("Subtype", PdfName.FromAscii("Widget")));
        PdfArray kids = new(Enumerable.Range(3, 262144).Select(i => (QpdfObject)new PdfReference(i, 0)).ToArray());
        PdfDictionary field = Dict(("FT", PdfName.FromAscii("Btn")), ("V", PdfName.FromAscii("Off")), ("Kids", kids));
        PdfDictionary page = Dict(("Annots", new PdfArray([new PdfReference(3, 0)])));
        copier.AddIndirectObjectWithId(page, 1); copier.AddIndirectObjectWithId(field, 2); copier.AddIndirectObjectWithId(widget, 3);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => GenerateAppearances.Apply([(copier, 1)], Registered(copier, 2),
            QpdfLimits.Default, governor, new(governor), CancellationToken.None, new HashSet<int> { 3 }, false, null, 4));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 131072L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Same(page, copier.CopiedObjects[1]); Assert.Same(widget, copier.CopiedObjects[3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringIndexGrowthRefundsAndPreservesTheGraph(bool governorToken)
    {
        using DocumentEngine engine = Engine(); ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 1, copierMemory);
        PdfDictionary widget = Dict(("Subtype", PdfName.FromAscii("Widget")));
        PdfArray kids = new(Enumerable.Range(3, 262144).Select(i => (QpdfObject)new PdfReference(i, 0)).ToArray());
        PdfDictionary page = Dict(("Annots", new PdfArray([new PdfReference(3, 0)])));
        copier.AddIndirectObjectWithId(page, 1); copier.AddIndirectObjectWithId(Dict(("Kids", kids)), 2); copier.AddIndirectObjectWithId(widget, 3);
        RecordingMemoryBudget root = new(1048576L);
        using (IMemoryScope owner = root.CreateScope())
        using (CancellationTokenSource cancellation = new())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, new CancelingGrowth(owner, cancellation)))
        {
            ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None, host);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            Assert.ThrowsAny<OperationCanceledException>(() => GenerateAppearances.Apply([(copier, 1)], Registered(copier, 2),
                QpdfLimits.Default, governor, new(governor), governorToken ? CancellationToken.None : cancellation.Token, new HashSet<int> { 3 }, false, null, 4));
            Assert.True(cancellation.IsCancellationRequested); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 131072L);
            Assert.Same(page, copier.CopiedObjects[1]); Assert.Same(widget, copier.CopiedObjects[3]);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    [InlineData(64, false)]
    [InlineData(64, true)]
    [InlineData(120, false)]
    [InlineData(120, true)]
    public void FieldParentsUseTheConfiguredBoundaryAndRefundTraversal(int maximum, bool tooDeep)
    {
        using DocumentEngine engine = Engine();
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 1, copierMemory);
        PdfDictionary page = Dict(("Annots", new PdfArray([new PdfReference(2, 0)])));
        copier.AddIndirectObjectWithId(page, 1);
        int edges = maximum + (tooDeep ? 1 : 0);
        for (int at = 0; at <= edges; at++)
        {
            PdfDictionary node = at == edges ? Dict(("FT", PdfName.FromAscii("Btn")), ("V", PdfName.FromAscii("Off")))
                : at == 0 ? Dict(("Subtype", PdfName.FromAscii("Widget")), ("Parent", new PdfReference(3, 0)))
                : Dict(("Parent", new PdfReference(3 + at, 0)));
            copier.AddIndirectObjectWithId(node, 2 + at);
        }
        QpdfObject widget = copier.CopiedObjects[2];
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = maximum };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        void Generate() => GenerateAppearances.Apply([(copier, 1)], new Dictionary<GraphCopier, CopiedForm?>(),
            limits, governor, new(governor), CancellationToken.None, new HashSet<int> { 2 }, false, null, edges + 3);
        if (tooDeep)
        {
            QpdfLimitException failure = Assert.Throws<QpdfLimitException>(Generate);
            Assert.Equal(nameof(QpdfLimits.MaxNestingDepth), failure.LimitName);
            Assert.Equal(maximum + 1L, failure.Requested); Assert.Equal(maximum, failure.Allowed);
            Assert.Same(widget, copier.CopiedObjects[2]);
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
        else
        {
            Generate();
            Assert.Equal(PdfName.FromAscii("Off"), Get(Assert.IsType<PdfDictionary>(copier.CopiedObjects[2]), "AS"));
            Assert.True(governor.Snapshot().LiveMemoryBytes > 0L);
        }
        Assert.Same(page, copier.CopiedObjects[1]);
        governor.CompleteStorage();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void KidsOnlyTextParentsIncludeDirectBranchesInsideTheRegisteredTree()
    {
        using DocumentEngine engine = Engine(); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(engine, governor, QpdfLimits.Default, 1, copierMemory);
        PdfDictionary widget = Dict(("Subtype", PdfName.FromAscii("Widget")), ("Rect", new PdfArray([new PdfInteger("0", 0), new PdfInteger("0", 0), new PdfInteger("90", 90), new PdfInteger("40", 40)])));
        PdfDictionary parent = Dict(("V", new PdfByteString("nested"u8.ToArray(), false)), ("Kids", new PdfArray([new PdfReference(3, 0)])));
        PdfDictionary field = Dict(("FT", PdfName.FromAscii("Tx")), ("DA", new PdfByteString("/F1 9 Tf 0 g"u8.ToArray(), false)), ("Kids", new PdfArray([parent])));
        copier.AddIndirectObjectWithId(Dict(("Annots", new PdfArray([new PdfReference(3, 0)]))), 1);
        copier.AddIndirectObjectWithId(field, 2); copier.AddIndirectObjectWithId(widget, 3);
        Dictionary<GraphCopier, CopiedForm?> forms = Registered(copier, 2);
        forms[copier] = Assert.IsType<CopiedForm>(forms[copier]) with
        {
            DefaultResources = Dict(("Font", Dict(("F1", Dict(("Type", PdfName.FromAscii("Font")),
                ("Subtype", PdfName.FromAscii("Type1")), ("BaseFont", PdfName.FromAscii("Helvetica")))))))
        };
        (int next, bool complete) = GenerateAppearances.Apply([(copier, 1)], forms, QpdfLimits.Default,
            governor, new(governor), CancellationToken.None, new HashSet<int> { 3 }, false, null, 4);
        Assert.True(complete); Assert.Equal(5, next);
        PdfDictionary updated = Assert.IsType<PdfDictionary>(copier.CopiedObjects[3]);
        PdfDictionary ap = Assert.IsType<PdfDictionary>(Get(updated, "AP"));
        PdfStream appearance = Assert.IsType<PdfStream>(copier.CopiedObjects[Assert.IsType<PdfReference>(Get(ap, "N")).ObjectNumber]);
        Assert.Contains("(nested) Tj", System.Text.Encoding.ASCII.GetString(appearance.RawMemory.Span), StringComparison.Ordinal);
        Assert.Same(field, copier.CopiedObjects[2]); Assert.Same(parent, Assert.IsType<PdfArray>(Get(field, "Kids"))[0]);
        copierMemory.Dispose(); governor.CompletePrivateStorage(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisteredRootsOnlyUseExplicitParents(bool explicitParent)
    {
        using DocumentEngine engine = Engine(); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(engine, governor, QpdfLimits.Default, 1, memory);
        PdfDictionary widget = Dict(("Subtype", PdfName.FromAscii("Widget")), ("FT", PdfName.FromAscii("Btn")),
            ("Parent", explicitParent ? new PdfReference(2, 0) : PdfNull.Instance));
        copier.AddIndirectObjectWithId(Dict(("Annots", new PdfArray([new PdfReference(3, 0)]))), 1);
        copier.AddIndirectObjectWithId(Dict(("FT", PdfName.FromAscii("Btn")), ("V", PdfName.FromAscii("Off")), ("Kids", new PdfArray([new PdfReference(3, 0)]))), 2);
        copier.AddIndirectObjectWithId(widget, 3);
        CopiedForm form = new([new(2, 0), new(3, 0)], true, false, false, null, false, [], new Dictionary<PdfName, QpdfObject>());
        DiagnosticBag diagnostics = new(governor);
        (_, bool complete) = GenerateAppearances.Apply([(copier, 1)], new Dictionary<GraphCopier, CopiedForm?> { [copier] = form },
            QpdfLimits.Default, governor, diagnostics, CancellationToken.None, new HashSet<int> { 3 }, false, null, 4);
        Assert.Equal(explicitParent, complete);
        if (explicitParent) { Assert.Equal(PdfName.FromAscii("Off"), Get(Assert.IsType<PdfDictionary>(copier.CopiedObjects[3]), "AS")); Assert.Empty(diagnostics.Entries); }
        else { Assert.Same(widget, copier.CopiedObjects[3]); Assert.Single(diagnostics.Entries); }
    }

    [Fact]
    public void ARegisteredRootDoesNotIndexLargePrivateKids()
    {
        using DocumentEngine engine = Engine(); ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 1, memory);
        PdfDictionary widget = Dict(("Subtype", PdfName.FromAscii("Widget")), ("FT", PdfName.FromAscii("Btn")), ("V", PdfName.FromAscii("Off")));
        copier.AddIndirectObjectWithId(Dict(("Annots", new PdfArray([new PdfReference(2, 0)]))), 1);
        copier.AddIndirectObjectWithId(widget, 2);
        PdfDictionary unrelated = Dict(("Kids", new PdfArray(Enumerable.Range(2, 262144).Select(n => (QpdfObject)new PdfReference(n, 0)).ToArray())));
        copier.AddIndirectObjectWithId(unrelated, 3);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        (_, bool complete) = GenerateAppearances.Apply([(copier, 1)], Registered(copier, 2), limits, governor, new(governor),
            CancellationToken.None, new HashSet<int> { 2 }, false, null, 4);
        Assert.True(complete); Assert.Same(unrelated, copier.CopiedObjects[3]);
        Assert.Equal(PdfName.FromAscii("Off"), Get(Assert.IsType<PdfDictionary>(copier.CopiedObjects[2]), "AS"));
        governor.CompletePrivateStorage(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(8, false)]
    [InlineData(8, true)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    public void RegisteredKidsTraversalKeepsDepthBoundsAndRefunds(int maximum, bool tooDeep)
    {
        using DocumentEngine engine = Engine(); ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 1, memory);
        int edges = maximum + (tooDeep ? 1 : 0), widget = edges + 2;
        copier.AddIndirectObjectWithId(Dict(("Annots", new PdfArray([new PdfReference(widget, 0)]))), 1);
        for (int id = 2; id <= widget; id++)
        {
            PdfDictionary node = id == widget ? Dict(("Subtype", PdfName.FromAscii("Widget")))
                : Dict(("FT", PdfName.FromAscii("Btn")), ("V", PdfName.FromAscii("Off")), ("Kids", new PdfArray([new PdfReference(id + 1, 0)])));
            copier.AddIndirectObjectWithId(node, id);
        }
        QpdfObject original = copier.CopiedObjects[widget];
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = maximum };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        void Generate() => GenerateAppearances.Apply([(copier, 1)], Registered(copier, 2), limits, governor, new(governor),
            CancellationToken.None, new HashSet<int> { widget }, false, null, widget + 1);
        if (tooDeep)
        {
            QpdfLimitException error = Assert.Throws<QpdfLimitException>(Generate);
            Assert.Equal(nameof(QpdfLimits.MaxNestingDepth), error.LimitName); Assert.Equal(maximum + 1L, error.Requested);
            Assert.Same(original, copier.CopiedObjects[widget]);
        }
        else { Generate(); Assert.Equal(PdfName.FromAscii("Off"), Get(Assert.IsType<PdfDictionary>(copier.CopiedObjects[widget]), "AS")); }
        governor.CompletePrivateStorage(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private static QpdfObject Get(PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
}
