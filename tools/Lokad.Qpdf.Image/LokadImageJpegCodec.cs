using Lokad.Image;

namespace Lokad.Qpdf.Image;

/// <summary>Development-only JPEG adapter using the public Lokad.qpdf and Lokad.Image sample contracts.</summary>
public sealed class LokadImageJpegCodec : IQpdfJpegCodec
{
    /// <summary>Gets a stateless implementation with independent per-call storage.</summary>
    public static LokadImageJpegCodec Instance { get; } = new();

    internal static int MapQuality(int quality)
    {
        if (quality is < 0 or > 100) { throw new ArgumentOutOfRangeException(nameof(quality)); }
        return Math.Max(1, quality);
    }

    /// <inheritdoc />
    public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
    {
        ValidateSamples(frame, samples.Length);
        using JpegInput input = JpegInput.Read(encoded, context);
        if (input.Memory.Length < 2 || input.Memory.Span[0] != 255 || input.Memory.Span[1] != 216)
        { throw new UnsupportedImageException(UnsupportedImageFeature.Format, "Expected a JPEG image."); }
        JpegMemoryBudget budget = new(context.MemoryBudget);
        ImageLimits limits = Limits(frame, budget);
        ImageInfo header = ImageCodec.Identify(input.Memory.Span, limits, context.CancellationToken);
        if (header.Width != frame.Width || header.Height != frame.Height || header.SourceComponentCount != frame.Components)
        { throw new InvalidDataException("Encoded JPEG does not match the admitted frame."); }
        ImageInfo decoded = ImageCodec.DecodeJpeg(input.Memory.Span, samples.Span, (JpegSampleFormat)frame.Components,
            options: new JpegDecodeOptions { Limits = limits, ApplyOrientation = false },
            cancellationToken: context.CancellationToken);
        if (decoded.Width != frame.Width || decoded.Height != frame.Height || decoded.SourceComponentCount != frame.Components)
        { throw new InvalidDataException("Decoded JPEG does not match the admitted frame."); }
        context.CheckCancellation();
    }

    /// <inheritdoc />
    public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
    {
        ValidateSamples(frame, samples.Length);
        int mapped = MapQuality(quality);
        JpegMemoryBudget budget = new(context.MemoryBudget);
        ImageCodec.EncodeJpeg(samples.Span, frame.Width, frame.Height, (JpegSampleFormat)frame.Components, encoded,
            options: new JpegEncodeOptions { Limits = Limits(frame, budget), Quality = mapped,
                Sampling = frame.Components == 1 ? JpegSampling.Grayscale : JpegSampling.YCbCr420 },
            cancellationToken: context.CancellationToken);
        context.CheckCancellation();
    }

    private static void ValidateSamples(QpdfJpegFrame frame, int length)
    {
        if (frame.Width < 1 || frame.Height < 1 || frame.Components is not (1 or 3)
            || checked((long)frame.Width * frame.Height * frame.Components) != length)
        { throw new ArgumentException("Packed Gray/RGB samples must match the admitted frame.", nameof(frame)); }
    }

    private static ImageLimits Limits(QpdfJpegFrame frame, JpegMemoryBudget budget) => new()
    {
        MaxDimension = Math.Max(frame.Width, frame.Height), MaxPixels = (long)frame.Width * frame.Height,
        // The core already bounds borrowed bytes and output. Keep Image's local
        // limits finite without imposing smaller independent default ceilings.
        MaxInputBytes = int.MaxValue, MaxOutputBytes = int.MaxValue, MaxWorkingBytes = long.MaxValue,
        MaxMetadataBytes = int.MaxValue, MaxContainerItems = int.MaxValue, MemoryBudget = budget,
    };
}
