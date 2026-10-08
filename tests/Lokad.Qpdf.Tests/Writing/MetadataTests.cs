using System.Text;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class MetadataTests
{
    private static readonly byte[] PageData = Encoding.ASCII.GetBytes("BT (Page) Tj ET");

    private static readonly byte[] XmpData = Encoding.ASCII.GetBytes("<?xpacket begin=\"x\"?><x:xmpmeta>Meta</x:xmpmeta>");

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] BuildMetaPdf(string catalogExtra, string trailerExtra, List<(string Dict, byte[]? StreamData)> extras)
    {
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R" + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            "<< /Length " + PageData.Length.ToString() + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        foreach ((string dict, byte[]? _) in extras)
        {
            bodies.Add(dict);
        }

        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[]? streamData = null;
            if (number == 4)
            {
                streamData = PageData;
            }
            else if (number > 5)
            {
                streamData = extras[number - 6].StreamData;
            }

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

        xref.Append("trailer\n<< /Size " + size.ToString() + " /Root 1 0 R" + trailerExtra + " >>\n");
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

    private static byte[] InfoFixture()
    {
        return BuildMetaPdf(
            string.Empty,
            " /Info 6 0 R",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Title (TitleText) /Author (AuthorText) /Producer (ProducerText) /CreationDate (D:20230101000000Z) /ModDate (D:20240101000000Z) >>", null),
            });
    }

    private static byte[] MetadataFixture()
    {
        return BuildMetaPdf(
            " /Metadata 6 0 R",
            string.Empty,
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Length " + XmpData.Length.ToString() + " /Type /Metadata /Subtype /XML >>", XmpData),
            });
    }

    private static byte[] InfoMetadataFixture()
    {
        return BuildMetaPdf(
            " /Metadata 7 0 R",
            " /Info 6 0 R",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Title (TitleText) /ModDate (D:20240101000000Z) >>", null),
                ("<< /Length " + XmpData.Length.ToString() + " /Type /Metadata /Subtype /XML >>", XmpData),
            });
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

    private static async Task<int> RereadPageCountAsync(InMemoryQpdfHost host, byte[] pdf)
    {
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, pdf, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
        return document.PageCount;
    }

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
    }

    [Fact]
    public async Task InfoPreservedByDefault()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", InfoFixture());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("/Info", text, StringComparison.Ordinal);
        Assert.Contains("(TitleText)", text, StringComparison.Ordinal);
        Assert.Contains("(AuthorText)", text, StringComparison.Ordinal);
        Assert.Contains("(ProducerText)", text, StringComparison.Ordinal);
        Assert.Contains("(D:20240101000000Z)", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task MetadataPreservedByDefault()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", MetadataFixture());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("/Metadata", text, StringComparison.Ordinal);
        Assert.Contains("x:xmpmeta", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task RemoveInfoKeepsOnlyModDate()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", InfoMetadataFixture());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--remove-info");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("/Info", text, StringComparison.Ordinal);
        Assert.Contains("(D:20240101000000Z)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(TitleText)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Metadata", text, StringComparison.Ordinal);
        Assert.DoesNotContain("x:xmpmeta", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task RemoveInfoWithoutModDateDropsInfo()
    {
        byte[] bytes = BuildMetaPdf(
            string.Empty,
            " /Info 6 0 R",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Title (TitleText) >>", null),
            });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--remove-info");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.DoesNotContain("/Info", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(TitleText)", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task RemoveMetadataDropsMetadataKeepsInfo()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", InfoMetadataFixture());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--remove-metadata");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.DoesNotContain("/Metadata", text, StringComparison.Ordinal);
        Assert.DoesNotContain("x:xmpmeta", text, StringComparison.Ordinal);
        Assert.Contains("(TitleText)", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task BothRemovalsDropBoth()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", InfoMetadataFixture());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--remove-info", "--remove-metadata");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.DoesNotContain("(TitleText)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Metadata", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task MergeKeepsPrimaryInfo()
    {
        byte[] aBytes = BuildMetaPdf(
            string.Empty,
            " /Info 6 0 R",
            new List<(string Dict, byte[]? StreamData)> { ("<< /Title (PrimaryTitle) >>", null) });
        byte[] bBytes = BuildMetaPdf(
            string.Empty,
            " /Info 6 0 R",
            new List<(string Dict, byte[]? StreamData)> { ("<< /Title (SecondaryTitle) >>", null) });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", aBytes);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), bBytes, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] pdf));
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("(PrimaryTitle)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(SecondaryTitle)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SplitGroupsCarryInfo()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", InfoFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--split-pages=9", "--pages", ".", "1", "--", "outputs/s.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/s-1.pdf"), out byte[] pdf));
        Assert.Contains("(TitleText)", Encoding.ASCII.GetString(pdf), StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task InfoSurvivesObjectStreams()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", InfoFixture());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--object-streams=generate");
        string text = await StreamObservation.PdfText(pdf);
        Assert.Contains("/Type /ObjStm", text, StringComparison.Ordinal);
        Assert.Contains("(TitleText)", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task JobJsonParityHolds()
    {
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", InfoMetadataFixture());
        byte[] a = await SelectAsync(argvHost, "inputs/a.pdf", "outputs/a.pdf", "--remove-info");
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", InfoMetadataFixture());
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"removeInfo\": \"\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("--remove-info", "--remove-info")]
    [InlineData("--remove-metadata", "--remove-metadata")]
    public async Task DuplicateFlagsFail(params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--pages", ".", "1", "--", "outputs/a.pdf"]);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", InfoFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("--remove-info=x")]
    [InlineData("--remove-metadata=")]
    public async Task ValuedFlagsFail(params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--pages", ".", "1", "--", "outputs/a.pdf"]);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", InfoFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JobJsonInvalidValuesFail()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", InfoFixture());
        QpdfExecutionResult info = await Qpdf.ExecuteJobAsync(
            host,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"removeInfo\": 42}",
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(info).Code);
        QpdfExecutionResult metadata = await Qpdf.ExecuteJobAsync(
            host,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"removeMetadata\": \"yes\"}",
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(metadata).Code);
    }

    [Theory]
    [InlineData("--remove-info")]
    [InlineData("--remove-metadata")]
    public async Task MetadataEditsYieldToExhaustedBudget(params string[] flags)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", InfoMetadataFixture());
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1" };
        argv.InsertRange(1, flags);
        argv.AddRange(["--", "outputs/a.pdf"]);
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(argv),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }
}
