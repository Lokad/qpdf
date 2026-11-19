using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class BoundPageSelectionTests
{
    [Fact]
    public async Task CreationCollatesRepeatedRangesFromTheAuthoredTree()
    {
        using InMemoryQpdfHost host = new();
        await AddAuthoredGraphAsync(host, TwoPages());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["graph", "--json-input", "--pages", ".", "2,2", ".", "1,1", "--", "--collate=1", "output"]), CancellationToken.None);
        AssertSuccess(result);
        AssertText(host, "output", ["Second", "First", "Second", "First"]);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task UpdateSelectsThePageInThePatchedOrderBeforePlacement()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), TwoPages(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), TwoPages(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("patch"), """
            {"qpdf":[{"jsonversion":2},{"obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["4 0 R","3 0 R"],"/Count":2}},
              "obj:6 0 R":{"stream":{"dict":{},"data":"QlQgL0YxIDI0IFRmIDEwMCA3MDAgVGQgKFBhdGNoZWRTZWNvbmQpIFRqIEVU"}}}]}
            """u8.ToArray(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """
            {"inputFile":"input","updateFromJson":"patch","pages":[{"file":".","range":"1"}],
             "overlay":[{"file":"stamp","from":"2","to":"1"}],"outputFile":"output"}
            """, CancellationToken.None);
        AssertSuccess(result);
        AssertText(host, "output", ["PatchedSecondSecond"]);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task BoundSelectionReportRecreatesAnAuthenticatedSecondaryPage()
    {
        using InMemoryQpdfHost host = new();
        await AddAuthoredGraphAsync(host, TwoPages());
        AssertSuccess(await Qpdf.ExecuteAsync(host,
            new(["input", "--encrypt", "reader", "owner", "256", "--", "donor"]), CancellationToken.None));
        Assert.True(host.TryGetOutput(QpdfPath.From("donor"), out byte[] donor));
        host.AddInput(QpdfPath.From("donor"), donor, QpdfInputOwnership.Transfer);
        QpdfExecutionResult report = await Qpdf.ExecuteAsync(host, new(["graph", "--json-input", "--pages", ".", "1",
            "donor", "--password=reader", "2", "--", "--json-output", "--json-stream-data=inline"]), CancellationToken.None);
        AssertSuccess(report);
        host.AddInput(QpdfPath.From("report"), Encoding.UTF8.GetBytes(Assert.IsType<string>(report.OutputText)), QpdfInputOwnership.Transfer);
        AssertSuccess(await Qpdf.ExecuteAsync(host, new(["report", "--json-input", "output"]), CancellationToken.None));
        AssertText(host, "output", ["First", "Second"]);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task BoundReportRepeatedWidgetsGetSeparateActiveRegistrations()
    {
        using InMemoryQpdfHost host = new();
        await AddAuthoredGraphAsync(host, ReportAppearanceGenerationTests.Fixture());
        QpdfExecutionResult report = await Qpdf.ExecuteAsync(host,
            new(["graph", "--json-input", "--pages", ".", "1,1", "--", "--json=2", "--json-key=pages",
                "--json-key=qpdf", "--json-stream-data=inline"]), CancellationToken.None);
        AssertSuccess(report);
        using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(report.OutputText));
        JsonElement table = json.RootElement.GetProperty("qpdf")[1];
        JsonElement catalog = Value(table.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        var widgets = json.RootElement.GetProperty("pages").EnumerateArray().Select(descriptor =>
        {
            JsonElement pageRef = descriptor.GetProperty("object");
            JsonElement page = Value(pageRef);
            JsonElement widget = Assert.Single(Value(page.GetProperty("/Annots")).EnumerateArray());
            Assert.Equal(pageRef.GetString(), Value(widget).GetProperty("/P").GetString());
            return widget.GetString();
        }).ToArray();
        Assert.Equal(2, widgets.Distinct().Count());
        JsonElement registrations = Value(Value(catalog.GetProperty("/AcroForm")).GetProperty("/Fields"));
        Assert.Equal(widgets.Order(StringComparer.Ordinal), registrations.EnumerateArray().Select(item => item.GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(0L, host.OpenLeaseCount);

        JsonElement Value(JsonElement raw) => raw.ValueKind == JsonValueKind.String
            ? table.GetProperty("obj:" + raw.GetString()).GetProperty("value") : raw;
    }

    [Fact]
    public async Task BoundReportKeepsTheDeclaredPagePairAndMovesItsLabel()
    {
        using InMemoryQpdfHost host = new();
        await AddAuthoredGraphAsync(host, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "labels-2page.pdf")));
        QpdfExecutionResult report = await Qpdf.ExecuteAsync(host,
            new(["graph", "--json-input", "--pages", ".", "2", "--", "--json=2", "--json-key=pages", "--json-key=pagelabels"]),
            CancellationToken.None);
        AssertSuccess(report);
        using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(report.OutputText));
        JsonElement page = Assert.Single(json.RootElement.GetProperty("pages").EnumerateArray());
        Assert.Equal("4 0 R", page.GetProperty("object").GetString());
        JsonElement label = Assert.Single(json.RootElement.GetProperty("pagelabels").EnumerateArray());
        Assert.Equal(0, label.GetProperty("index").GetInt32());
        Assert.Equal("/R", label.GetProperty("label").GetProperty("/S").GetString());
        Assert.Equal(3, label.GetProperty("label").GetProperty("/St").GetInt32());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task BoundSelectionCoalescesAndPrunesOnlyTheActivePageRoles()
    {
        using InMemoryQpdfHost host = new();
        byte[] text = "BT /F1 12 Tf 5 50 Td (Kept) Tj ET"u8.ToArray();
        await AddAuthoredGraphAsync(host, PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 8 0 R /AuditResources 8 0 R /Contents [5 0 R 6 0 R] >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (5, $"<< /Length {text.Length} >>", text),
            (6, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (8, "<< /Font << /F1 7 0 R /F2 7 0 R >> >>", null)], 1));
        QpdfExecutionResult report = await Qpdf.ExecuteAsync(host, new(["graph", "--json-input", "--pages", ".", "1", "--",
            "--coalesce-contents", "--remove-unreferenced-resources=yes", "--json=2", "--json-key=pages",
            "--json-key=qpdf", "--json-stream-data=inline"]), CancellationToken.None);
        AssertSuccess(report);
        using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(report.OutputText));
        JsonElement table = json.RootElement.GetProperty("qpdf")[1];
        JsonElement descriptor = Assert.Single(json.RootElement.GetProperty("pages").EnumerateArray());
        JsonElement page = Value(descriptor.GetProperty("object"));
        JsonElement active = Value(Value(page.GetProperty("/Resources")).GetProperty("/Font"));
        Assert.Equal("/F1", Assert.Single(active.EnumerateObject()).Name);
        Assert.True(Value(Value(page.GetProperty("/AuditResources")).GetProperty("/Font")).TryGetProperty("/F2", out _));
        string? stream = Assert.Single(descriptor.GetProperty("contents").EnumerateArray()).GetString();
        string program = Encoding.ASCII.GetString(table.GetProperty("obj:" + stream).GetProperty("stream").GetProperty("data").GetBytesFromBase64());
        Assert.Contains("(Kept) Tj", program, StringComparison.Ordinal);
        Assert.Contains("q Q", program, StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);

        JsonElement Value(JsonElement raw) => raw.ValueKind == JsonValueKind.String
            ? table.GetProperty("obj:" + raw.GetString()).GetProperty("value") : raw;
    }

    [Fact]
    public async Task OutOfRangeBoundSelectionPreservesTheNamedDestination()
    {
        QpdfPath target = QpdfPath.From("output");
        using InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(TwoPages(), target);
        host.AddInput(QpdfPath.From("patch"), """{"qpdf":[{"jsonversion":2},{}]}"""u8.ToArray(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--update-from-json=patch", "--pages", ".", "3", "--", target.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.InvalidArguments
            && item.Message.Contains("Invalid page range", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(target, out byte[] prior));
        Assert.Equal(new byte[] { 17, 23, 41 }, prior);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public Task BoundPagePreparationKeepsThePriorTargetOnAdmissionAndCommitFailures()
    {
        QpdfPath target = QpdfPath.From("output");
        return PreparationAdmissionChecks.VerifyAsync(Prepare, (host, token) => Qpdf.ExecuteAsync(host,
            new(["input", "--update-from-json=patch", "--pages", ".", "2,1", "--", target.Value]), token),
            target, 1800, ["write", "commit", "cancel"]);

        async Task<InMemoryQpdfHost> Prepare()
        {
            InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(TwoPages(), target);
            host.AddInput(QpdfPath.From("patch"), """{"qpdf":[{"jsonversion":2},{}]}"""u8.ToArray(), QpdfInputOwnership.Transfer);
            return host;
        }
    }

    [Fact]
    public async Task BoundSelectionProtectsTheNamedSecondaryFromPublication()
    {
        QpdfPath target = QpdfPath.From("donor");
        using InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(TwoPages(), target);
        host.AddInput(target, TwoPages(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("patch"), """{"qpdf":[{"jsonversion":2},{}]}"""u8.ToArray(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--update-from-json=patch", "--pages", target.Value, "1", "--", target.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, item => item.Message.Contains("aliases an input", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(target, out byte[] prior));
        Assert.Equal(new byte[] { 17, 23, 41 }, prior);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task BoundSelectionReportsAnUnresolvedSecondaryResourceOnce()
    {
        using InMemoryQpdfHost host = new();
        await AddAuthoredGraphAsync(host, TwoPages());
        byte[] content = "/Missing Do\n"u8.ToArray();
        host.AddInput(QpdfPath.From("donor"), PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>", null),
            (4, $"<< /Length {content.Length} >>", content)], 1), QpdfInputOwnership.Transfer);
        QpdfExecutionResult report = await Qpdf.ExecuteAsync(host, new(["graph", "--json-input",
            "--pages", "donor", "1", "--", "--json-output", "--json-stream-data=inline"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, report.ExitCode);
        QpdfDiagnostic warning = Assert.Single(report.Diagnostics, item => item.Code == QpdfDiagnosticCode.PageStructureWarning);
        Assert.Contains("cannot be resolved", warning.Message, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(report.OutputText));
        JsonElement table = json.RootElement.GetProperty("qpdf")[1];
        Assert.Contains(table.EnumerateObject(), item => item.Value.TryGetProperty("stream", out JsonElement stream)
            && stream.GetProperty("data").GetBytesFromBase64().SequenceEqual(content));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static async Task AddAuthoredGraphAsync(InMemoryQpdfHost host, byte[] source)
    {
        host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        QpdfExecutionResult report = await Qpdf.ExecuteAsync(host, new(["input", "--json-output", "--json-stream-data=inline"]), CancellationToken.None);
        AssertSuccess(report);
        host.AddInput(QpdfPath.From("graph"), Encoding.UTF8.GetBytes(Assert.IsType<string>(report.OutputText)), QpdfInputOwnership.Transfer);
    }

    private static byte[] TwoPages() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf"));

    private static void AssertText(InMemoryQpdfHost host, string path, string[] expected)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From(path), out byte[] bytes));
        using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.Equal(expected, document.GetPages().Select(page => page.Text));
    }

    private static void AssertSuccess(QpdfExecutionResult result)
        => Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("\n", result.Diagnostics.Select(item => item.Message)));
}
