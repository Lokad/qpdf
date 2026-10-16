using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonImportTests
{
    private static readonly byte[] PageData = Encoding.ASCII.GetBytes("BT (Page) Tj ET");

    private static byte[] BuildPdf(string catalogExtra, List<(string Dict, byte[]? StreamData)> extras)
    {
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R" + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            "<< /Length 15 >>",
        };
        foreach ((string dict, byte[]? _) in extras)
        {
            bodies.Add(dict);
        }

        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[]? streamData = null;
            if (number == 4)
            {
                streamData = PageData;
            }
            else if (number > 4)
            {
                streamData = extras[number - 5].StreamData;
            }

            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj\n" + bodies[i] + "\n");
            if (streamData is null)
            {
                byte[] close = Encoding.ASCII.GetBytes("endobj\n");
                offsets.Add(position);
                position += open.Length + close.Length;
                parts.Add(open);
                parts.Add(close);
                continue;
            }

            byte[] streamOpen = Encoding.ASCII.GetBytes("stream\n");
            byte[] streamClose = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
            offsets.Add(position);
            position += open.Length + streamOpen.Length + streamData.Length + streamClose.Length;
            parts.Add(open);
            parts.Add(streamOpen);
            parts.Add(streamData);
            parts.Add(streamClose);
        }

        int size = bodies.Count + 1;
        StringBuilder xref = new();
        xref.Append("xref\n0 " + size.ToString() + "\n");
        xref.Append("0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            xref.Append(offset.ToString("D10") + " 00000 n \n");
        }

        xref.Append("trailer\n<< /Size " + size.ToString() + " /Root 1 0 R >>\n");
        xref.Append("startxref\n" + position.ToString() + "\n%%EOF\n");
        parts.Add(Encoding.ASCII.GetBytes(xref.ToString()));
        int total = 0;
        foreach (byte[] part in parts)
        {
            total += part.Length;
        }

        byte[] output = new byte[total];
        int cursor = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, output, cursor, part.Length);
            cursor += part.Length;
        }

        return output;
    }

    private static byte[] PlainFixture()
    {
        return BuildPdf(string.Empty, new List<(string Dict, byte[]? StreamData)>());
    }

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static async Task<string> ExportAsync(InMemoryQpdfHost host, params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.json"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] json));
        return Encoding.UTF8.GetString(json);
    }

    private static async Task<QpdfExecutionResult> ImportAsync(InMemoryQpdfHost host, params string[] flags)
    {
        List<string> argv = new() { "inputs/a.json" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.pdf"]);
        return await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
    }

    private static async Task<QpdfExecutionResult> UpdateAsync(InMemoryQpdfHost host, params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.pdf"]);
        return await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
    }

    private static string MutateObjects(string json, Action<JsonObject> mutate)
    {
        JsonNode? root = JsonNode.Parse(json);
        Assert.NotNull(root);
        JsonObject objects = root["qpdf"]![1]!.AsObject();
        mutate(objects);
        return root.ToJsonString();
    }

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
    }
    [Fact]
    public async Task CreateRoundTrip()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json", "--json-stream-data=inline");
        InMemoryQpdfHost importHost = new();
        importHost.AddInput(QpdfPath.From("inputs/a.json"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await ImportAsync(importHost, "--json-input");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(importHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        InMemoryQpdfHost reopenHost = new();
        reopenHost.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        string again = await ExportAsync(reopenHost, "--json", "--json-stream-data=inline");
        AssertJsonEqualIgnoringIds(json, again);
    }

    [Fact]
    public async Task CreateYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json", "--json-stream-data=inline");
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.json"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.json", "--json-input", "--", "outputs/a.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }

    [Fact]
    public async Task UpdateYieldsToExhaustedBudget()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json", "--json-stream-data=inline");
        string changed = MutateObjects(json, objects =>
        {
            objects["obj:1 0 R"]!["value"]!.AsObject()["/Custom"] = "u:New";
        });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/u.json"), Encoding.UTF8.GetBytes(changed), QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--update-from-json=inputs/u.json", "--", "outputs/a.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] _));
    }
    private static void AssertJsonEqualIgnoringIds(string expected, string actual)
    {
        using JsonDocument left = JsonDocument.Parse(expected);
        using JsonDocument right = JsonDocument.Parse(actual);
        Assert.True(JsonEqualMaskingIds(left.RootElement, right.RootElement, false));
    }

    private static bool JsonEqualMaskingIds(JsonElement left, JsonElement right, bool maskStrings)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
            {
                List<string> names = new();
                foreach (JsonProperty property in left.EnumerateObject())
                {
                    if (property.Name != "/ID")
                    {
                        names.Add(property.Name);
                    }
                }

                int counterparts = 0;
                foreach (JsonProperty candidate in right.EnumerateObject())
                {
                    if (candidate.Name != "/ID")
                    {
                        counterparts += 1;
                    }
                }

                if (names.Count != counterparts)
                {
                    return false;
                }

                foreach (string name in names)
                {
                    if (!right.TryGetProperty(name, out JsonElement counterpart))
                    {
                        return false;
                    }

                    if (!JsonEqualMaskingIds(left.GetProperty(name), counterpart, maskStrings || name == "/ID"))
                    {
                        return false;
                    }
                }

                return true;
            }

            case JsonValueKind.Array:
            {
                if (left.GetArrayLength() != right.GetArrayLength())
                {
                    return false;
                }

                for (int i = 0; i < left.GetArrayLength(); i++)
                {
                    if (!JsonEqualMaskingIds(left[i], right[i], maskStrings))
                    {
                        return false;
                    }
                }

                return true;
            }

            case JsonValueKind.String:
                return maskStrings || left.GetString() == right.GetString();
            default:
                return left.GetRawText() == right.GetRawText();
        }
    }

    private static bool IsUpperHex(string text)
    {
        foreach (char c in text)
        {
            bool digit = c >= (char)48 && c <= (char)57;
            bool upper = c >= (char)65 && c <= (char)70;
            if (!digit && !upper)
            {
                return false;
            }
        }

        return true;
    }
    [Fact]
    public async Task CreateFromMinimalJson()
    {
        string json = "{\"qpdf\": [{\"jsonversion\": 2, \"pdfversion\": \"1.4\"}, {\"obj:9 0 R\": {\"value\": {\"/Type\": \"/Catalog\", \"/Pages\": \"8 0 R\"}}, \"obj:8 0 R\": {\"value\": {\"/Type\": \"/Pages\", \"/Kids\": [\"7 0 R\"], \"/Count\": 1}}, \"obj:7 0 R\": {\"value\": {\"/Type\": \"/Page\", \"/Parent\": \"8 0 R\", \"/MediaBox\": [0, 0, 612, 792], \"/Contents\": \"6 0 R\", \"/Resources\": {}}}, \"obj:6 0 R\": {\"stream\": {\"data\": \"QlQgKFBhZ2UpIFRqIEVU\", \"dict\": {}}}, \"trailer\": {\"value\": {\"/Root\": \"9 0 R\"}}}]}";
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.json"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await ImportAsync(host, "--json-input");
        foreach (QpdfDiagnostic dd in result.Diagnostics) { System.Console.WriteLine("DIAG:" + dd.Code + ":" + dd.Message); }
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        string text = Encoding.ASCII.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", text, StringComparison.Ordinal);
        InMemoryQpdfHost reopenHost = new();
        reopenHost.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        string again = await ExportAsync(reopenHost, "--json", "--json-stream-data=inline");
        using JsonDocument document = JsonDocument.Parse(again);
        JsonElement objects = document.RootElement.GetProperty("qpdf")[1];
        Assert.True(objects.TryGetProperty("obj:1 0 R", out JsonElement catalog));
        Assert.Equal("/Catalog", catalog.GetProperty("value").GetProperty("/Type").GetString());
        Assert.True(objects.TryGetProperty("trailer", out _));
    }

    [Fact]
    public async Task CreateReadsDatafiles()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json", "--json-stream-data=file");
        InMemoryQpdfHost importHost = new();
        importHost.AddInput(QpdfPath.From("inputs/a.json"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
        Assert.True(exportHost.TryGetOutput(QpdfPath.From("outputs/a.json-4"), out byte[] sidecar));
        importHost.AddInput(QpdfPath.From("outputs/a.json-4"), sidecar, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            importHost,
            new QpdfCommandInvocation(["inputs/a.json", "--json-input", "--", "outputs/b.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(importHost.TryGetOutput(QpdfPath.From("outputs/b.pdf"), out byte[] pdf));
        InMemoryQpdfHost reopenHost = new();
        reopenHost.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        string again = await ExportAsync(reopenHost, "--json", "--json-stream-data=inline");
        using JsonDocument document = JsonDocument.Parse(again);
        JsonElement objects = document.RootElement.GetProperty("qpdf")[1];
        Assert.Equal(Convert.ToBase64String(PageData), objects.GetProperty("obj:4 0 R").GetProperty("stream").GetProperty("data").GetString());
    }

    [Fact]
    public async Task CreateAppliesNameAndStringForms()
    {
        string json = "{\"qpdf\": [{\"jsonversion\": 2, \"pdfversion\": \"1.7\"}, {\"obj:1 0 R\": {\"value\": {\"/Type\": \"/Catalog\", \"/Pages\": \"2 0 R\", \"/Slash\": \"/text/plain\", \"/Binary\": \"n:/one#a0two\", \"/Title\": \"u:Hi\", \"/Raw\": \"b:ff00\", \"/Real\": 0.5, \"/Null\": null, \"/Flag\": true}}, \"obj:2 0 R\": {\"value\": {\"/Type\": \"/Pages\", \"/Kids\": [], \"/Count\": 0}}, \"trailer\": {\"value\": {\"/Root\": \"1 0 R\"}}}]}";
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.json"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await ImportAsync(host, "--json-input");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("/text#2Fplain", text, StringComparison.Ordinal);
        Assert.Contains("/one#A0two", text, StringComparison.Ordinal);
        Assert.Contains("(Hi)", text, StringComparison.Ordinal);
        Assert.Contains("<FF00>", text, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("{}")]
    [InlineData("{\"pages\": []}")]
    [InlineData("{\"qpdf\": []}")]
    [InlineData("{\"qpdf\": [{}, {}, {}]}")]
    [InlineData("{\"qpdf\": [{\"jsonversion\": 1, \"pdfversion\": \"1.4\"}, {}]}")]
    [InlineData("{\"qpdf\": [{\"jsonversion\": 2}, {}]}")]
    [InlineData("{\"qpdf\": [{\"jsonversion\": 2, \"pdfversion\": \"1.4\"}, {}]}")]
    [InlineData("{\"qpdf\": [{\"jsonversion\": 2, \"pdfversion\": \"9.9\"}, {}]}")]
    [InlineData("{\"qpdf\": [{\"jsonversion\": 2, \"pdfversion\": \"1.4\"}, {\"trailer\": {\"value\": {}}}]}")]
    [InlineData("{\"qpdf\": [{\"jsonversion\": 2, \"pdfversion\": \"1.4\"}, {\"bogus\": {\"value\": 1}, \"trailer\": {\"value\": {\"/Root\": \"1 0 R\"}}}]}")]
    [InlineData("{\"qpdf\": [{\"jsonversion\": 2, \"pdfversion\": \"1.4\"}, {\"obj:1 0 R\": {\"value\": \"2 0 R\"}, \"trailer\": {\"value\": {\"/Root\": \"1 0 R\"}}}]}")]
    [InlineData("{\"qpdf\": [{\"jsonversion\": 2, \"pdfversion\": \"1.4\"}, {\"obj:1 0 R\": {\"value\": 1, \"stream\": {\"dict\": {}}}, \"trailer\": {\"value\": {\"/Root\": \"1 0 R\"}}}]}")]
    [InlineData("{\"qpdf\": [{\"jsonversion\": 2, \"pdfversion\": \"1.4\"}, {\"obj:1 0 R\": {\"stream\": {\"dict\": {}}}, \"trailer\": {\"value\": {\"/Root\": \"1 0 R\"}}}]}")]
    [InlineData("{\"qpdf\": [{\"jsonversion\": 2, \"pdfversion\": \"1.4\"}, {\"obj:1 0 R\": {\"stream\": {\"dict\": {}, \"data\": \"@@@\", \"datafile\": \"x\"}}, \"trailer\": {\"value\": {\"/Root\": \"1 0 R\"}}}]}")]
    [InlineData("{\"qpdf\": [{\"jsonversion\": 2, \"pdfversion\": \"1.4\"}, {\"obj:1 0 R\": {\"stream\": {\"dict\": {}, \"data\": 5}}, \"trailer\": {\"value\": {\"/Root\": \"1 0 R\"}}}]}")]
    [InlineData("{\"qpdf\": [{\"jsonversion\": 2, \"pdfversion\": \"1.4\"}, {\"obj:1 0 R\": {\"value\": {\"/Type\": \"/Catalog\"}}, \"trailer\": {\"stream\": {\"dict\": {}}}}]}")]
    [InlineData("{\"qpdf\": [{\"jsonversion\": 2, \"pdfversion\": \"1.4\"}, {\"obj:1 0 R\": {\"value\": {\"/Type\": \"/Catalog\"}}, \"trailer\": {\"value\": 5}}]}")]
    [InlineData("{\"qpdf\": [{\"jsonversion\": 2, \"pdfversion\": \"1.4\"}, {\"obj:1 1 R\": {\"value\": 1}, \"trailer\": {\"value\": {\"/Root\": \"1 0 R\"}}}]}")]
    [InlineData("{\"qpdf\": [{\"jsonversion\": 2, \"pdfversion\": \"1.4\"}, {\"obj:1 0 R\": {\"value\": \"plain\"}, \"trailer\": {\"value\": {\"/Root\": \"1 0 R\"}}}]}")]
    [InlineData("{\"qpdf\": [{\"jsonversion\": 2, \"pdfversion\": \"1.4\"}, {\"obj:1 0 R\": {\"value\": {\"/Type\": \"/Catalog\"}}, \"trailer\": {\"value\": {}}}]}")]
    public async Task MalformedJsonFails(string payload)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.json"), Encoding.UTF8.GetBytes(payload), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await ImportAsync(host, "--json-input");
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task MissingDatafileFails()
    {
        string json = "{\"qpdf\": [{\"jsonversion\": 2, \"pdfversion\": \"1.4\"}, {\"obj:1 0 R\": {\"value\": {\"/Type\": \"/Catalog\", \"/Pages\": \"2 0 R\"}}, \"obj:2 0 R\": {\"value\": {\"/Type\": \"/Pages\", \"/Kids\": [], \"/Count\": 0}}, \"trailer\": {\"value\": {\"/Root\": \"1 0 R\"}}, \"obj:3 0 R\": {\"stream\": {\"dict\": {}, \"datafile\": \"missing.bin\"}}}]}";
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.json"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await ImportAsync(host, "--json-input");
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
    }

    [Theory]
    [InlineData("--pages", "1")]
    [InlineData("--check")]
    [InlineData("--json")]
    [InlineData("--list-attachments")]
    [InlineData("--add-attachment", "files/n.bin", "--")]
    [InlineData("--update-from-json=other.json")]
    [InlineData("--replace-input")]
    public async Task ImportCombinationsFail(params string[] flags)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.json"), Encoding.UTF8.GetBytes("{}"), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("files/n.bin"), [1, 2, 3], QpdfInputOwnership.Copy);
        List<string> argv = new() { "inputs/a.json", "--json-input" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task MissingOutputFails()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.json"), Encoding.UTF8.GetBytes("{}"), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.json", "--json-input"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }
    [Fact]
    public async Task JobCreateRoundTrip()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json", "--json-stream-data=inline");
        InMemoryQpdfHost argvHost = new();
        argvHost.AddInput(QpdfPath.From("inputs/a.json"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
        QpdfExecutionResult argv = await ImportAsync(argvHost, "--json-input");
        Assert.Equal(QpdfExitCode.Success, argv.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));
        InMemoryQpdfHost jobHost = new();
        jobHost.AddInput(QpdfPath.From("inputs/a.json"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.json\", \"outputFile\": \"outputs/a.pdf\", \"jsonInput\": true}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.json\", \"outputFile\": \"outputs/a.pdf\", \"jsonInput\": \"yes\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.json\", \"outputFile\": \"outputs/a.pdf\", \"jsonInput\": true, \"pages\": [{\"file\": \".\", \"range\": \"1\"}]}")]
    [InlineData("{\"inputFile\": \"inputs/a.json\", \"outputFile\": \"outputs/a.pdf\", \"jsonInput\": true, \"listAttachments\": true}")]
    [InlineData("{\"inputFile\": \"inputs/a.json\", \"outputFile\": \"outputs/a.pdf\", \"jsonInput\": true, \"json\": \"2\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.json\", \"outputFile\": \"outputs/a.pdf\", \"jsonInput\": true, \"jsonOutput\": \"latest\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.json\", \"outputFile\": \"outputs/a.pdf\", \"jsonInput\": true, \"addAttachment\": [{\"file\": \"f\"}]}")]
    [InlineData("{\"inputFile\": \"inputs/a.json\", \"outputFile\": \"outputs/a.pdf\", \"updateFromJson\": \"other.json\"}")]
    public async Task JobMalformedFails(string jobJson)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.json"), Encoding.UTF8.GetBytes("{}"), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task TypedCreateRoundTrip()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json", "--json-stream-data=inline");
        InMemoryQpdfHost typedHost = new();
        typedHost.AddInput(QpdfPath.From("inputs/a.json"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
        QpdfJsonImportResult result = await Qpdf.CreatePdfFromJsonAsync(
            typedHost,
            QpdfPath.From("inputs/a.json"),
            QpdfPath.From("outputs/a.pdf"),
            CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        Assert.True(typedHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        InMemoryQpdfHost reopenHost = new();
        reopenHost.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        string again = await ExportAsync(reopenHost, "--json", "--json-stream-data=inline");
        AssertJsonEqualIgnoringIds(json, again);
    }

    [Fact]
    public async Task TypedValidation()
    {
        InMemoryQpdfHost host = new();
        await Assert.ThrowsAsync<ArgumentNullException>(() => Qpdf.CreatePdfFromJsonAsync(
            null!, QpdfPath.From("inputs/a.json"), QpdfPath.From("outputs/a.pdf"), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => Qpdf.CreatePdfFromJsonAsync(
            host, QpdfPath.From("inputs/a.json"), QpdfPath.From("outputs/a.pdf"), null!, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task CreateRespectsVersions()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json", "--json-key=qpdf", "--json-stream-data=inline");
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.json"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.json", "--json-input", "--force-version=1.7", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.StartsWith("%PDF-1.7", Encoding.ASCII.GetString(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateChangesValues()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json", "--json-stream-data=inline");
        string changed = MutateObjects(json, objects =>
        {
            objects["obj:1 0 R"]!["value"]!.AsObject()["/Custom"] = "u:New";
        });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/u.json"), Encoding.UTF8.GetBytes(changed), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--update-from-json=inputs/u.json", "--", "outputs/a.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        InMemoryQpdfHost reopenHost = new();
        reopenHost.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        string again = await ExportAsync(reopenHost, "--json", "--json-key=qpdf");
        using JsonDocument document = JsonDocument.Parse(again);
        JsonElement objects = document.RootElement.GetProperty("qpdf")[1];
        Assert.True(objects.TryGetProperty("obj:1 0 R", out JsonElement catalog));
        Assert.Equal("u:New", catalog.GetProperty("value").GetProperty("/Custom").GetString());
        Assert.True(objects.TryGetProperty("obj:4 0 R", out _));
    }

    [Fact]
    public async Task UpdateKeepsStreamData()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json");
        string changed = MutateObjects(json, objects =>
        {
            objects["obj:4 0 R"]!["stream"]!.AsObject()["dict"]!.AsObject()["/Custom"] = "u:X";
        });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/u.json"), Encoding.UTF8.GetBytes(changed), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await UpdateAsync(host, "--update-from-json=inputs/u.json");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        InMemoryQpdfHost reopenHost = new();
        reopenHost.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        string again = await ExportAsync(reopenHost, "--json", "--json-stream-data=inline");
        using JsonDocument document = JsonDocument.Parse(again);
        JsonElement stream = document.RootElement.GetProperty("qpdf")[1].GetProperty("obj:4 0 R").GetProperty("stream");
        Assert.Equal(Convert.ToBase64String(PageData), stream.GetProperty("data").GetString());
        Assert.Equal("u:X", stream.GetProperty("dict").GetProperty("/Custom").GetString());
    }

    [Fact]
    public async Task UpdateReplacesStreamData()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json");
        byte[] replacement = Encoding.ASCII.GetBytes("Replacement bytes.");
        string changed = MutateObjects(json, objects =>
        {
            objects["obj:4 0 R"]!["stream"]!.AsObject()["data"] = Convert.ToBase64String(replacement);
        });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/u.json"), Encoding.UTF8.GetBytes(changed), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await UpdateAsync(host, "--update-from-json=inputs/u.json");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.Contains("Replacement bytes.", Encoding.ASCII.GetString(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAddsObjects()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json", "--json-key=qpdf");
        string changed = MutateObjects(json, objects =>
        {
            JsonObject added = new();
            JsonObject inner = new();
            inner["/Marker"] = "u:added";
            added["value"] = inner;
            objects["obj:9 0 R"] = added;
        });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/u.json"), Encoding.UTF8.GetBytes(changed), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await UpdateAsync(host, "--update-from-json=inputs/u.json");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        InMemoryQpdfHost reopenHost = new();
        reopenHost.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        string again = await ExportAsync(reopenHost, "--json", "--json-key=qpdf");
        using JsonDocument document = JsonDocument.Parse(again);
        bool found = false;
        foreach (JsonProperty property in document.RootElement.GetProperty("qpdf")[1].EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("value", out JsonElement value)
                && value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("/Marker", out JsonElement marker)
                && marker.GetString() == "u:added")
            {
                found = true;
            }
        }

        Assert.True(found);
    }

    [Fact]
    public async Task UpdateWithoutTrailerKeeps()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json", "--json-key=qpdf");
        string changed = MutateObjects(json, objects =>
        {
            objects.Remove("trailer");
        });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/u.json"), Encoding.UTF8.GetBytes(changed), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await UpdateAsync(host, "--update-from-json=inputs/u.json");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        InMemoryQpdfHost reopenHost = new();
        reopenHost.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        string again = await ExportAsync(reopenHost, "--json", "--json-key=qpdf");
        using JsonDocument document = JsonDocument.Parse(again);
        Assert.True(document.RootElement.GetProperty("qpdf")[1].TryGetProperty("trailer", out _));
    }
    [Fact]
    public async Task UpdateNoOpIsStable()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json", "--json-stream-data=inline");
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/u.json"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await UpdateAsync(host, "--update-from-json=inputs/u.json");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        InMemoryQpdfHost reopenHost = new();
        reopenHost.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        string again = await ExportAsync(reopenHost, "--json", "--json-stream-data=inline");
        AssertJsonEqualIgnoringIds(json, again);
    }

    [Fact]
    public async Task UpdateHonorsPdfVersion()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json", "--json-key=qpdf");
        string changed = MutateObjects(json, objects =>
        {
        });
        JsonNode? root = JsonNode.Parse(changed);
        Assert.NotNull(root);
        root["qpdf"]![0]!.AsObject()["pdfversion"] = "1.7";
        changed = root.ToJsonString();
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/u.json"), Encoding.UTF8.GetBytes(changed), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await UpdateAsync(host, "--update-from-json=inputs/u.json");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        Assert.StartsWith("%PDF-1.7", Encoding.ASCII.GetString(pdf), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--pages", "1")]
    [InlineData("--check")]
    [InlineData("--json")]
    [InlineData("--json-input")]
    [InlineData("--list-attachments")]
    [InlineData("--replace-input")]
    public async Task UpdateCombinationsFail(params string[] flags)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/u.json"), Encoding.UTF8.GetBytes("{}"), QpdfInputOwnership.Copy);
        List<string> argv = new() { "inputs/a.pdf", "--update-from-json=inputs/u.json" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.pdf"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task UpdateMissingOutputFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/u.json"), Encoding.UTF8.GetBytes("{}"), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--update-from-json=inputs/u.json"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task UpdatePushedAttributesFail()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json", "--json-key=qpdf");
        string changed = MutateObjects(json, objects =>
        {
        });
        JsonNode? root = JsonNode.Parse(changed);
        Assert.NotNull(root);
        JsonObject metaObject = root["qpdf"]![0]!.AsObject();
        metaObject["pushedinheritedpageresources"] = true;
        changed = root.ToJsonString();
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/u.json"), Encoding.UTF8.GetBytes(changed), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await UpdateAsync(host, "--update-from-json=inputs/u.json");
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }
    [Fact]
    public async Task JobUpdateRoundTrip()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json", "--json-stream-data=inline");
        string changed = MutateObjects(json, objects =>
        {
            objects["obj:1 0 R"]!["value"]!.AsObject()["/Custom"] = "u:New";
        });
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", PlainFixture());
        argvHost.AddInput(QpdfPath.From("inputs/u.json"), Encoding.UTF8.GetBytes(changed), QpdfInputOwnership.Copy);
        QpdfExecutionResult argv = await UpdateAsync(argvHost, "--update-from-json=inputs/u.json");
        Assert.Equal(QpdfExitCode.Success, argv.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));
        InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", PlainFixture());
        jobHost.AddInput(QpdfPath.From("inputs/u.json"), Encoding.UTF8.GetBytes(changed), QpdfInputOwnership.Copy);
        QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
            jobHost,
            "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"updateFromJson\": \"inputs/u.json\"}",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, job.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"updateFromJson\": \"\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"updateFromJson\": 5}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"updateFromJson\": \"inputs/u.json\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}]}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"updateFromJson\": \"inputs/u.json\", \"json\": \"2\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"updateFromJson\": \"inputs/u.json\", \"jsonInput\": true}")]
    [InlineData("{\"inputFile\": \"inputs/a.json\", \"outputFile\": \"outputs/a.pdf\", \"jsonInput\": true, \"updateFromJson\": \"inputs/u.json\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.pdf\", \"updateFromJson\": \"inputs/u.json\", \"addAttachment\": [{\"file\": \"f\"}]}")]
    public async Task JobUpdateMalformedFails(string jobJson)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("inputs/u.json"), Encoding.UTF8.GetBytes("{}"), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task TypedUpdateRoundTrip()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json", "--json-stream-data=inline");
        InMemoryQpdfHost typedHost = HostWith("inputs/a.pdf", PlainFixture());
        typedHost.AddInput(QpdfPath.From("inputs/u.json"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Copy);
        QpdfJsonImportResult result = await Qpdf.UpdatePdfFromJsonAsync(
            typedHost,
            QpdfPath.From("inputs/a.pdf"),
            QpdfPath.From("inputs/u.json"),
            QpdfPath.From("outputs/a.pdf"),
            CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        Assert.True(typedHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] pdf));
        InMemoryQpdfHost reopenHost = new();
        reopenHost.AddInput(QpdfPath.From("inputs/a.pdf"), pdf, QpdfInputOwnership.Copy);
        string again = await ExportAsync(reopenHost, "--json", "--json-stream-data=inline");
        AssertJsonEqualIgnoringIds(json, again);
    }

    [Fact]
    public async Task TypedUpdateValidation()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        await Assert.ThrowsAsync<ArgumentNullException>(() => Qpdf.UpdatePdfFromJsonAsync(
            null!, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("inputs/u.json"), QpdfPath.From("outputs/a.pdf"), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => Qpdf.UpdatePdfFromJsonAsync(
            host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("inputs/u.json"), QpdfPath.From("outputs/a.pdf"), null!, CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task InlineDataAdmittedBeforeDecoding()
    {
        InMemoryQpdfHost exportHost = HostWith("inputs/a.pdf", PlainFixture());
        string json = await ExportAsync(exportHost, "--json", "--json-stream-data=inline");
        string big = Convert.ToBase64String(new byte[30000]);
        string changed = MutateObjects(json, objects =>
        {
            objects["obj:4 0 R"]!["stream"]!["data"] = big;
        });
        using (JsonDocument admitted = JsonDocument.Parse(changed))
        {
            InMemoryQpdfHost host = new();
            QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 35000L };
            ResourceGovernor governor = new(limits, CancellationToken.None);
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            await Assert.ThrowsAsync<QpdfLimitException>(() => JsonImport.BuildCreateModelAsync(host, admitted, limits, governor, memory, CancellationToken.None).AsTask());
        }
        using (JsonDocument document = JsonDocument.Parse(changed))
        {
            InMemoryQpdfHost host = new();
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            ImportedModel model = await JsonImport.BuildCreateModelAsync(host, document, QpdfLimits.Default, governor, memory, CancellationToken.None);
            PdfStream stream = Assert.IsType<PdfStream>(model.Objects[4]);
            Assert.Equal(30000, stream.Data.Length);
        }
    }
}