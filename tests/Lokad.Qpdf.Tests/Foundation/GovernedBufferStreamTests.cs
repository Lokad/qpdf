using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class GovernedBufferStreamTests
{
    [Fact]
    public void ReturnedCopyAndWorkingCapacityCoexistWithinTheBudget()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] payload = new byte[16384];
        new Random(17).NextBytes(payload);
        byte[] result;
        using (GovernedBufferStream stream = new(governor, 65536L, nameof(QpdfLimits.MaxOutputBytes)))
        {
            stream.Write(payload);
            result = stream.ToArray();
            Assert.Equal(payload, result);
            Assert.True(governor.Snapshot().LiveMemoryBytes >= 2L * payload.Length);
        }

        Assert.Equal(result.Length + GovernedBufferStream.ArrayOverheadBytes, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(result);
    }

    [Fact]
    public void GrowthRejectsBeforeAllocatingAndRetainsTheOldContents()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 9000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        byte[] first = new byte[1024];
        byte[] second = new byte[8192];
        using (GovernedBufferStream stream = new(governor, 65536L, nameof(QpdfLimits.MaxOutputBytes)))
        {
            stream.Write(first);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<QpdfLimitException>(() => stream.Write(second));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 8192L, $"Rejected growth allocated {allocated} bytes.");
            Assert.Equal(1024L, stream.Length);
            Assert.True(governor.Snapshot().LiveMemoryBytes <= limits.MaxMemoryBytes);
        }

        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ReturnedCopyRequiresAdmissionWhileTheWorkingArrayIsLive()
    {
        QpdfLimits limits = QpdfLimits.Default with
        {
            MaxMemoryBytes = 8192L + GovernedBufferStream.ArrayOverheadBytes,
        };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using (GovernedBufferStream stream = new(governor, 65536L, nameof(QpdfLimits.MaxOutputBytes)))
        {
            stream.Write(new byte[1024]);
            Assert.Throws<QpdfLimitException>(() => stream.ToArray());
        }

        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void SeekingAndLengthChangesZeroGapsAndPreserveOverwrites()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using GovernedBufferStream stream = new(governor, 65536L, nameof(QpdfLimits.MaxOutputBytes));
        stream.Write(new byte[] { 1, 2, 3 });
        stream.Seek(5L, SeekOrigin.Begin);
        stream.Write(new byte[] { 6 });
        stream.SetLength(8L);
        Assert.Equal(new byte[] { 1, 2, 3, 0, 0, 6, 0, 0 }, stream.ToArray());
        stream.Seek(1L, SeekOrigin.Begin);
        stream.Write(new byte[] { 9 });
        stream.SetLength(3L);
        Assert.Equal(new byte[] { 1, 9, 3 }, stream.ToArray());
    }

    [Fact]
    public void LargeGapsAndTruncatedStorageAreZeroedAcrossCapacityGrowth()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using GovernedBufferStream stream = new(governor, 65536L, nameof(QpdfLimits.MaxOutputBytes));
        byte[] dirty = new byte[16384]; Array.Fill(dirty, (byte)0xA5);
        stream.Write(dirty);
        stream.SetLength(4L);
        stream.SetLength(12000L);
        byte[] reextended = stream.ToArray();
        Assert.Equal(dirty.AsSpan(0, 4).ToArray(), reextended.AsSpan(0, 4).ToArray());
        Assert.True(reextended.AsSpan(4).IndexOfAnyExcept((byte)0) < 0);
        stream.Seek(20000L, SeekOrigin.Begin);
        stream.Write([7, 8]);
        byte[] grown = stream.ToArray();
        Assert.Equal(20002, grown.Length);
        Assert.True(grown.AsSpan(4, 19996).IndexOfAnyExcept((byte)0) < 0);
        Assert.Equal(new byte[] { 7, 8 }, grown.AsSpan(20000).ToArray());
    }

    [Fact]
    public void OutputLimitRejectsBeforeAllocating()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using GovernedBufferStream stream = new(governor, 3L, nameof(QpdfLimits.MaxOutputBytes));
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => stream.Write(new byte[4]));
        Assert.Equal(nameof(QpdfLimits.MaxOutputBytes), error.LimitName);
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
    }

    [Fact]
    public void CanceledWriteAllocatesNothing()
    {
        using CancellationTokenSource source = new();
        ResourceGovernor governor = new(QpdfLimits.Default, source.Token);
        using GovernedBufferStream stream = new(governor, 65536L, nameof(QpdfLimits.MaxOutputBytes));
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => stream.Write(new byte[4]));
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
    }
}
