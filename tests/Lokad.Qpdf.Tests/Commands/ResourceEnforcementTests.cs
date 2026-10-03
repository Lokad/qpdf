using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class ResourceEnforcementTests
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

    private static async Task<InMemoryQpdfHost> HostWithTwoPage()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        host.AddInput(QpdfPath.From("inputs/b.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        return host;
    }

    private static async Task AssertBudgetExceeded(InMemoryQpdfHost host, List<string> argv, string output)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(argv),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, diagnostic.Code);
        Assert.False(host.TryGetOutput(QpdfPath.From(output), out byte[] _));
    }

    [Fact]
    public async Task JsonInspectionYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        await AssertBudgetExceeded(host, ["inputs/a.pdf", "--json", "--json-key=pages", "--", "outputs/a.json"], "outputs/a.json");
    }

    [Theory]
    [InlineData("--stream-data=compress")]
    [InlineData("--compress-streams=y")]
    [InlineData("--decode-level=all")]
    [InlineData("--compression-level=9")]
    [InlineData("--stream-data=compress", "--recompress-flate", "--compression-level=9")]
    public async Task CompressYieldsToExhaustedBudget(params string[] flags)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1", ".", "2" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.pdf"]);
        await AssertBudgetExceeded(host, argv, "outputs/a.pdf");
    }

    [Fact]
    public async Task CoalesceYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        await AssertBudgetExceeded(host, ["inputs/a.pdf", "--pages", ".", "1", "--coalesce-contents", "--", "outputs/a.pdf"], "outputs/a.pdf");
    }

    [Fact]
    public async Task NormalizeYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        await AssertBudgetExceeded(host, ["inputs/a.pdf", "--pages", ".", "1", "--normalize-content=y", "--", "outputs/a.pdf"], "outputs/a.pdf");
    }

    [Fact]
    public async Task PruneYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        await AssertBudgetExceeded(host, ["inputs/a.pdf", "--pages", ".", "1", "--remove-unreferenced-resources=yes", "--", "outputs/a.pdf"], "outputs/a.pdf");
    }

    [Fact]
    public async Task FlattenRotationYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "rotated-1page.pdf");
        await AssertBudgetExceeded(host, ["inputs/a.pdf", "--pages", ".", "1", "--flatten-rotation", "--", "outputs/a.pdf"], "outputs/a.pdf");
    }

    [Fact]
    public async Task OverlayYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithTwoPage();
        await AssertBudgetExceeded(host, ["inputs/a.pdf", "--overlay", "inputs/b.pdf", "--", "--", "outputs/a.pdf"], "outputs/a.pdf");
    }

    [Fact]
    public async Task OverlayAnnotationsYieldToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-1page.pdf");
        host.AddInput(QpdfPath.From("inputs/o.pdf"), await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), "overlay-annot-1page.pdf")), QpdfInputOwnership.Copy);
        await AssertBudgetExceeded(host, ["inputs/a.pdf", "--overlay", "inputs/o.pdf", "--", "--", "outputs/a.pdf"], "outputs/a.pdf");
    }

    [Fact]
    public async Task OverlayFromYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithTwoPage();
        await AssertBudgetExceeded(host, ["inputs/a.pdf", "--overlay", "inputs/b.pdf", "--from=1", "--", "--", "outputs/a.pdf"], "outputs/a.pdf");
    }

    [Fact]
    public async Task OverlayRepeatYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithTwoPage();
        await AssertBudgetExceeded(host, ["inputs/a.pdf", "--overlay", "inputs/b.pdf", "--repeat=1", "--", "--", "outputs/a.pdf"], "outputs/a.pdf");
    }

    [Fact]
    public async Task OverlayToYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithTwoPage();
        await AssertBudgetExceeded(host, ["inputs/a.pdf", "--overlay", "inputs/b.pdf", "--to=1", "--", "--", "outputs/a.pdf"], "outputs/a.pdf");
    }

    [Fact]
    public async Task UnderlayYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithTwoPage();
        await AssertBudgetExceeded(host, ["inputs/a.pdf", "--underlay", "inputs/b.pdf", "--", "--", "outputs/a.pdf"], "outputs/a.pdf");
    }

    [Fact]
    public async Task LinearizeYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        await AssertBudgetExceeded(host, ["inputs/a.pdf", "--pages", ".", "1", ".", "2", "--linearize", "--", "outputs/a.pdf"], "outputs/a.pdf");
    }

    [Fact]
    public async Task QdfYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        await AssertBudgetExceeded(host, ["inputs/a.pdf", "--pages", ".", "1", ".", "2", "--qdf", "--", "outputs/a.pdf"], "outputs/a.pdf");
    }

    [Fact]
    public async Task SplitYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        await AssertBudgetExceeded(host, ["inputs/a.pdf", "--pages", ".", "1", ".", "2", "--split-pages", "--", "outputs/split.pdf"], "outputs/split-1.pdf");
    }

    [Fact]
    public async Task RotateYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "minimal-2page.pdf");
        await AssertBudgetExceeded(host, ["inputs/a.pdf", "--pages", ".", "1", "--rotate=+90", "--", "outputs/a.pdf"], "outputs/a.pdf");
    }

    [Fact]
    public async Task OptimizeImagesYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "photo-1page.pdf");
        await AssertBudgetExceeded(host, ["inputs/a.pdf", "--optimize-images", "--pages", ".", "1", "--", "outputs/a.pdf"], "outputs/a.pdf");
    }

    [Fact]
    public async Task GenerateAppearancesYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "forms-1page.pdf");
        await AssertBudgetExceeded(host, ["inputs/a.pdf", "--pages", ".", "1", "--generate-appearances", "--", "outputs/a.pdf"], "outputs/a.pdf");
    }

    [Theory]
    [InlineData("--externalize-inline-images")]
    [InlineData("--externalize-inline-images", "--ii-min-bytes=5")]
    public async Task ExternalizeInlineImagesYieldsToExhaustedBudget(params string[] flags)
    {
        InMemoryQpdfHost host = await HostWithFixture("inputs/a.pdf", "inline-1page.pdf");
        List<string> argv = new() { "inputs/a.pdf", "--pages", ".", "1" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.pdf"]);
        await AssertBudgetExceeded(host, argv, "outputs/a.pdf");
    }
}
