using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Lokad.Qpdf.ImageSharp;

/// <summary>Development-only JPEG adapter; depends only on the public Lokad.qpdf codec contract.</summary>
public sealed class ImageSharpJpegCodec : IQpdfJpegCodec
{
    /// <summary>Gets a stateless implementation; every call has independent codec state.</summary>
    public static ImageSharpJpegCodec Instance { get; } = new();
    private const long WorkingSetBytes = 65536L;

    private static Configuration CreateConfiguration(QpdfJpegCodecContext context) => new(new JpegConfigurationModule())
    {
        MemoryAllocator = new JpegMemoryAllocator(context),
        MaxDegreeOfParallelism = 1,
        StreamProcessingBufferSize = 8192,
    };

    internal static int MapQuality(int quality)
    {
        if (quality < 0 || quality > 100) { throw new ArgumentOutOfRangeException(nameof(quality)); }
        return quality == 0 ? 1 : quality;
    }

    /// <inheritdoc />
    public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
    {
        using IDisposable state = context.ReserveMemory(WorkingSetBytes);
        if (frame.Components == 1) { Decode<L8>(encoded, frame, samples, context); }
        else { Decode<Rgb24>(encoded, frame, samples, context); }
    }

    private static void Decode<TPixel>(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        DecoderOptions options = new() { Configuration = CreateConfiguration(context), SkipMetadata = true, MaxFrames = 1 };
        using Image<TPixel> image = Image.LoadAsync<TPixel>(options, encoded, context.CancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        if (image.Width != frame.Width || image.Height != frame.Height)
        { throw new InvalidDataException("Decoded JPEG dimensions differ from the admitted frame."); }
        if (samples.Length != checked(checked(frame.Width * frame.Height) * frame.Components))
        { throw new ArgumentException("Sample destination does not match the frame.", nameof(samples)); }
        image.ProcessPixelRows(accessor =>
        {
            int position = 0;
            for (int row = 0; row < image.Height; row++)
            {
                context.CheckCancellation();
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(accessor.GetRowSpan(row));
                bytes.CopyTo(samples.Span.Slice(position, bytes.Length)); position += bytes.Length;
            }
        });
    }

    /// <inheritdoc />
    public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
    {
        using IDisposable state = context.ReserveMemory(WorkingSetBytes);
        if (frame.Components == 1) { Encode<L8>(samples, frame, quality, encoded, context); }
        else { Encode<Rgb24>(samples, frame, quality, encoded, context); }
    }

    private static void Encode<TPixel>(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int mapped = MapQuality(quality);
        if (samples.Length != checked(checked(frame.Width * frame.Height) * frame.Components))
        { throw new ArgumentException("Sample source does not match the frame.", nameof(samples)); }
        using Image<TPixel> image = new(CreateConfiguration(context), frame.Width, frame.Height);
        image.ProcessPixelRows(accessor =>
        {
            int position = 0;
            for (int row = 0; row < image.Height; row++)
            {
                context.CheckCancellation();
                Span<byte> bytes = MemoryMarshal.AsBytes(accessor.GetRowSpan(row));
                context.ChargeWork(bytes.Length);
                samples.Span.Slice(position, bytes.Length).CopyTo(bytes); position += bytes.Length;
            }
        });
        JpegEncoder encoder = frame.Components == 1
            ? new JpegEncoder { Quality = mapped, ColorType = JpegEncodingColor.Luminance }
            : new JpegEncoder { Quality = mapped };
        image.SaveAsync(encoded, encoder, context.CancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
    }
}
