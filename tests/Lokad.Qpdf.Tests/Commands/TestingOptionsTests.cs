using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class TestingOptionsTests
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

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
    }

    private static QpdfDiagnostic SingleNote(QpdfExecutionResult result, QpdfDiagnosticCode code)
    {
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        return Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public async Task ReportsPeakOnCheck()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--report-memory-usage"]), CancellationToken.None);
        QpdfDiagnostic note = SingleNote(result, QpdfDiagnosticCode.MemoryUsage);
        Assert.Equal(QpdfDiagnosticSeverity.Info, note.Severity);
        Assert.Contains("Peak memory usage:", note.Message, StringComparison.Ordinal);
        Assert.Contains("bytes (estimated).", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsPeakOnMerge()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--report-memory-usage", "--pages", ".", "1", ".", "2", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.MemoryUsage);
    }

    [Fact]
    public async Task ReportsPeakOnShowNpages()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-npages", "--report-memory-usage"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.MemoryUsage);
    }

    [Theory]
    [InlineData("--report-memory-usage=x")]
    [InlineData("--report-memory-usage=")]
    [InlineData("--report-memory-usage", "--report-memory-usage=x")]
    [InlineData("--test-json-schema=x")]
    [InlineData("--test-json-schema=")]
    [InlineData("--test-json-schema", "--test-json-schema")]
    public async Task MalformedTestingFlagsFail(params string[] flags)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--check" };
        argv.AddRange(flags);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JobMemoryKeyMatchesArgv()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            argvHost, new QpdfCommandInvocation(["inputs/a.pdf", "--show-npages", "--report-memory-usage"]), CancellationToken.None);
        string argvMessage = SingleNote(argv, QpdfDiagnosticCode.MemoryUsage).Message;
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"showNpages\": true, \"reportMemoryUsage\": true}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        string jobMessage = Assert.Single(job.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.MemoryUsage).Message;
        Assert.Contains($"{argv.Counters.PeakMemoryBytes} bytes (estimated).", argvMessage, StringComparison.Ordinal);
        Assert.Contains($"{job.Counters.PeakMemoryBytes} bytes (estimated).", jobMessage, StringComparison.Ordinal);
        Assert.Equal(QpdfExecutionStatus.Completed, argv.Status);
        Assert.Equal(QpdfExecutionStatus.Completed, job.Status);
        Assert.Equal(0L, argvHost.OpenLeaseCount);
        Assert.Equal(0L, jobHost.OpenLeaseCount);
    }

    [Fact]
    public async Task MemoryReportYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--report-memory-usage", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
    }

    [Theory]
    [InlineData("--json", "--json-key=pages")]
    [InlineData("--json=1", "--json-key=objects")]
    [InlineData("--json-output", "--json-key=qpdf")]
    public async Task SchemaCheckValidatesVersions(params string[] flags)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--test-json-schema", "--", "outputs/a.json"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        QpdfDiagnostic check = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.JsonSchemaCheck);
        Assert.Equal(QpdfDiagnosticSeverity.Info, check.Severity);
        Assert.Contains("passed", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchemaCheckWithoutJsonFails()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--test-json-schema"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JobSchemaKeyMatchesArgv()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            argvHost,
            new QpdfCommandInvocation(["inputs/a.pdf", "--json", "--json-key=pages", "--test-json-schema", "--", "outputs/a.json"]),
            CancellationToken.None);
        string argvMessage = Assert.Single(argv.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.JsonSchemaCheck).Message;
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"json\": \"latest\", \"jsonKey\": [\"pages\"], \"testJsonSchema\": true}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        string jobMessage = Assert.Single(job.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.JsonSchemaCheck).Message;
        Assert.Equal(argvMessage, jobMessage);
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showNpages\": true, \"testJsonSchema\": true}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"jsonInput\": true, \"testJsonSchema\": true}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"json\": \"latest\", \"testJsonSchema\": \"yes\"}")]
    [InlineData("{\"inputFile\":\"inputs/a.pdf\",\"outputFile\":\"outputs/a.json\",\"json\":\"latest\",\"testJsonSchema\":true,\"testJsonSchema\":[]}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showNpages\": true, \"reportMemoryUsage\": \"yes\"}")]
    public async Task BadTestingJobKeysFail(string jobJson)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] _));
    }

    [Fact]
    public async Task SchemaCheckYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--json", "--json-key=pages", "--test-json-schema", "--", "outputs/a.json"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] _));
    }
}
