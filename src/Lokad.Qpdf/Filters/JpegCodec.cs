using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Filters;

/// <summary>PDF-side JPEG validation, limits and bounded output; actual encoding is supplied by the host.</summary>
internal static class JpegCodec
{
    internal const string FilterName = "DCTDecode";
    internal const int DefaultQuality = 75;
    internal const long SampleArrayOverheadBytes = GovernedBufferStream.ArrayOverheadBytes;

    internal static byte[] EncodeGray(byte[] raw, int width, int height, int quality, QpdfLimits limits, ResourceGovernor governor)
        => Encode(raw, width, height, 1, quality, limits, governor);

    internal static byte[] EncodeRgb(byte[] raw, int width, int height, int quality, QpdfLimits limits, ResourceGovernor governor)
        => Encode(raw, width, height, 3, quality, limits, governor);

    private static byte[] Encode(byte[] raw, int width, int height, int components, int quality,
        QpdfLimits limits, ResourceGovernor governor)
    {
        ArgumentNullException.ThrowIfNull(raw); ArgumentNullException.ThrowIfNull(limits); ArgumentNullException.ThrowIfNull(governor);
        limits.Validate(); governor.CheckCancellation();
        if (width <= 0 || height <= 0)
        { throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "JPEG frame dimensions are not positive."); }
        long pixels = (long)width * height;
        if (pixels > raw.LongLength / components || raw.LongLength != pixels * components)
        { throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Raw samples do not match the declared frame size."); }
        if (raw.LongLength > limits.MaxDecodedBytes)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), raw.LongLength, limits.MaxDecodedBytes); }
        if (quality < 0 || quality > 100) { throw new ArgumentOutOfRangeException(nameof(quality)); }
        IQpdfJpegCodec codec = governor.JpegCodec ?? throw new QpdfFilterException(QpdfFilterFailureReason.UnsupportedFilter,
            FilterName, "JPEG encoding requires a codec supplied by the host.");
        governor.ChargeWork(pixels);
        QpdfJpegCodecContext context = new(governor);
        try
        {
            using GovernedBufferStream buffer = new(governor, limits.MaxOutputBytes, nameof(QpdfLimits.MaxOutputBytes));
            try
            {
                codec.Encode(raw, new QpdfJpegFrame(width, height, components), quality, buffer, context);
                context.CheckCancellation();
            }
            catch (Exception ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException or OutOfMemoryException) && ex is not OperationCanceledException && ex is not QpdfFilterException)
            { throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Raw samples cannot be JPEG-encoded.", ex); }
            byte[] result = buffer.ToArray();
            bool accepted = false;
            try
            {
                if (!DctFilter.TryParseFrame(result, governor, out int actualWidth, out int actualHeight, out int actualComponents)
                    || actualWidth != width || actualHeight != height || actualComponents != components)
                { throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "JPEG codec returned an invalid or mismatched frame."); }
                accepted = true;
                return result;
            }
            finally
            {
                if (!accepted && result.Length != 0) { governor.ReleaseMemory(result.LongLength + SampleArrayOverheadBytes); }
            }
        }
        finally { context.Close(); }
    }
}
