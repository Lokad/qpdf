using System.IO.Compression;
using System.Text.Json;
using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ImageOptimizeTests
{
    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] NoiseRgb(int width, int height)
    {
        Random random = new(42);
        byte[] raw = new byte[checked(width * height * 3)];
        random.NextBytes(raw);
        return raw;
    }

    private static byte[] NoiseGray(int width, int height)
    {
        Random random = new(7);
        byte[] raw = new byte[checked(width * height)];
        random.NextBytes(raw);
        return raw;
    }

    private static byte[] ZLibCompress(byte[] input)
    {
        using MemoryStream output = new();
        using (ZLibStream compressor = new(output, CompressionLevel.Optimal))
        {
            compressor.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }

    private static byte[] EncodePhotoJpeg(int width, int height)
    {
        using Image<Rgb24> image = new(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new Rgb24((byte)((x * 2 + y) & 0xFF), (byte)((x + y * 3) & 0xFF), (byte)((x * y) & 0xFF));
            }
        }

        using MemoryStream buffer = new();
        image.Save(buffer, new JpegEncoder { Quality = 75 });
        return buffer.ToArray();
    }

    private static byte[] BuildImagePage(int width, int height, string colorspace, int bits, string filter, byte[] data)
    {
        byte[] content = Encoding.ASCII.GetBytes("q " + width.ToString() + " 0 0 " + height.ToString() + " 0 0 cm /Im0 Do Q");
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>",
            "<< /Length " + content.Length.ToString() + " >>",
            "<< /Type /XObject /Subtype /Image /Width " + width.ToString() + " /Height " + height.ToString() + " /ColorSpace " + (colorspace.StartsWith("[", StringComparison.Ordinal) ? colorspace : "/" + colorspace) + " /BitsPerComponent " + bits.ToString() + " /Length " + data.Length.ToString() + filter + " >>",
        };
        List<byte[]> payloads = new()
        {
            content,
            data,
        };
        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj\n" + bodies[i] + "\n");
            if (number == 4 || number == 5)
            {
                byte[] streamData = number == 4 ? payloads[0] : payloads[1];
                byte[] streamOpen = Encoding.ASCII.GetBytes("stream\n");
                byte[] streamClose = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
                offsets.Add(position);
                position += open.Length + streamOpen.Length + streamData.Length + streamClose.Length;
                byte[] payload = new byte[open.Length + streamOpen.Length + streamData.Length + streamClose.Length];
                int at = 0;
                Buffer.BlockCopy(open, 0, payload, at, open.Length);
                at += open.Length;
                Buffer.BlockCopy(streamOpen, 0, payload, at, streamOpen.Length);
                at += streamOpen.Length;
                Buffer.BlockCopy(streamData, 0, payload, at, streamData.Length);
                at += streamData.Length;
                Buffer.BlockCopy(streamClose, 0, payload, at, streamClose.Length);
                parts.Add(payload);
                continue;
            }

            byte[] close = Encoding.ASCII.GetBytes("endobj\n");
            offsets.Add(position);
            position += open.Length + close.Length;
            byte[] plain = new byte[open.Length + close.Length];
            Buffer.BlockCopy(open, 0, plain, 0, open.Length);
            Buffer.BlockCopy(close, 0, plain, open.Length, close.Length);
            parts.Add(plain);
        }

        int size = bodies.Count + 1;
        StringBuilder xref = new();
        xref.Append("xref\n0 " + size.ToString() + "\n");
        xref.Append("0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            xref.Append(offset.ToString("D10") + " 00000 n \n");
        }

        xref.Append("trailer\n<< /Size " + size.ToString() + " /Root 1 0 R >>\n");
        xref.Append("startxref\n" + position.ToString() + "\n%%EOF\n");
        byte[] tail = Encoding.ASCII.GetBytes(xref.ToString());
        parts.Add(tail);
        int total = 0;
        foreach (byte[] part in parts)
        {
            total += part.Length;
        }

        byte[] output = new byte[total];
        int cursor = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, output, cursor, part.Length);
            cursor += part.Length;
        }

        return output;
    }

    private static byte[] BuildMaskedImagePage(string imageDict, byte[] imageData, string maskDict, byte[] maskData)
    {
        byte[] content = Encoding.ASCII.GetBytes("q 200 0 0 200 0 0 cm /Im0 Do Q");
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>",
            "<< /Length " + content.Length.ToString() + " >>",
            imageDict,
            maskDict,
        };
        List<byte[]> payloads = new()
        {
            content,
            imageData,
            maskData,
        };
        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[]? streamData = number == 4 ? payloads[0] : number == 5 ? payloads[1] : number == 6 ? payloads[2] : null;
            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj\n" + bodies[i] + "\n");
            if (streamData is null)
            {
                byte[] close = Encoding.ASCII.GetBytes("endobj\n");
                offsets.Add(position);
                position += open.Length + close.Length;
                byte[] plain = new byte[open.Length + close.Length];
                Buffer.BlockCopy(open, 0, plain, 0, open.Length);
                Buffer.BlockCopy(close, 0, plain, open.Length, close.Length);
                parts.Add(plain);
                continue;
            }

            byte[] streamOpen = Encoding.ASCII.GetBytes("stream\n");
            byte[] streamClose = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
            offsets.Add(position);
            position += open.Length + streamOpen.Length + streamData.Length + streamClose.Length;
            byte[] payload = new byte[open.Length + streamOpen.Length + streamData.Length + streamClose.Length];
            int at = 0;
            Buffer.BlockCopy(open, 0, payload, at, open.Length);
            at += open.Length;
            Buffer.BlockCopy(streamOpen, 0, payload, at, streamOpen.Length);
            at += streamOpen.Length;
            Buffer.BlockCopy(streamData, 0, payload, at, streamData.Length);
            at += streamData.Length;
            Buffer.BlockCopy(streamClose, 0, payload, at, streamClose.Length);
            parts.Add(payload);
        }

        int size = bodies.Count + 1;
        StringBuilder xref = new();
        xref.Append("xref\n0 " + size.ToString() + "\n");
        xref.Append("0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            xref.Append(offset.ToString("D10") + " 00000 n \n");
        }

        xref.Append("trailer\n<< /Size " + size.ToString() + " /Root 1 0 R >>\n");
        xref.Append("startxref\n" + position.ToString() + "\n%%EOF\n");
        byte[] tail = Encoding.ASCII.GetBytes(xref.ToString());
        parts.Add(tail);
        int total = 0;
        foreach (byte[] part in parts)
        {
            total += part.Length;
        }

        byte[] output = new byte[total];
        int cursor = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, output, cursor, part.Length);
            cursor += part.Length;
        }

        return output;
    }

    private static async Task<PdfStream> OutputImageAsync(InMemoryQpdfHost host, byte[] pdf)
    {
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, pdf, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
        for (int number = 1; number <= 40; number++)
        {
            try
            {
                if (document.GetObject(number) is PdfStream stream
                    && stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject subtype)
                    && subtype is PdfName name && name.DisplayText == "Image")
                {
                    return stream;
                }
            }
            catch (QpdfParseException)
            {
            }
        }

        throw new Xunit.Sdk.XunitException("No image XObject found in output.");
    }

    private static string FilterOf(PdfStream stream)
    {
        if (stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject raw) && raw is PdfName name)
        {
            return name.DisplayText;
        }

        return string.Empty;
    }

    [Fact]
    public async Task NoisyPhotoOptimizesToSmallerDct()
    {
        byte[] raw = NoiseRgb(200, 200);
        byte[] flated = ZLibCompress(raw);
        byte[] input = BuildImagePage(200, 200, "DeviceRGB", 8, " /Filter /FlateDecode", flated);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        PdfStream image = await OutputImageAsync(host, pdf);
        Assert.Equal("DCTDecode", FilterOf(image));
        Assert.True(image.Data.Length < flated.Length);
        Assert.True(DctFilter.TryParseFrame(image.Data, out int width, out int height, out int components));
        Assert.Equal(200, width);
        Assert.Equal(200, height);
        Assert.Equal(3, components);
    }

    [Fact]
    public async Task GrayPhotoOptimizesToSingleComponentDct()
    {
        byte[] raw = NoiseGray(200, 200);
        byte[] input = BuildImagePage(200, 200, "DeviceGray", 8, string.Empty, raw);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        PdfStream image = await OutputImageAsync(host, pdf);
        Assert.Equal("DCTDecode", FilterOf(image));
        Assert.True(image.Data.Length < raw.Length);
        Assert.True(DctFilter.TryParseFrame(image.Data, out int width, out int height, out int components));
        Assert.Equal(200, width);
        Assert.Equal(200, height);
        Assert.Equal(1, components);
    }

    [Fact]
    public async Task SmallImageStaysUntouched()
    {
        byte[] raw = NoiseRgb(64, 64);
        byte[] flated = ZLibCompress(raw);
        byte[] input = BuildImagePage(64, 64, "DeviceRGB", 8, " /Filter /FlateDecode", flated);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        PdfStream image = await OutputImageAsync(host, pdf);
        Assert.Equal("FlateDecode", FilterOf(image));
        Assert.Equal(flated, image.Data);
    }

    [Fact]
    public async Task ZeroThresholdsOptimizeSmallImages()
    {
        byte[] raw = NoiseRgb(64, 64);
        byte[] flated = ZLibCompress(raw);
        byte[] input = BuildImagePage(64, 64, "DeviceRGB", 8, " /Filter /FlateDecode", flated);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--oi-min-width=0", "--oi-min-height=0", "--oi-min-area=0", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        PdfStream image = await OutputImageAsync(host, pdf);
        Assert.Equal("DCTDecode", FilterOf(image));
    }

    [Fact]
    public async Task IncompressibleSolidKeepsFlate()
    {
        byte[] raw = new byte[200 * 200 * 3];
        byte[] flated = ZLibCompress(raw);
        byte[] input = BuildImagePage(200, 200, "DeviceRGB", 8, " /Filter /FlateDecode", flated);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        PdfStream image = await OutputImageAsync(host, pdf);
        Assert.Equal("FlateDecode", FilterOf(image));
        Assert.Equal(flated, image.Data);
    }

    [Fact]
    public async Task ExistingDctStaysUntouchedWithoutQuality()
    {
        byte[] jpeg = EncodePhotoJpeg(200, 200);
        byte[] input = BuildImagePage(200, 200, "DeviceRGB", 8, " /Filter /DCTDecode", jpeg);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        PdfStream image = await OutputImageAsync(host, pdf);
        Assert.Equal("DCTDecode", FilterOf(image));
        Assert.Equal(jpeg, image.Data);
    }

    [Fact]
    public async Task QualityForcesDctRecompression()
    {
        byte[] jpeg = EncodePhotoJpeg(200, 200);
        byte[] input = BuildImagePage(200, 200, "DeviceRGB", 8, " /Filter /DCTDecode", jpeg);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--jpeg-quality=5", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        PdfStream image = await OutputImageAsync(host, pdf);
        Assert.Equal("DCTDecode", FilterOf(image));
        Assert.NotEqual(jpeg, image.Data);
    }

    [Fact]
    public async Task UnsupportedColorspaceStaysUntouched()
    {
        byte[] raw = NoiseRgb(200, 200);
        byte[] flated = ZLibCompress(raw);
        byte[] input = BuildImagePage(200, 200, "DeviceCMYK", 8, " /Filter /FlateDecode", flated);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        PdfStream image = await OutputImageAsync(host, pdf);
        Assert.Equal("FlateDecode", FilterOf(image));
        Assert.Equal(flated, image.Data);
    }

    [Fact]
    public async Task BitMaskStaysUntouched()
    {
        byte[] raw = new byte[200 * 200];
        byte[] input = BuildImagePage(200, 200, "DeviceGray", 1, string.Empty, raw);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        PdfStream image = await OutputImageAsync(host, pdf);
        Assert.Equal(string.Empty, FilterOf(image));
        Assert.Equal(raw, image.Data);
    }

    [Fact]
    public async Task SmaskImageOptimizesKeepingMask()
    {
        byte[] raw = NoiseRgb(200, 200);
        byte[] flated = ZLibCompress(raw);
        byte[] maskRaw = NoiseGray(200, 200);
        byte[] maskFlated = ZLibCompress(maskRaw);
        string imageDict = "<< /Type /XObject /Subtype /Image /Width 200 /Height 200 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /SMask 6 0 R /Length " + flated.Length.ToString() + " >>";
        string maskDict = "<< /Type /XObject /Subtype /Image /Width 200 /Height 200 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Length " + maskFlated.Length.ToString() + " >>";
        byte[] input = BuildMaskedImagePage(imageDict, flated, maskDict, maskFlated);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        PdfStream image = await OutputImageAsync(host, pdf);
        Assert.Equal("DCTDecode", FilterOf(image));
        Assert.True(image.Data.Length < flated.Length);
        Assert.True(image.Dictionary.TryGet(PdfName.FromAscii("SMask"), out QpdfObject smask));
        PdfReference smaskRef = Assert.IsType<PdfReference>(smask);
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, pdf, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
        PdfStream mask = Assert.IsType<PdfStream>(document.GetObject(smaskRef.ObjectNumber));
        Assert.Equal("FlateDecode", FilterOf(mask));
        Assert.Equal(maskFlated, mask.Data);
    }

    [Fact]
    public async Task MaskArrayImageKeepsMaskEntry()
    {
        byte[] raw = NoiseRgb(200, 200);
        byte[] flated = ZLibCompress(raw);
        byte[] input = BuildImagePage(200, 200, "DeviceRGB", 8, " /Filter /FlateDecode /Mask [0 0 0 255 255 255]", flated);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        PdfStream image = await OutputImageAsync(host, pdf);
        Assert.Equal("DCTDecode", FilterOf(image));
        Assert.True(image.Dictionary.TryGet(PdfName.FromAscii("Mask"), out QpdfObject mask));
        PdfArray maskArray = Assert.IsType<PdfArray>(mask);
        Assert.Equal(6, maskArray.Count);
    }

    [Fact]
    public async Task IndexedImageStaysUntouched()
    {
        byte[] raw = NoiseGray(200, 200);
        byte[] flated = ZLibCompress(raw);
        byte[] input = BuildImagePage(200, 200, "[/Indexed /DeviceRGB 255]", 8, " /Filter /FlateDecode", flated);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        PdfStream image = await OutputImageAsync(host, pdf);
        Assert.Equal("FlateDecode", FilterOf(image));
        Assert.Equal(flated, image.Data);
    }

    [Fact]
    public async Task DimensionMismatchSkipsSilently()
    {
        byte[] raw = NoiseRgb(8, 8);
        byte[] input = BuildImagePage(200, 200, "DeviceRGB", 8, string.Empty, raw);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        PdfStream image = await OutputImageAsync(host, pdf);
        Assert.Equal(string.Empty, FilterOf(image));
        Assert.Equal(raw, image.Data);
    }

    [Fact]
    public void EncodeBudgetsFailExplicitly()
    {
        byte[] raw = NoiseRgb(64, 64);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = 1000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException ex = Assert.Throws<QpdfLimitException>(
            () => JpegCodec.EncodeRgb(raw, 64, 64, 75, limits, governor));
        Assert.Equal(nameof(QpdfLimits.MaxDecodedBytes), ex.LimitName);
    }    [Fact]
    public async Task MergeOptimizesEveryPage()
    {
        byte[] raw = NoiseRgb(200, 200);
        byte[] flated = ZLibCompress(raw);
        byte[] fixture = BuildImagePage(200, 200, "DeviceRGB", 8, " /Filter /FlateDecode", flated);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), fixture, QpdfInputOwnership.Copy);
        List<string> argv = new() { "inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--optimize-images", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, pdf, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
        Assert.Equal(2, document.PageCount);
        int optimized = 0;
        for (int number = 1; number <= 40; number++)
        {
            try
            {
                if (document.GetObject(number) is PdfStream stream
                    && stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject subtype)
                    && subtype is PdfName name && name.DisplayText == "Image"
                    && FilterOf(stream) == "DCTDecode")
                {
                    optimized += 1;
                }
            }
            catch (QpdfParseException)
            {
            }
        }

        Assert.Equal(2, optimized);
    }

    [Fact]
    public async Task JobParityHolds()
    {
        byte[] raw = NoiseRgb(200, 200);
        byte[] flated = ZLibCompress(raw);
        byte[] fixture = BuildImagePage(200, 200, "DeviceRGB", 8, " /Filter /FlateDecode", flated);
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", fixture);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult argvResult = await Qpdf.ExecuteAsync(argvHost, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argvResult.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] argvPdf));
        PdfStream argvImage = await OutputImageAsync(HostWith("inputs/x.pdf", argvPdf), argvPdf);
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", fixture);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"optimizeImages\": \"\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] jobPdf));
        PdfStream jobImage = await OutputImageAsync(HostWith("inputs/y.pdf", jobPdf), jobPdf);
        Assert.Equal(argvImage.Data, jobImage.Data);
    }

    [Theory]
    [InlineData("--oi-min-width=0", "\"oiMinWidth\": 0")]
    [InlineData("--oi-min-height=0", "\"oiMinHeight\": 0")]
    [InlineData("--oi-min-area=0", "\"oiMinArea\": 0")]
    [InlineData("--keep-inline-images", "\"keepInlineImages\": \"\"")]
    [InlineData("--jpeg-quality=50", "\"jpegQuality\": 50")]
    public async Task ThresholdJobParityHolds(string flag, string jobKey)
    {
        byte[] raw = NoiseRgb(200, 200);
        byte[] flated = ZLibCompress(raw);
        byte[] fixture = BuildImagePage(200, 200, "DeviceRGB", 8, " /Filter /FlateDecode", flated);
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", fixture);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", flag, "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult argvResult = await Qpdf.ExecuteAsync(argvHost, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argvResult.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] argvPdf));
        PdfStream argvImage = await OutputImageAsync(HostWith("inputs/x.pdf", argvPdf), argvPdf);
        Assert.Equal("DCTDecode", FilterOf(argvImage));
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", fixture);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"optimizeImages\": \"\", " + jobKey + "}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] jobPdf));
        PdfStream jobImage = await OutputImageAsync(HostWith("inputs/y.pdf", jobPdf), jobPdf);
        Assert.Equal("DCTDecode", FilterOf(jobImage));
        Assert.Equal(argvImage.Data, jobImage.Data);
    }

    [Theory]
    [InlineData("--jpeg-quality=101")]
    [InlineData("--jpeg-quality=abc")]
    [InlineData("--jpeg-quality")]
    [InlineData("--oi-min-width=abc")]
    [InlineData("--oi-min-area=-1")]
    [InlineData("--keep-inline-images=x")]
    public async Task InvalidValuesFail(params string[] flags)
    {
        byte[] raw = NoiseRgb(200, 200);
        byte[] fixture = BuildImagePage(200, 200, "DeviceRGB", 8, string.Empty, raw);
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--pages", ".", "1", "--", "outputs/a.pdf"]);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, error.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("--optimize-images", "--optimize-images")]
    [InlineData("--jpeg-quality=5", "--jpeg-quality=6")]
    [InlineData("--oi-min-width=1", "--oi-min-width=2")]
    [InlineData("--keep-inline-images", "--keep-inline-images")]
    public async Task DuplicateFlagsFail(params string[] flags)
    {
        byte[] raw = NoiseRgb(200, 200);
        byte[] fixture = BuildImagePage(200, 200, "DeviceRGB", 8, string.Empty, raw);
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--pages", ".", "1", "--", "outputs/a.pdf"]);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, error.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"jpegQuality\": 101}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"oiMinWidth\": -1}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"optimizeImages\": \"yes\"}")]
    public async Task JobMalformedFails(string jobJson)
    {
        byte[] raw = NoiseRgb(200, 200);
        byte[] fixture = BuildImagePage(200, 200, "DeviceRGB", 8, string.Empty, raw);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public void QualityMappingMatchesReference()
    {
        Assert.Equal(1, JpegCodec.MapQuality(0));
        Assert.Equal(75, JpegCodec.MapQuality(75));
        Assert.Equal(100, JpegCodec.MapQuality(100));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegCodec.MapQuality(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegCodec.MapQuality(101));
    }
    private static byte[] InlineContent(string dict, byte[] image)
    {
        byte[] head = Encoding.ASCII.GetBytes(string.Concat("BT ", "BI", " ", dict, " ", "ID", " "));
        byte[] tail = Encoding.ASCII.GetBytes(string.Concat(" ", "EI", " ", "ET"));
        byte[] output = new byte[head.Length + image.Length + tail.Length];
        Buffer.BlockCopy(head, 0, output, 0, head.Length);
        Buffer.BlockCopy(image, 0, output, head.Length, image.Length);
        Buffer.BlockCopy(tail, 0, output, head.Length + image.Length, tail.Length);
        return output;
    }

    private static byte[] BuildInlinePage(byte[] content)
    {
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            "<< /Length " + content.Length.ToString() + " >>",
        };
        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj\n" + bodies[i] + "\n");
            byte[] streamData = number == 4 ? content : Array.Empty<byte>();
            if (number == 4)
            {
                byte[] streamOpen = Encoding.ASCII.GetBytes("stream\n");
                byte[] streamClose = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
                offsets.Add(position);
                position += open.Length + streamOpen.Length + streamData.Length + streamClose.Length;
                byte[] payload = new byte[open.Length + streamOpen.Length + streamData.Length + streamClose.Length];
                int at = 0;
                Buffer.BlockCopy(open, 0, payload, at, open.Length);
                at += open.Length;
                Buffer.BlockCopy(streamOpen, 0, payload, at, streamOpen.Length);
                at += streamOpen.Length;
                Buffer.BlockCopy(streamData, 0, payload, at, streamData.Length);
                at += streamData.Length;
                Buffer.BlockCopy(streamClose, 0, payload, at, streamClose.Length);
                parts.Add(payload);
                continue;
            }

            byte[] close = Encoding.ASCII.GetBytes("endobj\n");
            offsets.Add(position);
            position += open.Length + close.Length;
            byte[] plain = new byte[open.Length + close.Length];
            Buffer.BlockCopy(open, 0, plain, 0, open.Length);
            Buffer.BlockCopy(close, 0, plain, open.Length, close.Length);
            parts.Add(plain);
        }

        int size = bodies.Count + 1;
        StringBuilder xref = new();
        xref.Append("xref\n0 " + size.ToString() + "\n");
        xref.Append("0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            xref.Append(offset.ToString("D10") + " 00000 n \n");
        }

        xref.Append("trailer\n<< /Size " + size.ToString() + " /Root 1 0 R >>\n");
        xref.Append("startxref\n" + position.ToString() + "\n%%EOF\n");
        byte[] tail = Encoding.ASCII.GetBytes(xref.ToString());
        parts.Add(tail);
        int total = 0;
        foreach (byte[] part in parts)
        {
            total += part.Length;
        }

        byte[] output = new byte[total];
        int cursor = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, output, cursor, part.Length);
            cursor += part.Length;
        }

        return output;
    }

    private static async Task<byte[]> PageContentAsync(byte[] pdf)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/r.pdf"), pdf, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/r.pdf", "--json", "--json-stream-data=inline", "--", "outputs/r.json"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/r.json"), out byte[] json));
        using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(json));
        JsonElement objects = document.RootElement.GetProperty("qpdf")[1];
        JsonElement page = document.RootElement.GetProperty("pages")[0];
        List<byte> joined = new();
        foreach (JsonElement content in page.GetProperty("contents").EnumerateArray())
        {
            string reference = content.GetString() ?? string.Empty;
            string[] parts = reference.Split((char)32);
            string key = string.Concat("obj:", parts[0], " ", parts[1], " R");
            string data = objects.GetProperty(key).GetProperty("stream").GetProperty("data").GetString() ?? string.Empty;
            joined.AddRange(Convert.FromBase64String(data));
        }

        return [.. joined];
    }

    private static async Task<bool> HasImageXObjectAsync(byte[] pdf)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/r.pdf"), pdf, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/r.pdf"), CancellationToken.None);
        for (int number = 1; number <= 60; number++)
        {
            try
            {
                if (document.GetObject(number) is PdfStream stream
                    && stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject subtype)
                    && subtype is PdfName name && name.DisplayText == "Image")
                {
                    return true;
                }
            }
            catch (QpdfParseException)
            {
            }
        }

        return false;
    }

    [Fact]
    public async Task LargeInlineImageExternalizesAndOptimizes()
    {
        byte[] raw = NoiseRgb(200, 200);
        byte[] content = InlineContent("/W 200 /H 200 /CS /RGB /BPC 8", raw);
        byte[] fixture = BuildInlinePage(content);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        byte[] output = await PageContentAsync(pdf);
        string text = Encoding.ASCII.GetString(output);
        Assert.DoesNotContain("BI", text);
        Assert.Contains("Do", text);
        PdfStream image = await OutputImageAsync(host, pdf);
        Assert.Equal("DCTDecode", FilterOf(image));
        Assert.True(image.Data.Length < raw.Length);
    }

    [Fact]
    public async Task KeepInlineImagesLeavesInlineAlone()
    {
        byte[] raw = NoiseRgb(200, 200);
        byte[] content = InlineContent("/W 200 /H 200 /CS /RGB /BPC 8", raw);
        byte[] fixture = BuildInlinePage(content);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--keep-inline-images", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        byte[] output = await PageContentAsync(pdf);
        string text = Encoding.ASCII.GetString(output);
        Assert.Contains("BI", text);
        Assert.False(await HasImageXObjectAsync(pdf));
    }

    [Fact]
    public async Task SmallInlineImageStaysInline()
    {
        byte[] raw = NoiseGray(8, 8);
        byte[] content = InlineContent("/W 8 /H 8 /CS /G /BPC 8", raw);
        byte[] fixture = BuildInlinePage(content);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        byte[] output = await PageContentAsync(pdf);
        string text = Encoding.ASCII.GetString(output);
        Assert.Contains("BI", text);
        Assert.False(await HasImageXObjectAsync(pdf));
    }

    [Fact]
    public async Task ExternalizedInlineRespectsOiThresholds()
    {
        byte[] raw = NoiseRgb(200, 200);
        byte[] content = InlineContent("/W 200 /H 200 /CS /RGB /BPC 8", raw);
        byte[] fixture = BuildInlinePage(content);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
        List<string> argv = new() { "inputs/a.pdf", "--optimize-images", "--oi-min-width=1000", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.True(await HasImageXObjectAsync(pdf));
        PdfStream image = await OutputImageAsync(host, pdf);
        Assert.NotEqual("DCTDecode", FilterOf(image));
    }
}