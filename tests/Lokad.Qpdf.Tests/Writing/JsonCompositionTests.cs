using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonCompositionTests
{
    private static readonly QpdfPath Source = QpdfPath.From("source.json");
    private static readonly QpdfPath Patch = QpdfPath.From("patch.json");
    private static readonly QpdfPath Target = QpdfPath.From("target.pdf");
    private static readonly QpdfPath Sidecar = QpdfPath.From("payload.bin");
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly byte[] Payload = [0, 254, 255, 10];
    private static readonly byte[] Prior = "prior output"u8.ToArray();

    private const string Create = """
        {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
          "obj:5 2 R":{"value":{"/Type":"/Catalog","/Pages":"8 0 R","/New":"41 7 R","/Stale":"23 1 R","/Keep":"23 4 R"}},
          "obj:8 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["19 7 R"]}},
          "obj:19 7 R":{"value":{"/Type":"/Page","/Parent":"8 0 R","/MediaBox":[0,0,100,100],"/Resources":{}}},
          "obj:23 4 R":{"stream":{"dict":{"/Tag":"u:original"},"datafile":"payload.bin"}},
          "trailer":{"value":{"/Root":"5 2 R","/Size":42}}
        }]}
        """;
    private const string ValidPatch = """
        {"qpdf":[{"jsonversion":2},{
          "obj:41 7 R":{"value":"u:declared-second"},
          "obj:23 4 R":{"stream":{"dict":{"/Tag":"u:patched"}}}
        }]}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateThenPatchBindsExactPairsAndReusesTheFirstPayload(bool job)
    {
        using InMemoryQpdfHost host = NewHost(ValidPatch);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """
                {"inputFile":"source.json","outputFile":"target.pdf","jsonInput":"","updateFromJson":"patch.json","streamData":"preserve"}
                """, Token)
            : await Qpdf.ExecuteAsync(host,
                new(["source.json", "--json-input", "--update-from-json=patch.json", "--stream-data=preserve", "target.pdf"]), Token);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(Target, out byte[] pdf));
        using InMemoryQpdfHost readback = new(); readback.AddInput(Target, pdf, QpdfInputOwnership.Transfer);
        QpdfExecutionResult inspection = await Qpdf.ExecuteAsync(readback,
            new(["target.pdf", "--json=2", "--json-key=qpdf", "--json-stream-data=inline"]), Token);
        Assert.Equal(QpdfExitCode.Success, inspection.ExitCode);
        using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(inspection.OutputText));
        JsonElement table = json.RootElement.GetProperty("qpdf")[1];
        JsonElement catalog = ReadCatalog(table);
        Assert.False(catalog.TryGetProperty("/Stale", out _));
        JsonElement declared = Resolve(table, catalog.GetProperty("/New"));
        Assert.Equal("u:declared-second", declared.GetProperty("value").GetString());
        JsonElement stream = Resolve(table, catalog.GetProperty("/Keep")).GetProperty("stream");
        Assert.Equal("u:patched", stream.GetProperty("dict").GetProperty("/Tag").GetString());
        Assert.Equal(Payload, stream.GetProperty("data").GetBytesFromBase64());
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task PatchCanAddressAPagePromotedBeforeItsDeclarationsApply()
    {
        const string direct = """
            {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
              "obj:5 2 R":{"value":{"/Type":"/Catalog","/Pages":"8 0 R"}},
              "obj:8 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":[{"/Type":"/Page","/MediaBox":[0,0,1,1],"/Resources":{}}]}},
              "trailer":{"value":{"/Root":"5 2 R","/Pending":"45 1 R","/Size":100}}
            }]}
            """;
        const string patch = """
            {"qpdf":[{"jsonversion":2,"calledgetallpages":true},{
              "obj:46 0 R":{"value":{"/Type":"/Page","/Parent":"8 0 R","/Rotate":90,"/MediaBox":[0,0,1,1],"/Resources":{}}},
              "obj:100 1 R":{"value":"u:other"}
            }]}
            """;
        using InMemoryQpdfHost host = NewHost(patch);
        host.AddInput(Source, Encoding.UTF8.GetBytes(direct), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["source.json", "--json-input", "--update-from-json=patch.json", "target.pdf"]), Token);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(Target, out byte[] pdf));
        using InMemoryQpdfHost readback = new(); readback.AddInput(Target, pdf, QpdfInputOwnership.Transfer);
        QpdfExecutionResult inspection = await Qpdf.ExecuteAsync(readback, new(["target.pdf", "--json=2", "--json-key=pages", "--json-key=qpdf"]), Token);
        using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(inspection.OutputText));
        JsonElement page = Assert.Single(json.RootElement.GetProperty("pages").EnumerateArray());
        JsonElement table = json.RootElement.GetProperty("qpdf")[1];
        Assert.Equal(90, Resolve(table, page.GetProperty("object")).GetProperty("value").GetProperty("/Rotate").GetInt32());
    }

    [Theory]
    [InlineData("invalid-envelope")]
    [InlineData("stale-stream")]
    public async Task MalformedSecondStagePreservesTheActualPriorTarget(string failure)
    {
        string patch = failure == "invalid-envelope" ? "{}" : ValidPatch.Replace("23 4 R", "23 1 R", StringComparison.Ordinal);
        using InMemoryQpdfHost host = NewHost(patch);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Target, Token))
        {
            await prior.WriteAsync(Prior, Token);
            await prior.CommitAsync(Token);
        }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["source.json", "--json-input", "--update-from-json=patch.json", "target.pdf"]), Token);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.InvalidArguments);
        Assert.True(host.TryGetOutput(Target, out byte[] current));
        Assert.Equal(Prior, current);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task BoundProjectionKeepsDeclaredAddressesAcrossDescriptorsAndValues()
    {
        using InMemoryQpdfHost host = NewHost(ValidPatch);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([
            "source.json", "--json-input", "--update-from-json=patch.json", "--json=2",
            "--json-key=pages", "--json-key=qpdf", "--json-stream-data=inline"]), Token);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
        JsonElement page = Assert.Single(json.RootElement.GetProperty("pages").EnumerateArray());
        Assert.Equal("19 7 R", page.GetProperty("object").GetString());
        JsonElement table = json.RootElement.GetProperty("qpdf")[1];
        Assert.Equal("5 2 R", table.GetProperty("trailer").GetProperty("value").GetProperty("/Root").GetString());
        JsonElement catalog = ReadCatalog(table);
        Assert.Equal("41 7 R", catalog.GetProperty("/New").GetString());
        Assert.Equal("23 4 R", catalog.GetProperty("/Keep").GetString());
        Assert.False(catalog.TryGetProperty("/Stale", out _));
        Assert.Equal(Payload, table.GetProperty("obj:23 4 R").GetProperty("stream").GetProperty("data").GetBytesFromBase64());
        Assert.Equal(0, host.OpenLeaseCount);

        QpdfExecutionResult unpatched = await Qpdf.ExecuteAsync(host, new(["source.json", "--json-input", "--json-output"]), Token);
        Assert.Equal(QpdfExitCode.Success, unpatched.ExitCode);
        using JsonDocument creation = JsonDocument.Parse(Assert.IsType<string>(unpatched.OutputText));
        JsonElement originalTable = creation.RootElement.GetProperty("qpdf")[1];
        Assert.False(ReadCatalog(originalTable).TryGetProperty("/New", out _));
        Assert.Equal("u:original", originalTable.GetProperty("obj:23 4 R").GetProperty("stream").GetProperty("dict").GetProperty("/Tag").GetString());
    }

    [Fact]
    public async Task NamedProjectionSeparatesSameNumberStreamGenerations()
    {
        using InMemoryQpdfHost host = NewHost(ValidPatch);
        string second = """
            {"qpdf":[{"jsonversion":2},{"obj:23 6 R":{"stream":{"dict":{},"data":"T1RIRVI="}}}]}
            """;
        host.AddInput(Patch, Encoding.UTF8.GetBytes(second), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """
            {"inputFile":"source.json","jsonInput":"","updateFromJson":"patch.json","jsonOutput":"2",
             "jsonStreamData":"file","jsonStreamPrefix":"parts","outputFile":"report.json"}
            """, Token);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Null(result.OutputText);
        Assert.True(host.TryGetOutput(QpdfPath.From("report.json"), out byte[] report));
        using JsonDocument json = JsonDocument.Parse(report);
        JsonElement table = json.RootElement.GetProperty("qpdf")[1];
        CheckPayload("obj:23 4 R", "parts-23-4", Payload);
        CheckPayload("obj:23 6 R", "parts-23-6", "OTHER"u8.ToArray());
        Assert.Equal(0, host.OpenLeaseCount);

        void CheckPayload(string key, string expectedName, byte[] expected)
        {
            JsonElement stream = table.GetProperty(key).GetProperty("stream");
            Assert.Equal(expectedName, stream.GetProperty("datafile").GetString());
            Assert.True(host.TryGetOutput(QpdfPath.From(expectedName), out byte[] bytes));
            Assert.Equal(expected, bytes);
        }
    }

    [Fact]
    public async Task AProjectedSidecarCannotReplaceABoundInput()
    {
        using InMemoryQpdfHost host = NewHost(ValidPatch);
        QpdfPath report = QpdfPath.From("report.json");
        QpdfPath sidecar = QpdfPath.From("parts-23");
        host.AddInput(Source, Encoding.UTF8.GetBytes(Create.Replace("payload.bin", "parts-23", StringComparison.Ordinal)), QpdfInputOwnership.Transfer);
        host.AddInput(sidecar, Payload, QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(report, Token))
        {
            await prior.WriteAsync(Prior, Token);
            await prior.CommitAsync(Token);
        }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([
            "source.json", "--json-input", "--update-from-json=patch.json", "--json-output",
            "--json-stream-data=file", "--json-stream-prefix=parts", "report.json"]), Token);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments && d.Message.Contains("aliases an input", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(report, out byte[] current));
        Assert.Equal(Prior, current);
        Assert.False(host.TryGetOutput(sidecar, out _));
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ObjectFilterMatchesTheDeclaredPairInVersionOne()
    {
        using InMemoryQpdfHost host = NewHost(ValidPatch);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([
            "source.json", "--json-input", "--update-from-json=patch.json", "--json=1",
            "--json-key=objects", "--json-object=23,4"]), Token);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
        JsonProperty stream = Assert.Single(json.RootElement.GetProperty("objects").EnumerateObject());
        Assert.Equal("23 4 R", stream.Name);
        Assert.Equal("patched", stream.Value.GetProperty("/Tag").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PhysicalUpdateAuthenticatesBeforeProjection(bool authenticated)
    {
        using InMemoryQpdfHost host = new();
        QpdfPath input = QpdfPath.From("protected.pdf");
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r6-ascii.pdf"));
        host.AddInput(input, bytes, QpdfInputOwnership.Transfer);
        QpdfExecutionResult original = await Qpdf.ExecuteAsync(host,
            new(["protected.pdf", "--password=reader", "--json=2", "--json-key=qpdf"]), Token);
        Assert.Equal(QpdfExitCode.Success, original.ExitCode);
        using JsonDocument sourceReport = JsonDocument.Parse(Assert.IsType<string>(original.OutputText));
        JsonElement table = sourceReport.RootElement.GetProperty("qpdf")[1];
        string root = Assert.IsType<string>(table.GetProperty("trailer").GetProperty("value").GetProperty("/Root").GetString());
        Dictionary<string, JsonElement> catalog = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ReadCatalog(table))
            ?? throw new InvalidOperationException("Missing source catalog.");
        catalog.Add("/Marker", JsonSerializer.SerializeToElement("u:bound"));
        Dictionary<string, object> change = new() { ["obj:" + root] = new { value = catalog } };
        byte[] patch = JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2 }, change } });
        host.AddInput(Patch, patch, QpdfInputOwnership.Transfer);

        string password = authenticated ? "reader" : "wrong";
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([
            "protected.pdf", "--password=" + password, "--update-from-json=patch.json", "--json=2",
            "--json-key=qpdf", "--json-key=encrypt"]), Token);
        if (!authenticated)
        {
            Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidPassword);
            Assert.Null(result.OutputText);
        }
        else
        {
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
            JsonElement effective = json.RootElement.GetProperty("qpdf")[1];
            Assert.Equal("u:bound", ReadCatalog(effective).GetProperty("/Marker").GetString());
            Assert.True(json.RootElement.GetProperty("encrypt").GetProperty("encrypted").GetBoolean());
            Assert.False(effective.GetProperty("trailer").GetProperty("value").TryGetProperty("/Encrypt", out _));
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    private static InMemoryQpdfHost NewHost(string patch)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(Source, Encoding.UTF8.GetBytes(Create), QpdfInputOwnership.Transfer);
        host.AddInput(Patch, Encoding.UTF8.GetBytes(patch), QpdfInputOwnership.Transfer);
        host.AddInput(Sidecar, Payload, QpdfInputOwnership.Copy);
        return host;
    }

    private static JsonElement ReadCatalog(JsonElement table)
        => Resolve(table, table.GetProperty("trailer").GetProperty("value").GetProperty("/Root")).GetProperty("value");

    private static JsonElement Resolve(JsonElement table, JsonElement reference)
        => table.GetProperty("obj:" + reference.GetString());
}
