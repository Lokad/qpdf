using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class GovernedCollectionTests
{
    [Fact]
    public void NameComparisonMatchesOrdinalDisplayForEverySingleByte()
    {
        PdfName[] names = Enumerable.Range(0, 256).Select(value => new PdfName([(byte)value])).ToArray();
        string[] displays = names.Select(name => name.DisplayText).ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        for (int left = 0; left < names.Length; left++)
        {
            for (int right = 0; right < names.Length; right++)
            {
                Assert.Equal(Math.Sign(string.CompareOrdinal(displays[left], displays[right])),
                    Math.Sign(GovernedCollections.CompareNames(governor, names[left], names[right])));
            }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void MixedEscapedPrefixesSortWithoutDisplayAllocation()
    {
        PdfName[] names = [new([]), new([35]), new([35, 50, 51]), new([0]), new([255]), new([65, 0]),
            new([65]), new([65, 32]), new([126]), new([127]), new([35, 0])];
        PdfName[] expected = names.OrderBy(name => name.DisplayText, StringComparer.Ordinal).ToArray();
        List<PdfName> actual = new(names);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        GovernedCollections.SortNames(memory, governor, actual);
        Assert.Equal(expected, actual);
        Assert.Equal(64L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void LongEscapedComparisonAllocatesNoFormattedText()
    {
        PdfName left = new(Enumerable.Repeat((byte)35, 4096).ToArray());
        PdfName right = new(Enumerable.Repeat((byte)35, 4096).ToArray());
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using IDisposable capture = OwnershipBatch.CaptureWithoutCollection();
        // Warm the same comparison path before observing exact thread-local
        // allocation, inside the isolated sampling phase.
        for (int warmup = 0; warmup < 64; warmup++)
        { Assert.Equal(0, GovernedCollections.CompareNames(governor, left, right)); }
        Assert.Equal(0, GovernedCollections.CompareNames(governor, left, right));
        long before = GC.GetAllocatedBytesForCurrentThread();
        int result = 0;
        for (int iteration = 0; iteration < 64; iteration++) { result |= GovernedCollections.CompareNames(governor, left, right); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, result);
        Assert.Equal(0L, allocated);
        Assert.True(governor.Snapshot().TotalWork >= 64L * 4096L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SortFailuresReturnComparerAdmissionAndExposeTheOriginalLimit(bool names)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 4L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        QpdfLimitException failure = names
            ? Assert.Throws<QpdfLimitException>(() => GovernedCollections.SortNames(memory, governor,
                Enumerable.Range(0, 32).Reverse().Select(value => new PdfName([(byte)(value + 33)])).ToList()))
            : Assert.Throws<QpdfLimitException>(() => GovernedCollections.SortNumbers(memory, governor,
                Enumerable.Range(0, 32).Reverse().ToList()));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        Assert.Equal(64L, governor.Snapshot().LiveMemoryBytes);
        Assert.InRange(governor.Snapshot().TotalWork, 1L, limits.MaxWork);
    }

    [Fact]
    public void ComparerCreationFailurePreservesBorrowedItemsAndCanRetry()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 128L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        governor.ReserveMemory(33L);
        List<int> values = [3, 1, 2];
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => GovernedCollections.SortNumbers(memory, governor, values));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(new[] { 3, 1, 2 }, values);
        Assert.Equal(97L, governor.Snapshot().LiveMemoryBytes);
        governor.ReleaseMemory(33L);
        GovernedCollections.SortNumbers(memory, governor, values);
        Assert.Equal(new[] { 1, 2, 3 }, values);
        Assert.Equal(64L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void CanceledSortingReleasesItsComparerWithoutChangingAcceptedStorage()
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        List<int> values = [3, 1, 2];
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => GovernedCollections.SortNumbers(memory, governor, values));
        Assert.Equal(new[] { 3, 1, 2 }, values);
        Assert.Equal(64L, governor.Snapshot().LiveMemoryBytes);
    }
}
