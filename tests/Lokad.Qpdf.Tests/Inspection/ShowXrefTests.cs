using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class ShowXrefTests
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

    private static List<string> XrefLines(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> lines = new();
        foreach (QpdfDiagnostic diagnostic in result.Diagnostics)
        {
            Assert.Equal(QpdfDiagnosticCode.XrefListing, diagnostic.Code);
            lines.Add(diagnostic.Message);
        }

        return lines;
    }

    [Fact]
    public async Task ReportsClassicEntries()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-xref"]), CancellationToken.None);
        List<string> lines = XrefLines(result);
        Assert.Equal(
            [
                "1/0: uncompressed; offset = 9",
                "2/0: uncompressed; offset = 58",
                "3/0: uncompressed; offset = 115",
                "4/0: uncompressed; offset = 241",
                "5/0: uncompressed; offset = 328",
            ],
            lines);
    }

    [Fact]
    public async Task ReportsCompressedEntries()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "objstm-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-xref"]), CancellationToken.None);
        List<string> lines = XrefLines(result);
        Assert.Equal(
            [
                "1/0: uncompressed; offset = 9",
                "2/0: uncompressed; offset = 58",
                "3/0: uncompressed; offset = 115",
                "4/0: uncompressed; offset = 241",
                "5/0: uncompressed; offset = 331",
                "6/0: compressed; stream = 5, index = 0",
                "7/0: compressed; stream = 5, index = 1",
                "8/0: uncompressed; offset = 494",
            ],
            lines);
    }

    [Theory]
    [InlineData("inputs/a.pdf", "--show-xref", "--pages", ".", "1", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-xref", "--check", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-xref", "--linearize", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-xref", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-xref=x")]
    public async Task RejectsCombinationsAndValuedForm(params string[] argv)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("--show-xref", "--show-npages")]
    [InlineData("--show-xref", "--show-pages")]
    [InlineData("--show-xref", "--show-object=3")]
    [InlineData("--show-object=3", "--show-xref")]
    public async Task CombinedXrefInspectionKeepsPhysicalEntries(params string[] flags)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["inputs/a.pdf", .. flags]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.XrefListing && d.Message == "3/0: uncompressed; offset = 121");
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task JobShowXrefMatchesArgv()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "objstm-1page.pdf");
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            argvHost, new QpdfCommandInvocation(["inputs/a.pdf", "--show-xref"]), CancellationToken.None);
        List<string> argvLines = XrefLines(argv);
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "objstm-1page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"showXref\": true}",
            CancellationToken.None);
        Assert.Equal(argvLines, XrefLines(job));
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"o.pdf\", \"showXref\": true}", false)]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"showXref\": true}", true)]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showXref\": \"yes\"}", false)]
    [InlineData("{\"inputFile\":\"inputs/a.pdf\",\"showXref\":true,\"showXref\":[]}", false)]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showXref\": true, \"showNpages\": true}", true)]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showXref\": true, \"showPages\": true}", true)]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showXref\": true, \"showObject\": \"3\"}", true)]
    public async Task JobShowXrefValidatesCombinations(string jobJson, bool accepted)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        if (accepted)
        {
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.XrefListing
                && d.Message.StartsWith("3/0: uncompressed; offset = ", StringComparison.Ordinal));
            Assert.Equal(0L, host.OpenLeaseCount);
            return;
        }
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
            host, new QpdfCommandInvocation(["inputs/e.pdf", "--show-xref"]), CancellationToken.None);
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
            new QpdfCommandInvocation(["inputs/a.pdf", "--show-xref"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, diagnostic.Code);
    }
}
