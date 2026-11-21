using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Jobs;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class SplitReplaceTests
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

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
    }

    private static string[] PageText(InMemoryQpdfHost host, string name)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From(name), out byte[] bytes));
        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(bytes);
        return document.GetPages().Select(page => page.Text).ToArray();
    }

    [Fact]
    public async Task SplitPagesWritesNamedGroups()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--split-pages", "--pages", ".", "1", ".", "2", "--", "outputs/split.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument first = await Reopen(host, "outputs/split-1.pdf");
        Assert.Equal(1, first.PageCount);
        await using QpdfDocument second = await Reopen(host, "outputs/split-2.pdf");
        Assert.Equal(1, second.PageCount);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/split.pdf"), out byte[] _));
    }

    [Fact]
    public async Task SplitGroupsShareNamingRule()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--split-pages=2", "--pages", ".", "1", ".", "2", "--", "outputs/split.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument both = await Reopen(host, "outputs/split-1-2.pdf");
        Assert.Equal(2, both.PageCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SplitQdfKeepsOriginalNumberCommentsUnlessSuppressed(bool suppress)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        List<string> arguments = ["inputs/a.pdf", "--qdf", "--static-id", "--split-pages"];
        if (suppress) { arguments.Add("--no-original-object-ids"); }
        arguments.AddRange(["--pages", ".", "2", ".", "1", "--", "outputs/split.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(arguments), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        foreach (string path in new[] { "outputs/split-1.pdf", "outputs/split-2.pdf" })
        {
            Assert.True(host.TryGetOutput(QpdfPath.From(path), out byte[] bytes));
            string text = Encoding.ASCII.GetString(bytes);
            Assert.Contains("%QDF-1.0", text, StringComparison.Ordinal);
            Assert.Equal(!suppress, text.Contains("%% Original object ID:", StringComparison.Ordinal));
            await using (QpdfDocument document = await Reopen(host, path))
            {
                Assert.Equal(1, document.PageCount);
            }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SplitPercentDPatternApplies()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--split-pages", "--pages", ".", "1", ".", "2", "--", "outputs/split%d.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument first = await Reopen(host, "outputs/split1.pdf");
        Assert.Equal(1, first.PageCount);
    }

    [Fact]
    public async Task SplitPrunesUnsharedObjects()
    {
        using InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--stream-data=uncompress", "--split-pages", "outputs/split.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/split-1.pdf"), out byte[] first));
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/split-2.pdf"), out byte[] second));
        string firstText = Encoding.ASCII.GetString(first), secondText = Encoding.ASCII.GetString(second);
        Assert.Contains("(First)", firstText, StringComparison.Ordinal);
        Assert.DoesNotContain("(Second)", firstText, StringComparison.Ordinal);
        Assert.Contains("(Second)", secondText, StringComparison.Ordinal);
        Assert.DoesNotContain("(First)", secondText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SplitSizeAndNumericOutputHaveExplicitPrecedence()
    {
        using InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult grouped = await Qpdf.ExecuteAsync(host,
            new(["inputs/a.pdf", "--split-pages", "2", "outputs/group.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, grouped.ExitCode);
        await using (QpdfDocument both = await Reopen(host, "outputs/group-1-2.pdf"))
        { Assert.Equal(2, both.PageCount); }

        QpdfExecutionResult numeric = await Qpdf.ExecuteAsync(host,
            new(["inputs/a.pdf", "--stream-data=uncompress", "--split-pages", "--", "2"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, numeric.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("2-1"), out byte[] first));
        Assert.True(host.TryGetOutput(QpdfPath.From("2-2"), out byte[] second));
        Assert.Contains("(First)", Encoding.ASCII.GetString(first), StringComparison.Ordinal);
        Assert.Contains("(Second)", Encoding.ASCII.GetString(second), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--split-pages", "2", "output")]
    [InlineData("--split-pages", "2147483648", "group size")]
    [InlineData("--split-pages=2147483648", "outputs/split.pdf", "group size")]
    [InlineData("--split-pages=-1", "outputs/split.pdf", "group size")]
    public async Task SplitReportsMissingOutputAndInvalidSizesBeforeOpeningInput(string option, string next, string diagnostic)
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["inputs/a.pdf", option, next]), CancellationToken.None);
        QpdfDiagnostic error = SingleError(result);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, error.Code);
        Assert.Contains(diagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, result.Counters.HostOperations);
    }

    [Fact]
    public async Task SplitOutputAliasingInputFailsCleanly()
    {
        InMemoryQpdfHost host = await HostWithFixture("outputs/m-1.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["outputs/m-1.pdf", "--split-pages", "--pages", ".", "1", "--", "outputs/m.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/m-1.pdf"), out byte[] _));
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/m.pdf"), out byte[] _));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ReplaceInputPublishesAtInputPathWithoutChangingMemoryHostRegistration()
    {
        using InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--replace-input", "--pages", ".", "2", "--"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("inputs/a.pdf"), out byte[] published));
        await using (QpdfDocument original = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None))
        {
            Assert.Equal(2, original.PageCount);
        }
        host.AddInput(QpdfPath.From("inputs/a.pdf"), published, QpdfInputOwnership.Copy);
        await using (QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None))
        {
            Assert.Equal(1, output.PageCount);
        }

        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("response")]
    [InlineData("job")]
    public async Task ReplacementAcceptsPageSourceAliasAfterClosingLeases(string route)
    {
        QpdfPath input = QpdfPath.From("input.pdf");
        QpdfPath alias = QpdfPath.From("./input.pdf");
        using RecordingJobHost host = new(await HostWithFixture(input.Value, "minimal-2page.pdf"), "create", input, "success", true);
        host.Inner.AddInput(QpdfPath.From("args.txt"),
            "input.pdf\n--pages\n./input.pdf\n2\n--\n--replace-input\n"u8.ToArray(), QpdfInputOwnership.Transfer);
        IQpdfHost aliases = new OutputAliasHost(host, alias, input);
        Task<QpdfExecutionResult> executing = (route switch
        {
            "argv" => Qpdf.ExecuteAsync(aliases, new([input.Value, "--pages", alias.Value, "2", "--", "--replace-input"]), CancellationToken.None),
            "response" => Qpdf.ExecuteAsync(aliases, new(["@args.txt"]), CancellationToken.None),
            _ => Qpdf.ExecuteJobAsync(aliases,
                """{"inputFile":"input.pdf","pages":[{"file":"./input.pdf","range":"2"}],"replaceInput":""}""", CancellationToken.None),
        }).AsTask();
        try
        {
            Assert.Same(host.Entered, await Task.WhenAny(host.Entered, executing).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.Equal(input, Assert.Single(host.CreatedPaths));
            Assert.Empty(host.Committed);
            Assert.Equal(2, host.OpenedPaths.Count(path => path == input));
            host.Resume();
            QpdfExecutionResult result = await executing;
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Equal(input, Assert.Single(host.Committed));
            Assert.Equal(["Second"], PageText(host.Inner, input.Value));
            ExecutionJobFixtures.AssertClosed(host, result);
        }
        finally { host.Resume(); await executing; }
    }

    [Theory]
    [InlineData(".", true)]
    [InlineData("./input.pdf", false)]
    public async Task ReplacementKeepsPageStreamSharingScopedToTheSourceSpelling(string secondSource, bool shared)
    {
        QpdfPath input = QpdfPath.From("input.pdf");
        using RecordingJobHost host = new(await HostWithFixture(input.Value, "minimal-2page.pdf"), null, null, "success", true);
        IQpdfHost aliases = new OutputAliasHost(host, QpdfPath.From("./input.pdf"), input);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(aliases,
            new([input.Value, "--pages", ".", "1", secondSource, "1", "--", "--replace-input"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await Reopen(host.Inner, input.Value);
        Assert.Equal(2, output.PageCount);
        PdfReference first = PageContents(output.PageReferences[0].Page);
        PdfReference second = PageContents(output.PageReferences[1].Page);
        Assert.Equal(shared, first.Equals(second));
        Assert.Equal(["First", "First"], PageText(host.Inner, input.Value));
    }

    [Fact]
    public async Task ReplacementReadsADifferentDocumentWhenTheHostResolvesTheAliasSeparately()
    {
        using InMemoryQpdfHost host = await HostWithFixture("input.pdf", "minimal-2page.pdf");
        host.AddInput(QpdfPath.From("./input.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf")), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input.pdf", "--pages", "./input.pdf", "1", "--", "--replace-input"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.False(host.TryGetOutput(QpdfPath.From("./input.pdf"), out _));
        Assert.Equal(["Hello"], PageText(host, "input.pdf"));
    }

    [Theory]
    [InlineData(null, QpdfDiagnosticCode.PasswordRequired)]
    [InlineData("wrong", QpdfDiagnosticCode.InvalidPassword)]
    [InlineData("reader", null)]
    public async Task ReplacementAuthenticatesThePageSourceAliasSeparately(string? password, QpdfDiagnosticCode? error)
    {
        QpdfPath input = QpdfPath.From("input.pdf");
        using RecordingJobHost host = new(await HostWithFixture(input.Value, "security-r6-ascii.pdf"), null, null, "success", true);
        IQpdfHost aliases = new OutputAliasHost(host, QpdfPath.From("./input.pdf"), input);
        List<string> args = [input.Value, "--password=reader", "--pages", "./input.pdf"];
        if (password is not null) { args.Add("--password=" + password); }
        args.AddRange(["1", "--", "--replace-input"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(aliases, new(args), CancellationToken.None);
        if (error is QpdfDiagnosticCode code)
        {
            Assert.Equal(code, SingleError(result).Code);
            Assert.Empty(host.CreatedPaths);
            Assert.Empty(host.Committed);
        }
        else
        {
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Equal(input, Assert.Single(host.Committed));
        }
        ExecutionJobFixtures.AssertClosed(host, result);
    }

    private static PdfReference PageContents(PdfDictionary page)
    {
        Assert.True(page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents));
        return Assert.IsType<PdfReference>(contents);
    }

    [Fact]
    public async Task ReplaceRejectsOutputAndSplitAndMultiInput()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        host.AddInput(QpdfPath.From("inputs/b.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        QpdfExecutionResult withOutput = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--replace-input", "--pages", ".", "1", "--", "outputs/x.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(withOutput).Code);

        QpdfExecutionResult withSplit = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--split-pages", "--replace-input", "--pages", ".", "1"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(withSplit).Code);

        QpdfExecutionResult multiInput = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--replace-input", "--pages", ".", "1", "inputs/b.pdf", "1"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(multiInput).Code);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SplitSingleGroupMatchesDirectBytes()
    {
        InMemoryQpdfHost direct = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult plain = await Qpdf.ExecuteAsync(
            direct,
            new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "2", "--", "outputs/plain.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, plain.ExitCode);
        Assert.True(direct.TryGetOutput(QpdfPath.From("outputs/plain.pdf"), out byte[] expected));

        InMemoryQpdfHost split = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            split,
            new QpdfCommandInvocation(["inputs/a.pdf", "--split-pages=9", "--pages", ".", "2", "--", "outputs/s.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(split.TryGetOutput(QpdfPath.From("outputs/s-1.pdf"), out byte[] actual));
        Assert.Equal(expected, actual);
    }
    [Fact]
    public async Task JobReplaceMatchesArgv()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            argvHost,
            new QpdfCommandInvocation(["inputs/a.pdf", "--replace-input", "--pages", ".", "2", "--"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argv.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("inputs/a.pdf"), out byte[] a));
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            """{"inputFile": "inputs/a.pdf", "pages": [{"file": ".", "range": "2"}], "replaceInput": true}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("inputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
        jobHost.AddInput(QpdfPath.From("inputs/reread.pdf"), b, QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(jobHost, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        Assert.Equal(1, output.PageCount);
    }

    [Fact]
    public async Task JobReplaceRotatesInPlace()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host,
            """{"inputFile": "inputs/a.pdf", "pages": [{"file": ".", "range": "2"}], "replaceInput": true, "rotate": ["+90"]}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("inputs/a.pdf"), out byte[] published));
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), published, QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        Assert.Equal(1, output.PageCount);
        Assert.True(output.PageReferences[0].Page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject _));
    }

    [Theory]
    [InlineData("""{"inputFile": "inputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "replaceInput": "yes"}""")]
    [InlineData("""{"inputFile":"inputs/a.pdf","pages":[{"file":".","range":"1"}],"replaceInput":true,"replaceInput":[]}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "replaceInput": true}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "replaceInput": []}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "pages": [{"file": ".", "range": "1"}], "replaceInput": true, "empty": []}""")]
    public async Task JobReplaceRejectsMalformed(string jobJson)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("inputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task ReplaceYieldsToExhaustedBudgetLeavingInputUntouched()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--replace-input", "--pages", ".", "1", "--"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("inputs/a.pdf"), out byte[] _));
        await using QpdfDocument input = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        Assert.Equal(2, input.PageCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    public async Task JobSplitDefaultsToOnePageInThePlan(string value)
    {
        using InMemoryQpdfHost host = new();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        string settings = System.Text.Json.JsonSerializer.Serialize(new
        { inputFile = "input.pdf", outputFile = "output.pdf", splitPages = value });
        using JobJsonLoader.LoadedJob job = await JobJsonLoader.ParseAsync(host, settings, QpdfLimits.Default,
            governor, CancellationToken.None);
        Assert.Equal(1, job.Plan.SplitPages);
        Assert.Equal(0L, governor.Snapshot().HostOperations);
    }

    [Fact]
    public async Task JobSplitGroupsApply()
    {
        using InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/s.pdf", "pages": [{"file": ".", "range": "2"}, {"file": ".", "range": "1"}], "splitPages": "2"}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(["Second", "First"], PageText(host, "outputs/s-1-2.pdf"));
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/s.pdf"), out byte[] _));
    }

    [Fact]
    public async Task JobSplitWholeFile()
    {
        using InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(
            host,
            """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/s.pdf", "splitPages": ""}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(["First"], PageText(host, "outputs/s-1.pdf"));
        Assert.Equal(["Second"], PageText(host, "outputs/s-2.pdf"));
    }

    [Theory]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/s.pdf", "pages": [{"file": ".", "range": "1"}], "splitPages": "x"}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/s.pdf", "pages": [{"file": ".", "range": "1"}], "splitPages": "0"}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/s.pdf", "pages": [{"file": ".", "range": "1"}], "splitPages": "-1"}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/s.pdf", "pages": [{"file": ".", "range": "1"}], "splitPages": 1}""")]
    [InlineData("""{"inputFile":"inputs/a.pdf","outputFile":"outputs/s.pdf","pages":[{"file":".","range":"1"}],"splitPages":"1","splitPages":[]}""")]
    [InlineData("""{"inputFile": "inputs/a.pdf", "outputFile": "outputs/s.pdf", "pages": [{"file": ".", "range": "1"}], "splitPages": "1", "replaceInput": true}""")]
    public async Task JobSplitMalformedFails(string job)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/s-1.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("--split-pages")]
    [InlineData("--split-pages=")]
    [InlineData("--split-pages=1")]
    public async Task ArgvSplitDefaultsToOnePageInThePlan(string flag)
    {
        using InMemoryQpdfHost host = new();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using JobJsonLoader.LoadedJob job = await JobJsonLoader.ParseArgvAsync(host,
            ["input.pdf", flag, "output.pdf"], QpdfLimits.Default, governor, CancellationToken.None);
        Assert.Equal(1, job.Plan.SplitPages);
        Assert.Equal(0L, governor.Snapshot().HostOperations);
    }

    [Fact]
    public async Task TypedSplitWritesNamedGroups()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/a.pdf"), PdfPageSelectionOptions.FromPages([1, 2]))],
            QpdfPath.From("outputs/split.pdf"),
            new PdfPageMergeOptions { SplitPages = 1 },
            CancellationToken.None);
        Assert.Equal(2, result.PageCount);
        Assert.Equal(["outputs/split-1.pdf", "outputs/split-2.pdf"], result.OutputPaths.Select(path => path.Value));
        await using QpdfDocument first = await Reopen(host, "outputs/split-1.pdf");
        Assert.Equal(1, first.PageCount);
        await using QpdfDocument second = await Reopen(host, "outputs/split-2.pdf");
        Assert.Equal(1, second.PageCount);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/split.pdf"), out byte[] _));
    }

    [Fact]
    public async Task TypedSplitMergesSourcesBeforeGrouping()
    {
        using InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        host.AddInput(QpdfPath.From("inputs/b.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/a.pdf"), PdfPageSelectionOptions.FromPages([1, 2])), new PdfPageSource(QpdfPath.From("inputs/b.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("outputs/split.pdf"),
            new PdfPageMergeOptions { SplitPages = 2 },
            CancellationToken.None);
        Assert.Equal(3, result.PageCount);
        Assert.Equal(["outputs/split-1-2.pdf", "outputs/split-3.pdf"], result.OutputPaths.Select(path => path.Value));
        Assert.Equal(["First", "Second"], PageText(host, "outputs/split-1-2.pdf"));
        Assert.Equal(["Hello"], PageText(host, "outputs/split-3.pdf"));
    }

    [Fact]
    public async Task TypedSplitMatchesArgv()
    {
        using InMemoryQpdfHost typedHost = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        await Qpdf.SelectPagesAsync(
            typedHost,
            [new PdfPageSource(QpdfPath.From("inputs/a.pdf"), PdfPageSelectionOptions.FromPages([1, 2]))],
            QpdfPath.From("outputs/typed.pdf"),
            new PdfPageMergeOptions { SplitPages = 1 },
            CancellationToken.None);
        Assert.True(typedHost.TryGetOutput(QpdfPath.From("outputs/typed-1.pdf"), out byte[] a));
        Assert.True(typedHost.TryGetOutput(QpdfPath.From("outputs/typed-2.pdf"), out byte[] b));
        using InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            argvHost,
            new QpdfCommandInvocation(["inputs/a.pdf", "--split-pages", "--pages", ".", "1-2", "--", "outputs/argv.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, argv.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/argv-1.pdf"), out byte[] c));
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/argv-2.pdf"), out byte[] d));
        await Writing.StreamObservation.SameDecodedGraph(a, c);
        await Writing.StreamObservation.SameDecodedGraph(b, d);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task TypedSplitNonPositiveFailsCleanly(int splitPages)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Qpdf.SelectPagesAsync(
            host,
            [new PdfPageSource(QpdfPath.From("inputs/a.pdf"), PdfPageSelectionOptions.FromPages([1, 2]))],
            QpdfPath.From("outputs/x.pdf"),
            new PdfPageMergeOptions { SplitPages = splitPages },
            CancellationToken.None).AsTask());
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _));
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x-1.pdf"), out byte[] _));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv", 9)]
    [InlineData("argv", int.MaxValue - 1)]
    [InlineData("argv", int.MaxValue)]
    [InlineData("job", 9)]
    [InlineData("job", int.MaxValue - 1)]
    [InlineData("job", int.MaxValue)]
    [InlineData("typed", 9)]
    [InlineData("typed", int.MaxValue - 1)]
    [InlineData("typed", int.MaxValue)]
    public async Task OversizedPositiveGroupsProduceOneCompleteOutput(string route, int groupSize)
    {
        using InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        if (route == "typed")
        {
            PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host,
                [new PdfPageSource(QpdfPath.From("inputs/a.pdf"), PdfPageSelectionOptions.FromPages([1, 2]))],
                QpdfPath.From("outputs/s.pdf"), new PdfPageMergeOptions { SplitPages = groupSize }, CancellationToken.None);
            Assert.Equal(2, result.PageCount);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["inputs/a.pdf", "--split-pages=" + groupSize, "--pages", ".", "1-2", "--", "outputs/s.pdf"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"inputs/a.pdf\",\"outputFile\":\"outputs/s.pdf\",\"pages\":[{\"file\":\".\",\"range\":\"1-2\"}],\"splitPages\":\"" + groupSize + "\"}", CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        await using (QpdfDocument output = await Reopen(host, "outputs/s-1-2.pdf")) { Assert.Equal(2, output.PageCount); }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/s.pdf"), out _));
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/s-1.pdf"), out _));
    }
}
