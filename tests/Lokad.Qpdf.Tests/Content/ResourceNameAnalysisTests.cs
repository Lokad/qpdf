using System.Reflection;
using System.Text;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ResourceNameAnalysisTests
{
    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => new PdfName(Encoding.Latin1.GetBytes(entry.Key)), entry => entry.Value));
    private static readonly Func<GraphCopier, int, PdfDictionary, QpdfLimits, ResourceGovernor, DiagnosticBag, CancellationToken, PdfDictionary> PruneCore =
        (typeof(ContentTransforms).GetMethod("PrunePage", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new InvalidOperationException("Missing page pruning."))
        .CreateDelegate<Func<GraphCopier, int, PdfDictionary, QpdfLimits, ResourceGovernor, DiagnosticBag, CancellationToken, PdfDictionary>>();
    private static PdfDictionary Prune(GraphCopier copier, int id, PdfDictionary page, QpdfLimits limits,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        ((IDictionary<int, QpdfObject>)copier.CopiedObjects).Add(id, page);
        return PruneCore(copier, id, page, limits, governor, diagnostics, token);
    }
    private sealed class Prepared : IDisposable
    {
        private readonly DocumentEngine _engine;
        private readonly ParserMemoryScope _copyMemory;
        internal readonly GraphCopier Copier;
        internal Prepared(ResourceGovernor governor)
        {
            ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None); PdfDictionary trailer = Dictionary();
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
                    construction, new DiagnosticBag(construction), QpdfRecoveryMode.Strict, 32L, valuesEscape: true, hostInput: false, snapshotBorrow: null);
                fixtureStorage.Retain();
            }
            catch { fixtureTable.Dispose(); throw; }
            _engine = fixtureEngine;
            _copyMemory = ParserMemoryScope.Create(governor);
            Copier = GraphCopier.CreateOwned(_engine, governor, QpdfLimits.Default, 1, _copyMemory);
        }
        public void Dispose() { _engine.Dispose(); _copyMemory.Dispose(); }
    }
    private static PdfDictionary Page(string content, PdfDictionary resources)
        => Dictionary(("Resources", resources), ("Contents", new PdfStream(Dictionary(), Encoding.Latin1.GetBytes(content))));
    private static PdfDictionary Get(PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(new PdfName(Encoding.Latin1.GetBytes(key)), out QpdfObject value)); return Assert.IsType<PdfDictionary>(value); }
    private static PdfDictionary Resources(PdfDictionary page) => Get(page, "Resources");
    private static PdfDictionary Fonts(PdfDictionary page) => Get(Resources(page), "Font");
    private static bool Has(PdfDictionary dictionary, string name) => dictionary.TryGet(new PdfName(Encoding.Latin1.GetBytes(name)), out _);

    [Theory]
    [InlineData("F1", "/F#31 12 Tf", true)]
    [InlineData("F#31", "/F#2331 12 Tf", true)]
    [InlineData("A#41", "/A#23#34#31 12 Tf", true)]
    [InlineData("AA", "/A#23#34#31 cs", false)]
    [InlineData("", "/ 12 Tf", true)]
    [InlineData("A#", "/A# 12 Tf", true)]
    [InlineData("A#4", "/A#4 12 Tf", true)]
    [InlineData("A#GG", "/A#GG 12 Tf", true)]
    [InlineData("A\u0000\u00ff", "/A#00#ff 12 Tf", true)]
    [InlineData("Used", "/Used 12 Q Tf", true)]
    [InlineData("Used", "/Used (name) Tj", false)]
    public void PruningComparesDecodedResourceKeysWithoutDecodingThemAgain(string resourceName, string content, bool kept)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using Prepared prepared = new(governor);
        PdfDictionary fonts = Dictionary((resourceName, Dictionary(("Type", PdfName.FromAscii("Font")))), ("Unused", Dictionary()));
        PdfDictionary page = Page(content, Dictionary(("Font", fonts))); DiagnosticBag diagnostics = new(governor);
        PdfDictionary output = Prune(prepared.Copier, 3, page, QpdfLimits.Default, governor, diagnostics, CancellationToken.None);
        Assert.Equal(kept, Has(Fonts(output), resourceName)); Assert.False(Has(Fonts(output), "Unused"));
    }

    [Theory]
    [InlineData("CS")]
    [InlineData("cs")]
    [InlineData("gs")]
    [InlineData("Tf")]
    [InlineData("SCN")]
    [InlineData("scn")]
    [InlineData("BDC")]
    [InlineData("DP")]
    [InlineData("sh")]
    [InlineData("Do")]
    public void AllEstablishedResourceOperatorsRetainTheLastName(string operation)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using Prepared prepared = new(governor);
        PdfDictionary page = Page("/Used 12 " + operation, Dictionary(("Font", Dictionary(("Used", Dictionary()), ("Unused", Dictionary())))));
        PdfDictionary output = Prune(prepared.Copier, 3, page, QpdfLimits.Default, governor, new DiagnosticBag(governor), CancellationToken.None);
        Assert.True(Has(Fonts(output), "Used")); Assert.False(Has(Fonts(output), "Unused"));
    }

    [Fact]
    public void UnresolvedExplicitFormResourcesPreserveThePageScopeAndEncodedNames()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using Prepared prepared = new(governor);
        PdfStream form = new(Dictionary(("Subtype", PdfName.FromAscii("Form")), ("Resources", Dictionary())), "/A#23#34#31 12 Tf"u8.ToArray());
        PdfDictionary resources = Dictionary(("Font", Dictionary(("A#41", Dictionary()), ("AA", Dictionary()), ("Unused", Dictionary()))),
            ("XObject", Dictionary(("Form", form))));
        PdfDictionary output = Prune(prepared.Copier, 3, Page("/Form Do", resources), QpdfLimits.Default, governor, new DiagnosticBag(governor), CancellationToken.None);
        // An unresolved name under explicit local Resources makes the page
        // scope uncertain. Preserve its other fonts as well as the encoded name.
        Assert.True(Has(Fonts(output), "A#41")); Assert.True(Has(Fonts(output), "AA")); Assert.True(Has(Fonts(output), "Unused"));
    }

    [Fact]
    public void InlineImageBytesDoNotIntroduceResourceNames()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using Prepared prepared = new(governor);
        PdfDictionary page = Page("BI /W 1 /H 1 ID /Unused Tf EI /Used 12 Tf", Dictionary(("Font", Dictionary(("Used", Dictionary()), ("Unused", Dictionary())))));
        PdfDictionary output = Prune(prepared.Copier, 3, page, QpdfLimits.Default, governor, new DiagnosticBag(governor), CancellationToken.None);
        Assert.True(Has(Fonts(output), "Used")); Assert.False(Has(Fonts(output), "Unused"));
    }

    [Fact]
    public void MalformedContentRetainsResourcesAndTheExistingWarning()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using Prepared prepared = new(governor);
        PdfDictionary page = Page("/Used 12 Tf (unterminated", Dictionary(("Font", Dictionary(("Used", Dictionary()), ("Unused", Dictionary())))));
        DiagnosticBag diagnostics = new(governor);
        PdfDictionary output = Prune(prepared.Copier, 3, page, QpdfLimits.Default, governor, diagnostics, CancellationToken.None);
        Assert.Same(page, output); Assert.True(Has(Fonts(output), "Used")); Assert.True(Has(Fonts(output), "Unused"));
        QpdfDiagnostic warning = Assert.Single(diagnostics.Entries); Assert.Equal(QpdfDiagnosticCode.UnsupportedFeature, warning.Code);
        Assert.Equal("Skipped resource pruning for content that could not be parsed.", warning.Message);
    }
}
