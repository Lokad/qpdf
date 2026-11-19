using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Image;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class BoundImageTransformTests
{
    private static readonly QpdfPath Model = QpdfPath.From("graph.json"), Output = QpdfPath.From("output.pdf");
    private static readonly byte[] PatchedInline = CreateInlineProgram(true);
    private static readonly byte[] SmallInline = "BI /W 8 /H 1 /BPC 8 /CS /Local ID abcdefgh EI\n"u8.ToArray();
    private static readonly byte[] Stamp = [.. "q\n"u8, .. SmallInline, .. "Q\n"u8];

    [Theory]
    [InlineData(9, true)]
    [InlineData(10, false)]
    public async Task CreationExternalizesByPayloadLengthAndLeavesPrivateProgramsIntact(int minimum, bool externalized)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Model, InheritedResourcesModel(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Model.Value, "--json-input", "--stream-data=preserve",
            "--externalize-inline-images", $"--ii-min-bytes={minimum}", Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using (QpdfDocument document = await Read(host, Output))
        {
            Assert.Equal(2, document.PageCount);
            PdfDictionary catalog = Catalog(document);
            PdfDictionary first = document.PageReferences[0].Page;
            PdfDictionary second = document.PageReferences[1].Page;
            PdfReference original = Link(catalog, "AuditProgram");
            PdfReference originalForm = Link(catalog, "AuditForm");
            PdfDictionary parent = Dictionary(document, catalog, "Pages");
            Assert.Equal(Link(catalog, "AuditResources"), Link(parent, "Resources"));
            PdfDictionary parentObjects = Dictionary(document, Dictionary(document, parent, "Resources"), "XObject");
            Assert.False(parentObjects.TryGet(PdfName.FromAscii("IIm1"), out _));
            Assert.Equal(originalForm, Link(parentObjects, "Stamp"));

            PdfReference current = Link(first, "Contents");
            PdfDictionary resources = Dictionary(document, first, "Resources");
            PdfDictionary currentObjects = Dictionary(document, resources, "XObject");
            PdfDictionary formObjects = Dictionary(document, Dictionary(document, second, "Resources"), "XObject");
            PdfReference currentForm = Link(formObjects, "Stamp");
            Assert.Equal(!externalized, current.Equals(original));
            Assert.Equal(!externalized, currentForm.Equals(originalForm));
            Assert.Equal(SmallInline, Stream(document, original).Data);
            Assert.Equal(Stamp, Stream(document, originalForm).Data);
            PdfStream activeForm = Stream(document, currentForm);
            if (externalized)
            {
                Assert.Contains("/IIm1 Do", Encoding.ASCII.GetString(Stream(document, current).Data), StringComparison.Ordinal);
                Assert.Contains("/IIm1 Do", Encoding.ASCII.GetString(activeForm.Data), StringComparison.Ordinal);
                CheckExternalizedSample(document, currentObjects);
                CheckExternalizedSample(document, Dictionary(document, Dictionary(document, activeForm.Dictionary, "Resources"), "XObject"));
            }
            else
            {
                Assert.False(currentObjects.TryGet(PdfName.FromAscii("IIm1"), out _));
                Assert.Equal(SmallInline, Stream(document, current).Data);
                Assert.Equal(Stamp, activeForm.Data);
            }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("optimize", "DCTDecode")]
    [InlineData("keep-inline", "")]
    [InlineData("explicit", "DCTDecode")]
    [InlineData("width-limit", "FlateDecode")]
    [InlineData("decode-all", "FlateDecode")]
    [InlineData("compressible", "DCTDecode")]
    [InlineData("split", "DCTDecode")]
    public async Task JobUpdateAppliesInlineAndJpegSettingsToThePatchedPage(string mode, string filter)
    {
        RecordingCodec codec = new();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
        byte[] program = mode == "compressible" ? CreateInlineProgram(false) : PatchedInline;
        AddUpdateInputs(host, program);
        JsonObject job = UpdateJob();
        if (mode is "keep-inline" or "explicit") { job["keepInlineImages"] = ""; }
        if (mode == "explicit") { job["externalizeInlineImages"] = ""; }
        if (mode == "width-limit") { job["oiMinWidth"] = "128"; }
        if (mode == "decode-all") { job["decodeLevel"] = "all"; }
        if (mode == "split") { job["splitPages"] = "1"; job["normalizeContent"] = "y"; }
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job.ToJsonString(), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(mode is "keep-inline" or "width-limit" ? Array.Empty<int>() : new[] { 37 }, codec.EncodeQualities);
        QpdfPath output = mode == "split" ? QpdfPath.From("output-1.pdf") : Output;
        await using (QpdfDocument document = await Read(host, output))
        {
            PdfDictionary page = Assert.Single(document.PageReferences).Page;
            PdfReference active = Link(page, "Contents");
            PdfReference original = Link(Catalog(document), "Audit");
            Assert.Equal(program, StreamObservation.Decode(Stream(document, original)));
            if (filter.Length == 0)
            {
                Assert.Equal(original, active);
                return;
            }
            Assert.NotEqual(original, active);
            Assert.Contains("/IIm1 Do", Encoding.ASCII.GetString(StreamObservation.Decode(Stream(document, active))), StringComparison.Ordinal);
            PdfDictionary resources = Dictionary(document, page, "Resources");
            PdfStream image = Stream(document, Link(Dictionary(document, resources, "XObject"), "IIm1"));
            Assert.Equal(filter, Assert.IsType<PdfName>(Value(image.Dictionary, "Filter")).DisplayText);
            Assert.Equal(128, Assert.IsType<PdfInteger>(Value(image.Dictionary, "Width")).Value);
            Assert.Equal(128, Assert.IsType<PdfInteger>(Value(image.Dictionary, "Height")).Value);
            if (filter == "DCTDecode") { Assert.True(image.Data.AsSpan().StartsWith(new byte[] { 255, 216 })); }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task MissingCodecRefusesBeforeReplacingTheTarget()
    {
        using RecordingJobHost host = new(null, null, "success", true);
        AddUpdateInputs(host.Inner, PatchedInline);
        await using (IQpdfOutputTransaction prior = await host.Inner.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, UpdateJob().ToJsonString(), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, item => item.Message.Contains("codec supplied by the host", StringComparison.Ordinal));
        Assert.Empty(host.CreatedPaths);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] kept));
        Assert.Equal("prior"u8.ToArray(), kept);
        ExecutionJobFixtures.AssertClosed(host, result);
    }

    [Theory]
    [InlineData(false, false, "yes", true)]
    [InlineData(false, true, "yes", true)]
    [InlineData(true, false, "yes", false)]
    [InlineData(false, false, "no", false)]
    public async Task SplitPrunesUnusedImagesBeforeRequiringACodec(bool drawn, bool identityCrypt, string policy, bool succeeds)
    {
        using RecordingJobHost host = new(null, null, "success", true);
        byte[] samples = new byte[128 * 128];
        Array.Fill(samples, (byte)96);
        string data = Convert.ToBase64String(samples);
        string program = Convert.ToBase64String(drawn ? "q /Im Do Q"u8 : "q Q"u8);
        string filter = identityCrypt ? """ "/Filter":"/Crypt","/DecodeParms":{"/Name":"/Identity"} """ : "";
        string json = $$$$"""
            {"qpdf":[{"jsonversion":2,"pdfversion":"1.7"},{
              "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/AuditImage":"6 0 R"}},
              "obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["3 0 R"],"/Count":1,"/MediaBox":[0,0,128,128],
                "/Resources":{"/XObject":{"/Im":"6 0 R"}}} },
              "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/Contents":"4 0 R"}},
              "obj:4 0 R":{"stream":{"dict":{ {{{{filter}}}} },"data":"{{{{program}}}}"}},
              "obj:6 0 R":{"stream":{"dict":{"/Type":"/XObject","/Subtype":"/Image","/Width":128,"/Height":128,
                "/ColorSpace":"/DeviceGray","/BitsPerComponent":8},"data":"{{{{data}}}}"}},
              "trailer":{"value":{"/Root":"1 0 R","/Size":7}}
            }]}
            """;
        host.Inner.AddInput(Model, Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Transfer);
        QpdfPath split = QpdfPath.From("output-1.pdf");
        await using (IQpdfOutputTransaction prior = await host.Inner.CreateOutputAsync(split, CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }

        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Model.Value, "--json-input", "--split-pages=1",
            $"--remove-unreferenced-resources={policy}", "--stream-data=preserve", "--optimize-images", "--keep-inline-images",
            "--oi-min-width=1", "--oi-min-height=1", "--oi-min-area=1", Output.Value]), CancellationToken.None);

        if (succeeds)
        {
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Equal(0L, result.Counters.TotalDecodedBytes);
            await using QpdfDocument document = await Read(host.Inner, split);
            PdfDictionary resources = Dictionary(document, Assert.Single(document.PageReferences).Page, "Resources");
            if (resources.TryGet(PdfName.FromAscii("XObject"), out _))
            { Assert.False(Dictionary(document, resources, "XObject").TryGet(PdfName.FromAscii("Im"), out _)); }
            Assert.Equal(samples, Stream(document, Link(Catalog(document), "AuditImage")).Data);
        }
        else
        {
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, item => item.Message.Contains("codec supplied by the host", StringComparison.Ordinal));
            Assert.Empty(host.CreatedPaths);
            Assert.True(host.Inner.TryGetOutput(split, out byte[] kept));
            Assert.Equal("prior"u8.ToArray(), kept);
        }
        ExecutionJobFixtures.AssertClosed(host, result);
    }

    [Fact]
    public async Task AuthoredGraphReportDescribesExternalizedImageAndKeepsDeclaredPrivatePairs()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Model, InheritedResourcesModel(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Model.Value, "--json-input", "--json-output=2",
            "--json-key=pages", "--externalize-inline-images", "--ii-min-bytes=9", "report"]), CancellationToken.None);

        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("report"), out byte[] bytes));
        using JsonDocument report = JsonDocument.Parse(bytes);
        JsonElement root = report.RootElement;
        JsonElement pages = root.GetProperty("pages");
        JsonElement descriptor = Assert.Single(pages[0].GetProperty("images").EnumerateArray());
        Assert.Equal("/IIm1", descriptor.GetProperty("name").GetString());
        Assert.Equal(8, descriptor.GetProperty("width").GetInt32());
        JsonElement table = root.GetProperty("qpdf")[1];
        string imagePair = Assert.IsType<string>(descriptor.GetProperty("object").GetString());
        JsonElement image = table.GetProperty("obj:" + imagePair).GetProperty("stream");
        Assert.Equal("abcdefgh "u8.ToArray(), image.GetProperty("data").GetBytesFromBase64());
        string programPair = Assert.IsType<string>(Assert.Single(pages[0].GetProperty("contents").EnumerateArray()).GetString());
        Assert.NotEqual("5 0 R", programPair);
        byte[] program = table.GetProperty("obj:" + programPair).GetProperty("stream").GetProperty("data").GetBytesFromBase64();
        Assert.Contains("/IIm1 Do", Encoding.ASCII.GetString(program), StringComparison.Ordinal);
        Assert.Equal("5 0 R", table.GetProperty("obj:1 0 R").GetProperty("value").GetProperty("/AuditProgram").GetString());
        Assert.Equal(SmallInline, table.GetProperty("obj:5 0 R").GetProperty("stream").GetProperty("data").GetBytesFromBase64());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task PatchedGraphImageListingUsesOptimizedResourcesAndExactSourcePairs()
    {
        RecordingCodec codec = new();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
        AddUpdateInputs(host, PatchedInline);
        JsonObject job = UpdateJob();
        job.Remove("outputFile");
        job["showPages"] = "";
        job["withImages"] = "";
        job["showObject"] = "4,7";
        job["rawStreamData"] = "";

        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job.ToJsonString(), CancellationToken.None);

        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(new[] { 37 }, codec.EncodeQualities);
        Assert.Equal(PatchedInline, result.OutputBytes?.ToArray());
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.PageListing
            && item.Message.StartsWith("    /IIm1: ", StringComparison.Ordinal)
            && item.Message.EndsWith(" R, 128 x 128", StringComparison.Ordinal));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ReturnedPatchedReportShowsJpegAndUnchangedPrivateBytes()
    {
        RecordingCodec codec = new();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
        AddUpdateInputs(host, PatchedInline);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["source.pdf", "--update-from-json=graph.json",
            "--json-output=2", "--json-key=pages", "--optimize-images", "--oi-min-width=1", "--oi-min-height=1",
            "--oi-min-area=1", "--ii-min-bytes=0", "--jpeg-quality=37"]), CancellationToken.None);

        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        using JsonDocument report = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
        JsonElement root = report.RootElement;
        JsonElement page = root.GetProperty("pages")[0];
        JsonElement descriptor = Assert.Single(page.GetProperty("images").EnumerateArray());
        string imagePair = Assert.IsType<string>(descriptor.GetProperty("object").GetString());
        JsonElement table = root.GetProperty("qpdf")[1];
        JsonElement image = table.GetProperty("obj:" + imagePair).GetProperty("stream");
        Assert.Equal("/DCTDecode", image.GetProperty("dict").GetProperty("/Filter").GetString());
        Assert.Equal(PatchedInline, table.GetProperty("obj:4 7 R").GetProperty("stream").GetProperty("data").GetBytesFromBase64());
        Assert.Equal(new[] { 37 }, codec.EncodeQualities);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static void CheckExternalizedSample(QpdfDocument document, PdfDictionary xobjects)
    {
        PdfStream image = Stream(document, Link(xobjects, "IIm1"));
        Assert.Equal("DeviceGray", Assert.IsType<PdfName>(Value(image.Dictionary, "ColorSpace")).DisplayText);
        Assert.Equal("abcdefgh "u8.ToArray(), StreamObservation.Decode(image));
    }

    private static byte[] InheritedResourcesModel()
    {
        string program = Convert.ToBase64String(SmallInline);
        string stamp = Convert.ToBase64String(Stamp);
        return Encoding.UTF8.GetBytes($$$$"""
            {"qpdf":[{"jsonversion":2,"pdfversion":"1.7"},{
              "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/AuditProgram":"5 0 R","/AuditResources":"7 0 R","/AuditForm":"8 0 R"}},
              "obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["3 0 R","4 0 R"],"/Count":2,"/MediaBox":[0,0,100,100],"/Resources":"7 0 R"}},
              "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/Contents":"5 0 R"}},
              "obj:4 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/Contents":"6 0 R"}},
              "obj:5 0 R":{"stream":{"dict":{"/Filter":"/Crypt","/DecodeParms":{"/Name":"/Identity"}},"data":"{{{{program}}}}"}},
              "obj:6 0 R":{"stream":{"dict":{},"data":"cSAvU3RhbXAgRG8gUQo="}},
              "obj:7 0 R":{"value":{"/ColorSpace":{"/Local":"/DeviceGray"},"/XObject":{"/Stamp":"8 0 R"}}},
              "obj:8 0 R":{"stream":{"dict":{"/Type":"/XObject","/Subtype":"/Form","/BBox":[0,0,10,10],
                "/Resources":{"/ColorSpace":{"/Local":"/DeviceGray"}},"/Filter":"/Crypt"},"data":"{{{{stamp}}}}"}},
              "trailer":{"value":{"/Root":"1 0 R","/Size":9}}
            }]}
            """);
    }

    private static byte[] CreateInlineProgram(bool noisy)
    {
        byte[] samples = new byte[128 * 128];
        if (noisy) { new Random(1490).NextBytes(samples); }
        else { Array.Fill(samples, (byte)96); }
        return [.. "q\nBI /W 128 /H 128 /BPC 8 /CS /G ID "u8, .. samples, .. " EI\nQ\n"u8];
    }

    private static void AddUpdateInputs(InMemoryQpdfHost host, byte[] program)
    {
        byte[] before = "q Q"u8.ToArray();
        byte[] pdf = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Audit 4 7 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] /Resources <<>> /MediaBox [0 0 200 200] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /Contents 4 7 R >>", null),
            (4, $"<< /Length {before.Length} >>", before)], 1, new Dictionary<int, int> { [4] = 7 });
        host.AddInput(QpdfPath.From("source.pdf"), pdf, QpdfInputOwnership.Transfer);
        string payload = Convert.ToBase64String(program);
        host.AddInput(Model, Encoding.UTF8.GetBytes($$$$"""
            {"qpdf":[{"jsonversion":2},{"obj:4 7 R":{"stream":{"dict":{},"data":"{{{{payload}}}}"}}}]}
            """), QpdfInputOwnership.Transfer);
    }

    private static JsonObject UpdateJob() => Assert.IsType<JsonObject>(JsonNode.Parse("""
        {"inputFile":"source.pdf","updateFromJson":"graph.json","outputFile":"output.pdf",
         "optimizeImages":"","iiMinBytes":"0","oiMinWidth":"1","oiMinHeight":"1","oiMinArea":"1","jpegQuality":"37"}
        """));

    private static async ValueTask<QpdfDocument> Read(InMemoryQpdfHost host, QpdfPath output)
    {
        Assert.True(host.TryGetOutput(output, out byte[] bytes));
        QpdfPath input = QpdfPath.From("published");
        host.AddInput(input, bytes, QpdfInputOwnership.Transfer);
        return await QpdfDocument.OpenAsync(host, input, CancellationToken.None);
    }

    private static PdfDictionary Catalog(QpdfDocument document) => Dictionary(document, document.Engine.Trailer, "Root");

    private static PdfDictionary Dictionary(QpdfDocument document, PdfDictionary owner, string key)
    {
        QpdfObject value = Value(owner, key);
        return Assert.IsType<PdfDictionary>(value is PdfReference reference ? document.GetObject(reference) : value);
    }

    private static PdfReference Link(PdfDictionary owner, string name) => Assert.IsType<PdfReference>(Value(owner, name));
    private static PdfStream Stream(QpdfDocument document, PdfReference reference)
        => Assert.IsType<PdfStream>(document.GetObject(reference));
    private static QpdfObject Value(PdfDictionary owner, string key)
    {
        Assert.True(owner.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value;
    }

    private sealed class RecordingCodec : IQpdfJpegCodec
    {
        internal List<int> EncodeQualities { get; } = [];
        public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
            => LokadImageJpegCodec.Instance.Decode(encoded, frame, samples, context);
        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
        {
            EncodeQualities.Add(quality);
            LokadImageJpegCodec.Instance.Encode(samples, frame, quality, encoded, context);
        }
    }
}
