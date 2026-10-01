using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Filters;

/// <summary>
/// Governed DCTDecode (JPEG) through the pinned ImageSharp codec. Baseline
/// 1- and 3-component images decode to raw sample bytes at
/// <c>--decode-level=all</c>; anything else stays opaque with an explicit
/// failure. Four-component CMYK/YCCK samples cannot round-trip through the
/// approved public pixel API (decoding yields color-converted RGB), so those
/// streams are never silently reduced: they fail as unsupported and stay
/// opaque. Frame dimensions are read from the JPEG header before any pixel
/// buffer is allocated, and both work and memory budgets are charged up
/// front. Decode parameters are ignored because JPEG frames are
/// self-describing.
/// </summary>
internal static class DctFilter
{
    internal const string FilterName = "DCTDecode";

    internal static byte[] Decode(byte[] input, QpdfLimits limits, ResourceGovernor governor)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
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
        if (input.Length == 0)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "JPEG input holds no bytes.");
        }

        if (!TryParseFrame(input, out int width, out int height, out int components))
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "JPEG input has no readable frame header.");
        }

        if (components != 1 && components != 3)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.UnsupportedFilter, FilterName, "JPEG input holds " + components.ToString() + " components; only 1- and 3-component images decode in this milestone.");
        }

        long pixels = (long)width * height;
        long expected = pixels * (components == 1 ? 1L : 3L);
        if (expected > limits.MaxDecodedBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), expected, limits.MaxDecodedBytes);
        }

        governor.ChargeWork(pixels);
        governor.ReserveMemory(expected);
        try
        {
            byte[] decoded = components == 1
                ? DecodeGray(input)
                : DecodeRgb(input);
            if ((long)decoded.Length > limits.MaxDecodedBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), decoded.Length, limits.MaxDecodedBytes);
            }

            governor.ChargeDecodedBytes(decoded.Length);
            return decoded;
        }
        finally
        {
            governor.ReleaseMemory(expected);
        }
    }

    internal static bool TryParseFrame(byte[] input, out int width, out int height, out int components)
    {
        width = 0;
        height = 0;
        components = 0;
        if (input is null || input.Length < 4 || input[0] != 0xFF || input[1] != 0xD8)
        {
            return false;
        }

        int pos = 2;
        while (pos + 1 < input.Length)
        {
            if (input[pos] != 0xFF)
            {
                return false;
            }

            int m = pos + 1;
            while (m < input.Length && input[m] == 0xFF)
            {
                m += 1;
            }

            if (m >= input.Length)
            {
                return false;
            }

            byte marker = input[m];
            pos = m + 1;
            if (marker == 0xD9)
            {
                return false;
            }

            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            if (pos + 1 >= input.Length)
            {
                return false;
            }

            int segLen = (input[pos] << 8) | input[pos + 1];
            if (segLen < 2 || pos + segLen > input.Length)
            {
                return false;
            }

            if (IsFrameMarker(marker))
            {
                if (segLen < 8)
                {
                    return false;
                }

                height = (input[pos + 3] << 8) | input[pos + 4];
                width = (input[pos + 5] << 8) | input[pos + 6];
                components = input[pos + 7];
                return width > 0 && height > 0 && components > 0;
            }

            pos += segLen;
        }

        return false;
    }

    private static bool IsFrameMarker(byte marker)
    {
        switch (marker)
        {
            case 0xC0:
            case 0xC1:
            case 0xC2:
            case 0xC3:
            case 0xC5:
            case 0xC6:
            case 0xC7:
            case 0xC9:
            case 0xCA:
            case 0xCB:
            case 0xCD:
            case 0xCE:
            case 0xCF:
                return true;
            default:
                return false;
        }
    }

    private static byte[] DecodeGray(byte[] input)
    {
        using Image<L8> image = Load<L8>(input);
        if (image.Width <= 0 || image.Height <= 0)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "JPEG frame dimensions are not positive.");
        }

        byte[] output = new byte[checked(image.Width * image.Height)];
        int at = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                output[at] = image[x, y].PackedValue;
                at += 1;
            }
        }

        return output;
    }

    private static byte[] DecodeRgb(byte[] input)
    {
        using Image<Rgb24> image = Load<Rgb24>(input);
        if (image.Width <= 0 || image.Height <= 0)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "JPEG frame dimensions are not positive.");
        }

        byte[] output = new byte[checked(checked(image.Width * image.Height) * 3)];
        int at = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Rgb24 pixel = image[x, y];
                output[at] = pixel.R;
                output[at + 1] = pixel.G;
                output[at + 2] = pixel.B;
                at += 3;
            }
        }

        return output;
    }

    private static Image<TPixel> Load<TPixel>(byte[] input)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        try
        {
            return Image.Load<TPixel>(input);
        }
        catch (Exception ex) when (ex is not QpdfLimitException && ex is not OperationCanceledException)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "JPEG data cannot be decoded.", ex);
        }
    }
}