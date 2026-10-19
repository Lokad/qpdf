using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class SelectionStorageAdmissionTests
{
    [Theory]
    [InlineData("normal")]
    [InlineData("memory")]
    [InlineData("canceled")]
    public void ScopedMetadataPreservesMaterialAdmissionAndImmutableCopies(string mode)
    {
        QpdfLimits limits = mode == "memory" ? QpdfLimits.Default with { MaxMemoryBytes = 4096L }
            : QpdfLimits.Default;
        ResourceGovernor governor = new(limits, CancellationToken.None);
        int[] source = Enumerable.Range(1, mode == "memory" ? 4096 : 256).ToArray();
        IReadOnlyList<int>? result = null;
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            if (mode == "canceled")
            {
                Assert.ThrowsAny<OperationCanceledException>(() => ResultCollectionStorage.Copy(memory, source,
                    sizeof(int), governor, new CancellationToken(true)));
            }
            else if (mode is "memory" or "work")
            {
                QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => ResultCollectionStorage.Copy(memory,
                    source, sizeof(int), governor, CancellationToken.None));
                Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
            }
            else
            {
                result = ResultCollectionStorage.Copy(memory, source, sizeof(int), governor, CancellationToken.None);
                Assert.True(governor.Snapshot().LiveMemoryBytes >= source.Length * sizeof(int));
                source[0] = 99;
                Assert.Throws<NotSupportedException>(() => ((IList<int>)result)[0] = 99);
            }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
        if (result is not null) { Assert.Equal(1, result[0]); Assert.Equal(256, result[^1]); }
    }
}
