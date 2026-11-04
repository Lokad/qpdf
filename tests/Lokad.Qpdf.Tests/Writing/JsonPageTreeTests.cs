using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonPageTreeTests
{
    internal static byte[] Fixture(int depth, string layout, bool directRoot)
    {
        int page = depth + 2;
        int pageType = page + 1, pagesType = page + 2, catalogType = page + 3, kidsArray = page + 4, countValue = page + 5;
        bool indirect = layout == "indirect";
        Dictionary<string, object> catalog = new() { ["/Type"] = indirect ? catalogType + " 0 R" : "/Catalog", ["/Pages"] = "2 0 R" };
        Dictionary<string, object> objects = new() { ["obj:1 0 R"] = new { value = catalog } };
        int expected = layout == "empty" ? 0 : layout is "duplicate" or "shared" ? 2 : 1;
        for (int index = 0; index < depth; index++)
        {
            int number = index + 2;
            string[] children = layout == "empty" ? [] : layout == "cycle" ? ["2 0 R"]
                : layout == "missing-child" ? ["999 0 R"]
                : index == 0 && layout is "duplicate" or "shared" ? [(number + 1) + " 0 R", (number + 1) + " 0 R"]
                : [(number + 1) + " 0 R"];
            Dictionary<string, object> node = new() { ["/Type"] = indirect ? pagesType + " 0 R" : "/Pages",
                ["/Kids"] = layout == "bad-kids" ? (object)17 : indirect && index == 0 ? kidsArray + " 0 R" : children,
                ["/Count"] = layout == "count-mismatch" ? 0 : index == 0 ? indirect ? countValue + " 0 R" : expected : 1 };
            if (index > 0) { node["/Parent"] = (number - 1) + " 0 R"; }
            objects["obj:" + number + " 0 R"] = new { value = node };
        }
        objects["obj:" + page + " 0 R"] = new { value = new Dictionary<string, object> {
            ["/Type"] = indirect || layout == "indirect-type" ? pageType + " 0 R" : layout == "bad-type" ? "/Other" : "/Page",
            ["/Parent"] = (page - 1) + " 0 R", ["/MediaBox"] = new[] { 0, 0, 10, 10 }, ["/Resources"] = new Dictionary<string, object>() } };
        objects["obj:" + pageType + " 0 R"] = new { value = "/Page" };
        objects["obj:" + pagesType + " 0 R"] = new { value = "/Pages" };
        objects["obj:" + catalogType + " 0 R"] = new { value = "/Catalog" };
        objects["obj:" + kidsArray + " 0 R"] = new { value = new[] { "3 0 R" } };
        objects["obj:" + countValue + " 0 R"] = new { value = expected };
        objects["trailer"] = new { value = new Dictionary<string, object> { ["/Root"] = directRoot ? catalog : "1 0 R", ["/Size"] = countValue + 1 } };
        return JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2, pdfversion = "1.4" }, objects } });
    }

    internal static void Register(InMemoryQpdfHost host, byte[] json)
    {
        host.AddInput(QpdfPath.From("json"), json, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("input"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null)], 1), QpdfInputOwnership.Copy);
    }

    internal static async Task Seed(InMemoryQpdfHost host)
    {
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }

    internal static async Task<QpdfJsonImportResult> Typed(IQpdfHost host, bool update, QpdfLimits limits, CancellationToken token)
        => update ? await Qpdf.UpdatePdfFromJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("json"), QpdfPath.From("output"), limits, token)
            : await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("json"), QpdfPath.From("output"), limits, token);

    internal static async Task<QpdfExecutionResult> Execute(IQpdfHost host, bool update, string route, QpdfLimits limits, CancellationToken token)
        => route == "argv" ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(update
            ? ["input", "--update-from-json=json", "--", "output"] : ["json", "--json-input", "--", "output"]), new QpdfExecutionOptions(limits), token)
            : await Qpdf.ExecuteJobAsync(host, update
                ? "{\"inputFile\":\"input\",\"updateFromJson\":\"json\",\"outputFile\":\"output\"}"
                : "{\"inputFile\":\"json\",\"jsonInput\":\"\",\"outputFile\":\"output\"}", new QpdfExecutionOptions(limits), token);

    public static IEnumerable<object[]> ValidCases()
    {
        foreach (string layout in new[] { "direct", "indirect-type", "indirect", "empty", "duplicate", "shared" })
        foreach (bool update in new[] { false, true })
        foreach (string route in new[] { "typed", "argv", "job" })
        { yield return [layout, false, update, route]; }
        foreach (string layout in new[] { "direct", "indirect" })
        foreach (bool update in new[] { false, true })
        foreach (string route in new[] { "typed", "argv", "job" })
        { yield return [layout, true, update, route]; }
    }

    [Theory]
    [MemberData(nameof(ValidCases))]
    public async Task CountsMatchReaderForEquivalentAndSharedPageTrees(string layout, bool directRoot, bool update, string route)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = 2 };
        int expected = layout == "empty" ? 0 : layout is "duplicate" or "shared" ? 2 : 1;
        using InMemoryQpdfHost host = new(); Register(host, Fixture(layout == "shared" ? 2 : 1, layout, directRoot)); await Seed(host);
        if (route == "typed")
        {
            QpdfJsonImportResult result = await Typed(host, update, limits, CancellationToken.None);
            Assert.Equal(expected, result.PageCount); Assert.Empty(result.Warnings); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        else
        {
            QpdfExecutionResult result = await Execute(host, update, route, limits, CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0, host.OpenLeaseCount); Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("result"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument reader = await QpdfDocument.OpenAsync(host, QpdfPath.From("result"), limits, CancellationToken.None);
        Assert.Equal(expected, reader.PageCount);
        if (expected == 2) { Assert.Same(reader.PageReferences[0].Page, reader.PageReferences[1].Page); }
    }

    public static IEnumerable<object[]> InvalidCases()
    {
        foreach (string layout in new[] { "cycle", "missing-child", "bad-kids", "bad-type", "depth", "default-depth" })
        foreach (bool update in new[] { false, true })
        foreach (string route in new[] { "typed", "argv", "job" })
        { yield return [layout, update, route]; }
    }

    internal static void AssertPrior(InMemoryQpdfHost host)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public async Task InvalidPageTreesFailBeforePublication(string layout, bool update, string route)
    {
        bool deep = layout is "depth" or "default-depth";
        QpdfLimits limits = layout == "default-depth" ? QpdfLimits.Default : QpdfLimits.Default with { MaxNestingDepth = 2 };
        int depth = deep ? checked((int)limits.MaxNestingDepth + 1) : 1;
        using InMemoryQpdfHost host = new(); Register(host, Fixture(depth, deep ? "direct" : layout, false)); await Seed(host);
        if (route == "typed")
        {
            if (deep) { await Assert.ThrowsAsync<QpdfLimitException>(() => Typed(host, update, limits, CancellationToken.None)); }
            else { await Assert.ThrowsAsync<QpdfParseException>(() => Typed(host, update, limits, CancellationToken.None)); }
        }
        else
        {
            QpdfExecutionResult result = await Execute(host, update, route, limits, CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.NotEmpty(result.Diagnostics); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        AssertPrior(host);
    }

    [Theory]
    [InlineData(false, "typed")]
    [InlineData(true, "typed")]
    [InlineData(false, "argv")]
    [InlineData(true, "argv")]
    [InlineData(false, "job")]
    [InlineData(true, "job")]
    public async Task DeclaredCountMismatchUsesReaderWarningPolicy(bool update, string route)
    {
        using InMemoryQpdfHost host = new(); Register(host, Fixture(1, "count-mismatch", false));
        if (route == "typed")
        {
            QpdfJsonImportResult result = await Typed(host, update, QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(1, result.PageCount); Assert.Contains(result.Warnings, item => item.Code == QpdfDiagnosticCode.PageCountMismatch);
        }
        else
        {
            QpdfExecutionResult result = await Execute(host, update, route, QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(QpdfExitCode.Warning, result.ExitCode); Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.PageCountMismatch);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out _)); Assert.Equal(0, host.OpenLeaseCount);
    }
}
