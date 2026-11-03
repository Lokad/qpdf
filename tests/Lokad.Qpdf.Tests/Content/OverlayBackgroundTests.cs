using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayBackgroundTests
{
    private static readonly byte[] Artwork = "1 0 0 rg 25 35 10 20 re f 0 0 1 rg 65 155 10 25 re f 0 1 0 rg 45 90 10 10 re f"u8.ToArray();
    private static byte[] Target(int rotation, int unit, bool crop, bool trim)
        => CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Original 4 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox 6 0 R " + (crop ? "/CropBox 8 0 R " : "") + (trim ? "/TrimBox 7 0 R " : "") + "/Rotate 9 0 R /UserUnit 10 0 R /Resources << >> /Contents 4 0 R >>", null),
            (4, "<< /Length " + Artwork.Length + " >>", Artwork), (5, "null", null),
            (6, "[10 20 110 220]", null), (7, "[30 60 70 140]", null), (8, "[20 30 80 190]", null),
            (9, rotation.ToString(CultureInfo.InvariantCulture), null), (10, unit.ToString(CultureInfo.InvariantCulture), null)], 1);

    public static IEnumerable<object[]> Cases()
    {
        foreach (int rotation in new[] { 0, 90, 180, 270 })
        foreach (int unit in new[] { 1, 2 })
        foreach (bool crop in new[] { false, true })
        foreach (bool trim in new[] { false, true })
        foreach (bool job in new[] { false, true })
        foreach (bool underlay in new[] { false, true }) { yield return [rotation, unit, crop, trim, job, underlay]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EmptyStampKeepsOriginalContentCoordinatesAndVisibleBounds(int rotation, int unit, bool crop, bool trim, bool job, bool underlay)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Target(rotation, unit, crop, trim), QpdfInputOwnership.Copy);
        byte[] empty = CheckTests.Assemble([(1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)], 1);
        host.AddInput(QpdfPath.From("stamp"), empty, QpdfInputOwnership.Copy); string mode = underlay ? "underlay" : "overlay";
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new Dictionary<string, object> { ["inputFile"] = "input", ["outputFile"] = "output", ["compressStreams"] = "n", ["normalizeContent"] = "n", [mode] = new { file = "stamp" } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--compress-streams=n", "--normalize-content=n", "--" + mode, "stamp", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        QpdfObject Get(PdfDictionary dictionary, string name) { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return Resolve(value); }
        double[] Numbers(PdfArray array)
        {
            double[] result = new double[array.Count];
            for (int i = 0; i < array.Count; i++) { result[i] = Resolve(array[i]) switch { PdfInteger integer => integer.Value, PdfReal real => real.Value, _ => throw new InvalidOperationException() }; }
            return result;
        }
        PdfDictionary page = document.PageReferences[0].Page;
        Assert.Equal(rotation, Assert.IsType<PdfInteger>(Get(page, "Rotate")).Value); Assert.Equal(unit, Assert.IsType<PdfInteger>(Get(page, "UserUnit")).Value);
        PdfDictionary objects = Assert.IsType<PdfDictionary>(Get(Assert.IsType<PdfDictionary>(Get(page, "Resources")), "XObject"));
        PdfStream original = Assert.IsType<PdfStream>(Get(objects, "Fx0")); Assert.Equal(Artwork, original.RawMemory.ToArray());
        double[] bounds = Numbers(Assert.IsType<PdfArray>(Get(original.Dictionary, "BBox"))), visible = crop ? [20, 30, 80, 190] : [10, 20, 110, 220];
        Assert.True(Math.Min(bounds[0], bounds[2]) <= visible[0] && Math.Min(bounds[1], bounds[3]) <= visible[1]
            && Math.Max(bounds[0], bounds[2]) >= visible[2] && Math.Max(bounds[1], bounds[3]) >= visible[3], "The background Form clips existing visible content.");
        double[] form = original.Dictionary.TryGet(PdfName.FromAscii("Matrix"), out QpdfObject raw) ? Numbers(Assert.IsType<PdfArray>(Resolve(raw))) : [1, 0, 0, 1, 0, 0];
        string program = Encoding.ASCII.GetString(Assert.IsType<PdfStream>(Get(page, "Contents")).RawMemory.Span);
        Match match = Regex.Match(program, @"([-+0-9.eE]+) ([-+0-9.eE]+) ([-+0-9.eE]+) ([-+0-9.eE]+) ([-+0-9.eE]+) ([-+0-9.eE]+) cm\s+/Fx0 Do"); Assert.True(match.Success, program);
        double[] cm = Enumerable.Range(1, 6).Select(i => double.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
        double[] effective = [cm[0] * form[0] + cm[2] * form[1], cm[1] * form[0] + cm[3] * form[1],
            cm[0] * form[2] + cm[2] * form[3], cm[1] * form[2] + cm[3] * form[3],
            cm[0] * form[4] + cm[2] * form[5] + cm[4], cm[1] * form[4] + cm[3] * form[5] + cm[5]];
        Assert.Equal(new double[] { 1, 0, 0, 1, 0, 0 }, effective);
    }
}
