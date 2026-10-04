using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Lokad.Qpdf.Probe;

// Bounds and maps the pinned ImageSharp 3.1.12 JPEG codec for PDF DCT use.
// Grayscale and RGB round-trips are feasible; four-component CMYK/YCCK raw
// samples are not preserved through the public pixel API (decoded pixels are
// color-converted, and no CMYK pixel type exists). qpdf --jpeg-quality 0..100 maps
// to ImageSharp 1..100 with 0 clamped to 1. Four-component opacity is a final
// decision (work item 133); see docs/COMPATIBILITY.md.
internal static class JpegProbe
{
    internal const int ProbeWidth = 8;
    internal const int ProbeHeight = 8;
    internal const long MaxProbePixels = 1024 * 1024;

    internal static int MapQpdfQualityToImageSharp(int qpdfQuality)
    {
        if (qpdfQuality < 0 || qpdfQuality > 100)
        {
            throw new ProbeException($"qpdf --jpeg-quality must be in [0..100], got {qpdfQuality}.", 1);
        }

        return qpdfQuality == 0 ? 1 : qpdfQuality;
    }

    internal static void CheckDimensions(int width, int height, long maxPixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ProbeException($"Invalid JPEG dimensions {width}x{height}. Dimensions must be positive.", 1);
        }

