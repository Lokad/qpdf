using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class OperationFlagsTests
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

    private static async Task<byte[]> MergeAsync(InMemoryQpdfHost host, params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "inputs/b.pdf", "1" };
        argv.InsertRange(1, flags);
        argv.AddRange(["--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        return pdf;
    }

    [Theory]
    [InlineData("--keep-files-open=y")]
    [InlineData("--keep-files-open=n")]
    [InlineData("--keep-files-open=y", "--keep-files-open-threshold=200")]
    [InlineData("--keep-files-open=n", "--keep-files-open-threshold", "0")]
    public async Task KeepOpenHintsDoNotChangeOutput(params string[] flags)
    {
        InMemoryQpdfHost plain = await HostWithTwoSources();
        byte[] baseline = await MergeAsync(plain);
        InMemoryQpdfHost hinted = await HostWithTwoSources();
        byte[] hintedBytes = await MergeAsync(hinted, flags);
        Assert.Equal(baseline, hintedBytes);
    }

    [Theory]
    [InlineData("--keep-files-open")]
    [InlineData("--keep-files-open=")]
    [InlineData("--keep-files-open=maybe")]
    [InlineData("--keep-files-open=y", "--keep-files-open=n")]
    [InlineData("--keep-files-open-threshold")]
    [InlineData("--keep-files-open-threshold=")]
    [InlineData("--keep-files-open-threshold=x")]
    [InlineData("--keep-files-open-threshold=-1")]
    [InlineData("--keep-files-open-threshold=5", "--keep-files-open-threshold=6")]
    public async Task BadKeepOpenFlagsFail(params string[] flags)
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        List<string> argv = new() { "inputs/a.pdf", "--pages", "inputs/a.pdf", "1" };
        argv.InsertRange(1, flags);
        argv.AddRange(["--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task JobKeepOpenMatchesArgv()
    {
        InMemoryQpdfHost argvHost = await HostWithTwoSources();
        byte[] a = await MergeAsync(argvHost, "--keep-files-open=y", "--keep-files-open-threshold=200");
        InMemoryQpdfHost jobHost = await HostWithTwoSources();
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \"inputs/a.pdf\", \"range\": \"1\"}, {\"file\": \"inputs/b.pdf\", \"range\": \"1\"}], \"keepFilesOpen\": \"y\", \"keepFilesOpenThreshold\": 200}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \"inputs/a.pdf\", \"range\": \"1\"}], \"keepFilesOpen\": \"maybe\"}")]
    [InlineData("{\"inputFile\":\"inputs/a.pdf\",\"outputFile\":\"outputs/a.pdf\",\"pages\":[{\"file\":\"inputs/a.pdf\",\"range\":\"1\"}],\"keepFilesOpen\":\"y\",\"keepFilesOpen\":[]}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \"inputs/a.pdf\", \"range\": \"1\"}], \"keepFilesOpenThreshold\": -1}")]
    public async Task BadKeepOpenJobKeysFail(string jobJson)
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task VerboseSummarizesCheck()
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--verbose"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        QpdfDiagnostic summary = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.VerboseSummary);
        Assert.Equal(QpdfDiagnosticSeverity.Info, summary.Severity);
        Assert.Contains("Verbose summary:", summary.Message, StringComparison.Ordinal);
        Assert.Contains("output bytes", summary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerboseSummarizesMerge()
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        List<string> argv = new() { "inputs/a.pdf", "--verbose", "--pages", "inputs/a.pdf", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.VerboseSummary);
    }

    [Fact]
    public async Task VerboseSummarizesShowNpages()
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-npages", "--verbose"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.VerboseSummary);
    }

    [Theory]
    [InlineData("--verbose=x")]
    [InlineData("--verbose=")]
    [InlineData("--verbose", "--verbose=x")]
    public async Task BadVerboseFlagsFail(params string[] flags)
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        List<string> argv = new() { "inputs/a.pdf", "--check" };
        argv.AddRange(flags);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JobVerboseMatchesArgv()
    {
        InMemoryQpdfHost argvHost = await HostWithTwoSources();
        List<string> argv = new() { "inputs/a.pdf", "--verbose", "--pages", "inputs/a.pdf", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult argvResult = await Qpdf.ExecuteAsync(argvHost, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argvResult.ExitCode);
        string argvMessage = Assert.Single(argvResult.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.VerboseSummary).Message;
        InMemoryQpdfHost jobHost = await HostWithTwoSources();
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \"inputs/a.pdf\", \"range\": \"1\"}], \"verbose\": true}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        string jobMessage = Assert.Single(job.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.VerboseSummary).Message;
        Assert.Contains($"peak {job.Counters.PeakMemoryBytes} bytes.", jobMessage, StringComparison.Ordinal);
        Assert.True(job.Counters.PeakMemoryBytes > argvResult.Counters.PeakMemoryBytes);
        Assert.Equal(
            System.Text.RegularExpressions.Regex.Replace(argvMessage, @"peak \d+ bytes", "peak bytes"),
            System.Text.RegularExpressions.Regex.Replace(jobMessage, @"peak \d+ bytes", "peak bytes"));
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showNpages\": true, \"verbose\": \"yes\"}")]
    [InlineData("{\"inputFile\":\"inputs/a.pdf\",\"showNpages\":true,\"verbose\":true,\"verbose\":[]}")]
    public async Task BadVerboseJobKeysFail(string jobJson)
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task VerboseYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--verbose", "--pages", "inputs/a.pdf", "1", "--", "outputs/a.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }
}
