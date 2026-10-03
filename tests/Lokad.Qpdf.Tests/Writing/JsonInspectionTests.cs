using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonInspectionTests
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

    private static async Task<JsonDocument> InspectAsync(InMemoryQpdfHost host, params string[] flags)
    {
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.json"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] json));
        string text = Encoding.UTF8.GetString(json);
        Assert.EndsWith("\n", text);
        return JsonDocument.Parse(text);
    }

    private static QpdfDiagnostic SingleError(QpdfExecutionResult result)
    {
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        return Assert.Single(result.Diagnostics);
    }
    [Fact]
    public async Task DefaultVersionIsTwo()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        using JsonDocument document = await InspectAsync(host, "--json");
        JsonElement root = document.RootElement;
        Assert.Equal(2, root.GetProperty("version").GetInt32());
        Assert.Equal("generalized", root.GetProperty("parameters").GetProperty("decodelevel").GetString());
        JsonElement pages = root.GetProperty("pages");
        Assert.Equal(1, pages.GetArrayLength());
        JsonElement page = pages[0];
        Assert.Equal("3 0 R", page.GetProperty("object").GetString());
        Assert.Equal(1, page.GetProperty("pageposfrom1").GetInt32());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("label").ValueKind);
        Assert.Equal(0, page.GetProperty("outlines").GetArrayLength());
        Assert.Equal(0, page.GetProperty("images").GetArrayLength());
        JsonElement contents = page.GetProperty("contents");
        Assert.Equal(1, contents.GetArrayLength());
        Assert.Equal("4 0 R", contents[0].GetString());
        JsonElement qpdf = root.GetProperty("qpdf");
        Assert.Equal(2, qpdf.GetArrayLength());
        Assert.Equal(2, qpdf[0].GetProperty("jsonversion").GetInt32());
        Assert.Equal("1.4", qpdf[0].GetProperty("pdfversion").GetString());
        Assert.Equal(JsonValueKind.False, qpdf[0].GetProperty("pushedinheritedpageresources").ValueKind);
        Assert.Equal(JsonValueKind.True, qpdf[0].GetProperty("calledgetallpages").ValueKind);
        JsonElement objects = qpdf[1];
        Assert.True(objects.TryGetProperty("obj:1 0 R", out JsonElement catalog));
        Assert.Equal("/Catalog", catalog.GetProperty("value").GetProperty("/Type").GetString());
        Assert.True(objects.TryGetProperty("obj:4 0 R", out JsonElement stream));
        JsonElement streamInfo = stream.GetProperty("stream");
        Assert.Equal(15, streamInfo.GetProperty("dict").GetProperty("/Length").GetInt32());
        Assert.False(streamInfo.TryGetProperty("data", out _));
        Assert.True(objects.TryGetProperty("trailer", out _));
    }

    [Fact]
    public async Task ExplicitVersionOne()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        using JsonDocument document = await InspectAsync(host, "--json=1");
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.False(root.TryGetProperty("qpdf", out _));
        JsonElement objects = root.GetProperty("objects");
        Assert.True(objects.TryGetProperty("1 0", out JsonElement catalog));
        Assert.Equal("/Catalog", catalog.GetProperty("/Type").GetString());
        Assert.True(objects.TryGetProperty("4 0", out JsonElement streamDict));
        Assert.Equal(15, streamDict.GetProperty("/Length").GetInt32());
        Assert.True(objects.TryGetProperty("trailer", out JsonElement trailer));
        Assert.Equal("1 0 R", trailer.GetProperty("/Root").GetString());
        JsonElement info = root.GetProperty("objectinfo");
        Assert.True(info.TryGetProperty("4 0", out JsonElement streamInfo));
        Assert.Equal(JsonValueKind.True, info.GetProperty("4 0").GetProperty("stream").GetProperty("is").ValueKind);
    }

    [Fact]
    public async Task LatestMapsToTwo()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        using JsonDocument document = await InspectAsync(host, "--json=latest");
        Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task KeySelectionLimitsOutput()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        using JsonDocument document = await InspectAsync(host, "--json", "--json-key=pages");
        JsonElement root = document.RootElement;
        Assert.Equal(2, root.GetProperty("version").GetInt32());
        Assert.True(root.TryGetProperty("parameters", out _));
        Assert.True(root.TryGetProperty("pages", out _));
        Assert.False(root.TryGetProperty("qpdf", out _));
        Assert.False(root.TryGetProperty("objects", out _));
        Assert.False(root.TryGetProperty("attachments", out _));
    }

    [Theory]
    [InlineData("--json=9")]
    [InlineData("--json=banana")]
    public async Task BadVersionFails(params string[] flags)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.json"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Theory]
    [InlineData("--json=1", "--json-key=qpdf")]
    [InlineData("--json=2", "--json-key=objects")]
    [InlineData("--json=2", "--json-key=objectinfo")]
    [InlineData("--json", "--json-key=bogus")]
    public async Task BadKeyFails(params string[] flags)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.json"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }
    [Theory]
    [InlineData("--pages", "1")]
    [InlineData("--check")]
    [InlineData("--list-attachments")]
    [InlineData("--add-attachment", "files/n.bin", "--")]
    [InlineData("--replace-input")]
    public async Task JsonCombinationsFail(params string[] flags)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        host.AddInput(QpdfPath.From("files/n.bin"), [1, 2, 3], QpdfInputOwnership.Copy);
        List<string> argv = new() { "inputs/a.pdf", "--json" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.json"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task MissingOutputFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--json"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task StrayKeyRequiresJson()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--json-key=pages", "--", "outputs/a.json"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Theory]
    [InlineData("--json-input")]
    public async Task DeferredOptionsFail(params string[] flags)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        List<string> argv = new() { "inputs/a.pdf", "--json" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.json"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Theory]
    [InlineData("--json-help")]
    [InlineData("--json-help=1")]
    [InlineData("--json-help=2")]
    [InlineData("--json-help=latest")]
    public async Task JsonHelpDescribesRequestedVersion(string flag)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation([flag]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        QpdfDiagnostic help = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.HelpText, help.Code);
        using JsonDocument described = JsonDocument.Parse(help.Message);
        JsonElement root = described.RootElement;
        Assert.True(root.TryGetProperty("pages", out _));
        Assert.True(root.TryGetProperty("acroform", out _));
        if (flag == "--json-help=1")
        {
            Assert.True(root.TryGetProperty("objects", out _));
            Assert.True(root.TryGetProperty("objectinfo", out _));
            Assert.False(root.TryGetProperty("qpdf", out _));
        }
        else
        {
            Assert.True(root.TryGetProperty("qpdf", out _));
            Assert.False(root.TryGetProperty("objects", out _));
        }
    }

    [Theory]
    [InlineData("--json-help=")]
    [InlineData("--json-help=3")]
    [InlineData("--json-help=x")]
    public async Task JsonHelpRejectsUnknownVersions(string flag)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation(["inputs/a.pdf", flag, "--", "outputs/a.json"]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] _));
    }

    [Fact]
    public async Task JsonHelpWinsOverJsonOperation()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--json", "--json-help", "--", "outputs/a.json"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(QpdfDiagnosticCode.HelpText, Assert.Single(result.Diagnostics).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] _));
    }

    [Fact]
    public async Task NamesAndStringsEncode()
    {
        byte[] fixture = BuildPdf(
            " /Slash /text#2fplain /Real .5 /Title <FEFF00480069> /Raw <FF00> /Count 7",
            new List<(string Dict, byte[]? StreamData)>());
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
        using JsonDocument v2 = await InspectAsync(host, "--json", "--json-key=qpdf");
        JsonElement objects = v2.RootElement.GetProperty("qpdf")[1];
        JsonElement catalog = objects.GetProperty("obj:1 0 R").GetProperty("value");
        Assert.Equal("/text/plain", catalog.GetProperty("/Slash").GetString());
        Assert.Equal(0.5, catalog.GetProperty("/Real").GetDouble());
        Assert.Equal("u:Hi", catalog.GetProperty("/Title").GetString());
        Assert.Equal("b:ff00", catalog.GetProperty("/Raw").GetString());
        Assert.Equal(7, catalog.GetProperty("/Count").GetInt32());

        InMemoryQpdfHost host1 = HostWith("inputs/a.pdf", fixture);
        using JsonDocument v1 = await InspectAsync(host1, "--json=1", "--json-key=objects");
        JsonElement one = v1.RootElement.GetProperty("objects").GetProperty("1 0");
        Assert.Equal("/text#2fplain", one.GetProperty("/Slash").GetString());
        Assert.Equal("Hi", one.GetProperty("/Title").GetString());
    }
    [Fact]
    public async Task OutlinesAndLabelsResolve()
    {
        byte[] fixture = BuildPdf(
            " /Outlines 5 0 R /PageLabels 7 0 R",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 1 >>", null),
                ("<< /Title (Chapter) /Parent 5 0 R /Dest [3 0 R /Fit] >>", null),
                ("<< /Nums [0 << /S /D >>] >>", null),
            });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
        using JsonDocument document = await InspectAsync(host, "--json");
        JsonElement root = document.RootElement;
        JsonElement outlines = root.GetProperty("outlines");
        Assert.Equal(1, outlines.GetArrayLength());
        JsonElement outline = outlines[0];
        Assert.Equal("6 0 R", outline.GetProperty("object").GetString());
        Assert.Equal("Chapter", outline.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.True, outline.GetProperty("open").ValueKind);
        Assert.Equal(1, outline.GetProperty("destpageposfrom1").GetInt32());
        Assert.Equal(0, outline.GetProperty("kids").GetArrayLength());
        JsonElement labels = root.GetProperty("pagelabels");
        Assert.Equal(1, labels.GetArrayLength());
        Assert.Equal(0, labels[0].GetProperty("index").GetInt32());
        Assert.Equal("/D", labels[0].GetProperty("label").GetProperty("/S").GetString());
        JsonElement page = root.GetProperty("pages")[0];
        Assert.Equal("/D", page.GetProperty("label").GetProperty("/S").GetString());
        Assert.Equal(1, page.GetProperty("outlines").GetArrayLength());
        Assert.Equal("Chapter", page.GetProperty("outlines")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task AttachmentsKeyReportsFiles()
    {
        byte[] payload = Encoding.ASCII.GetBytes("Hello attachment.");
        byte[] fixture = BuildPdf(
            " /Names 5 0 R",
            new List<(string Dict, byte[]? StreamData)>
            {
                ("<< /EmbeddedFiles 6 0 R >>", null),
                ("<< /Names [(note.txt) 7 0 R] >>", null),
                ("<< /Type /Filespec /F (note.txt) /UF (note.txt) /Desc (A note) /EF << /F 8 0 R >> >>", null),
                ("<< /Type /EmbeddedFile /Subtype /text#2fplain /Length " + payload.Length + " /Params << /Size " + payload.Length + " /CreationDate (D:20240102150406Z) /CheckSum <0102> >> >>", payload),
            });
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
        using JsonDocument document = await InspectAsync(host, "--json", "--json-key=attachments");
        JsonElement attachments = document.RootElement.GetProperty("attachments");
        Assert.True(attachments.TryGetProperty("note.txt", out JsonElement file));
        Assert.Equal("7 0 R", file.GetProperty("filespec").GetString());
        Assert.Equal("note.txt", file.GetProperty("preferredname").GetString());
        Assert.Equal("8 0 R", file.GetProperty("preferredcontents").GetString());
        Assert.Equal("A note", file.GetProperty("description").GetString());
        Assert.Equal("note.txt", file.GetProperty("names").GetProperty("UF").GetString());
        JsonElement streams = file.GetProperty("streams");
        Assert.True(streams.TryGetProperty("F", out JsonElement stream));
        Assert.Equal("2024-01-02T15:04:06Z", stream.GetProperty("creationdate").GetString());
        Assert.Equal("2024-01-02T15:04:06Z", stream.GetProperty("modificationdate").GetString());
        Assert.Equal("text/plain", stream.GetProperty("mimetype").GetString());
        Assert.Equal("0102", stream.GetProperty("checksum").GetString());
    }

    [Fact]
    public async Task EncryptKeyReportsUnencrypted()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        using JsonDocument document = await InspectAsync(host, "--json", "--json-key=encrypt");
        JsonElement encrypt = document.RootElement.GetProperty("encrypt");
        Assert.Equal(JsonValueKind.False, encrypt.GetProperty("encrypted").ValueKind);
        Assert.Equal(JsonValueKind.True, encrypt.GetProperty("capabilities").GetProperty("extract").ValueKind);
        Assert.Equal("none", encrypt.GetProperty("parameters").GetProperty("method").GetString());
        Assert.Equal(0, encrypt.GetProperty("parameters").GetProperty("bits").GetInt32());
    }
    [Fact]
    public async Task JobRoundTripMatchesArgv()
    {
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", PlainFixture());
        using (JsonDocument argv = await InspectAsync(argvHost, "--json", "--json-key=pages"))
        {
            Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] a));
            InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", PlainFixture());
            QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
                jobHost,
                "{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"json\": \"latest\", \"jsonKey\": [\"pages\"]}",
                CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, job.ExitCode);
            Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] b));
            Assert.Equal(a, b);
        }
    }

    [Theory]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"json\": \"9\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"json\": 3}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"jsonKey\": [\"pages\"]}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"json\": \"2\", \"jsonKey\": \"pages\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"json\": \"2\", \"jsonKey\": [\"bogus\"]}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"json\": \"1\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}]}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"json\": \"2\", \"listAttachments\": true}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"jsonStreamData\": \"inline\"}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"jsonObject\": [\"trailer\"]}")]
    [InlineData("{\"inputFile\": \"inputs/a.pdf\", \"outputFile\": \"outputs/a.json\", \"jsonInput\": true}")]
    public async Task JobMalformedFails(string jobJson)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
    }

    [Fact]
    public async Task TypedRoundTripMatchesArgv()
    {
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", PlainFixture());
        using (JsonDocument unused = await InspectAsync(argvHost, "--json"))
        {
            Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] a));
            InMemoryQpdfHost typedHost = HostWith("inputs/a.pdf", PlainFixture());
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(
                typedHost,
                QpdfPath.From("inputs/a.pdf"),
                QpdfPath.From("outputs/a.json"),
                new QpdfJsonOptions(2, []),
                CancellationToken.None);
            Assert.True(typedHost.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] b));
            Assert.Equal(a, b);
            Assert.Equal(Encoding.UTF8.GetString(a), result.Json);
        }
    }

    [Fact]
    public async Task PageImagesListDirectImages()
    {
        JsonElement images = await InspectFixturePagesAsync("images-1page.pdf", "--json", "--json-key=pages");
        Assert.Equal(1, images.GetArrayLength());
        JsonElement image = images[0];
        Assert.Equal(8, image.GetProperty("bitspercomponent").GetInt32());
        Assert.Equal("/DeviceGray", image.GetProperty("colorspace").GetString());
        Assert.Equal(1, image.GetProperty("decodeparms").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, image.GetProperty("decodeparms")[0].ValueKind);
        Assert.Equal(1, image.GetProperty("filter").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, image.GetProperty("filter")[0].ValueKind);
        Assert.Equal(JsonValueKind.True, image.GetProperty("filterable").ValueKind);
        Assert.Equal(2, image.GetProperty("height").GetInt32());
        Assert.Equal("/Im0", image.GetProperty("name").GetString());
        Assert.Equal("5 0 R", image.GetProperty("object").GetString());
        Assert.Equal(2, image.GetProperty("width").GetInt32());
    }

    [Fact]
    public async Task PageImagesNullMissingDimensions()
    {
        JsonElement images = await InspectFixturePagesAsync("images-nodim-1page.pdf", "--json", "--json-key=pages");
        Assert.Equal(1, images.GetArrayLength());
        JsonElement image = images[0];
        Assert.Equal(JsonValueKind.Null, image.GetProperty("bitspercomponent").ValueKind);
        Assert.Equal(JsonValueKind.Null, image.GetProperty("colorspace").ValueKind);
        Assert.Equal(JsonValueKind.Null, image.GetProperty("height").ValueKind);
        Assert.Equal("/NoDim", image.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, image.GetProperty("width").ValueKind);
    }

    [Fact]
    public async Task PageImagesFilteredDetails()
    {
        JsonElement images = await InspectFixturePagesAsync("images-filtered-1page.pdf", "--json", "--json-key=pages");
        Assert.Equal(4, images.GetArrayLength());
        Assert.Equal("/Chain8", images[0].GetProperty("name").GetString());
        Assert.Equal("/ASCIIHexDecode", images[0].GetProperty("filter")[0].GetString());
        Assert.Equal("/FlateDecode", images[0].GetProperty("filter")[1].GetString());
        Assert.Equal(2, images[0].GetProperty("decodeparms").GetArrayLength());
        Assert.Equal(JsonValueKind.True, images[0].GetProperty("filterable").ValueKind);
        Assert.Equal("/Dct8", images[1].GetProperty("name").GetString());
        Assert.Equal("/DCTDecode", images[1].GetProperty("filter")[0].GetString());
        Assert.Equal(JsonValueKind.False, images[1].GetProperty("filterable").ValueKind);
        Assert.Equal("/Fl8", images[2].GetProperty("name").GetString());
        Assert.Equal("/FlateDecode", images[2].GetProperty("filter")[0].GetString());
        Assert.Equal(1, images[2].GetProperty("decodeparms")[0].GetProperty("/Predictor").GetInt32());
        Assert.Equal(JsonValueKind.True, images[2].GetProperty("filterable").ValueKind);
        Assert.Equal("/Idx8", images[3].GetProperty("name").GetString());
        Assert.Equal("/Indexed", images[3].GetProperty("colorspace")[0].GetString());
        Assert.Equal("/DeviceRGB", images[3].GetProperty("colorspace")[1].GetString());
        Assert.Equal(1, images[3].GetProperty("colorspace")[2].GetInt32());
        Assert.Equal("b:00ff", images[3].GetProperty("colorspace")[3].GetString());
    }

    [Fact]
    public async Task PageImagesRespectDecodeLevel()
    {
        JsonElement images = await InspectFixturePagesAsync("images-filtered-1page.pdf", "--json", "--json-key=pages", "--decode-level=all");
        Assert.Equal(JsonValueKind.True, images[1].GetProperty("filterable").ValueKind);
    }

    [Fact]
    public async Task PageImagesMatchAcrossVersions()
    {
        JsonElement latest = await InspectFixturePagesAsync("images-1page.pdf", "--json", "--json-key=pages");
        JsonElement first = await InspectFixturePagesAsync("images-1page.pdf", "--json=1", "--json-key=pages");
        Assert.Equal(latest.GetRawText(), first.GetRawText());
    }

    [Fact]
    public async Task PageImagesJobParity()
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "images-1page.pdf"));
        InMemoryQpdfHost argvHost = HostWith("inputs/a.pdf", bytes);
        using (JsonDocument argv = await InspectAsync(argvHost, "--json", "--json-key=pages"))
        {
            Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] a));
            InMemoryQpdfHost jobHost = HostWith("inputs/a.pdf", bytes);
            QpdfExecutionResult job = await Qpdf.ExecuteJobAsync(
                jobHost,
                """{"inputFile": "inputs/a.pdf", "outputFile": "outputs/a.json", "json": "latest", "jsonKey": ["pages"]}""",
                CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, job.ExitCode);
            Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] b));
            Assert.Equal(a, b);
            using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(b));
            Assert.Equal(1, document.RootElement.GetProperty("pages")[0].GetProperty("images").GetArrayLength());
        }
    }

    [Theory]
    [InlineData("/Filter [/FlateDecode 7]", "")]
    [InlineData("/Filter [/FlateDecode /FlateDecode] /DecodeParms [<< /Predictor 1 >>]", "")]
    public async Task PageImagesMalformedFilterFails(string imageExtra, string _)
    {
        byte[] fixture = BuildImagePdf(imageExtra);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", fixture);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/a.pdf", "--json", "--json-key=pages", "--", "outputs/a.json"]),
            CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, SingleError(result).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] _));
    }

    private static async Task<JsonElement> InspectFixturePagesAsync(string fixture, params string[] flags)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", bytes);
        List<string> argv = new() { "inputs/a.pdf" };
        argv.AddRange(flags);
        argv.AddRange(["--", "outputs/a.json"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.json"), out byte[] json));
        using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(json));
        return document.RootElement.GetProperty("pages")[0].GetProperty("images").Clone();
    }

    private static byte[] BuildImagePdf(string imageExtra)
    {
        byte[] content = Encoding.ASCII.GetBytes("BT (Hi) Tj ET");
        byte[] payload = new byte[] { 1, 2, 3, 4 };
        List<string> bodies = new()
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>",
            "<< /Length " + content.Length.ToString() + " >>",
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 /ColorSpace /DeviceGray " + imageExtra + " /Length " + payload.Length.ToString() + " >>",
        };
        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < bodies.Count; i++)
        {
            int number = i + 1;
            byte[]? streamData = number == 4 ? content : number == 5 ? payload : null;
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

    [Fact]
    public async Task TypedValidation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QpdfJsonOptions(3, []));
        Assert.Throws<ArgumentNullException>(() => new QpdfJsonOptions(2, null!));
        Assert.Throws<ArgumentException>(() => new QpdfJsonOptions(2, [null!]));
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", PlainFixture());
        await Assert.ThrowsAsync<ArgumentNullException>(() => Qpdf.GetDocumentJsonAsync(
            null!, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.json"), new QpdfJsonOptions(2, []), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.GetDocumentJsonAsync(
            host, QpdfPath.From("inputs/a.pdf"), QpdfPath.From("outputs/a.json"), new QpdfJsonOptions(2, ["bogus"]), CancellationToken.None).AsTask());
    }
}
