using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class RotationDestinationTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string mode in new[] { "XYZ", "FitR", "Fit", "FitB", "FitH", "FitV", "FitBH", "FitBV" })
        foreach (int angle in new[] { 0, 90, 180, 270 })
        foreach (bool indirect in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [mode, angle, indirect, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RetainedDestinationRolesFollowTheActualBakedFrame(string mode, int angle, bool indirect, bool job)
    {
        RecordingMemoryBudget budget = new(16L << 20);
        using (IMemoryScope owner = budget.CreateScope())
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
            host.AddInput(QpdfPath.From("input"), Source(mode, angle, indirect), QpdfInputOwnership.Transfer);
            await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
            { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", flattenRotation = true }), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--flatten-rotation", "output"]), CancellationToken.None);
            bool unsupported = angle is 90 or 270 && mode is "FitH" or "FitV" or "FitBH" or "FitBV";
            Assert.Equal(unsupported ? QpdfExitCode.Error : QpdfExitCode.Success, result.ExitCode);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            if (unsupported)
            {
                Assert.Equal("prior"u8.ToArray(), output);
                Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.UnsupportedFeature);
            }
            else
            {
                Assert.Empty(result.Diagnostics);
                host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
                PdfDictionary catalog = Dict(document, document.Engine.Trailer, "Root");
                PdfDictionary page = document.PageReferences[0].Page;
                if (angle != 0) { Assert.False(page.TryGet(PdfName.FromAscii("Rotate"), out _)); }
                PdfArray annotations = Assert.IsType<PdfArray>(Value(document, page, "Annots"));
                PdfDictionary annotation = Assert.IsType<PdfDictionary>(Resolve(document, annotations[0]));
                PdfDictionary action = Dict(document, annotation, "A");
                PdfDictionary outlines = Dict(document, catalog, "Outlines");
                PdfDictionary outline = Dict(document, outlines, "First");
                PdfDictionary legacy = Dict(document, catalog, "Dests");
                PdfDictionary tree = Dict(document, Dict(document, catalog, "Names"), "Dests");
                PdfArray names = Assert.IsType<PdfArray>(Value(document, tree, "Names"));
                PdfDictionary wrapper = Assert.IsType<PdfDictionary>(Resolve(document, names[1]));
                QpdfObject[] retained = [Value(document, catalog, "OpenAction"), Value(document, annotation, "Dest"), Value(document, action, "D"),
                    Value(document, outline, "Dest"), Value(document, Dict(document, outline, "A"), "D"), Value(document, legacy, "mark"), Value(document, wrapper, "D")];
                foreach (QpdfObject destination in retained) { AssertDestination(document, destination, mode, Expected(mode, angle)); }
                AssertDestination(document, Value(document, catalog, "Private"), mode, Expected(mode, 0));
                AssertDestination(document, Value(document, annotation, "Private"), mode, Expected(mode, 0));
                AssertDestination(document, Value(document, wrapper, "Private"), mode, Expected(mode, 0));
                Assert.Same(action, Value(document, action, "Next"));
                if (indirect)
                {
                    Assert.Equal(Raw(catalog, "OpenAction"), Raw(annotation, "Dest"));
                    Assert.Equal(Raw(catalog, "OpenAction"), Raw(action, "D"));
                    if (angle != 0 && mode is not ("Fit" or "FitB")) { Assert.NotEqual(Raw(catalog, "OpenAction"), Raw(catalog, "Private")); }
                    else { Assert.Equal(Raw(catalog, "OpenAction"), Raw(catalog, "Private")); }
                }
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    internal static byte[] Source(string mode, int angle, bool indirect)
    {
        string[] values = mode switch { "XYZ" => ["25", "60", "0"], "FitR" => ["25", "60", "75", "120"],
            "FitH" or "FitBH" => ["60"], "FitV" or "FitBV" => ["25"], _ => [] };
        string array = "[3 0 R " + (indirect ? "17 0 R" : "/" + mode) + " " + string.Join(" ", values.Select((v, i) => indirect ? (10 + i) + " 0 R" : v)) + "]";
        string destination = indirect ? "9 0 R" : array;
        List<(int, string, byte[]?)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /OpenAction " + destination + " /Dests << /mark " + destination + " >> /Names << /Dests << /Names [(mark) 8 0 R] >> >> /Outlines 5 0 R /Private " + destination + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [10 20 110 220] /CropBox [20 30 100 210] /Resources << >> /Rotate " + angle + " /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Link /Border [0 0 0] /Rect [25 60 75 120] /Dest " + destination + " /A 7 0 R /Private " + destination + " >>", null),
            (5, "<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 1 >>", null),
            (6, "<< /Title (target) /Parent 5 0 R /Dest " + destination + " /A 7 0 R >>", null),
            (7, "<< /S /GoTo /D " + destination + " /Next 7 0 R >>", null),
            (8, "<< /D " + destination + " /Private " + destination + " >>", null),
            (9, array, null)];
        for (int i = 0; i < 4; i++) { objects.Add((10 + i, i < values.Length ? values[i] : "0", null)); }
        for (int i = 14; i < 17; i++) { objects.Add((i, "null", null)); }
        objects.Add((17, "/" + mode, null));
        return CheckTests.Assemble(objects, 1);
    }

    internal static double[] Expected(string mode, int angle)
    {
        if (mode is "Fit" or "FitB") { return []; }
        if (mode is "FitH" or "FitBH") { return [angle == 180 ? 180 : 60]; }
        if (mode is "FitV" or "FitBV") { return [angle == 180 ? 95 : 25]; }
        (double X, double Y) Point(double x, double y) => angle switch { 90 => (y, 120 - x), 180 => (120 - x, 240 - y), 270 => (240 - y, x), _ => (x, y) };
        if (mode == "XYZ") { var p = Point(25, 60); return [p.X, p.Y, 0]; }
        var a = Point(25, 60); var b = Point(75, 120);
        return [Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)];
    }

    internal static void AssertDestination(QpdfDocument document, QpdfObject value, string mode, double[] expected)
    {
        PdfArray array = Assert.IsType<PdfArray>(Resolve(document, value));
        Assert.Equal(expected.Length + 2, array.Count); Assert.Equal(PdfName.FromAscii(mode), Assert.IsType<PdfName>(Resolve(document, array[1])));
        for (int i = 0; i < expected.Length; i++)
        {
            QpdfObject scalar = Resolve(document, array[i + 2]);
            double actual = scalar is PdfInteger integer ? integer.Value : Assert.IsType<PdfReal>(scalar).Value;
            Assert.Equal(expected[i], actual, 6);
        }
    }
    internal static QpdfObject Resolve(QpdfDocument document, QpdfObject value) => value is PdfReference r ? document.GetObject(r.ObjectNumber) : value;
    internal static QpdfObject Raw(PdfDictionary dictionary, string key) { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
    internal static QpdfObject Value(QpdfDocument document, PdfDictionary dictionary, string key) => Resolve(document, Raw(dictionary, key));
    internal static PdfDictionary Dict(QpdfDocument document, PdfDictionary dictionary, string key) => Assert.IsType<PdfDictionary>(Value(document, dictionary, key));
}
