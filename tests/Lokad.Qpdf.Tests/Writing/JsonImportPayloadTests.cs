using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonImportPayloadTests
{
    private static byte[] Input(bool stream) => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /Probe 4 0 R >>", null),
        (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >>"
            + (stream ? " /Contents 4 0 R" : "") + " >>", null),
        (4, stream ? "<< /Length 3 >>" : "17", stream ? "q Q"u8.ToArray() : null)], 1);

    private static string Json(string target, string payload, string metadata)
    {
        bool create = target == "create"; int number = target == "new" ? 5 : 4;
        Dictionary<string, object?> header = new() { ["jsonversion"] = 2 };
        if (create) { header["pdfversion"] = "1.7"; }
        if (metadata == "missing-pdfversion") { header.Remove("pdfversion"); }
        if (metadata == "missing-jsonversion") { header.Remove("jsonversion"); }
        Dictionary<string, object?> stream = new();
        if (payload != "missing-dict") { stream["dict"] = new Dictionary<string, object?> { ["/Marker"] = "u:changed" }; }
        if (payload is "inline" or "both" or "missing-dict") { stream["data"] = "cQpR"; }
        if (payload is "sidecar" or "both" or "empty-sidecar") { stream["datafile"] = "payload"; }
        if (payload == "empty-inline") { stream["data"] = ""; }
        if (payload == "bad-base64") { stream["data"] = "!!!"; }
        if (payload == "bad-datafile") { stream["datafile"] = ""; }
        if (payload == "null-data") { stream["data"] = null; }
        if (payload == "null-datafile") { stream["datafile"] = null; }
        Dictionary<string, object?> objects = new()
        {
            ["obj:1 0 R"] = new { value = new Dictionary<string, object?> { ["/Type"] = "/Catalog", ["/Pages"] = "2 0 R", ["/Probe"] = number + " 0 R" } },
            ["obj:" + number + " 0 R"] = new { stream }
        };
        if (create)
        {
            objects["obj:2 0 R"] = new { value = new Dictionary<string, object?> { ["/Type"] = "/Pages", ["/Count"] = 1, ["/Kids"] = new[] { "3 0 R" } } };
            objects["obj:3 0 R"] = new { value = new Dictionary<string, object?> { ["/Type"] = "/Page", ["/Parent"] = "2 0 R", ["/MediaBox"] = new[] { 0, 0, 80, 40 }, ["/Contents"] = "4 0 R", ["/Resources"] = new Dictionary<string, object?>() } };
            if (metadata != "missing-trailer")
            {
                objects["trailer"] = metadata switch
                {
                    "empty-trailer" => new Dictionary<string, object?>(),
                    "stream-trailer" => new { stream = new { dict = new Dictionary<string, object?>(), data = "" } },
                    "non-dict-trailer" => new { value = 17 },
                    _ => new { value = new Dictionary<string, object?> { ["/Root"] = "1 0 R" } }
                };
            }
        }
        return JsonSerializer.Serialize(new { qpdf = new object[] { header, objects } });
    }

    public static IEnumerable<object[]> PayloadCases()
    {
        foreach (string target in new[] { "create", "existing-stream", "existing-scalar", "new" })
        foreach (string payload in new[] { "inline", "sidecar", "empty-inline", "empty-sidecar", "omitted", "both", "missing-dict", "bad-base64", "bad-datafile", "null-data", "null-datafile" })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [target, payload, route]; }
    }

    private static async Task Run(InMemoryQpdfHost host, bool create, string route, bool valid)
    {
        if (route == "typed")
        {
            Task<QpdfJsonImportResult> running = create
                ? Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("json"), QpdfPath.From("output"), CancellationToken.None).AsTask()
                : Qpdf.UpdatePdfFromJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("json"), QpdfPath.From("output"), CancellationToken.None).AsTask();
            if (valid) { QpdfJsonImportResult result = await running; Assert.Equal(0L, result.Counters.OpenHandles); }
            else { await Assert.ThrowsAnyAsync<QpdfException>(async () => await running); }
        }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, create
                    ? """{"inputFile":"json","outputFile":"output","jsonInput":true}"""
                    : """{"inputFile":"input","outputFile":"output","updateFromJson":"json"}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(create ? ["json", "--json-input", "--", "output"]
                    : ["input", "--update-from-json=json", "--", "output"]), CancellationToken.None);
            Assert.Equal(valid ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static async Task<InMemoryQpdfHost> Host(string target, string payload, string metadata)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Input(target != "existing-scalar"), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("json"), Encoding.UTF8.GetBytes(Json(target, payload, metadata)), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("payload"), payload == "empty-sidecar" ? [] : "q  Q"u8.ToArray(), QpdfInputOwnership.Transfer);
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await prior.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
        return host;
    }

    [Theory]
    [MemberData(nameof(PayloadCases))]
    public async Task CreationAndUpdateKeepPayloadValidationAndOwnership(string target, string payload, string route)
    {
        bool valid = payload is "inline" or "sidecar" or "empty-inline" or "empty-sidecar" || payload == "omitted" && target == "existing-stream";
        byte[] output;
        using (InMemoryQpdfHost host = await Host(target, payload, "complete"))
        {
            await Run(host, target == "create", route, valid);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out output));
        }
        if (!valid) { Assert.Equal(new byte[] { 17, 23, 41 }, output); return; }
        using InMemoryQpdfHost reader = new(); reader.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(reader, QpdfPath.From("read"), CancellationToken.None))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            Assert.True(catalog.TryGet(PdfName.FromAscii("Probe"), out QpdfObject probe));
            PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(probe).ObjectNumber));
            byte[] expected = payload switch { "omitted" => "q Q"u8.ToArray(), "inline" => "q\nQ"u8.ToArray(), "sidecar" => "q  Q"u8.ToArray(), _ => [] };
            Assert.Equal(expected, stream.RawMemory.ToArray());
            Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("Marker"), out QpdfObject marker));
            Assert.Equal("changed", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(marker).Value));
            Assert.Equal(1, document.PageCount);
        }
        Assert.Equal(0L, reader.OpenLeaseCount);
    }

    public static IEnumerable<object[]> MetadataCases()
    {
        foreach (string metadata in new[] { "missing-pdfversion", "missing-jsonversion", "missing-trailer", "empty-trailer", "stream-trailer", "non-dict-trailer" })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [metadata, route]; }
    }

    [Theory]
    [MemberData(nameof(MetadataCases))]
    public async Task RequiredCreationMetadataStillRejectsBeforePublication(string metadata, string route)
    {
        using InMemoryQpdfHost host = await Host("create", "inline", metadata);
        await Run(host, true, route, false);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal(new byte[] { 17, 23, 41 }, output);
    }
}
