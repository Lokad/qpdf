using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class GraphCopierSharingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialConstructionRefusalRefundsItsOwnerWithoutReleasingPriorStorage(bool shared)
    {
        QpdfLimits limits = shared ? QpdfLimits.Default : QpdfLimits.Default with { MaxMemoryBytes = 1099L };
        RecordingMemoryBudget root = new(1024 * 1024L) { RefuseOnGrowth = shared ? 2 : 0 };
        using IMemoryScope caller = root.CreateScope(); using InMemoryQpdfHost host = new(limits, null, caller);
        ResourceGovernor governor = new(limits, CancellationToken.None, host);
        using DocumentEngine engine = EmptyEngine(new(QpdfLimits.Default, CancellationToken.None));
        using (ParserMemoryScope prior = ParserMemoryScope.Create(governor))
        {
            prior.Reserve(700L); long baseline = governor.Snapshot().LiveMemoryBytes;
            using (ParserMemoryScope ownership = ParserMemoryScope.Create(governor))
            {
                Action construct = () => GraphCopier.CreateOwned(engine, governor, limits, 1, ownership);
                if (shared) { Assert.Throws<QpdfMemoryAdmissionException>(construct); }
                else { Assert.Throws<QpdfLimitException>(construct); }
            }
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(700L, prior.ReservedBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }

    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));

    private static DocumentEngine EmptyEngine(ResourceGovernor governor)
    {
        PdfDictionary trailer = Dictionary();
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
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ChangedPathsPreservePrefixesAndRemapCycles(int changedIndex)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        var objects = CheckTests.SinglePage("<< /Length 4 >>", "q Q\n"u8.ToArray());
        objects.Add((6, "<< /Before 17 /Self 6 0 R /After (tail) >>", null));
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("source.pdf"), CheckTests.Assemble(objects, 1), QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenCore(host, QpdfPath.From("source.pdf"),
            QpdfLimits.Default, governor, new(governor), QpdfRecoveryMode.Strict, CancellationToken.None);
        using ParserMemoryScope ownership = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, QpdfLimits.Default, 100, ownership);
        QpdfObject[] values = [new PdfInteger("17", 17L), new PdfByteString("kept"u8.ToArray(), false), PdfBoolean.True];
        values[changedIndex] = new PdfReference(6, 0);
        PdfArray array = new(values);
        PdfDictionary dictionary = Dictionary(("A", values[0]), ("B", values[1]), ("C", values[2]));
        PdfArray copiedArray = Assert.IsType<PdfArray>(copier.CopyValue(array, 0));
        PdfDictionary copiedDictionary = Assert.IsType<PdfDictionary>(copier.CopyValue(dictionary, 0));
        Assert.Equal(dictionary.Keys, copiedDictionary.Keys);
        for (int index = 0; index < values.Length; index++)
        {
            Assert.True(copiedDictionary.TryGet(PdfName.FromAscii(((char)('A' + index)).ToString()), out QpdfObject copied));
            if (index == changedIndex)
            {
                Assert.Equal(100, Assert.IsType<PdfReference>(copiedArray[index]).ObjectNumber);
                Assert.Equal(100, Assert.IsType<PdfReference>(copied).ObjectNumber);
                Assert.Equal(6, Assert.IsType<PdfReference>(array[index]).ObjectNumber);
            }
            else { Assert.Equal(values[index], copiedArray[index]); Assert.Equal(values[index], copied); }
        }
        PdfDictionary cycle = Assert.IsType<PdfDictionary>(Assert.Single(copier.CopiedObjects).Value);
        Assert.True(cycle.TryGet(PdfName.FromAscii("Self"), out QpdfObject self));
        Assert.Equal(100, Assert.IsType<PdfReference>(self).ObjectNumber);
        await document.DisposeAsync();
        Assert.True(cycle.TryGet(PdfName.FromAscii("After"), out QpdfObject after));
        Assert.Equal("tail"u8.ToArray(), Assert.IsType<PdfByteString>(after).Value);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public void UnchangedContainersKeepPublicPayloadsImmutable()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using DocumentEngine engine = EmptyEngine(governor);
        using ParserMemoryScope ownership = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(engine, governor, QpdfLimits.Default, 100, ownership);
        byte[] bytes = "kept"u8.ToArray();
        PdfStream stream = new(Dictionary(("Length", new PdfInteger("4", 4L))), bytes);
        PdfArray array = new([Dictionary(("Data", stream)), new PdfArray([])]);
        PdfArray copied = Assert.IsType<PdfArray>(copier.CopyValue(array, 0));
        Assert.Equal(array, copied);
        PdfDictionary copiedHeader = Assert.IsType<PdfDictionary>(copied[0]);
        Assert.True(copiedHeader.TryGet(PdfName.FromAscii("Data"), out QpdfObject copiedValue));
        PdfStream copiedStream = Assert.IsType<PdfStream>(copiedValue);
        Array.Fill(bytes, (byte)'X');
        byte[] publicCopy = copiedStream.Data; Array.Fill(publicCopy, (byte)'Y');
        Assert.Equal("kept"u8.ToArray(), stream.Data);
        Assert.Equal("kept"u8.ToArray(), copiedStream.Data);
    }

    [Fact]
    public async Task IdentityReferencesStillValidateGenerationsAndCopyReachableCycles()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        var objects = CheckTests.SinglePage("<< /Length 4 >>", "q Q\n"u8.ToArray());
        objects.Add((6, "<< /Self 6 0 R /Text (kept) >>", null));
        InMemoryQpdfHost host = new(); QpdfPath path = QpdfPath.From("source.pdf");
        host.AddInput(path, CheckTests.Assemble(objects, 1), QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenCore(host, path,
            QpdfLimits.Default, governor, new(governor), QpdfRecoveryMode.Strict, CancellationToken.None);
        using ParserMemoryScope ownership = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, QpdfLimits.Default, 6, ownership);
        PdfReference reference = new(6, 0);
        PdfArray source = new([Dictionary(("Link", reference)), reference]);
        Assert.Same(source, copier.CopyValue(source, 0));
        KeyValuePair<int, QpdfObject> copied = Assert.Single(copier.CopiedObjects);
        Assert.Equal(6, copied.Key);
        PdfDictionary cycle = Assert.IsType<PdfDictionary>(copied.Value);
        Assert.True(cycle.TryGet(PdfName.FromAscii("Self"), out QpdfObject self));
        Assert.Equal(6, Assert.IsType<PdfReference>(self).ObjectNumber);
        Assert.Same(PdfNull.Instance, copier.CopyValue(new PdfReference(6, 1), 0));
        Assert.Single(copier.CopiedObjects);
        await document.DisposeAsync();
        Assert.True(cycle.TryGet(PdfName.FromAscii("Text"), out QpdfObject text));
        Assert.Equal("kept"u8.ToArray(), Assert.IsType<PdfByteString>(text).Value);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public void RemovedEntriesAndRepairedLengthsPreserveRemainingOrder()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using DocumentEngine engine = EmptyEngine(governor);
        using ParserMemoryScope ownership = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(engine, governor, QpdfLimits.Default, 100, ownership);
        QpdfObject[] absent = new QpdfObject[1];
        PdfDictionary source = Dictionary(("A", PdfBoolean.True), ("Missing", absent[0]),
            ("Parent", Dictionary(("Type", PdfName.FromAscii("Pages")))), ("B", PdfBoolean.False));
        PdfDictionary copied = Assert.IsType<PdfDictionary>(copier.CopyValue(source, 0));
        Assert.Equal(new[] { "A", "B" }, copied.Keys.Select(key => key.DisplayText));
        foreach (PdfDictionary header in new[] { Dictionary(("Tag", PdfBoolean.True)),
            Dictionary(("Tag", PdfBoolean.True), ("Length", new PdfReference(404, 0))) })
        {
            PdfStream stream = new(header, "kept"u8.ToArray());
            PdfStream repaired = Assert.IsType<PdfStream>(copier.CopyValue(stream, 0));
            Assert.True(repaired.Dictionary.TryGet(PdfName.FromAscii("Length"), out QpdfObject length));
            Assert.Equal(4L, Assert.IsType<PdfInteger>(length).Value);
            Assert.Equal("kept"u8.ToArray(), repaired.Data);
        }
    }
}
