using Lokad.Qpdf.Hosting;
using System.Text.Json;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class ShowPagesTests
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

    private static List<string> PageLines(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        List<string> lines = new();
        foreach (QpdfDiagnostic diagnostic in result.Diagnostics)
        {
            Assert.Equal(QpdfDiagnosticCode.PageListing, diagnostic.Code);
            lines.Add(diagnostic.Message);
        }

        return lines;
    }

    private static byte[] ContentsFixture(string shape, byte[] payload)
    {
        string references = shape == "generations" ? "6 3 R 4 2 R 6 3 R" : "6 0 R 4 0 R 6 0 R";
        string array = shape.StartsWith("empty", StringComparison.Ordinal) ? "[]"
            : shape == "mixed" ? "[6 0 R 17 (ignored) null 4 0 R 6 0 R]" : "[" + references + "]";
        string contents = shape switch { "direct" or "empty-direct" => array, "single" => "4 0 R", _ => "5 0 R" };
        string indirect = shape switch { "non-array" => "42", "cycle" => "5 0 R", "deep" => "[[[[[[4 0 R]]]]]]", _ => array };
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents " + contents + " >>", null),
            (4, "<< /Length " + payload.Length + " >>", payload),
            (5, indirect, null),
            (6, "<< /Length 0 >>", Array.Empty<byte>()),
        ], 1, shape == "generations" ? new Dictionary<int, int> { [4] = 2, [6] = 3 } : null);
    }

    private static Task<QpdfExecutionResult> InspectContents(IQpdfHost host, string route, QpdfLimits limits, CancellationToken token)
        => route == "argv"
            ? Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--show-pages"]), new QpdfExecutionOptions(limits), token).AsTask()
            : Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","showPages":""}""", new QpdfExecutionOptions(limits), token).AsTask();

    public static IEnumerable<object[]> ContentsShapes()
    {
        foreach (string route in new[] { "argv", "job" })
        foreach (string shape in new[] { "direct", "indirect", "empty-direct", "empty-indirect", "mixed", "generations", "single", "non-array", "cycle" })
        { yield return [route, shape]; }
    }

    [Theory]
    [MemberData(nameof(ContentsShapes))]
    public async Task ContentsListingResolvesOnlyTheOuterArray(string route, string shape)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), ContentsFixture(shape, "q Q"u8.ToArray()), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await InspectContents(host, route, QpdfLimits.Default, CancellationToken.None);
        string[] expected = shape switch
        {
            "empty-direct" or "empty-indirect" => [], "single" => ["4 0 R"],
            "non-array" or "cycle" => ["5 0 R"], "generations" => ["6 3 R", "4 2 R", "6 3 R"],
            _ => ["6 0 R", "4 0 R", "6 0 R"],
        };
        Assert.Equal(new[] { "page 1: 3 0 R", "  content:" }.Concat(expected.Select(item => "    " + item)), PageLines(result));
        QpdfJsonResult report = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("report"), new QpdfJsonOptions(2, ["pages"]), CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(report.Json);
        Assert.Equal(expected, json.RootElement.GetProperty("pages")[0].GetProperty("contents").EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String && item.GetString()?.EndsWith(" R", StringComparison.Ordinal) == true)
            .Select(item => item.GetString()));
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task ContentsArrayInsideObjectStreamMatchesJsonReferences(string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("source"), ContentsFixture("indirect", "q Q"u8.ToArray()), QpdfInputOwnership.Copy);
        QpdfExecutionResult generated = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["source", "--object-streams=generate", "packed"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, generated.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("packed"), out byte[] bytes));
        host.AddInput(QpdfPath.From("input"), bytes, QpdfInputOwnership.Copy);
        QpdfJsonResult report = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("report"), new QpdfJsonOptions(2, ["pages"]), CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(report.Json);
        string[] expected = json.RootElement.GetProperty("pages")[0].GetProperty("contents").EnumerateArray()
            .Select(item => item.GetString() ?? throw new InvalidOperationException("Missing stream reference.")).ToArray();
        QpdfExecutionResult result = await InspectContents(host, route, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(expected.Select(item => "    " + item), PageLines(result).Skip(2));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv", "single")]
    [InlineData("job", "single")]
    [InlineData("argv", "indirect")]
    [InlineData("job", "indirect")]
    public async Task ContentsReferenceListingDoesNotLoadStreamPayloads(string route, string shape)
    {
        byte[] bytes = ContentsFixture(shape, new byte[4194304]);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), bytes, QpdfInputOwnership.Transfer);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = bytes.Length + 60000L };
        QpdfExecutionResult result = await InspectContents(host, route, limits, CancellationToken.None);
        Assert.Equal(shape == "single" ? new[] { "    4 0 R" } : new[] { "    6 0 R", "    4 0 R", "    6 0 R" }, PageLines(result).Skip(2));
        Assert.InRange(result.Counters.PeakMemoryBytes, 1L, limits.MaxMemoryBytes);
        Assert.Equal(0L, result.Counters.TotalDecodedBytes); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    public async Task ContentsInspectionKeepsDepthAndCancellationBounds(string route, bool canceled)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), ContentsFixture("deep", []), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await InspectContents(host, route, QpdfLimits.Default with { MaxNestingDepth = 4L }, new CancellationToken(canceled));
        Assert.Equal(canceled ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ReportsTwoPageDictionaries()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-pages"]), CancellationToken.None);
        List<string> lines = PageLines(result);
        Assert.Equal(
            ["page 1: 3 0 R", "  content:", "    5 0 R", "page 2: 4 0 R", "  content:", "    6 0 R"],
            lines);
    }

    [Fact]
    public async Task ReportsSinglePageDictionary()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-pages"]), CancellationToken.None);
        List<string> lines = PageLines(result);
        Assert.Equal(["page 1: 3 0 R", "  content:", "    4 0 R"], lines);
    }

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    [Fact]
    public async Task FreedContentsReferenceListsEmpty()
    {
        // The page still references the freed 4 0 R pair: like the reference,
        // selection drops the dead pair instead of resurrecting the reused object.
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "incr-freereuse-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        InMemoryQpdfHost check = HostWith("inputs/b.pdf", pdf);
        QpdfExecutionResult shown = await Qpdf.ExecuteAsync(
            check, new QpdfCommandInvocation(["inputs/b.pdf", "--show-pages"]), CancellationToken.None);
        Assert.Equal(["page 1: 3 0 R", "  content:"], PageLines(shown));
    }
    [Theory]
    [InlineData("inputs/a.pdf", "--show-pages", "--pages", ".", "1", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-pages", "--check", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-pages", "--linearize", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-pages", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--show-pages=x")]
    public async Task RejectsCombinationsAndValuedForm(params string[] argv)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Theory]
    [InlineData("--show-pages", "--show-npages")]
    [InlineData("--show-npages", "--show-pages")]
    [InlineData("--show-pages", "--with-images", "--with-images")]
    public async Task CombinedPageInspectionKeepsCountAndPageIdentity(params string[] flags)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["inputs/a.pdf", .. flags]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageListing && d.Message == "page 2: 4 0 R");
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task JobShowPagesMatchesArgv()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            argvHost, new QpdfCommandInvocation(["inputs/a.pdf", "--show-pages"]), CancellationToken.None);
        List<string> argvLines = PageLines(argv);
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"showPages\": true}",
            CancellationToken.None);
        Assert.Equal(argvLines, PageLines(job));
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"o.pdf\", \"showPages\": true}", false)]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"showPages\": true}", true)]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showPages\": \"yes\"}", false)]
    [InlineData("{\"inputFile\":\"inputs/a.pdf\",\"showPages\":true,\"showPages\":[]}", false)]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showPages\": true, \"showNpages\": true}", true)]
    public async Task JobShowPagesValidatesCombinations(string jobJson, bool accepted)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        if (accepted)
        {
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageListing && d.Message == "page 1: 3 0 R");
            Assert.Equal(0L, host.OpenLeaseCount);
            return;
        }
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("o.pdf"), out byte[] _));
    }

    [Fact]
    public async Task EncryptedInputFailsWithCryptoCode()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/e.pdf", "encrypted-decl.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/e.pdf", "--show-pages"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, diagnostic.Code);
    }

    [Fact]
    public async Task FailsOnExhaustedInputBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxInputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--show-pages"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, diagnostic.Code);
    }

    [Fact]
    public async Task ListsDirectImagesSortedSkippingForms()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "images-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-pages", "--with-images"]), CancellationToken.None);
        List<string> lines = PageLines(result);
        Assert.Equal(
            ["page 1: 3 0 R", "  images:", "    /Im0: 5 0 R, 2 x 2", "  content:", "    4 0 R"],
            lines);
    }

    [Fact]
    public async Task OmitsImagesSectionWithoutImages()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-pages", "--with-images"]), CancellationToken.None);
        List<string> lines = PageLines(result);
        Assert.Equal(["page 1: 3 0 R", "  content:", "    4 0 R"], lines);
    }

    [Fact]
    public async Task WarnsOnMissingDimensions()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "images-nodim-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-pages", "--with-images"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        List<string> listings = new();
        List<QpdfDiagnostic> warnings = new();
        foreach (QpdfDiagnostic diagnostic in result.Diagnostics)
        {
            if (diagnostic.Code == QpdfDiagnosticCode.PageListing)
            {
                listings.Add(diagnostic.Message);
            }
            else
            {
                warnings.Add(diagnostic);
            }
        }

        Assert.Equal(["page 1: 3 0 R", "  images:", "    /NoDim: 5 0 R, 0 x 0", "  content:", "    4 0 R"], listings);
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, warning => Assert.Equal(QpdfDiagnosticSeverity.Warning, warning.Severity));
    }

    [Theory]
    [InlineData("inputs/a.pdf", "--with-images", "--", "outputs/a.pdf")]
    [InlineData("inputs/a.pdf", "--check", "--with-images")]
    [InlineData("inputs/a.pdf", "--show-npages", "--with-images")]
    [InlineData("inputs/a.pdf", "--show-pages", "--with-images=x")]
    [InlineData("inputs/a.pdf", "--show-pages", "--with-images=")]
    public async Task RejectsImagesWithoutShowPages(params string[] argv)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task JobWithImagesMatchesArgv()
    {
        InMemoryQpdfHost argvHost = await HostWithFixture("inputs/a.pdf", "images-1page.pdf");
        QpdfExecutionResult argv = await Qpdf.ExecuteAsync(
            argvHost, new QpdfCommandInvocation(["inputs/a.pdf", "--show-pages", "--with-images"]), CancellationToken.None);
        List<string> argvLines = PageLines(argv);
        InMemoryQpdfHost jobHost = await HostWithFixture("inputs/a.pdf", "images-1page.pdf");
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"showPages\": true, \"withImages\": true}",
            CancellationToken.None);
        Assert.Equal(argvLines, PageLines(job));
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"o.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"withImages\": true}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showNpages\": true, \"withImages\": true}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showPages\": true, \"withImages\": \"yes\"}")]
    [InlineData("{\"inputFile\":\"inputs/a.pdf\",\"showPages\":true,\"withImages\":true,\"withImages\":[]}")]
    public async Task JobWithImagesRejectsCombinations(string jobJson)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("o.pdf"), out byte[] _));
    }

    [Fact]
    public async Task WithImagesYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "images-1page.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxInputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--show-pages", "--with-images"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, diagnostic.Code);
    }
}
