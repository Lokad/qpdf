using System.Text;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.ImageSharp;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ImageCandidateStorageTests
{
    private sealed class ObservedCodec(Action<ReadOnlyMemory<byte>> observe) : IQpdfJpegCodec
    {
        public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
            => ImageSharpJpegCodec.Instance.Decode(encoded, frame, samples, context);
        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
        {
            observe(samples);
            ImageSharpJpegCodec.Instance.Encode(samples, frame, quality, encoded, context);
        }
    }

    private static PdfStream Image(int width, byte[] data, bool hex)
    {
        Dictionary<PdfName, QpdfObject> header = new()
        {
            [PdfName.FromAscii("Width")] = new PdfInteger(width.ToString(), width),
            [PdfName.FromAscii("Height")] = new PdfInteger(width.ToString(), width),
            [PdfName.FromAscii("BitsPerComponent")] = new PdfInteger("8", 8),
            [PdfName.FromAscii("ColorSpace")] = PdfName.FromAscii("DeviceGray"),
        };
        if (hex) { header[PdfName.FromAscii("Filter")] = PdfName.FromAscii("ASCIIHexDecode"); }
        return PdfStream.FromOwnedData(new(header), data);
    }

    private static PdfStream? Optimize(PdfStream image, ResourceGovernor governor, QpdfLimits limits)
        => ImageOptimizer.TryOptimize(image, new(true, 0, 0, 0, false, 75),
            new(new Dictionary<int, QpdfObject>()), limits, governor, governor.CancellationToken);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodedAndTrimmedSamplesRemainFundedDuringCodecCall(bool trim)
    {
        byte[] samples = new byte[128 * 128 + (trim ? 1 : 0)];
        new Random(42).NextBytes(samples);
        if (trim) { samples[^1] = (byte)' '; }
        PdfStream image = Image(128, Encoding.ASCII.GetBytes(Convert.ToHexString(samples) + ">"), true);
        byte[] original = image.RawMemory.ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        bool called = false;
        governor = new(QpdfLimits.Default, CancellationToken.None)
        {
            JpegCodec = new ObservedCodec(raw =>
            {
                called = true;
                Assert.Equal(128 * 128, raw.Length);
                long minimum = samples.LongLength + 32L + (trim ? raw.Length + 32L : 0L);
                Assert.True(governor.Snapshot().LiveMemoryBytes >= minimum);
            }),
        };
        PdfStream result = Assert.IsType<PdfStream>(Optimize(image, governor, QpdfLimits.Default));
        Assert.True(called);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= result.RawMemory.Length + 32L);
        Assert.True(governor.Snapshot().LiveMemoryBytes < samples.Length);
        Assert.Equal(original, image.RawMemory.ToArray());
        governor.CompletePrivateStorage();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.NotEmpty(result.RawMemory.ToArray());
    }

    [Fact]
    public void LargerCandidateReturnsItsEntireTemporaryAdmission()
    {
        PdfStream image = Image(8, new byte[64], false);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None) { JpegCodec = ImageSharpJpegCodec.Instance };
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(71L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.Null(Optimize(image, governor, QpdfLimits.Default));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void InputMaterializationRefusesBeforeCopyingAndBeforeCodecCall()
    {
        PdfStream source = Image(1024, new byte[1024 * 1024 + 1], false);
        PdfStream image = PdfStream.FromImmutableSlice(source.Dictionary, source.RawMemory[1..]);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None)
            { JpegCodec = new ObservedCodec(_ => Assert.Fail("Codec must not run after refusal.")) };
        Assert.Throws<QpdfLimitException>(() => Optimize(image, governor, limits));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void CancellationDuringCodecReturnsSampleAndCandidateAdmission()
    {
        byte[] samples = new byte[128 * 128];
        PdfStream image = Image(128, Encoding.ASCII.GetBytes(Convert.ToHexString(samples) + ">"), true);
        using CancellationTokenSource source = new();
        ResourceGovernor governor = new(QpdfLimits.Default, source.Token)
            { JpegCodec = new ObservedCodec(_ => source.Cancel()) };
        Assert.ThrowsAny<OperationCanceledException>(() => Optimize(image, governor, QpdfLimits.Default));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
