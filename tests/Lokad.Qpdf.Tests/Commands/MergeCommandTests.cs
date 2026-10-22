using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class MergeCommandTests
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

    private static async Task<List<string>> OutputMarkers(InMemoryQpdfHost host, string output)
    {
        if (!host.TryGetOutput(QpdfPath.From(output), out byte[] published))
        {
            throw new Xunit.Sdk.XunitException("Output was not published: " + output + ".");
        }

        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, published, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
        List<string> markers = new();
        for (int i = 0; i < document.PageCount; i++)
        {
            PdfDictionary page = document.PageReferences[i].Page;
            Assert.True(page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject raw));
            PdfReference reference = Assert.IsType<PdfReference>(raw);
            PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(reference.ObjectNumber));
            markers.Add(Encoding.ASCII.GetString(Writing.StreamObservation.Decode(stream)));
        }

        return markers;
    }

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
    }

    [Fact]
    public async Task ArgvMultiFileMergesInOrder()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        host.AddInput(QpdfPath.From("inputs/b.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", "inputs/a.pdf", "2", "inputs/b.pdf", "1", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> markers = await OutputMarkers(host, "outputs/m.pdf");
        Assert.Equal(2, markers.Count);
        Assert.Contains("Second", markers[0], StringComparison.Ordinal);
        Assert.Contains("Hello", markers[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArgvCollateInterleavesSources()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--collate", "--pages", ".", "1", ".", "2", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> markers = await OutputMarkers(host, "outputs/m.pdf");
        Assert.Equal(2, markers.Count);
        Assert.Contains("First", markers[0], StringComparison.Ordinal);
        Assert.Contains("Second", markers[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArgvCollateGroupsApply()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--collate=2,1", "--pages", ".", "1", "--file=inputs/a.pdf", "--range=2", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> markers = await OutputMarkers(host, "outputs/m.pdf");
        Assert.Equal(2, markers.Count);
    }

    [Fact]
    public async Task ArgvMergeErrorsAreExplicit()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult badCollate = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--collate=x", "--pages", ".", "1", ".", "2", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(badCollate).Code);

        QpdfExecutionResult outOfRange = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "9", ".", "1", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(outOfRange).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] _));
    }

    [Fact]
    public async Task JobMultiEntryMergesInOrder()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        host.AddInput(QpdfPath.From("inputs/b.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/m.pdf", "pages": [{"file": ".", "range": "2"}, {"file": "inputs/b.pdf", "range": "1"}]}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> markers = await OutputMarkers(host, "outputs/m.pdf");
        Assert.Equal(2, markers.Count);
        Assert.Contains("Second", markers[0], StringComparison.Ordinal);
        Assert.Contains("Hello", markers[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobSingleEntryKeepsSingleBehavior()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/j.pdf", "pages": [{"file": ".", "range": "1"}]}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await Reopen(host, "outputs/j.pdf");
        Assert.Equal(1, output.PageCount);
    }

    [Fact]
    public async Task ArgvMainLevelCollateMatchesInsideForm()
    {
        InMemoryQpdfHost insideHost = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult insideResult = await Qpdf.ExecuteAsync(
            insideHost,
            new QpdfCommandInvocation(["inputs/a.pdf", "--collate", "--pages", ".", "1", ".", "2", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, insideResult.ExitCode);
        Assert.True(insideHost.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] inside));
        InMemoryQpdfHost mainHost = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult mainResult = await Qpdf.ExecuteAsync(
            mainHost,
            new QpdfCommandInvocation(["--collate", "inputs/a.pdf", "--pages", ".", "1", ".", "2", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, mainResult.ExitCode);
        Assert.True(mainHost.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] main));
        Assert.Equal(inside, main);
    }

    [Fact]
    public async Task ArgvCollateCountMismatchFailsExplicitly()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--collate=1,2", "--pages", ".", "1", ".", "2", "inputs/a.pdf", "1", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] _));
    }

    [Fact]
    public async Task ArgvUnusedCollatePreservesInspectedCount()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["--collate", "inputs/a.pdf", "--check", "--show-npages"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount && d.Message.EndsWith(": 1", StringComparison.Ordinal));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task JobCollateMatchesArgv()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult argvResult = await Qpdf.ExecuteAsync(
            argvHost,
            new QpdfCommandInvocation(["inputs/a.pdf", "--collate", "--pages", ".", "1", ".", "2", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argvResult.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] a));
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult jobResult = await Qpdf.ExecuteJobAsync(
            jobHost,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/m.pdf", "pages": [{"file": ".", "range": "1"}, {"file": ".", "range": "2"}], "collate": ""}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, jobResult.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] b));
        Assert.Equal(a, b);
        List<string> markers = await OutputMarkers(jobHost, "outputs/m.pdf");
        Assert.Equal(2, markers.Count);
        Assert.Contains("First", markers[0], StringComparison.Ordinal);
        Assert.Contains("Second", markers[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobCollateGroupsApply()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/m.pdf", "pages": [{"file": ".", "range": "1"}, {"file": "inputs/a.pdf", "range": "2"}], "collate": "2,1"}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> markers = await OutputMarkers(host, "outputs/m.pdf");
        Assert.Equal(2, markers.Count);
        Assert.Contains("First", markers[0], StringComparison.Ordinal);
        Assert.Contains("Second", markers[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobCollateSingleEntryMerges()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/m.pdf", "pages": [{"file": ".", "range": "1"}], "collate": ""}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await Reopen(host, "outputs/m.pdf");
        Assert.Equal(1, output.PageCount);
    }

    [Theory]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/m.pdf", "pages": [{"file": ".", "range": "1"}, {"file": ".", "range": "2"}], "collate": "x"}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/m.pdf", "pages": [{"file": ".", "range": "1"}, {"file": ".", "range": "2"}], "collate": "1,"}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/m.pdf", "pages": [{"file": ".", "range": "1"}, {"file": ".", "range": "2"}], "collate": "-1"}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/m.pdf", "pages": [{"file": ".", "range": "1"}], "collate": "1,2"}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/m.pdf", "pages": [{"file": ".", "range": "1"}, {"file": ".", "range": "2"}], "collate": "1,2,3"}""")]
    [InlineData("""{"inputFile":"inputs/a.pdf","outputFile":"outputs/m.pdf","pages":[{"file":".","range":"1"},{"file":".","range":"2"}],"collate":"1","collate":[]}""")]
    public async Task JobCollateMalformedFails(string job)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] _));
    }

    [Fact]
    public async Task JobCollateWithoutPagesKeepsTheWholeDocument()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/m.pdf", "coalesceContents": true, "collate": ""}""",
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] bytes));
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
    }

    [Theory]
    [InlineData("1-2", "First", "Second")]
    [InlineData("2-1", "Second", "First")]
    public async Task ArgvRangeSelectsPagesInOrder(string range, string first, string second)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", range, "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> markers = await OutputMarkers(host, "outputs/m.pdf");
        Assert.Equal(2, markers.Count);
        Assert.Contains(first, markers[0], StringComparison.Ordinal);
        Assert.Contains(second, markers[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("z", "Second")]
    [InlineData("r2", "First")]
    [InlineData("1-2:even", "Second")]
    [InlineData("1-2,x2", "First")]
    [InlineData("1,1", "First")]
    public async Task ArgvRangeFormsSelectExpectedPages(string range, string marker)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", range, "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> markers = await OutputMarkers(host, "outputs/m.pdf");
        Assert.Contains(marker, markers[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArgvRangeOutOfRangeFailsWithoutOutput()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1-3", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("1-")]
    [InlineData("x1")]
    [InlineData("r")]
    [InlineData("1-2-3")]
    [InlineData("1-2:even:odd")]
    public async Task ArgvRangeMalformedFailsWithoutOutput(string range)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", range, "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] _));
    }

    [Fact]
    public async Task JobRangeMatchesArgv()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult argvResult = await Qpdf.ExecuteAsync(
            argvHost,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "2-1", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argvResult.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] a));
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult jobResult = await Qpdf.ExecuteJobAsync(
            jobHost,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/m.pdf", "pages": [{"file": ".", "range": "2-1"}]}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, jobResult.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] b));
        Assert.Equal(a, b);
        List<string> markers = await OutputMarkers(jobHost, "outputs/m.pdf");
        Assert.Equal(2, markers.Count);
        Assert.Contains("Second", markers[0], StringComparison.Ordinal);
        Assert.Contains("First", markers[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypedRangeSelectionExpandsInOrder()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/a.pdf"), PdfPageSelectionOptions.FromRange("2-1"))],
            QpdfPath.From("outputs/m.pdf"),
            CancellationToken.None);
        Assert.Equal(2, result.PageCount);
        List<string> markers = await OutputMarkers(host, "outputs/m.pdf");
        Assert.Equal(2, markers.Count);
        Assert.Contains("Second", markers[0], StringComparison.Ordinal);
        Assert.Contains("First", markers[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArgvCollateRangesInterleaveInOrder()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["--collate", "inputs/a.pdf", "--pages", "inputs/a.pdf", "1-2", "inputs/a.pdf", "2-1", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> markers = await OutputMarkers(host, "outputs/m.pdf");
        Assert.Equal(4, markers.Count);
        Assert.Contains("First", markers[0], StringComparison.Ordinal);
        Assert.Contains("Second", markers[1], StringComparison.Ordinal);
        Assert.Contains("Second", markers[2], StringComparison.Ordinal);
        Assert.Contains("First", markers[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobCollateRangesMatchArgv()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult argvResult = await Qpdf.ExecuteAsync(
            argvHost,
            new QpdfCommandInvocation(["--collate", "inputs/a.pdf", "--pages", "inputs/a.pdf", "1-2", "inputs/a.pdf", "2-1", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argvResult.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] a));
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult jobResult = await Qpdf.ExecuteJobAsync(
            jobHost,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/m.pdf", "pages": [{"file": "inputs/a.pdf", "range": "1-2"}, {"file": "inputs/a.pdf", "range": "2-1"}], "collate": ""}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, jobResult.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task CollateRangesCountMismatchFailsWithoutOutput()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["--collate=1,2", "inputs/a.pdf", "--pages", "inputs/a.pdf", "1", "inputs/a.pdf", "2", "inputs/a.pdf", "1", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] _));
    }

    [Fact]
    public async Task CollateYieldsToStructuralCardinalityBound()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxContainerSize = 32L };
        string range = string.Join(',', Enumerable.Repeat("1", 17));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["--collate", "inputs/a.pdf", "--pages", "inputs/a.pdf", range, "inputs/a.pdf", range, "--", "outputs/m.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] _));
    }

    [Fact]
    public async Task ArgvPositionalOmittedRangeSelectsAllPages()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", "inputs/a.pdf", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> markers = await OutputMarkers(host, "outputs/m.pdf");
        Assert.Equal(2, markers.Count);
        Assert.Contains("First", markers[0], StringComparison.Ordinal);
        Assert.Contains("Second", markers[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArgvFileOmittedRangeSelectsAllPages()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", "--file=inputs/a.pdf", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> markers = await OutputMarkers(host, "outputs/m.pdf");
        Assert.Equal(2, markers.Count);
    }

    [Fact]
    public async Task ArgvFirstFileOmittedRangeSelectsAllPages()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        host.AddInput(QpdfPath.From("inputs/b.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-2page.pdf")), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", "--file=inputs/a.pdf", "--file=inputs/b.pdf", "--range=1", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> markers = await OutputMarkers(host, "outputs/m.pdf");
        Assert.Equal(2, markers.Count);
        Assert.Contains("Hello", markers[0], StringComparison.Ordinal);
        Assert.Contains("First", markers[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArgvPagesStillRequiresOneFile()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] _));
    }

    [Fact]
    public async Task JobOmittedRangeMatchesArgv()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult argvResult = await Qpdf.ExecuteAsync(
            argvHost,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argvResult.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] a));
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult jobResult = await Qpdf.ExecuteJobAsync(
            jobHost,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/m.pdf", "pages": [{"file": "."}]}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, jobResult.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] b));
        Assert.Equal(a, b);
        List<string> markers = await OutputMarkers(jobHost, "outputs/m.pdf");
        Assert.Equal(2, markers.Count);
    }

    [Fact]
    public async Task TypedNullRangeSelectsAllPages()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        IReadOnlyList<int> resolved = PdfPageSelectionOptions.FromRange(null).Resolve(3, long.MaxValue, governor, memory);
        Assert.Equal([1, 2, 3], resolved);
    }

    private static async Task<QpdfDocument> Reopen(InMemoryQpdfHost host, string output)
    {
        if (!host.TryGetOutput(QpdfPath.From(output), out byte[] published))
        {
            throw new Xunit.Sdk.XunitException("Output was not published: " + output + ".");
        }

        QpdfPath reread = QpdfPath.From("inputs/reread.pdf");
        host.AddInput(reread, published, QpdfInputOwnership.Copy);
        return await QpdfDocument.OpenAsync(host, reread, CancellationToken.None);
    }
}
