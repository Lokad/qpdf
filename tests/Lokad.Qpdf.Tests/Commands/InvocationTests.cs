using System.Text;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class InvocationTests
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

    private static async Task<QpdfDocument> ReopenOutput(InMemoryQpdfHost host, string output)
    {
        if (!host.TryGetOutput(QpdfPath.From(output), out byte[] published))
        {
            throw new Xunit.Sdk.XunitException($"Output was not published: {output}.");
        }

        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, published, QpdfInputOwnership.Copy);
        return await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
    }

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
    }

    [Fact]
    public async Task ResponseFileExpandsThroughHost()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        host.AddInput(
            QpdfPath.From("args.txt"),
            Encoding.UTF8.GetBytes("inputs/a.pdf\n--pages\n.\n2\n--\noutputs/r.pdf\n"),
            QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["@args.txt"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        {
            await using QpdfDocument output = await ReopenOutput(host, "outputs/r.pdf");
            Assert.Equal(1, output.PageCount);
        }

        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task MissingResponseFileStaysLiteral()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["@no-such-file.txt"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task StdinResponseFileFailsExplicitly()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["@-"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task NestedResponseFileStaysLiteral()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        host.AddInput(
            QpdfPath.From("outer.txt"),
            Encoding.UTF8.GetBytes("@inner.txt"),
            QpdfInputOwnership.Copy);
        host.AddInput(
            QpdfPath.From("inner.txt"),
            Encoding.UTF8.GetBytes("inputs/a.pdf\n--check"),
            QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["@outer.txt"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task OversizedResponseFileFailsOnBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        host.AddInput(QpdfPath.From("big.txt"), new byte[200], QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxInputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["@big.txt"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task JobJsonFileMatchesInlineJob()
    {
        const string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/j.pdf", "pages": [{"file": ".", "range": "2"}]}""";
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        host.AddInput(QpdfPath.From("job.json"), Encoding.UTF8.GetBytes(job), QpdfInputOwnership.Copy);
        QpdfExecutionResult fromFile = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--job-json-file", "job.json"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, fromFile.ExitCode);
        QpdfExecutionResult inline = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, inline.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/j.pdf"), out byte[] _));
    }

    [Fact]
    public async Task JobJsonFileErrorsAreExplicit()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult missing = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--job-json-file", "nope.json"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.HostOperationFailed, SingleError(missing).Code);

        QpdfExecutionResult bare = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--job-json-file"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(bare).Code);

        host.AddInput(QpdfPath.From("job.json"), Encoding.UTF8.GetBytes("{}"), QpdfInputOwnership.Copy);
        QpdfExecutionResult combined = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["--job-json-file", "job.json", "inputs/a.pdf", "--check"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, combined.Status);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task WarningExitZeroMapsWarningsToSuccess()
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf"));
        byte[] marker = Encoding.ASCII.GetBytes("/Count 1");
        int at = FindMarker(bytes, marker);
        Assert.True(at >= 0);
        bytes[at + 7] = (byte)'9';
        InMemoryQpdfHost host = HostWith("inputs/mismatch.pdf", bytes);
        QpdfExecutionResult plain = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/mismatch.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, plain.ExitCode);
        Assert.Contains(plain.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageCountMismatch);

        InMemoryQpdfHost flagged = HostWith("inputs/mismatch.pdf", bytes);
        QpdfExecutionResult zero = await Qpdf.ExecuteAsync(
            flagged,
            new QpdfCommandInvocation(["inputs/mismatch.pdf", "--warning-exit-0", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, zero.ExitCode);
        Assert.Contains(zero.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageCountMismatch);
    }

    [Fact]
    public async Task JobWarningExitZeroMatchesArgv()
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf"));
        byte[] marker = Encoding.ASCII.GetBytes("/Count 1");
        int at = FindMarker(bytes, marker);
        Assert.True(at >= 0);
        bytes[at + 7] = (byte)'9';
        InMemoryQpdfHost host = HostWith("inputs/mismatch.pdf", bytes);
        string job = """{"inputFile": "inputs/mismatch.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "warningExit0": true}""";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.PageCountMismatch);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task JobNoWarnMatchesArgv()
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf"));
        byte[] marker = Encoding.ASCII.GetBytes("/Count 1");
        int at = FindMarker(bytes, marker);
        Assert.True(at >= 0);
        bytes[at + 7] = (byte)'9';
        InMemoryQpdfHost host = HostWith("inputs/mismatch.pdf", bytes);
        string job = """{"inputFile": "inputs/mismatch.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "noWarn": true}""";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == QpdfDiagnosticSeverity.Warning);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "warningExit0": "yes"}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "noWarn": "yes"}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "suppressRecovery": "yes"}""")]
    [InlineData("""{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.pdf","warningExit0":true,"warningExit0":[]}""")]
    public async Task JobWarningFlagsRejectMalformed(string jobJson)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task JobSuppressRecoveryFailsLikeStrict()
    {
        byte[] intact = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf"));
        byte[] marker = Encoding.ASCII.GetBytes("startxref\n");
        int at = FindMarker(intact, marker);
        Assert.True(at >= 0);
        byte[] broken = (byte[])intact.Clone();
        int digits = at + marker.Length;
        int end = digits;
        while (end < broken.Length && broken[end] >= (byte)'0' && broken[end] <= (byte)'9')
        {
            end += 1;
        }

        Assert.True(end > digits);
        for (int i = digits; i < end; i++)
        {
            broken[i] = (byte)'9';
        }
        InMemoryQpdfHost host = HostWith("inputs/broken.pdf", broken);
        string job = """{"inputFile": "inputs/broken.pdf", "outputFile": "outputs/x.pdf", "pages": [{"file": ".", "range": "1"}], "suppressRecovery": true}""";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("--warning-exit-0")]
    [InlineData("--no-warn")]
    public async Task WarningFlagsYieldToExhaustedBudget(string flag)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf"));
        byte[] marker = Encoding.ASCII.GetBytes("/Count 1");
        int at = FindMarker(bytes, marker);
        Assert.True(at >= 0);
        bytes[at + 7] = (byte)'9';
        InMemoryQpdfHost host = HostWith("inputs/mismatch.pdf", bytes);
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/mismatch.pdf", flag, "--pages", ".", "1", "--", "outputs/a.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded,
            Assert.Single(result.Diagnostics, item => item.Severity == QpdfDiagnosticSeverity.Error).Code);
        QpdfDiagnostic[] warnings = result.Diagnostics.Where(item => item.Severity == QpdfDiagnosticSeverity.Warning).ToArray();
        if (flag == "--no-warn") { Assert.Empty(warnings); }
        else { Assert.Equal(QpdfDiagnosticCode.PageCountMismatch, Assert.Single(warnings).Code); }
        Assert.Equal(flag == "--no-warn" ? 1 : 2, result.Diagnostics.Count);
        Assert.Equal(1L, result.Counters.DiagnosticCount);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("inputs/a.pdf", "--warning-exit-0", "--warning-exit-0")]
    [InlineData("inputs/a.pdf", "--no-warn", "--no-warn")]
    [InlineData("inputs/a.pdf", "--suppress-recovery", "--suppress-recovery")]
    public async Task WarningFlagsRejectDuplicates(params string[] flags)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags.Skip(1));
        argv.AddRange(["--pages", ".", "1", "--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task NoWarnSuppressesWarnings()
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf"));
        byte[] marker = Encoding.ASCII.GetBytes("/Count 1");
        int at = FindMarker(bytes, marker);
        Assert.True(at >= 0);
        bytes[at + 7] = (byte)'9';
        InMemoryQpdfHost host = HostWith("inputs/mismatch.pdf", bytes);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/mismatch.pdf", "--no-warn", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == QpdfDiagnosticSeverity.Warning);
        await using QpdfDocument output = await ReopenOutput(host, "outputs/a.pdf");
        Assert.Equal(1, output.PageCount);
    }

    [Fact]
    public async Task HelpAndVersionAreIdentityRequests()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult help = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--help"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, help.Status);
        Assert.Equal(QpdfExitCode.Success, help.ExitCode);
        QpdfDiagnostic helpText = Assert.Single(help.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.HelpText, helpText.Code);
        Assert.Contains("--check", helpText.Message, StringComparison.Ordinal);
        Assert.Contains("--pages", helpText.Message, StringComparison.Ordinal);

        QpdfExecutionResult version = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--version"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, version.ExitCode);
        QpdfDiagnostic versionText = Assert.Single(version.Diagnostics);
        Assert.Contains("12.4.2", versionText.Message, StringComparison.Ordinal);
        Assert.Contains("Lokad.qpdf", versionText.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpDescribesRemovePageLabelsAsPrune()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--help"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        QpdfDiagnostic help = Assert.Single(result.Diagnostics);
        Assert.Contains("--remove-page-labels", help.Message, StringComparison.Ordinal);
        Assert.Contains("drop merged page labels", help.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("accepted no-op", help.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobJsonHelpCryptoSentenceIsWellFormed()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--job-json-help"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        QpdfDiagnostic help = Assert.Single(result.Diagnostics);
        Assert.Contains("are rejected with UnsupportedCryptoOperation", help.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("are rejected with (encrypt", help.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyrightIdentifiesImplementation()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult bare = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--copyright"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, bare.Status);
        Assert.Equal(QpdfExitCode.Success, bare.ExitCode);
        QpdfDiagnostic notice = Assert.Single(bare.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.HelpText, notice.Code);
        Assert.Contains("Lokad.qpdf", notice.Message, StringComparison.Ordinal);
        Assert.Contains("MIT", notice.Message, StringComparison.Ordinal);
        Assert.Contains("12.4.2", notice.Message, StringComparison.Ordinal);

        QpdfExecutionResult withInput = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--copyright"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, withInput.ExitCode);
        Assert.Equal(notice.Message, Assert.Single(withInput.Diagnostics).Message);
    }

    [Fact]
    public async Task JobJsonHelpIsIdentityRequest()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult bare = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--job-json-help"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, bare.Status);
        Assert.Equal(QpdfExitCode.Success, bare.ExitCode);
        QpdfDiagnostic help = Assert.Single(bare.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.HelpText, help.Code);
        Assert.Contains("inputFile", help.Message, StringComparison.Ordinal);
        Assert.Contains("outputFile", help.Message, StringComparison.Ordinal);
        Assert.Contains("pages", help.Message, StringComparison.Ordinal);

        QpdfExecutionResult withInput = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--job-json-help"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, withInput.ExitCode);
        Assert.Equal(help.Message, Assert.Single(withInput.Diagnostics).Message);

        QpdfExecutionResult combined = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--job-json-help", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, combined.Status);
        Assert.Equal(QpdfExitCode.Success, combined.ExitCode);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task CompletionScriptsAreIdentityRequests()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult bash = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--completion-bash"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, bash.Status);
        Assert.Equal(QpdfExitCode.Success, bash.ExitCode);
        QpdfDiagnostic bashScript = Assert.Single(bash.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.HelpText, bashScript.Code);
        Assert.Contains("complete -F _qpdf_complete qpdf", bashScript.Message, StringComparison.Ordinal);
        foreach (string flag in new[] { "--check", "--json", "--qdf", "--linearize" , "--pages"})
        {
            Assert.Contains(flag, bashScript.Message, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("--encrypt", bashScript.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--password", bashScript.Message, StringComparison.Ordinal);

        QpdfExecutionResult zsh = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--completion-zsh"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, zsh.Status);
        Assert.Equal(QpdfExitCode.Success, zsh.ExitCode);
        QpdfDiagnostic zshScript = Assert.Single(zsh.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.HelpText, zshScript.Code);
        Assert.Contains("compdef _qpdf_complete qpdf", zshScript.Message, StringComparison.Ordinal);
        Assert.Contains("--check", zshScript.Message, StringComparison.Ordinal);

        QpdfExecutionResult withInput = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--completion-bash"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, withInput.ExitCode);
        Assert.Equal(bashScript.Message, Assert.Single(withInput.Diagnostics).Message);

        QpdfExecutionResult combined = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--completion-zsh", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, combined.Status);
        Assert.Equal(QpdfExitCode.Success, combined.ExitCode);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("--help=x")]
    [InlineData("--version=x")]
    [InlineData("--copyright=x")]
    [InlineData("--job-json-help=x")]
    [InlineData("--completion-bash=x")]
    [InlineData("--completion-zsh=x")]
    public async Task ValuedIdentityFormsFail(string flag)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", flag, "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("--copyright")]
    [InlineData("--job-json-help")]
    [InlineData("--json-help")]
    [InlineData("--completion-bash")]
    [InlineData("--completion-zsh")]
    public async Task IdentityRequestsIgnoreExhaustedOutputBudget(string flag)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 1L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation([flag]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
    }

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static int FindMarker(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public async Task JobFileYieldsToExhaustedBudget()
    {
        const string job = """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/j.pdf", "pages": [{"file": ".", "range": "2"}]}""";
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        host.AddInput(QpdfPath.From("job.json"), Encoding.UTF8.GetBytes(job), QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["--job-json-file", "job.json"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/j.pdf"), out byte[] _));
    }
}
