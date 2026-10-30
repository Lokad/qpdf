using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class JpegBudgetFallbackTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void RefusedCodecProbeAllowsAffordableFallbackAndOutput(bool decode, bool shared, bool growth)
    {
        byte[] jpeg = DctDecodeTests.EncodeGrayJpeg(8, 8);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = shared ? 100000L : 16000L };
        RecordingMemoryBudget root = new(shared ? 16384L : 100000L);
        using IMemoryScope invocation = root.CreateScope();
        ResourceGovernor governor = new(limits, CancellationToken.None, new Host(invocation))
        { JpegCodec = new FallbackCodec(jpeg, growth) };
        byte[] result = decode ? DctFilter.Decode(jpeg, limits, governor)
            : JpegCodec.EncodeGray(new byte[64], 8, 8, 75, limits, governor);
        if (decode) { Assert.All(result, value => Assert.Equal((byte)123, value)); }
        else { Assert.Equal(jpeg, result); }
        long retained = result.Length + JpegCodec.SampleArrayOverheadBytes;
        Assert.Equal(retained, governor.Snapshot().LiveMemoryBytes);
        governor.ReleaseMemory(retained);
        Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0, root.LiveReservations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OversizedCodecEstimatesRemainMemoryFailures(bool decode, bool growth)
    {
        byte[] jpeg = DctDecodeTests.EncodeGrayJpeg(8, 8);
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope invocation = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new Host(invocation))
        { JpegCodec = new OversizedCodec(growth) };
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => decode
            ? DctFilter.Decode(jpeg, QpdfLimits.Default, governor)
            : JpegCodec.EncodeGray(new byte[64], 8, 8, 75, QpdfLimits.Default, governor));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(long.MaxValue, failure.Requested);
        Assert.Equal(QpdfLimits.Default.MaxMemoryBytes, failure.Allowed);
        Assert.Equal(0, root.ResizeCalls);
        Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class OversizedCodec(bool growth) : IQpdfJpegCodec
    {
        public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context) => Fail(context);
        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context) => Fail(context);
        private void Fail(QpdfJpegCodecContext context)
        {
            using IMemoryScope owner = context.MemoryBudget.CreateScope();
            Assert.True(owner.TryReserve(1L, out IMemoryReservation? receipt));
            if (growth) { Assert.False(receipt.TryResize(long.MaxValue)); }
            else { Assert.False(owner.TryReserve(long.MaxValue, out _)); }
            throw new InvalidDataException("Nested codec refused its estimate.");
        }
    }

    private sealed class Host(IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class FallbackCodec(byte[] jpeg, bool growth) : IQpdfJpegCodec
    {
        public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
        { Probe(context); samples.Span.Fill(123); }
        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
        { Probe(context); encoded.Write(jpeg); }
        private void Probe(QpdfJpegCodecContext context)
        {
            using IMemoryScope owner = context.MemoryBudget.CreateScope();
            Assert.True(owner.TryReserve(1000L, out IMemoryReservation? receipt));
            if (growth) { Assert.False(receipt.TryResize(32000L)); }
            else { Assert.False(owner.TryReserve(32000L, out _)); }
            Assert.Equal(1000L, receipt.Bytes);
            Assert.True(owner.TryReserve(1L, out _));
            Assert.True(owner.TryReserve(2048L, out _));
        }
    }
}
