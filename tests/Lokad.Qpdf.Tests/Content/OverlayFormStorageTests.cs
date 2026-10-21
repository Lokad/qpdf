using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayFormStorageTests
{
    private static PdfDictionary Dict(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));

    private static DocumentEngine Engine(IReadOnlyDictionary<int, QpdfObject> objects, PdfDictionary form)
    {
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary trailer = Dict(("Root", Dict(("AcroForm", form))));
        DocumentEngine engine = new(ReadOnlyMemory<byte>.Empty, new XRefTable(0L, new Dictionary<int, XrefEntry>(), trailer, [trailer]),
            QpdfLimits.Default, construction, new(construction), QpdfRecoveryMode.Strict);
        engine.SetInspectionObjects(objects); return engine;
    }

    [Fact]
    public void RepeatedFieldsNeedOnlyBoundedTraversalStorage()
    {
        PdfArray fields = new(Enumerable.Repeat<QpdfObject>(new PdfReference(2, 0), 262144).ToArray());
        using DocumentEngine engine = Engine(new Dictionary<int, QpdfObject> { [2] = Dict() }, Dict(("Fields", fields)));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        using (ParserMemoryScope planning = ParserMemoryScope.Create(governor))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            AcroFormCopy.OverlaySourceForm? form = AcroFormCopy.TryReadOverlaySourceForm(engine, QpdfLimits.Default, governor, planning, CancellationToken.None);
            Assert.NotNull(form); Assert.Equal(2, Assert.Single(form.ReachedObjects));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 65536L);
            Assert.True(planning.ReservedBytes > 72L); Assert.False(form.FieldsNotArray); Assert.False(form.HasDirectNodes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void DistinctFieldsRefuseBeforeLargeGrowthAndRefundTheCandidate()
    {
        PdfArray fields = new(Enumerable.Range(2, 4096).Select(i => (QpdfObject)new PdfReference(i, 0)).ToArray());
        IReadOnlyDictionary<int, QpdfObject> objects = Enumerable.Range(2, 4096).ToDictionary(i => i, _ => (QpdfObject)Dict());
        using DocumentEngine engine = Engine(objects, Dict(("Fields", fields)));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        using ParserMemoryScope planning = ParserMemoryScope.Create(governor); planning.Reserve(17L);
        long baseline = governor.Snapshot().LiveMemoryBytes, before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => AcroFormCopy.TryReadOverlaySourceForm(engine, QpdfLimits.Default, governor, planning, CancellationToken.None));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 65536L);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(17L, planning.ReservedBytes);
    }

    private sealed class CancelingObjects(Action cancel) : Dictionary<int, QpdfObject>, IReadOnlyDictionary<int, QpdfObject>
    {
        bool IReadOnlyDictionary<int, QpdfObject>.TryGetValue(int key, [MaybeNullWhen(false)] out QpdfObject value)
        { if (key == 2) { cancel(); } return TryGetValue(key, out value); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringResolutionRefundsTraversalAndRetainedCandidate(bool caller)
    {
        using CancellationTokenSource cancellation = new();
        CancelingObjects objects = new(cancellation.Cancel) { [2] = Dict() };
        using DocumentEngine engine = Engine(objects, Dict(("Fields", new PdfArray([new PdfReference(2, 0)]))));
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope planning = ParserMemoryScope.Create(governor); planning.Reserve(17L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.ThrowsAny<OperationCanceledException>(() => AcroFormCopy.TryReadOverlaySourceForm(engine, QpdfLimits.Default,
            governor, planning, caller ? cancellation.Token : CancellationToken.None));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(17L, planning.ReservedBytes);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(16, 4)]
    public void TraversalPreservesCycleBoundsAndDirectNodeReporting(int depth, int expected)
    {
        Dictionary<int, QpdfObject> objects = new()
        {
            [2] = Dict(("Kids", new PdfArray([new PdfReference(3, 0)]))),
            [3] = Dict(("Kids", new PdfArray([new PdfReference(5, 0)]))),
            [5] = Dict(("Kids", new PdfArray([new PdfReference(2, 0)]))),
            [4] = Dict(("Kids", new PdfInteger("1", 1)))
        };
        PdfDictionary source = Dict(("Fields", new PdfArray([new PdfReference(2, 0), new PdfReference(4, 0), Dict()])));
        using DocumentEngine engine = Engine(objects, source);
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = depth };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor);
        AcroFormCopy.OverlaySourceForm? form = AcroFormCopy.TryReadOverlaySourceForm(engine, limits, governor, owner, CancellationToken.None);
        Assert.NotNull(form); Assert.True(form.HasDirectNodes); Assert.False(form.FieldsNotArray);
        Assert.Equal(expected, form.ReachedObjects.Count);
    }

    [Fact]
    public void MalformedFieldsStillReportTheirShape()
    {
        using DocumentEngine engine = Engine(new Dictionary<int, QpdfObject>(), Dict(("Fields", new PdfInteger("7", 7))));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope planning = ParserMemoryScope.Create(governor);
        AcroFormCopy.OverlaySourceForm? form = AcroFormCopy.TryReadOverlaySourceForm(engine, QpdfLimits.Default, governor, planning, CancellationToken.None);
        Assert.NotNull(form); Assert.True(form.HasFields); Assert.True(form.FieldsNotArray); Assert.Empty(form.ReachedObjects);
    }
}
