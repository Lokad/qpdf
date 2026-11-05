using System.Globalization;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AnnotationDistanceTests
{
    public static IEnumerable<object[]> Distances()
    {
        foreach (bool indirect in new[] { false, true })
        {
            yield return ["LL", -5L, indirect];
            yield return ["LLE", 3L, indirect];
            yield return ["LLO", 4L, indirect];
        }
    }

    [Theory]
    [MemberData(nameof(Distances))]
    public void ScaleLineDistancesWithoutRotatingTheirLocalAxes(string key, long value, bool indirect)
    {
        PdfInteger source = new(value.ToString(CultureInfo.InvariantCulture), value);
        QpdfObject raw = indirect ? new PdfReference(1, 0) : source;
        PoolResolver pool = new(new Dictionary<int, QpdfObject> { [1] = source });
        PdfDictionary annotation = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Line") });
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None))
        {
            PdfReal result = Assert.IsType<PdfReal>(AnnotationCoordinates.TransformEntry(annotation, PdfName.FromAscii(key), raw,
                [0, -2, 2, 0, 17, 23], pool, graph));
            Assert.Equal(value * 2.0, result.Value); Assert.Equal(value, source.Value);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("LL")]
    [InlineData("LLE")]
    [InlineData("LLO")]
    [InlineData("CO")]
    public void RotationAndTranslationKeepLocalValuesBorrowed(string key)
    {
        QpdfObject raw = PdfName.FromAscii("Opaque");
        PdfDictionary annotation = new(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Line"), [PdfName.FromAscii("Cap")] = new PdfBoolean(true) });
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        long before = governor.Snapshot().LiveMemoryBytes;
        Assert.Same(raw, AnnotationCoordinates.TransformEntry(annotation, PdfName.FromAscii(key), raw,
            [0, -1, 1, 0, 17, 23], new(new Dictionary<int, QpdfObject>()), graph));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CaptionOffsetsScaleInTheirLineAxesOnlyWhenUsed(bool caption, bool indirect)
    {
        QpdfObject source = caption ? new PdfArray([new PdfReference(2, 0), new PdfInteger("-3", -3)]) : PdfName.FromAscii("Opaque");
        QpdfObject raw = indirect ? new PdfReference(1, 0) : source;
        PoolResolver pool = new(new Dictionary<int, QpdfObject> { [1] = source, [2] = new PdfInteger("2", 2) });
        PdfDictionary annotation = new(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Line"), [PdfName.FromAscii("Cap")] = new PdfBoolean(caption) });
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None))
        {
            QpdfObject result = AnnotationCoordinates.TransformEntry(annotation, PdfName.FromAscii("CO"), raw, [0, -2, 2, 0, 17, 23], pool, graph);
            if (caption) { Assert.Equal(new double[] { 4, -6 }, Assert.IsType<PdfArray>(result).Items.Select(x => Assert.IsType<PdfReal>(x).Value)); }
            else { Assert.Same(raw, result); }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("LL")]
    [InlineData("CO")]
    public void UnrecognizedRolesKeepTheirOpaqueValues(string key)
    {
        QpdfObject raw = PdfName.FromAscii("Opaque");
        PdfDictionary annotation = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Private") });
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        Assert.Same(raw, AnnotationCoordinates.TransformEntry(annotation, PdfName.FromAscii(key), raw,
            [0, -2, 2, 0, 17, 23], new(new Dictionary<int, QpdfObject>()), graph));
    }

    [Theory]
    [InlineData("LL", false)]
    [InlineData("LL", true)]
    [InlineData("CO", false)]
    [InlineData("CO", true)]
    public void NullAndZeroDistancesNeedNoFreshStorage(string key, bool absent)
    {
        QpdfObject raw = absent ? PdfNull.Instance : key is "LL" ? new PdfInteger("0", 0)
            : new PdfArray([new PdfInteger("0", 0), new PdfInteger("0", 0)]);
        PdfDictionary annotation = new(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Line"), [PdfName.FromAscii("Cap")] = new PdfBoolean(true) });
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        long before = governor.Snapshot().LiveMemoryBytes;
        Assert.Same(raw, AnnotationCoordinates.TransformEntry(annotation, PdfName.FromAscii(key), raw,
            [0, -2, 2, 0, 17, 23], new(new Dictionary<int, QpdfObject>()), graph));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("LL", "malformed")]
    [InlineData("CO", "malformed")]
    [InlineData("LL", "refuse")]
    [InlineData("CO", "refuse")]
    [InlineData("LL", "caller")]
    [InlineData("CO", "caller")]
    [InlineData("LL", "governor")]
    [InlineData("CO", "governor")]
    public void MeaningfulDistancesPreserveFailureAndCleanup(string key, string failure)
    {
        QpdfObject raw = key is "LL" ? failure is "malformed" ? PdfName.FromAscii("Bad") : new PdfInteger("1", 1)
            : failure is "malformed" ? new PdfArray([new PdfInteger("1", 1)]) : new PdfArray([new PdfInteger("1", 1), new PdfInteger("2", 2)]);
        PdfDictionary annotation = new(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Line"), [PdfName.FromAscii("Cap")] = new PdfBoolean(true) });
        using CancellationTokenSource caller = new(), operation = new();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = failure is "refuse" ? 128L : 4096L }, operation.Token);
        using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, caller.Token))
        {
            if (failure is "caller") { caller.Cancel(); }
            if (failure is "governor") { operation.Cancel(); }
            Action transform = () => AnnotationCoordinates.TransformEntry(annotation, PdfName.FromAscii(key), raw,
                [0, -2, 2, 0, 17, 23], new(new Dictionary<int, QpdfObject>()), graph);
            if (failure is "malformed") { Assert.Throws<QpdfParseException>(transform); }
            else if (failure is "refuse") { Assert.Throws<QpdfLimitException>(transform); }
            else { Assert.ThrowsAny<OperationCanceledException>(transform); }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
