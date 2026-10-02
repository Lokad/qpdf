using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class ShowObjectTests
{
    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    private static async Task<InMemoryQpdfHost> HostWithFixture(string logical, string fixture)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), fixture));
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(logical), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static List<string> ObjectLines(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> lines = new();
        foreach (QpdfDiagnostic diagnostic in result.Diagnostics)
        {
            Assert.Equal(QpdfDiagnosticCode.ObjectListing, diagnostic.Code);
            lines.Add(diagnostic.Message);
        }

        return lines;
    }

    [Fact]
    public async Task ReportsDictionaryDefinition()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=3"]), CancellationToken.None);
        List<string> lines = ObjectLines(result);
        Assert.Equal(
            ["<< /Contents 4 0 R /MediaBox [ 0 0 612 792 ] /Parent 2 0 R /Resources << /Font << /F1 5 0 R >> >> /Type /Page >>"],
            lines);
    }

    [Fact]
    public async Task ReportsStreamDictionary()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=4"]), CancellationToken.None);
        List<string> lines = ObjectLines(result);
        Assert.Equal(["Object is stream.  Dictionary:", "<< /Length 37 >>"], lines);
    }

    [Fact]
    public async Task ReportsTrailer()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=trailer"]), CancellationToken.None);
        List<string> lines = ObjectLines(result);
        Assert.Equal(["<< /Root 1 0 R /Size 6 >>"], lines);
    }

    [Fact]
    public async Task ReportsNullForMissingObject()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=99"]), CancellationToken.None);
        List<string> lines = ObjectLines(result);
        Assert.Equal(["null"], lines);
    }

    [Fact]
    public async Task ReportsCompressedMember()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "objstm-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=1"]), CancellationToken.None);
        List<string> lines = ObjectLines(result);
        Assert.Single(lines);
        Assert.Contains("/Type /Catalog", lines[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("inputs/a.pdf", "--show-object", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-object=", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-object=nope", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-object=0", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-object=1,2,3", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-object=3", "--show-object=4")]
    [InlineData("inputs/a.pdf", "--show-object=3", "--pages", ".", "1", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-object=3", "--show-npages")]
    [InlineData("inputs/a.pdf", "--show-object=3", "--show-pages")]
    [InlineData("inputs/a.pdf", "--show-object=3", "--check")]
    [InlineData("inputs/a.pdf", "--show-object=3", "--", "outputs/a.pdf")]
    public async Task RejectsMalformedAndCombinedForms(params string[] argv)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task RejectsNonZeroGeneration()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=3,1"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
    }

    [Fact]
    public async Task JobShowObjectMatchesArgv()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            argvHost, new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=3"]), CancellationToken.None);
        List<string> argvLines = ObjectLines(argv);
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"showObject\": \"3\"}",
            CancellationToken.None);
        Assert.Equal(argvLines, ObjectLines(job));
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"o.pdf\", \"showObject\": \"3\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"showObject\": \"3\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showObject\": \"\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showObject\": \"nope\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showObject\": \"3\", \"showObject\": \"3\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showObject\": \"3\", \"showNpages\": true}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showObject\": \"3\", \"showPages\": true}")]
    public async Task JobShowObjectRejectsCombinations(string jobJson)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("o.pdf"), out byte[] _));
    }

    [Fact]
    public async Task EncryptedInputFailsWithCryptoCode()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/e.pdf", "encrypted-decl.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/e.pdf", "--show-object=1"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, diagnostic.Code);
    }

    [Fact]
    public async Task FailsOnExhaustedInputBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxInputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--show-object=3"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, diagnostic.Code);
    }
}
