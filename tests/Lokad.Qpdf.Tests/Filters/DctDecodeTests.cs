using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class DctDecodeTests
{
    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] EncodeGrayJpeg(int width, int height)
    {
        using Image<L8> image = new(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new L8((byte)((x * 32 + y * 4) & 0xFF));
            }
        }

        using MemoryStream buffer = new();
        image.Save(buffer, new JpegEncoder { Quality = 75, ColorType = JpegEncodingColor.Luminance });
        return buffer.ToArray();
    }

    private static byte[] EncodeRgbJpeg(int width, int height)
    {
        using Image<Rgb24> image = new(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new Rgb24((byte)((x * 32) & 0xFF), (byte)((y * 32) & 0xFF), 128);
            }
        }

        using MemoryStream buffer = new();
        image.Save(buffer, new JpegEncoder { Quality = 75 });
        return buffer.ToArray();
    }

    private static byte[] EncodeCmykLabeledJpeg(int width, int height)
    {
        using Image<Rgb24> image = new(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new Rgb24((byte)((x * 32) & 0xFF), (byte)((y * 32) & 0xFF), 128);
            }
        }

        using MemoryStream buffer = new();
        image.Save(buffer, new JpegEncoder { Quality = 75, ColorType = JpegEncodingColor.Cmyk });
        return buffer.ToArray();
    }

    private static int FindFrameHeightOffset(byte[] jpeg)
    {
        int pos = 2;
        while (pos + 1 < jpeg.Length)
        {
            int m = pos + 1;
            while (m < jpeg.Length && jpeg[m] == 0xFF)
            {
                m += 1;
            }

            byte marker = jpeg[m];
            pos = m + 1;
            if (marker == 0xD9 || marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            int segLen = (jpeg[pos] << 8) | jpeg[pos + 1];
            if (marker == 0xC0 || marker == 0xC1 || marker == 0xC2 || marker == 0xC3
                || marker == 0xC5 || marker == 0xC6 || marker == 0xC7
                || marker == 0xC9 || marker == 0xCA || marker == 0xCB
                || marker == 0xCD || marker == 0xCE || marker == 0xCF)
            {
                return pos + 3;
            }

            pos += segLen;
        }

        throw new InvalidOperationException("No JPEG frame header found.");
    }

    private static byte[] PatchFrameDimensions(byte[] jpeg, int width, int height)
    {
        byte[] patched = (byte[])jpeg.Clone();
        int at = FindFrameHeightOffset(jpeg);
        patched[at] = (byte)((height >> 8) & 0xFF);
        patched[at + 1] = (byte)(height & 0xFF);
        patched[at + 2] = (byte)((width >> 8) & 0xFF);
        patched[at + 3] = (byte)(width & 0xFF);
        return patched;
    }

    private static byte[] BuildDctPage(byte[] jpeg)
    {
        return BuildFilteredPage("<< /Length " + jpeg.Length.ToString() + " /Filter /DCTDecode >>", jpeg);
    }

    private static byte[] BuildFilteredPage(string streamDict, byte[] streamData)
    {
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            streamDict,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
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

    private static async Task<byte[]> SelectAsync(InMemoryQpdfHost host, string input, string output, params string[] flags)
    {
        List<string> argv = new() { input };
        argv.AddRange(flags);
        argv.AddRange(["--pages", ".", "1", "--", output]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From(output), out byte[] pdf));
        return pdf;
    }

    private static async Task<PdfStream> OutputContentsAsync(InMemoryQpdfHost host, byte[] pdf)
    {
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, pdf, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        PdfDictionary? page = null;
        for (int number = 3; number <= 8; number++)
        {
            try
            {
                if (document.GetObject(number) is PdfDictionary candidate
                    && candidate.TryGet(PdfName.FromAscii("Type"), out QpdfObject type)
                    && type is PdfName name && name.DisplayText == "Page")
                {
                    page = candidate;
                    break;
                }
            }
            catch (QpdfParseException)
            {
            }
        }

        Assert.NotNull(page);
        Assert.True(page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject raw));
        PdfReference reference = Assert.IsType<PdfReference>(raw);
        return Assert.IsType<PdfStream>(document.GetObject(reference.ObjectNumber));
    }

    [Fact]
    public async Task GrayJpegDecodesAtAll()
    {
        byte[] jpeg = EncodeGrayJpeg(8, 8);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDctPage(jpeg));
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--decode-level=all");
        PdfStream stream = await OutputContentsAsync(host, pdf);
        Assert.False(stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject _));
        Assert.Equal(64, stream.Data.Length);
    }

    [Fact]
    public async Task RgbJpegDecodesAtAll()
    {
        byte[] jpeg = EncodeRgbJpeg(8, 6);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDctPage(jpeg));
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--decode-level=all");
        PdfStream stream = await OutputContentsAsync(host, pdf);
        Assert.False(stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject _));
        Assert.Equal(144, stream.Data.Length);
    }

    [Fact]
    public async Task CmykLabeledJpegStaysOpaque()
    {
        byte[] jpeg = EncodeCmykLabeledJpeg(8, 8);
        Assert.True(DctFilter.TryParseFrame(jpeg, out int width, out int height, out int components));
        Assert.Equal(4, components);
        Assert.Equal(8, width);
        Assert.Equal(8, height);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDctPage(jpeg));
        List<string> argv = new() { "inputs/a.pdf", "--decode-level=all", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        QpdfDiagnostic warning = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedFeature, warning.Code);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        PdfStream stream = await OutputContentsAsync(host, pdf);
        Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject raw));
        PdfName name = Assert.IsType<PdfName>(raw);
        Assert.Equal("DCTDecode", name.DisplayText);
        Assert.Equal(jpeg, stream.Data);
    }
    [Fact]
    public async Task FourComponentFrameStaysOpaque()
    {
        byte[] jpeg = EncodeRgbJpeg(8, 8);
        byte[] patched = (byte[])jpeg.Clone();
        int at = FindFrameHeightOffset(jpeg);
        patched[at + 4] = 4;
        Assert.True(DctFilter.TryParseFrame(patched, out int width, out int height, out int components));
        Assert.Equal(4, components);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDctPage(patched));
        List<string> argv = new() { "inputs/a.pdf", "--decode-level=all", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        QpdfDiagnostic warning = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedFeature, warning.Code);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        PdfStream stream = await OutputContentsAsync(host, pdf);
        Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject raw));
        PdfName name = Assert.IsType<PdfName>(raw);
        Assert.Equal("DCTDecode", name.DisplayText);
        Assert.Equal(patched, stream.Data);
    }

    [Fact]
    public async Task SpecializedLevelKeepsDctOpaqueSilently()
    {
        byte[] jpeg = EncodeGrayJpeg(8, 8);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDctPage(jpeg));
        List<string> argv = new() { "inputs/a.pdf", "--decode-level=specialized", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        PdfStream stream = await OutputContentsAsync(host, pdf);
        Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject raw));
        PdfName name = Assert.IsType<PdfName>(raw);
        Assert.Equal("DCTDecode", name.DisplayText);
        Assert.Equal(jpeg, stream.Data);
    }

    [Fact]
    public async Task JobParityHoldsForDecodeAll()
    {
        byte[] jpeg = EncodeGrayJpeg(8, 8);
        byte[] argvPdf = await SelectAsync(HostWith("inputs/a.pdf", BuildDctPage(jpeg)), "inputs/a.pdf", "outputs/a.pdf", "--decode-level=all");
        PdfStream argvStream = await OutputContentsAsync(HostWith("inputs/x.pdf", argvPdf), argvPdf);
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", BuildDctPage(jpeg));
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"decodeLevel\": \"all\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] jobPdf));
        PdfStream jobStream = await OutputContentsAsync(HostWith("inputs/y.pdf", jobPdf), jobPdf);
        Assert.Equal(argvStream.Data, jobStream.Data);
    }

    [Fact]
    public void OversizedFrameFailsBeforeAllocation()
    {
        byte[] jpeg = EncodeGrayJpeg(8, 8);
        byte[] patched = PatchFrameDimensions(jpeg, 30000, 30000);
        Assert.True(DctFilter.TryParseFrame(patched, out int width, out int height, out int components));
        Assert.Equal(30000, width);
        Assert.Equal(30000, height);
        Assert.Equal(1, components);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Throws<QpdfLimitException>(() => DctFilter.Decode(patched, QpdfLimits.Default, governor));
    }
}