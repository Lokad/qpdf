using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Filters;

/// <summary>Validates and admits PDF DCT samples before invoking the host's optional JPEG codec.</summary>
internal static class DctFilter
{
    internal const string FilterName = "DCTDecode";

    internal static byte[] Decode(byte[] input, QpdfLimits limits, ResourceGovernor governor)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(limits); ArgumentNullException.ThrowIfNull(governor);
        limits.Validate(); governor.CheckCancellation();
        if (input.Length == 0)
        { throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "JPEG input holds no bytes."); }
        if (!TryParseFrame(input, governor, out int width, out int height, out int components))
        { throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "JPEG input has no readable frame header."); }
        if (components is not (1 or 3))
        { throw new QpdfFilterException(QpdfFilterFailureReason.UnsupportedFilter, FilterName, "Only grayscale and RGB JPEG samples are supported."); }
        long pixels = (long)width * height;
        long expected = pixels * components;
        long maximum = Math.Min(limits.MaxDecodedBytes, Array.MaxLength);
        if (expected > maximum) { throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), expected, maximum); }
        governor.CheckDecodedBytes(expected);
        IQpdfJpegCodec codec = governor.JpegCodec ?? throw new QpdfFilterException(QpdfFilterFailureReason.UnsupportedFilter,
            FilterName, "JPEG decoding requires a codec supplied by the host.");
        governor.ChargeWork(pixels);
        long reserved = checked(expected + JpegCodec.SampleArrayOverheadBytes);
        QpdfJpegCodecContext context = new(governor);
        governor.ReserveMemory(reserved);
        bool success = false;
        try
        {
            byte[] decoded = new byte[(int)expected];
            using MemoryStream source = new(input, writable: false);
            try
            {
                codec.Decode(source, new QpdfJpegFrame(width, height, components), decoded, context);
                context.CheckCancellation();
            }
            catch (Exception ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException or OutOfMemoryException) && ex is not OperationCanceledException && ex is not QpdfFilterException)
            { throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "JPEG data cannot be decoded.", ex); }
            governor.ChargeDecodedBytes(decoded.Length);
            success = true;
            return decoded;
        }
        finally { context.Close(); if (!success) { governor.ReleaseMemory(reserved); } }
    }

    internal static bool TryParseFrame(byte[] input, out int width, out int height, out int components)
        => TryParseFrame(input, null, out width, out height, out components);

    internal static bool TryParseFrame(byte[] input, ResourceGovernor? governor, out int width, out int height, out int components)
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
            governor?.CheckCancellation();
            governor?.ChargeWork(1L);
            if (input[pos] != 0xFF)
            {
                return false;
            }

            int m = pos + 1;
            while (m < input.Length && input[m] == 0xFF)
            {
                m += 1;
                if ((m & 1023) == 0) { governor?.CheckCancellation(); governor?.ChargeWork(1024L); }
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

}
