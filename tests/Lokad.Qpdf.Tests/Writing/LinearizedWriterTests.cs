using System.Text;
using Lokad.Qpdf.Hosting;
using UglyToad.PdfPig;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LinearizedWriterTests
{
    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

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

    private static async Task<(QpdfExecutionResult Result, byte[] Pdf)> RunAsync(InMemoryQpdfHost host, params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        return (result, pdf);
    }

    private static async Task CheckLinearizedAsync(byte[] pdf, int pages)
    {
        InMemoryQpdfHost host = HostWith("inputs/r.pdf", pdf);
        QpdfCheckResult check = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/r.pdf"), CancellationToken.None);
        Assert.Equal(pages, check.PageCount);
        QpdfCheckOutcome linearization = Assert.Single(check.Checks, outcome => outcome.Name == "linearization");
        Assert.Equal(QpdfCheckStatus.Passed, linearization.Status);
        using PdfDocument reread = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Equal(pages, reread.NumberOfPages);
    }

    [Fact]
    public async Task TwoPageLinearizePassesOwnCheck()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--pages", ".", "1", ".", "2", "--linearize");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await CheckLinearizedAsync(pdf, 2);
    }

    [Fact]
    public async Task SinglePageLinearizePassesOwnCheck()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--pages", ".", "1", "--linearize");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await CheckLinearizedAsync(pdf, 1);
    }

    [Fact]
    public async Task MergeLinearizePassesOwnCheck()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        host.AddInput(QpdfPath.From("inputs/b.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-2page.pdf")), QpdfInputOwnership.Copy);
        List<string> argv = new() { "inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--linearize", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        await CheckLinearizedAsync(pdf, 2);
    }

    [Fact]
    public async Task OutputsAreDeterministic()
    {
        InMemoryQpdfHost first = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        (_, byte[] a) = await RunAsync(first, "--pages", ".", "1", ".", "2", "--linearize");
        InMemoryQpdfHost second = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        (_, byte[] b) = await RunAsync(second, "--pages", ".", "1", ".", "2", "--linearize");
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task CompressedHintsPassOwnCheck()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--pages", ".", "1", ".", "2", "--linearize", "--stream-data=compress");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains("/FlateDecode", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
        await CheckLinearizedAsync(pdf, 2);
    }

    [Fact]
    public async Task QdfSilentlyDisabled()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--pages", ".", "1", ".", "2", "--linearize", "--qdf");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.DoesNotContain("%QDF-1.0", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
        await CheckLinearizedAsync(pdf, 2);
    }

    [Fact]
    public async Task ObjectStreamsGenerateFails()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--linearize", "--object-streams=generate", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.InvalidArguments);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task SplitPagesLinearizeEachGroup()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", ".", "2", "--split-pages", "--linearize", "--", "outputs/split.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/split-1.pdf"), out byte[] first));
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/split-2.pdf"), out byte[] second));
        await CheckLinearizedAsync(first, 1);
        await CheckLinearizedAsync(second, 1);
    }

    [Fact]
    public async Task StaticIdLinearizes()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--pages", ".", "1", "--linearize", "--static-id");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await CheckLinearizedAsync(pdf, 1);
    }

    [Fact]
    public async Task ThumbnailsLinearizePassesOwnCheck()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "thumb-1page.pdf");
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--pages", ".", "1", "--linearize");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await CheckLinearizedAsync(pdf, 1);
        InMemoryQpdfHost showHost = HostWith("inputs/r.pdf", pdf);
        List<string> lines = await ShowAsync(showHost, "inputs/r.pdf");
        Assert.Contains(" 1-2", lines);
    }

    [Fact]
    public async Task ThumbnailsLinearizeJobSucceeds()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "thumb-1page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            host,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"linearize\": \"\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        await CheckLinearizedAsync(pdf, 1);
    }

    [Fact]
    public async Task JsonCannotCombine()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--json", "--linearize", "--", "outputs/a.json" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.InvalidArguments);
    }

    [Fact]
    public async Task CheckCannotCombine()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--check", "--linearize" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
    }

    private static async Task<List<string>> ShowAsync(InMemoryQpdfHost host, string input)
    {
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation([input, "--show-linearization"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> lines = new();
        foreach (QpdfDiagnostic diagnostic in result.Diagnostics)
        {
            Assert.Equal(QpdfDiagnosticCode.LinearizationReport, diagnostic.Code);
            lines.Add(diagnostic.Message);
        }

        return lines;
    }

    private static byte[] PatchOutlineFirstObject(byte[] pdf, byte firstByte)
    {
        byte[] output = (byte[])pdf.Clone();
        int hintAt = IndexOf(pdf, Encoding.ASCII.GetBytes(" /S "));
        Assert.NotEqual(-1, hintAt);
        int outlineAt = IndexOf(pdf, Encoding.ASCII.GetBytes(" /O "), hintAt);
        Assert.NotEqual(-1, outlineAt);
        int valueAt = outlineAt + 4;
        int valueEnd = valueAt;
        while (pdf[valueEnd] >= (byte)48 && pdf[valueEnd] <= (byte)57)
        {
            valueEnd += 1;
        }

        int tableAt = valueAt;
        int tableValue = 0;
        for (int i = valueAt; i < valueEnd; i++)
        {
            tableValue = checked(tableValue * 10 + (pdf[i] - (byte)48));
        }

        int streamAt = IndexOf(pdf, Encoding.ASCII.GetBytes("stream\n"), valueEnd);
        Assert.NotEqual(-1, streamAt);
        int firstAt = streamAt + 7 + tableValue;
        output[firstAt] = 0;
        output[firstAt + 1] = 0;
        output[firstAt + 2] = 0;
        output[firstAt + 3] = firstByte;
        return output;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start = 0)
    {
        for (int i = start; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int k = 0; k < needle.Length; k++)
            {
                if (haystack[i + k] != needle[k])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public async Task OutlinesLinearizePassesOwnCheck()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "outlined-2page.pdf");
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--pages", ".", "1", ".", "2", "--linearize");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await CheckLinearizedAsync(pdf, 2);
        InMemoryQpdfHost showHost = HostWith("inputs/r.pdf", pdf);
        List<string> lines = await ShowAsync(showHost, "inputs/r.pdf");
        Assert.Contains("Outlines Hint Table", lines);
        Assert.Contains("first_object: 5", lines);
        Assert.Contains("nobjects: 3", lines);
        Assert.Contains(" 4-7", lines);
    }

    [Fact]
    public async Task OutlinesLinearizeJobSucceeds()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "outlined-2page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            host,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}, {\"file\": \".\", \"range\": \"2\"}], \"linearize\": \"\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        await CheckLinearizedAsync(pdf, 2);
    }

    [Fact]
    public async Task UseOutlinesRideFirstPage()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "outlined-use-2page.pdf");
        (QpdfExecutionResult result, byte[] pdf) = await RunAsync(host, "--pages", ".", "1", ".", "2", "--linearize");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await CheckLinearizedAsync(pdf, 2);
        InMemoryQpdfHost showHost = HostWith("inputs/r.pdf", pdf);
        List<string> lines = await ShowAsync(showHost, "inputs/r.pdf");
        Assert.Contains("first_object: 11", lines);
        Assert.Contains("nobjects: 3", lines);
        Assert.Contains(" 8-13", lines);
    }

    [Fact]
    public async Task DirectOutlinesFailExplicitly()
    {
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines << /First 9 0 R /Count 0 >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << >> >>",
            "<< /Length 37 >>",
            "<< /Length 38 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Title (Ch1) /Parent 1 0 R >>",
        };
        List<byte[]> streams = new()
        {
            Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (First) Tj ET"),
            Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (Second) Tj ET"),
        };
        List<byte[]> parts = new() { Encoding.ASCII.GetBytes("%PDF-1.4\n") };
        List<long> offsets = new();
        long position = parts[0].Length;
        int streamsSeen = 0;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj\n" + bodies[i] + "\n");
            if (number == 5 || number == 6)
            {
                byte[] data = streams[streamsSeen];
                streamsSeen += 1;
                byte[] streamOpen = Encoding.ASCII.GetBytes("stream\n");
                byte[] streamClose = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
                offsets.Add(position);
                position += open.Length + streamOpen.Length + data.Length + streamClose.Length;
                byte[] payload = new byte[open.Length + streamOpen.Length + data.Length + streamClose.Length];
                int at = 0;
                Buffer.BlockCopy(open, 0, payload, at, open.Length);
                at += open.Length;
                Buffer.BlockCopy(streamOpen, 0, payload, at, streamOpen.Length);
                at += streamOpen.Length;
                Buffer.BlockCopy(data, 0, payload, at, data.Length);
                at += data.Length;
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
        parts.Add(Encoding.ASCII.GetBytes(xref.ToString()));
        int total = 0;
        foreach (byte[] part in parts)
        {
            total += part.Length;
        }

        byte[] input = new byte[total];
        int cursor = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, input, cursor, part.Length);
            cursor += part.Length;
        }

        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", ".", "2", "--linearize", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.InvalidArguments);
    }

    [Fact]
    public async Task OutlineUnknownFirstObjectFailsCheck()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "outlined-2page.pdf");
        (_, byte[] pdf) = await RunAsync(host, "--pages", ".", "1", ".", "2", "--linearize");
        byte[] damaged = PatchOutlineFirstObject(pdf, 127);
        InMemoryQpdfHost damagedHost = HostWith("inputs/r.pdf", damaged);
        QpdfCheckResult check = await Qpdf.CheckAsync(damagedHost, QpdfPath.From("inputs/r.pdf"), CancellationToken.None);
        QpdfCheckOutcome linearization = Assert.Single(check.Checks, outcome => outcome.Name == "linearization");
        Assert.Equal(QpdfCheckStatus.Failed, linearization.Status);
        Assert.Contains("utline", linearization.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutlineOffsetMismatchFailsCheck()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "outlined-2page.pdf");
        (_, byte[] pdf) = await RunAsync(host, "--pages", ".", "1", ".", "2", "--linearize");
        byte[] damaged = PatchOutlineFirstObject(pdf, 3);
        InMemoryQpdfHost damagedHost = HostWith("inputs/r.pdf", damaged);
        QpdfCheckResult check = await Qpdf.CheckAsync(damagedHost, QpdfPath.From("inputs/r.pdf"), CancellationToken.None);
        QpdfCheckOutcome linearization = Assert.Single(check.Checks, outcome => outcome.Name == "linearization");
        Assert.Equal(QpdfCheckStatus.Failed, linearization.Status);
        Assert.Contains("Incorrect offset in outlines table.", linearization.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobParityMatchesArgvBytes()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        (_, byte[] a) = await RunAsync(argvHost, "--pages", ".", "1", "--linearize");
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(jobHost, "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"linearize\": \"\"}", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
        await CheckLinearizedAsync(b, 1);
    }

    [Fact]
    public async Task Pass1CapturedAlongsideFinalOutput()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", ".", "2", "--linearize", "--linearize-pass1=outputs/p1.bin", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/p1.bin"), out byte[] pass1));
        await CheckLinearizedAsync(pdf, 2);
        string finalText = Encoding.ASCII.GetString(pdf);
        string pass1Text = Encoding.ASCII.GetString(pass1);
        Assert.Contains("0000000000 00000 n ", pass1Text, StringComparison.Ordinal);
        Assert.DoesNotContain("0000000000 00000 n ", finalText, StringComparison.Ordinal);
        Assert.DoesNotContain(" /S ", pass1Text, StringComparison.Ordinal);
        Assert.Contains(" /S ", finalText, StringComparison.Ordinal);
        int hintAt = finalText.IndexOf("/H [ ", StringComparison.Ordinal);
        Assert.NotEqual(-1, hintAt);
        int hintEnd = finalText.IndexOf(" ]", hintAt, StringComparison.Ordinal);
        string[] hintParts = finalText.Substring(hintAt + 5, hintEnd - hintAt - 5).Split((char)32, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, hintParts.Length);
        Assert.Equal(pdf.Length, pass1.Length + int.Parse(hintParts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Pass1WithoutLinearizeIsIgnored()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--linearize-pass1=outputs/p1.bin", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/p1.bin"), out byte[] _));
    }

    [Fact]
    public async Task Pass1SpaceFormCaptures()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--linearize", "--linearize-pass1", "outputs/p1.bin", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/p1.bin"), out byte[] pass1));
        Assert.Contains("0000000000 00000 n ", Encoding.ASCII.GetString(pass1), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--linearize-pass1=")]
    [InlineData("--linearize-pass1")]
    public async Task Pass1MalformedFormsFail(string flag)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--linearize", flag, "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task Pass1DuplicateFails()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--linearize", "--linearize-pass1=a.bin", "--linearize-pass1=b.bin", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Pass1JobParityMatchesArgvBytes()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--linearize", "--linearize-pass1=outputs/p1.bin", "--", "outputs/a.pdf" };
        QpdfExecutionResult argvResult = await Qpdf.ExecuteAsync(argvHost, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argvResult.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/p1.bin"), out byte[] a));
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"linearize\": \"\", \"linearizePass1\": \"outputs/p1.bin\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/p1.bin"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"o.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"linearizePass1\": \"\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"o.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"linearizePass1\": true}")]
    public async Task Pass1MalformedJobKeysFail(string jobJson)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
    }
}
