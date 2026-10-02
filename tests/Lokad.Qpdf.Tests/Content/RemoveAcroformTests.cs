using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Content;

public sealed class RemoveAcroformTests
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

    private static void AssertFormDictionaryGone(byte[] pdf)
    {
        string text = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.DoesNotContain("/AcroForm", text, StringComparison.Ordinal);
        Assert.Contains("/Annots", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlagDropsFormDictionary()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--remove-acroform", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        AssertFormDictionaryGone(pdf);
    }

    [Fact]
    public async Task OutputsPreserveFormDictionaryByDefault()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        string text = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.Contains("/AcroForm", text, StringComparison.Ordinal);
        Assert.Contains("/Annots", text, StringComparison.Ordinal);
        Assert.Contains("(Hello)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeDropsFormDictionary()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        host.AddInput(QpdfPath.From("inputs/b.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        List<string> argv = new() { "inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "--remove-acroform", "--", "outputs/m.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] pdf));
        AssertFormDictionaryGone(pdf);
    }

    [Fact]
    public async Task IgnoredWithCheck()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--remove-acroform"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
    }

    [Theory]
    [InlineData("inputs/a.pdf", "--remove-acroform", "--remove-acroform")]
    [InlineData("inputs/a.pdf", "--remove-acroform=x")]
    public async Task MalformedFlagsFail(params string[] flags)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
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
    public async Task JobRemoveAcroformMatchesArgv()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "removeAcroform": true}""";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        AssertFormDictionaryGone(pdf);
    }

    [Theory]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "removeAcroform": "yes"}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "removeAcroform": true, "removeAcroform": true}""")]
    public async Task JobRemoveAcroformRejectsMalformed(string jobJson)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task FlagYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--remove-acroform", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, diagnostic.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }
}
