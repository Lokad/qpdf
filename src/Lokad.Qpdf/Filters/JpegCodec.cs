using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Filters;

/// <summary>
/// Governed JPEG encoding through the pinned ImageSharp codec. The qpdf
/// quality scale (0..100) maps onto the ImageSharp scale (1..100) with 0
/// clamped to 1; an omitted quality encodes at 75 like the reference
/// defaults. Grayscale encodes with an explicit luminance color type so the
/// output keeps a single component. Only single- and three-component raw
/// samples encode; anything else fails explicitly instead of silently
/// reducing fidelity.
/// </summary>
internal static class JpegCodec
{
    internal const string FilterName = "DCTDecode";

    internal const int DefaultQuality = 75;

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
        governor.ReserveMemory(expected);
        try
        {
            using Image<TPixel> image = LoadPixels<TPixel>(raw, width, height);
            JpegEncoder encoder = luminance
                ? new JpegEncoder { Quality = mapped, ColorType = JpegEncodingColor.Luminance }
                : new JpegEncoder { Quality = mapped };

            using MemoryStream buffer = new();
            try
            {
                image.Save(buffer, encoder);
            }
            catch (Exception ex) when (ex is not QpdfLimitException && ex is not OperationCanceledException)
            {
                throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Raw samples cannot be JPEG-encoded.", ex);
            }

            byte[] encoded = buffer.ToArray();
            governor.ChargeDecodedBytes(encoded.Length);
            return encoded;
        }
        finally
        {
            governor.ReleaseMemory(expected);
        }
    }

    private static Image<TPixel> LoadPixels<TPixel>(byte[] raw, int width, int height)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        try
        {
            return Image.LoadPixelData<TPixel>(raw, width, height);
        }
        catch (Exception ex) when (ex is not QpdfLimitException && ex is not OperationCanceledException)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Raw samples cannot be loaded for JPEG encoding.", ex);
        }
    }
}