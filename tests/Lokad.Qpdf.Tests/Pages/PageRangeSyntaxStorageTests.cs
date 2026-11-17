using Lokad.Qpdf.Pages;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Pages;

public sealed class PageRangeSyntaxStorageTests
{
    private static string Dense() => string.Join(',', Enumerable.Repeat("1", 2048));

    [Fact]
    public void DenseValidationDoesNotBuildSyntaxGraphs()
    {
        string range = Dense(); Assert.Equal(4095, range.Length);
        for (int i = 0; i < 64; i++) { PageRanges.CheckSyntax(range); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++) { PageRanges.CheckSyntax(range); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0L, 32768L);
        Assert.Throws<QpdfParseException>(() => PageRanges.CheckSyntax(range[..^1] + "q"));
    }

    [Fact]
    public void SyntaxAdmissionRefusalLeavesPriorAndOutputStorageUntouched()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(17L);
        long before = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => PageRanges.Expand(Dense(), 1, 2048L, governor, memory));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(17L, memory.ReservedBytes); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, before, 4096L);
    }

    [Fact]
    public void SuccessfulExpansionRetainsOnlyTheResultList()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            IReadOnlyList<int> result = PageRanges.Expand(Dense(), 1, 2048L, governor, memory);
            Assert.Equal(2048, result.Count); Assert.All(result, value => Assert.Equal(1, value));
            Assert.InRange(memory.ReservedBytes, 8192L, 32768L);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void CompleteSyntaxValidationPrecedesBoundsAndRetiresFailedPreparation()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(71L);
        long before = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfParseException>(() => PageRanges.Expand("2,bad", 1, 10L, governor, memory));
        Assert.Equal(71L, memory.ReservedBytes); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }
}
