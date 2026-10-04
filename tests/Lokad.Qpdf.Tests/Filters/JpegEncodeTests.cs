using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class JpegEncodeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void EncodingLeavesOnlyTheReturnedBytesCharged(int components)
    {
        byte[] raw = new byte[32 * 24 * components];
        new Random(19).NextBytes(raw);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] encoded = components == 1
            ? JpegCodec.EncodeGray(raw, 32, 24, 75, QpdfLimits.Default, governor)
            : JpegCodec.EncodeRgb(raw, 32, 24, 75, QpdfLimits.Default, governor);
        Assert.True(DctFilter.TryParseFrame(encoded, out int width, out int height, out int actualComponents));
        Assert.Equal(32, width);
        Assert.Equal(24, height);
        Assert.Equal(components, actualComponents);
        Assert.Equal(encoded.Length + GovernedBufferStream.ArrayOverheadBytes, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().TotalDecodedBytes);
        Assert.True(governor.Snapshot().PeakMemoryBytes > raw.Length + encoded.Length);
        GC.KeepAlive(encoded);
    }

    [Fact]
    public void EncodedOutputLimitReleasesAllWorkingOwners()
    {
        byte[] raw = new byte[32 * 32];
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(
            () => JpegCodec.EncodeGray(raw, 32, 32, 75, limits, governor));
        Assert.Equal(nameof(QpdfLimits.MaxOutputBytes), error.LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void TightMemoryAdmissionPrecedesFrameAllocation()
    {
        byte[] raw = new byte[1024 * 1024];
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => JpegCodec.EncodeGray(raw, 1024, 1024, 75, limits, governor));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 65536L, $"Rejected encoding allocated {allocated} bytes.");
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
