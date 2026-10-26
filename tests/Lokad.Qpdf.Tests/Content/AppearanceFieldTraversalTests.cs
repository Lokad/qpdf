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
                governor, new(governor), QpdfRecoveryMode.Strict, 32L);
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
        int next = GenerateAppearances.Apply([(copier, 1)], new Dictionary<GraphCopier, CopiedForm?>(), QpdfLimits.Default,
            governor, diagnostics, CancellationToken.None, new HashSet<int> { 3 }, kind == "direct-kids", 5);
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
        Assert.Throws<QpdfLimitException>(() => GenerateAppearances.Apply([(copier, 1)], new Dictionary<GraphCopier, CopiedForm?>(),
            QpdfLimits.Default, governor, new(governor), CancellationToken.None, new HashSet<int> { 3 }, false, 4));
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
            Assert.ThrowsAny<OperationCanceledException>(() => GenerateAppearances.Apply([(copier, 1)], new Dictionary<GraphCopier, CopiedForm?>(),
                QpdfLimits.Default, governor, new(governor), governorToken ? CancellationToken.None : cancellation.Token, new HashSet<int> { 3 }, false, 4));
            Assert.True(cancellation.IsCancellationRequested); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 131072L);
            Assert.Same(page, copier.CopiedObjects[1]); Assert.Same(widget, copier.CopiedObjects[3]);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    private static QpdfObject Get(PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
}
