using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class OutlineNamedDestinationTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Report = QpdfPath.From("report");

    public static IEnumerable<object[]> NamedForms()
    {
        foreach (string kind in new[] { "name", "string" })
        foreach (bool action in new[] { false, true })
        foreach (bool wrapper in new[] { false, true }) { yield return [kind, action, wrapper]; }
    }

    [Theory]
    [MemberData(nameof(NamedForms))]
    public async Task NamesTreesAndWrappersProjectTheResolvedDestination(string kind, bool action, bool wrapper)
    {
        using InMemoryQpdfHost host = Storage(Fixture(kind, action, wrapper, 3, 4, false));
        using JsonDocument report = await Inspect(host, "typed", 2);
        AssertDestination(report, true);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("string")]
    public async Task MissingNamesProjectNullWithoutInventingAPage(string kind)
    {
        using InMemoryQpdfHost host = Storage(Fixture(kind, false, false, 3, 4, true));
        using JsonDocument report = await Inspect(host, "typed", 2);
        JsonElement item = Assert.Single(report.RootElement.GetProperty("outlines").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("dest").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("destpageposfrom1").ValueKind);
        Assert.Empty(report.RootElement.GetProperty("pages")[0].GetProperty("outlines").EnumerateArray());
    }

    [Theory]
    [InlineData("argv", 1, "name", false)]
    [InlineData("job", 2, "utf16", true)]
    [InlineData("typed", 1, "utf16", false)]
    public async Task PublicRoutesAndJsonVersionsKeepNamedPageProjection(string route, int version, string kind, bool action)
    {
        using InMemoryQpdfHost host = Storage(Fixture(kind, action, true, 3, 4, false));
        using JsonDocument report = await Inspect(host, route, version);
        AssertDestination(report, true);
    }

    [Theory]
    [InlineData("name", 3, 4, true)]
    [InlineData("string", 3, 4, true)]
    [InlineData("name", 3, 0, false)]
    [InlineData("string", 3, 0, false)]
    [InlineData("name", 99, 0, false)]
    [InlineData("string", 99, 0, false)]
    public async Task NamedTargetsKeepFullPageReferenceValidation(string kind, int number, int generation, bool live)
    {
        using InMemoryQpdfHost host = Storage(Fixture(kind, true, false, number, generation, false));
        using JsonDocument report = await Inspect(host, "typed", 2);
        AssertDestination(report, live);
    }

    [Fact]
    public async Task PagePositionsFollowTheRewrittenNamedTarget()
    {
        using InMemoryQpdfHost host = Storage(Fixture("reordered", true, true, 3, 4, false));
        QpdfPath selected = QpdfPath.From("selected");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "2,1", "--", "selected"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(selected, out byte[] bytes)); host.AddInput(QpdfPath.From("rewritten"), bytes, QpdfInputOwnership.Transfer);
        using JsonDocument report = JsonDocument.Parse((await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("rewritten"), new(2, ["outlines", "pages"]), CancellationToken.None)).Json);
        JsonElement pages = report.RootElement.GetProperty("pages"), entry = Assert.Single(report.RootElement.GetProperty("outlines").EnumerateArray());
        Assert.Equal(2, entry.GetProperty("destpageposfrom1").GetInt32());
        Assert.Equal(pages[1].GetProperty("object").GetString(), entry.GetProperty("dest")[0].GetString());
        Assert.Empty(pages[0].GetProperty("outlines").EnumerateArray()); Assert.Single(pages[1].GetProperty("outlines").EnumerateArray());
    }

    [Fact]
    public async Task NamedProjectionDoesNotMutateTheSourceDestinationArray()
    {
        using InMemoryQpdfHost host = Storage(Fixture("string", false, true, 3, 0, false));
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None);
        PdfArray original = Assert.IsType<PdfArray>(document.GetObject(6));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (PreparedJsonOutput output = PreparedJsonOutput.Create(governor))
        using (DisplayBuffer text = DocumentJson.WriteDocument(document, new(2, ["outlines", "pages"], "none", "none", null, null, false), output,
            QpdfLimits.Default, governor, new(governor), CancellationToken.None))
        using (GovernedText rendered = text.Finish())
        using (JsonDocument report = JsonDocument.Parse(rendered.Value)) { AssertDestination(report, false); }
        Assert.Same(original, document.GetObject(6)); Assert.Equal(new PdfReference(3, 0), original[0]);
    }

    [Fact]
    public Task NamedProjectionRefundsAdmissionAndPreservesThePriorReport()
        => PreparationAdmissionChecks.VerifyAsync(() => PreparationAdmissionChecks.StorageAsync(Fixture("utf16", true, true, 3, 4, false), Report),
            (host, token) => Qpdf.ExecuteAsync(host, new(["input", "--json=2", "--json-key=outlines", "--json-key=pages", "--", "report"]), token),
            Report, 128, ["write", "cancel"]);

    [Fact]
    public async Task CyclicNamedTreesFailBeforeReplacingTheReport()
    {
        byte[] bytes = Fixture("cycle", false, false, 3, 4, false);
        using InMemoryQpdfHost host = Storage(bytes);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Report, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfParseException error = await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.GetDocumentJsonAsync(host, Input, Report,
            new(2, ["outlines", "pages"]), CancellationToken.None).AsTask());
        Assert.Contains("cycle", error.Message, StringComparison.Ordinal); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(Report, out byte[] priorBytes)); Assert.Equal("prior"u8.ToArray(), priorBytes);
    }

    private static void AssertDestination(JsonDocument report, bool live)
    {
        JsonElement item = Assert.Single(report.RootElement.GetProperty("outlines").EnumerateArray());
        JsonElement destination = item.GetProperty("dest"); Assert.Equal(JsonValueKind.Array, destination.ValueKind);
        Assert.Equal("/Fit", destination[1].GetString());
        if (live)
        {
            Assert.Equal("3 4 R", destination[0].GetString()); Assert.Equal(1, item.GetProperty("destpageposfrom1").GetInt32());
            JsonElement pageItem = Assert.Single(report.RootElement.GetProperty("pages")[0].GetProperty("outlines").EnumerateArray());
            Assert.True(JsonElement.DeepEquals(destination, pageItem.GetProperty("dest"))); Assert.Equal("Chapter", pageItem.GetProperty("title").GetString());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, destination[0].ValueKind); Assert.Equal(JsonValueKind.Null, item.GetProperty("destpageposfrom1").ValueKind);
            Assert.Empty(report.RootElement.GetProperty("pages")[0].GetProperty("outlines").EnumerateArray());
        }
    }

    internal static byte[] Fixture(string kind, bool action, bool wrapper, int number, int generation, bool missing)
    {
        string text = missing ? "Missing" : "Chapter";
        string Token(string value) => kind == "name" ? "/" + value
            : kind == "utf16" ? "<feff" + string.Concat(value.Select(c => ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture))) + ">" : "(" + value + ")";
        string valueReference = wrapper ? "7 0 R" : "6 0 R";
        string context = kind == "name" ? "/Dests 12 0 R" : "/Names << /Dests 9 0 R >>";
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R " + context + " /Audit 6 0 R >>", null),
            (2, "<< /Type /Pages /Count " + (kind == "reordered" ? "2" : "1") + " /Kids [3 4 R" + (kind == "reordered" ? " 13 0 R" : "") + "] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> >>", null),
            (4, "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>", null),
            (5, "<< /Title (Chapter) /Parent 4 0 R " + (action ? "/A << /S /GoTo /D 10 0 R >>" : "/Dest 10 0 R") + " >>", null),
            (6, "[" + number + " " + generation + " R /Fit]", null), (7, "<< /D 6 0 R >>", null),
            (8, "<< /Names [" + Token("Chapter") + " " + valueReference + "] /Limits [" + Token("Chapter") + " " + Token("Chapter") + "] >>", null),
            (9, kind == "cycle" ? "<< /Kids [9 0 R] >>" : "<< /Kids [8 0 R] /Limits [" + Token("Chapter") + " " + Token("Chapter") + "] >>", null),
            (10, Token(text), null), (11, "null", null), (12, "<< /Chapter " + valueReference + " >>", null)];
        if (kind == "reordered")
        { objects.Add((13, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> >>", null)); }
        return PdfTestDocument.Assemble(objects, 1, new Dictionary<int, int> { [3] = 4 });
    }

    private static InMemoryQpdfHost Storage(byte[] bytes)
    { InMemoryQpdfHost host = new(); host.AddInput(Input, bytes, QpdfInputOwnership.Transfer); return host; }

    private static async Task<JsonDocument> Inspect(InMemoryQpdfHost host, string route, int version)
    {
        string text;
        if (route == "typed") { text = (await Qpdf.GetDocumentJsonAsync(host, Input, new(version, ["outlines", "pages"]), CancellationToken.None)).Json; }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--json=" + version, "--json-key=outlines", "--json-key=pages"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", json = version.ToString(System.Globalization.CultureInfo.InvariantCulture), jsonKey = new[] { "outlines", "pages" } }), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            text = result.OutputText ?? throw new InvalidOperationException("Missing JSON report.");
        }
        Assert.Equal(0L, host.OpenLeaseCount); return JsonDocument.Parse(text);
    }
}
