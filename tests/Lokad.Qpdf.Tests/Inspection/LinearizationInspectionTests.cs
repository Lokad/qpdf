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
    public async Task ShowLinearizationReportsHintTables()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes("linearized-2page.pdf"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-linearization"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> lines = new();
        foreach (QpdfDiagnostic diagnostic in result.Diagnostics)
        {
            Assert.Equal(QpdfDiagnosticCode.LinearizationReport, diagnostic.Code);
            lines.Add(diagnostic.Message);
        }

        string[] expected =
        [
            "inputs/a.pdf: linearization data:",
            "file_size: 1501",
            "first_page_object: 7",
            "first_page_end: 988",
            "npages: 2",
            "xref_zero_offset: 1303",
            "first_page: 0",
            "H_offset: 550",
            "H_length: 127",
            "Page Offsets Hint Table",
            "min_nobjects: 2",
            "first_page_offset: 677",
            "min_page_length: 242",
            "shared_denominator: 4",
            "Page 0:",
            "  nobjects: 3",
            "  length: 311",
            "  nshared_objects: 0",
            "Page 1:",
            "  nobjects: 2",
            "  length: 242",
            "  nshared_objects: 1",
            "    identifier 0: 2",
            "    numerator 0: 0",
            "Shared Objects Hint Table",
            "first_shared_obj: 0",
            "first_shared_offset: 0",
            "nshared_first_page: 3",
            "nshared_total: 3",
            "min_group_length: 70",
            "Shared Object 0:",
            "  group length: 128",
            "Shared Object 1:",
            "  group length: 113",
            "Shared Object 2:",
            "  group length: 70",
            "Computed Linearization Part Assignments",
            "part 4 (root, document open):",
            " 5",
            "part 6 (first page):",
            " 7-9",
            "part 7 (other pages, private):",
            " 1-2",
            "part 8 (other pages, shared): none",
            "part 9 (everything else):",
            " 3",
        ];
        int at = 0;
        foreach (string want in expected)
        {
            bool found = false;
            while (at < lines.Count)
            {
                if (lines[at] == want)
                {
                    found = true;
                    at += 1;
                    break;
                }

                at += 1;
            }

            Assert.True(found, $"Missing show line: {want}");
        }
    }

    [Theory]
    [InlineData("lin-objstm-1page.pdf")]
    [InlineData("lin-objstm-2page.pdf")]
    public async Task CheckLinearizationReportsNoErrorsOnObjectStreams(string fixture)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes(fixture));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check-linearization"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        QpdfDiagnostic line = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.LinearizationReport, line.Code);
        Assert.Contains("no linearization errors", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowLinearizationReportsStreamedHintTables()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes("lin-objstm-2page.pdf"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-linearization"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> lines = new();
        foreach (QpdfDiagnostic diagnostic in result.Diagnostics)
        {
            Assert.Equal(QpdfDiagnosticCode.LinearizationReport, diagnostic.Code);
            lines.Add(diagnostic.Message);
        }

        string[] expected =
        [
            "inputs/a.pdf: linearization data:",
            "file_size: 1730",
            "npages: 2",
            "Page Offsets Hint Table",
            "Shared Objects Hint Table",
            "  group length: 130",
            "Computed Linearization Part Assignments",
            "part 6 (first page):",
        ];
        int at = 0;
        foreach (string want in expected)
        {
            bool found = false;
            while (at < lines.Count)
            {
                if (lines[at] == want)
                {
                    found = true;
                    at += 1;
                    break;
                }

                at += 1;
            }

            Assert.True(found, $"Missing show line: {want}");
        }
    }

    [Fact]
    public async Task ShowLinearizationOnPlainFile()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes("minimal-1page.pdf"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-linearization"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        QpdfDiagnostic line = Assert.Single(result.Diagnostics);
        Assert.Contains("is not linearized", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowLinearizationFailsOnBadHints()
    {
        byte[] bytes = ReplaceUnique(await FixtureBytes("linearized-2page.pdf"), "/H [ 550 127 ]", "/H [ 550 999 ]");
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-linearization"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.CheckFailed, SingleError(result).Code);
    }

    [Theory]
    [InlineData("inputs/a.pdf", "--show-linearization", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-linearization", "--pages", ".", "1", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-linearization", "--linearize", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--json", "--show-linearization", "--", "outputs/a.json")]
    [InlineData("inputs/a.pdf", "--show-linearization", "--show-linearization")]
    public async Task ShowLinearizationRejectsCombinations(params string[] argv)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes("minimal-1page.pdf"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JobShowLinearizationMatchesArgv()
    {
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", await FixtureBytes("linearized-2page.pdf"));
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            argvHost, new QpdfCommandInvocation(["inputs/a.pdf", "--show-linearization"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argv.ExitCode);
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", await FixtureBytes("linearized-2page.pdf"));
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"showLinearization\": \"\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.Equal(argv.Diagnostics.Count, job.Diagnostics.Count);
        for (int i = 0; i < argv.Diagnostics.Count; i++)
        {
            Assert.Equal(argv.Diagnostics[i].Message, job.Diagnostics[i].Message);
        }
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"o.pdf\", \"showLinearization\": \"\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"showLinearization\": \"\"}")]
    public async Task JobShowLinearizationRejectsCombinations(string jobJson)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes("minimal-1page.pdf"));
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        SingleError(result);
    }

    [Fact]
    public async Task CheckAndShowLinearizationCombine()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes("linearized-2page.pdf"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check-linearization", "--show-linearization"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("no linearization errors", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("Page Offsets Hint Table", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckPlusShowLinearization()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes("linearized-2page.pdf"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--check", "--show-linearization"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Message.Contains("no linearization errors", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("Page Offsets Hint Table", StringComparison.Ordinal));
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

    [Theory]
    [InlineData("--check-linearization")]
    [InlineData("--show-linearization")]
    public async Task LinearizationInspectionYieldsToExhaustedInputBudget(params string[] flags)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes("linearized-2page.pdf"));
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        QpdfLimits limits = QpdfLimits.Default with { MaxInputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(argv),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(result.Diagnostics).Code);
    }
}
