using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonNameAuthoringTests
{
    private static byte[] Model(string name, string role)
    {
        JsonObject catalog = new() { ["/Type"] = "/Catalog", ["/Pages"] = "2 0 R" };
        JsonObject objects = new()
        {
            ["obj:1 0 R"] = new JsonObject { ["value"] = catalog },
            ["obj:2 0 R"] = new JsonObject { ["value"] = new JsonObject { ["/Type"] = "/Pages", ["/Kids"] = new JsonArray("3 0 R"), ["/Count"] = 1 } },
            ["obj:3 0 R"] = new JsonObject { ["value"] = new JsonObject { ["/Type"] = "/Page", ["/Parent"] = "2 0 R", ["/MediaBox"] = new JsonArray(0, 0, 100, 100), ["/Resources"] = new JsonObject() } },
            ["trailer"] = new JsonObject { ["value"] = new JsonObject { ["/Root"] = "1 0 R", ["/Size"] = role == "indirect" ? 5 : 4 } },
        };
        if (role == "key") { catalog["/Audit"] = new JsonObject { [name] = 1, ["/AB"] = 2 }; }
        else if (role == "indirect") { catalog["/Probe"] = "4 0 R"; objects["obj:4 0 R"] = new JsonObject { ["value"] = name }; }
        else { catalog["/Probe"] = name; }
        return Encoding.UTF8.GetBytes(new JsonObject { ["qpdf"] = new JsonArray(new JsonObject { ["jsonversion"] = 2, ["pdfversion"] = "1.4" }, objects) }.ToJsonString());
    }

    private static InMemoryQpdfHost Host(byte[] json)
    {
        InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("json"), json, QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("input"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)], 1), QpdfInputOwnership.Transfer);
        return host;
    }

    private static async Task Typed(InMemoryQpdfHost host, bool update)
    {
        if (update) { await Qpdf.UpdatePdfFromJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("json"), QpdfPath.From("output"), CancellationToken.None); }
        else { await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("json"), QpdfPath.From("output"), CancellationToken.None); }
    }

    private static ValueTask<QpdfExecutionResult> Execute(InMemoryQpdfHost host, string route, bool update)
        => route == "job"
            ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(update
                ? new Dictionary<string, object> { ["inputFile"] = "input", ["outputFile"] = "output", ["updateFromJson"] = "json" }
                : new Dictionary<string, object> { ["inputFile"] = "json", ["outputFile"] = "output", ["jsonInput"] = true }), CancellationToken.None)
            : Qpdf.ExecuteAsync(host, new(update ? ["input", "--update-from-json=json", "--", "output"] : ["json", "--json-input", "--", "output"]), CancellationToken.None);

    public static IEnumerable<object[]> InvalidNames()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (bool update in new[] { false, true })
        foreach (bool syntax in new[] { false, true })
        foreach (string role in new[] { "key", "value", "indirect" }) { yield return [route, update, syntax, role]; }
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public async Task NamesContainingNulRejectBeforePublication(string route, bool update, bool syntax, string role)
    {
        using InMemoryQpdfHost host = Host(Model(syntax ? "n:/A#00B" : "/A\0B", role));
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        if (route == "typed")
        {
            QpdfParseException error = await Assert.ThrowsAsync<QpdfParseException>(() => Typed(host, update));
            Assert.Contains("NUL", error.Message, StringComparison.Ordinal);
        }
        else
        {
            QpdfExecutionResult result = await Execute(host, route, update);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("NUL", StringComparison.Ordinal));
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] priorBytes)); Assert.Equal("prior"u8.ToArray(), priorBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> ValidNamesAndText()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (bool update in new[] { false, true })
        foreach (string kind in new[] { "literal-hash", "escaped-hash", "literal-zero-hash", "escaped-zero-hash", "text-nul" }) { yield return [route, update, kind]; }
    }

    [Theory]
    [MemberData(nameof(ValidNamesAndText))]
    public async Task LiteralHashesAndNulTextRemainDistinctValidRoles(string route, bool update, string kind)
    {
        using InMemoryQpdfHost host = Host(Model(kind switch { "literal-hash" => "/A#B", "escaped-hash" => "n:/A#23B", "literal-zero-hash" => "/A#00B", "escaped-zero-hash" => "n:/A#2300B", _ => "u:A\0B" }, "value"));
        if (route == "typed") { await Typed(host, update); }
        else { Assert.Equal(QpdfExitCode.Success, (await Execute(host, route, update)).ExitCode); }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1)); Assert.True(catalog.TryGet(PdfName.FromAscii("Probe"), out QpdfObject probe));
            if (kind == "text-nul") { Assert.Equal(new byte[] { 65, 0, 66 }, Assert.IsType<PdfByteString>(probe).Value); }
            else { Assert.Equal(kind.Contains("zero", StringComparison.Ordinal) ? "A#00B"u8.ToArray() : "A#B"u8.ToArray(), Assert.IsType<PdfName>(probe).RawBytes); }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> RetainedSourceNames()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (string role in new[] { "key", "value" })
        {
            yield return [route, role, false];
            if (route != "typed") { yield return [route, role, true]; }
        }
    }

    [Theory]
    [MemberData(nameof(RetainedSourceNames))]
    public async Task MalformedSourceNamesKeepExistingRecoveryPolicy(string route, string role, bool qdf)
    {
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> "
                + (role == "key" ? "/A#00B 42" : "/Audit /A#00B") + " >>", null)], 1);
        using InMemoryQpdfHost host = Host("{}"u8.ToArray()); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        if (route == "typed")
        {
            QpdfParseException error = await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.SelectPageAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new PdfPageSelectionOptions(1), CancellationToken.None).AsTask());
            Assert.Contains("NUL", error.Message, StringComparison.Ordinal);
        }
        else
        {
            List<string> argv = ["input"];
            if (qdf) { argv.Add("--qdf"); } argv.AddRange(["--", "output"]);
            Dictionary<string, object> job = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
            if (qdf) { job["qdf"] = true; }
            QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == QpdfDiagnosticSeverity.Warning);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] priorBytes));
        if (route == "typed") { Assert.Equal("prior"u8.ToArray(), priorBytes); }
        else
        {
            host.AddInput(QpdfPath.From("again"), priorBytes, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
            Assert.Equal(1, document.PageCount);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
