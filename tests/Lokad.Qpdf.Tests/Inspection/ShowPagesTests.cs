using Lokad.Qpdf.Hosting;

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
    [InlineData("inputs/a.pdf", "--show-pages", "--check")]
    [InlineData("inputs/a.pdf", "--show-pages", "--show-npages")]
    [InlineData("inputs/a.pdf", "--show-npages", "--show-pages")]
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
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"o.pdf\", \"showPages\": true}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"showPages\": true}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showPages\": \"yes\"}")]
    [InlineData("{\"inputFile\":\"inputs/a.pdf\",\"showPages\":true,\"showPages\":[]}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"showPages\": true, \"showNpages\": true}")]
    public async Task JobShowPagesRejectsCombinations(string jobJson)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
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
    [InlineData("inputs/a.pdf", "--show-pages", "--with-images", "--with-images")]
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
