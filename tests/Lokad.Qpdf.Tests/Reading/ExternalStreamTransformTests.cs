using System.IO.Compression;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class ExternalStreamTransformTests
{
    [Theory]
    [InlineData("normalize", false)]
    [InlineData("normalize", true)]
    [InlineData("coalesce", false)]
    [InlineData("coalesce", true)]
    [InlineData("externalize", false)]
    [InlineData("externalize", true)]
    [InlineData("overlay", false)]
    [InlineData("overlay", true)]
    public async Task SemanticContentTransformsRefuseExternalBytes(string operation, bool encoded)
    {
        using InMemoryQpdfHost host = new();
        byte[] source = ExternalStreamDataTests.Fixture(operation == "coalesce" ? "content-array" : "content", "file", encoded).Input;
        host.AddInput(QpdfPath.From("source"), source, QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("input"), operation == "overlay"
            ? ExternalStreamDataTests.Fixture("content", "absent", false).Input : source, QpdfInputOwnership.Transfer);
        foreach (bool job in new[] { false, true })
        {
            await Seed(host);
            string[] options = operation switch
            {
                "normalize" => ["--normalize-content=y"], "coalesce" => ["--coalesce-contents"],
                "externalize" => ["--externalize-inline-images", "--ii-min-bytes=0"], _ => ["--overlay", "source", "--"],
            };
            string settings = operation switch
            {
                "normalize" => "\"normalizeContent\":\"y\"", "coalesce" => "\"coalesceContents\":true",
                "externalize" => "\"externalizeInlineImages\":true,\"iiMinBytes\":\"0\"", _ => "\"overlay\":{\"file\":\"source\"}",
            };
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\"," + settings + "}", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", .. options, "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Contains(result.Diagnostics, x => x.Code == QpdfDiagnosticCode.UnsupportedFeature);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior);
            Assert.Equal(0L, host.OpenLeaseCount);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task JsonInspectionKeepsOpaqueEncodingAndPhysicalBytes(bool encoded, bool sidecar)
    {
        using InMemoryQpdfHost host = new();
        (byte[] input, byte[] physical) = ExternalStreamDataTests.Fixture("metadata", "file", encoded);
        host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--json=2", "--json-key=qpdf",
            "--decode-level=all", "--json-stream-data=" + (sidecar ? "file" : "inline"), "--json-stream-prefix=side", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        using JsonDocument json = JsonDocument.Parse(output);
        JsonElement stream = json.RootElement.GetProperty("qpdf")[1].GetProperty("obj:5 0 R").GetProperty("stream");
        JsonElement dictionary = stream.GetProperty("dict");
        Assert.Equal("u:external.bin", dictionary.GetProperty("/F").GetString());
        Assert.Equal("/ASCIIHexDecode", dictionary.GetProperty("/FFilter").GetString());
        if (encoded) { Assert.Equal("/FlateDecode", dictionary.GetProperty("/Filter").GetString()); }
        else { Assert.False(dictionary.TryGetProperty("/Filter", out _)); }
        byte[] actual;
        if (sidecar)
        {
            Assert.Equal("side-5", stream.GetProperty("datafile").GetString());
            Assert.True(host.TryGetOutput(QpdfPath.From("side-5"), out actual));
        }
        else { actual = Convert.FromBase64String(stream.GetProperty("data").GetString() ?? throw new Exception()); }
        Assert.Equal(physical, actual); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EligibleExternalImageFailsBeforeCallingCodec(bool job)
    {
        RejectingCodec codec = new(); using InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
        byte[] samples = new byte[32 * 32 * 3];
        host.AddInput(QpdfPath.From("input"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im 5 0 R >> >> /Contents 4 0 R >>", null),
            (4, "<< /Length 6 >>", "/Im Do"u8.ToArray()),
            (5, "<< /Subtype /Image /Width 32 /Height 32 /BitsPerComponent 8 /ColorSpace /DeviceRGB /F (external.bin) /Length 3072 >>", samples),
        ], 1), QpdfInputOwnership.Transfer);
        await Seed(host);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","optimizeImages":true,"oiMinWidth":"0","oiMinHeight":"0","oiMinArea":"0"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--optimize-images", "--oi-min-width=0", "--oi-min-height=0", "--oi-min-area=0", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode); Assert.Equal(0, codec.Calls);
        Assert.Contains(result.Diagnostics, x => x.Code == QpdfDiagnosticCode.UnsupportedFeature);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private sealed class RejectingCodec : IQpdfJpegCodec
    {
        internal int Calls;
        public void Decode(Stream source, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
        { Calls++; throw new InvalidDataException("Unexpected codec invocation."); }
        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream target, QpdfJpegCodecContext context)
        { Calls++; throw new InvalidDataException("Unexpected codec invocation."); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PruningKeepsNamespacesWhoseEffectiveContentIsUnavailable(bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /Keep << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> /Contents 4 0 R >>", null),
            (4, "<< /F (external.bin) /Length 4 >>", "q Q\n"u8.ToArray()),
        ], 1), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1"}],"removeUnreferencedResources":"yes"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1", "--", "--remove-unreferenced-resources=yes", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
        {
            Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject raw));
            PdfDictionary resources = Assert.IsType<PdfDictionary>(raw is PdfReference r ? document.GetObject(r.ObjectNumber) : raw);
            Assert.True(resources.TryGet(PdfName.FromAscii("Font"), out QpdfObject fontRaw));
            PdfDictionary fonts = Assert.IsType<PdfDictionary>(fontRaw is PdfReference f ? document.GetObject(f.ObjectNumber) : fontRaw);
            Assert.True(fonts.TryGet(PdfName.FromAscii("Keep"), out _));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AppearanceGenerationKeepsUnavailableProgramsAndFieldReadiness(bool encoded, bool flatten)
    {
        byte[] physical = "/Tx BMC\nEMC\n"u8.ToArray();
        if (encoded)
        {
            using MemoryStream buffer = new();
            using (ZLibStream codec = new(buffer, CompressionLevel.SmallestSize, true)) { codec.Write(physical); }
            physical = buffer.ToArray();
        }
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Audit 6 0 R /AcroForm << /Fields [5 0 R] /NeedAppearances true /DR << /Font << /F1 7 0 R >> >> /DA (/F1 12 Tf) >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R /Annots [5 0 R] >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Field) /V (Value) /Rect [10 10 90 30] /F 4 /P 3 0 R /AP << /N 6 0 R >> >>", null),
            (6, "<< /Type /XObject /Subtype /Form /BBox [0 0 80 20] /Resources << /Font << /F1 7 0 R >> >> /F (external) /Length " + physical.Length + (encoded ? " /Filter /FlateDecode" : "") + " >>", physical),
            (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", null),
        ], 1);
        foreach (bool job in new[] { false, true })
        {
            using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy);
            List<string> arguments = ["input", "--generate-appearances"];
            Dictionary<string, object> values = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["generateAppearances"] = true };
            if (flatten) { arguments.Add("--flatten-annotations=all"); values["flattenAnnotations"] = "all"; }
            arguments.Add("output");
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(values), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
            Assert.Contains(result.Diagnostics, x => x.Code == QpdfDiagnosticCode.UnsupportedFeature && x.Message.Contains("appearance stream", StringComparison.Ordinal));
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            host.AddInput(QpdfPath.From("copy"), output, QpdfInputOwnership.Transfer);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), CancellationToken.None))
            {
                QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(document.Engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject root) ? root : PdfNull.Instance));
                PdfDictionary form = Assert.IsType<PdfDictionary>(Resolve(catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject formValue) ? formValue : PdfNull.Instance));
                Assert.True(Assert.IsType<PdfBoolean>(form.TryGet(PdfName.FromAscii("NeedAppearances"), out QpdfObject need) ? need : PdfNull.Instance).Value);
                PdfDictionary page = document.PageReferences[0].Page;
                PdfArray annotations = Assert.IsType<PdfArray>(Resolve(page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject array) ? array : PdfNull.Instance));
                PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(Assert.Single(annotations.Items)));
                PdfDictionary ap = Assert.IsType<PdfDictionary>(Resolve(widget.TryGet(PdfName.FromAscii("AP"), out QpdfObject apValue) ? apValue : PdfNull.Instance));
                Assert.True(ap.TryGet(PdfName.FromAscii("N"), out QpdfObject appearance));
                PdfStream stream = Assert.IsType<PdfStream>(Resolve(appearance));
                Assert.Equal(physical, stream.Data);
                Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("F"), out _));
                Assert.True(catalog.TryGet(PdfName.FromAscii("Audit"), out QpdfObject alias)); Assert.Equal(alias, appearance);
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
    }

    private static async Task Seed(InMemoryQpdfHost host)
    {
        await using IQpdfOutputTransaction output = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await output.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await output.CommitAsync(CancellationToken.None);
    }
}
