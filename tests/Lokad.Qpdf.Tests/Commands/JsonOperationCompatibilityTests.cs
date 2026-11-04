using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

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
    private static readonly byte[] Pdf = CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R /StructTreeRoot 6 0 R >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
        (4, "null", null), (5, "<< /Fields [] >>", null), (6, "<< /Type /StructTreeRoot /K [] >>", null)], 1);

    public static IEnumerable<object[]> RejectedRequests()
    {
        (string Key, object Value, string[] Arguments)[] requests = [
            ("qdf", true, ["--qdf"]), ("linearize", true, ["--linearize"]),
            ("removeAcroform", true, ["--remove-acroform"]), ("removeStructure", true, ["--remove-structure"]),
            ("coalesceContents", true, ["--coalesce-contents"]), ("normalizeContent", "y", ["--normalize-content=y"]),
            ("externalizeInlineImages", true, ["--externalize-inline-images"]), ("iiMinBytes", "17", ["--ii-min-bytes=17"]),
            ("removeUnreferencedResources", "yes", ["--remove-unreferenced-resources=yes"]),
            ("flattenRotation", true, ["--flatten-rotation"]), ("flattenAnnotations", "all", ["--flatten-annotations=all"]),
            ("generateAppearances", true, ["--generate-appearances"]), ("optimizeImages", true, ["--optimize-images"]),
            ("oiMinWidth", "17", ["--oi-min-width=17"]), ("keepInlineImages", true, ["--keep-inline-images"]),
            ("jpegQuality", "75", ["--jpeg-quality=75"]), ("removePageLabels", true, ["--remove-page-labels"]),
            ("setPageLabels", new[] { "1:D/5/Label" }, ["--set-page-labels", "1:D/5/Label", "--"]),
            ("overlay", new { file = "stamp" }, ["--overlay", "stamp", "--"]),
            ("underlay", new { file = "stamp" }, ["--underlay", "stamp", "--"]),
            ("linearizePass1", "pass1", ["--linearize-pass1=pass1"]),
            ("noOriginalObjectIds", true, ["--no-original-object-ids"]),
        ];
        foreach ((string key, object value, string[] arguments) in requests)
        foreach (string mode in new[] { "create", "update", "inspect", "json-output" })
        foreach (string route in new[] { "argv", "job", "mixed" })
        {
            // Inspection retains its documented writer-ignore policy. These
            // layout/geometry combinations already reject in the argv route.
            if (mode is "inspect" or "json-output"
                && key is not ("qdf" or "linearize" or "flattenRotation" or "flattenAnnotations"
                    or "generateAppearances" or "overlay" or "underlay")) { continue; }
            yield return [key, value, arguments, mode, route];
        }
    }

    [Theory]
    [MemberData(nameof(RejectedRequests))]
    public async Task RejectUnsupportedOperationsBeforeDocumentAccess(
        string key, object value, string[] arguments, string mode, string route)
    {
        using InMemoryQpdfHost storage = CreateHost();
        await Seed(storage);
        ObservingHost host = new(storage);
        QpdfExecutionResult result = await Execute(host, storage, mode, route, new() { [key] = value }, arguments);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments);
        Assert.All(host.Inputs, path => Assert.Equal("settings", path));
        Assert.Equal(0, host.Outputs);
        Assert.Equal(0, storage.OpenLeaseCount);
        Assert.Equal(0, result.Counters.OpenHandles);
        Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] prior));
        Assert.Equal("prior"u8.ToArray(), prior);
    }

    [Theory]
    [InlineData("create", "argv")]
    [InlineData("create", "job")]
    [InlineData("create", "mixed")]
    [InlineData("update", "argv")]
    [InlineData("update", "job")]
    [InlineData("update", "mixed")]
    public async Task PreserveSupportedImportWriterAndReportingSettings(string mode, string route)
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

    [Fact]
    public async Task PreserveDocumentedCheckingWriterIgnores()
    {
        using InMemoryQpdfHost storage = CreateHost();
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(storage,
            """{"inputFile":"input","check":"","showObject":"1","qdf":true,"removeAcroform":true,"removeStructure":true}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("/AcroForm", StringComparison.Ordinal)
            && d.Message.Contains("/StructTreeRoot", StringComparison.Ordinal));
        Assert.False(storage.TryGetOutput(QpdfPath.From("output"), out _));
        Assert.Equal(0, storage.OpenLeaseCount);
    }

    [Theory]
    [InlineData("inspect", "argv")]
    [InlineData("inspect", "job")]
    [InlineData("inspect", "mixed")]
    [InlineData("json-output", "argv")]
    [InlineData("json-output", "job")]
    [InlineData("json-output", "mixed")]
    public async Task PreserveDocumentJsonWriterIgnores(string mode, string route)
    {
        using InMemoryQpdfHost storage = CreateHost();
        QpdfExecutionResult result = await Execute(new(storage), storage, mode, route,
            new() { ["removeAcroform"] = true, ["removeStructure"] = true }, ["--remove-acroform", "--remove-structure"]);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        using JsonDocument json = JsonDocument.Parse(output);
        JsonElement catalog = json.RootElement.GetProperty("qpdf")[1].GetProperty("obj:1 0 R").GetProperty("value");
        Assert.True(catalog.TryGetProperty("/AcroForm", out _));
        Assert.True(catalog.TryGetProperty("/StructTreeRoot", out _));
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
            : mode == "inspect" ? "--json=2" : "--json-output=2";
        List<string> arguments = [mode == "create" ? "graph" : "input", operation];
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
