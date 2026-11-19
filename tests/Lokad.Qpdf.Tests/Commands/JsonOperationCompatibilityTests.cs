using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class JsonOperationCompatibilityTests
{
    private const string Graph = """
        {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
          "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/AcroForm":"5 0 R","/StructTreeRoot":"6 0 R"}},
          "obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["3 0 R"],"/Count":1}},
          "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,100],"/Resources":{}}},
          "obj:4 0 R":{"value":null},
          "obj:5 0 R":{"value":{"/Fields":[]}},
          "obj:6 0 R":{"value":{"/Type":"/StructTreeRoot","/K":[]}},
          "trailer":{"value":{"/Root":"1 0 R","/Size":7}}
        }]}
        """;
    private const string Update = """{"qpdf":[{"jsonversion":2},{}]}""";
    private static readonly byte[] Pdf = PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R /StructTreeRoot 6 0 R >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
        (4, "null", null), (5, "<< /Fields [] >>", null), (6, "<< /Type /StructTreeRoot /K [] >>", null)], 1);

    private static readonly (string Key, object Value, string[] Arguments)[] RefusalSettings =
        [
            ("qdf", true, ["--qdf"]), ("linearize", true, ["--linearize"]),
            ("removeUnreferencedResources", "yes", ["--remove-unreferenced-resources=yes"]),
            ("flattenRotation", true, ["--flatten-rotation"]), ("flattenAnnotations", "all", ["--flatten-annotations=all"]),
            ("generateAppearances", true, ["--generate-appearances"]),
        ];

    public static IEnumerable<object[]> RejectedRequests()
    {
        foreach (var setting in RefusalSettings)
        {
            string key = setting.Key;
            if (key is not ("linearize" or "qdf" or "removeUnreferencedResources"))
            { yield return [key, "create", "argv"]; }
            if (key is "qdf" or "linearize")
            { yield return [key, "inspect", "argv"]; }
        }

        // Sample operation and adapter wiring independently of setting policy.
        (string Key, string Mode, string Route)[] adapters = [
            ("removeUnreferencedResources", "bound-inspect", "mixed"),
            ("flattenAnnotations", "update", "mixed"),
        ];
        foreach ((string key, string mode, string route) in adapters)
        {
            yield return [key, mode, route];
        }
    }

    [Theory]
    [MemberData(nameof(RejectedRequests))]
    public async Task RejectUnsupportedOperationsBeforeDocumentAccess(string key, string mode, string route)
    {
        (_, object value, string[] arguments) = RefusalSettings.Single(setting => setting.Key == key);
        using InMemoryQpdfHost storage = CreateHost();
        await Seed(storage);
        ObservingHost host = new(storage);
        QpdfExecutionResult result = await Execute(host, storage, mode, route, new() { [key] = value }, arguments);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments);
        if (route == "mixed") { Assert.Equal(new[] { "settings" }, host.Inputs); }
        else { Assert.Empty(host.Inputs); }
        Assert.Equal(0, host.Outputs);
        Assert.Equal(0, storage.OpenLeaseCount);
        Assert.Equal(0, result.Counters.OpenHandles);
        Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] prior));
        Assert.Equal("prior"u8.ToArray(), prior);
    }

    [Theory]
    [InlineData("create", "argv")]
    [InlineData("create", "mixed")]
    [InlineData("update", "job")]
    public async Task CompatibleImportSettingsCoexist(string mode, string route)
    {
        using InMemoryQpdfHost storage = CreateHost();
        ObservingHost host = new(storage);
        QpdfExecutionResult result = await Execute(host, storage, mode, route, new()
        {
            ["minVersion"] = "1.6", ["forceVersion"] = "1.7", ["staticId"] = true,
            ["newlineBeforeEndstream"] = true, ["reportMemoryUsage"] = true, ["noWarn"] = true,
        }, ["--min-version=1.6", "--force-version=1.7", "--static-id", "--newline-before-endstream", "--report-memory-usage", "--no-warn"]);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.StartsWith("%PDF-1.7", Encoding.ASCII.GetString(output), StringComparison.Ordinal);
        storage.AddInput(QpdfPath.From("published"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(storage, QpdfPath.From("published"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.True(catalog.TryGet(new PdfName("AcroForm"u8.ToArray()), out _));
        Assert.True(catalog.TryGet(new PdfName("StructTreeRoot"u8.ToArray()), out _));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public async Task MinimumVersionActsWithoutAnOverridingForce(string mode)
    {
        using InMemoryQpdfHost storage = CreateHost();
        QpdfExecutionResult result = await Execute(new(storage), storage, mode, "argv",
            new() { ["minVersion"] = "1.6" }, ["--min-version=1.6"]);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.StartsWith("%PDF-1.6", Encoding.ASCII.GetString(output), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("create", "job")]
    [InlineData("update", "argv")]
    public async Task StaticIdentifiersReachTheImportWriter(string mode, string route)
    {
        using InMemoryQpdfHost storage = CreateHost();
        QpdfExecutionResult result = await Execute(new(storage), storage, mode, route,
            new() { ["staticId"] = true }, ["--static-id"]);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        (string first, string second) = WriterControlsTests.IdsOf(output);
        Assert.Equal("31415926535897932384626433832795", first);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public async Task NewlineSettingChangesTheActualStreamBoundary(string mode)
    {
        foreach (bool enabled in new[] { false, true })
        {
            using InMemoryQpdfHost storage = CreateHost();
            JsonObject graph = Assert.IsType<JsonObject>(JsonNode.Parse(Graph));
            JsonObject objects = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(graph["qpdf"])[1]);
            JsonObject page = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(objects["obj:3 0 R"])["value"]);
            page["/Contents"] = "4 0 R";
            objects["obj:4 0 R"] = new JsonObject { ["stream"] = new JsonObject { ["dict"] = new JsonObject(), ["data"] = "cSBR" } };
            storage.AddInput(QpdfPath.From("graph"), Encoding.UTF8.GetBytes(graph.ToJsonString()), QpdfInputOwnership.Transfer);
            storage.AddInput(QpdfPath.From("input"), PdfTestDocument.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
                (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << >> >>", null),
                (4, "<< /Length 3 >>", "q Q"u8.ToArray())], 1), QpdfInputOwnership.Transfer);
            QpdfExecutionResult result = await Execute(new(storage), storage, mode, "argv",
                new() { ["newlineBeforeEndstream"] = enabled }, enabled ? ["--newline-before-endstream"] : []);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            storage.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(storage, QpdfPath.From("read"), CancellationToken.None);
            Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject raw));
            PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(raw)));
            Assert.Equal("q Q"u8.ToArray(), StreamObservation.Decode(stream));
            byte[] boundary = stream.Data.Concat(enabled ? new byte[] { 10 } : []).Concat("endstream"u8.ToArray()).ToArray();
            Assert.True(output.AsSpan().IndexOf(boundary) >= 0);
            byte[] extra = stream.Data.Concat(enabled ? new byte[] { 10, 10 } : new byte[] { 10 }).Concat("endstream"u8.ToArray()).ToArray();
            Assert.True(output.AsSpan().IndexOf(extra) < 0);
        }
    }

    [Fact]
    public async Task MemoryReportingIsObservableForNestedJsonCreation()
    {
        using InMemoryQpdfHost storage = CreateHost();
        QpdfExecutionResult result = await Execute(new(storage), storage, "create", "mixed",
            new() { ["reportMemoryUsage"] = true }, []);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        QpdfDiagnostic report = Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.MemoryUsage);
        Assert.Equal(QpdfDiagnosticSeverity.Info, report.Severity);
        Assert.Contains("Peak memory usage:", report.Message, StringComparison.Ordinal);
        Assert.True(result.Counters.PeakMemoryBytes > 0);
    }

    [Fact]
    public async Task WarningSuppressionActsOnTheUpdatedPdfReader()
    {
        foreach (bool enabled in new[] { false, true })
        {
            using InMemoryQpdfHost storage = CreateHost();
            storage.AddInput(QpdfPath.From("input"), PdfTestDocument.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
                (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
                (4, "<< /Title (first) /Title (last) >>", null)], 1, null, "1.4", "/Info 4 0 R"), QpdfInputOwnership.Transfer);
            QpdfExecutionResult result = await Execute(new(storage), storage, "update", "argv",
                new() { ["noWarn"] = enabled }, enabled ? ["--no-warn"] : []);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
            if (enabled) { Assert.DoesNotContain(result.Diagnostics, d => d.Severity == QpdfDiagnosticSeverity.Warning); }
            else { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.DuplicateKey); }
            Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            Assert.True(output.AsSpan().StartsWith("%PDF-"u8));
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("inspect")]
    [InlineData("json-output")]
    public async Task PreserveDisabledSettingsAndLayerOverrides(string mode)
    {
        using InMemoryQpdfHost storage = CreateHost();
        Dictionary<string, object> disabled = new()
        {
            ["qdf"] = false, ["linearize"] = false, ["removeAcroform"] = false, ["removeStructure"] = false,
            ["coalesceContents"] = false, ["normalizeContent"] = "n", ["externalizeInlineImages"] = false,
            ["flattenRotation"] = false, ["generateAppearances"] = false, ["optimizeImages"] = false,
            ["removePageLabels"] = false, ["noOriginalObjectIds"] = false, ["keepInlineImages"] = false,
        };
        ObservingHost host = new(storage);
        QpdfExecutionResult result = await Execute(host, storage, mode, "job", disabled, []);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Dictionary<string, object> enabled = new(disabled) { ["coalesceContents"] = true, ["normalizeContent"] = "y" };
        Dictionary<string, object> settings = Settings(mode, enabled);
        storage.AddInput(QpdfPath.From("settings"), JsonSerializer.SerializeToUtf8Bytes(settings), QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("disabled"), JsonSerializer.SerializeToUtf8Bytes(disabled), QpdfInputOwnership.Transfer);
        result = await Qpdf.ExecuteAsync(host, new(["--job-json-file=settings", "--job-json-file=disabled"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(0, storage.OpenLeaseCount);
    }

    [Theory]
    [InlineData("inspect", "argv")]
    [InlineData("json-output", "argv")]
    [InlineData("inspect", "job")]
    [InlineData("json-output", "mixed")]
    public async Task ApplyDocumentJsonCatalogRemovals(string mode, string route)
    {
        using InMemoryQpdfHost storage = CreateHost();
        ObservingHost host = new(storage);
        Dictionary<string, object> removals = new() { ["removeAcroform"] = true, ["removeStructure"] = true };
        QpdfExecutionResult result;
        if (route == "mixed")
        {
            Dictionary<string, object> settings = Settings(mode, removals);
            settings["removeAcroform"] = false;
            storage.AddInput(QpdfPath.From("settings"), JsonSerializer.SerializeToUtf8Bytes(settings), QpdfInputOwnership.Transfer);
            result = await Qpdf.ExecuteAsync(host, new(["--job-json-file=settings", "--remove-acroform"]), CancellationToken.None);
            Assert.Equal(new[] { "settings", "input", "output" }, host.Inputs);
        }
        else
        {
            result = await Execute(host, storage, mode, route, removals, ["--remove-acroform", "--remove-structure"]);
        }
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        using JsonDocument json = JsonDocument.Parse(output);
        JsonElement table = json.RootElement.GetProperty("qpdf")[1];
        string root = table.GetProperty("trailer").GetProperty("value").GetProperty("/Root").GetString()
            ?? throw new InvalidOperationException();
        JsonElement catalog = table.GetProperty("obj:" + root).GetProperty("value");
        Assert.False(catalog.TryGetProperty("/AcroForm", out _));
        Assert.False(catalog.TryGetProperty("/StructTreeRoot", out _));
        Assert.Equal(0, storage.OpenLeaseCount);
    }

    [Theory]
    [InlineData("create", "argv")]
    [InlineData("create", "job")]
    [InlineData("create", "mixed")]
    [InlineData("update", "argv")]
    [InlineData("update", "job")]
    [InlineData("update", "mixed")]
    public async Task PreserveUnchangedThresholdsAndDisabledPruning(string mode, string route)
    {
        using InMemoryQpdfHost storage = CreateHost();
        QpdfExecutionResult result = await Execute(new(storage), storage, mode, route, new()
        {
            ["iiMinBytes"] = "1024", ["oiMinWidth"] = "128", ["oiMinHeight"] = "128",
            ["oiMinArea"] = "16384", ["removeUnreferencedResources"] = "no",
        }, ["--ii-min-bytes=1024", "--oi-min-width=128", "--oi-min-height=128", "--oi-min-area=16384", "--remove-unreferenced-resources=no"]);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(output), StringComparison.Ordinal);
        Assert.Equal(0, storage.OpenLeaseCount);
    }

    [Fact]
    public async Task DisabledJsonInputDoesNotRestrictOrdinaryPdfRewriting()
    {
        using InMemoryQpdfHost storage = CreateHost();
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(storage,
            """{"inputFile":"input","outputFile":"output","jsonInput":false,"qdf":true}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.Contains("%QDF-", Encoding.ASCII.GetString(output), StringComparison.Ordinal);
        Assert.Equal(0, storage.OpenLeaseCount);
    }

    private static InMemoryQpdfHost CreateHost()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Pdf, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("stamp"), Pdf, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("graph"), Encoding.UTF8.GetBytes(Graph), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("update"), Encoding.UTF8.GetBytes(Update), QpdfInputOwnership.Transfer);
        return host;
    }

    private static async Task Seed(InMemoryQpdfHost host)
    {
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
        await prior.CommitAsync(CancellationToken.None);
    }

    private static Dictionary<string, object> Settings(string mode, Dictionary<string, object> extra)
    {
        Dictionary<string, object> settings = new(extra) { ["inputFile"] = mode == "create" ? "graph" : "input", ["outputFile"] = "output" };
        if (mode == "create") { settings["jsonInput"] = true; }
        else if (mode == "update") { settings["updateFromJson"] = "update"; }
        else if (mode == "bound-inspect")
        { settings["updateFromJson"] = "update"; settings["json"] = "2"; }
        else { settings[mode == "inspect" ? "json" : "jsonOutput"] = "2"; }
        return settings;
    }

    private static Task<QpdfExecutionResult> Execute(ObservingHost host, InMemoryQpdfHost storage,
        string mode, string route, Dictionary<string, object> extra, string[] flags)
    {
        Dictionary<string, object> settings = Settings(mode, extra);
        if (route == "job") { return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None).AsTask(); }
        if (route == "mixed")
        {
            storage.AddInput(QpdfPath.From("settings"), JsonSerializer.SerializeToUtf8Bytes(settings), QpdfInputOwnership.Transfer);
            return Qpdf.ExecuteAsync(host, new(["--job-json-file=settings"]), CancellationToken.None).AsTask();
        }
        string operation = mode == "create" ? "--json-input" : mode == "update" ? "--update-from-json=update"
            : mode is "inspect" or "bound-inspect" ? "--json=2" : "--json-output=2";
        List<string> arguments = [mode == "create" ? "graph" : "input", operation];
        if (mode == "bound-inspect") { arguments.Add("--update-from-json=update"); }
        arguments.AddRange(flags); arguments.AddRange(["--", "output"]);
        return Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None).AsTask();
    }

    private sealed class ObservingHost(InMemoryQpdfHost storage) : IQpdfHost
    {
        internal List<string> Inputs { get; } = [];
        internal int Outputs { get; private set; }
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        { Inputs.Add(path.Value); return storage.OpenInputAsync(path, token); }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Outputs++; return storage.CreateOutputAsync(path, token); }
    }
}
