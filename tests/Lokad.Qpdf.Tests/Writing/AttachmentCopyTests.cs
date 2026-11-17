using System.Text;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentCopyTests
{
    private static readonly byte[] PageData = Encoding.ASCII.GetBytes("BT (Page) Tj ET");

    private static readonly byte[] PayloadA = Encoding.ASCII.GetBytes("Copy source alpha.");

    private static readonly byte[] PayloadB = Encoding.ASCII.GetBytes("Copy source beta, slightly longer.");

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

    private static byte[] BuildPdf(string catalogExtra, List<(string Dict, byte[]? StreamData)> extras)
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
        return PdfTestDocument.AssembleBodies(bodies, number => number == 4 ? PageData : number > 5 ? extras[number - 6].StreamData : null,
            "1.4", "");
    }

    internal static byte[] SourceFixture()
    {
        return BuildPdf(
            " /Names 6 0 R",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /EmbeddedFiles 7 0 R >>", null),
                ("<< /Names [(alpha.txt) 8 0 R (beta.txt) 10 0 R] >>", null),
                ("<< /Type /Filespec /F (alpha.txt) /UF (alpha.txt) /EF << /F 9 0 R /UF 9 0 R >> >>", null),
                ("<< /Type /EmbeddedFile /Length " + PayloadA.Length.ToString() + " /Params << /Size " + PayloadA.Length.ToString() + " >> >>", PayloadA),
                ("<< /Type /Filespec /F (beta.txt) /EF << /F 11 0 R >> >>", null),
                ("<< /Type /EmbeddedFile /Length " + PayloadB.Length.ToString() + " >>", PayloadB),
            });
    }

    private static byte[] PlainFixture()
    {
        return BuildPdf(string.Empty, new List<(string Dict, byte[]? StreamData)>());
    }

    private static async Task<IReadOnlyList<QpdfAttachmentInfo>> ListAsync(InMemoryQpdfHost host, string path)
    {
        return await Qpdf.ListAttachmentsAsync(host, QpdfPath.From(path), CancellationToken.None);
    }

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
    }

    [Fact]
    public async Task CopyBasic()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/s.pdf"), SourceFixture(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--copy-attachments-from", "inputs/s.pdf", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, pdf, QpdfInputOwnership.Copy);
        IReadOnlyList<QpdfAttachmentInfo> infos = await ListAsync(host, "inputs/reread.pdf");
        Assert.Equal(2, infos.Count);
        Assert.Equal("alpha.txt", infos[0].Key);
        Assert.Equal("beta.txt", infos[1].Key);
        QpdfAttachmentExtraction alpha = await Qpdf.ExtractAttachmentAsync(host, reread, "alpha.txt", QpdfPath.From("outputs/alpha.bin"), CancellationToken.None);
        Assert.Equal(PayloadA.Length, alpha.ByteCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/alpha.bin"), out byte[] a));
        Assert.Equal(PayloadA, a);
        QpdfAttachmentExtraction beta = await Qpdf.ExtractAttachmentAsync(host, reread, "beta.txt", QpdfPath.From("outputs/beta.bin"), CancellationToken.None);
        Assert.Equal(PayloadB.Length, beta.ByteCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/beta.bin"), out byte[] b));
        Assert.Equal(PayloadB, b);
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("/PageMode /UseAttachments", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyPrefix()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/s.pdf"), SourceFixture(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--copy-attachments-from", "inputs/s.pdf", "--prefix=cp-", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, pdf, QpdfInputOwnership.Copy);
        IReadOnlyList<QpdfAttachmentInfo> infos = await ListAsync(host, "inputs/reread.pdf");
        Assert.Equal(2, infos.Count);
        Assert.Equal("cp-alpha.txt", infos[0].Key);
        Assert.Equal("cp-beta.txt", infos[1].Key);
        Assert.Equal("alpha.txt", infos[0].FileName);
    }

    [Fact]
    public async Task CopySpacePrefixForm()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/s.pdf"), SourceFixture(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--copy-attachments-from", "inputs/s.pdf", "--prefix", "q-", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, pdf, QpdfInputOwnership.Copy);
        IReadOnlyList<QpdfAttachmentInfo> infos = await ListAsync(host, "inputs/reread.pdf");
        Assert.Equal("q-alpha.txt", infos[0].Key);
    }

    [Fact]
    public async Task CopyCollisionFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", SourceFixture());
        host.AddInput(QpdfPath.From("inputs/s.pdf"), SourceFixture(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--copy-attachments-from", "inputs/s.pdf", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task CopyUnknownSourceFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--copy-attachments-from", "inputs/gone.pdf", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.HostOperationFailed, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task CopyEncryptedSourceFails()
    {
        byte[] encrypted = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "encrypted-decl.pdf"));
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/s.pdf"), encrypted, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--copy-attachments-from", "inputs/s.pdf", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task CopyPasswordIsIgnoredForPlaintextSources()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/s.pdf"), SourceFixture(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--copy-attachments-from", "inputs/s.pdf", "--password=x", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] bytes));
        host.AddInput(QpdfPath.From("copy.pdf"), bytes, QpdfInputOwnership.Copy);
        Assert.Equal(2, (await ListAsync(host, "copy.pdf")).Count);
    }

    [Fact]
    public async Task CopyAddCollisionFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/s.pdf"), SourceFixture(), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("files/n.bin"), PayloadA, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--add-attachment", "files/n.bin", "--key=alpha.txt", "--", "--copy-attachments-from", "inputs/s.pdf", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("--copy-attachments-from")]
    [InlineData("--copy-attachments-from=inputs/s.pdf")]
    [InlineData("--copy-attachments-from", "inputs/s.pdf", "--prefix")]
    public async Task CopyMalformedFails(params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--", "--", "outputs/a.pdf"]);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/s.pdf"), SourceFixture(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task CopyStrayPrefixFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--prefix=x", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JobCopyRoundTrip()
    {
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", PlainFixture());
        argvHost.AddInput(QpdfPath.From("inputs/s.pdf"), SourceFixture(), QpdfInputOwnership.Copy);
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            argvHost,
            new QpdfCommandInvocation(["inputs/a.pdf", "--copy-attachments-from", "inputs/s.pdf", "--prefix=p-", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argv.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", PlainFixture());
        jobHost.AddInput(QpdfPath.From("inputs/s.pdf"), SourceFixture(), QpdfInputOwnership.Copy);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"copyAttachmentsFrom\": [{\"file\": \"inputs/s.pdf\", \"prefix\": \"p-\"}]}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        jobHost.AddInput(reread, b, QpdfInputOwnership.Copy);
        IReadOnlyList<QpdfAttachmentInfo> infos = await ListAsync(jobHost, "inputs/reread.pdf");
        Assert.Equal(2, infos.Count);
        Assert.Equal("p-alpha.txt", infos[0].Key);
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"copyAttachmentsFrom\": \"x\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"copyAttachmentsFrom\": [{\"prefix\": \"p-\"}]}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"copyAttachmentsFrom\": [{\"file\": \"inputs/s.pdf\", \"bogus\": 1}]}")]
    public async Task JobMalformedCopiesFail(string jobJson)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/s.pdf"), SourceFixture(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JobCopyPasswordIsIgnoredForPlaintextSources()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/s.pdf"), SourceFixture(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"copyAttachmentsFrom\": [{\"file\": \"inputs/s.pdf\", \"password\": \"x\"}]}",
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] bytes));
        host.AddInput(QpdfPath.From("copy.pdf"), bytes, QpdfInputOwnership.Copy);
        Assert.Equal(2, (await ListAsync(host, "copy.pdf")).Count);
    }

    [Fact]
    public async Task TypedAddRoundTrip()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("files/n.bin"), PayloadA, QpdfInputOwnership.Copy);
        QpdfAttachmentEditResult result = await Qpdf.AddAttachmentsAsync(
            host,
            QpdfPath.From("inputs/a.pdf"),
            QpdfPath.From("outputs/a.pdf"),
            [new QpdfAttachmentSpec("files/n.bin", "k1", "display.bin", "A desc", "text/plain", "D:20240101000000Z", "D:20240202000000Z", false)],
            CancellationToken.None);
        Assert.Equal(1, result.AttachmentCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, pdf, QpdfInputOwnership.Copy);
        IReadOnlyList<QpdfAttachmentInfo> infos = await ListAsync(host, "inputs/reread.pdf");
        QpdfAttachmentInfo info = Assert.Single(infos);
        Assert.Equal("k1", info.Key);
        Assert.Equal("display.bin", info.FileName);
        Assert.Equal("A desc", info.Description);
        Assert.Equal("text/plain", info.MimeType);
    }

    [Fact]
    public async Task TypedAddValidation()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Qpdf.AddAttachmentsAsync(host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.pdf"), null!, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => Qpdf.AddAttachmentsAsync(host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.pdf"), [], CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => Qpdf.AddAttachmentsAsync(host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.pdf"), [new QpdfAttachmentSpec(string.Empty, null, null, null, null, null, null, false)], CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task TypedRemoveRoundTrip()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", SourceFixture());
        QpdfAttachmentEditResult result = await Qpdf.RemoveAttachmentsAsync(
            host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.pdf"), ["beta.txt"], CancellationToken.None);
        Assert.Equal(1, result.AttachmentCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, pdf, QpdfInputOwnership.Copy);
        IReadOnlyList<QpdfAttachmentInfo> infos = await ListAsync(host, "inputs/reread.pdf");
        Assert.Equal("alpha.txt", Assert.Single(infos).Key);
        await Assert.ThrowsAsync<QpdfParseException>(
            () => Qpdf.RemoveAttachmentsAsync(host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/b.pdf"), ["missing.txt"], CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<QpdfParseException>(
            () => Qpdf.RemoveAttachmentsAsync(host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/b.pdf"), [string.Empty], CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task TypedCopyRoundTrip()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/s.pdf"), SourceFixture(), QpdfInputOwnership.Copy);
        QpdfAttachmentEditResult result = await Qpdf.CopyAttachmentsAsync(
            host,
            QpdfPath.From("inputs/a.pdf"),
            QpdfPath.From("outputs/a.pdf"),
            [new QpdfAttachmentCopy(QpdfPath.From("inputs/s.pdf"), "c-")],
            CancellationToken.None);
        Assert.Equal(2, result.AttachmentCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, pdf, QpdfInputOwnership.Copy);
        IReadOnlyList<QpdfAttachmentInfo> infos = await ListAsync(host, "inputs/reread.pdf");
        Assert.Equal("c-alpha.txt", infos[0].Key);
        QpdfAttachmentExtraction extracted = await Qpdf.ExtractAttachmentAsync(host, reread, "c-beta.txt", QpdfPath.From("outputs/b.bin"), CancellationToken.None);
        Assert.Equal(PayloadB.Length, extracted.ByteCount);
    }

    [Fact]
    public async Task TypedCopyCollisionThrows()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", SourceFixture());
        host.AddInput(QpdfPath.From("inputs/s.pdf"), SourceFixture(), QpdfInputOwnership.Copy);
        await Assert.ThrowsAsync<QpdfParseException>(
            () => Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.pdf"), [new QpdfAttachmentCopy(QpdfPath.From("inputs/s.pdf"), null)], CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task TypedValidation()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.pdf"), null!, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.pdf"), [], CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task CopyYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/s.pdf"), SourceFixture(), QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--copy-attachments-from", "inputs/s.pdf", "--", "--", "outputs/a.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task CopyEmptyPrefixKeepsOriginalKeys()
    {
        using InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/s.pdf"), SourceFixture(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--copy-attachments-from", "inputs/s.pdf", "--prefix=", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] output));
        host.AddInput(QpdfPath.From("reread"), output, QpdfInputOwnership.Transfer);
        IReadOnlyList<QpdfAttachmentInfo> infos = await ListAsync(host, "reread");
        Assert.Equal(new[] { "alpha.txt", "beta.txt" }, infos.Select(info => info.Key));
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
