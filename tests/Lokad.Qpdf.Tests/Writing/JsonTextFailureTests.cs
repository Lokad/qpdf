using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonTextFailureTests
{
    private const string Template = """{"qpdf":[{"jsonversion":2,"pdfversion":"1.7"},{"obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Review":"u:valid"}},"obj:2 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["3 0 R"]}},"obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,100],"/Resources":{}}},"trailer":{"value":{"/Root":"1 0 R","/Size":4}}}]}""";
    private static byte[] Pdf() => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)], 1);

    public static IEnumerable<object[]> Cases()
    {
        foreach (string kind in new[] { "valid", "paired", "high", "low", "key", "version", "object-key", "meta-key", "stream-key", "data", "datafile", "syntax" })
        foreach (bool update in new[] { false, true })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [kind, update, route]; }
    }

    private static string Fixture(string kind)
    {
        if (kind is "stream-key" or "data" or "datafile" or "sidecar")
        {
            string stream = kind switch
            {
                "stream-key" => """{"dict":{},"bad\uD800key":"","data":""}""",
                "data" => """{"dict":{},"data":"\uD800"}""",
                "datafile" => """{"dict":{},"datafile":"\uD800"}""",
                _ => """{"dict":{},"datafile":"payload"}"""
            };
            return Template.Replace("\"trailer\":", "\"obj:4 0 R\":{\"stream\":" + stream + "},\"trailer\":", StringComparison.Ordinal);
        }
        return kind switch
        {
            "paired" => Template.Replace("u:valid", "u:\\uD83D\\uDE00", StringComparison.Ordinal),
            "high" => Template.Replace("u:valid", "u:\\uD800", StringComparison.Ordinal),
            "low" => Template.Replace("u:valid", "u:\\uDC00", StringComparison.Ordinal),
            "key" => Template.Replace("/Review", "/Re\\uD800view", StringComparison.Ordinal),
            "version" => Template.Replace("1.7", "1.\\uD800", StringComparison.Ordinal),
            "object-key" => Template.Replace("obj:1 0 R", "obj:1 \\uD800R", StringComparison.Ordinal),
            "meta-key" => Template.Replace("\"jsonversion\":2", "\"jsonversion\":2,\"bad\\uD800key\":true", StringComparison.Ordinal),
            "syntax" => "{broken",
            _ => Template
        };
    }

    private static async Task<QpdfExecutionResult?> Run(IQpdfHost host, bool update, string route)
    {
        if (route == "typed")
        {
            if (update) { await Qpdf.UpdatePdfFromJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("json"), QpdfPath.From("output"), CancellationToken.None); }
            else { await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("json"), QpdfPath.From("output"), CancellationToken.None); }
            return null;
        }
        if (route == "argv") return await Qpdf.ExecuteAsync(host, new(update ? ["input", "--update-from-json=json", "--", "output"]
            : ["json", "--json-input", "--", "output"]), CancellationToken.None);
        Dictionary<string, string> job = new() { ["inputFile"] = update ? "input" : "json", ["outputFile"] = "output" };
        job[update ? "updateFromJson" : "jsonInput"] = update ? "json" : "";
        return await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TextFailuresUseParserOutcomesAndPreservePriorOutput(string kind, bool update, string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Pdf(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("json"), Encoding.UTF8.GetBytes(Fixture(kind)), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await seed.WriteAsync(new byte[] { 17, 23 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        bool valid = kind is "valid" or "paired";
        if (!valid && route == "typed") { await Assert.ThrowsAsync<QpdfParseException>(() => Run(host, update, route)); }
        else
        {
            QpdfExecutionResult? result = await Run(host, update, route);
            if (route != "typed")
            {
                Assert.NotNull(result);
                Assert.Equal(valid ? QpdfExecutionStatus.Completed : QpdfExecutionStatus.Failed, result.Status);
                Assert.Equal(valid ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
                if (!valid) { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments); }
            }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        if (!valid) { Assert.Equal(new byte[] { 17, 23 }, output); }
        else
        {
            host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
            await using QpdfDocument doc = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
            Assert.Equal(1, doc.PageCount);
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(doc.GetObject(1));
            Assert.True(catalog.TryGet(PdfName.FromAscii("Review"), out QpdfObject text));
            Assert.Equal(kind == "paired" ? "FEFFD83DDE00" : "76616C6964", Convert.ToHexString(Assert.IsType<PdfByteString>(text).Value));
        }
    }

    [Theory]
    [InlineData(false, "typed")]
    [InlineData(true, "typed")]
    [InlineData(false, "argv")]
    [InlineData(true, "argv")]
    [InlineData(false, "job")]
    [InlineData(true, "job")]
    public async Task HostInvariantFailuresAreNotRelabeledAsBadText(bool update, string route)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), Pdf(), QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("json"), Encoding.UTF8.GetBytes(Fixture("sidecar")), QpdfInputOwnership.Transfer);
        InvalidOperationException sentinel = new("host invariant");
        Exception failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new FaultHost(storage, sentinel), update, route));
        Assert.Same(sentinel, failure); Assert.Equal(0L, storage.OpenLeaseCount);
    }

    private sealed class FaultHost(InMemoryQpdfHost storage, Exception error) : IQpdfHost
    {
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => path.Value == "payload" ? throw error : storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }
}
