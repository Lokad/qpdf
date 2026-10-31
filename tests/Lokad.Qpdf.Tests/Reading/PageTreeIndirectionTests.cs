using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class PageTreeIndirectionTests
{
    private static byte[] Fixture(string variant, string link)
    {
        string catalog = variant is "catalog" or "all" ? "6 0 R" : "/Catalog";
        string pages = variant is "pages" or "all" ? "7 0 R" : "/Pages";
        string page = variant is "pages" or "all" or "type-cycle" or "type-stale" ? "8 0 R" : "/Page";
        string kids = variant is "kids" or "all" or "bad-kids" or "kids-cycle" ? "9 0 R" : "[3 0 R 10 0 R]";
        if (variant == "type-stale") { page = "8 1 R"; }
        if (variant == "kids-stale") { kids = "9 1 R"; }
        string count = variant is "count" or "all" ? "11 0 R" : "2";
        if (variant == "bad-type") { catalog = "6 0 R"; }
        return CheckTests.Assemble(
        [
            (1, "<< /Type " + catalog + " /Pages 2 0 R >>", null),
            (2, "<< /Type " + pages + " /Kids " + kids + " /Count " + count + " >>", null),
            (3, "<< /Type " + page + " /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> /Contents 4 0 R /Omitted 10 0 R " + link + " >>", null),
            (4, "<< /Length 4 >>", "q Q\n"u8.ToArray()),
            (5, "null", null),
            (6, variant == "bad-type" ? "(Catalog)" : "/Catalog", null),
            (7, "/Pages", null),
            (8, variant == "type-cycle" ? "8 0 R" : "/Page", null),
            (9, variant == "bad-kids" ? "42" : variant == "kids-cycle" ? "[2 0 R]" : "[3 0 R 10 0 R]", null),
            (10, "<< /Type " + page + " /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null),
            (11, "2", null),
        ], 1);
    }

    private static async Task Run(IQpdfHost host, string route, bool rewrite)
    {
        if (route == "typed")
        {
            if (rewrite) { await Qpdf.SelectPageAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new(1), CancellationToken.None); }
            else
            {
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
                Assert.Equal(2, document.PageCount);
            }
            return;
        }
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(rewrite ? ["input", "--pages", ".", "1", "--", "output"] : ["input", "--check"]), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, rewrite ? "{\"inputFile\":\"input\",\"pages\":[{\"file\":\".\",\"range\":\"1\"}],\"outputFile\":\"output\"}" : "{\"inputFile\":\"input\",\"check\":\"\"}", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.DoesNotContain(result.Diagnostics, item => item.Severity == QpdfDiagnosticSeverity.Warning);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    public static IEnumerable<object[]> ValidCases()
    {
        foreach (string variant in new[] { "direct", "catalog", "pages", "kids", "count", "all" })
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (bool rewrite in new[] { false, true })
        { yield return [variant, route, rewrite]; }
    }

    [Theory]
    [MemberData(nameof(ValidCases))]
    public async Task EquivalentPageTreesOpenAndCopyWithOmittedPagePruning(string variant, string route, bool rewrite)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(variant, string.Empty), QpdfInputOwnership.Copy);
        await Run(host, route, rewrite); Assert.Equal(0, host.OpenLeaseCount);
        if (!rewrite) { return; }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("result"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("result"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Type"), out QpdfObject type));
        Assert.Equal("Page", Assert.IsType<PdfName>(type).DisplayText);
        Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Omitted"), out QpdfObject omitted));
        Assert.IsType<PdfNull>(omitted);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task IndirectDeclaredCountReportsTheSameNumber(string route)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("all", string.Empty), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--show-npages"]), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"showNpages\":\"\"}", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, item => item.Message == "input: 2");
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.PageCountMismatch);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task IndirectPageTypesRemainWritableWithLinearization(string route)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("all", string.Empty), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--linearize", "--pages", ".", "1", "--", "output"]), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"pages\":[{\"file\":\".\",\"range\":\"1\"}],\"linearize\":\"\",\"outputFile\":\"output\"}", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(0, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("result"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("result"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
    }

    public static IEnumerable<object[]> InvalidCases()
    {
        foreach (string variant in new[] { "bad-type", "type-cycle", "type-stale", "bad-kids", "kids-cycle", "kids-stale" })
        foreach (string route in new[] { "typed", "argv", "job" })
        { yield return [variant, route]; }
    }

    private static async Task Seed(InMemoryQpdfHost host)
    {
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }

    private static async Task AssertRejected(InMemoryQpdfHost host, string route)
    {
        await Seed(host);
        if (route == "typed")
        { await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.SelectPageAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new(1), CancellationToken.None).AsTask()); }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--pages", ".", "1", "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"pages\":[{\"file\":\".\",\"range\":\"1\"}],\"outputFile\":\"output\"}", CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.NotEmpty(result.Diagnostics); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public async Task InvalidIndirectTreesFailWithoutPublishing(string variant, string route)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(variant, string.Empty), QpdfInputOwnership.Copy);
        await AssertRejected(host, route);
    }

    [Theory]
    [InlineData("/CatalogLink 1 0 R")]
    [InlineData("/TreeLink 2 0 R")]
    public async Task CopierStillRejectsIndirectlyTypedStructuralNodes(string link)
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        {
            using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("all", link), QpdfInputOwnership.Copy);
            await AssertRejected(host, route);
        }
    }

    private sealed class BudgetHost(IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost, IDisposable
    {
        internal readonly InMemoryQpdfHost Inner = new();
        public IMemoryBudget? MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => Inner.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => Inner.CreateOutputAsync(path, token);
        public void Dispose() => Inner.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedRefusalsReleaseAdditionalIndirectObjects(bool rewrite)
    {
        int growths;
        RecordingMemoryBudget baseline = new(16777216L);
        using (IMemoryScope caller = baseline.CreateScope())
        using (BudgetHost host = new(caller))
        {
            host.Inner.AddInput(QpdfPath.From("input"), Fixture("all", string.Empty), QpdfInputOwnership.Copy);
            await Run(host, "typed", rewrite); growths = baseline.GrowthCalls;
        }
        Assert.Equal(0L, baseline.ReservedBytes);
        for (int refused = 1; refused <= growths; refused++)
        {
            RecordingMemoryBudget budget = new(16777216L) { RefuseOnGrowth = refused };
            using (IMemoryScope caller = budget.CreateScope())
            using (BudgetHost host = new(caller))
            {
                host.Inner.AddInput(QpdfPath.From("input"), Fixture("all", string.Empty), QpdfInputOwnership.Copy);
                await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => Run(host, "typed", rewrite));
                Assert.Equal(0, host.Inner.OpenLeaseCount);
            }
            Assert.Equal(0L, budget.ReservedBytes);
        }
    }
}
