using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Lokad.Qpdf.Runtime;
using System.Runtime.InteropServices;

namespace Lokad.Qpdf.Filters;

/// <summary>
/// Governed JPEG encoding through the pinned ImageSharp codec. The qpdf
/// quality scale (0..100) maps onto the ImageSharp scale (1..100) with 0
/// clamped to 1; an omitted quality encodes at 75 like the reference
/// defaults. Grayscale encodes with an explicit luminance color type so the
/// output keeps a single component. Only single- and three-component raw
/// samples encode; anything else fails explicitly instead of silently
/// reducing fidelity. The caller's token reaches codec work; pixel copies and
/// buffer admission also check cancellation.
/// </summary>
internal static class JpegCodec
{
    internal const string FilterName = "DCTDecode";

    internal const int DefaultQuality = 75;

    /// <summary>
    /// Allowance for codec/configuration state outside ImageSharp's allocator.
    /// Pixel, coefficient and stream buffers must be charged separately through
    /// the per-job allocator; this allowance does not replace their ownership.
    /// </summary>
    internal const long CodecWorkingSetBytes = 65536L;

    internal const long SampleArrayOverheadBytes = GovernedBufferStream.ArrayOverheadBytes;

    internal static Configuration CreateConfiguration(ResourceGovernor governor)
    {
        return new Configuration(new JpegConfigurationModule())
        {
            MemoryAllocator = new JpegMemoryAllocator(governor),
            MaxDegreeOfParallelism = 1,
            StreamProcessingBufferSize = 8192,
        };
    }

    internal static int MapQuality(int qpdfQuality)
    {
        if (qpdfQuality < 0 || qpdfQuality > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(qpdfQuality), "JPEG quality must be within 0..100.");
        }

        return qpdfQuality == 0 ? 1 : qpdfQuality;
    }

    internal static byte[] EncodeGray(
        byte[] raw,
        int width,
        int height,
        int quality,
        QpdfLimits limits,
        ResourceGovernor governor)
    {
        return Encode<L8>(raw, width, height, 1, quality, limits, governor, true);
    }

    internal static byte[] EncodeRgb(
        byte[] raw,
        int width,
        int height,
        int quality,
        QpdfLimits limits,
        ResourceGovernor governor)
    {
        return Encode<Rgb24>(raw, width, height, 3, quality, limits, governor, false);
    }

    private static byte[] Encode<TPixel>(
        byte[] raw,
        int width,
        int height,
        int components,
        int quality,
        QpdfLimits limits,
        ResourceGovernor governor,
        bool luminance)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (raw is null)
        {
            throw new ArgumentNullException(nameof(raw));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        limits.Validate();
        governor.CheckCancellation();
        if (width <= 0 || height <= 0)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "JPEG frame dimensions are not positive.");
        }

        long pixels = (long)width * height;
        long expected = pixels * components;
        if ((long)raw.Length != expected)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Raw samples do not match the declared frame size.");
        }

        if (expected > limits.MaxDecodedBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), expected, limits.MaxDecodedBytes);
        }

        int mapped = MapQuality(quality);
        governor.ChargeWork(pixels);
        governor.ReserveMemory(CodecWorkingSetBytes);
        try
        {
            using Image<TPixel> image = LoadPixels<TPixel>(raw, width, height, governor);
            JpegEncoder encoder = luminance
                ? new JpegEncoder { Quality = mapped, ColorType = JpegEncodingColor.Luminance }
                : new JpegEncoder { Quality = mapped };

            using GovernedBufferStream buffer = new(governor, limits.MaxOutputBytes, nameof(QpdfLimits.MaxOutputBytes));
            try
            {
                // All streams are in-memory and complete their async writes synchronously.
                // The codec's token-bearing API also checks cancellation during encoding.
                image.SaveAsync(buffer, encoder, governor.CancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is not QpdfLimitException && ex is not OperationCanceledException)
            {
                throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Raw samples cannot be JPEG-encoded.", ex);
            }

            return buffer.ToArray();
        }
        finally
        {
            governor.ReleaseMemory(CodecWorkingSetBytes);
        }
    }

    private static Image<TPixel> LoadPixels<TPixel>(byte[] raw, int width, int height, ResourceGovernor governor)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        try
        {
            Image<TPixel> image = new(CreateConfiguration(governor), width, height);
            try
            {
                image.ProcessPixelRows(accessor =>
                {
                    int position = 0;
                    for (int y = 0; y < height; y++)
                    {
                        governor.CheckCancellation();
                        Span<byte> row = MemoryMarshal.AsBytes(accessor.GetRowSpan(y));
                        governor.ChargeWork(row.Length);
                        raw.AsSpan(position, row.Length).CopyTo(row);
                        position += row.Length;
                    }
                });
                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is not QpdfLimitException && ex is not OperationCanceledException)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Raw samples cannot be loaded for JPEG encoding.", ex);
        }
    }
}
