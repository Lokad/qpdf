using System.Text;
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
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"global\": {\"parserMaxErrors\": 5}}")]
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
    [InlineData("--no-default-limits")]
    [InlineData("--no-default-limits=x")]
    public async Task NoDefaultLimitsRefused(string flag)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDeepPage(10));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", flag]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task NoDefaultLimitsRefusedEverywhere()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildDeepPage(10));
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(host, "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"global\": {\"noDefaultLimits\": \"\"}}", CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(job).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 1L };
        QpdfExecutionResult starved = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["--no-default-limits"]), new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(starved).Code);
    }

    [Fact]
    public async Task FlagOverridesTighterHostBudget()
    {
        byte[] input = BuildDeepPage(300);
        InMemoryQpdfHost plain = HostWith("inputs/a.pdf", input);
        QpdfLimits tight = QpdfLimits.Default with { MaxNestingDepth = 10L };
        QpdfExecutionResult denied = await Qpdf.ExecuteAsync(plain, new QpdfCommandInvocation(["inputs/a.pdf", "--check"]), new QpdfExecutionOptions(tight), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(denied).Code);
        InMemoryQpdfHost raised = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult allowed = await Qpdf.ExecuteAsync(raised, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--parser-max-nesting=499"]), new QpdfExecutionOptions(tight), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, allowed.ExitCode);
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
    [InlineData("--parser-max-errors=5")]
    public async Task BadContainerFlagsFail(string flag)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", BuildBigPage());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", flag]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
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
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"global\": {\"parserMaxErrors\": 5}}")]
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
        QpdfExecutionResult allowed = await Qpdf.ExecuteAsync(raised, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--parser-max-container-size=1000"]), new QpdfExecutionOptions(tight), CancellationToken.None);
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
}