        long pixels = (long)width * height;
        if (pixels > maxPixels)
        {
            throw new ProbeException($"JPEG dimensions {width}x{height} exceed the probe pixel budget ({pixels} > {maxPixels}).", 1);
        }
    }

    internal static void Run()
    {
        CheckDimensions(ProbeWidth, ProbeHeight, MaxProbePixels);

        (int gray1, int gray75, int gray100) = RoundTripGrayscale();
        Console.WriteLine($"jpeg: grayscale 8x8 bytes q1={gray1} q75={gray75} q100={gray100}");

        (int rgb1, int rgb75, int rgb100) = RoundTripRgb();
        Console.WriteLine($"jpeg: rgb 8x8 bytes q1={rgb1} q75={rgb75} q100={rgb100}");

        CheckQualityEndpoints();
        Console.WriteLine("jpeg: quality endpoints ImageSharp 1..100 ok; qpdf 0 maps to 1; 101 and 0-native rejected");

        ReportFourComponentGap();
        CheckOversizedRejected();
        CheckCancelledDecode();
        Console.WriteLine("jpeg: allocation bounds and cancellation ok");
    }

    private static (int, int, int) RoundTripGrayscale()
    {
        using Image<L8> image = new(ProbeWidth, ProbeHeight);
        for (int y = 0; y < ProbeHeight; y++)
        {
            for (int x = 0; x < ProbeWidth; x++)
            {
                image[x, y] = new L8((byte)((x * 32 + y * 4) & 0xFF));
            }
        }

        int q1 = EncodeAndVerifyL8(image, 1);
        int q75 = EncodeAndVerifyL8(image, 75);
        int q100 = EncodeAndVerifyL8(image, 100);
        image.Dispose();
        return (q1, q75, q100);
    }

    private static int EncodeAndVerifyL8(Image<L8> image, int quality)
    {
        JpegEncoder encoder = new()
        {
            Quality = quality,
        };
        using MemoryStream buffer = new();
        image.Save(buffer, encoder);
        byte[] bytes = buffer.ToArray();
        if (bytes.Length == 0)
        {
            throw new ProbeException($"JPEG grayscale encode produced no bytes at quality {quality}.", 1);
        }

        using Image<L8> decoded = Image.Load<L8>(bytes);
        if (decoded.Width != ProbeWidth || decoded.Height != ProbeHeight)
        {
            throw new ProbeException($"JPEG grayscale round-trip changed dimensions: {decoded.Width}x{decoded.Height}.", 1);
        }

        return bytes.Length;
    }

    private static (int, int, int) RoundTripRgb()
    {
        using Image<Rgb24> image = new(ProbeWidth, ProbeHeight);
        for (int y = 0; y < ProbeHeight; y++)
        {
            for (int x = 0; x < ProbeWidth; x++)
            {
                image[x, y] = new Rgb24((byte)((x * 32) & 0xFF), (byte)((y * 32) & 0xFF), 128);
            }
        }

        int q1 = EncodeAndVerifyRgb(image, 1);
        int q75 = EncodeAndVerifyRgb(image, 75);
        int q100 = EncodeAndVerifyRgb(image, 100);
        image.Dispose();
        return (q1, q75, q100);
    }

    private static int EncodeAndVerifyRgb(Image<Rgb24> image, int quality)
    {
        JpegEncoder encoder = new()
        {
            Quality = quality,
        };
        using MemoryStream buffer = new();
        image.Save(buffer, encoder);
        byte[] bytes = buffer.ToArray();
        if (bytes.Length == 0)
        {
            throw new ProbeException($"JPEG RGB encode produced no bytes at quality {quality}.", 1);
        }

        using Image<Rgb24> decoded = Image.Load<Rgb24>(bytes);
        if (decoded.Width != ProbeWidth || decoded.Height != ProbeHeight)
        {
            throw new ProbeException($"JPEG RGB round-trip changed dimensions: {decoded.Width}x{decoded.Height}.", 1);
        }

        return bytes.Length;
    }

    private static void CheckQualityEndpoints()
    {
        bool zeroRejected = false;
        try
        {
            JpegEncoder encoder = new()
            {
                Quality = 0,
            };
            _ = encoder.Quality;
        }
        catch (ArgumentException)
        {
            zeroRejected = true;
        }

        if (!zeroRejected)
        {
            throw new ProbeException("Expected ImageSharp JpegEncoder Quality=0 to throw ArgumentException.", 1);
        }

        bool highRejected = false;
        try
        {
            JpegEncoder encoder = new()
            {
                Quality = 101,
            };
            _ = encoder.Quality;
        }
        catch (ArgumentException)
        {
            highRejected = true;
        }

        if (!highRejected)
        {
            throw new ProbeException("Expected ImageSharp JpegEncoder Quality=101 to throw ArgumentException.", 1);
        }

        if (MapQpdfQualityToImageSharp(0) != 1 || MapQpdfQualityToImageSharp(100) != 100 || MapQpdfQualityToImageSharp(75) != 75)
        {
            throw new ProbeException("qpdf-to-ImageSharp quality mapping is inconsistent.", 1);
        }
    }

    private static void ReportFourComponentGap()
    {
        // The enum values exist, but the public pixel API decodes to RGB/gray:
        // raw four-component CMYK/YCCK samples are not preserved.
        JpegEncodingColor cmyk = JpegEncodingColor.Cmyk;
        JpegEncodingColor ycck = JpegEncodingColor.Ycck;
        if (!cmyk.Equals(JpegEncodingColor.Cmyk) || !ycck.Equals(JpegEncodingColor.Ycck))
        {
            throw new ProbeException("ImageSharp JpegEncodingColor CMYK/YCCK enum values are unavailable.", 1);
        }

        using Image<Rgb24> image = new(ProbeWidth, ProbeHeight);
        JpegEncoder encoder = new()
        {
            Quality = 75,
            ColorType = JpegEncodingColor.Cmyk,
        };
        using MemoryStream buffer = new();
        image.Save(buffer, encoder);
        byte[] bytes = buffer.ToArray();
        using Image<Rgb24> decoded = Image.Load<Rgb24>(bytes);
        if (decoded.Width != ProbeWidth || decoded.Height != ProbeHeight)
        {
            throw new ProbeException("CMYK-labeled JPEG round-trip changed dimensions.", 1);
        }

        Console.WriteLine("jpeg: four-component decision: CMYK/YCCK encode labels exist but no CMYK pixel type or raw-component decoder surface exists, so four-component samples stay opaque by decision (work item 133)");
    }

    private static void CheckOversizedRejected()
    {
        bool rejected = false;
        try
        {
            CheckDimensions(100000, 100000, MaxProbePixels);
        }
        catch (ProbeException)
        {
            rejected = true;
        }

        if (!rejected)
        {
            throw new ProbeException("Oversized JPEG dimensions were not rejected before allocation.", 1);
        }
    }

    private static void CheckCancelledDecode()
    {
        using Image<Rgb24> image = new(ProbeWidth, ProbeHeight);
        JpegEncoder encoder = new()
        {
            Quality = 75,
        };
        using MemoryStream buffer = new();
        image.Save(buffer, encoder);
        byte[] bytes = buffer.ToArray();

        using CancellationTokenSource cts = new();
        cts.Cancel();
        bool cancelled = false;
        try
        {
            using MemoryStream input = new(bytes);
            Image.LoadAsync<Rgb24>(input, cts.Token).GetAwaiter().GetResult().Dispose();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        if (!cancelled)
        {
            throw new ProbeException("Cancelled JPEG decode did not honor cancellation.", 1);
        }
    }
}
