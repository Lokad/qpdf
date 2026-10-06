using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Content;

public sealed class RemoveStructureTests
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

    private static void AssertStructureGone(byte[] pdf)
    {
        string text = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.DoesNotContain("/StructTreeRoot", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/MarkInfo", text, StringComparison.Ordinal);
        Assert.Contains("/Type /Pages", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlagDropsStructureTree()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "struct-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--remove-structure", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        AssertStructureGone(pdf);
    }

    [Fact]
    public async Task OutputsOmitStructureTreeByDefault()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "struct-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        AssertStructureGone(pdf);
    }

    [Fact]
    public async Task MergeDropsStructureTree()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "struct-1page.pdf");
        host.AddInput(QpdfPath.From("inputs/b.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        List<string> argv = new() { "inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--remove-structure", "--", "outputs/m.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] pdf));
        AssertStructureGone(pdf);
    }

    [Fact]
    public async Task IgnoredWithCheck()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "struct-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--remove-structure"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
    }

    [Theory]
    [InlineData("inputs/a.pdf", "--remove-structure", "--remove-structure")]
    [InlineData("inputs/a.pdf", "--remove-structure=x")]
    public async Task MalformedFlagsFail(params string[] flags)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "struct-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--pages", ".", "1", "--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task JobRemoveStructureMatchesArgv()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "struct-1page.pdf");
        string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "removeStructure": true}""";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        AssertStructureGone(pdf);
    }

    [Theory]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "removeStructure": "yes"}""")]
    [InlineData("""{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.pdf","removeStructure":true,"removeStructure":[]}""")]
    public async Task JobRemoveStructureRejectsMalformed(string jobJson)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "struct-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task FlagYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "struct-1page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--remove-structure", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, diagnostic.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }
}
