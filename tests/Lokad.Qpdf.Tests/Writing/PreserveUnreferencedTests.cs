using System.Text;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class PreserveUnreferencedTests
{
    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] BuildOrphanPdf(string catalogExtra, List<(string Dict, byte[]? StreamData)> extras)
    {
        byte[] pageData = Encoding.ASCII.GetBytes("BT (Page) Tj ET");
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R" + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + pageData.Length.ToString() + " >>",
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
                streamData = pageData;
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

    private static byte[] OrphanFixture()
    {
        return BuildOrphanPdf(string.Empty, new List<(string Dict, byte[]? StreamData)>
        {
            ("<< /Marker (OrphanOne) >>", null),
            ("<< /Length 12 >>", Encoding.ASCII.GetBytes("HELLO-ORPHAN")),
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
    public async Task DefaultDropsUnreferencedObjects()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", OrphanFixture());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.DoesNotContain("OrphanOne", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HELLO-ORPHAN", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task PreserveKeepsUnreferencedObjects()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", OrphanFixture());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--preserve-unreferenced");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("OrphanOne", text, StringComparison.Ordinal);
        Assert.Contains("HELLO-ORPHAN", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task PreserveSkipsOldDocumentStructure()
    {
        byte[] bytes = BuildOrphanPdf(
            " /NeedsRendering true",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1 1] /Contents 4 0 R /StructParents 424242 >>", null),
            });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--preserve-unreferenced");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.DoesNotContain("NeedsRendering", text, StringComparison.Ordinal);
        Assert.DoesNotContain("424242", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task PreserveSkipsUnpreservableWithWarning()
    {
        byte[] bytes = BuildOrphanPdf(
            string.Empty,
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /BackRef 1 0 R >>", null),
                ("<< /Length 12 >>", Encoding.ASCII.GetBytes("HELLO-ORPHAN")),
            });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        List<string> argv = new() { "inputs/a.pdf", "--preserve-unreferenced", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        QpdfDiagnostic warning = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedFeature, warning.Code);
        Assert.Equal(QpdfDiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("Skipped 1", warning.Message, StringComparison.Ordinal);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.Contains("HELLO-ORPHAN", Encoding.ASCII.GetString(pdf), StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task PreserveAcrossMergeKeepsBothOrphans()
    {
        byte[] aBytes = BuildOrphanPdf(
            string.Empty,
            new List<(string Dict, byte[]? StreamData)> { ("<< /Marker (AlphaOrphan) >>", null) });
        byte[] bBytes = BuildOrphanPdf(
            string.Empty,
            new List<(string Dict, byte[]? StreamData)> { ("<< /Marker (BetaOrphan) >>", null) });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", aBytes);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), bBytes, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--preserve-unreferenced", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] pdf));
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("AlphaOrphan", text, StringComparison.Ordinal);
        Assert.Contains("BetaOrphan", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreserveAcrossSplitKeepsOrphansPerGroup()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", OrphanFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--preserve-unreferenced", "--pages", ".", "1", "--split-pages=9", "--", "outputs/s.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/s-1.pdf"), out byte[] pdf));
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("OrphanOne", text, StringComparison.Ordinal);
        Assert.Contains("HELLO-ORPHAN", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreserveWithGenerateCompressesOrphans()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", OrphanFixture());
        byte[] pdf = await SelectAsync(host, "inputs/a.pdf", "outputs/a.pdf", "--preserve-unreferenced", "--object-streams=generate");
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("/Type /ObjStm", text, StringComparison.Ordinal);
        Assert.Contains("OrphanOne", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task JobJsonParityHolds()
    {
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", OrphanFixture());
        byte[] a = await SelectAsync(argvHost, "inputs/a.pdf", "outputs/a.pdf", "--preserve-unreferenced");
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", OrphanFixture());
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"preserveUnreferenced\": \"\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task DuplicateFlagFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", OrphanFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--preserve-unreferenced", "--preserve-unreferenced", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("--preserve-unreferenced=x")]
    [InlineData("--preserve-unreferenced=")]
    public async Task ValuedFlagFails(params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--pages", ".", "1", "--", "outputs/a.pdf"]);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", OrphanFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JobJsonInvalidValueFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", OrphanFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"preserveUnreferenced\": 42}",
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }
}
