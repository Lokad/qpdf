using Lokad.Image;

namespace Lokad.Qpdf.Probe;

// Exercise the pinned development codec's packed Gray/RGB sample API. The
// separate PDF policy keeps CMYK/YCCK opaque despite upstream sample support.
internal static class JpegProbe
{
    internal const int ProbeWidth = 8;
    internal const int ProbeHeight = 8;
    internal const long MaxProbePixels = 1024 * 1024;

    internal static int MapQpdfQualityToImage(int quality)
    {
        if (quality is < 0 or > 100) { throw new ProbeException("qpdf JPEG quality must be in [0..100].", 1); }
        return Math.Max(1, quality);
    }

    internal static void CheckDimensions(int width, int height, long maxPixels)
    {
        if (width < 1 || height < 1 || (long)width * height > maxPixels)
        { throw new ProbeException("JPEG dimensions exceed the probe pixel budget.", 1); }
    }

    internal static void Run()
    {
        CheckDimensions(ProbeWidth, ProbeHeight, MaxProbePixels);
        foreach (int components in new[] { 1, 3 })
        {
            List<int> sizes = [];
            foreach (int quality in new[] { 1, 75, 100 }) { sizes.Add(RoundTrip(components, quality)); }
            Console.WriteLine($"jpeg: {(components == 1 ? "grayscale" : "rgb")} 8x8 bytes q1={sizes[0]} q75={sizes[1]} q100={sizes[2]}");
        }
        foreach (int quality in new[] { 0, 101 })
        {
            using MemoryStream output = new();
            bool rejected = false;
            try
            {
                ImageCodec.EncodeJpeg(new byte[64], 8, 8, JpegSampleFormat.Grayscale, output,
                    options: new JpegEncodeOptions { Quality = quality });
            }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            if (!rejected) { throw new ProbeException("Invalid encoder quality was accepted.", 1); }
        }
        if (MapQpdfQualityToImage(0) != 1 || MapQpdfQualityToImage(75) != 75 || MapQpdfQualityToImage(100) != 100)
        { throw new ProbeException("Inconsistent qpdf quality mapping.", 1); }
        Console.WriteLine("jpeg: quality endpoints Lokad.Image 1..100 ok; qpdf 0 maps to 1");
        Console.WriteLine("jpeg: PDF four-component decision: CMYK/YCCK samples remain opaque");
        bool oversized = false;
        try { CheckDimensions(100000, 100000, MaxProbePixels); }
        catch (ProbeException) { oversized = true; }
        if (!oversized) { throw new ProbeException("Oversized frame was accepted.", 1); }
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        bool canceled = false;
        try
        {
            using MemoryStream output = new();
            ImageCodec.EncodeJpeg(new byte[64], 8, 8, JpegSampleFormat.Grayscale, output, cancellationToken: cancellation.Token);
        }
        catch (OperationCanceledException) { canceled = true; }
        if (!canceled) { throw new ProbeException("Canceled codec call was accepted.", 1); }
        Console.WriteLine("jpeg: allocation bounds and cancellation ok");
    }

    private static int RoundTrip(int components, int quality)
    {
        byte[] raw = new byte[ProbeWidth * ProbeHeight * components];
        for (int y = 0; y < ProbeHeight; y++) for (int x = 0; x < ProbeWidth; x++) for (int c = 0; c < components; c++)
        { raw[(y * ProbeWidth + x) * components + c] = (byte)(components == 1 ? x * 32 + y * 4 : c == 0 ? x * 32 : c == 1 ? y * 32 : 128); }
        using MemoryStream output = new();
        ImageCodec.EncodeJpeg(raw, ProbeWidth, ProbeHeight, (JpegSampleFormat)components, output,
            options: new JpegEncodeOptions { Quality = quality,
                Sampling = components == 1 ? JpegSampling.Grayscale : JpegSampling.YCbCr420 });
        byte[] jpeg = output.ToArray(), decoded = new byte[raw.Length];
        ImageInfo info = ImageCodec.DecodeJpeg(jpeg, decoded, (JpegSampleFormat)components);
        if (info.Width != ProbeWidth || info.Height != ProbeHeight || info.SourceComponentCount != components)
        { throw new ProbeException("JPEG sample round-trip changed its frame.", 1); }
        return jpeg.Length;
    }
}
