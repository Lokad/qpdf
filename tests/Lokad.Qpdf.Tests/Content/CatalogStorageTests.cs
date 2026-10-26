using System.Collections;
using System.Reflection;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class CatalogStorageTests
{
    private delegate PdfDictionary Catalog(QpdfObject? labels, PdfVersion version, int extension,
        PdfReference? metadata, PdfReference? names, string? mode, PdfReference? outlines, PdfDictionary? form,
        PdfReference? structure, QpdfObject? mark, IReadOnlyDictionary<PdfName, QpdfObject>? extras,
        IReadOnlyDictionary<int, QpdfObject> pool, ResourceGovernor governor, CancellationToken token);
    private static readonly Catalog Build = (typeof(Qpdf).GetMethod("NewCatalog", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing live catalog builder.")).CreateDelegate<Catalog>();

    private static PdfDictionary Dict(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(e => PdfName.FromAscii(e.Key), e => e.Value));

    private static DocumentEngine Engine(PdfDictionary catalog)
    {
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary trailer = Dict(("Root", catalog));
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
                construction, new(construction), QpdfRecoveryMode.Strict, 32L);
            fixtureStorage.Retain();
        }
        catch { fixtureTable.Dispose(); throw; }
        return fixtureEngine;
    }

    internal static byte[] Fixture(int keys) => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R " + string.Concat(Enumerable.Range(0, keys).Select(i => "/Key" + i + " 1 ")) + ">>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 20] /Rotate 90 >>", null)], 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeCatalogCapacityRefusesBeforeAllocationAndRefunds(bool copying)
    {
        Dictionary<PdfName, QpdfObject> extras = Enumerable.Range(0, 262144).ToDictionary(i => PdfName.FromAscii("K" + i), _ => (QpdfObject)new PdfInteger("1", 1));
        using DocumentEngine engine = Engine(new PdfDictionary(extras));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor); owner.Reserve(31L);
        GraphCopier copier = GraphCopier.CreateOwned(engine, governor, QpdfLimits.Default, 3, owner);
        long before = governor.Snapshot().LiveMemoryBytes, allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() =>
        {
            if (copying) { CatalogCopy.TryCopyExtras(engine, copier, true, false, governor, owner, CancellationToken.None); }
            else { Build(null, new(1, 4), 0, null, null, null, null, null, null, null, extras, copier.CopiedObjects, governor, CancellationToken.None); }
        });
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 65536L);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void LongRawKeysStayBorrowedThroughCopyAndEscapingCatalogConstruction()
    {
        byte[] raw = new byte[1048576]; raw.AsSpan().Fill(0xff); raw[0] = 0;
        PdfName key = new(raw); PdfByteString payload = new(new byte[] { 255, 0, 128 }, true);
        using DocumentEngine engine = Engine(new(new Dictionary<PdfName, QpdfObject> { [key] = payload }));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None) { OwnsDocumentCounters = true };
        PdfDictionary escaped;
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            GraphCopier copier = GraphCopier.CreateOwned(engine, governor, QpdfLimits.Default, 3, owner);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            IReadOnlyDictionary<PdfName, QpdfObject> extras = CatalogCopy.TryCopyExtras(engine, copier, true, false, governor, owner, CancellationToken.None);
            escaped = Build(null, new(1, 4), 0, null, null, null, null, null, null, null, extras, copier.CopiedObjects, governor, CancellationToken.None);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 65536L);
            Assert.Same(key, Assert.Single(extras).Key);
        }
        engine.Dispose(); governor.AcceptDocument(); governor.CompletePrivateStorage(); Assert.True(governor.CallerStorageBytes > 0L);
        Assert.True(escaped.TryGet(key, out QpdfObject kept)); Assert.Same(payload, kept);
        Assert.Equal(new byte[] { 255, 0, 128 }, Assert.IsType<PdfByteString>(kept).Value);
        Assert.Contains(escaped.Keys, candidate => ReferenceEquals(candidate, key));
    }

    private sealed class CancelingExtras(Action cancel) : IReadOnlyDictionary<PdfName, QpdfObject>
    {
        public int Count => 128;
        public IEnumerable<PdfName> Keys => throw new NotSupportedException();
        public IEnumerable<QpdfObject> Values => throw new NotSupportedException();
        public QpdfObject this[PdfName key] => throw new NotSupportedException();
        public bool ContainsKey(PdfName key) => throw new NotSupportedException();
        public bool TryGetValue(PdfName key, out QpdfObject value) => throw new NotSupportedException();
        public IEnumerator<KeyValuePair<PdfName, QpdfObject>> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            { if (i == 32) { cancel(); } yield return new(PdfName.FromAscii("Key" + i), PdfNull.Instance); }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringFinalAssemblyRefundsTheCandidate(bool caller)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(31L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.ThrowsAny<OperationCanceledException>(() => Build(null, new(1, 4), 0, null, null, null, null, null, null, null,
            new CancelingExtras(cancellation.Cancel), new Dictionary<int, QpdfObject>(), governor, caller ? cancellation.Token : CancellationToken.None));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(31L, prior.ReservedBytes);
    }

    private sealed class CancelingObjects(Action cancel) : Dictionary<int, QpdfObject>, IReadOnlyDictionary<int, QpdfObject>
    {
        bool IReadOnlyDictionary<int, QpdfObject>.TryGetValue(int key, out QpdfObject value)
        {
            bool found = TryGetValue(key, out QpdfObject? item);
            value = item ?? PdfNull.Instance; cancel(); return found;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringSourceResolutionRefundsAtThePhaseBoundary(bool caller)
    {
        using CancellationTokenSource cancellation = new();
        using DocumentEngine engine = Engine(Dict(("Extra", new PdfReference(10, 0))));
        engine.SetInspectionObjects(new CancelingObjects(cancellation.Cancel) { [10] = new PdfInteger("1", 1) });
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            GraphCopier copier = GraphCopier.CreateOwned(engine, governor, QpdfLimits.Default, 3, owner);
            Assert.ThrowsAny<OperationCanceledException>(() => CatalogCopy.TryCopyExtras(engine, copier, true, false, governor, owner,
                caller ? cancellation.Token : CancellationToken.None));
        }
        governor.CompletePrivateStorage(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void CatalogConstructionKeepsReplacementAndExtensionSemantics()
    {
        PdfDictionary form = Dict(("Fields", new PdfArray([])));
        PdfArray opaque = new([new PdfReference(17, 0)]); PdfReference mode = new(44, 0);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary catalog = Build(null, new(1, 7), 3, null, null, "UseAttachments", null, form, null, null,
            new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("AcroForm")] = opaque, [PdfName.FromAscii("PageMode")] = mode }, new Dictionary<int, QpdfObject>(), governor, CancellationToken.None);
        Assert.True(catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject kept)); Assert.Same(form, kept);
        Assert.True(catalog.TryGet(PdfName.FromAscii("PageMode"), out kept)); Assert.Same(mode, kept);
        Assert.True(catalog.TryGet(PdfName.FromAscii("Extensions"), out kept)); PdfDictionary extensions = Assert.IsType<PdfDictionary>(kept);
        Assert.True(extensions.TryGet(PdfName.FromAscii("ADBE"), out kept)); PdfDictionary adbe = Assert.IsType<PdfDictionary>(kept);
        Assert.True(adbe.TryGet(PdfName.FromAscii("BaseVersion"), out kept)); Assert.Equal("1.7", Assert.IsType<PdfName>(kept).DisplayText);
        Assert.True(adbe.TryGet(PdfName.FromAscii("ExtensionLevel"), out kept)); Assert.Equal(3, Assert.IsType<PdfInteger>(kept).Value);
        Assert.True(governor.Snapshot().LiveMemoryBytes > 0L); governor.CompletePrivateStorage(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringExtensionResolutionRefundsConstruction(bool caller)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(31L);
        long before = governor.Snapshot().LiveMemoryBytes;
        CancelingObjects pool = new(cancellation.Cancel) { [10] = Dict(("ACME", Dict(("ExtensionLevel", new PdfInteger("42", 42))))) };
        Dictionary<PdfName, QpdfObject> extras = new() { [PdfName.FromAscii("Extensions")] = new PdfReference(10, 0) };
        Assert.ThrowsAny<OperationCanceledException>(() => Build(null, new(1, 7), 3, null, null, null, null, null, null, null,
            extras, pool, governor, caller ? cancellation.Token : CancellationToken.None));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(31L, prior.ReservedBytes);
    }
}
