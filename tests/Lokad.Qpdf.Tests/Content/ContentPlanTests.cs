using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class ContentPlanTests
{
    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));

    private sealed class Prepared : IDisposable
    {
        private readonly DocumentEngine _engine;
        private readonly ParserMemoryScope _memory;
        internal readonly GraphCopier Copier;
        internal readonly PdfStream Stream;
        internal readonly ResourceGovernor Construction = new(QpdfLimits.Default, CancellationToken.None);
        internal Prepared()
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
            Stream = new(Dictionary(), new byte[2 * 1024 * 1024]);
            ((IDictionary<int, QpdfObject>)Copier.CopiedObjects)[7] = Stream;
            ((IDictionary<int, QpdfObject>)Copier.CopiedObjects)[8] = PdfNull.Instance;
        }
        public void Dispose() { _engine.Dispose(); _memory.Dispose(); }
    }

    [Fact]
    public void IndirectArrayEntriesKeepStreamIdentities()
    {
        using Prepared prepared = new();
        var pool = (IDictionary<int, QpdfObject>)prepared.Copier.CopiedObjects;
        pool[9] = new PdfArray([new PdfReference(7, 0), prepared.Stream, new PdfReference(7, 0)]);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ContentEntries? entries = ContentTransforms.ReadPageContents(prepared.Copier,
            Dictionary(("Contents", new PdfReference(9, 0))), governor, new(governor), CancellationToken.None);
        Assert.NotNull(entries); Assert.Equal(3, entries.Count);
        Assert.Equal(7, entries.Get(0).Reference?.ObjectNumber); Assert.Null(entries.Get(1).Reference); Assert.Equal(7, entries.Get(2).Reference?.ObjectNumber);

    }

    [Theory]
    [InlineData("direct")]
    [InlineData("reference")]
    [InlineData("mixed")]
    [InlineData("empty")]
    public void PlanBorrowsStreamsAndPreservesIdsIndicesAndGenerationTreatment(string shape)
    {
        using Prepared prepared = new();
        QpdfObject contents = shape switch
        { "direct" => prepared.Stream, "reference" => new PdfReference(7, 11), "mixed" => new PdfArray([prepared.Stream, new PdfReference(7, 11), prepared.Stream]), _ => new PdfArray([]) };
        PdfDictionary page = Dictionary(("Contents", contents));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        ContentEntries? entries = ContentTransforms.ReadPageContents(prepared.Copier, page, governor, new(prepared.Construction), CancellationToken.None);
        try
        {
            Assert.NotNull(entries); int count = shape == "mixed" ? 3 : shape == "empty" ? 0 : 1;
            Assert.Equal(count, entries.Count); Assert.True(governor.Snapshot().LiveMemoryBytes >= 160L);
            for (int index = 0; index < count; index++)
            {
                ContentEntries.Entry entry = entries.Get(index); Assert.Same(prepared.Stream, entry.Stream); Assert.Equal(index, entry.ArrayIndex);
                Assert.Equal(shape == "reference" || shape == "mixed" && index == 1 ? 7 : (int?)null, entry.Reference?.ObjectNumber);
            }
            Assert.Equal(1, page.Count);
        }
        finally { entries?.Dispose(); }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("dictionary")]
    public void AbsentNullAndUnsupportedSingleContentsStaySilentWithoutAnOwner(string shape)
    {
        using Prepared prepared = new();
        PdfDictionary page = shape == "missing" ? Dictionary() : Dictionary(("Contents", shape == "null" ? PdfNull.Instance : Dictionary()));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(prepared.Construction);
        Assert.Null(ContentTransforms.ReadPageContents(prepared.Copier, page, governor, diagnostics, CancellationToken.None)); Assert.Empty(diagnostics.Entries);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("missing-reference")]
    [InlineData("nonstream-reference")]
    [InlineData("array-nonstream")]
    [InlineData("array-reference")]
    public void InvalidContentsDropPrefixScratchAndKeepTheExactWarning(string shape)
    {
        using Prepared prepared = new();
        if (shape == "nonstream-reference") { ((IDictionary<int, QpdfObject>)prepared.Copier.CopiedObjects)[8] = PdfInteger.GetSharedValue(3); }
        QpdfObject contents = shape switch
        { "missing-reference" => new PdfReference(99, 3), "nonstream-reference" => new PdfReference(8, 3),
          "array-nonstream" => new PdfArray([prepared.Stream, PdfNull.Instance]), _ => new PdfArray([prepared.Stream, new PdfReference(99, 3)]) };
        PdfDictionary page = Dictionary(("Contents", contents)); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag diagnostics = new(prepared.Construction);
        Assert.Null(ContentTransforms.ReadPageContents(prepared.Copier, page, governor, diagnostics, CancellationToken.None));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        QpdfDiagnostic warning = Assert.Single(diagnostics.Entries);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedFeature, warning.Code); Assert.Equal(QpdfDiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("Skipped content operation on unreadable page contents.", warning.Message);
        Assert.True(page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject unchanged));
        Assert.Same(contents, unchanged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionalNullContentsOwnAnEmptyPlanAndRefundItsStorage(bool indirect)
    {
        using Prepared prepared = new(); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope scratch = ParserMemoryScope.Create(governor))
        {
            PoolResolver resolver = PoolResolver.Create(scratch, prepared.Copier.CopiedObjects); long baseline = governor.Snapshot().LiveMemoryBytes;
            using (ContentEntries? entries = ContentEntries.TryRead(indirect ? new PdfReference(8, 0) : PdfNull.Instance, resolver, governor, CancellationToken.None))
            {
                Assert.NotNull(entries); Assert.Equal(0, entries.Count); Assert.True(governor.Snapshot().LiveMemoryBytes > baseline);
            }
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(256L)]
    [InlineData(512L)]
    [InlineData(2048L)]
    public void PlanGrowthFailurePreservesOriginalInputsAndPriorFunding(long maximum)
    {
        using Prepared prepared = new(); PdfArray contents = new(Enumerable.Repeat<QpdfObject>(prepared.Stream, 512).ToArray());
        PdfDictionary page = Dictionary(("Contents", contents)); ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = maximum }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L); long live = governor.Snapshot().LiveMemoryBytes;
        DiagnosticBag diagnostics = new(prepared.Construction);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => ContentTransforms.ReadPageContents(prepared.Copier, page, governor, diagnostics, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(61L, prior.ReservedBytes); Assert.Equal(512, contents.Count); Assert.Empty(diagnostics.Entries);
    }

    [Fact]
    public void GovernorCancellationPreventsPlanLookupAndConstruction()
    {
        using Prepared prepared = new(); PdfDictionary page = Dictionary(("Contents", prepared.Stream));
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => ContentTransforms.ReadPageContents(prepared.Copier, page, governor, new(prepared.Construction), CancellationToken.None));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
