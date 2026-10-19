using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class FlateMemoryTests
{
    private static byte[] Payload(int length)
    {
        byte[] bytes = new byte[length];
        new Random(31).NextBytes(bytes);
        return bytes;
    }

    private static byte[] Compress(byte[] payload)
    {
        return FlateFilter.Encode(payload, null, QpdfLimits.Default,
            new ResourceGovernor(QpdfLimits.Default, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TightBudgetRejectsBeforeCodecAndOutputAllocation(bool decode)
    {
        byte[] input = Payload(256 * 1024);
        byte[] compressed = Compress(input);
        // Warm both paths so first-use codec initialization is outside this probe.
        FlateFilter.Decode(compressed, null, QpdfLimits.Default,
            new ResourceGovernor(QpdfLimits.Default, CancellationToken.None));
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        long before = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
        {
            if (decode)
            {
                FlateFilter.Decode(compressed, null, limits, governor);
            }
            else
            {
                FlateFilter.Encode(input, null, limits, governor);
            }
        });
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), error.LimitName);
        Assert.True(allocated < 65536L, $"Rejected codec allocated {allocated} bytes.");
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyReturnedArrayRemainsCharged(bool decode)
    {
        byte[] raw = Payload(100000);
        byte[] input = decode ? Compress(raw) : raw;
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] result = decode
            ? FlateFilter.Decode(input, null, QpdfLimits.Default, governor)
            : FlateFilter.Encode(input, null, QpdfLimits.Default, governor);
        byte[] roundtrip = decode ? result : FlateFilter.Decode(result, null, QpdfLimits.Default,
            new ResourceGovernor(QpdfLimits.Default, CancellationToken.None));
        Assert.Equal(raw, roundtrip);
        Assert.Equal(result.Length + GovernedBufferStream.ArrayOverheadBytes, governor.Snapshot().LiveMemoryBytes);
        long state = decode ? FlateFilter.InflateWorkingSetBytes : FlateFilter.DeflateWorkingSetBytes;
        Assert.True(governor.Snapshot().PeakMemoryBytes >= state + 2L * result.Length);
        Assert.Equal(decode ? result.Length : 0L, governor.Snapshot().TotalDecodedBytes);
        GC.KeepAlive(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReturnedCopyNeedsMemoryWhileWorkingCapacityIsLive(bool decode)
    {
        byte[] raw = Payload(1024);
        byte[] input = decode ? Compress(raw) : raw;
        long state = decode
            ? FlateFilter.InflateWorkingSetBytes + 8192L + GovernedBufferStream.ArrayOverheadBytes
            : FlateFilter.DeflateWorkingSetBytes;
        QpdfLimits limits = QpdfLimits.Default with
        {
            MaxMemoryBytes = state + 8192L + GovernedBufferStream.ArrayOverheadBytes,
        };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
        {
            if (decode)
            {
                FlateFilter.Decode(input, null, limits, governor);
            }
            else
            {
                FlateFilter.Encode(input, null, limits, governor);
            }
        });
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), error.LimitName);
        Assert.Equal(limits.MaxMemoryBytes, governor.Snapshot().PeakMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void BadChecksumReleasesAllWorkingMemory()
    {
        byte[] compressed = Compress(Payload(40000));
        compressed[^1] ^= 1;
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Throws<QpdfFilterException>(() => FlateFilter.Decode(compressed, null, QpdfLimits.Default, governor));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ExpansionFailureReleasesAllWorkingMemory()
    {
        byte[] compressed = Compress(new byte[40000]);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = 10000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => FlateFilter.Decode(compressed, null, limits, governor));
        Assert.Equal(nameof(QpdfLimits.MaxDecodedBytes), error.LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyCompressionChecksBudgetsBeforeAllocation(bool outputLimit)
    {
        QpdfLimits limits = outputLimit
            ? QpdfLimits.Default with { MaxOutputBytes = 7L }
            : QpdfLimits.Default with { MaxMemoryBytes = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => FlateFilter.Encode([], null, limits, governor));
        Assert.Equal(outputLimit ? nameof(QpdfLimits.MaxOutputBytes) : nameof(QpdfLimits.MaxMemoryBytes), error.LimitName);
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void EmptyCompressionStillValidatesLevel(int level)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Throws<ArgumentOutOfRangeException>(() => FlateFilter.Encode([], level, QpdfLimits.Default, governor));
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
    }
}
