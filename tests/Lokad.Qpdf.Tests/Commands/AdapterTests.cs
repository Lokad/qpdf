using System.Text;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class AdapterTests
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
    public async Task ArgvMissingSecondFileFails()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "2", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.HostOperationFailed, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task ArgvPositionalSelectionSucceeds()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfCommandInvocation invocation = new(["inputs/a.pdf", "--pages", ".", "2", "--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, invocation, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await ReopenOutput(host, "outputs/a.pdf");
        Assert.Equal(1, output.PageCount);
    }

    [Fact]
    public async Task ArgvNamedFormsSucceed()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfCommandInvocation invocation = new(
            ["inputs/a.pdf", "--pages", "--file=inputs/a.pdf", "--range=1", "--", "outputs/b.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, invocation, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);

        InMemoryQpdfHost second = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfCommandInvocation separate = new(
            ["inputs/a.pdf", "--pages", "--file", "inputs/a.pdf", "--range", "1", "--", "outputs/b.pdf"]);
        QpdfExecutionResult other = await Qpdf.ExecuteAsync(second, separate, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, other.ExitCode);
    }

    [Fact]
    public async Task ArgvAndJobParityHolds()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argv.ExitCode);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            host,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/j.pdf", "pages": [{"file": ".", "range": "1"}]}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/j.pdf"), out byte[] j));
        Assert.Equal(a, j);
    }

    [Theory]
    [InlineData("inputs/a.pdf", "--pages")]
    [InlineData("inputs/a.pdf", "--pages", ".", "1", "outputs/a.pdf")]
    [InlineData("--pages", ".", "1", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "inputs/b.pdf", "--pages", ".", "1", "--", "outputs/a.pdf")]
    public async Task ArgvStructuralErrorsFail(params string[] argv)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        QpdfDiagnostic diagnostic = SingleError(result);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
    }

    [Theory]
    [InlineData("1-3")]
    [InlineData("0")]
    [InlineData("even")]
    [InlineData("99999999999")]
    public async Task ArgvRangeErrorsFail(string range)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", range, "--", "outputs/a.pdf"]),
            CancellationToken.None);
        QpdfDiagnostic diagnostic = SingleError(result);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
    }

    [Theory]
    [InlineData("--password=s3cret-value")]
    [InlineData("--password-file=pw.txt")]
    [InlineData("--decrypt")]
    [InlineData("--encrypt", "u", "o", "128", "--")]
    [InlineData("--remove-restrictions")]
    public async Task ArgvCryptoOptionsAreRejected(params string[] extra)
    {
        List<string> argv = ["inputs/a.pdf", "--pages", ".", "1"];
        argv.AddRange(extra);
        argv.AddRange(["--", "outputs/a.pdf"]);
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        QpdfDiagnostic diagnostic = SingleError(result);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, diagnostic.Code);
        Assert.DoesNotContain("s3cret-value", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("pw.txt", diagnostic.Message, StringComparison.Ordinal);
    }


    [Fact]
    public async Task ArgvRejectsResponseFilesEmptyAndStdout()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult response = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["@args.txt"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(response).Code);

        QpdfExecutionResult empty = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["--empty", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(empty).Code);

        QpdfExecutionResult stdout = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "-"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(stdout).Code);
    }

    [Fact]
    public async Task ArgvRejectsUnknownOptions()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--flatten-annotations", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task ArgvRejectsMultipleSources()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", "inputs/b.pdf", "1", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JobHappyPathSucceeds()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/j.pdf", "pages": [{"file": ".", "range": "1"}]}""",
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await ReopenOutput(host, "outputs/j.pdf");
        Assert.Equal(1, output.PageCount);
    }

    [Theory]
    [InlineData("""{"outputFile": "o.pdf", "pages": [{"file": ".", "range": "1"}]}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "pages": [{"file": ".", "range": "1"}]}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "o.pdf"}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "o.pdf", "pages": []}""")]

    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "o.pdf", "pages": [{"range": "1"}]}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "o.pdf", "pages": [{"file": ".", "range": 1}]}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "o.pdf", "pages": [{"file": ".", "range": "1", "password": "x"}]}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "-", "pages": [{"file": ".", "range": "1"}]}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "o.pdf", "unknown": 1, "pages": [{"file": ".", "range": "1"}]}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "o.pdf", "pages": [{"file": ".", "range": "1", "collate": 1}]}""")]
    [InlineData("""not json""")]
    [InlineData("""[1, 2]""")]
    public async Task JobStructuralErrorsFail(string jobJson)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        SingleError(result);
    }

    [Fact]
    public async Task JobCryptoKeysAreRejected()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult encrypt = await Qpdf.ExecuteJobAsync(
            host,
            """{"inputFile": "inputs/a.pdf", "outputFile": "o.pdf", "encrypt": {}, "pages": [{"file": ".", "range": "1"}]}""",
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, SingleError(encrypt).Code);

        QpdfExecutionResult password = await Qpdf.ExecuteJobAsync(
            host,
            """{"inputFile": "inputs/a.pdf", "outputFile": "o.pdf", "pages": [{"file": ".", "range": "1", "password": "s3cret"}]}""",
            CancellationToken.None);
        QpdfDiagnostic diagnostic = SingleError(password);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, diagnostic.Code);
        Assert.DoesNotContain("s3cret", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobOversizedDocumentIsRejected()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxInputBytes = 10L });
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host,
            """{"inputFile": "inputs/a.pdf", "outputFile": "o.pdf", "pages": [{"file": ".", "range": "1"}]}""",
            options,
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
    }

    [Fact]
    public async Task EncryptedInputFailsWithCryptoCode()
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "encrypted-decl.pdf"));
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/e.pdf"), bytes, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/e.pdf", "--pages", ".", "1", "--", "outputs/e.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, SingleError(result).Code);
    }

    [Fact]
    public async Task CountMismatchSurfacesWarningExit()
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf"));
        byte[] marker = Encoding.ASCII.GetBytes("/Count 1");
        int at = FindMarker(bytes, marker);
        Assert.True(at >= 0);
        bytes[at + 7] = (byte)'9';
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/m.pdf"), bytes, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/m.pdf", "--pages", ".", "1", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
    }

    [Fact]
    public async Task CancellationYieldsCanceled()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        using CancellationTokenSource source = new();
        source.Cancel();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]),
            source.Token);
        Assert.Equal(QpdfExecutionStatus.Canceled, result.Status);
    }

    [Fact]
    public async Task NullGuardsThrow()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
#pragma warning disable CS8625 // Intentional null literals for negative tests.
        Assert.Throws<ArgumentNullException>(() => new QpdfCommandInvocation(null));
        Assert.Throws<ArgumentNullException>(() => new QpdfExecutionOptions(null));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Qpdf.ExecuteAsync(
            null, new QpdfCommandInvocation(["a"]), CancellationToken.None).AsTask());
#pragma warning restore CS8625
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
