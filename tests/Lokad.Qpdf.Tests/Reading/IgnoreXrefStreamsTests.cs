using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class IgnoreXrefStreamsTests
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

    private static int PageCount(QpdfExecutionResult result, string input)
    {
        QpdfDiagnostic listing = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageCount);
        Assert.Equal(input + ": 1", listing.Message);
        return 1;
    }

    [Fact]
    public async Task HybridFallsBackToClassicTable()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "hybrid-xstm.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-npages", "--ignore-xref-streams"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.RecoveryEngaged);
        PageCount(result, "inputs/a.pdf");
    }

    [Fact]
    public async Task DamagedStreamLinkReadsClean()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "xstm-classic.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-npages", "--ignore-xref-streams"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        PageCount(result, "inputs/a.pdf");
    }

    [Fact]
    public async Task StreamOnlyFileRecoversWithoutStreams()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "objstm-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-npages", "--ignore-xref-streams"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.RecoveryEngaged);
        PageCount(result, "inputs/a.pdf");
    }

    [Fact]
    public async Task ClassicFileIgnoresFlag()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-npages", "--ignore-xref-streams"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        PageCount(result, "inputs/a.pdf");
    }

    [Theory]
    [InlineData("--ignore-xref-streams=x")]
    [InlineData("--ignore-xref-streams=")]
    [InlineData("--ignore-xref-streams", "--ignore-xref-streams")]
    public async Task MalformedFlagsFail(params string[] flags)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task JobParityHolds()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "hybrid-xstm.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--ignore-xref-streams", "--pages", ".", "1", "--", "outputs/a.pdf" };
        QpdfExecutionResult argvResult = await Qpdf.ExecuteAsync(argvHost, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, argvResult.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "hybrid-xstm.pdf");
        QpdfExecutionResult jobResult = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"ignoreXrefStreams\": true}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, jobResult.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
        InMemoryQpdfHost reopen = new();
        reopen.AddInput(QpdfPath.From("inputs/r.pdf"), a, QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(reopen, QpdfPath.From("inputs/r.pdf"), CancellationToken.None);
        Assert.Equal(1, output.PageCount);
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showNpages\": true, \"ignoreXrefStreams\": \"yes\"}")]
    [InlineData("{\"inputFile\":\"inputs/a.pdf\",\"showNpages\":true,\"ignoreXrefStreams\":true,\"ignoreXrefStreams\":[]}")]
    public async Task JobRejectsMalformed(string jobJson)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "hybrid-xstm.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task HostBudgetHonoredWithoutFlag()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "xstm-classic.pdf");
        QpdfLimits limits = QpdfLimits.Default with { IgnoreXrefStreams = true };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--show-npages"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        PageCount(result, "inputs/a.pdf");
    }
}
