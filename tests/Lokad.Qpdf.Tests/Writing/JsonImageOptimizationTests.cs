using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Image;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonImageOptimizationTests
{
    private static readonly QpdfPath Output = QpdfPath.From("output");
    private static readonly string[] Optimize = ["--optimize-images", "--oi-min-width=1", "--oi-min-height=1", "--oi-min-area=1", "--jpeg-quality=75"];
    private static readonly byte[] Pixels = MakePixels();

    private static byte[] MakePixels()
    { byte[] pixels = new byte[128 * 128 * 3]; new Random(42).NextBytes(pixels); return pixels; }

    private static byte[] Payload(bool encoded)
        => Payload(Pixels, encoded);

    private static byte[] Payload(byte[] pixels, bool encoded)
    {
        if (!encoded) { return pixels; }
        using MemoryStream buffer = new();
        using (ZLibStream compressor = new(buffer, CompressionLevel.Optimal, true)) { compressor.Write(pixels); }
        return buffer.ToArray();
    }

    internal static byte[] Fixture(int generation, string shape, bool encoded)
        => Fixture(generation, shape, encoded, Pixels);

    private static byte[] Fixture(int generation, string shape, bool encoded, byte[] pixels)
    {
        string Ref(int number) => number + " " + generation + " R";
        bool nested = shape == "nested", inherited = shape is "inherited" or "shared";
        string resources = "<< /XObject << /Im " + Ref(5) + " /Alias " + Ref(5) + " /Stale 5 " + (generation + 1) + " R >> >>";
        string page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 128 128] /Contents " + Ref(4)
            + (inherited ? "" : " /Resources " + Ref(8)) + " >>";
        byte[] formContent = "q /Im Do Q"u8.ToArray();
        byte[] content = nested ? "q /F Do Q"u8.ToArray() : formContent, data = Payload(pixels, encoded);
        List<(int, string, byte[]?)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /PrivateImage " + Ref(5) + " /PrivateResources " + Ref(nested ? 11 : 8)
                + " /Dead [" + string.Join(" ", Enumerable.Range(12, 16).Select(number => number + " 0 R")) + "] >>", null),
            (2, "<< /Type /Pages /Count " + (shape == "shared" ? "2" : "1") + " /Kids [" + Ref(3)
                + (shape == "shared" ? " " + Ref(9) : "") + "]" + (inherited ? " /Resources " + Ref(8) : "") + " >>", null),
            (3, page, null), (4, "<< /Length " + content.Length + " >>", content),
            (5, "<< /Type /XObject /Subtype " + Ref(7) + " /Width " + Ref(6) + " /Height 128 /BitsPerComponent 8 /ColorSpace /DeviceRGB"
                + (encoded ? " /Filter /FlateDecode" : "") + " /Length " + data.Length + " >>", data),
            (6, "128", null), (7, "/Image", null), (8, nested ? "<< /XObject << /F " + Ref(10) + " >> >>" : resources, null), (9, page, null),
            (10, nested ? "<< /Type /XObject /Subtype /Form /BBox [0 0 128 128] /Resources " + Ref(11) + " /Length " + formContent.Length + " >>" : "null", nested ? formContent : null),
            (11, nested ? resources : "null", null)];
        return PdfTestDocument.Assemble(objects, 1, objects.Where(item => item.Item1 > 2).ToDictionary(item => item.Item1, _ => generation), "1.7", "");
    }

    [Theory]
    [InlineData(0, "ordinary", false)]
    [InlineData(17, "inherited", false)]
    [InlineData(0, "nested", true)]
    [InlineData(17, "shared", true)]
    public async Task OptimizedResourceRolesKeepOriginalAliasesAndExactSourcePairs(int generation, string shape, bool encoded)
    {
        CountingCodec codec = new(Pixels);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
        host.AddInput(QpdfPath.From("input"), Fixture(generation, shape, encoded), QpdfInputOwnership.Transfer);
        int quality = shape == "nested" ? 25 : 75;
        using JsonDocument report = await Inspect(host, [.. Optimize, "--jpeg-quality=" + quality]);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        JsonElement catalog = Value(table, table.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        JsonElement pages = Value(table, catalog.GetProperty("/Pages")).GetProperty("/Kids");
        string? first = null;
        foreach (JsonElement reference in pages.EnumerateArray())
        {
            JsonElement page = Value(table, reference), resources = Value(table, page.GetProperty("/Resources"));
            JsonElement objects = Value(table, resources.GetProperty("/XObject"));
            if (shape == "nested")
            {
                JsonElement form = table.GetProperty("obj:" + objects.GetProperty("/F").GetString()).GetProperty("stream").GetProperty("dict");
                resources = Value(table, form.GetProperty("/Resources")); objects = Value(table, resources.GetProperty("/XObject"));
            }
            string image = objects.GetProperty("/Im").GetString() ?? throw new InvalidOperationException();
            Assert.Equal(image, objects.GetProperty("/Alias").GetString()); Assert.False(objects.TryGetProperty("/Stale", out _));
            if (first is not null) { Assert.Equal(first, image); }
            first = image;
            JsonElement stream = table.GetProperty("obj:" + image).GetProperty("stream");
            Assert.Equal("/DCTDecode", stream.GetProperty("dict").GetProperty("/Filter").GetString());
            Assert.InRange(Data(stream).Length, 1, Pixels.Length - 1);
            Assert.Equal("6 " + generation + " R", stream.GetProperty("dict").GetProperty("/Width").GetString());
        }
        JsonElement original = table.GetProperty("obj:" + catalog.GetProperty("/PrivateImage").GetString()).GetProperty("stream");
        Assert.Equal(Payload(encoded), Data(original));
        JsonElement originalResources = Value(table, catalog.GetProperty("/PrivateResources"));
        Assert.Equal("5 " + generation + " R", Value(table, originalResources.GetProperty("/XObject")).GetProperty("/Im").GetString());
        Assert.All(catalog.GetProperty("/Dead").EnumerateArray(), entry => Assert.Equal(JsonValueKind.Null, entry.ValueKind));
        Assert.Equal(1, codec.Encodes); Assert.Equal(quality, codec.Quality);
        JsonElement metadata = report.RootElement.GetProperty("qpdf")[0];
        Assert.True(metadata.GetProperty("calledgetallpages").GetBoolean()); Assert.False(metadata.GetProperty("pushedinheritedpageresources").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCodecRefusesBeforeReplacingTheActualReport(bool returned)
    {
        using InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(Fixture(17, "inherited", false), Output);
        List<string> arguments = ["input", "--json-output=2", .. Optimize];
        if (!returned) { arguments.Add("output"); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Contains(result.Diagnostics, entry => entry.Code == QpdfDiagnosticCode.UnsupportedFeature);
        Assert.Null(result.OutputText); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal(new byte[] { 17, 23, 41 }, bytes);
    }

    [Fact]
    public Task CodecAndReplacementOwnersRefundAndProtectThePriorReport()
        => PreparationAdmissionChecks.VerifyAsync(
            () => PreparationAdmissionChecks.StorageAsync(Fixture(17, "nested", true), Output),
            (host, token) => Qpdf.ExecuteAsync(new CodecBudgetHost(host), new(["input", "--json-output=2", .. Optimize, "output"]), token),
            Output, 512, ["write", "cancel"]);

    [Theory]
    [InlineData("baseline")]
    [InlineData("quality-only")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("area")]
    public async Task InactiveSettingsAndThresholdBoundariesKeepTheOriginalImage(string mode)
    {
        CountingCodec codec = new(Pixels);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
        host.AddInput(QpdfPath.From("input"), Fixture(17, "inherited", false), QpdfInputOwnership.Transfer);
        string[] options = mode switch { "baseline" => [], "quality-only" => ["--jpeg-quality=25"],
            "width" => [.. Optimize, "--oi-min-width=128"], "height" => [.. Optimize, "--oi-min-height=128"],
            _ => [.. Optimize, "--oi-min-area=16384"] };
        using JsonDocument report = await Inspect(host, options);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        Assert.Equal("5 17 R", Bindings(table, Page(table)).GetProperty("/Im").GetString());
        Assert.Equal(Pixels, Data(table.GetProperty("obj:5 17 R").GetProperty("stream")));
        Assert.Equal(0, codec.Encodes);
        Assert.Equal(mode is "width" or "height" or "area", report.RootElement.GetProperty("qpdf")[0].GetProperty("calledgetallpages").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargerCandidatesAndEncoderFailureKeepTheOriginalPayload(bool failure)
    {
        byte[] pixels = failure ? Pixels : new byte[Pixels.Length];
        CountingCodec codec = new(pixels) { FailEncode = failure };
        using InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
        host.AddInput(QpdfPath.From("input"), Fixture(0, "ordinary", true, pixels), QpdfInputOwnership.Transfer);
        using JsonDocument report = await Inspect(host, Optimize);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        Assert.Equal("5 0 R", Bindings(table, Page(table)).GetProperty("/Im").GetString());
        Assert.Equal(Payload(pixels, true), Data(table.GetProperty("obj:5 0 R").GetProperty("stream")));
        Assert.Equal(1, codec.Encodes);
    }

    internal static byte[] InlineFixture()
    {
        byte[] content = Encoding.ASCII.GetBytes("q BI /W 128 /H 128 /BPC 8 /CS /RGB /F /AHx ID " + Convert.ToHexString(Pixels) + "> EI Q");
        return PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /PrivateContents 4 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 128 128] /Resources << >> /Contents 4 0 R >>", null),
            (4, "<< /Length " + content.Length + " >>", content)], 1);
    }

    [Theory]
    [InlineData("implicit", true)]
    [InlineData("keep", false)]
    [InlineData("explicit", true)]
    [InlineData("threshold", false)]
    public async Task InlinePolicyDeterminesWhetherOptimizationCreatesAnImage(string mode, bool optimized)
    {
        CountingCodec codec = new(Pixels);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
        host.AddInput(QpdfPath.From("input"), InlineFixture(), QpdfInputOwnership.Transfer);
        List<string> options = [.. Optimize, "--ii-min-bytes=" + (mode == "threshold" ? "1000000" : "0")];
        if (mode is "keep" or "explicit") { options.Add("--keep-inline-images"); }
        if (mode == "explicit") { options.Add("--externalize-inline-images"); }
        using JsonDocument report = await Inspect(host, options);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1], page = Page(table);
        string content = page.GetProperty("/Contents").GetString() ?? throw new InvalidOperationException();
        byte[] active = Data(table.GetProperty("obj:" + content).GetProperty("stream"));
        Assert.Equal(!optimized, Encoding.ASCII.GetString(active).Contains("BI", StringComparison.Ordinal));
        Assert.Contains("BI", Encoding.ASCII.GetString(Data(table.GetProperty("obj:4 0 R").GetProperty("stream"))));
        if (optimized)
        {
            JsonProperty binding = Assert.Single(Bindings(table, page).EnumerateObject());
            Assert.Contains(binding.Name + " Do", Encoding.ASCII.GetString(active));
            JsonElement image = table.GetProperty("obj:" + binding.Value.GetString()).GetProperty("stream");
            Assert.Equal("/DCTDecode", image.GetProperty("dict").GetProperty("/Filter").GetString());
            Assert.InRange(Data(image).Length, 1, Pixels.Length - 1);
        }
        Assert.Equal(optimized ? 1 : 0, codec.Encodes);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public async Task JobMinimalAndReturnedSidecarsCarryTheOptimizedPayload(bool job, bool returned, bool sidecars)
    {
        CountingCodec codec = new(Pixels);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
        host.AddInput(QpdfPath.From("input"), Fixture(17, "inherited", true), QpdfInputOwnership.Transfer);
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["jsonOutput"] = "2", ["decodeLevel"] = "none",
            ["optimizeImages"] = true, ["oiMinWidth"] = "1", ["oiMinHeight"] = "1", ["oiMinArea"] = "1", ["jpegQuality"] = "75" };
        List<string> args = ["input", "--json=2", "--decode-level=none", "--json-stream-data=" + (sidecars ? "file" : "inline"), .. Optimize];
        if (sidecars) { args.Add("--json-stream-prefix=side"); }
        if (!returned) { args.Add("output"); settings["outputFile"] = "output"; }
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(args), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        byte[] json;
        if (returned) { json = Encoding.UTF8.GetBytes(result.OutputText ?? throw new InvalidOperationException()); }
        else { Assert.True(host.TryGetOutput(Output, out json)); }
        using JsonDocument report = JsonDocument.Parse(json);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        string reference = Bindings(table, Page(table)).GetProperty("/Im").GetString() ?? throw new InvalidOperationException();
        JsonElement image = table.GetProperty("obj:" + reference).GetProperty("stream");
        Assert.Equal("/DCTDecode", image.GetProperty("dict").GetProperty("/Filter").GetString());
        byte[] payload;
        if (sidecars) { Assert.True(host.TryGetOutput(QpdfPath.From(image.GetProperty("datafile").GetString() ?? throw new InvalidOperationException()), out payload)); }
        else { payload = Data(image); }
        Assert.InRange(payload.Length, 1, Pixels.Length - 1); Assert.Equal(1, codec.Encodes);
        if (!job)
        {
            JsonElement descriptors = report.RootElement.GetProperty("pages")[0].GetProperty("images");
            Assert.Equal(2, descriptors.GetArrayLength());
            Assert.All(descriptors.EnumerateArray(), descriptor => Assert.Equal(reference, descriptor.GetProperty("object").GetString()));
        }
    }

    [Fact]
    public async Task EmptyPrimaryAssemblyOptimizesTheCopiedSource()
    {
        CountingCodec codec = new(Pixels);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
        host.AddInput(QpdfPath.From("input"), Fixture(17, "ordinary", true), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["--empty", "--pages", "input", "1", "--", "--json-output=2", .. Optimize]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        using JsonDocument report = JsonDocument.Parse(result.OutputText ?? throw new InvalidOperationException());
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        string reference = Bindings(table, Page(table)).GetProperty("/Im").GetString() ?? throw new InvalidOperationException();
        Assert.Equal("/DCTDecode", table.GetProperty("obj:" + reference).GetProperty("stream").GetProperty("dict").GetProperty("/Filter").GetString());
        Assert.Equal(1, codec.Encodes); Assert.True(report.RootElement.GetProperty("qpdf")[0].GetProperty("pushedinheritedpageresources").GetBoolean());
    }

    private static async Task<JsonDocument> Inspect(InMemoryQpdfHost host, IReadOnlyList<string> options)
    {
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--json-output=2", .. options, "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); return JsonDocument.Parse(bytes);
    }
    private static JsonElement Value(JsonElement table, JsonElement value)
        => value.ValueKind == JsonValueKind.String ? table.GetProperty("obj:" + value.GetString()).GetProperty("value") : value;
    private static byte[] Data(JsonElement stream)
        => Convert.FromBase64String(stream.GetProperty("data").GetString() ?? throw new InvalidOperationException());

    private static JsonElement Page(JsonElement table)
    {
        JsonElement catalog = Value(table, table.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        return Value(table, Value(table, catalog.GetProperty("/Pages")).GetProperty("/Kids")[0]);
    }
    private static JsonElement Bindings(JsonElement table, JsonElement page)
    {
        for (int depth = 0; depth < 2; depth++)
        {
            if (page.TryGetProperty("/Resources", out JsonElement resources))
            { return Value(table, Value(table, resources).GetProperty("/XObject")); }
            page = Value(table, page.GetProperty("/Parent"));
        }
        throw new InvalidOperationException("Fixture resources are missing.");
    }

    private sealed class CountingCodec(ReadOnlyMemory<byte> expected) : IQpdfJpegCodec
    {
        internal int Encodes, Quality;
        internal bool FailEncode;
        public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
            => LokadImageJpegCodec.Instance.Decode(encoded, frame, samples, context);
        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
        {
            Encodes++; Quality = quality; Assert.True(samples.Span.SequenceEqual(expected.Span));
            if (FailEncode) { throw new IOException("Fixture encoder failure."); }
            LokadImageJpegCodec.Instance.Encode(samples, frame, quality, encoded, context);
        }
    }

    private sealed class CodecBudgetHost(IQpdfHost host) : IQpdfHost, IQpdfMemoryHost, IQpdfJpegCodecHost
    {
        public IMemoryBudget MemoryBudget => ((IQpdfMemoryHost)host).MemoryBudget ?? throw new InvalidOperationException("The admission view requires its caller budget.");
        public IQpdfJpegCodec JpegCodec => LokadImageJpegCodec.Instance;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => host.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => host.CreateOutputAsync(path, token);
    }
}
