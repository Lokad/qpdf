using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonImportCompressionTests
{
    private static readonly QpdfPath Graph = QpdfPath.From("graph"), Input = QpdfPath.From("input"), Output = QpdfPath.From("output");
    private static readonly byte[] Program = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("q 1 0 0 1 2 3 cm Q\n", 128)));
    private static readonly byte[] Encoded = Encode(CompressionLevel.Fastest);

    [Theory]
    [InlineData("default", false, true)]
    [InlineData("preserve", true, true)]
    [InlineData("preserve", false, false)]
    [InlineData("compress", false, true)]
    [InlineData("uncompress", true, false)]
    [InlineData("compress-no", true, false)]
    [InlineData("decode-none", true, true)]
    [InlineData("preserve-then-compress", false, true)]
    [InlineData("compress-then-preserve", false, false)]
    public async Task CreateUsesEffectiveCompressionSetters(string policy, bool encodedInput, bool flate)
    {
        using InMemoryQpdfHost host = CreateHost(encodedInput);
        string[] setters = policy switch
        {
            "default" => [],
            "compress-no" => ["--compress-streams=n"],
            "decode-none" => ["--compress-streams=n", "--decode-level=none"],
            "preserve-then-compress" => ["--stream-data=preserve", "--compress-streams=y"],
            "compress-then-preserve" => ["--compress-streams=y", "--stream-data=preserve"],
            _ => ["--stream-data=" + policy],
        };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([Graph.Value, "--json-input", .. setters, Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        byte[] raw = await Inspect(host, flate);
        if (encodedInput && flate) { Assert.Equal(Encoded, raw); }
    }

    [Fact]
    public async Task JobUpdateCompressesRetainedSourceStreamsAtRequestedLevel()
    {
        using InMemoryQpdfHost host = CreateHost(false);
        host.AddInput(QpdfPath.From("patch"), """{"qpdf":[{"jsonversion":2},{}]}"""u8.ToArray(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, """
            {"inputFile":"input","updateFromJson":"patch","compressionLevel":"9","outputFile":"output"}
            """, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(Encode(CompressionLevel.SmallestSize), await Inspect(host, true));
    }

    [Fact]
    public async Task TypedCreationKeepsEncodedPayloadBytes()
    {
        using InMemoryQpdfHost host = CreateHost(true);
        QpdfJsonImportResult result = await Qpdf.CreatePdfFromJsonAsync(host, Graph, Output, CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        Assert.Equal(Encoded, await Inspect(host, true));
    }

    [Fact]
    public async Task ExplicitRecompressionChangesTheStoredEncoding()
    {
        using InMemoryQpdfHost host = CreateHost(true);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new([Graph.Value, "--json-input", "--recompress-flate", "--compression-level=9", Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        byte[] recompressed = await Inspect(host, true);
        Assert.False(Encoded.AsSpan().SequenceEqual(recompressed));
        Assert.Equal(Encode(CompressionLevel.SmallestSize), recompressed);
    }

    [Fact]
    public async Task FilterLimitKeepsTheStreamOpaqueAndWarns()
    {
        byte[] nested = Encode(Encoded, CompressionLevel.Fastest);
        using InMemoryQpdfHost host = CreateHost(nested, "[\"/FlateDecode\",\"/FlateDecode\"]", "[/FlateDecode /FlateDecode]");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new([Graph.Value, "--json-input", "--stream-data=uncompress", "--max-stream-filters=1", Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.UnsupportedFeature
            && d.Message.Contains("kept opaque", StringComparison.Ordinal));
        Assert.Equal(0, result.Counters.TotalDecodedBytes);
        PdfStream content = await ReadContent(host);
        Assert.Equal(nested, content.Data);
        Assert.True(content.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filter));
        Assert.Equal(2, Assert.IsType<PdfArray>(filter).Count);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static InMemoryQpdfHost CreateHost(bool encoded)
        => encoded ? CreateHost(Encoded, "\"/FlateDecode\"", "/FlateDecode") : CreateHost(Program, null, null);

    private static InMemoryQpdfHost CreateHost(byte[] bytes, string? jsonFilter, string? pdfFilter)
    {
        string filter = jsonFilter is null ? "" : ",\"/Filter\":" + jsonFilter;
        string graph = $$$"""
            {"qpdf":[{"jsonversion":2,"pdfversion":"1.7"},{
              "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R"}},
              "obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["3 0 R"],"/Count":1}},
              "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,100],"/Resources":{},"/Contents":"4 0 R"}},
              "obj:4 0 R":{"stream":{"dict":{"/Length":{{{bytes.Length}}}{{{filter}}}},"data":{{{JsonSerializer.Serialize(Convert.ToBase64String(bytes))}}}}},
              "trailer":{"value":{"/Root":"1 0 R","/Size":5}}
            }]}
            """;
        InMemoryQpdfHost host = new();
        host.AddInput(Graph, Encoding.UTF8.GetBytes(graph), QpdfInputOwnership.Transfer);
        host.AddInput(Input, PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << >> >>", null),
            (4, $"<< /Length {bytes.Length}" + (pdfFilter is null ? "" : " /Filter " + pdfFilter) + " >>", bytes)], 1), QpdfInputOwnership.Transfer);
        return host;
    }

    private static async Task<byte[]> Inspect(InMemoryQpdfHost host, bool flate)
    {
        PdfStream stream = await ReadContent(host);
        Assert.Equal(flate, stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filter));
        if (flate) { Assert.Equal(PdfName.FromAscii("FlateDecode"), filter); }
        Assert.Equal(Program, StreamObservation.Decode(stream));
        return stream.Data;
    }

    private static async Task<PdfStream> ReadContent(InMemoryQpdfHost host)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] pdf));
        QpdfPath read = QpdfPath.From("read");
        host.AddInput(read, pdf, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, read, CancellationToken.None);
        Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents));
        return Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(contents)));
    }

    private static byte[] Encode(CompressionLevel level)
        => Encode(Program, level);

    private static byte[] Encode(byte[] bytes, CompressionLevel level)
    {
        using MemoryStream output = new();
        using (ZLibStream stream = new(output, level, true)) { stream.Write(bytes); }
        return output.ToArray();
    }
}
