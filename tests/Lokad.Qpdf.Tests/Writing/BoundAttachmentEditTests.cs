using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class BoundAttachmentEditTests
{
    private const string Source = """
        {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
          "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Names":"4 0 R","/PrivateNames":"4 0 R"}},
          "obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["3 0 R"],"/Count":1}},
          "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,100],"/Resources":{}}},
          "obj:4 0 R":{"value":{"/EmbeddedFiles":{"/Names":["b:feff004b","5 3 R"]},"/Dests":{"/Names":[]}}},
          "obj:5 3 R":{"value":{"/Type":"/Filespec","/UF":"u:original.txt","/EF":{"/UF":"6 4 R"}}},
          "obj:6 4 R":{"stream":{"dict":{"/Type":"/EmbeddedFile"},"data":"QQD/"}},
          "trailer":{"value":{"/Root":"1 0 R"}}
        }]}
        """;
    private static readonly byte[] Payload = [0, 128, 255, 90];

    [Fact]
    public async Task BoundFullReportEditsTheActiveTreeAndKeepsDeclaredPrivatePairs()
    {
        using InMemoryQpdfHost host = new();
        AddSources(host);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """
            {"inputFile":"graph","jsonInput":"","json":"2","jsonKey":["attachments","qpdf"],"jsonStreamData":"inline",
             "removeAttachment":["K"],"addAttachment":[{"file":"payload","key":"K","filename":"fresh.bin","description":"fresh"}]}
            """, CancellationToken.None);
        AssertSuccess(result);
        using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
        JsonElement table = json.RootElement.GetProperty("qpdf")[1];
        Assert.Equal("1.7", json.RootElement.GetProperty("qpdf")[0].GetProperty("pdfversion").GetString());
        JsonElement catalog = Value(table, table.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        Assert.Equal("4 0 R", catalog.GetProperty("/PrivateNames").GetString());
        JsonElement activeNames = Value(table, catalog.GetProperty("/Names"));
        Assert.Empty(activeNames.GetProperty("/Dests").GetProperty("/Names").EnumerateArray());
        JsonElement privateNames = Value(table, catalog.GetProperty("/PrivateNames"));
        Assert.Equal("5 3 R", privateNames.GetProperty("/EmbeddedFiles").GetProperty("/Names")[1].GetString());
        Assert.Equal("6 4 R", table.GetProperty("obj:5 3 R").GetProperty("value").GetProperty("/EF").GetProperty("/UF").GetString());
        Assert.Equal(new byte[] { 65, 0, 255 }, table.GetProperty("obj:6 4 R").GetProperty("stream").GetProperty("data").GetBytesFromBase64());
        JsonElement attachment = Assert.Single(json.RootElement.GetProperty("attachments").EnumerateObject()).Value;
        string stream = Assert.IsType<string>(attachment.GetProperty("preferredcontents").GetString());
        Assert.Equal(Payload, table.GetProperty("obj:" + stream).GetProperty("stream").GetProperty("data").GetBytesFromBase64());
        Assert.Equal("fresh", attachment.GetProperty("description").GetString());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task BoundPdfCreationPublishesEditedPayloadWithOutputEncryption()
    {
        using InMemoryQpdfHost host = new();
        AddSources(host);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["graph", "--json-input", "--add-attachment", "payload",
            "--key=added", "--mimetype=application/octet-stream", "--", "--encrypt", "reader", "owner", "256", "--", "output"]), CancellationToken.None);
        AssertSuccess(result);
        UsePublished(host, "output", "published");
        QpdfPath pdf = QpdfPath.From("published"), extracted = QpdfPath.From("extracted");
        Assert.Equal(QpdfExitCode.Error, (await Qpdf.ExecuteAsync(host, new(["published", "--show-npages"]), CancellationToken.None)).ExitCode);
        await Qpdf.ExtractAttachmentAsync(host, pdf, "added", extracted, QpdfPassword.FromText("reader"), QpdfLimits.Default, CancellationToken.None);
        Assert.True(host.TryGetOutput(extracted, out byte[] bytes));
        Assert.Equal(Payload, bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task BoundMinimalReportUsesThePatchedAttachmentTreeAndAnAuthenticatedCopy()
    {
        using InMemoryQpdfHost host = new();
        AddSources(host);
        AssertSuccess(await Qpdf.ExecuteAsync(host, new(["graph", "--json-input", "input"]), CancellationToken.None));
        UsePublished(host, "input", "input");
        AssertSuccess(await Qpdf.ExecuteAsync(host,
            new(["input", "--encrypt", "reader", "owner", "256", "--", "donor"]), CancellationToken.None));
        UsePublished(host, "donor", "donor");
        QpdfExecutionResult baseline = await Qpdf.ExecuteAsync(host, new(["input", "--json-output"]), CancellationToken.None);
        AssertSuccess(baseline);
        using (JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(baseline.OutputText)))
        {
            JsonElement table = json.RootElement.GetProperty("qpdf")[1];
            JsonElement catalog = Value(table, table.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
            string namesKey = "obj:" + Assert.IsType<string>(catalog.GetProperty("/Names").GetString());
            JsonObject names = Assert.IsType<JsonObject>(JsonNode.Parse(table.GetProperty(namesKey).GetRawText()));
            JsonObject value = Assert.IsType<JsonObject>(names["value"]);
            JsonObject embedded = Assert.IsType<JsonObject>(value["/EmbeddedFiles"]);
            Assert.IsType<JsonArray>(embedded["/Names"])[0] = "u:patched";
            JsonObject patch = new() { ["qpdf"] = new JsonArray(new JsonObject { ["jsonversion"] = 2 },
                new JsonObject { [namesKey] = names }) };
            host.AddInput(QpdfPath.From("patch"), JsonSerializer.SerializeToUtf8Bytes(patch), QpdfInputOwnership.Transfer);
        }
        QpdfExecutionResult report = await Qpdf.ExecuteAsync(host, new(["input", "--update-from-json=patch", "--remove-attachment=patched",
            "--copy-attachments-from", "donor", "--password=reader", "--prefix=copy-", "--", "--json-output",
            "--json-stream-data=inline", "report"]), CancellationToken.None);
        AssertSuccess(report);
        UsePublished(host, "report", "report");
        AssertSuccess(await Qpdf.ExecuteAsync(host, new(["report", "--json-input", "recreated"]), CancellationToken.None));
        UsePublished(host, "recreated", "recreated");
        QpdfPath pdf = QpdfPath.From("recreated"), extracted = QpdfPath.From("extracted");
        Assert.Equal("copy-K", Assert.Single(await Qpdf.ListAttachmentsAsync(host, pdf, CancellationToken.None)).Key);
        await Qpdf.ExtractAttachmentAsync(host, pdf, "copy-K", extracted, CancellationToken.None);
        Assert.True(host.TryGetOutput(extracted, out byte[] bytes));
        Assert.Equal(new byte[] { 65, 0, 255 }, bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task BoundAttachmentConflictKeepsThePriorReport()
    {
        using InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync([], QpdfPath.From("report"));
        AddSources(host);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["graph", "--json-input", "--json-output", "--add-attachment", "payload", "--key=K", "--", "report"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, item => item.Message.Contains("conflicting keys", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(QpdfPath.From("report"), out byte[] bytes));
        Assert.Equal(new byte[] { 17, 23, 41 }, bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task BoundReportCannotPublishOverAnAttachmentInput()
    {
        QpdfPath target = QpdfPath.From("payload");
        using InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync([], target);
        AddSources(host);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["graph", "--json-input", "--json-output", "--add-attachment", target.Value, "--", target.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, item => item.Message.Contains("aliases an input", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(target, out byte[] bytes));
        Assert.Equal(new byte[] { 17, 23, 41 }, bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public Task BoundAttachmentPreparationRefundsAdmissionAndKeepsThePriorReport()
    {
        QpdfPath target = QpdfPath.From("report");
        return PreparationAdmissionChecks.VerifyAsync(Prepare, (host, token) => Qpdf.ExecuteAsync(host,
            new(["graph", "--json-input", "--json-output", "--json-stream-data=inline", "--remove-attachment=K",
                "--add-attachment", "payload", "--key=K", "--", target.Value]), token), target, 1024, ["write", "commit", "cancel"]);

        async Task<InMemoryQpdfHost> Prepare()
        {
            InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync([], target);
            AddSources(host);
            return host;
        }
    }

    private static JsonElement Value(JsonElement table, JsonElement raw)
        => raw.ValueKind == JsonValueKind.String ? table.GetProperty("obj:" + raw.GetString()).GetProperty("value") : raw;

    private static void UsePublished(InMemoryQpdfHost host, string from, string to)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From(from), out byte[] bytes));
        host.AddInput(QpdfPath.From(to), bytes, QpdfInputOwnership.Transfer);
    }

    private static void AddSources(InMemoryQpdfHost host)
    {
        host.AddInput(QpdfPath.From("graph"), Encoding.UTF8.GetBytes(Source), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("payload"), (byte[])Payload.Clone(), QpdfInputOwnership.Transfer);
    }

    private static void AssertSuccess(QpdfExecutionResult result)
        => Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("\n", result.Diagnostics.Select(item => item.Message)));
}
