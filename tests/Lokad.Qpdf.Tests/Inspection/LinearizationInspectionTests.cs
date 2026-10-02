using System.Text;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class LinearizationInspectionTests
{
    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static async Task<byte[]> FixtureBytes(string fixture)
    {
        return await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), fixture));
    }

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        return Assert.Single(result.Diagnostics);
    }

    private static byte[] ReplaceUnique(byte[] input, string needle, string replacement)
    {
        byte[] needleBytes = Encoding.ASCII.GetBytes(needle);
        byte[] replacementBytes = Encoding.ASCII.GetBytes(replacement);
        Assert.Equal(needleBytes.Length, replacementBytes.Length);
        int at = IndexOf(input, needleBytes);
        Assert.NotEqual(-1, at);
        Assert.Equal(-1, IndexOf(input, needleBytes, at + 1));
        byte[] output = new byte[input.Length];
        Buffer.BlockCopy(input, 0, output, 0, input.Length);
        Buffer.BlockCopy(replacementBytes, 0, output, at, replacementBytes.Length);
        return output;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start = 0)
    {
        for (int i = start; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int k = 0; k < needle.Length; k++)
            {
                if (haystack[i + k] != needle[k])
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
    public async Task CheckLinearizationReportsNoErrors()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes("linearized-2page.pdf"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check-linearization"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        QpdfDiagnostic line = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.LinearizationReport, line.Code);
        Assert.Contains("inputs/a.pdf", line.Message, StringComparison.Ordinal);
        Assert.Contains("no linearization errors", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckLinearizationOnPlainFile()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes("minimal-1page.pdf"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check-linearization"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        QpdfDiagnostic line = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.LinearizationReport, line.Code);
        Assert.Contains("is not linearized", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckLinearizationFailsOnBadHints()
    {
        byte[] bytes = ReplaceUnique(await FixtureBytes("linearized-2page.pdf"), "/H [ 550 127 ]", "/H [ 550 999 ]");
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check-linearization"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.CheckFailed, SingleError(result).Code);
    }

    [Fact]
    public async Task CheckLinearizationRejectsOutputFile()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes("minimal-1page.pdf"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check-linearization", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("inputs/a.pdf", "--check-linearization", "--pages", ".", "1", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--check-linearization", "--linearize", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--check-linearization", "--qdf", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--json", "--check-linearization", "--", "outputs/a.json")]
    [InlineData("inputs/a.pdf", "--check-linearization", "--list-attachments")]
    [InlineData("inputs/a.pdf", "--check-linearization", "--check-linearization")]
    public async Task CheckLinearizationRejectsCombinations(params string[] argv)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes("minimal-1page.pdf"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JobCheckLinearizationMatchesArgv()
    {
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", await FixtureBytes("linearized-2page.pdf"));
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            argvHost, new QpdfCommandInvocation(["inputs/a.pdf", "--check-linearization"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argv.ExitCode);
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", await FixtureBytes("linearized-2page.pdf"));
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"checkLinearization\": \"\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.Equal(argv.Diagnostics.Count, job.Diagnostics.Count);
        Assert.Equal(argv.Diagnostics[0].Message, job.Diagnostics[0].Message);
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"o.pdf\", \"checkLinearization\": \"\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"checkLinearization\": \"\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"linearize\": \"\", \"checkLinearization\": \"\"}")]
    public async Task JobCheckLinearizationRejectsCombinations(string jobJson)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes("minimal-1page.pdf"));
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        SingleError(result);
    }

    [Fact]
    public async Task CheckPlusCheckLinearization()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes("linearized-2page.pdf"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--check-linearization"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.LinearizationReport
            && diagnostic.Message.Contains("no linearization errors", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckPlusCheckLinearizationSurfacesHintDefects()
    {
        byte[] bytes = ReplaceUnique(await FixtureBytes("linearized-2page.pdf"), "/H [ 550 127 ]", "/H [ 550 999 ]");
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--check-linearization"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.CheckFailed);
    }
}
