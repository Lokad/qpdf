using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.ImageSharp;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class DctParameterTests
{
    private static readonly byte[] Jpeg = EncodeJpeg();
    private static readonly QpdfPath Input = QpdfPath.From("input");
    private static readonly QpdfPath Output = QpdfPath.From("output");

    public static IEnumerable<object[]> Cases()
    {
        foreach (string form in new[] { "absent", "null", "empty", "ct0", "ct1", "ct99", "unknown", "indirect", "array-null", "array-dict", "chain" })
        foreach (string operation in new[] { "extract", "rewrite", "optimize" })
        foreach (bool job in new[] { false, true }) { yield return [form, operation, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ParametersGateCodecUseAndPreserveOpaqueImages(string form, string operation, bool job)
    {
        bool unsupported = form is not ("absent" or "null" or "array-null");
        (byte[] pdf, byte[] encoded) = Fixture(form);
        RecordingCodec codec = new();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
        host.AddInput(Input, pdf, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, operation, job, CancellationToken.None);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(unsupported ? 0 : 1, codec.Decodes);
        Assert.Equal(!unsupported && operation == "optimize" ? 1 : 0, codec.Encodes);
        if (unsupported && operation == "extract")
        {
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Null(result.OutputBytes);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains("DecodeParms", StringComparison.Ordinal));
            Assert.False(host.TryGetOutput(Output, out _));
            return;
        }
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        if (operation == "extract")
        {
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Equal(32 * 32 * 3, Assert.IsType<ReadOnlyMemory<byte>>(result.OutputBytes).Length);
            return;
        }
        Assert.True(host.TryGetOutput(Output, out byte[] output));
        (PdfStream image, PdfDictionary? parameters) = await ReadImage(host, output);
        host.Dispose(); // Returned immutable image data survives the input/host leases.
        if (unsupported)
        {
            Assert.Equal(encoded, image.Data);
            Assert.NotNull(parameters);
            Assert.True(image.Dictionary.TryGet(PdfName.FromAscii("Filter"), out _));
            if (form == "empty") { Assert.Equal(0, parameters.Count); }
            else
            {
                Assert.True(parameters.TryGet(PdfName.FromAscii(form == "unknown" ? "Unknown" : "ColorTransform"), out QpdfObject value));
                Assert.Equal(form == "ct99" ? 99L : form is "ct1" or "unknown" ? 1L : 0L, Assert.IsType<PdfInteger>(value).Value);
            }
            if (operation == "rewrite") { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.UnsupportedFeature); }
        }
        else
        {
            Assert.Null(parameters);
            Assert.Equal(operation == "optimize", image.Dictionary.TryGet(PdfName.FromAscii("Filter"), out _));
            if (operation == "rewrite") { Assert.Equal(3072, image.Data.Length); }
            else { Assert.True(image.Data.Length < Jpeg.Length); }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnsupportedOptimizationNeedsNeitherCodecNorDecodedSamples(bool job, bool codecPresent)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = 1L };
        RecordingCodec codec = new();
        using InMemoryQpdfHost host = new(limits, codecPresent ? codec : null);
        (byte[] pdf, byte[] encoded) = Fixture("ct0");
        host.AddInput(Input, pdf, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, "optimize", job, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, codec.Decodes); Assert.Equal(0, codec.Encodes);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(Output, out byte[] output));
        using InMemoryQpdfHost reader = new();
        (PdfStream image, _) = await ReadImage(reader, output);
        Assert.Equal(encoded, image.Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedExtractionPreservesPriorTarget(bool job)
    {
        RecordingCodec codec = new(); using InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
        host.AddInput(Input, Fixture("ct0").Pdf, QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Run(host, "extract", job, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Null(result.OutputBytes);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("DecodeParms", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        Assert.Equal(new byte[] { 17, 23, 41 }, bytes);
        Assert.Equal(0, codec.Decodes); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedDecodingIsReportedAsSkipped(bool codecPresent)
    {
        RecordingCodec codec = new(); using InMemoryQpdfHost host = new(QpdfLimits.Default, codecPresent ? codec : null);
        host.AddInput(Input, Fixture("ct0").Pdf, QpdfInputOwnership.Transfer);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, Input, new QpdfCheckOptions { DecodeLevel = QpdfStreamDecodeLevel.All }, QpdfLimits.Default, CancellationToken.None);
        QpdfCheckOutcome streams = Assert.Single(result.Checks, outcome => outcome.Name == "streams");
        Assert.Equal(QpdfCheckStatus.Skipped, streams.Status);
        Assert.Contains("DecodeParms", streams.Detail, StringComparison.Ordinal);
        Assert.Equal(0, codec.Decodes); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationPrecedesParameterizedExtraction(bool job)
    {
        RecordingCodec codec = new(); using InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
        host.AddInput(Input, Fixture("ct0").Pdf, QpdfInputOwnership.Transfer);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        QpdfExecutionResult result = await Run(host, "extract", job, cancellation.Token);
        Assert.Equal(QpdfExecutionStatus.Canceled, result.Status); Assert.Null(result.OutputBytes);
        Assert.Equal(0, codec.Decodes); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonEligibilityAndInlineBytesUseTheSameParameterBoundary(bool unsupported)
    {
        RecordingCodec codec = new(); using InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
        host.AddInput(Input, Fixture(unsupported ? "ct0" : "null").Pdf, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--json=2", "--json-stream-data=inline", "--decode-level=all", "--json-key=qpdf", "--json-key=pages"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
        JsonElement image = json.RootElement.GetProperty("pages")[0].GetProperty("images")[0];
        Assert.Equal(!unsupported, image.GetProperty("filterable").GetBoolean());
        JsonElement stream = json.RootElement.GetProperty("qpdf")[1].GetProperty("obj:5 0 R").GetProperty("stream");
        byte[] bytes = Convert.FromBase64String(Assert.IsType<string>(stream.GetProperty("data").GetString()));
        if (unsupported)
        {
            Assert.Equal(Jpeg, bytes);
            Assert.True(stream.GetProperty("dict").TryGetProperty("/DecodeParms", out _));
        }
        else { Assert.Equal(3072, bytes.Length); }
        Assert.Equal(unsupported ? 0 : 1, codec.Decodes); Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static ValueTask<QpdfExecutionResult> Run(InMemoryQpdfHost host, string operation, bool job, CancellationToken token)
    {
        if (job)
        {
            Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["decodeLevel"] = operation == "optimize" ? "none" : "all", ["compressStreams"] = "n" };
            if (operation == "extract") { settings["showObject"] = "5"; settings["filteredStreamData"] = ""; }
            if (operation != "extract") { settings["outputFile"] = "output"; }
            if (operation == "optimize")
            { settings["optimizeImages"] = ""; settings["jpegQuality"] = "1"; settings["oiMinWidth"] = "0"; settings["oiMinHeight"] = "0"; settings["oiMinArea"] = "0"; settings["keepInlineImages"] = ""; }
            return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), token);
        }
        List<string> flags = ["input", operation == "optimize" ? "--decode-level=none" : "--decode-level=all", "--compress-streams=n"];
        if (operation == "extract") { flags.AddRange(["--show-object=5", "--filtered-stream-data"]); }
        if (operation == "optimize") { flags.AddRange(["--optimize-images", "--jpeg-quality=1", "--oi-min-width=0", "--oi-min-height=0", "--oi-min-area=0", "--keep-inline-images"]); }
        if (operation != "extract") { flags.AddRange(["--", "output"]); }
        return Qpdf.ExecuteAsync(host, new(flags), token);
    }

    private static (byte[] Pdf, byte[] Encoded) Fixture(string form)
    {
        string filter = form is "array-null" or "array-dict" ? "[/DCTDecode]" : form == "chain" ? "[/ASCIIHexDecode /DCTDecode]" : "/DCTDecode";
        string parameters = form switch
        {
            "absent" => "", "null" => "/DecodeParms null", "empty" => "/DecodeParms << >>",
            "indirect" => "/DecodeParms 6 0 R", "array-null" => "/DecodeParms [null]",
            "array-dict" => "/DecodeParms [<< /ColorTransform 0 >>]", "chain" => "/DecodeParms [null << /ColorTransform 0 >>]",
            "unknown" => "/DecodeParms << /Unknown 1 >>", _ => "/DecodeParms << /ColorTransform " + form[2..] + " >>"
        };
        byte[] encoded = form == "chain" ? Encoding.ASCII.GetBytes(Convert.ToHexString(Jpeg) + ">") : Jpeg;
        byte[] content = "q 32 0 0 32 0 0 cm /Im0 Do Q\n"u8.ToArray();
        byte[] pdf = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 32 32] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>", null),
            (4, $"<< /Length {content.Length} >>", content),
            (5, $"<< /Type /XObject /Subtype /Image /Width 32 /Height 32 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter {filter} {parameters} /Length {encoded.Length} >>", encoded),
            (6, "<< /ColorTransform 0 >>", null)
        ], 1);
        return (pdf, encoded);
    }

    private static async Task<(PdfStream Image, PdfDictionary? Parameters)> ReadImage(InMemoryQpdfHost host, byte[] output)
    {
        QpdfPath path = QpdfPath.From("readback"); host.AddInput(path, output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, path, CancellationToken.None);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(Property(document, document.PageReferences[0].Page, "Resources"));
        PdfDictionary objects = Assert.IsType<PdfDictionary>(Property(document, resources, "XObject"));
        PdfStream image = Assert.IsType<PdfStream>(Property(document, objects, "Im0"));
        PdfDictionary? parameters = null;
        if (image.Dictionary.TryGet(PdfName.FromAscii("DecodeParms"), out QpdfObject raw) && raw is not PdfNull)
        {
            raw = Resolve(document, raw); if (raw is PdfArray array) { raw = Resolve(document, array.Items.Last()); }
            parameters = Assert.IsType<PdfDictionary>(raw);
        }
        return (image, parameters);
    }

    private static QpdfObject Property(QpdfDocument document, PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject raw)); return Resolve(document, raw); }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    private static byte[] EncodeJpeg()
    {
        using Image<Rgb24> image = new(32, 32);
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) { image[x, y] = new Rgb24((byte)(x * 7), (byte)(y * 7), (byte)((x * y) % 256)); }
        using MemoryStream stream = new(); image.Save(stream, new JpegEncoder { Quality = 100 }); return stream.ToArray();
    }

    private sealed class RecordingCodec : IQpdfJpegCodec
    {
        internal int Decodes; internal int Encodes;
        public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
        { Decodes++; ImageSharpJpegCodec.Instance.Decode(encoded, frame, samples, context); }
        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
        { Encodes++; ImageSharpJpegCodec.Instance.Encode(samples, frame, quality, encoded, context); }
    }
}
