using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonModesTests
{
    private static readonly byte[] PageData = Encoding.ASCII.GetBytes("BT (Page) Tj ET");

    private static readonly byte[] HexData = Encoding.ASCII.GetBytes("68656C6C6F");

    private static readonly byte[] RawData = [0xFF, 0xD8, 0x00, 0x01];

    private static byte[] BuildPdf(string catalogExtra, string pageExtra, List<(string Dict, byte[]? StreamData)> extras)
    {
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R" + catalogExtra + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >>" + pageExtra + " >>",
        };
        foreach ((string dict, byte[]? _) in extras)
        {
            bodies.Add(dict);
        }

        List<byte[]?> payloads = new() { null, null, null };
        foreach ((string _, byte[]? streamData) in extras)
        {
            payloads.Add(streamData);
        }

        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[]? streamData = payloads[i];
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

    private static byte[] StreamFixture()
    {
        return BuildPdf(
            string.Empty,
            string.Empty,
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Length 15 >>", PageData),
                ("<< /Length 10 /Filter /ASCIIHexDecode >>", HexData),
                ("<< /Length 4 /Filter /DCTDecode >>", RawData),
            });
    }

    private static byte[] FormFixture()
    {
        return BuildPdf(
            " /AcroForm 5 0 R",
            " /Annots [7 0 R 9 0 R 11 0 R]",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Length 15 >>", PageData),
                ("<< /Fields [6 0 R 8 0 R 10 0 R] /NeedAppearances true >>", null),
                ("<< /FT /Tx /T (field1) /V (hello) /Q 1 /Kids [7 0 R] >>", null),
                ("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /Parent 6 0 R /F 4 /AS /Off >>", null),
                ("<< /FT /Ch /T (ch) /TU (Pick one) /Opt [(x) (y)] /Kids [9 0 R] >>", null),
                ("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /Parent 8 0 R >>", null),
                ("<< /FT /Btn /T (btn) /Kids [11 0 R] >>", null),
                ("<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /Parent 10 0 R >>", null),
            });
    }

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static async Task<JsonDocument> InspectAsync(InMemoryQpdfHost host, params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.json"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] json));
        return JsonDocument.Parse(Encoding.UTF8.GetString(json));
    }

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
    }
    [Fact]
    public async Task InlineDecodesStreams()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        using JsonDocument document = await InspectAsync(host, "--json", "--json-stream-data=inline");
        JsonElement objects = document.RootElement.GetProperty("qpdf")[1];
        JsonElement decoded = objects.GetProperty("obj:5 0 R").GetProperty("stream");
        Assert.Equal("aGVsbG8=", decoded.GetProperty("data").GetString());
        Assert.False(decoded.GetProperty("dict").TryGetProperty("/Length", out _));
        Assert.False(decoded.GetProperty("dict").TryGetProperty("/Filter", out _));
        JsonElement raw = objects.GetProperty("obj:4 0 R").GetProperty("stream");
        Assert.Equal(Convert.ToBase64String(PageData), raw.GetProperty("data").GetString());
        Assert.False(raw.GetProperty("dict").TryGetProperty("/Length", out _));
    }

    [Fact]
    public async Task InlineFallsBackToRaw()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        using JsonDocument document = await InspectAsync(host, "--json", "--json-stream-data=inline");
        JsonElement objects = document.RootElement.GetProperty("qpdf")[1];
        JsonElement fallback = objects.GetProperty("obj:6 0 R").GetProperty("stream");
        Assert.Equal(Convert.ToBase64String(RawData), fallback.GetProperty("data").GetString());
        Assert.Equal("/DCTDecode", fallback.GetProperty("dict").GetProperty("/Filter").GetString());
        Assert.False(fallback.GetProperty("dict").TryGetProperty("/Length", out _));
    }

    [Fact]
    public async Task DecodeNoneKeepsRaw()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        using JsonDocument document = await InspectAsync(host, "--json", "--json-stream-data=inline", "--decode-level=none");
        JsonElement objects = document.RootElement.GetProperty("qpdf")[1];
        JsonElement stream = objects.GetProperty("obj:5 0 R").GetProperty("stream");
        Assert.Equal(Convert.ToBase64String(HexData), stream.GetProperty("data").GetString());
        Assert.Equal("/ASCIIHexDecode", stream.GetProperty("dict").GetProperty("/Filter").GetString());
        Assert.Equal("none", document.RootElement.GetProperty("parameters").GetProperty("decodelevel").GetString());
    }

    [Fact]
    public async Task FileModeWritesSidecars()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        using JsonDocument document = await InspectAsync(host, "--json", "--json-stream-data=file");
        JsonElement objects = document.RootElement.GetProperty("qpdf")[1];
        JsonElement stream = objects.GetProperty("obj:5 0 R").GetProperty("stream");
        Assert.Equal("outputs/a.json-5", stream.GetProperty("datafile").GetString());
        Assert.False(stream.TryGetProperty("data", out _));
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.json-5"), out byte[] sidecar));
        Assert.Equal(Encoding.ASCII.GetBytes("hello"), sidecar);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.json-4"), out byte[] content));
        Assert.Equal(PageData, content);
    }

    [Fact]
    public async Task FileModeHonorsPrefix()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        using JsonDocument document = await InspectAsync(host, "--json", "--json-stream-data=file", "--json-stream-prefix=pfx");
        JsonElement objects = document.RootElement.GetProperty("qpdf")[1];
        Assert.Equal("pfx-6", objects.GetProperty("obj:6 0 R").GetProperty("stream").GetProperty("datafile").GetString());
        Assert.True(host.TryGetOutput(QpdfPath.From("pfx-6"), out byte[] sidecar));
        Assert.Equal(RawData, sidecar);
    }

    [Fact]
    public async Task VersionOneIgnoresStreamData()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        using JsonDocument document = await InspectAsync(host, "--json=1", "--json-key=objects", "--json-stream-data=inline");
        JsonElement objects = document.RootElement.GetProperty("objects");
        JsonElement stream = objects.GetProperty("5 0");
        Assert.Equal(10, stream.GetProperty("/Length").GetInt32());
        Assert.False(stream.TryGetProperty("data", out _));
    }

    [Theory]
    [InlineData("--json-stream-data=banana")]
    [InlineData("--json-stream-data=inline", "--json-stream-data=file")]
    [InlineData("--json-stream-prefix=a", "--json-stream-prefix=b")]
    public async Task BadStreamOptionsFail(params string[] flags)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        List<string> argv = new() { "inputs/a.pdf", "--json" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.json"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task StrayStreamOptionsFail()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--json-stream-data=file", "--", "outputs/a.json"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }
    [Fact]
    public async Task ObjectFilterSelectsObjects()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        using JsonDocument document = await InspectAsync(host, "--json", "--json-key=qpdf", "--json-object=4");
        JsonElement objects = document.RootElement.GetProperty("qpdf")[1];
        Assert.True(objects.TryGetProperty("obj:4 0 R", out _));
        Assert.False(objects.TryGetProperty("obj:1 0 R", out _));
        Assert.False(objects.TryGetProperty("trailer", out _));
    }

    [Fact]
    public async Task ObjectFilterSelectsTrailer()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        using JsonDocument document = await InspectAsync(host, "--json", "--json-key=qpdf", "--json-object=trailer", "--json-object=4,0");
        JsonElement objects = document.RootElement.GetProperty("qpdf")[1];
        Assert.True(objects.TryGetProperty("obj:4 0 R", out _));
        Assert.True(objects.TryGetProperty("trailer", out _));
        Assert.False(objects.TryGetProperty("obj:5 0 R", out _));
    }

    [Fact]
    public async Task ObjectFilterAppliesToVersionOne()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        using JsonDocument document = await InspectAsync(host, "--json=1", "--json-key=objects", "--json-key=objectinfo", "--json-object=5");
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("objects").TryGetProperty("5 0", out _));
        Assert.False(root.GetProperty("objects").TryGetProperty("4 0", out _));
        Assert.False(root.GetProperty("objects").TryGetProperty("trailer", out _));
        Assert.True(root.GetProperty("objectinfo").TryGetProperty("5 0", out _));
        Assert.False(root.GetProperty("objectinfo").TryGetProperty("4 0", out _));
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("1,2,3")]
    [InlineData("4,")]
    [InlineData("")]
    public async Task BadObjectSelectorsFail(string selector)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--json", "--json-object=" + selector, "--", "outputs/a.json"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task JsonOutputIsMinimal()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        using JsonDocument document = await InspectAsync(host, "--json-output");
        JsonElement root = document.RootElement;
        Assert.False(root.TryGetProperty("version", out _));
        Assert.False(root.TryGetProperty("parameters", out _));
        JsonElement objects = root.GetProperty("qpdf")[1];
        Assert.True(objects.TryGetProperty("obj:4 0 R", out JsonElement stream));
        Assert.True(stream.GetProperty("stream").TryGetProperty("data", out _));
        Assert.False(root.TryGetProperty("pages", out _));
    }

    [Fact]
    public async Task JsonOutputAcceptsKeys()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        using JsonDocument document = await InspectAsync(host, "--json-output", "--json-key=pages");
        JsonElement root = document.RootElement;
        Assert.False(root.TryGetProperty("version", out _));
        Assert.True(root.TryGetProperty("qpdf", out _));
        Assert.True(root.TryGetProperty("pages", out _));
    }

    [Theory]
    [InlineData("--json-output=1")]
    [InlineData("--json=1", "--json-output")]
    public async Task JsonOutputRejectsVersionOne(params string[] flags)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.json"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }
    [Fact]
    public async Task AcroformReportsFields()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", FormFixture());
        using JsonDocument document = await InspectAsync(host, "--json", "--json-key=acroform");
        JsonElement acroform = document.RootElement.GetProperty("acroform");
        Assert.Equal(JsonValueKind.True, acroform.GetProperty("hasacroform").ValueKind);
        Assert.Equal(JsonValueKind.True, acroform.GetProperty("needappearances").ValueKind);
        JsonElement fields = acroform.GetProperty("fields");
        Assert.Equal(3, fields.GetArrayLength());
        JsonElement text = fields[0];
        Assert.Equal("6 0 R", text.GetProperty("object").GetString());
        Assert.Equal(JsonValueKind.Null, text.GetProperty("parent").ValueKind);
        Assert.Equal(1, text.GetProperty("pageposfrom1").GetInt32());
        Assert.Equal("/Tx", text.GetProperty("fieldtype").GetString());
        Assert.Equal(0, text.GetProperty("fieldflags").GetInt32());
        Assert.Equal("field1", text.GetProperty("fullname").GetString());
        Assert.Equal("field1", text.GetProperty("partialname").GetString());
        Assert.Equal("field1", text.GetProperty("alternativename").GetString());
        Assert.Equal("field1", text.GetProperty("mappingname").GetString());
        Assert.Equal("u:hello", text.GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, text.GetProperty("defaultvalue").ValueKind);
        Assert.Equal(1, text.GetProperty("quadding").GetInt32());
        Assert.Equal(JsonValueKind.True, text.GetProperty("istext").ValueKind);
        Assert.Equal(0, text.GetProperty("choices").GetArrayLength());
        JsonElement annotation = text.GetProperty("annotation");
        Assert.Equal("7 0 R", annotation.GetProperty("object").GetString());
        Assert.Equal("/Off", annotation.GetProperty("appearancestate").GetString());
        Assert.Equal(4, annotation.GetProperty("annotationflags").GetInt32());
        JsonElement choice = fields[1];
        Assert.Equal("8 0 R", choice.GetProperty("object").GetString());
        Assert.Equal("/Ch", choice.GetProperty("fieldtype").GetString());
        Assert.Equal("ch", choice.GetProperty("fullname").GetString());
        Assert.Equal("Pick one", choice.GetProperty("alternativename").GetString());
        Assert.Equal(JsonValueKind.True, choice.GetProperty("ischoice").ValueKind);
        Assert.Equal(2, choice.GetProperty("choices").GetArrayLength());
        Assert.Equal("x", choice.GetProperty("choices")[0].GetString());
        Assert.Equal("9 0 R", choice.GetProperty("annotation").GetProperty("object").GetString());
        JsonElement button = fields[2];
        Assert.Equal(JsonValueKind.True, button.GetProperty("ischeckbox").ValueKind);
        Assert.Equal(JsonValueKind.False, button.GetProperty("isradiobutton").ValueKind);
    }

    [Fact]
    public async Task AcroformAbsentReportsEmpty()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        using JsonDocument document = await InspectAsync(host, "--json", "--json-key=acroform");
        JsonElement acroform = document.RootElement.GetProperty("acroform");
        Assert.Equal(JsonValueKind.False, acroform.GetProperty("hasacroform").ValueKind);
        Assert.Equal(JsonValueKind.False, acroform.GetProperty("needappearances").ValueKind);
        Assert.Equal(0, acroform.GetProperty("fields").GetArrayLength());
    }

    [Fact]
    public async Task JobModesRoundTrip()
    {
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", StreamFixture());
        using (JsonDocument unused = await InspectAsync(argvHost, "--json-output", "--json-key=pages"))
        {
            Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] a));
            InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", StreamFixture());
            QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
                jobHost,
                "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"jsonOutput\": \"latest\", \"jsonKey\": [\"pages\"]}",
                CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, job.ExitCode);
            Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] b));
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public async Task JobStreamModesRoundTrip()
    {
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", StreamFixture());
        using (JsonDocument unused = await InspectAsync(argvHost, "--json", "--json-stream-data=file", "--json-stream-prefix=side", "--json-object=5"))
        {
            Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] a));
            Assert.True(argvHost.TryGetOutput(QpdfPath.From("side-5"), out byte[] sideA));
            InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", StreamFixture());
            QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
                jobHost,
                "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"json\": \"latest\", \"jsonStreamData\": \"file\", \"jsonStreamPrefix\": \"side\", \"jsonObject\": [\"5\"]}",
                CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, job.ExitCode);
            Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] b));
            Assert.True(jobHost.TryGetOutput(QpdfPath.From("side-5"), out byte[] sideB));
            Assert.Equal(a, b);
            Assert.Equal(sideA, sideB);
        }
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"jsonOutput\": \"1\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"json\": \"1\", \"jsonOutput\": \"latest\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"jsonOutput\": \"latest\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}]}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"jsonStreamData\": \"raw\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"jsonStreamData\": \"inline\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"jsonObject\": [\"1,2,3\"]}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"jsonObject\": \"trailer\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"jsonStreamPrefix\": \"side\"}")]
    public async Task JobModesMalformedFail(string jobJson)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task TypedModesRoundTrip()
    {
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", StreamFixture());
        using (JsonDocument unused = await InspectAsync(argvHost, "--json", "--json-stream-data=inline", "--json-object=5"))
        {
            Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] a));
            InMemoryQpdfHost typedHost = HostWith("inputs/a.pdf", StreamFixture());
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(
                typedHost,
                QpdfPath.From("inputs/a.pdf"),
                QpdfPath.From("outputs/a.json"),
                new QpdfJsonOptions(2, [], "inline", null, ["5"], false),
                CancellationToken.None);
            Assert.True(typedHost.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] b));
            Assert.Equal(a, b);
            Assert.Equal(Encoding.UTF8.GetString(a), result.Json);
        }
    }

    [Fact]
    public async Task TypedMinimalOmitsVersion()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(
            host,
            QpdfPath.From("inputs/a.pdf"),
            QpdfPath.From("outputs/a.json"),
            new QpdfJsonOptions(2, [], "inline", null, [], true),
            CancellationToken.None);
        using JsonDocument document = JsonDocument.Parse(result.Json);
        Assert.False(document.RootElement.TryGetProperty("version", out _));
        Assert.True(document.RootElement.TryGetProperty("qpdf", out _));
    }

    [Fact]
    public async Task TypedValidation()
    {
        Assert.Throws<ArgumentException>(() => new QpdfJsonOptions(2, [], "raw", null, [], false));
        Assert.Throws<ArgumentNullException>(() => new QpdfJsonOptions(2, [], "inline", null, null!, false));
        Assert.Throws<ArgumentException>(() => new QpdfJsonOptions(2, [], "inline", null, [null!], false));
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", StreamFixture());
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.GetDocumentJsonAsync(
            host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.json"), new QpdfJsonOptions(2, ["bogus"], "inline", null, [], false), CancellationToken.None).AsTask());
    }
}