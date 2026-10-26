using System.Reflection;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Filters;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ResourcePruningChunkTests
{
    private static readonly Func<GraphCopier, int, PdfDictionary, QpdfLimits, ResourceGovernor, DiagnosticBag, CancellationToken, PdfDictionary> Prune =
        (typeof(ContentTransforms).GetMethod("PrunePage", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing pruning."))
        .CreateDelegate<Func<GraphCopier, int, PdfDictionary, QpdfLimits, ResourceGovernor, DiagnosticBag, CancellationToken, PdfDictionary>>();
    private static readonly Func<GraphCopier, PdfDictionary, PoolResolver, QpdfLimits, ResourceGovernor, CancellationToken, object?> Collect =
        (typeof(ContentTransforms).GetMethod("CollectPageChunks", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing collection."))
        .CreateDelegate<Func<GraphCopier, PdfDictionary, PoolResolver, QpdfLimits, ResourceGovernor, CancellationToken, object?>>();
    private sealed class Prepared : IDisposable
    {
        private readonly DocumentEngine _engine;
        private readonly ParserMemoryScope _copyMemory;
        internal readonly GraphCopier Copier;
        internal Prepared(ResourceGovernor governor)
        {
            ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
            PdfDictionary trailer = DecoderOwnershipFixtures.Dictionary();
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
                    construction, new DiagnosticBag(construction), QpdfRecoveryMode.Strict, 32L);
                fixtureStorage.Retain();
            }
            catch { fixtureTable.Dispose(); throw; }
            _engine = fixtureEngine;
            _copyMemory = ParserMemoryScope.Create(governor);
            Copier = GraphCopier.CreateOwned(_engine, governor, QpdfLimits.Default, 1, _copyMemory);
        }
        public void Dispose() { _engine.Dispose(); _copyMemory.Dispose(); }
    }
    private static PdfStream Stream(string filter, byte[] raw, bool form)
    {
        PdfDictionary dictionary = form
            ? DecoderOwnershipFixtures.Dictionary(("Filter", PdfName.FromAscii(filter)), ("Subtype", PdfName.FromAscii("Form")), ("Resources", DecoderOwnershipFixtures.Dictionary()))
            : DecoderOwnershipFixtures.Dictionary(("Filter", PdfName.FromAscii(filter)));
        return new(dictionary, DecoderOwnershipFixtures.Encode(filter, raw));
    }
    public static TheoryData<string, string> SuccessfulShapes()
    {
        TheoryData<string, string> cases = new();
        foreach (string filter in new[] { "FlateDecode", "ASCIIHexDecode", "ASCII85Decode", "LZWDecode", "RunLengthDecode" })
        {
            foreach (string shape in new[] { "single", "multiple", "form" }) { cases.Add(filter, shape); }
        }
        return cases;
    }
    [Theory]
    [MemberData(nameof(SuccessfulShapes))]
    public void AnalysisRetiresAllDecodedChunksAcrossPageAndFormShapes(string filter, string shape)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
        using Prepared prepared = new(governor); long live = governor.Snapshot().LiveMemoryBytes;
        byte[] raw = System.Text.Encoding.ASCII.GetBytes(new string(' ', 16384) + "q");
        PdfStream stream = Stream(filter, raw, shape == "form"); byte[] original = stream.Data;
        PdfDictionary resources = DecoderOwnershipFixtures.Dictionary(); QpdfObject contents = stream;
        if (shape == "multiple") { contents = new PdfArray([stream, Stream(filter, "Q"u8.ToArray(), false)]); }
        if (shape == "form")
        {
            ((IDictionary<int, QpdfObject>)prepared.Copier.CopiedObjects).Add(5, stream);
            resources = DecoderOwnershipFixtures.Dictionary(("XObject", DecoderOwnershipFixtures.Dictionary(("Form", new PdfReference(5, 0)))));
            contents = new PdfStream(DecoderOwnershipFixtures.Dictionary(), "/Form Do"u8.ToArray());
        }
        PdfDictionary page = DecoderOwnershipFixtures.Dictionary(("Resources", resources), ("Contents", contents));
        ((IDictionary<int, QpdfObject>)prepared.Copier.CopiedObjects).Add(3, page);
        Assert.Same(page, Prune(prepared.Copier, 3, page, QpdfLimits.Default, governor, new DiagnosticBag(governor), CancellationToken.None));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(61L, prior.ReservedBytes); Assert.Equal(original, stream.Data);
        Assert.True(governor.Snapshot().TotalDecodedBytes >= raw.Length);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableLaterChunkRetiresOnlyCollectionStorage(bool unsupported)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
        using Prepared prepared = new(governor); long live = governor.Snapshot().LiveMemoryBytes; PdfStream first = Stream("FlateDecode", "q"u8.ToArray(), false); byte[] original = first.Data;
        PdfStream bad = new(DecoderOwnershipFixtures.Dictionary(("Filter", PdfName.FromAscii(unsupported ? "DCTDecode" : "ASCIIHexDecode"))), "ABCG>"u8.ToArray());
        PdfDictionary page = DecoderOwnershipFixtures.Dictionary(("Contents", new PdfArray([first, bad])));
        Assert.Null(Collect(prepared.Copier, page, new PoolResolver((IReadOnlyDictionary<int, QpdfObject>)prepared.Copier.CopiedObjects), QpdfLimits.Default, governor, CancellationToken.None));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(61L, prior.ReservedBytes); Assert.Equal(original, first.Data);
    }
}
