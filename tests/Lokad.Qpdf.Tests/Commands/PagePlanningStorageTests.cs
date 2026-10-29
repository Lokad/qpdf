using System.Collections;
using System.Reflection;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class PagePlanningStorageTests
{
    private delegate List<(int Source, int Slot)> Collate(IReadOnlyList<int> counts, PdfPageCollation? policy, ResourceGovernor governor, ParserMemoryScope owner);
    private delegate void Rotate(List<(GraphCopier Copier, int Id)> occurrences, List<List<(PdfRotationMode Mode, int Angle)>> folds, ResourceGovernor governor, ParserMemoryScope owner);
    private static readonly Collate Order = (typeof(Qpdf).GetMethod("CollatePageOrder", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing live collation implementation.")).CreateDelegate<Collate>();
    private static readonly Rotate Apply = (typeof(Qpdf).GetMethod("ApplyRotations", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing live rotation implementation.")).CreateDelegate<Rotate>();

    [Fact]
    public void CollationRefusesLargeGrowthAndRefundsTheCandidate()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor); owner.Reserve(31L);
        long before = governor.Snapshot().LiveMemoryBytes, allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => Order([262144], null, governor, owner));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 65536L);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(31L, owner.ReservedBytes);
    }

    private sealed class CancelingCounts(Action cancel) : IReadOnlyList<int>
    {
        private int _reads;
        public int Count => 1;
        public int this[int index] { get { if (++_reads == 32) { cancel(); } return 262144; } }
        public IEnumerator<int> GetEnumerator() { yield return this[0]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void CancellationDuringCollationRefundsTheCandidate()
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor); owner.Reserve(31L);
        long before = governor.Snapshot().LiveMemoryBytes;
        Assert.ThrowsAny<OperationCanceledException>(() => Order(new CancelingCounts(cancellation.Cancel), null, governor, owner));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(31L, owner.ReservedBytes);
    }

    [Fact]
    public void InterleavingKeepsZeroGroupsAndReturnsOnlyOwnedOrderStorage()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor);
        Assert.Equal(new[] { (0, 0), (2, 0), (2, 1), (0, 1), (2, 2) }, Order([2, 7, 3], new([1, 0, 2]), governor, owner));
        Assert.True(owner.ReservedBytes > 0L); owner.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private static DocumentEngine Engine()
    {
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary trailer = new(new Dictionary<PdfName, QpdfObject>());
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
        return fixtureEngine;
    }

    [Fact]
    public void UnaffectedRotationsBorrowOccurrencesWithoutSnapshottingPages()
    {
        using DocumentEngine engine = Engine(); ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 3, copierMemory);
        PdfDictionary page = new(new Dictionary<PdfName, QpdfObject>()); copier.AddIndirectObjectWithId(page, 3);
        List<(GraphCopier Copier, int Id)> occurrences = Enumerable.Repeat((copier, 3), 262144).ToList();
        List<List<(PdfRotationMode Mode, int Angle)>> folds = Enumerable.Repeat(new List<(PdfRotationMode, int)>(), occurrences.Count).ToList();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor);
        long before = GC.GetAllocatedBytesForCurrentThread(); Apply(occurrences, folds, governor, owner);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 65536L);
        Assert.Equal(0L, owner.ReservedBytes); Assert.Same(page, copier.CopiedObjects[3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusedOrMalformedRotationPreservesSourceAndPriorAdmission(bool malformed)
    {
        using DocumentEngine engine = Engine(); ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 3, copierMemory);
        Dictionary<PdfName, QpdfObject> entries = Enumerable.Range(0, 16384).ToDictionary(i => PdfName.FromAscii("K" + i), _ => (QpdfObject)PdfNull.Instance);
        entries.Add(PdfName.FromAscii("Rotate"), malformed ? PdfName.FromAscii("broken") : new PdfInteger("90", 90));
        PdfDictionary page = new(entries); copier.AddIndirectObjectWithId(page, 3);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor); owner.Reserve(31L);
        long before = governor.Snapshot().LiveMemoryBytes;
        Action rotate = () => Apply([(copier, 3)], [[(PdfRotationMode.Add, 90)]], governor, owner);
        if (malformed) { Assert.Throws<QpdfParseException>(rotate); } else { Assert.Throws<QpdfLimitException>(rotate); }
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(31L, owner.ReservedBytes); Assert.Same(page, copier.CopiedObjects[3]);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    [InlineData("typed")]
    public async Task DuplicateSelectionsRotateIndependentlyFromTheInheritedValue(string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /Rotate 90 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 20] >>", null)], 1), QpdfInputOwnership.Transfer);
        if (route == "typed")
        {
            await Qpdf.SelectPagesAsync(host, [new PdfPageSource(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages([1, 1]))],
                QpdfPath.From("output"), new PdfPageMergeOptions { Rotations = [new(PdfRotationMode.Add, 90, [1]), new(PdfRotationMode.Subtract, 90, [2])] }, CancellationToken.None);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1,1", "--", "--rotate=+90:1", "--rotate=-90:2", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1,1"}],"rotate":["+90:1","-90:2"]}""", CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(0L, host.OpenLeaseCount); Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("inspection"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inspection"), CancellationToken.None);
        Assert.Equal(2, document.PageCount);
        for (int i = 0; i < 2; i++)
        {
            Assert.True(document.PageReferences[i].Page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject value));
            Assert.Equal(i == 0 ? 180 : 0, Assert.IsType<PdfInteger>(value).Value);
        }
    }
}
