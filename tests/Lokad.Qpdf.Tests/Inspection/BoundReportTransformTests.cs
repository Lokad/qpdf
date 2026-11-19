using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Content;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class BoundReportTransformTests
{
    [Fact]
    public async Task PatchedFieldValueDrivesTheFullReportAndKeepsThePrivateForm()
    {
        using InMemoryQpdfHost host = new();
        JsonArray envelope = await SourceGraph(host, ReportAppearanceGenerationTests.Fixture());
        JsonObject objects = Assert.IsType<JsonObject>(envelope[1]);
        ObjectValue(objects, 1)["/FormAudit"] = "4 0 R";
        ObjectValue(objects, 4)["/SigFlags"] = 1;
        ObjectValue(objects, 5)["/V"] = "u:PATCHED";
        JsonObject patch = new();
        foreach (string name in new[] { "obj:1 0 R", "obj:4 0 R", "obj:5 0 R" })
        { patch[name] = Assert.IsType<JsonObject>(objects[name]).DeepClone(); }
        Save(host, "patch", new JsonArray(new JsonObject { ["jsonversion"] = 2 }, patch));

        QpdfExecutionResult report = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input","updateFromJson":"patch","json":"2","jsonKey":["qpdf","acroform"],"jsonStreamData":"inline","generateAppearances":""}""",
            CancellationToken.None);
        using JsonDocument json = Parse(report);
        JsonElement table = json.RootElement.GetProperty("qpdf")[1];
        JsonElement catalog = Catalog(table);
        JsonElement form = Value(table, catalog.GetProperty("/AcroForm"));
        Assert.False(form.TryGetProperty("/NeedAppearances", out _));
        Assert.Equal(1, form.GetProperty("/SigFlags").GetInt32());
        Assert.True(Value(table, catalog.GetProperty("/FormAudit")).GetProperty("/NeedAppearances").GetBoolean());
        JsonElement widget = Value(table, form.GetProperty("/Fields")[0]);
        Assert.Contains("(PATCHED) Tj", Program(table, widget.GetProperty("/AP").GetProperty("/N")), StringComparison.Ordinal);
        Assert.Contains("(STALE) Tj", Program(table, catalog.GetProperty("/Audit")), StringComparison.Ordinal);
        Assert.False(json.RootElement.GetProperty("acroform").GetProperty("needappearances").GetBoolean());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task AuthoredGraphPaintsTheFreshProgramWithInheritedResources()
    {
        using InMemoryQpdfHost host = new();
        JsonArray envelope = await SourceGraph(host, ReportAppearanceGenerationTests.Fixture());
        JsonObject objects = Assert.IsType<JsonObject>(envelope[1]);
        ObjectValue(objects, 2)["/Resources"] = new JsonObject
            { ["/XObject"] = new JsonObject { ["/Inherit"] = "7 0 R" } };
        ObjectValue(objects, 3).Remove("/Resources");
        Save(host, "source", envelope);

        QpdfExecutionResult report = await Qpdf.ExecuteAsync(host,
            new(["source", "--json-input", "--generate-appearances", "--flatten-annotations=all", "--json-output", "--json-stream-data=inline"]),
            CancellationToken.None);
        using JsonDocument json = Parse(report);
        JsonElement table = json.RootElement.GetProperty("qpdf")[1];
        JsonElement catalog = Catalog(table);
        Assert.False(catalog.TryGetProperty("/AcroForm", out _));
        JsonElement kids = Value(table, catalog.GetProperty("/Pages")).GetProperty("/Kids");
        JsonElement page = Value(table, kids[0]);
        Assert.False(page.TryGetProperty("/Annots", out _));
        JsonElement resources = Value(table, page.GetProperty("/Resources"));
        JsonElement forms = Value(table, resources.GetProperty("/XObject"));
        Assert.Contains("(STALE) Tj", Program(table, forms.GetProperty("/Inherit")), StringComparison.Ordinal);
        JsonProperty painted = Assert.Single(forms.EnumerateObject(), item => item.Name != "/Inherit");
        Assert.Contains("(FRESH) Tj", Program(table, painted.Value), StringComparison.Ordinal);
        Assert.Contains(page.GetProperty("/Contents").EnumerateArray(), item =>
            Program(table, item).Contains(painted.Name + " Do", StringComparison.Ordinal));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task BoundBakingRecreatesTheInheritedFrameAndMovesOnlyActiveDestinations()
    {
        using InMemoryQpdfHost host = new();
        JsonArray envelope = await SourceGraph(host, RotationDestinationTests.Source("XYZ", 90, true));
        JsonObject objects = Assert.IsType<JsonObject>(envelope[1]);
        ObjectValue(objects, 3).Remove("/Rotate");
        ObjectValue(objects, 2)["/Rotate"] = 90;
        Save(host, "source", envelope);
        QpdfExecutionResult report = await Qpdf.ExecuteAsync(host,
            new(["source", "--json-input", "--flatten-rotation", "--json-output", "--json-stream-data=inline"]), CancellationToken.None);
        using JsonDocument json = Parse(report);
        JsonElement metadata = json.RootElement.GetProperty("qpdf")[0];
        Assert.True(metadata.GetProperty("calledgetallpages").GetBoolean());
        Assert.True(metadata.GetProperty("pushedinheritedpageresources").GetBoolean());
        QpdfPath prepared = QpdfPath.From("prepared"), recreated = QpdfPath.From("recreated");
        host.AddInput(prepared, Encoding.UTF8.GetBytes(report.OutputText ?? throw new InvalidOperationException()), QpdfInputOwnership.Transfer);
        Assert.Equal(1, (await Qpdf.CreatePdfFromJsonAsync(host, prepared, recreated, CancellationToken.None)).PageCount);
        Assert.True(host.TryGetOutput(recreated, out byte[] bytes));
        host.AddInput(recreated, bytes, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, recreated, CancellationToken.None))
        {
            PdfDictionary catalog = RotationDestinationTests.Dict(document, document.Engine.Trailer, "Root");
            PdfDictionary page = Assert.Single(document.PageReferences).Page;
            PdfDictionary parent = RotationDestinationTests.Dict(document, page, "Parent");
            Assert.False(page.TryGet(PdfName.FromAscii("Rotate"), out _));
            Assert.False(parent.TryGet(PdfName.FromAscii("Rotate"), out _));
            RotationDestinationTests.AssertDestination(document, RotationDestinationTests.Value(document, catalog, "OpenAction"),
                "XYZ", RotationDestinationTests.Expected("XYZ", 90));
            RotationDestinationTests.AssertDestination(document, RotationDestinationTests.Value(document, catalog, "Private"),
                "XYZ", RotationDestinationTests.Expected("XYZ", 0));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task BoundNavigationRefusalLeavesThePriorReport()
    {
        QpdfPath target = QpdfPath.From("report");
        using InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(RotationDestinationTests.Source("FitH", 90, false), target);
        Save(host, "patch", new JsonArray(new JsonObject { ["jsonversion"] = 2 }, new JsonObject()));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--update-from-json=patch", "--flatten-rotation", "--json-output", target.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.UnsupportedFeature);
        Assert.True(host.TryGetOutput(target, out byte[] prior));
        Assert.Equal(new byte[] { 17, 23, 41 }, prior);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public Task BoundTransformAdmissionAndPublicationKeepThePriorReport()
    {
        QpdfPath target = QpdfPath.From("report");
        return PreparationAdmissionChecks.VerifyAsync(Prepare, (host, token) => Qpdf.ExecuteAsync(host,
            new(["input", "--update-from-json=patch", "--generate-appearances", "--flatten-annotations=all",
                "--json-output", "--json-stream-data=inline", target.Value]), token), target, 1024, ["write", "commit", "cancel"]);

        async Task<InMemoryQpdfHost> Prepare()
        {
            InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(ReportAppearanceGenerationTests.Fixture(), target);
            Save(host, "patch", new JsonArray(new JsonObject { ["jsonversion"] = 2 }, new JsonObject()));
            return host;
        }
    }

    private static async Task<JsonArray> SourceGraph(InMemoryQpdfHost host, byte[] pdf)
    {
        host.AddInput(QpdfPath.From("input"), pdf, QpdfInputOwnership.Transfer);
        QpdfExecutionResult report = await Qpdf.ExecuteAsync(host,
            new(["input", "--json-output", "--json-stream-data=inline"]), CancellationToken.None);
        using JsonDocument json = Parse(report);
        return Assert.IsType<JsonArray>(JsonNode.Parse(json.RootElement.GetProperty("qpdf").GetRawText()));
    }

    private static void Save(InMemoryQpdfHost host, string path, JsonArray envelope)
        => host.AddInput(QpdfPath.From(path), JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["qpdf"] = envelope }),
            QpdfInputOwnership.Transfer);

    private static JsonDocument Parse(QpdfExecutionResult report)
    {
        Assert.True(report.ExitCode == QpdfExitCode.Success, string.Join("\n", report.Diagnostics.Select(item => item.Message)));
        return JsonDocument.Parse(report.OutputText ?? throw new InvalidOperationException());
    }

    private static JsonElement Catalog(JsonElement table)
        => Value(table, table.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));

    private static JsonObject ObjectValue(JsonObject objects, int number)
        => Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(objects[$"obj:{number} 0 R"])["value"]);

    private static JsonElement Value(JsonElement table, JsonElement raw)
        => raw.ValueKind == JsonValueKind.String ? table.GetProperty("obj:" + raw.GetString()).GetProperty("value") : raw;

    private static string Program(JsonElement table, JsonElement reference)
        => Encoding.ASCII.GetString(table.GetProperty("obj:" + reference.GetString()).GetProperty("stream").GetProperty("data").GetBytesFromBase64());
}
