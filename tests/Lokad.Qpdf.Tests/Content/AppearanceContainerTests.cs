using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceContainerTests
{
    private static PdfDictionary Dict(params (string Key, QpdfObject Value)[] pairs)
        => new(pairs.ToDictionary(p => PdfName.FromAscii(p.Key), p => p.Value));

    private static DocumentEngine Engine()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); PdfDictionary trailer = Dict();
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
    [InlineData("generation-noop")]
    [InlineData("generation-direct")]
    [InlineData("flatten-contents")]
    [InlineData("flatten-annotations")]
    public void LargeArraysBorrowOrRefuseBeforeMaterialization(string mode)
    {
        using DocumentEngine engine = Engine(); ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 1, copierMemory);
        PdfDictionary annot = Dict(("Subtype", PdfName.FromAscii(mode == "generation-direct" ? "Widget" : "Text")),
            ("FT", PdfName.FromAscii("Btn")), ("V", PdfName.FromAscii("Off")));
        QpdfObject[] items = Enumerable.Repeat<QpdfObject>(new PdfReference(3, 0), 262144).ToArray();
        if (mode == "generation-direct") { items[0] = annot; }
        PdfArray large = new(items);
        PdfDictionary page = Dict(("Annots", mode == "flatten-contents" ? new PdfArray([new PdfReference(3, 0)]) : large),
            ("Contents", mode == "flatten-contents" ? new PdfArray(Enumerable.Repeat<QpdfObject>(new PdfReference(4, 0), 262144).ToArray()) : PdfNull.Instance));
        copier.AddIndirectObjectWithId(page, 1); copier.AddIndirectObjectWithId(annot, 3);
        copier.AddIndirectObjectWithId(PdfStream.FromOwnedData(Dict(), "q Q"u8.ToArray()), 4);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192 }, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor) { SuppressWarningStorage = true }; long allocated = GC.GetAllocatedBytesForCurrentThread();
        void Run()
        {
            if (mode.StartsWith("generation", StringComparison.Ordinal))
            { GenerateAppearances.Apply([(copier, 1)], new Dictionary<GraphCopier, CopiedForm?>(), QpdfLimits.Default, governor, diagnostics, CancellationToken.None, new HashSet<int> { 3 }, true, 5); }
            else { FlattenAnnotations.Apply([(copier, 1)], "all", false, false, null, QpdfLimits.Default, governor, diagnostics, CancellationToken.None, 5); }
        }
        if (mode == "generation-noop") { Run(); } else { Assert.Throws<QpdfLimitException>(Run); }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 131072L);
        Assert.Same(page, copier.CopiedObjects[1]); Assert.Same(annot, copier.CopiedObjects[3]); Assert.Equal(3, copier.CopiedObjects.Count);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndirectWidgetsKeepOriginalAnnotationStorage(bool indirectArray)
    {
        using DocumentEngine engine = Engine(); ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 1, copierMemory);
        PdfArray annots = new([new PdfReference(3, 0)]);
        PdfDictionary page = Dict(("Annots", indirectArray ? new PdfReference(2, 0) : annots));
        PdfDictionary widget = Dict(("Subtype", PdfName.FromAscii("Widget")), ("FT", PdfName.FromAscii("Btn")), ("V", PdfName.FromAscii("Off")));
        copier.AddIndirectObjectWithId(page, 1); copier.AddIndirectObjectWithId(annots, 2); copier.AddIndirectObjectWithId(widget, 3);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        GenerateAppearances.Apply([(copier, 1)], new Dictionary<GraphCopier, CopiedForm?>(), QpdfLimits.Default,
            governor, new(governor), CancellationToken.None, new HashSet<int> { 3 }, false, 4);
        Assert.Same(page, copier.CopiedObjects[1]); Assert.Same(annots, copier.CopiedObjects[2]); Assert.NotSame(widget, copier.CopiedObjects[3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectReplacementSurvivesItsConstructionOwner(bool indirectArray)
    {
        PdfArray escaped; PdfDictionary skipped = Dict(("Subtype", PdfName.FromAscii("Widget")), ("FT", PdfName.FromAscii("Btn")),
            ("Ff", new PdfInteger("32768", 32768)), ("V", PdfName.FromAscii("Off")));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None) { OwnsDocumentCounters = true };
        using (DocumentEngine engine = Engine())
        {
            ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
            using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
            GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 1, copierMemory);
            PdfDictionary widget = Dict(("Subtype", PdfName.FromAscii("Widget")), ("FT", PdfName.FromAscii("Btn")), ("V", PdfName.FromAscii("Off")));
            PdfArray original = new([skipped, widget]); PdfDictionary page = Dict(("Annots", indirectArray ? new PdfReference(2, 0) : original));
            copier.AddIndirectObjectWithId(page, 1); copier.AddIndirectObjectWithId(original, 2);
            GenerateAppearances.Apply([(copier, 1)], new Dictionary<GraphCopier, CopiedForm?>(), QpdfLimits.Default,
                governor, new(governor), CancellationToken.None, new HashSet<int>(), true, 3);
            if (indirectArray) { escaped = Assert.IsType<PdfArray>(copier.CopiedObjects[2]); }
            else
            {
                Assert.True(Assert.IsType<PdfDictionary>(copier.CopiedObjects[1]).TryGet(PdfName.FromAscii("Annots"), out QpdfObject value));
                escaped = Assert.IsType<PdfArray>(value);
            }
            Assert.NotSame(original, escaped); Assert.Same(skipped, escaped[0]);
            governor.AcceptDocument(); governor.CompletePrivateStorage();
        }
        Assert.True(governor.CallerStorageBytes > 0L); Assert.Equal(2, escaped.Count);
        Assert.True(Assert.IsType<PdfDictionary>(escaped[1]).TryGet(PdfName.FromAscii("AS"), out QpdfObject state));
        Assert.Equal(PdfName.FromAscii("Off"), state); GC.KeepAlive(escaped);
    }
}
