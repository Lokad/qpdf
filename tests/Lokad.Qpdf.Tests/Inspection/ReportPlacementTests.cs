using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Content;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class ReportPlacementTests
{
    [Fact]
    public async Task JsonOverlayFollowsSelectionAndUsesTheAuthorizedPlacementInput()
    {
        using InMemoryQpdfHost host = new();
        byte[] source = TwoPages();
        host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("plain-stamp"), source, QpdfInputOwnership.Transfer);
        QpdfExecutionResult encrypted = await Qpdf.ExecuteAsync(host,
            new(["plain-stamp", "--encrypt", "reader", "owner", "256", "--", "encrypted"]), CancellationToken.None);
        AssertSuccess(encrypted);
        Assert.True(host.TryGetOutput(QpdfPath.From("encrypted"), out byte[] donor));
        host.AddInput(QpdfPath.From("stamp"), donor, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--pages", ".", "2,1", "--", "--overlay", "stamp", "--from=1", "--to=1",
                "--password=reader", "--", "--json-output", "--json-stream-data=inline"]), CancellationToken.None);
        await AssertRecreatedText(host, result, "SecondFirst", "First");
    }

    [Fact]
    public async Task JsonJobUnderlayPaintsBeforeThePrimaryContent()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), TwoPages(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), TwoPages(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input","jsonOutput":"","jsonStreamData":"inline","underlay":[{"file":"stamp","from":"2","to":"1"}]}""",
            CancellationToken.None);
        await AssertRecreatedText(host, result, "SecondFirst", "Second");
    }

    [Fact]
    public async Task PlacementInputRemainsProtectedFromNamedJsonPublication()
    {
        QpdfPath target = QpdfPath.From("stamp");
        using InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(TwoPages(), target);
        host.AddInput(target, TwoPages(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--overlay", target.Value, "--from=2", "--to=1", "--", "--json-output", target.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, item => item.Message.Contains("aliases an input", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(target, out byte[] prior));
        Assert.Equal(new byte[] { 17, 23, 41 }, prior);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task PlacementReportKeepsActiveLayersAndTheOriginalPrivateRegistration()
    {
        using InMemoryQpdfHost host = new();
        byte[] primary = OptionalContentTests.Fixture(true, "static", true);
        byte[] donor = OptionalContentTests.Fixture(true, "static", false);
        donor[7] = (byte)'7';
        host.AddInput(QpdfPath.From("input"), primary, QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), donor, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--overlay", "stamp", "--", "--json=2", "--json-key=qpdf", "--json-key=pages", "--json-stream-data=inline"]),
            CancellationToken.None);
        AssertSuccess(result);
        using JsonDocument report = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
        JsonElement envelope = report.RootElement.GetProperty("qpdf");
        Assert.Equal("1.7", envelope[0].GetProperty("pdfversion").GetString());
        JsonElement table = envelope[1];
        JsonElement catalog = Value(table.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        JsonElement properties = Value(catalog.GetProperty("/OCProperties"));
        string?[] groups = Value(properties.GetProperty("/OCGs")).EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(2, groups.Distinct().Count());
        JsonElement original = Value(catalog.GetProperty("/Audit"));
        string? originalGroup = Assert.Single(Value(original.GetProperty("/OCGs")).EnumerateArray()).GetString();
        Assert.Contains(originalGroup, groups);

        JsonElement page = Value(report.RootElement.GetProperty("pages")[0].GetProperty("object"));
        JsonElement resources = Value(page.GetProperty("/Resources"));
        var paintedGroups = Value(resources.GetProperty("/XObject")).EnumerateObject().Select(placement =>
        {
            JsonElement form = table.GetProperty("obj:" + placement.Value.GetString()).GetProperty("stream").GetProperty("dict");
            JsonElement formResources = Value(form.GetProperty("/Resources"));
            return Value(formResources.GetProperty("/Properties")).GetProperty("/Layer").GetString();
        });
        Assert.Equal(groups.Order(StringComparer.Ordinal), paintedGroups.Order(StringComparer.Ordinal));
        Assert.Equal(groups, Value(Value(properties.GetProperty("/D")).GetProperty("/OFF")).EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(0L, host.OpenLeaseCount);

        JsonElement Value(JsonElement raw) => raw.ValueKind == JsonValueKind.String
            ? table.GetProperty("obj:" + raw.GetString()).GetProperty("value") : raw;
    }

    [Fact]
    public async Task AuthoredReportPlacesAnAuthenticatedSourceOnTheBoundPage()
    {
        using InMemoryQpdfHost host = new();
        await AddAuthoredPagesAsync(host);
        host.AddInput(QpdfPath.From("plain-stamp"), TwoPages(), QpdfInputOwnership.Transfer);
        AssertSuccess(await Qpdf.ExecuteAsync(host, new(["plain-stamp", "--encrypt", "reader", "owner", "256", "--", "stamp"]), CancellationToken.None));
        Assert.True(host.TryGetOutput(QpdfPath.From("stamp"), out byte[] donor));
        host.AddInput(QpdfPath.From("stamp"), donor, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """
            {"inputFile":"graph","jsonInput":"","jsonOutput":"","jsonStreamData":"inline",
             "overlay":[{"file":"stamp","from":"2","to":"1","password":"reader"}]}
            """, CancellationToken.None);
        await AssertRecreatedText(host, result, "FirstSecond", "Second");
    }

    [Fact]
    public async Task PatchedPdfPlacesBeforeSplittingTheNewPageOrder()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), TwoPages(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), TwoPages(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("patch"), """
            {"qpdf":[{"jsonversion":2},{"obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["4 0 R","3 0 R"],"/Count":2}},
              "obj:6 0 R":{"stream":{"dict":{},"data":"QlQgL0YxIDI0IFRmIDEwMCA3MDAgVGQgKFBhdGNoZWRTZWNvbmQpIFRqIEVU"}}}]}
            """u8.ToArray(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--update-from-json=patch", "--underlay", "stamp", "--from=2", "--to=1", "--", "--split-pages", "part.pdf"]), CancellationToken.None);
        AssertSuccess(result);
        foreach ((string path, string text) in new[] { ("part-1.pdf", "SecondPatchedSecond"), ("part-2.pdf", "First") })
        {
            Assert.True(host.TryGetOutput(QpdfPath.From(path), out byte[] bytes));
            using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(bytes);
            Assert.Equal(1, document.NumberOfPages);
            Assert.Equal(text, document.GetPage(1).Text);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task BoundPlacementGenerationPaintsTheImportedWidgetBeforeFormRemoval()
    {
        using InMemoryQpdfHost host = new();
        await AddAuthoredPagesAsync(host);
        host.AddInput(QpdfPath.From("widget"), ReportAppearanceGenerationTests.Fixture(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["graph", "--json-input", "--overlay", "widget", "--to=1", "--", "--generate-appearances",
                "--flatten-annotations=all", "--remove-acroform", "--json=2", "--json-key=qpdf", "--json-key=pages",
                "--json-stream-data=inline"]), CancellationToken.None);
        AssertSuccess(result);
        using JsonDocument report = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        JsonElement catalog = Value(table.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        Assert.False(catalog.TryGetProperty("/AcroForm", out _));
        JsonElement page = Value(report.RootElement.GetProperty("pages")[0].GetProperty("object"));
        Assert.False(page.TryGetProperty("/Annots", out _));
        JsonElement forms = Value(Value(page.GetProperty("/Resources")).GetProperty("/XObject"));
        JsonProperty painted = Assert.Single(forms.EnumerateObject(), form => Program(form.Value).Contains("(FRESH) Tj", StringComparison.Ordinal));
        Assert.Contains(Value(page.GetProperty("/Contents")).EnumerateArray(),
            stream => Program(stream).Contains(painted.Name + " Do", StringComparison.Ordinal));
        Assert.Equal(0L, host.OpenLeaseCount);

        string Program(JsonElement reference) => Encoding.ASCII.GetString(table.GetProperty("obj:" + reference.GetString())
            .GetProperty("stream").GetProperty("data").GetBytesFromBase64());
        JsonElement Value(JsonElement raw) => raw.ValueKind == JsonValueKind.String
            ? table.GetProperty("obj:" + raw.GetString()).GetProperty("value") : raw;
    }

    [Fact]
    public async Task BoundPlacementInputRemainsProtectedFromNamedJsonPublication()
    {
        QpdfPath target = QpdfPath.From("stamp");
        using InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(TwoPages(), target);
        host.AddInput(target, TwoPages(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("patch"), """{"qpdf":[{"jsonversion":2},{}]}"""u8.ToArray(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--update-from-json=patch", "--overlay", target.Value, "--to=1", "--", "--json-output", target.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, item => item.Message.Contains("aliases an input", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(target, out byte[] prior));
        Assert.Equal(new byte[] { 17, 23, 41 }, prior);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static async Task AddAuthoredPagesAsync(InMemoryQpdfHost host)
    {
        host.AddInput(QpdfPath.From("base"), TwoPages(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult report = await Qpdf.ExecuteAsync(host, new(["base", "--json-output", "--json-stream-data=inline"]), CancellationToken.None);
        AssertSuccess(report);
        host.AddInput(QpdfPath.From("graph"), Encoding.UTF8.GetBytes(Assert.IsType<string>(report.OutputText)), QpdfInputOwnership.Transfer);
    }

    private static byte[] TwoPages() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf"));

    private static async Task AssertRecreatedText(InMemoryQpdfHost host, QpdfExecutionResult result, string first, string second)
    {
        AssertSuccess(result);
        QpdfPath json = QpdfPath.From("assembled"), pdf = QpdfPath.From("recreated");
        host.AddInput(json, Encoding.UTF8.GetBytes(Assert.IsType<string>(result.OutputText)), QpdfInputOwnership.Transfer);
        QpdfJsonImportResult recreated = await Qpdf.CreatePdfFromJsonAsync(host, json, pdf, CancellationToken.None);
        Assert.Equal(2, recreated.PageCount);
        Assert.True(host.TryGetOutput(pdf, out byte[] bytes));
        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.Equal(first, document.GetPage(1).Text);
        Assert.Equal(second, document.GetPage(2).Text);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static void AssertSuccess(QpdfExecutionResult result)
        => Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("\n", result.Diagnostics.Select(item => item.Message)));
}
