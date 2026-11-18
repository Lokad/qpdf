using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Qpdf;
using Lokad.Qpdf.Hosting;

internal static class JsonLayoutChecks
{
    internal static async Task RunAsync()
    {
        using InMemoryQpdfHost host = new();
        QpdfPath json = QpdfPath.From("layout.json"), created = QpdfPath.From("created-layout.pdf"), updated = QpdfPath.From("updated-layout.pdf");
        const string model = """{"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{"obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Marker":"u:created"}},"obj:2 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["3 0 R"]}},"obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,100],"/Resources":{},"/Contents":"4 0 R"}},"obj:4 0 R":{"stream":{"dict":{},"data":"cQpRCg=="}},"obj:5 0 R":{"value":{"/Title":"u:layout"}},"trailer":{"value":{"/Root":"1 0 R","/Info":"5 0 R"}}}]}""";
        host.AddInput(json, Encoding.UTF8.GetBytes(model), QpdfInputOwnership.Transfer);
        QpdfPassword password = QpdfPassword.FromText("layout-reader");
        QpdfJsonImportOptions options = new("generate", true);
        QpdfOutputEncryption encryption = QpdfOutputEncryption.CreateAes256(password, QpdfPassword.FromText("layout-owner"), -4, true);
        QpdfJsonImportResult create = await Qpdf.CreatePdfFromJsonAsync(host, json, created, options, encryption, QpdfLimits.Default, CancellationToken.None);
        Check(create.PageCount == 1 && create.Counters.OpenHandles == 0 && options.ObjectStreams == "generate" && options.Linearize, "typed creation options");
        Check(host.TryGetOutput(created, out byte[] bytes), "created output");
        host.AddInput(created, bytes, QpdfInputOwnership.Transfer);
        QpdfCheckResult layout = await Qpdf.CheckAsync(host, created, new QpdfCheckOptions { Password = password }, QpdfLimits.Default, CancellationToken.None);
        Check(layout.Checks.Any(c => c.Name == "linearization" && c.Status == QpdfCheckStatus.Passed)
            && Encoding.Latin1.GetString(bytes).Contains("/Linearized", StringComparison.Ordinal)
            && Encoding.Latin1.GetString(bytes).Contains("/Type /ObjStm", StringComparison.Ordinal), "packed linearized creation");
        QpdfJsonOptions inspection = new(2, ["qpdf"]) { Password = password };
        QpdfJsonResult exported = await Qpdf.GetDocumentJsonAsync(host, created, inspection, CancellationToken.None);
        using JsonDocument report = JsonDocument.Parse(exported.Json);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        string reference = table.GetProperty("trailer").GetProperty("value").GetProperty("/Root").GetString()
            ?? throw new InvalidOperationException("Missing catalog reference.");
        JsonObject catalog = JsonNode.Parse(table.GetProperty("obj:" + reference).GetProperty("value").GetRawText()) as JsonObject
            ?? throw new InvalidOperationException("Missing catalog value.");
        Check(catalog["/Marker"]?.GetValue<string>() == "u:created", "created value"); catalog["/Marker"] = "u:updated";
        byte[] patch = JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2 },
            new Dictionary<string, object> { ["obj:" + reference] = new { value = catalog } } } });
        host.AddInput(json, patch, QpdfInputOwnership.Transfer);
        QpdfJsonImportResult update = await Qpdf.UpdatePdfFromJsonAsync(host, created, json, updated,
            new QpdfJsonImportOptions("preserve", false), password, QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None);
        Check(update.PageCount == 1 && update.Counters.OpenHandles == 0, "typed update options");
        Check(host.TryGetOutput(updated, out byte[] changed), "updated output"); host.AddInput(updated, changed, QpdfInputOwnership.Transfer);
        QpdfJsonResult read = await Qpdf.GetDocumentJsonAsync(host, updated, inspection, CancellationToken.None);
        using JsonDocument updatedReport = JsonDocument.Parse(read.Json);
        JsonElement updatedTable = updatedReport.RootElement.GetProperty("qpdf")[1];
        string target = updatedTable.GetProperty("trailer").GetProperty("value").GetProperty("/Root").GetString()
            ?? throw new InvalidOperationException("Missing updated catalog.");
        Check(updatedTable.GetProperty("obj:" + target).GetProperty("value").GetProperty("/Marker").GetString() == "u:updated"
            && Encoding.Latin1.GetString(changed).Contains("/Type /ObjStm", StringComparison.Ordinal), "protected packed update");
        Check(host.OpenLeaseCount == 0, "layout input cleanup");
        await CheckDecodeLevelAsync();
    }

    private static async Task CheckDecodeLevelAsync()
    {
        using InMemoryQpdfHost host = new();
        QpdfPath json = QpdfPath.From("encoded.json"), pdf = QpdfPath.From("encoded.pdf");
        const string model = """
            {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
              "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R"}},
              "obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["3 0 R"],"/Count":1}},
              "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/Contents":"4 0 R","/MediaBox":[0,0,100,100],"/Resources":{}}},
              "obj:4 0 R":{"stream":{"dict":{"/Filter":"/ASCIIHexDecode"},"data":"NzEwQTUxMEE+"}},
              "trailer":{"value":{"/Root":"1 0 R"}}
            }]}
            """;
        host.AddInput(json, Encoding.UTF8.GetBytes(model), QpdfInputOwnership.Transfer);
        await Qpdf.CreatePdfFromJsonAsync(host, json, pdf, CancellationToken.None);
        Check(host.TryGetOutput(pdf, out byte[] bytes), "encoded fixture publication");
        host.AddInput(pdf, bytes, QpdfInputOwnership.Transfer);

        await CheckReportAsync(false, QpdfStreamDecodeLevel.None, "NzEwQTUxMEE+", true);
        await CheckReportAsync(true, QpdfStreamDecodeLevel.Generalized, "cQpRCg==", false);

        async Task CheckReportAsync(bool minimal, QpdfStreamDecodeLevel level, string payload, bool hasFilter)
        {
            QpdfJsonOptions options = new(2, ["qpdf"], "inline", null, [], minimal) { DecodeLevel = level };
            QpdfJsonResult report = await Qpdf.GetDocumentJsonAsync(host, pdf, options, CancellationToken.None);
            using JsonDocument parsed = JsonDocument.Parse(report.Json);
            JsonElement root = parsed.RootElement;
            JsonElement stream = root.GetProperty("qpdf")[1].EnumerateObject()
                .Single(item => item.Value.TryGetProperty("stream", out _)).Value.GetProperty("stream");
            Check(root.TryGetProperty("parameters", out _) == !minimal, "decode level preserves report shape");
            Check(stream.GetProperty("data").GetString() == payload
                && stream.GetProperty("dict").TryGetProperty("/Filter", out _) == hasFilter, "decode level controls the stream");
        }
    }

    private static void Check(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException("JSON layout consumer: " + message); } }
}
