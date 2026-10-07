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
        XRefTable table = new(0L, new Dictionary<int, XrefEntry>(), empty, new List<PdfDictionary> { empty });
        return new DocumentEngine(ReadOnlyMemory<byte>.Empty, table, limits, governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict);
    }

    private static PdfDictionary ManyEntries()
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        for (int index = 0; index < 512; index++) { entries.Add(PdfName.FromAscii("unrelated-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)), PdfNull.Instance); }
        return new PdfDictionary(entries);
    }

    [Fact]
    public void MissingInheritedAttributeChargesAllVisitedLegacyEntriesAndDropsScratch()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 64L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using DocumentEngine engine = Engine(limits, governor); PdfDictionary page = ManyEntries();
        long before = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => PageInheritance.Find(engine, new PageLocation(null, page),
            PdfName.FromAscii("Missing"), limits, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(512, page.Count);
    }

    [Fact]
    public void LongInheritedNameComparisonHonorsWorkCeilingWithoutRetiringBorrowedValue()
    {
        byte[] raw = Enumerable.Repeat((byte)97, 8192).ToArray(); PdfName name = new(raw); PdfArray value = new([PdfNull.Instance]);
        PdfDictionary page = new(new Dictionary<PdfName, QpdfObject> { [name] = value });
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 64L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using DocumentEngine engine = Engine(limits, governor); long before = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => PageInheritance.Find(engine, new PageLocation(null, page), new PdfName(raw), limits, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.True(page.TryGet(name, out QpdfObject found)); Assert.Same(value, found);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InheritanceHonorsEitherPreCanceledRouteWithoutDroppingPriorStorage(bool governorToken)
    {
        using CancellationTokenSource cancellation = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using DocumentEngine engine = Engine(QpdfLimits.Default, governor); PdfDictionary page = ManyEntries();
        long before = governor.Snapshot().LiveMemoryBytes, work = governor.Snapshot().TotalWork;
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PageInheritance.Find(engine, new PageLocation(null, page), PdfName.FromAscii("Missing"), QpdfLimits.Default,
            governor, governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(work, governor.Snapshot().TotalWork); Assert.Equal(512, page.Count);
    }

    [Fact]
    public void MissingStreamLengthLookupHonorsWorkCeilingBeforeReportingMissingMetadata()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 64L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null); PdfDictionary dictionary = ManyEntries();
        MethodInfo method = typeof(PdfParser).GetMethod("ResolveStreamLength", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Stream length resolver is missing.");
        TargetInvocationException failure = Assert.Throws<TargetInvocationException>(() => method.Invoke(parser, [dictionary, 123L]));
        Assert.Equal(nameof(QpdfLimits.MaxWork), Assert.IsType<QpdfLimitException>(failure.InnerException).LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(512, dictionary.Count);
    }
}
