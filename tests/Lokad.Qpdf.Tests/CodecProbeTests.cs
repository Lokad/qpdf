using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Image;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Filters;

namespace Lokad.Qpdf.Tests;

public sealed class CodecProbeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void JpegSampleRoundTripPreservesDimensions(int components)
    {
        byte[] jpeg = JpegFixtures.Encode(8, 8, components, 75, (_, _, _) => 128);
        byte[] samples = new byte[8 * 8 * components];
        Lokad.Image.ImageInfo info = Lokad.Image.ImageCodec.DecodeJpeg(jpeg, samples,
            (Lokad.Image.JpegSampleFormat)components);
        Assert.Equal(8, info.Width); Assert.Equal(8, info.Height);
        Assert.Equal(components, info.SourceComponentCount);
        Assert.Equal(-1, samples.AsSpan().IndexOfAnyExcept((byte)128));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void PinnedEncoderRejectsOutOfRangeQuality(int quality)
    {
        using MemoryStream output = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => Lokad.Image.ImageCodec.EncodeJpeg(
            new byte[64], 8, 8, Lokad.Image.JpegSampleFormat.Grayscale, output,
            options: new Lokad.Image.JpegEncodeOptions { Quality = quality }));
    }

    [Fact]
    public void CmykCapabilityRetainsUninvertedSamples()
    {
        byte[] jpeg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cmyk-8x8.jpg"));
        byte[] samples = new byte[8 * 8 * 4];
        Lokad.Image.ImageInfo info = Lokad.Image.ImageCodec.DecodeJpeg(jpeg, samples, Lokad.Image.JpegSampleFormat.Cmyk,
            options: new Lokad.Image.JpegDecodeOptions { CmykInversion = Lokad.Image.JpegCmykInversion.None });
        Assert.Equal(8, info.Width); Assert.Equal(8, info.Height);
        Assert.Equal(4, info.SourceComponentCount);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None) { JpegCodec = LokadImageJpegCodec.Instance };
        byte[] decoded = DctFilter.Decode(jpeg, QpdfLimits.Default, governor);
        Assert.Equal(samples, decoded);
        governor.ReleaseMemory(decoded.LongLength + JpegCodec.SampleArrayOverheadBytes);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void JpegDecodeReservesCodecWorkingSet()
    {
        byte[] jpeg = DctDecodeTests.EncodeRgbJpeg(8, 8);
        long frame = 8L * 8L * 3L;
        QpdfLimits tight = QpdfLimits.Default with { MaxMemoryBytes = frame };
        Assert.Throws<QpdfLimitException>(() => DctFilter.Decode(jpeg, tight,
            new ResourceGovernor(tight, CancellationToken.None) { JpegCodec = LokadImageJpegCodec.Instance }));
        QpdfLimits roomy = QpdfLimits.Default with { MaxMemoryBytes = 256L * 1024L };
        byte[] decoded = DctFilter.Decode(jpeg, roomy,
            new ResourceGovernor(roomy, CancellationToken.None) { JpegCodec = LokadImageJpegCodec.Instance });
        Assert.Equal(frame, decoded.Length);
    }

    [Fact]
    public void JpegEncodeReservesCodecWorkingSet()
    {
        byte[] raw = new byte[8 * 8 * 3];
        QpdfLimits tight = QpdfLimits.Default with { MaxMemoryBytes = raw.Length };
        Assert.Throws<QpdfLimitException>(() => JpegCodec.EncodeRgb(raw, 8, 8, 75, tight,
            new ResourceGovernor(tight, CancellationToken.None) { JpegCodec = LokadImageJpegCodec.Instance }));
        QpdfLimits roomy = QpdfLimits.Default with { MaxMemoryBytes = 256L * 1024L };
        byte[] encoded = JpegCodec.EncodeRgb(raw, 8, 8, 75, roomy,
            new ResourceGovernor(roomy, CancellationToken.None) { JpegCodec = LokadImageJpegCodec.Instance });
        Assert.NotEmpty(encoded);
    }

    [Fact]
    public void CancelledEncodeFailsFast()
    {
        using CancellationTokenSource source = new(); source.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, source.Token);
        Assert.Throws<OperationCanceledException>(() => JpegCodec.EncodeRgb(new byte[8 * 8 * 3], 8, 8, 75, QpdfLimits.Default, governor));
    }

    [Fact]
    public void JpegCancelledDecodeHonorsCancellation()
    {
        byte[] jpeg = DctDecodeTests.EncodeRgbJpeg(8, 8);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Lokad.Image.ImageCodec.DecodeJpeg(
            jpeg, new byte[8 * 8 * 3], Lokad.Image.JpegSampleFormat.Rgb, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void ProbeJpegCommandSucceeds()
    {
        (int exit, string stdout, _) = ProbeTestProcess.Run(null, "probe-jpeg");
        Assert.Equal(0, exit);
        Assert.Contains("jpeg:", stdout, StringComparison.Ordinal);
    }
}
