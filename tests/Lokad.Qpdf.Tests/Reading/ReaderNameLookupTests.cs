using System.Reflection;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class ReaderNameLookupTests
{
    private static DocumentEngine Engine(QpdfLimits limits, ResourceGovernor governor)
    {
        PdfDictionary empty = new(new Dictionary<PdfName, QpdfObject>());
        using ParserMemoryScope fixtureStorage = ParserMemoryScope.Create(governor);
        Dictionary<int, XrefEntry> fixtureEntries = ParserContainers.CreateDictionary<int, XrefEntry>(fixtureStorage);
        using ParserMemoryScope fixtureValues = ParserMemoryScope.Create(governor);
        List<PdfDictionary> fixtureTrailers = ParserContainers.CreateList<PdfDictionary>(fixtureStorage, 1, IntPtr.Size);
        fixtureTrailers.Add(empty);
        XRefTable fixtureTable = XRefTable.CreateOwned(0L, fixtureEntries, empty, fixtureTrailers,
            governor, fixtureStorage, fixtureValues, false);
        DocumentEngine fixtureEngine;
        try
        {
            fixtureStorage.Reserve(32L);
            fixtureEngine = DocumentEngine.CreateOwned(ReadOnlyMemory<byte>.Empty, fixtureTable, limits,
                governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict, 32L);
            fixtureStorage.Retain();
        }
        catch { fixtureTable.Dispose(); throw; }
        return fixtureEngine;
    }

    private static PdfDictionary ManyEntries()
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        for (int index = 0; index < 512; index++) { entries.Add(PdfName.FromAscii("unrelated-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)), PdfNull.Instance); }
        return new PdfDictionary(entries);
    }

    [Fact]
    public void InheritedBinaryNameReturnsTheBorrowedAttribute()
    {
        PdfName name = new([0, 255, 35]); PdfArray value = new([PdfNull.Instance]);
        PdfDictionary page = new(new Dictionary<PdfName, QpdfObject> { [name] = value });
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using DocumentEngine engine = Engine(QpdfLimits.Default, governor); long before = governor.Snapshot().LiveMemoryBytes;
        Assert.Same(value, PageInheritance.Find(engine, new PageLocation(null, page), new PdfName([0, 255, 35]), QpdfLimits.Default, governor, CancellationToken.None));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void FirstParentUsesTheCompleteReferenceIdentity()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using DocumentEngine engine = Engine(QpdfLimits.Default, governor);
        PdfName key = PdfName.FromAscii("MediaBox"); PdfArray value = new([PdfNull.Instance]);
        PdfDictionary ancestor = new(new Dictionary<PdfName, QpdfObject> { [key] = value });
        engine.SetInspectionObjects(new Dictionary<int, QpdfObject> { [3] = ancestor });
        PdfDictionary page = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Parent")] = new PdfReference(3, 0) });
        long before = governor.Snapshot().LiveMemoryBytes;
        Assert.Same(value, PageInheritance.Find(engine, new PageLocation(new PdfReference(3, 1), page), key,
            QpdfLimits.Default, governor, CancellationToken.None));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InheritanceHonorsEitherPreCanceledRouteWithoutDroppingPriorStorage(bool governorToken)
    {
        using CancellationTokenSource cancellation = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using DocumentEngine engine = Engine(QpdfLimits.Default, governor); PdfDictionary page = ManyEntries();
        long before = governor.Snapshot().LiveMemoryBytes;
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PageInheritance.Find(engine, new PageLocation(null, page), PdfName.FromAscii("Missing"), QpdfLimits.Default,
            governor, governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(512, page.Count);
    }
}
