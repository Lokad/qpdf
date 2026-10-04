using System.Text;
using System.IO.Compression;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class GlobalLimitsTests
{
    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    private static async Task<InMemoryQpdfHost> HostWithFixture(string logical, string fixture)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), fixture));
        return HostWith(logical, bytes);
    }

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
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

    private static byte[] BuildDeepFilterPage(int stages)
    {
        byte[] payload = Encoding.ASCII.GetBytes("BT (Deep) Tj ET");
        for (int i = 0; i < stages; i++)
        {
            payload = FlateBytes(payload);
        }

        StringBuilder filters = new();
        filters.Append((char)91);
        for (int i = 0; i < stages; i++)
        {
            if (i > 0)
            {
                filters.Append((char)32);
            }

            filters.Append("/FlateDecode");
        }

        filters.Append((char)93);
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            "<< /Length " + payload.Length.ToString() + " /Filter " + filters.ToString() + " >>",
        };
        byte[] content = payload;
        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4" + (char)10);
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj" + (char)10 + bodies[i] + (char)10);
            if (number == 4)
            {
                byte[] streamOpen = Encoding.ASCII.GetBytes("stream" + (char)10);
                byte[] streamClose = Encoding.ASCII.GetBytes((char)10 + "endstream" + (char)10 + "endobj" + (char)10);
                offsets.Add(position);
                position += open.Length + streamOpen.Length + content.Length + streamClose.Length;
                byte[] combined = new byte[open.Length + streamOpen.Length + content.Length + streamClose.Length];
                int at = 0;
                Buffer.BlockCopy(open, 0, combined, at, open.Length);
                at += open.Length;
                Buffer.BlockCopy(streamOpen, 0, combined, at, streamOpen.Length);
                at += streamOpen.Length;
                Buffer.BlockCopy(content, 0, combined, at, content.Length);
                at += content.Length;
                Buffer.BlockCopy(streamClose, 0, combined, at, streamClose.Length);
                parts.Add(combined);
                continue;
            }

            byte[] close = Encoding.ASCII.GetBytes("endobj" + (char)10);
            offsets.Add(position);
            position += open.Length + close.Length;
            parts.Add(open);
            parts.Add(close);
        }

        int size = bodies.Count + 1;
        StringBuilder xref = new();
        xref.Append("xref" + (char)10 + "0 " + size.ToString() + (char)10);
        xref.Append("0000000000 65535 f " + (char)10);
        foreach (long offset in offsets)
        {
            xref.Append(offset.ToString("D10") + " 00000 n " + (char)10);
        }

        xref.Append("trailer" + (char)10 + "<< /Size " + size.ToString() + " /Root 1 0 R >>" + (char)10);
        xref.Append("startxref" + (char)10 + position.ToString() + (char)10 + "%%EOF" + (char)10);
        parts.Add(Encoding.ASCII.GetBytes(xref.ToString()));
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

    private static byte[] BuildDeepPage(int depth)
    {
        StringBuilder nested = new();
        for (int i = 0; i < depth; i++)
        {
            nested.Append((char)91);
        }

        nested.Append((char)49);
        for (int i = 0; i < depth; i++)
        {
            nested.Append((char)93);
        }

        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R /Deep " + nested.ToString() + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            "<< /Length 15 >>",
        };
        byte[] content = Encoding.ASCII.GetBytes("BT (Page) Tj ET");
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
                position += open.Length + streamOpen.Length + content.Length + streamClose.Length;
                byte[] payload = new byte[open.Length + streamOpen.Length + content.Length + streamClose.Length];
                int at = 0;
                Buffer.BlockCopy(open, 0, payload, at, open.Length);
                at += open.Length;
                Buffer.BlockCopy(streamOpen, 0, payload, at, streamOpen.Length);
                at += streamOpen.Length;
                Buffer.BlockCopy(content, 0, payload, at, content.Length);
                at += content.Length;
                Buffer.BlockCopy(streamClose, 0, payload, at, streamClose.Length);
                parts.Add(payload);
                continue;
            }

            byte[] close = Encoding.ASCII.GetBytes("endobj\n");
            offsets.Add(position);
            position += open.Length + close.Length;
            parts.Add(open);
            parts.Add(close);
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

        byte[] output = new byte[total];
        int cursor = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, output, cursor, part.Length);
            cursor += part.Length;
        }

        return output;
    }

    [Fact]
    public async Task DefaultNestingRejectsDeepCatalog()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDeepPage(300));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Theory]
    [InlineData("--parser-max-nesting=499")]
    [InlineData("--parser-max-nesting", "499")]
    public async Task RaisedCapParsesDeepCatalog(params string[] extra)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDeepPage(300));
        List<string> argv = new() { "inputs/a.pdf", "--check" };
        argv.AddRange(extra);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
    }

    [Theory]
    [InlineData("--parser-max-nesting=")]
    [InlineData("--parser-max-nesting=x")]
    [InlineData("--parser-max-nesting=-1")]
    [InlineData("--parser-max-nesting=500")]
    [InlineData("--parser-max-nesting=12345678901")]
    [InlineData("--parser-max-nesting")]
    public async Task BadNestingValuesFail(string flag)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDeepPage(300));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", flag]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task DuplicateNestingFlagFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDeepPage(300));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--parser-max-nesting=499", "--parser-max-nesting=499"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task CeilingMessageNamesTheMaximum()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDeepPage(300));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--parser-max-nesting=500"]), CancellationToken.None);
        Assert.Contains("499", SingleError(result).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedGlobalParityHolds()
    {
        byte[] input = BuildDeepPage(300);
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--parser-max-nesting=499", "--", "outputs/a.pdf" };
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult argvResult = await Qpdf.ExecuteAsync(argvHost, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argvResult.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult jobResult = await Qpdf.ExecuteJobAsync(jobHost, "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"global\": {\"parserMaxNesting\": 499}}", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, jobResult.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showNpages\": true, \"global\": {\"parserMaxNesting\": 499}}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"jsonInput\": true, \"global\": {\"parserMaxNesting\": 499}}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"global\": {\"parserMaxNesting\": 500}}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"global\": {\"parserMaxNesting\": -1}}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"global\": {\"bogus\": 1}}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"global\": 5}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"maxStreamFilters\": 1}")]
    public async Task BadNestedGlobalFails(string job)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDeepPage(300));
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("--no-default-limits=x")]
    public async Task ValuedNoDefaultLimitsFails(string flag)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDeepPage(10));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", flag]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task NoDefaultLimitsAccepted()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDeepPage(10));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--no-default-limits"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
    }

    [Fact]
    public async Task NoDefaultLimitsNeedsInput()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDeepPage(10));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["--no-default-limits"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task NoDefaultLimitsDuplicateFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDeepPage(10));
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--no-default-limits", "--no-default-limits"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(argv).Code);
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", BuildDeepPage(10));
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(jobHost, "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"global\": {\"noDefaultLimits\": true, \"noDefaultLimits\": false}}", CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(job).Code);
    }

    [Fact]
    public async Task NoDefaultLimitsDecodesOverLimitFilters()
    {
        byte[] input = BuildDeepFilterPage(30);
        InMemoryQpdfHost capped = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult opaque = await Qpdf.ExecuteAsync(capped, new QpdfCommandInvocation(["inputs/a.pdf", "--stream-data=uncompress", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, opaque.ExitCode);
        Assert.True(capped.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
        InMemoryQpdfHost lifted = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult decoded = await Qpdf.ExecuteAsync(lifted, new QpdfCommandInvocation(["inputs/a.pdf", "--stream-data=uncompress", "--no-default-limits", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, decoded.ExitCode);
        Assert.True(lifted.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] plain));
        InMemoryQpdfHost explicitHost = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult explicitCap = await Qpdf.ExecuteAsync(explicitHost, new QpdfCommandInvocation(["inputs/a.pdf", "--stream-data=uncompress", "--max-stream-filters=30", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, explicitCap.ExitCode);
        Assert.True(explicitHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] expected));
        Assert.Equal(expected, plain);
    }

    [Fact]
    public async Task NoDefaultLimitsExplicitFlagWins()
    {
        byte[] input = BuildDeepFilterPage(2);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--stream-data=uncompress", "--no-default-limits", "--max-stream-filters=1", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task NoDefaultLimitsKeepsNestingCap()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDeepPage(300));
        QpdfExecutionResult capped = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--no-default-limits"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(capped).Code);
        InMemoryQpdfHost raised = HostWith("inputs/a.pdf", BuildDeepPage(300));
        QpdfExecutionResult allowed = await Qpdf.ExecuteAsync(raised, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--no-default-limits", "--parser-max-nesting=499"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, allowed.ExitCode);
    }

    [Fact]
    public async Task NoDefaultLimitsRespectsTightHost()
    {
        byte[] input = BuildBigPage();
        QpdfLimits tight = QpdfLimits.Default with { MaxContainerSize = 10L };
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--no-default-limits"]), new QpdfExecutionOptions(tight), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task NoDefaultLimitsJobParityHolds()
    {
        byte[] input = BuildDeepFilterPage(30);
        List<string> argv = new() { "inputs/a.pdf", "--stream-data=uncompress", "--no-default-limits", "--pages", ".", "1", "--", "outputs/a.pdf" };
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult argvResult = await Qpdf.ExecuteAsync(argvHost, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argvResult.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult jobResult = await Qpdf.ExecuteJobAsync(jobHost, "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"streamData\": \"uncompress\", \"global\": {\"noDefaultLimits\": true}}", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, jobResult.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task NoDefaultLimitsRejectsRestrictedCombinations()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDeepPage(10));
        QpdfExecutionResult show = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-npages", "--no-default-limits"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, show.ExitCode);
        QpdfExecutionResult import = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--json-input", "--no-default-limits", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(import).Code);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(host, "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"jsonInput\": true, \"global\": {\"noDefaultLimits\": true}}", CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(job).Code);
    }
[Fact]
    public async Task FlagCannotRaiseTighterHostBudget()
    {
        byte[] input = BuildDeepPage(300);
        InMemoryQpdfHost plain = HostWith("inputs/a.pdf", input);
        QpdfLimits tight = QpdfLimits.Default with { MaxNestingDepth = 10L };
        QpdfExecutionResult denied = await Qpdf.ExecuteAsync(plain, new QpdfCommandInvocation(["inputs/a.pdf", "--check"]), new QpdfExecutionOptions(tight), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(denied).Code);
        InMemoryQpdfHost raised = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult capped = await Qpdf.ExecuteAsync(raised, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--parser-max-nesting=499"]), new QpdfExecutionOptions(tight), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(capped).Code);
        Assert.False(raised.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task FlagMayTightenBelowHostCeiling()
    {
        byte[] input = BuildDeepPage(300);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        QpdfLimits loose = QpdfLimits.Default with { MaxNestingDepth = 600L };
        QpdfExecutionResult allowed = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--parser-max-nesting=499"]), new QpdfExecutionOptions(loose), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, allowed.ExitCode);
    }

    [Fact]
    public async Task FlagCannotRaiseTighterFilterBudget()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildBigPage());
        QpdfLimits tight = QpdfLimits.Default with { MaxStreamFilters = 1L };
        QpdfExecutionResult capped = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--max-stream-filters=25"]), new QpdfExecutionOptions(tight), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(capped).Code);
    }

    private static byte[] BuildBigPage()
    {
        StringBuilder big = new();
        big.Append((char)91);
        for (int i = 0; i < 100; i++)
        {
            if (i > 0)
            {
                big.Append((char)32);
            }

            big.Append(i.ToString());
        }

        big.Append((char)93);
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R /Big " + big.ToString() + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            "<< /Length 15 >>",
        };
        byte[] content = Encoding.ASCII.GetBytes("BT (Page) Tj ET");
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
                position += open.Length + streamOpen.Length + content.Length + streamClose.Length;
                byte[] payload = new byte[open.Length + streamOpen.Length + content.Length + streamClose.Length];
                int at = 0;
                Buffer.BlockCopy(open, 0, payload, at, open.Length);
                at += open.Length;
                Buffer.BlockCopy(streamOpen, 0, payload, at, streamOpen.Length);
                at += streamOpen.Length;
                Buffer.BlockCopy(content, 0, payload, at, content.Length);
                at += content.Length;
                Buffer.BlockCopy(streamClose, 0, payload, at, streamClose.Length);
                parts.Add(payload);
                continue;
            }

            byte[] close = Encoding.ASCII.GetBytes("endobj\n");
            offsets.Add(position);
            position += open.Length + close.Length;
            parts.Add(open);
            parts.Add(close);
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

        byte[] output = new byte[total];
        int cursor = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, output, cursor, part.Length);
            cursor += part.Length;
        }

        return output;
    }

    private static int FindMarker(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
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
    private static byte[] WithBrokenStartXref(byte[] bytes)
    {
        byte[] copy = (byte[])bytes.Clone();
        byte[] marker = Encoding.ASCII.GetBytes("startxref\n");
        int at = FindMarker(copy, marker);
        int digits = at + marker.Length;
        int end = digits;
        while (end < copy.Length && copy[end] >= (byte)48 && copy[end] <= (byte)57)
        {
            end += 1;
        }

        for (int i = digits; i < end; i++)
        {
            copy[i] = (byte)57;
        }

        return copy;
    }

    [Fact]
    public async Task TightContainerCapFailsStrictParsing()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildBigPage());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--parser-max-container-size=10"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Theory]
    [InlineData("--parser-max-container-size=100")]
    [InlineData("--parser-max-container-size-damaged=100")]
    public async Task LooseContainerCapsParse(string flag)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildBigPage());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", flag]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
    }

    [Fact]
    public async Task SmallDictionaryCapFails()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--parser-max-container-size=1"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task DamagedCapGovernsRepair()
    {
        InMemoryQpdfHost broken = HostWith("inputs/a.pdf", WithBrokenStartXref(BuildBigPage()));
        QpdfExecutionResult repaired = await Qpdf.ExecuteAsync(broken, new QpdfCommandInvocation(["inputs/a.pdf", "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, repaired.ExitCode);
        InMemoryQpdfHost capped = HostWith("inputs/a.pdf", WithBrokenStartXref(BuildBigPage()));
        QpdfExecutionResult refused = await Qpdf.ExecuteAsync(capped, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--parser-max-container-size-damaged=10"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(refused).Code);
    }

    [Theory]
    [InlineData("--parser-max-container-size")]
    [InlineData("--parser-max-container-size=")]
    [InlineData("--parser-max-container-size=x")]
    [InlineData("--parser-max-container-size=-1")]
    [InlineData("--parser-max-container-size-damaged=")]
    [InlineData("--parser-max-container-size-damaged=x")]
    [InlineData("--parser-max-errors")]
    public async Task BadContainerFlagsFail(string flag)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildBigPage());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", flag]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task ParserMaxErrorsDefaultIsFifteen()
    {
        Assert.Equal(15L, QpdfLimits.Default.MaxParserErrors);
    }

    [Fact]
    public void ParserMaxErrorsValidationRejectsNonPositive()
    {
        QpdfLimits zero = QpdfLimits.Default with { MaxParserErrors = 0L };
        Assert.Throws<ArgumentOutOfRangeException>(() => zero.Validate());
        QpdfLimits negative = QpdfLimits.Default with { MaxParserErrors = -1L };
        Assert.Throws<ArgumentOutOfRangeException>(() => negative.Validate());
    }

    [Theory]
    [InlineData("--parser-max-errors")]
    [InlineData("--parser-max-errors=")]
    [InlineData("--parser-max-errors=x")]
    [InlineData("--parser-max-errors=-1")]
    [InlineData("--parser-max-errors=0")]
    [InlineData("--parser-max-errors=12345678901")]
    public async Task BadParserMaxErrorsValuesFail(string flag)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildBigPage());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", flag]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task DuplicateParserMaxErrorsFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildBigPage());
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--parser-max-errors=5", "--parser-max-errors=5"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(argv).Code);
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", BuildBigPage());
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(jobHost, "{\"inputFile\": \"inputs/a.pdf\", \"showNpages\": true, \"global\": {\"parserMaxErrors\": 5, \"parserMaxErrors\": 6}}", CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(job).Code);
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showNpages\": true, \"global\": {\"parserMaxErrors\": 0}}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showNpages\": true, \"global\": {\"parserMaxErrors\": -1}}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showNpages\": true, \"global\": {\"parserMaxErrors\": \"bogus\"}}")]
    public async Task BadParserMaxErrorsJobKeysFail(string job)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildBigPage());
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task ParserMaxErrorsRespectsTightHost()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildBigPage());
        QpdfLimits tight = QpdfLimits.Default with { MaxParserErrors = 1L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--parser-max-errors=5"]), new QpdfExecutionOptions(tight), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task DuplicateContainerFlagsFail()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildBigPage());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--parser-max-container-size=100", "--parser-max-container-size=100"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task ContainerCapsJobParityHolds()
    {
        byte[] input = BuildBigPage();
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--parser-max-container-size=1000", "--parser-max-container-size-damaged=1000", "--", "outputs/a.pdf" };
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult argvResult = await Qpdf.ExecuteAsync(argvHost, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argvResult.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult jobResult = await Qpdf.ExecuteJobAsync(jobHost, "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"global\": {\"parserMaxContainerSize\": 1000, \"parserMaxContainerSizeDamaged\": 1000}}", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, jobResult.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showNpages\": true, \"global\": {\"parserMaxContainerSize\": 100}}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"jsonInput\": true, \"global\": {\"parserMaxContainerSizeDamaged\": 100}}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"global\": {\"parserMaxContainerSize\": -1}}")]
    public async Task BadNestedContainerKeysFail(string job)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildBigPage());
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task HostContainerBudgetInterplay()
    {
        byte[] input = BuildBigPage();
        QpdfLimits tight = QpdfLimits.Default with { MaxContainerSize = 10L };
        InMemoryQpdfHost plain = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult denied = await Qpdf.ExecuteAsync(plain, new QpdfCommandInvocation(["inputs/a.pdf", "--check"]), new QpdfExecutionOptions(tight), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(denied).Code);
        InMemoryQpdfHost raised = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult capped = await Qpdf.ExecuteAsync(raised, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--parser-max-container-size=1000"]), new QpdfExecutionOptions(tight), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(capped).Code);
        Assert.False(raised.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task FlagMayTightenBelowContainerCeiling()
    {
        byte[] input = BuildBigPage();
        QpdfLimits loose = QpdfLimits.Default with { MaxContainerSize = 1000000L };
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult allowed = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--parser-max-container-size=1000"]), new QpdfExecutionOptions(loose), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, allowed.ExitCode);
    }

    [Fact]
    public async Task NegativeContainerBudgetsFailValidation()
    {
        QpdfLimits tight = QpdfLimits.Default with { MaxContainerSize = -1L };
        Assert.Throws<ArgumentOutOfRangeException>(() => tight.Validate());
        QpdfLimits damaged = QpdfLimits.Default with { MaxContainerSizeDamaged = -1L };
        Assert.Throws<ArgumentOutOfRangeException>(() => damaged.Validate());
    }

    [Fact]
    public async Task GlobalBlockMatchesBareFlags()
    {
        InMemoryQpdfHost plain = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult bare = await Qpdf.ExecuteAsync(plain, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, bare.ExitCode);
        Assert.True(plain.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));
        InMemoryQpdfHost blocked = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult wrapped = await Qpdf.ExecuteAsync(blocked, new QpdfCommandInvocation(["--global", "--max-stream-filters=25", "--", "inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, wrapped.ExitCode);
        Assert.True(blocked.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task GlobalBlockAcceptsNestingLimit()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDeepPage(300));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["--global", "--parser-max-nesting", "499", "--", "inputs/a.pdf", "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
    }

    [Fact]
    public async Task GlobalBlockAcceptsLimitsRemoval()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["--global", "--no-default-limits", "--", "inputs/a.pdf", "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
    }

    [Theory]
    [InlineData("--global", "--pages", "--", "inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf")]
    [InlineData("--global", "--compress-streams=y", "--", "inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf")]
    [InlineData("--global", "--global", "--max-stream-filters=25", "--", "--", "inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf")]
    [InlineData("--global", "--max-stream-filters=25")]
    [InlineData("--global", "--max-stream-filters=25", "--", "--max-stream-filters=26", "inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf")]
    public async Task BadGlobalBlocksFail(params string[] argv)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task GlobalBlockYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["--global", "--max-stream-filters=25", "--", "inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }
}
