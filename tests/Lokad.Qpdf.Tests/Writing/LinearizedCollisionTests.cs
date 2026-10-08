using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LinearizedCollisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BorrowedLookupAndOwnerIndexesPreserveCallerStorageOnSuccessOrWorkFailure(bool fail)
    {
        List<NumberedObject> model = LinearizedStreamedPartsTests.OnePageModel();
        Dictionary<int, int> owners = new() { [5] = 6 };
        for (int index = 1; index <= 512; index++)
        {
            int number = index * 919 * 1024;
            model.Add(new NumberedObject(number, PdfNull.Instance));
            owners.Add(number, 6);
        }
        Dictionary<int, QpdfObject> lookup = model.ToDictionary(item => item.Number, item => item.Value);
        QpdfLimits limits = fail ? QpdfLimits.Default with { MaxWork = 220000L } : QpdfLimits.Default;
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(97L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        if (fail)
        {
            QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
                LinearizedParts.ClassifyGraph(lookup, 1, owners, new[] { 6 }, memory, governor, CancellationToken.None));
            Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
            Assert.InRange(governor.Snapshot().TotalWork, 180000L, limits.MaxWork);
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(97L, memory.ReservedBytes);
        }
        else
        {
            using ClassifiedGraph graph = LinearizedParts.ClassifyGraph(lookup, 1, owners, new[] { 6 }, memory, governor, CancellationToken.None);
            Assert.Equal(new[] { 3, 4, 6 }, graph.Part6);
            Assert.Equal(new[] { 2 }, graph.Part9);
            Assert.True(governor.Snapshot().TotalWork > 250000L);
        }
        Assert.Equal(517, lookup.Count);
        Assert.Equal(513, owners.Count);
        Assert.All(owners.Values, container => Assert.Equal(6, container));
        Assert.Same(model[3].Value, lookup[4]);
        memory.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("classic")]
    [InlineData("streamed")]
    public void CollisionWorkExhaustionReturnsAttemptStorageWithoutTouchingBorrowedValues(string route)
    {
        List<NumberedObject> model = LinearizedStreamedPartsTests.OnePageModel();
        // 919 is the preceding BCL capacity; 1024 is the governed capacity.
        // These identifiers collide in both indexes at the final size.
        for (int index = 1; index <= 512; index++) { model.Add(new NumberedObject(index * 919 * 1024, PdfNull.Instance)); }
        Dictionary<int, QpdfObject> lookup = model.ToDictionary(item => item.Number, item => item.Value);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxWork = 65536L }, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(97L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
        {
            using IDisposable result = route switch
            {
                "graph" => LinearizedParts.ClassifyGraph(lookup, 1, memory, governor, CancellationToken.None),
                "classic" => LinearizedParts.Classify(model, memory, governor, CancellationToken.None),
                "streamed" => LinearizedParts.ClassifyStreamed(model, new IReadOnlyList<int>[] { new[] { 5 } }, memory, governor, CancellationToken.None),
                _ => throw new ArgumentOutOfRangeException(nameof(route)),
            };
        });
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        Assert.Equal(65536L, governor.Snapshot().TotalWork);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(97L, memory.ReservedBytes);
        Assert.True(governor.Snapshot().PeakMemoryBytes > baseline);
        Assert.Same(model[3].Value, lookup[4]);
        Assert.Equal("BT (Hi) Tj ET"u8.ToArray(), ((PdfStream)lookup[4]).Data);
    }
}
