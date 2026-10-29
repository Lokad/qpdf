using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Filters;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class InlineContentInputTests
{
    private static AnalysisChunks? Collect(GraphCopier copier, PdfDictionary page, QpdfLimits limits,
        ResourceGovernor governor, CancellationToken token)
        => InlineImages.CollectDecodedContents(copier, page, limits, governor, token);
    private static IReadOnlyList<byte[]> Parts(object owner) => ((AnalysisChunks)owner).Parts;
    private static void Close(object? owner) => (owner as AnalysisChunks)?.Dispose();
    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries) => DecoderOwnershipFixtures.Dictionary(entries);
    private static PdfStream Stream(string filter, byte[] raw)
    {
        QpdfObject name = filter == "bad-plan" ? new PdfInteger("1", 1L) : PdfName.FromAscii(filter);
        bool supported = filter is "FlateDecode" or "ASCIIHexDecode" or "ASCII85Decode" or "LZWDecode" or "RunLengthDecode";
        return new(filter == "" ? Dictionary() : Dictionary(("Filter", name)), supported ? DecoderOwnershipFixtures.Encode(filter, raw) : raw);
    }
    private sealed class Prepared : IDisposable
    {
        private readonly DocumentEngine _engine;
        private readonly ParserMemoryScope _memory;
        internal readonly ResourceGovernor Construction = new(QpdfLimits.Default, CancellationToken.None);
        internal readonly GraphCopier Copier;
        internal Prepared(PdfStream stream)
        {
            PdfDictionary trailer = Dictionary();
            using ParserMemoryScope fixtureStorage = ParserMemoryScope.Create(Construction);
            Dictionary<int, XrefEntry> fixtureEntries = ParserContainers.CreateDictionary<int, XrefEntry>(fixtureStorage);
            using ParserMemoryScope fixtureValues = ParserMemoryScope.Create(Construction);
            List<PdfDictionary> fixtureTrailers = ParserContainers.CreateList<PdfDictionary>(fixtureStorage, 1, IntPtr.Size);
            fixtureTrailers.Add(trailer);
            XRefTable fixtureTable = XRefTable.CreateOwned(0L, fixtureEntries, trailer, fixtureTrailers,
                Construction, fixtureStorage, fixtureValues, false);
            DocumentEngine fixtureEngine;
            try
            {
                fixtureStorage.Reserve(32L);
                fixtureEngine = DocumentEngine.CreateOwned(ReadOnlyMemory<byte>.Empty, fixtureTable, QpdfLimits.Default,
                    Construction, new DiagnosticBag(Construction), QpdfRecoveryMode.Strict, 32L, valuesEscape: true, hostInput: false, snapshotBorrow: null);
                fixtureStorage.Retain();
            }
            catch { fixtureTable.Dispose(); throw; }
            _engine = fixtureEngine;
            _memory = ParserMemoryScope.Create(Construction);
            Copier = GraphCopier.CreateOwned(_engine, Construction, QpdfLimits.Default, 1, _memory);
            var pool = (IDictionary<int, QpdfObject>)Copier.CopiedObjects;
            pool[7] = stream; pool[8] = PdfNull.Instance; pool[9] = new PdfInteger("3", 3L);
        }
        public void Dispose() { _engine.Dispose(); _memory.Dispose(); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("FlateDecode")]
    [InlineData("ASCIIHexDecode")]
    [InlineData("ASCII85Decode")]
    [InlineData("LZWDecode")]
    [InlineData("RunLengthDecode")]
    public void RawFallbacksBorrowStorageAndDecodedStorageRetiresAfterItsConsumer(string filter)
    {
        byte[] raw = "q Q /F1 12 Tf"u8.ToArray(); PdfStream stream = Stream(filter, raw); byte[] original = stream.Data; using Prepared prepared = new(stream);
        PdfDictionary page = Dictionary(("Contents", new PdfArray([stream, new PdfReference(7, 11), stream])));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(23L); long live = governor.Snapshot().LiveMemoryBytes;
        object? owner = Collect(prepared.Copier, page, QpdfLimits.Default, governor, CancellationToken.None);
        try
        {
            Assert.NotNull(owner); IReadOnlyList<byte[]> parts = Parts(owner); Assert.Equal(3, parts.Count);
            foreach (byte[] part in parts)
            {
                Assert.Equal(raw, part);
                if (filter is "" or "DCTDecode" or "UnknownFilter" or "bad-plan") { Assert.Same(stream.AllocationCarrier, part); }
                else { Assert.NotSame(stream.AllocationCarrier, part); }
            }
            using JoinedContent joined = JoinedContent.Create(parts, governor, CancellationToken.None);
            Close(owner); owner = null;
            Assert.Equal("q Q /F1 12 Tf\nq Q /F1 12 Tf\nq Q /F1 12 Tf"u8.ToArray(), joined.Bytes.ToArray());
            Assert.Equal(live + joined.Bytes.Length + 128L, governor.Snapshot().LiveMemoryBytes);
        }
        finally { Close(owner); }
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(original, stream.Data);
        Assert.Same(stream, prepared.Copier.CopiedObjects[7]);
    }

    [Theory]
    [InlineData("DCTDecode")]
    [InlineData("UnknownFilter")]
    [InlineData("bad-plan")]
    public void OpaqueAndMalformedEncodingsCannotBecomeContent(string filter)
    {
        PdfStream stream = Stream(filter, "q Q"u8.ToArray()); using Prepared prepared = new(stream);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Null(Collect(prepared.Copier, Dictionary(("Contents", stream)), QpdfLimits.Default, governor, CancellationToken.None));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal("q Q"u8.ToArray(), stream.Data);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("integer")]
    [InlineData("empty-array")]
    public void AbsentNullUnsupportedAndEmptyContentsProduceAnEmptyOwner(string shape)
    {
        using Prepared prepared = new(Stream("", []));
        PdfDictionary page = shape == "missing" ? Dictionary() : Dictionary(("Contents", shape switch
        { "null" => PdfNull.Instance, "empty-array" => new PdfArray([]), _ => new PdfInteger("1", 1L) }));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        object? owner = Collect(prepared.Copier, page, QpdfLimits.Default, governor, CancellationToken.None);
        try { Assert.NotNull(owner); Assert.Empty(Parts(owner)); }
        finally { Close(owner); }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("missing-reference")]
    [InlineData("null-reference")]
    [InlineData("integer-reference")]
    [InlineData("array-null")]
    [InlineData("array-actual-null")]
    [InlineData("array-integer")]
    public void AllEntriesAreValidatedBeforeDecodingAndInvalidPrefixesRetire(string shape)
    {
        PdfStream invalidData = new(Dictionary(("Filter", PdfName.FromAscii("ASCIIHexDecode"))), "ABCG>"u8.ToArray());
        using Prepared prepared = new(invalidData);
        QpdfObject contents = shape switch
        { "missing-reference" => new PdfReference(99, 7), "null-reference" => new PdfReference(8, 7), "integer-reference" => new PdfReference(9, 7),
            "array-null" => new PdfArray([invalidData, PdfNull.Instance]), "array-integer" => new PdfArray([invalidData, new PdfInteger("1", 1L)]),
            _ => new PdfArray(new QpdfObject[2]) };
        PdfDictionary page = Dictionary(("Contents", contents)); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Null(Collect(prepared.Copier, page, QpdfLimits.Default, governor, CancellationToken.None));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Same(invalidData, prepared.Copier.CopiedObjects[7]);
    }

    [Fact]
    public void SupportedDecodeFailureDropsPreviouslyDecodedChunksAndPreservesPriorAdmission()
    {
        PdfStream first = Stream("FlateDecode", "first"u8.ToArray()); using Prepared prepared = new(first);
        PdfStream bad = new(Dictionary(("Filter", PdfName.FromAscii("ASCIIHexDecode"))), "ABCG>"u8.ToArray());
        PdfDictionary page = Dictionary(("Contents", new PdfArray([first, bad])));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(23L); long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Null(Collect(prepared.Copier, page, QpdfLimits.Default, governor, CancellationToken.None));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(23L, prior.ReservedBytes);
    }

    [Theory]
    [InlineData(256L)]
    [InlineData(512L)]
    [InlineData(2048L)]
    public void CollectionCapacityFailurePreservesInputsAndPriorAdmission(long maximum)
    {
        PdfStream stream = Stream("", "borrowed"u8.ToArray()); using Prepared prepared = new(stream);
        PdfArray array = new(Enumerable.Repeat<QpdfObject>(stream, 512).ToArray()); PdfDictionary page = Dictionary(("Contents", array));
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = maximum }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(23L); long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => Collect(prepared.Copier, page, limits, governor, CancellationToken.None));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(512, array.Count); Assert.Same(stream, prepared.Copier.CopiedObjects[7]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothCancellationRoutesPreventInputWork(bool governorToken)
    {
        PdfStream stream = Stream("", "raw"u8.ToArray()); using Prepared prepared = new(stream); PdfDictionary page = Dictionary(("Contents", stream));
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        Assert.Throws<OperationCanceledException>(() => Collect(prepared.Copier, page, QpdfLimits.Default, governor, governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);     }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PageWarnsAndFormStaysSilentOnSupportedDecodeFailure(bool form)
    {
        PdfStream bad = new(Dictionary(("Filter", PdfName.FromAscii("ASCIIHexDecode"))), "ABCG>"u8.ToArray()); using Prepared prepared = new(bad);
        PdfDictionary page = Dictionary(("Contents", new PdfReference(7, 11)));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(prepared.Construction);
        if (form) { InlineImages.ExternalizeForm(prepared.Copier, 7, bad, 0L, false, null, QpdfLimits.Default, governor, diagnostics, CancellationToken.None, false, 0); Assert.Empty(diagnostics.Entries); }
        else
        {
            Assert.Same(page, InlineImages.ExternalizePage(prepared.Copier, 3, page, 0L, false, null, QpdfLimits.Default, governor, diagnostics, CancellationToken.None, false, 0));
            QpdfDiagnostic warning = Assert.Single(diagnostics.Entries); Assert.Equal(QpdfDiagnosticSeverity.Warning, warning.Severity);
            Assert.Equal(QpdfDiagnosticCode.UnsupportedFeature, warning.Code);
            Assert.Equal("Unable to filter content stream; not attempting to externalize inline images.", warning.Message);
        }
        Assert.Same(bad, prepared.Copier.CopiedObjects[7]); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
