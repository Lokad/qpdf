using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using UglyToad.PdfPig;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class QdfTests
{
    private static readonly byte[] PageRaw = Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (Hello) Tj ET");

    private static readonly byte[] PageCrlf = Encoding.ASCII.GetBytes("BT /F1 24 Tf\r\n100 700 Td\r\n(Hello) Tj\r\nET");

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static async Task<InMemoryQpdfHost> HostWithFixture(string logical, string fixture)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), fixture));
        return HostWith(logical, bytes);
    }

    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    private static byte[] FlateBytes(byte[] raw)
    {
        using MemoryStream output = new();
        using (ZLibStream compressor = new(output, CompressionLevel.Optimal, leaveOpen: true))
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

    private static async Task<(QpdfExecutionResult Result, byte[] Pdf)> RunAsync(InMemoryQpdfHost host, params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        return (result, pdf);
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
    public async Task MarkerAndReadableLayout()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--qdf", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        string text = Encoding.ASCII.GetString(pdf);
        Assert.StartsWith("%PDF-1.4\n", text, StringComparison.Ordinal);
        Assert.Contains("\n%QDF-1.0\n", text, StringComparison.Ordinal);
        Assert.Contains("%% Original object ID: 3 0", text, StringComparison.Ordinal);
        Assert.Contains("%% Page 1", text, StringComparison.Ordinal);
        Assert.Contains("%% Contents for page 1", text, StringComparison.Ordinal);
        Assert.Contains("\nendobj\n\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("%% Original object ID: 1 0", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamsUncompressedByDefault()
    {
        byte[] flated = FlateBytes(PageRaw);
        byte[] input = BuildFilteredPage("<< /Length " + flated.Length.ToString() + " /Filter /FlateDecode >>", flated);
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWith("inputs/a.pdf", input), "--qdf", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        string text = Encoding.ASCII.GetString(pdf);
        Assert.DoesNotContain("FlateDecode", text, StringComparison.Ordinal);
        Assert.Contains(PageRaw.Length.ToString() + "\nendobj", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentNormalizedByDefault()
    {
        byte[] input = BuildFilteredPage("<< /Length " + PageCrlf.Length.ToString() + " >>", PageCrlf);
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWith("inputs/a.pdf", input), "--qdf", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        PdfStream stream = await OutputContentsAsync(HostWith("inputs/r.pdf", pdf), pdf);
        Assert.DoesNotContain((byte)13, stream.Data);
    }

    [Fact]
    public async Task LengthsAreIndirectObjects()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--qdf", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        PdfStream stream = await OutputContentsAsync(HostWith("inputs/r.pdf", pdf), pdf);
        Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("Length"), out QpdfObject raw));
        PdfReference reference = Assert.IsType<PdfReference>(raw);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(HostWith("inputs/s.pdf", pdf), QpdfPath.From("inputs/s.pdf"), CancellationToken.None);
        PdfInteger length = Assert.IsType<PdfInteger>(document.GetObject(reference.ObjectNumber));
        Assert.Equal(stream.Data.Length, length.Value);
    }
    [Fact]
    public async Task TwoPageClassicQdfKeepsStreamLengths()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--qdf", "--pages", ".", "1", ".", "2");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(HostWith("inputs/r.pdf", pdf), QpdfPath.From("inputs/r.pdf"), CancellationToken.None);
        Assert.Equal(2, document.PageCount);
        using MemoryStream buffer = new(pdf);
        using PdfDocument reread = PdfDocument.Open(buffer);
        Assert.Equal(2, reread.NumberOfPages);
    }

    [Fact]
    public async Task NoOriginalObjectIdsSuppressesComments()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--qdf", "--no-original-object-ids", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        string text = Encoding.ASCII.GetString(pdf);
        Assert.DoesNotContain("Original object ID", text, StringComparison.Ordinal);
        Assert.Contains("%QDF-1.0", text, StringComparison.Ordinal);
        Assert.Contains("%% Page 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalizeExplicitNoKeepsNewlines()
    {
        byte[] input = BuildFilteredPage("<< /Length " + PageCrlf.Length.ToString() + " >>", PageCrlf);
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWith("inputs/a.pdf", input), "--qdf", "--normalize-content=n", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        PdfStream stream = await OutputContentsAsync(HostWith("inputs/r.pdf", pdf), pdf);
        Assert.Contains((byte)13, stream.Data);
    }

        [Fact]
    public async Task ExplicitOverridesRestoreFlate()
    {
        byte[] input = BuildFilteredPage("<< /Length " + PageRaw.Length.ToString() + " >>", PageRaw);
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWith("inputs/a.pdf", input), "--qdf", "--normalize-content=n", "--compress-streams=y", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("FlateDecode", text, StringComparison.Ordinal);
        Assert.Contains("%QDF-1.0", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalizeWinsOverDecodeNone()
    {
        byte[] flated = FlateBytes(PageCrlf);
        byte[] input = BuildFilteredPage("<< /Length " + flated.Length.ToString() + " /Filter /FlateDecode >>", flated);
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(HostWith("inputs/a.pdf", input), "--qdf", "--decode-level=none", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        string text = Encoding.ASCII.GetString(pdf);
        Assert.DoesNotContain("FlateDecode", text, StringComparison.Ordinal);
        PdfStream stream = await OutputContentsAsync(HostWith("inputs/r.pdf", pdf), pdf);
        Assert.DoesNotContain((byte)13, stream.Data);
    }
    [Fact]
    public async Task RoundTripsThroughOwnReaderAndPig()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--qdf", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(HostWith("inputs/r.pdf", pdf), QpdfPath.From("inputs/r.pdf"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        using MemoryStream buffer = new(pdf);
        using PdfDocument reread = PdfDocument.Open(buffer);
        Assert.Equal(1, reread.NumberOfPages);
    }

    [Fact]
    public async Task JobParityMatchesArgvBytes()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult argvResult, byte[] a) = await RunAsync(argvHost, "--qdf", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, argvResult.ExitCode);
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"qdf\": \"\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("--json", "--qdf")]
    [InlineData("--check", "--qdf")]
    [InlineData("--qdf", "--qdf")]
    [InlineData("--qdf=x")]
    public async Task MalformedQdfFlagsFail(params string[] flags)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task ObjectStreamsGenerateProducesStreamedQdf()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--qdf", "--object-streams=generate", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        string text = Encoding.Latin1.GetString(pdf);
        Assert.Contains("%QDF-1.0", text, StringComparison.Ordinal);
        Assert.Contains("/Type /ObjStm", text, StringComparison.Ordinal);
        Assert.Contains("/Type /XRef", text, StringComparison.Ordinal);
        Assert.Contains("/W [ 1 ", text, StringComparison.Ordinal);
        Assert.Contains("%% Object stream: object", text, StringComparison.Ordinal);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(HostWith("inputs/r.pdf", pdf), QpdfPath.From("inputs/r.pdf"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        using MemoryStream buffer = new(pdf);
        using PdfDocument reread = PdfDocument.Open(buffer);
        Assert.Equal(1, reread.NumberOfPages);
    }

    [Fact]
    public async Task ObjectStreamsQdfOffsetsLandOnValues()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--qdf", "--object-streams=generate", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        string text = Encoding.Latin1.GetString(pdf);
        int headerAt = text.IndexOf("/Type /ObjStm", StringComparison.Ordinal);
        Assert.NotEqual(-1, headerAt);
        int firstMarker = text.IndexOf("/First ", headerAt, StringComparison.Ordinal);
        Assert.NotEqual(-1, firstMarker);
        int firstLineEnd = text.IndexOf("\n", firstMarker, StringComparison.Ordinal);
        int first = int.Parse(text.Substring(firstMarker + 7, firstLineEnd - firstMarker - 7), System.Globalization.CultureInfo.InvariantCulture);
        int streamMarker = text.IndexOf("stream\n", headerAt, StringComparison.Ordinal);
        Assert.NotEqual(-1, streamMarker);
        int payloadStart = streamMarker + 7;
        int cursor = payloadStart;
        List<long> offsets = new();
        while (pdf[cursor] != 37)
        {
            int lineEnd = Array.IndexOf(pdf, (byte)10, cursor);
            Assert.NotEqual(-1, lineEnd);
            string line = Encoding.ASCII.GetString(pdf, cursor, lineEnd - cursor);
            string[] parts = line.Split((char)32, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, parts.Length);
            offsets.Add(long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
            cursor = lineEnd + 1;
        }

        Assert.NotEmpty(offsets);
        foreach (long offset in offsets)
        {
            int at = payloadStart + first + (int)offset;
            Assert.Equal(60, pdf[at]);
            Assert.Equal(60, pdf[at + 1]);
        }
    }

    [Fact]
    public async Task ObjectStreamsQdfNoOriginalIdsSuppressesComments()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--qdf", "--no-original-object-ids", "--object-streams=generate", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        string text = Encoding.Latin1.GetString(pdf);
        Assert.DoesNotContain("Original object ID", text, StringComparison.Ordinal);
        Assert.Contains("%QDF-1.0", text, StringComparison.Ordinal);
        Assert.Contains("%% Object stream: object", text, StringComparison.Ordinal);
        Assert.Contains("/Type /XRef", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObjectStreamsQdfJobParityMatchesArgvBytes()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult argvResult, byte[] a) = await RunAsync(argvHost, "--qdf", "--object-streams=generate", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, argvResult.ExitCode);
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"qdf\": \"\", \"objectStreams\": \"generate\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("--no-original-object-ids=x")]
    [InlineData("--no-original-object-ids=")]
    [InlineData("--no-original-object-ids", "--no-original-object-ids")]
    public async Task MalformedNoOriginalObjectIdsFlagsFail(params string[] flags)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--pages", ".", "1", "--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task NoOriginalIdsJobParityHolds()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult argvResult, byte[] a) = await RunAsync(argvHost, "--qdf", "--no-original-object-ids", "--pages", ".", "1");
        Assert.Equal(QpdfExitCode.Success, argvResult.ExitCode);
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "qdf": "", "noOriginalObjectIds": ""}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }
}
