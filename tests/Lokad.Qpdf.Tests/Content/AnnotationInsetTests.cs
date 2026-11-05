using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AnnotationInsetTests
{
    public static IEnumerable<object[]> Frames()
    {
        foreach (string subtype in new[] { "FreeText", "Square", "Circle", "Caret" })
        {
            yield return [subtype, new double[] { 2, 0, 0, 2, 17, 23 }, new double[] { 2, 4, 6, 8 }];
            yield return [subtype, new double[] { 0, -2, 2, 0, 17, 23 }, new double[] { 8, 2, 4, 6 }];
            yield return [subtype, new double[] { -2, 0, 0, -2, 17, 23 }, new double[] { 6, 8, 2, 4 }];
            yield return [subtype, new double[] { 0, 2, -2, 0, 17, 23 }, new double[] { 4, 6, 8, 2 }];
        }
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public void InnerRectangleFollowsTheFrameWithoutChangingOriginalInsets(string subtype, double[] matrix, double[] expected)
    {
        PdfArray source = new([new PdfReference(2, 0), new PdfInteger("2", 2), new PdfReal("3.0", 3), new PdfInteger("4", 4)]);
        PoolResolver pool = new(new Dictionary<int, QpdfObject> { [1] = source, [2] = new PdfInteger("1", 1) });
        PdfDictionary annotation = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Subtype")] = PdfName.FromAscii(subtype) });
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None))
        {
            PdfArray transformed = Assert.IsType<PdfArray>(AnnotationCoordinates.TransformEntry(annotation, PdfName.FromAscii("RD"), new PdfReference(1, 0), matrix, pool, graph));
            Assert.Equal(expected, transformed.Items.Select(item => Assert.IsType<PdfReal>(item).Value));
            Assert.IsType<PdfReference>(source[0]); Assert.Equal(2L, Assert.IsType<PdfInteger>(source[1]).Value);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("FreeText", true)]
    [InlineData("Square", true)]
    [InlineData("Circle", true)]
    [InlineData("Caret", true)]
    [InlineData("Stamp", false)]
    [InlineData("Private", false)]
    public void OptionalNullAndUnrecognizedInsetRolesStayBorrowed(string subtype, bool recognized)
    {
        QpdfObject raw = recognized ? PdfNull.Instance : PdfName.FromAscii("Opaque");
        PdfReference link = new(1, 0);
        PoolResolver pool = new(new Dictionary<int, QpdfObject> { [1] = raw });
        PdfDictionary annotation = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Subtype")] = PdfName.FromAscii(subtype) });
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None))
        {
            long before = governor.Snapshot().LiveMemoryBytes;
            Assert.Same(link, AnnotationCoordinates.TransformEntry(annotation, PdfName.FromAscii("RD"), link, [0, -2, 2, 0, 17, 23], pool, graph));
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void TranslationDoesNotInterpretOrAllocateInsetValues()
    {
        QpdfObject raw = PdfName.FromAscii("Opaque");
        PdfDictionary annotation = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Subtype")] = PdfName.FromAscii("FreeText") });
        PoolResolver pool = new(new Dictionary<int, QpdfObject>());
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        long before = governor.Snapshot().LiveMemoryBytes;
        Assert.Same(raw, AnnotationCoordinates.TransformEntry(annotation, PdfName.FromAscii("RD"), raw, [1, 0, 0, 1, 17, 23], pool, graph));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("shape")]
    [InlineData("number")]
    [InlineData("refuse")]
    [InlineData("caller")]
    [InlineData("governor")]
    public void ChangedInsetsKeepMalformedRefusalCancellationAndCleanup(string failure)
    {
        PdfArray source = failure is "shape" ? new([new PdfInteger("1", 1)])
            : new([new PdfInteger("1", 1), failure is "number" ? PdfName.FromAscii("Bad") : new PdfInteger("2", 2), new PdfInteger("3", 3), new PdfInteger("4", 4)]);
        PdfDictionary annotation = new(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Square") });
        PoolResolver pool = new(new Dictionary<int, QpdfObject>());
        using CancellationTokenSource caller = new(), operation = new();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = failure is "refuse" ? 128L : 4096L }, operation.Token);
        using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, caller.Token))
        {
            if (failure is "caller") { caller.Cancel(); }
            if (failure is "governor") { operation.Cancel(); }
            Action transform = () => AnnotationCoordinates.TransformEntry(annotation, PdfName.FromAscii("RD"), source,
                [0, -2, 2, 0, 17, 23], pool, graph);
            if (failure is "shape" or "number") { Assert.Throws<QpdfParseException>(transform); }
            else if (failure is "refuse") { Assert.Throws<QpdfLimitException>(transform); }
            else { Assert.ThrowsAny<OperationCanceledException>(transform); }
            Assert.Equal(1L, Assert.IsType<PdfInteger>(source[0]).Value);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
