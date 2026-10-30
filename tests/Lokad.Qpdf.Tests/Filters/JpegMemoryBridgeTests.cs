using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Image;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using ImageReceipt = Lokad.Image.IMemoryReservation;
using ImageScope = Lokad.Image.IMemoryScope;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class JpegMemoryBridgeTests
{
    [Fact]
    public void AncestorClosureRetiresResizedDescendantsAndKeepsSiblingStorage()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfJpegCodecContext context = new(governor);
        using IMemoryScope owner = context.MemoryBudget.CreateScope();
        JpegMemoryBudget budget = new(owner);
        using ImageScope first = budget.CreateScope(), second = budget.CreateScope(), descendant = first.CreateScope();
        Assert.True(first.TryReserve(1000L, out ImageReceipt? a));
        Assert.True(descendant.TryReserve(2000L, out ImageReceipt? b));
        Assert.True(second.TryReserve(3000L, out ImageReceipt? c));
        Assert.True(a.TryResize(60000L)); Assert.Equal(60000L, a.Bytes);
        Assert.True(b.TryResize(0L)); Assert.True(b.TryResize(4000L));
        first.Dispose(); first.Dispose();
        Assert.Equal(0L, a.Bytes); Assert.Equal(0L, b.Bytes);
        Assert.Equal(3000L, c.Bytes); Assert.Equal(3000L, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => descendant.TryReserve(1L, out _));
        Assert.Throws<ObjectDisposedException>(() => a.TryResize(1L));
        a.Dispose(); b.Dispose(); owner.Dispose(); c.Dispose();
        Assert.Equal(0L, c.Bytes); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => budget.CreateScope());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BridgeRefusalIsAtomicAndRestoresTheOriginalFailure(bool shared)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = shared ? 100000L : 16000L };
        RecordingMemoryBudget root = new(shared ? 16000L : 100000L);
        using InMemoryQpdfHost host = new(limits, null, root);
        ResourceGovernor governor = new(limits, CancellationToken.None, host);
        QpdfJpegCodecContext context = new(governor);
        using IMemoryScope owner = context.MemoryBudget.CreateScope();
        JpegMemoryBudget budget = new(owner);
        Assert.True(budget.TryReserve(1000L, out ImageReceipt? receipt));
        Assert.False(receipt.TryResize(32000L)); Assert.Equal(1000L, receipt.Bytes);
        Assert.Equal(1000L, governor.Snapshot().LiveMemoryBytes);
        Exception? failure = Record.Exception(() => context.RethrowMemoryRefusal());
        if (shared) { Assert.IsType<QpdfMemoryAdmissionException>(failure); }
        else { Assert.IsType<QpdfLimitException>(failure); }
        Assert.True(receipt.TryResize(0L)); receipt.Dispose();
        Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostFaultsAndOomEscapeWithoutBecomingImageRefusal(bool oom)
    {
        Exception expected = oom ? new OutOfMemoryException("injected") : new InvalidOperationException("host invariant");
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, new FaultBudget(expected));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
        QpdfJpegCodecContext context = new(governor);
        using IMemoryScope owner = context.MemoryBudget.CreateScope();
        JpegMemoryBudget budget = new(owner);
        Assert.Same(expected, Record.Exception(() => budget.TryReserve(1000L, out _)));
        context.RethrowMemoryRefusal();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void CancellationEscapesAdmissionWhileShrinkingAndDisposalRemainUsable()
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        QpdfJpegCodecContext context = new(governor);
        using IMemoryScope owner = context.MemoryBudget.CreateScope();
        JpegMemoryBudget budget = new(owner);
        Assert.True(budget.TryReserve(1000L, out ImageReceipt? receipt)); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => budget.TryReserve(1L, out _));
        Assert.ThrowsAny<OperationCanceledException>(() => receipt.TryResize(2000L));
        Assert.Equal(1000L, receipt.Bytes); Assert.True(receipt.TryResize(100L));
        receipt.Dispose(); context.RethrowMemoryRefusal();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InputBorrowingOrCopyingPreservesPositionAndStreamOwnership(bool visible)
    {
        byte[] bytes = new byte[1008]; new Random(17).NextBytes(bytes);
        using MemoryStream source = new(bytes, 8, 1000, false, visible); source.Position = 20L;
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (JpegInput input = JpegInput.Read(source, new(governor)))
        {
            Assert.Equal(bytes.AsSpan(28, 980).ToArray(), input.Memory.ToArray());
            Assert.Equal(visible ? 0L : 1012L, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(source.Length, source.Position);
        }
        Assert.True(source.CanRead); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void UnseekableGrowthAdmitsBothBuffersAndRefundsAfterRefusal()
    {
        using ChunkedInput source = new(new byte[30000], 8192);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 20000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        Assert.Throws<QpdfMemoryAdmissionException>(() => JpegInput.Read(source, new(governor)));
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 8192L, 20000L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void AdapterSupportsShortUnseekableReadsWithoutRetainingPrivateStorage(int components)
    {
        byte[] jpeg = JpegFixtures.Encode(17, 11, components, 75, (_, _, _) => 128);
        using ChunkedInput input = new(jpeg, 7);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] samples = new byte[17 * 11 * components];
        LokadImageJpegCodec.Instance.Decode(input, new(17, 11, components), samples, new(governor));
        Assert.Equal(-1, samples.AsSpan().IndexOfAnyExcept((byte)128));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.True(input.CanRead);
    }

    [Fact]
    public void ExifOrientationDoesNotRotatePdfSamples()
    {
        byte[] jpeg = JpegFixtures.Encode(17, 11, 3, 75, (x, y, c) => (byte)(x * 11 + y * 3 + c));
        // Little-endian TIFF IFD0: one SHORT orientation tag, value 6 (rotate 90).
        byte[] metadata = Convert.FromHexString("45786966000049492A0008000000010012010300010000000600000000000000");
        byte[] tagged = new byte[jpeg.Length + metadata.Length + 4];
        jpeg.AsSpan(0, 2).CopyTo(tagged); tagged[2] = 255; tagged[3] = 225;
        tagged[4] = 0; tagged[5] = checked((byte)(metadata.Length + 2));
        metadata.CopyTo(tagged, 6); jpeg.AsSpan(2).CopyTo(tagged.AsSpan(6 + metadata.Length));
        Assert.Equal(Lokad.Image.ImageOrientation.Rotate90, Lokad.Image.ImageCodec.Identify(tagged).Orientation);
        byte[] expected = new byte[17 * 11 * 3], actual = new byte[expected.Length];
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using MemoryStream plain = new(jpeg), oriented = new(tagged);
        LokadImageJpegCodec.Instance.Decode(plain, new(17, 11, 3), expected, new(governor));
        LokadImageJpegCodec.Instance.Decode(oriented, new(17, 11, 3), actual, new(governor));
        Assert.Equal(expected, actual); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void FrameAboveImageDefaultDimensionUsesTheAdmittedPdfBounds()
    {
        const int width = 32769;
        byte[] samples = Enumerable.Repeat((byte)128, width).ToArray();
        using MemoryStream encoded = new();
        Lokad.Image.ImageCodec.EncodeJpeg(samples, width, 1, Lokad.Image.JpegSampleFormat.Grayscale, encoded,
            options: new Lokad.Image.JpegEncodeOptions { Quality = 75, Sampling = Lokad.Image.JpegSampling.Grayscale,
                Limits = new Lokad.Image.ImageLimits { MaxDimension = 65535 } });
        encoded.Position = 0L;
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] actual = new byte[width];
        LokadImageJpegCodec.Instance.Decode(encoded, new(width, 1, 1), actual, new(governor));
        Assert.Equal(samples, actual); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class FaultBudget(Exception failure) : IMemoryBudget
    {
        public IMemoryScope CreateScope() => throw failure;
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation) => throw failure;
    }

    private sealed class ChunkedInput(byte[] bytes, int chunk) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int length = Math.Min(Math.Min(chunk, buffer.Length), bytes.Length - _position);
            bytes.AsSpan(_position, length).CopyTo(buffer); _position += length; return length;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
