using System.IO.Compression;
using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class CompressionTests
{
    private static readonly byte[] PageRaw = Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (Hello) Tj ET");

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] FlateBytes(byte[] raw)
    {
        return FlateBytesAt(raw, CompressionLevel.Optimal);
    }

    private static byte[] FlateBytesAt(byte[] raw, CompressionLevel level)
    {
        using MemoryStream output = new();
        using (ZLibStream compressor = new(output, level, leaveOpen: true))
        {
            compressor.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
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

    private static byte[] BuildRawPage()
    {
        return BuildFilteredPage("<< /Length " + PageRaw.Length.ToString() + " >>", PageRaw);
    }

    private static byte[] BuildFlatePage()
    {
        byte[] encoded = FlateBytes(PageRaw);
        return BuildFilteredPage("<< /Length " + encoded.Length.ToString() + " /Filter /FlateDecode >>", encoded);
    }

    private static byte[] BuildAsciiHexPage()
    {
        StringBuilder hex = new();
        foreach (byte b in PageRaw)
        {
            hex.Append(b.ToString("X2"));
        }

        hex.Append((char)62);
        byte[] encoded = Encoding.ASCII.GetBytes(hex.ToString());
        return BuildFilteredPage("<< /Length " + encoded.Length.ToString() + " /Filter /ASCIIHexDecode >>", encoded);
    }

    private static byte[] BuildDctPage()
    {
        byte[] fake = Encoding.ASCII.GetBytes("fake-jpeg-bytes");
        return BuildFilteredPage("<< /Length " + fake.Length.ToString() + " /Filter /DCTDecode >>", fake);
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

    private static byte[] DecodeFully(byte[] data, PdfDictionary dictionary)
    {
        IReadOnlyList<FilterStage> stages = FilterChain.Resolve(dictionary);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        return FilterChain.Decode(data, stages, QpdfLimits.Default, governor);
    }

    private static void AssertFilter(PdfDictionary dictionary, string expected)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject raw));
        PdfName name = Assert.IsType<PdfName>(raw);
        Assert.Equal(expected, name.DisplayText);
    }

    private static void AssertNoFilter(PdfDictionary dictionary)
    {
        Assert.False(dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject _));
    }

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
    }

    [Fact]
    public async Task DefaultPreservesRawStreamBytes()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildRawPage());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf");
        PdfStream stream = await OutputContentsAsync(host, pdf);
        AssertNoFilter(stream.Dictionary);
        Assert.Equal(PageRaw, stream.Data);
    }

    [Fact]
    public async Task StreamDataCompressFlatesRawStream()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildRawPage());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--stream-data=compress");
        PdfStream stream = await OutputContentsAsync(host, pdf);
        AssertFilter(stream.Dictionary, "FlateDecode");
        Assert.Equal(PageRaw, DecodeFully(stream.Data, stream.Dictionary));
    }

    [Fact]
    public async Task StreamDataUncompressDecodesHex()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildAsciiHexPage());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--stream-data=uncompress");
        PdfStream stream = await OutputContentsAsync(host, pdf);
        AssertNoFilter(stream.Dictionary);
        Assert.Equal(PageRaw, stream.Data);
    }

    [Fact]
    public async Task StreamDataPreserveKeepsEncoding()
    {
        byte[] input = BuildFlatePage();
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--stream-data=preserve");
        PdfStream stream = await OutputContentsAsync(host, pdf);
        AssertFilter(stream.Dictionary, "FlateDecode");
        Assert.Equal(PageRaw, DecodeFully(stream.Data, stream.Dictionary));
    }

    [Fact]
    public async Task CompressStreamsNoLeavesRaw()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildRawPage());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--compress-streams=n");
        PdfStream stream = await OutputContentsAsync(host, pdf);
        AssertNoFilter(stream.Dictionary);
        Assert.Equal(PageRaw, stream.Data);
    }

    [Fact]
    public async Task CompressStreamsBareDefaultsToYes()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildRawPage());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--compress-streams");
        PdfStream stream = await OutputContentsAsync(host, pdf);
        AssertFilter(stream.Dictionary, "FlateDecode");
        Assert.Equal(PageRaw, DecodeFully(stream.Data, stream.Dictionary));
    }

    [Fact]
    public async Task CompressYesKeepsFlateWithoutRecompress()
    {
        byte[] input = BuildFlatePage();
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--compress-streams=y");
        PdfStream stream = await OutputContentsAsync(host, pdf);
        AssertFilter(stream.Dictionary, "FlateDecode");
        PdfStream original = await OutputContentsAsync(HostWith("inputs/b.pdf", input), input);
        Assert.Equal(original.Data, stream.Data);
    }

    [Fact]
    public async Task RecompressFlateRewritesFlate()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildFlatePage());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--compress-streams=y", "--decode-level=generalized", "--recompress-flate", "--compression-level=1");
        PdfStream stream = await OutputContentsAsync(host, pdf);
        AssertFilter(stream.Dictionary, "FlateDecode");
        Assert.Equal(FlateBytesAt(PageRaw, CompressionLevel.Fastest), stream.Data);
        Assert.Equal(PageRaw, DecodeFully(stream.Data, stream.Dictionary));
    }

    [Fact]
    public async Task DecodeNoneWithCompressKeepsHex()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildAsciiHexPage());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--compress-streams=y");
        PdfStream stream = await OutputContentsAsync(host, pdf);
        AssertFilter(stream.Dictionary, "ASCIIHexDecode");
    }

    [Fact]
    public async Task DecodeGeneralizedUncompressesHex()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildAsciiHexPage());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--decode-level=generalized");
        PdfStream stream = await OutputContentsAsync(host, pdf);
        AssertNoFilter(stream.Dictionary);
        Assert.Equal(PageRaw, stream.Data);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("5")]
    [InlineData("9")]
    public async Task CompressionLevelsSucceed(string level)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildRawPage());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--stream-data=compress", "--compression-level=" + level);
        PdfStream stream = await OutputContentsAsync(host, pdf);
        AssertFilter(stream.Dictionary, "FlateDecode");
        Assert.Equal(PageRaw, DecodeFully(stream.Data, stream.Dictionary));
    }

    [Fact]
    public async Task DctBeyondLevelStaysOpaque()
    {
        byte[] input = BuildDctPage();
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--stream-data=compress");
        PdfStream stream = await OutputContentsAsync(host, pdf);
        AssertFilter(stream.Dictionary, "DCTDecode");
        PdfStream original = await OutputContentsAsync(HostWith("inputs/b.pdf", input), input);
        Assert.Equal(original.Data, stream.Data);
    }

    [Fact]
    public async Task DctAtAllWarnsAndStaysOpaque()
    {
        byte[] input = BuildDctPage();
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        List<string> argv = new() { "inputs/a.pdf", "--decode-level=all", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        QpdfDiagnostic warning = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedFeature, warning.Code);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        PdfStream stream = await OutputContentsAsync(host, pdf);
        AssertFilter(stream.Dictionary, "DCTDecode");
    }

    [Fact]
    public async Task ZopfliReportsUnavailable()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildRawPage());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--zopfli"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedFeature, diagnostic.Code);
        Assert.Contains("Zopfli", diagnostic.Message, StringComparison.Ordinal);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task JobJsonParityHolds()
    {
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", BuildRawPage());
        byte[] a = await SelectAsync(argvHost, "inputs/a.pdf", "outputs/a.pdf", "--stream-data=compress", "--compression-level=5", "--recompress-flate");
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", BuildRawPage());
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"streamData\": \"compress\", \"compressionLevel\": \"5\", \"recompressFlate\": \"\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("--stream-data=bogus")]
    [InlineData("--stream-data=")]
    [InlineData("--stream-data")]
    [InlineData("--decode-level=bogus")]
    [InlineData("--decode-level")]
    [InlineData("--compress-streams=maybe")]
    [InlineData("--compression-level=0")]
    [InlineData("--compression-level=10")]
    [InlineData("--compression-level=abc")]
    [InlineData("--compression-level")]
    public async Task InvalidValuesFail(params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--pages", ".", "1", "--", "outputs/a.pdf"]);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildRawPage());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("--stream-data=compress", "--stream-data=preserve")]
    [InlineData("--compress-streams=y", "--compress-streams=n")]
    [InlineData("--decode-level=none", "--decode-level=all")]
    [InlineData("--recompress-flate", "--recompress-flate")]
    [InlineData("--compression-level=5", "--compression-level=6")]
    public async Task DuplicateFlagsFail(params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--pages", ".", "1", "--", "outputs/a.pdf"]);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildRawPage());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task RecompressValuedFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildRawPage());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--recompress-flate=y", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JobJsonInvalidValuesFail()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildRawPage());
        QpdfExecutionResult level = await Qpdf.ExecuteJobAsync(
            host,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"compressionLevel\": \"10\"}",
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(level).Code);
        QpdfExecutionResult decode = await Qpdf.ExecuteJobAsync(
            host,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"decodeLevel\": \"bogus\"}",
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(decode).Code);
    }
}
