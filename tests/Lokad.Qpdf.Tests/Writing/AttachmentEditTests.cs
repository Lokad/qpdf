using System.Text;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentEditTests
{
    private static readonly byte[] PageData = Encoding.ASCII.GetBytes("BT (Page) Tj ET");

    private static readonly byte[] PayloadA = Encoding.ASCII.GetBytes("First payload bytes.");

    private static readonly byte[] PayloadB = Encoding.ASCII.GetBytes("Second payload, a bit longer than the first one.");

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] BuildPdf(string catalogExtra, string trailerExtra, List<(string Dict, byte[]? StreamData)> extras)
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

    private static byte[] SeededFixture()
    {
        return BuildPdf(
            " /Names 6 0 R",
            string.Empty,
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /EmbeddedFiles 7 0 R >>", null),
                ("<< /Names [(seed.txt) 8 0 R] >>", null),
                ("<< /Type /Filespec /F (seed.txt) /UF (seed.txt) /EF << /F 9 0 R /UF 9 0 R >> >>", null),
                ("<< /Type /EmbeddedFile /Length " + PayloadA.Length.ToString() + " /Params << /Size " + PayloadA.Length.ToString() + " >> >>", PayloadA),
            });
    }

    private static async Task<int> RereadPageCountAsync(InMemoryQpdfHost host, byte[] pdf)
    {
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, pdf, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
        return document.PageCount;
    }

    private static async Task<IReadOnlyList<QpdfAttachmentInfo>> ListAsync(InMemoryQpdfHost host, byte[] pdf)
    {
        QpdfPath reread = QpdfPath.From("inputs/listed.pdf");
        host.AddInput(reread, pdf, QpdfInputOwnership.Copy);
        return await Qpdf.ListAttachmentsAsync(host, reread, CancellationToken.None);
    }

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
    }

    [Fact]
    public async Task AddSingleAttachment()
    {
        byte[] input = BuildPdf(string.Empty, string.Empty, new List<(string Dict, byte[]? StreamData)>());
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        host.AddInput(QpdfPath.From("files/note.bin"), PayloadA, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--add-attachment", "files/note.bin", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("/Names", text, StringComparison.Ordinal);
        Assert.Contains("/PageMode /UseAttachments", text, StringComparison.Ordinal);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
        IReadOnlyList<QpdfAttachmentInfo> infos = await ListAsync(host, pdf);
        Assert.Equal("note.bin", Assert.Single(infos).Key);
        Assert.Equal("note.bin", Assert.Single(infos).FileName);
        Assert.Equal(PayloadA.Length, Assert.Single(infos).Size);
    }

    [Fact]
    public async Task AddDefaultsKeyAndFilename()
    {
        byte[] input = BuildPdf(string.Empty, string.Empty, new List<(string Dict, byte[]? StreamData)>());
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        host.AddInput(QpdfPath.From("docs/report.pdf"), PayloadB, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--add-attachment", "docs/report.pdf", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        IReadOnlyList<QpdfAttachmentInfo> infos = await ListAsync(host, pdf);
        Assert.Equal("report.pdf", Assert.Single(infos).Key);
    }

    [Fact]
    public async Task AddExplicitMetadata()
    {
        byte[] input = BuildPdf(string.Empty, string.Empty, new List<(string Dict, byte[]? StreamData)>());
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        host.AddInput(QpdfPath.From("files/blob.bin"), PayloadB, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--add-attachment", "files/blob.bin", "--key=k1", "--filename=display.bin", "--description=Some desc", "--mimetype=text/plain", "--creationdate=D:20240101000000Z", "--moddate=D:20240202000000Z", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        IReadOnlyList<QpdfAttachmentInfo> infos = await ListAsync(host, pdf);
        QpdfAttachmentInfo info = Assert.Single(infos);
        Assert.Equal("k1", info.Key);
        Assert.Equal("display.bin", info.FileName);
        Assert.Equal("Some desc", info.Description);
        Assert.Equal("text/plain", info.MimeType);
        Assert.Equal("D:20240101000000Z", info.CreationDate);
        Assert.Equal("D:20240202000000Z", info.ModDate);
        Assert.Equal(PayloadB.Length, info.Size);
    }

    [Fact]
    public async Task AddWithoutDatesOmitsParams()
    {
        byte[] input = BuildPdf(string.Empty, string.Empty, new List<(string Dict, byte[]? StreamData)>());
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        host.AddInput(QpdfPath.From("files/note.bin"), PayloadA, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--add-attachment", "files/note.bin", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        string text = Encoding.ASCII.GetString(pdf);
        Assert.DoesNotContain("CreationDate", text, StringComparison.Ordinal);
        Assert.Contains("/CheckSum", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddDuplicateKeyFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", SeededFixture());
        host.AddInput(QpdfPath.From("files/other.bin"), PayloadB, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--add-attachment", "files/other.bin", "--key=seed.txt", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task AddReplaceOverwrites()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", SeededFixture());
        host.AddInput(QpdfPath.From("files/other.bin"), PayloadB, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--add-attachment", "files/other.bin", "--key=seed.txt", "--replace", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        QpdfAttachmentExtraction extracted = await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("inputs/a.pdf"), "seed.txt", QpdfPath.From("outputs/x.bin"), CancellationToken.None);
        Assert.Equal(PayloadA.Length, extracted.ByteCount);
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, pdf, QpdfInputOwnership.Copy);
        QpdfAttachmentExtraction replaced = await Qpdf.ExtractAttachmentAsync(host, reread, "seed.txt", QpdfPath.From("outputs/y.bin"), CancellationToken.None);
        Assert.Equal(PayloadB.Length, replaced.ByteCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/y.bin"), out byte[] data));
        Assert.Equal(PayloadB, data);
    }

    [Fact]
    public async Task RemoveExisting()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", SeededFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--remove-attachment=seed.txt", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        IReadOnlyList<QpdfAttachmentInfo> infos = await ListAsync(host, pdf);
        Assert.Empty(infos);
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
    }

    [Fact]
    public async Task RemoveUnknownKeyFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", SeededFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--remove-attachment=missing.txt", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task RemoveThenReaddWins()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", SeededFixture());
        host.AddInput(QpdfPath.From("files/other.bin"), PayloadB, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--remove-attachment=seed.txt", "--add-attachment", "files/other.bin", "--key=seed.txt", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, pdf, QpdfInputOwnership.Copy);
        QpdfAttachmentExtraction extracted = await Qpdf.ExtractAttachmentAsync(host, reread, "seed.txt", QpdfPath.From("outputs/y.bin"), CancellationToken.None);
        Assert.Equal(PayloadB.Length, extracted.ByteCount);
    }

    [Fact]
    public async Task AddPreservesExisting()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", SeededFixture());
        host.AddInput(QpdfPath.From("files/other.bin"), PayloadB, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--add-attachment", "files/other.bin", "--key=other.txt", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        IReadOnlyList<QpdfAttachmentInfo> infos = await ListAsync(host, pdf);
        Assert.Equal(2, infos.Count);
        Assert.Equal("other.txt", infos[0].Key);
        Assert.Equal("seed.txt", infos[1].Key);
    }

    [Fact]
    public async Task AddWithPagesSelectsAndAttaches()
    {
        byte[] input = BuildPdf(string.Empty, string.Empty, new List<(string Dict, byte[]? StreamData)>());
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        host.AddInput(QpdfPath.From("files/note.bin"), PayloadA, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--add-attachment", "files/note.bin", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.Equal(1, await RereadPageCountAsync(host, pdf));
        IReadOnlyList<QpdfAttachmentInfo> infos = await ListAsync(host, pdf);
        Assert.Equal("note.bin", Assert.Single(infos).Key);
    }

    [Fact]
    public async Task AddMissingFileFails()
    {
        byte[] input = BuildPdf(string.Empty, string.Empty, new List<(string Dict, byte[]? StreamData)>());
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--add-attachment", "files/gone.bin", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.HostOperationFailed, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("--add-attachment")]
    [InlineData("--add-attachment", "--", "--", "outputs/a.pdf")]
    [InlineData("--add-attachment", "files/note.bin", "--key=")]
    [InlineData("--add-attachment", "files/note.bin", "--filename=")]
    [InlineData("--remove-attachment")]
    [InlineData("--remove-attachment=")]
    [InlineData("--copy-attachments-from", "inputs/b.pdf")]
    public async Task MalformedEditsFail(params string[] flags)
    {
        byte[] input = BuildPdf(string.Empty, string.Empty, new List<(string Dict, byte[]? StreamData)>());
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        if (!argv.Contains("--"))
        {
            argv.AddRange(["--", "outputs/a.pdf"]);
        }

        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task AddBlockMissingTerminatorFails()
    {
        byte[] input = BuildPdf(string.Empty, string.Empty, new List<(string Dict, byte[]? StreamData)>());
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        host.AddInput(QpdfPath.From("files/note.bin"), PayloadA, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--add-attachment", "files/note.bin"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task NestedUnknownFlagFails()
    {
        byte[] input = BuildPdf(string.Empty, string.Empty, new List<(string Dict, byte[]? StreamData)>());
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        host.AddInput(QpdfPath.From("files/note.bin"), PayloadA, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--add-attachment", "files/note.bin", "--bogus=1", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JobAddRemoveRoundTrip()
    {
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", SeededFixture());
        argvHost.AddInput(QpdfPath.From("files/other.bin"), PayloadB, QpdfInputOwnership.Copy);
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            argvHost,
            new QpdfCommandInvocation(["inputs/a.pdf", "--remove-attachment=seed.txt", "--add-attachment", "files/other.bin", "--key=other.txt", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argv.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", SeededFixture());
        jobHost.AddInput(QpdfPath.From("files/other.bin"), PayloadB, QpdfInputOwnership.Copy);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"removeAttachment\": [\"seed.txt\"], \"addAttachment\": [{\"file\": \"files/other.bin\", \"key\": \"other.txt\"}]}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        jobHost.AddInput(reread, b, QpdfInputOwnership.Copy);
        IReadOnlyList<QpdfAttachmentInfo> infos = await Qpdf.ListAttachmentsAsync(jobHost, reread, CancellationToken.None);
        Assert.Equal("other.txt", Assert.Single(infos).Key);
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"addAttachment\": \"x\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"addAttachment\": [{\"key\": \"k\"}]}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"addAttachment\": [{\"file\": \"f\", \"nope\": 1}]}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"removeAttachment\": \"seed.txt\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"removeAttachment\": [\"\"]}")]
    public async Task JobMalformedEditsFail(string jobJson)
    {
        byte[] input = BuildPdf(string.Empty, string.Empty, new List<(string Dict, byte[]? StreamData)>());
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }
}
