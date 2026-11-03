using System.Globalization;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class FreshAppearanceRectangleTests
{
    private static byte[] Fixture(int[] values, string shape)
    {
        string literals = "[" + string.Join(" ", values) + "]";
        string array = shape == "coordinates" ? "[7 0 R 8 0 R 9 0 R 10 0 R]" : literals;
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Audit 6 0 R /AcroForm << /Fields [4 0 R] /NeedAppearances true /DA (/F1 12 Tf 0 g) /DR << /Font << /F1 5 0 R >> >> >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Subtype /Widget /FT /Tx /T (Text) /V (HELLO) /F 4 /Rect " + (shape == "array" ? "6 0 R" : array) + " >>", null),
            (5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", null), (6, array, null),
            (7, values[0].ToString(CultureInfo.InvariantCulture), null), (8, values[1].ToString(CultureInfo.InvariantCulture), null),
            (9, values[2].ToString(CultureInfo.InvariantCulture), null), (10, values[3].ToString(CultureInfo.InvariantCulture), null)], 1);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool reverseX in new[] { false, true })
        foreach (bool reverseY in new[] { false, true })
        foreach (string shape in new[] { "direct", "array", "coordinates" })
        foreach (bool flatten in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [reverseX, reverseY, shape, flatten, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FreshAppearanceUsesNormalizedDimensionsAndPreservesRawRectangles(bool reverseX, bool reverseY, string shape, bool flatten, bool job)
    {
        int[] values = [reverseX ? 80 : 10, reverseY ? 50 : 20, reverseX ? 10 : 80, reverseY ? 20 : 50];
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(values, shape), QpdfInputOwnership.Copy);
        List<string> argv = ["input", "--generate-appearances", "--compress-streams=n", "--decode-level=none", "--remove-unreferenced-resources=no"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["generateAppearances"] = true, ["compressStreams"] = "n", ["decodeLevel"] = "none", ["removeUnreferencedResources"] = "no" };
        if (flatten) { argv.Add("--flatten-annotations=all"); settings["flattenAnnotations"] = "all"; } argv.Add("output");
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None) : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        QpdfObject Get(PdfDictionary dictionary, string key) { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(value); }
        double[] Numbers(PdfArray array)
        {
            double[] result = new double[array.Count];
            for (int index = 0; index < array.Count; index++) { result[index] = Resolve(array[index]) switch { PdfInteger integer => integer.Value, PdfReal real => real.Value, _ => throw new InvalidOperationException() }; }
            return result;
        }
        PdfDictionary page = document.PageReferences[0].Page; PdfStream generated;
        if (flatten)
        {
            Assert.False(page.TryGet(PdfName.FromAscii("Annots"), out _));
            PdfDictionary objects = Assert.IsType<PdfDictionary>(Get(Assert.IsType<PdfDictionary>(Get(page, "Resources")), "XObject"));
            generated = Assert.IsType<PdfStream>(Get(objects, "Fxo1"));
        }
        else
        {
            PdfArray annotations = Assert.IsType<PdfArray>(Get(page, "Annots")); Assert.Equal(1, annotations.Count);
            PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(annotations[0]));
            Assert.Equal(values.Select(i => (double)i), Numbers(Assert.IsType<PdfArray>(Get(widget, "Rect"))));
            generated = Assert.IsType<PdfStream>(Get(Assert.IsType<PdfDictionary>(Get(widget, "AP")), "N"));
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(Get(document.Engine.Trailer, "Root"));
            PdfDictionary form = Assert.IsType<PdfDictionary>(Get(catalog, "AcroForm"));
            if (form.TryGet(PdfName.FromAscii("NeedAppearances"), out QpdfObject need))
            { Assert.False(Assert.IsType<PdfBoolean>(Resolve(need)).Value); }
        }
        Assert.Equal(new double[] { 0, 0, 70, 30 }, Numbers(Assert.IsType<PdfArray>(Get(generated.Dictionary, "BBox"))));
        Assert.Contains("HELLO", Encoding.ASCII.GetString(generated.RawMemory.Span));
        PdfDictionary root = Assert.IsType<PdfDictionary>(Get(document.Engine.Trailer, "Root"));
        Assert.Equal(values.Select(i => (double)i), Numbers(Assert.IsType<PdfArray>(Get(root, "Audit"))));
    }
}
