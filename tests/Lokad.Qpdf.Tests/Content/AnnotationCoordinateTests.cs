using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AnnotationCoordinateTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string subtype in new[] { "Link", "Highlight", "Underline", "Squiggly", "StrikeOut", "Line", "Polygon", "PolyLine", "Ink", "FreeText" })
        foreach (string mode in new[] { "flatten", "overlay", "underlay" })
        foreach (bool indirect in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [subtype, mode, indirect, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AbsoluteSubtypePointsFollowThePageFrameAndRetainOriginalAliases(
        string subtype, string mode, bool indirect, bool job)
    {
        (string key, int[] points) = subtype switch
        {
            "Line" => ("L", new[] { 10, 20, 30, 40 }),
            "Polygon" or "PolyLine" => ("Vertices", new[] { 10, 20, 30, 20, 30, 40 }),
            "Ink" => ("InkList", new[] { 10, 20, 30, 40 }),
            "FreeText" => ("CL", new[] { 10, 20, 20, 30, 30, 40 }),
            _ => ("QuadPoints", new[] { 10, 20, 30, 20, 30, 40, 10, 40 }),
        };
        string coordinates = "[" + string.Join(" ", points.Select((value, index) => indirect && index < 2 ? (7 + index) + " 0 R" : value.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
        if (subtype == "Ink") { coordinates = "[" + coordinates + " [20 30]]"; }
        RecordingMemoryBudget budget = new(32L * 1048576L);
        using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Rotate 90 /Resources << /Font << /F1 10 0 R >> >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype " + (indirect ? "5 0 R" : "/" + subtype)
                + " /Rect [10 20 40 40] /F 4 /Contents (text) /DA (0 g /F1 12 Tf) /A << /S /URI /URI (https://example.test) >> /"
                + key + " " + (indirect ? "6 0 R" : coordinates) + " /Private 6 0 R /PrivateDictionary 9 0 R >>", null),
            (5, "/" + subtype, null), (6, coordinates, null), (7, "10", null), (8, "20", null),
            (9, "<< /Subtype /Private /" + key + " 6 0 R >>", null),
            (10, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null)], 1);
        byte[] target = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << >> >>", null)], 1);
        host.AddInput(QpdfPath.From("input"), mode == "flatten" ? source : target, QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), source, QpdfInputOwnership.Copy);
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
        if (mode == "flatten") { settings["flattenRotation"] = true; }
        else { settings[mode] = new { file = "stamp" }; }
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(mode == "flatten" ? ["input", "--flatten-rotation", "output"]
                : ["input", "--" + mode, "stamp", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        PdfArray escaped;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
        {
            QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
            QpdfObject Get(PdfDictionary dictionary, string name)
            { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return Resolve(value); }
            double[] Numbers(QpdfObject value)
            {
                PdfArray array = Assert.IsType<PdfArray>(Resolve(value));
                return array.Items.SelectMany(item => Resolve(item) is PdfArray nested ? Numbers(nested)
                    : new[] { Resolve(item) is PdfInteger integer ? (double)integer.Value : Assert.IsType<PdfReal>(Resolve(item)).Value }).ToArray();
            }
            PdfArray annotations = Assert.IsType<PdfArray>(Get(document.PageReferences[0].Page, "Annots"));
            PdfDictionary annotation = Assert.IsType<PdfDictionary>(Resolve(Assert.Single(annotations.Items)));
            escaped = Assert.IsType<PdfArray>(Get(annotation, "Private"));
            Assert.Same(escaped, Get(Assert.IsType<PdfDictionary>(Get(annotation, "PrivateDictionary")), key));
            double[] original = subtype == "Ink" ? points.Select(value => (double)value).Concat([20.0, 30.0]).ToArray()
                : points.Select(value => (double)value).ToArray();
            Assert.Equal(original, Numbers(escaped));
            double[] expected = new double[original.Length];
            for (int index = 0; index < expected.Length; index += 2)
            { expected[index] = original[index + 1] + (mode == "flatten" ? 0 : 100); expected[index + 1] = 100 - original[index] + (mode == "flatten" ? 0 : 150); }
            QpdfObject changed = Get(annotation, key);
            Assert.Equal(expected, Numbers(changed)); Assert.NotSame(escaped, changed);
        }
        host.Dispose(); Assert.NotEmpty(escaped.Items);
        owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }
}
