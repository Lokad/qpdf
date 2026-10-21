using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceFieldTraversalTests
{
    private static PdfDictionary Dict(params (string Key, QpdfObject Value)[] pairs)
        => new(pairs.ToDictionary(p => PdfName.FromAscii(p.Key), p => p.Value));

    private static DocumentEngine Engine()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary trailer = Dict();
        return new(ReadOnlyMemory<byte>.Empty, new XRefTable(0L, new Dictionary<int, XrefEntry>(), trailer, [trailer]),
            QpdfLimits.Default, governor, new(governor), QpdfRecoveryMode.Strict);
    }

    [Theory]
    [InlineData("kids")]
    [InlineData("direct-kids")]
    [InlineData("parent")]
    [InlineData("broken")]
    [InlineData("parent-cycle")]
    [InlineData("kids-cycle")]
    [InlineData("value-cycle")]
    public void ParentFallbackAndDamagedReferencesRemainBounded(string kind)
    {
        using DocumentEngine engine = Engine(); ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        GraphCopier copier = new(engine, construction, QpdfLimits.Default, 1);
        PdfDictionary widget = kind is "kids" or "direct-kids" or "kids-cycle" ? Dict(("Subtype", PdfName.FromAscii("Widget")))
            : Dict(("Subtype", PdfName.FromAscii("Widget")), ("Parent", new PdfReference(kind == "broken" ? 99 : 2, 0)));
        QpdfObject child = kind == "direct-kids" ? widget : new PdfReference(3, 0);
        PdfDictionary field = Dict(("FT", PdfName.FromAscii("Btn")), ("V", kind == "value-cycle" ? new PdfReference(4, 0) : PdfName.FromAscii("Off")),
            ("Kids", new PdfArray(kind == "parent" ? [] : [child])),
            ("Parent", kind is "parent-cycle" or "kids-cycle" ? new PdfReference(3, 0) : PdfNull.Instance));
        PdfDictionary page = Dict(("Annots", new PdfArray([child])));
        copier.AddIndirectObjectWithId(page, 1); copier.AddIndirectObjectWithId(field, 2); copier.AddIndirectObjectWithId(widget, 3);
        copier.AddIndirectObjectWithId(new PdfReference(4, 0), 4);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        int next = GenerateAppearances.Apply([(copier, 1)], new Dictionary<GraphCopier, PdfDictionary?>(), QpdfLimits.Default,
            governor, diagnostics, CancellationToken.None, new HashSet<int> { 3 }, kind == "direct-kids", 5);
        Assert.Equal(5, next);
        if (kind is "broken" or "value-cycle") { Assert.Same(widget, copier.CopiedObjects[3]); }
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
        GraphCopier copier = new(engine, construction, QpdfLimits.Default, 1);
        PdfDictionary widget = Dict(("Subtype", PdfName.FromAscii("Widget")));
        PdfArray kids = new(Enumerable.Range(3, 262144).Select(i => (QpdfObject)new PdfReference(i, 0)).ToArray());
        PdfDictionary field = Dict(("FT", PdfName.FromAscii("Btn")), ("V", PdfName.FromAscii("Off")), ("Kids", kids));
        PdfDictionary page = Dict(("Annots", new PdfArray([new PdfReference(3, 0)])));
        copier.AddIndirectObjectWithId(page, 1); copier.AddIndirectObjectWithId(field, 2); copier.AddIndirectObjectWithId(widget, 3);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => GenerateAppearances.Apply([(copier, 1)], new Dictionary<GraphCopier, PdfDictionary?>(),
            QpdfLimits.Default, governor, new(governor), CancellationToken.None, new HashSet<int> { 3 }, false, 4));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 131072L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Same(page, copier.CopiedObjects[1]); Assert.Same(widget, copier.CopiedObjects[3]);
    }

    private static QpdfObject Get(PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
}
