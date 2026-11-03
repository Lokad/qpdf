using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class InlineFramingContextTests
{
    private static (byte[] Pdf, byte[] Program) Fixture(string shape, bool reverse)
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
        return (CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Original 7 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R 6 0 R] /Count 4 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 8 0 R /Contents 7 0 R >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 8 0 R /Contents 7 0 R >>", null),
            (5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 9 0 R /Contents 7 0 R >>", null),
            (6, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 9 0 R /Contents 7 0 R >>", null),
            (7, "<< /Length " + program.Length + " >>", program), (8, first, null), (9, second, null)], 1), program);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string shape in new[] { "named", "device", "filtered", "missing" })
        foreach (bool reverse in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [shape, reverse, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NormalizationSharesOnlyContextIndependentOrMatchingResourceUses(string shape, bool reverse, bool job)
    {
        (byte[] pdf, byte[] original) = Fixture(shape, reverse);
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
}
