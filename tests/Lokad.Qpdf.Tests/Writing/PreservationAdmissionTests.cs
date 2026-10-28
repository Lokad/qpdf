using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class PreservationAdmissionTests
{
    internal static byte[] Scalars(int count)
    {
        var objects = CheckTests.SinglePage("<< /Length 3 >>", "q Q"u8.ToArray());
        for (int i = 0; i < count; i++) { objects.Add((i + 6, i.ToString(System.Globalization.CultureInfo.InvariantCulture), null)); }
        return CheckTests.Assemble(objects, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyCollectionsRefuseBeforeLargeUnadmittedGrowth(bool collectIds)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Scalars(1000), QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        for (int i = 6; i < 1006; i++) { _ = document.GetObject(i); }
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, limits, 10000, owner);
            HashSet<int>? roots = collectIds ? ParserContainers.CreateSet<int>(owner) : null;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => copier.PreserveUnreferencedObjects(document.Engine,
                new HashSet<int> { 1, 2, 3, 4, 5 }, new(governor), roots, CancellationToken.None));
            Assert.Contains(nameof(QpdfLimits.MaxMemoryBytes), failure.Message);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0L, 65536L);
            Assert.Empty(copier.CopiedObjects); Assert.Empty(copier.SourceToDestination);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulScalarCopiesKeepTheirTableCapacityAdmitted(bool collectIds)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Scalars(1000), QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, QpdfLimits.Default, 10000, owner);
            HashSet<int>? roots = collectIds ? ParserContainers.CreateSet<int>(owner) : null;
            copier.PreserveUnreferencedObjects(document.Engine, new HashSet<int> { 1, 2, 3, 4, 5 }, new(governor), roots, CancellationToken.None);
            Assert.Equal(1000, copier.CopiedObjects.Count); Assert.Equal(1000, copier.SourceToDestination.Count);
            if (roots is not null) { Assert.Equal(1000, roots.Count); }
            Assert.True(governor.Snapshot().LiveMemoryBytes > 65536L);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class CancelingObjects(Action cancel) : Dictionary<int, QpdfObject>, IReadOnlyDictionary<int, QpdfObject>
    {
        bool IReadOnlyDictionary<int, QpdfObject>.TryGetValue(int key, out QpdfObject value)
        {
            bool found = TryGetValue(key, out QpdfObject? result);
            if (key == 6) { cancel(); }
            value = result ?? PdfNull.Instance; return found;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EitherCancellationTokenStopsAfterResolvingTheLastCandidate(bool caller)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Scalars(1), QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        document.Engine.SetInspectionObjects(new CancelingObjects(cancellation.Cancel) { [6] = new PdfInteger("0", 0L) });
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, QpdfLimits.Default, 10000, owner);
        long before = governor.Snapshot().LiveMemoryBytes;
        Assert.ThrowsAny<OperationCanceledException>(() => copier.PreserveUnreferencedObjects(document.Engine,
            new HashSet<int> { 1, 2, 3, 4, 5 }, new(governor), null, caller ? cancellation.Token : CancellationToken.None));
        Assert.Empty(copier.CopiedObjects); Assert.Empty(copier.SourceToDestination); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }
}
