using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Content;

public sealed class InlineFramingContextTests
{
    private static (byte[] Pdf, byte[] Program) Fixture(string shape, bool reverse, bool inherited)
    {
        byte[] samples = Enumerable.Repeat((byte)64, 64).ToArray();
        byte[] rest = [.. " EI /Span << /ActualText (a\rline) >> BDC EMC "u8];
        byte[] pixels = [.. samples, .. rest, .. Enumerable.Repeat((byte)' ', 192 - samples.Length - rest.Length)];
        string filter = "";
        if (shape == "filtered")
        {
            using MemoryStream encoded = new();
            using (ZLibStream encoder = new(encoded, CompressionLevel.NoCompression, true)) { encoder.Write(pixels); }
            pixels = encoded.ToArray(); filter = "/F /Fl ";
        }
        byte[] program = [.. Encoding.ASCII.GetBytes("BX\r\nBI /W 8 /H 8 /BPC 8 /CS " + (shape == "device" ? "/G " : "/Local ") + filter + "ID "), .. pixels, .. " EI\nEX\r\n"u8];
        string first = shape == "missing" ? "<< >>" : "<< /ColorSpace << /Local /DeviceGray >> >>";
        string second = "<< /ColorSpace << /Local /DeviceRGB >> >>";
        if (reverse) { (first, second) = (second, first); }
        List<(int Number, string Dict, byte[]? StreamData)> records = [
            (1, "<< /Type /Catalog /Pages 2 0 R /Original 7 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R 6 0 R] /Count 4 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 8 0 R /Contents 7 0 R >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 8 0 R /Contents 7 0 R >>", null),
            (5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 9 0 R /Contents 7 0 R >>", null),
            (6, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 9 0 R /Contents 7 0 R >>", null),
            (7, "<< /Length " + program.Length + " >>", program), (8, first, null), (9, second, null)];
        if (inherited)
        {
            records[1] = (2, "<< /Type /Pages /Kids [10 0 R 11 0 R] /Count 4 /MediaBox [0 0 100 100] >>", null);
            for (int number = 3; number <= 6; number++)
            {
                int parent = number < 5 ? 10 : 11;
                records[number - 1] = (number, $"<< /Type /Page /Parent {parent} 0 R /Contents 7 0 R >>", null);
            }
            records.Add((10, "<< /Type /Pages /Parent 2 0 R /Count 2 /Kids [3 0 R 4 0 R] /Resources 8 0 R >>", null));
            records.Add((11, "<< /Type /Pages /Parent 2 0 R /Count 2 /Kids [5 0 R 6 0 R] /Resources 9 0 R >>", null));
        }
        return (PdfTestDocument.Assemble(records, 1), program);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string shape in new[] { "named", "device", "filtered", "missing" })
        foreach (bool reverse in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [shape, reverse, job]; }
    }

    [Theory]
    [InlineData("named", true)]
    [InlineData("device", false)]
    public async Task QdfImportKeepsSharedStreamsAndLeavesAmbiguousFramingOpaque(string shape, bool ambiguous)
    {
        (byte[] pdf, byte[] program) = Fixture(shape, false, false);
        using InMemoryQpdfHost host = new();
        QpdfPath graph = QpdfPath.From("graph"), output = QpdfPath.From("output"), published = QpdfPath.From("published");
        host.AddInput(QpdfPath.From("input"), pdf, QpdfInputOwnership.Transfer);
        QpdfExecutionResult export = await Qpdf.ExecuteAsync(host, new(["input", "--json-output=2", "--json-stream-data=inline", "graph"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, export.ExitCode);
        Assert.True(host.TryGetOutput(graph, out byte[] model));
        host.AddInput(graph, model, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["graph", "--json-input", "--qdf", "--stream-data=preserve", "output"]), CancellationToken.None);
        Assert.Equal(ambiguous ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
        if (ambiguous)
        { Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("resource contexts differ", StringComparison.Ordinal)); }
        Assert.True(host.TryGetOutput(output, out byte[] bytes));
        host.AddInput(published, bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, published, CancellationToken.None);
        Assert.Equal(4, document.PageCount);
        PdfReference[] references = document.PageReferences.Select(location =>
        {
            Assert.True(location.Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents));
            return Assert.IsType<PdfReference>(contents);
        }).ToArray();
        Assert.Single(references.Distinct());
        PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(references[0]));
        if (ambiguous) { Assert.Equal(program, stream.RawMemory.ToArray()); }
        else { Assert.True(stream.RawMemory.Span.StartsWith("BX\n"u8)); }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NormalizationSharesOnlyContextIndependentOrMatchingResourceUses(string shape, bool reverse, bool job)
    {
        (byte[] pdf, byte[] original) = Fixture(shape, reverse, false);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), pdf, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","normalizeContent":"y","compressStreams":"n","decodeLevel":"none","removeUnreferencedResources":"no"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--normalize-content=y", "--compress-streams=n", "--decode-level=none", "--remove-unreferenced-resources=no", "output"]), CancellationToken.None);
        Assert.Equal(shape == "missing" ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(shape == "missing" ? 1 : 0, result.Diagnostics.Count); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        List<PdfReference> references = [];
        foreach (var page in document.PageReferences)
        { Assert.True(page.Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject raw)); references.Add(Assert.IsType<PdfReference>(raw)); }
        Assert.Equal(references[0], references[1]); Assert.Equal(references[2], references[3]);
        if (shape is "device" or "filtered") { Assert.Equal(references[0], references[2]); }
        else { Assert.NotEqual(references[0], references[2]); }
        PdfStream first = Assert.IsType<PdfStream>(document.GetObject(references[0].ObjectNumber));
        PdfStream second = Assert.IsType<PdfStream>(document.GetObject(references[2].ObjectNumber));
        if (shape == "named")
        {
            PdfStream gray = reverse ? second : first, rgb = reverse ? first : second;
            Assert.Contains("(a\\nline)", Encoding.ASCII.GetString(gray.RawMemory.Span));
            Assert.True(rgb.RawMemory.Span.IndexOf("(a\rline)"u8) >= 0);
        }
        if (shape == "missing") { Assert.Equal(original, (reverse ? second : first).RawMemory.ToArray()); }
        Assert.True(document.Engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject root));
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(Assert.IsType<PdfReference>(root).ObjectNumber));
        Assert.True(catalog.TryGet(PdfName.FromAscii("Original"), out QpdfObject alias));
        Assert.Equal(original, Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(alias).ObjectNumber)).RawMemory.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundNormalizationUsesEffectiveResourceContexts(bool inherited)
    {
        (byte[] pdf, byte[] original) = Fixture("named", false, inherited);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), pdf, QpdfInputOwnership.Transfer);
        QpdfExecutionResult exported = await Qpdf.ExecuteAsync(host, new(["input", "--json-output=2", "--json-stream-data=inline"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, exported.ExitCode);
        string model = Assert.IsType<string>(exported.OutputText);
        using (JsonDocument graph = JsonDocument.Parse(model))
        {
            JsonElement objects = graph.RootElement.GetProperty("qpdf")[1];
            JsonElement page = objects.GetProperty("obj:3 0 R").GetProperty("value");
            Assert.Equal(!inherited, page.TryGetProperty("/Resources", out _));
            JsonElement owner = inherited ? objects.GetProperty("obj:10 0 R").GetProperty("value") : page;
            Assert.Equal("8 0 R", owner.GetProperty("/Resources").GetString());
        }
        host.AddInput(QpdfPath.From("graph"), Encoding.UTF8.GetBytes(model), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["graph", "--json-input", "--normalize-content=y", "--stream-data=preserve", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("published"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("published"), CancellationToken.None);
        Assert.Equal(4, document.PageCount);
        PdfReference[] contents = document.PageReferences.Select(page =>
        {
            Assert.True(page.Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject raw));
            return Assert.IsType<PdfReference>(raw);
        }).ToArray();
        Assert.Equal(contents[0], contents[1]);
        Assert.Equal(contents[2], contents[3]);
        Assert.NotEqual(contents[0], contents[2]);
        PdfStream gray = Assert.IsType<PdfStream>(document.GetObject(contents[0]));
        PdfStream rgb = Assert.IsType<PdfStream>(document.GetObject(contents[2]));
        Assert.True(gray.RawMemory.Span.IndexOf("(a\\nline)"u8) >= 0);
        Assert.True(rgb.RawMemory.Span.IndexOf("(a\rline)"u8) >= 0);
        Assert.True(document.Engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject root));
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(Assert.IsType<PdfReference>(root)));
        Assert.True(catalog.TryGet(PdfName.FromAscii("Original"), out QpdfObject alias));
        Assert.Equal(original, Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(alias))).RawMemory.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonNormalizationKeepsInheritedInlineImageContextsDistinct(bool imported)
    {
        (byte[] pdf, byte[] original) = Fixture("named", false, true);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), pdf, QpdfInputOwnership.Transfer);
        if (imported)
        {
            QpdfExecutionResult source = await Qpdf.ExecuteAsync(host,
                new(["input", "--json-output=2"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, source.ExitCode);
            host.AddInput(QpdfPath.From("graph"), Encoding.UTF8.GetBytes(Assert.IsType<string>(source.OutputText)),
                QpdfInputOwnership.Transfer);
        }
        string[] arguments = imported ? ["graph", "--json-input"] : ["input"];
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new([.. arguments, "--json=2", "--json-stream-data=inline", "--normalize-content=y"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        using JsonDocument report = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        string[] contents = report.RootElement.GetProperty("pages").EnumerateArray()
            .Select(page => Assert.IsType<string>(Assert.Single(page.GetProperty("contents").EnumerateArray()).GetString())).ToArray();
        Assert.Equal(4, contents.Length);
        Assert.Equal(contents[0], contents[1]);
        Assert.Equal(contents[2], contents[3]);
        Assert.NotEqual(contents[0], contents[2]);
        Assert.True(Bytes(contents[0]).AsSpan().IndexOf("(a\\nline)"u8) >= 0);
        Assert.True(Bytes(contents[2]).AsSpan().IndexOf("(a\rline)"u8) >= 0);
        Assert.Equal(original, Bytes("7 0 R"));
        Assert.Equal(0L, host.OpenLeaseCount);

        byte[] Bytes(string reference)
            => Convert.FromBase64String(Assert.IsType<string>(
                table.GetProperty("obj:" + reference).GetProperty("stream").GetProperty("data").GetString()));
    }
}
