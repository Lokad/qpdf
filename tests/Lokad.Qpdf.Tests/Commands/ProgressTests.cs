using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class ProgressTests
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

    [Fact]
    public async Task ProgressReportsWriteMilestonesWithIdenticalBytes()
    {
        InMemoryQpdfHost plain = await HostWithTwoSources();
        QpdfExecutionResult baseline = await Qpdf.ExecuteAsync(
            plain,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, baseline.ExitCode);
        Assert.True(plain.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] plainBytes));
        Assert.DoesNotContain(baseline.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.ProgressReport);
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "--progress", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] progressBytes));
        Assert.Equal(plainBytes, progressBytes);
        QpdfDiagnostic[] notes = result.Diagnostics
            .Where(diagnostic => diagnostic.Code == QpdfDiagnosticCode.ProgressReport)
            .ToArray();
        Assert.Equal(4, notes.Length);
        Assert.All(notes, note => Assert.Equal(QpdfDiagnosticSeverity.Info, note.Severity));
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal($"Write progress: {(i + 1) * 25}% of {progressBytes.Length} bytes.", notes[i].Message);
        }
    }

    [Fact]
    public async Task ProgressStaysSilentWithoutAnOutputFile()
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--show-npages", "--progress"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.ProgressReport);
    }

    [Theory]
    [InlineData("--progress=x")]
    [InlineData("--progress=")]
    [InlineData("--progress", "--progress")]
    public async Task BadProgressFlagsFail(params string[] flags)
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        List<string> argv = new() { "inputs/a.pdf", "--check" };
        argv.AddRange(flags);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JobProgressMatchesArgv()
    {
        InMemoryQpdfHost argvHost = await HostWithTwoSources();
        List<string> argv = new() { "inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "--progress", "--", "outputs/a.pdf" };
        QpdfExecutionResult argvResult = await Qpdf.ExecuteAsync(argvHost, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argvResult.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] argvBytes));
        InMemoryQpdfHost jobHost = await HostWithTwoSources();
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \"inputs/a.pdf\", \"range\": \"1\"}], \"progress\": true}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] jobBytes));
        Assert.Equal(argvBytes, jobBytes);
        Assert.Equal(
            argvResult.Diagnostics.Count(diagnostic => diagnostic.Code == QpdfDiagnosticCode.ProgressReport),
            job.Diagnostics.Count(diagnostic => diagnostic.Code == QpdfDiagnosticCode.ProgressReport));
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showNpages\": true, \"progress\": \"yes\"}")]
    [InlineData("{\"inputFile\":\"inputs/a.pdf\",\"showNpages\":true,\"progress\":true,\"progress\":[]}")]
    public async Task BadProgressJobKeysFail(string jobJson)
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task ProgressCombinesWithVerboseWithoutChangingBytes()
    {
        InMemoryQpdfHost plain = await HostWithTwoSources();
        QpdfExecutionResult baseline = await Qpdf.ExecuteAsync(
            plain,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.True(plain.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] plainBytes));
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "--verbose", "--progress", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] progressBytes));
        Assert.Equal(plainBytes, progressBytes);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.VerboseSummary);
        Assert.Equal(4, result.Diagnostics.Count(diagnostic => diagnostic.Code == QpdfDiagnosticCode.ProgressReport));
    }

    [Fact]
    public async Task ProgressYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithTwoSources();
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "--progress", "--", "outputs/a.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.ProgressReport);
    }
}
