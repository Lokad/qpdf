using Lokad.Image;

namespace Lokad.Qpdf.Tests.Filters;

internal static class JpegFixtures
{
    internal static byte[] Encode(int width, int height, int components, int quality, Func<int, int, int, byte> sample)
    {
        byte[] samples = new byte[checked(width * height * components)];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) for (int c = 0; c < components; c++)
        { samples[(y * width + x) * components + c] = sample(x, y, c); }
        using MemoryStream encoded = new();
        ImageCodec.EncodeJpeg(samples, width, height, (JpegSampleFormat)components, encoded,
            options: new JpegEncodeOptions { Quality = quality,
                Sampling = components == 1 ? JpegSampling.Grayscale : JpegSampling.YCbCr420 });
        return encoded.ToArray();
    }
}
