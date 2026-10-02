using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class EmptyInputTests
{
    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    private static async Task<InMemoryQpdfHost> HostWithTwoSources()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-2page.pdf")), QpdfInputOwnership.Copy);
        return host;
    }

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
    }

    private static async Task<int> ReopenPageCount(InMemoryQpdfHost host, string output)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From(output), out byte[] pdf));
        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, pdf, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
        return document.PageCount;
    }

    [Fact]
    public async Task MergesFromEmptyBase()
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["--empty", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1", "inputs/b.pdf", "2", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(3, await ReopenPageCount(host, "outputs/a.pdf"));
    }

    [Fact]
    public async Task SingleGroupMergesFromEmptyBase()
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["--empty", "--pages", "inputs/b.pdf", "2", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(1, await ReopenPageCount(host, "outputs/a.pdf"));
    }

    [Fact]
    public async Task JobEmptyMatchesArgv()
    {
        InMemoryQpdfHost argvHost = await HostWithTwoSources();
        QpdfExecutionResult argvResult = await Qpdf.ExecuteAsync(
            argvHost,
            new QpdfCommandInvocation(["--empty", "--pages", "inputs/a.pdf", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argvResult.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));
        InMemoryQpdfHost jobHost = await HostWithTwoSources();
        QpdfExecutionResult jobResult = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"empty\": true, \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \"inputs/a.pdf\", \"range\": \"1\"}]}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, jobResult.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
        Assert.Equal(1, await ReopenPageCount(jobHost, "outputs/a.pdf"));
    }

    [Theory]
    [InlineData("--empty", "--pages", ".", "1", "--", "outputs/a.pdf")]
    [InlineData("--empty", "--empty", "--pages", "inputs/a.pdf", "1", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--empty", "--pages", "inputs/a.pdf", "1", "--", "outputs/a.pdf")]
    [InlineData("--empty", "--pages", "inputs/a.pdf", "1", "--", "outputs/a.pdf", "--replace-input")]
    public async Task MalformedEmptySelectionsFail(params string[] argv)
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task BareEmptyHasNoOperation()
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--empty", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task EmptyWithReplaceInputFails()
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--empty", "--pages", "inputs/a.pdf", "1", "--replace-input"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task EmptyWithOverlayFails()
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["--empty", "--pages", "inputs/a.pdf", "1", "--overlay", "inputs/b.pdf", "--", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("{\"empty\": true, \"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \"inputs/a.pdf\", \"range\": \"1\"}]}")]
    [InlineData("{\"empty\": true, \"outputFile\": \"outputs/a.pdf\"}")]
    [InlineData("{\"empty\": true, \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}]}")]
    [InlineData("{\"empty\": true, \"showNpages\": true}")]
    [InlineData("{\"empty\": \"yes\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \"inputs/a.pdf\", \"range\": \"1\"}]}")]
    public async Task BadEmptyJobsFail(string jobJson)
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task EmptyMergeYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["--empty", "--pages", "inputs/a.pdf", "1", "--", "outputs/a.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, diagnostic.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }
}
