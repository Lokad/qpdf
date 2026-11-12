using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentPreservationTests
{
    private static byte[] Fixture(string profileRoot, string alias, string key, string description, bool nullParameters)
    {
        string catalog = "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 7 0 R >>"
            + (profileRoot == "metadata" ? " /Metadata 4 0 R" : "") + " >>";
        byte[] source = CheckTests.Assemble([
            (1, catalog, null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >>" + (alias == "af" ? " /AF [5 0 R]" : "") + " >>", null),
            (4, "<< /Type /Metadata /Subtype /XML /Audit 8 0 R /Length 4 >>", "<x/>"u8.ToArray()),
            (5, "<< /Type /Filespec /UF (file) /EF << /UF 6 0 R >> " + description + (profileRoot == "filespec" ? " /Audit 8 0 R" : "") + " >>", null),
            (6, "<< /Type /EmbeddedFile /Length 1" + (nullParameters ? " /Params 8 0 R" : "") + " >>", "A"u8.ToArray()),
            (7, "<< /Names [" + key + " 5 0 R] >>", null),
            (8, profileRoot == "none" ? "null" : "<< /Type /Sig >>", null),
            (9, "<< /Audit 8 0 R >>", null)], 1);
        if (profileRoot == "info" || alias == "info")
        {
            // Trailer changes follow the xref table, so object offsets stay valid.
            source = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(source).Replace("/Root 1 0 R", "/Root 1 0 R /Info "
                + (alias == "info" ? "5" : "9") + " 0 R", StringComparison.Ordinal));
        }
        return source;
    }

    private static InMemoryQpdfHost Host(byte[] source)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("added"), "N"u8.ToArray(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("base"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>", null)], 1), QpdfInputOwnership.Transfer);
        return host;
    }

    private static async Task Seed(InMemoryQpdfHost host)
    {
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await transaction.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await transaction.CommitAsync(CancellationToken.None);
    }

    private static async Task AssertPayload(InMemoryQpdfHost host, string expected)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
        await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("again"), "K", QpdfPath.From("payload"), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("payload"), out byte[] payload));
        Assert.Equal(Encoding.ASCII.GetBytes(expected), payload); Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static async Task<QpdfExecutionResult> Run(InMemoryQpdfHost host, string operation, bool job, bool suppress, QpdfLimits limits)
    {
        List<string> argv = [operation == "copy" ? "base" : "input"];
        Dictionary<string, object> config = new() { ["inputFile"] = operation == "copy" ? "base" : "input", ["outputFile"] = "output" };
        if (operation is "replace" or "add")
        {
            string key = operation == "replace" ? "K" : "extra";
            argv.AddRange(["--add-attachment", "added", "--key=" + key]);
            if (operation == "replace") { argv.Add("--replace"); }
            argv.Add("--"); config["addAttachment"] = new[] { new { file = "added", key, replace = operation == "replace" } };
        }
        if (operation == "copy")
        { argv.AddRange(["--copy-attachments-from", "input", "--"]); config["copyAttachmentsFrom"] = new[] { new { file = "input" } }; }
        if (suppress)
        { argv.AddRange(["--no-warn", "--warning-exit-0"]); config["noWarn"] = true; config["warningExit0"] = true; }
        argv.AddRange(["--", "output"]);
        return job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(config), new QpdfExecutionOptions(limits), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), new QpdfExecutionOptions(limits), CancellationToken.None);
    }

    public static IEnumerable<object[]> ProfileCases()
    {
        foreach (string root in new[] { "info", "metadata", "filespec" })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [root, route]; }
    }

    [Theory]
    [MemberData(nameof(ProfileCases))]
    public async Task PrivateSignatureMetadataSurvivesAttachmentEdits(string root, string route)
    {
        using InMemoryQpdfHost host = Host(Fixture(root, "none", "(K)", "", false)); await Seed(host);
        if (route == "typed")
        {
            await Qpdf.AddAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"),
                [new("added", "extra", null, null, null, null, null, false)], CancellationToken.None);
        }
        else
        {
            QpdfExecutionResult result = await Run(host, "add", route == "job", true, QpdfLimits.Default);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.UnsupportedCryptoOperation);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.True(prior.AsSpan().IndexOf("/Sig"u8) >= 0);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(false, "af")]
    [InlineData(true, "af")]
    [InlineData(false, "info")]
    [InlineData(true, "info")]
    public async Task ReplacementDiscardsOnlyTheOldAttachmentEdge(bool job, string alias)
    {
        using InMemoryQpdfHost host = Host(Fixture("filespec", alias, "(K)", "", false)); await Seed(host);
        QpdfExecutionResult result = await Run(host, "replace", job, false, QpdfLimits.Default);
        if (alias == "none")
        { Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); await AssertPayload(host, "N"); }
        else
        {
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.UnsupportedCryptoOperation);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.True(prior.AsSpan().IndexOf("/Sig"u8) >= 0);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SelectiveExtractionDoesNotValidateUnrelatedFilespecMetadata()
    {
        using InMemoryQpdfHost host = Host(Fixture("filespec", "none", "(K)", "", false));
        await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("input"), "K", QpdfPath.From("payload"), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("payload"), out byte[] payload)); Assert.Equal("A"u8.ToArray(), payload);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("rewrite", 5242880L)]
    [InlineData("replace", 3145728L)]
    [InlineData("copy", 5242880L)]
    public async Task UnusedDescriptionFitsTheExistingAdmissionThreshold(string operation, long budget)
    {
        using InMemoryQpdfHost host = Host(Fixture("none", "none", "(K)", "/Desc (" + new string('X', 1048576) + ")", false));
        QpdfExecutionResult result = await Run(host, operation, false, false, QpdfLimits.Default with { MaxMemoryBytes = budget });
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        await AssertPayload(host, operation == "replace" ? "N" : "A");
    }

    [Theory]
    [InlineData("rewrite")]
    [InlineData("copy")]
    public async Task RawNameBytesSurviveWithoutReencoding(string operation)
    {
        using InMemoryQpdfHost host = Host(Fixture("none", "none", "<FEFF004B>", "", false));
        QpdfExecutionResult result = await Run(host, operation, false, false, QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); await AssertPayload(host, "A");
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        QpdfObject Get(PdfDictionary node, string name) { Assert.True(node.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return Resolve(value); }
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Get(document.Engine.Trailer, "Root"));
        PdfDictionary names = Assert.IsType<PdfDictionary>(Get(catalog, "Names"));
        PdfDictionary tree = Assert.IsType<PdfDictionary>(Get(names, "EmbeddedFiles"));
        PdfArray pairs = Assert.IsType<PdfArray>(Get(tree, "Names"));
        Assert.Equal(new byte[] { 0xfe, 0xff, 0x00, 0x4b }, Assert.IsType<PdfByteString>(pairs[0]).Value);
    }

    [Theory]
    [InlineData("rewrite")]
    [InlineData("copy")]
    public async Task PreservationDoesNotRequireListableOptionalMetadata(string operation)
    {
        using InMemoryQpdfHost host = Host(Fixture("none", "none", "(K)", "", true));
        QpdfExecutionResult result = await Run(host, operation, false, false, QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); await AssertPayload(host, "A");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OrdinarySelectionRetainsPrimarySignatureMetadata(bool job, bool provider)
    {
        using InMemoryQpdfHost host = Host(Fixture("filespec", "none", "(K)", "", false)); await Seed(host);
        string file = provider ? "base" : ".";
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", noWarn = true, warningExit0 = true,
                pages = new[] { new { file, range = "1" } } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--pages", file, "1", "--", "--no-warn", "--warning-exit-0", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.UnsupportedCryptoOperation);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.True(prior.AsSpan().IndexOf("/Sig"u8) >= 0);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task TypedOrdinarySelectionRetainsPrimarySignatureMetadata()
    {
        using InMemoryQpdfHost host = Host(Fixture("filespec", "none", "(K)", "", false)); await Seed(host);
        await Qpdf.SelectPageAsync(host, QpdfPath.From("input"), QpdfPath.From("output"),
            new PdfPageSelectionOptions(1), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.True(prior.AsSpan().IndexOf("/Sig"u8) >= 0);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("typed", false)]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("typed", true)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    public async Task SecondaryCollectionIsImportedOnlyWhenPageReachable(string route, bool pageFile)
    {
        using InMemoryQpdfHost host = Host(Fixture("none", "none", "(K)", "", false)); await Seed(host);
        host.AddInput(QpdfPath.From("secondary"), Fixture("filespec", pageFile ? "af" : "none", "(foreign)", "", false), QpdfInputOwnership.Transfer);
        if (route == "typed")
        {
            Task<PdfMultiPageSelectionResult> RunTyped() => Qpdf.SelectPagesAsync(host,
                [new(QpdfPath.From("input"), new PdfPageSelectionOptions(1)), new(QpdfPath.From("secondary"), new PdfPageSelectionOptions(1))],
                QpdfPath.From("output"), CancellationToken.None).AsTask();
            Assert.Empty((await RunTyped()).Warnings);
        }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1"},{"file":"secondary","range":"1"}]}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1", "secondary", "1", "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            if (pageFile) { Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.UnsupportedCryptoOperation); }
        }
        if (pageFile)
        { Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.True(prior.AsSpan().IndexOf("/Sig"u8) >= 0); }
        else
        {
            await AssertPayload(host, "A");
            Assert.Equal("K", Assert.Single(await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("again"), CancellationToken.None)).Key);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
