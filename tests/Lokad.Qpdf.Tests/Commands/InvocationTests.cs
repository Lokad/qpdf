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
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(combined).Code);
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
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
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
}
