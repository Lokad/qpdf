using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayRectangleTests
{
    private static byte[] Target(string key, int[] values, bool indirect)
    {
        string raw = indirect ? "6 0 R" : "[" + string.Join(" ", values) + "]";
        string media = key == "MediaBox" ? raw : "[0 0 100 200]";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Audit 6 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox " + media + (key == "MediaBox" ? "" : " /" + key + " " + raw) + " /Resources << >> >>", null),
            (4, "null", null), (5, "null", null),
            (6, indirect ? "[7 0 R 8 0 R 9 0 R 10 0 R]" : "[" + string.Join(" ", values) + "]", null),
            (7, values[0].ToString(CultureInfo.InvariantCulture), null), (8, values[1].ToString(CultureInfo.InvariantCulture), null),
            (9, values[2].ToString(CultureInfo.InvariantCulture), null), (10, values[3].ToString(CultureInfo.InvariantCulture), null)], 1);
    }

    private static byte[] Source()
    {
        byte[] content = "1 0 0 rg 5 10 15 30 re f"u8.ToArray();
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R /Annots [5 0 R] >>", null),
            (4, "<< /Length " + content.Length + " >>", content),
            (5, "<< /Subtype /Link /Rect [10 20 30 40] /A << /S /URI /URI (https://example.test) >> >>", null)], 1);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string key in new[] { "CropBox", "TrimBox", "MediaBox" })
        foreach (bool reverseX in new[] { false, true })
        foreach (bool reverseY in new[] { false, true })
        foreach (bool indirect in new[] { false, true })
        foreach (bool job in new[] { false, true })
        foreach (bool underlay in new[] { false, true }) { yield return [key, reverseX, reverseY, indirect, job, underlay]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OpposingCornersPreserveIndependentPlacementAndOriginalBoxValues(string key, bool reverseX, bool reverseY, bool indirect, bool job, bool underlay)
    {
        int[] values = [reverseX ? 90 : 10, reverseY ? 180 : 20, reverseX ? 10 : 90, reverseY ? 20 : 180];
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Target(key, values, indirect), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("stamp"), Source(), QpdfInputOwnership.Copy);
        string mode = underlay ? "underlay" : "overlay";
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new Dictionary<string, object> { ["inputFile"] = "input", ["outputFile"] = "output", ["compressStreams"] = "n", ["decodeLevel"] = "none", [mode] = new { file = "stamp" } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--compress-streams=n", "--decode-level=none", "--" + mode, "stamp", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        QpdfObject Get(PdfDictionary owner, string name) { Assert.True(owner.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return Resolve(value); }
        double[] Numbers(PdfArray array)
        {
            double[] result = new double[array.Count];
            for (int i = 0; i < array.Count; i++) { result[i] = Resolve(array[i]) switch { PdfInteger integer => integer.Value, PdfReal real => real.Value, _ => throw new InvalidOperationException() }; }
            return result;
        }
        PdfDictionary page = document.PageReferences[0].Page;
        PdfStream content = Assert.IsType<PdfStream>(Get(page, "Contents")); string program = Encoding.ASCII.GetString(content.RawMemory.Span);
        Match match = Regex.Match(program, @"([-+0-9.eE]+) ([-+0-9.eE]+) ([-+0-9.eE]+) ([-+0-9.eE]+) ([-+0-9.eE]+) ([-+0-9.eE]+) cm\s+/Fx1 Do"); Assert.True(match.Success, program);
        double[] matrix = Enumerable.Range(1, 6).Select(i => double.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(new double[] { .8, 0, 0, .8, 10, 60 }, matrix);
        PdfArray annotations = Assert.IsType<PdfArray>(Get(page, "Annots")); Assert.Equal(1, annotations.Count);
        PdfDictionary annotation = Assert.IsType<PdfDictionary>(Resolve(annotations[0]));
        Assert.Equal(new double[] { 18, 76, 34, 92 }, Numbers(Assert.IsType<PdfArray>(Get(annotation, "Rect"))));
        Assert.Equal(values.Select(i => (double)i), Numbers(Assert.IsType<PdfArray>(Get(page, key))));
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Get(document.Engine.Trailer, "Root"));
        Assert.Equal(values.Select(i => (double)i), Numbers(Assert.IsType<PdfArray>(Get(catalog, "Audit"))));
    }
}
