using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayOptionalSourceTests
{
    private static readonly byte[] Artwork = "1 0 0 rg 20 20 10 30 re f"u8.ToArray();
    private static byte[] Source(string key, string shape)
    {
        string resolved = shape switch { "refnull" => "null", "refempty" => "[]", "refvalid" => key == "Contents" ? "[4 0 R]" : "[0 0 100 100]", _ => "17" };
        string raw = shape switch { "null" => "null", "refnull" or "refempty" or "refvalid" or "refbad" => "8 0 R", "empty" => "[]", "valid" => key == "Contents" ? "4 0 R" : "[0 0 100 100]",
            "nested" => key == "Contents" ? "[4 0 R [4 0 R]]" : "[[0 0 100 100]]", "null-entry" => key == "Contents" ? "[4 0 R null]" : "[0 null 100 100]", _ => "17" };
        string entry = shape == "absent" ? "" : "/" + key + " " + raw;
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /CropBox [10 10 90 90] /Resources << >> /Annots [5 0 R] " + (key == "TrimBox" ? "/Contents 4 0 R " : "") + entry + " >>", null),
            (4, "<< /Length " + Artwork.Length + " >>", Artwork),
            (5, "<< /Subtype /Link /Rect [10 20 30 40] /A << /S /URI /URI (https://example.test) >> >>", null),
            (6, "null", null), (7, "null", null), (8, resolved, null)], 1);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string key in new[] { "Contents", "TrimBox" })
        foreach (string shape in new[] { "absent", "null", "refnull", "empty", "refempty", "valid", "refvalid", "bad", "refbad", "nested", "null-entry" })
        foreach (bool job in new[] { false, true })
        foreach (bool underlay in new[] { false, true }) { yield return [key, shape, job, underlay]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OptionalSourceNullsUseFallbacksWhileMalformedValuesKeepTheirSkipPolicy(string key, string shape, bool job, bool underlay)
    {
        bool invalid = shape is "bad" or "refbad" or "nested" or "null-entry" || key == "TrimBox" && shape is "empty" or "refempty";
        byte[] target = CheckTests.Assemble([(1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), target, QpdfInputOwnership.Copy); host.AddInput(QpdfPath.From("stamp"), Source(key, shape), QpdfInputOwnership.Copy);
        string mode = underlay ? "underlay" : "overlay";
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new Dictionary<string, object> { ["inputFile"] = "input", ["outputFile"] = "output", ["compressStreams"] = "n", [mode] = new { file = "stamp" } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--compress-streams=n", "--" + mode, "stamp", "--", "output"]), CancellationToken.None);
        Assert.Equal(invalid ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
        if (invalid) { Assert.Single(result.Diagnostics); } else { Assert.Empty(result.Diagnostics); }
        Assert.Equal(0L, host.OpenLeaseCount); Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        QpdfObject Get(PdfDictionary node, string name) { Assert.True(node.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return Resolve(value); }
        PdfDictionary page = document.PageReferences[0].Page;
        PdfDictionary objects = Assert.IsType<PdfDictionary>(Get(Assert.IsType<PdfDictionary>(Get(page, "Resources")), "XObject"));
        if (invalid) { Assert.False(page.TryGet(PdfName.FromAscii("Annots"), out _)); Assert.False(objects.TryGet(PdfName.FromAscii("Fx1"), out _)); return; }
        PdfArray annotations = Assert.IsType<PdfArray>(Get(page, "Annots")); Assert.Equal(1, annotations.Count);
        PdfStream form = Assert.IsType<PdfStream>(Get(objects, "Fx1"));
        Assert.Equal(key == "TrimBox" || shape is "valid" or "refvalid" ? Artwork : [], form.RawMemory.ToArray());
        PdfArray box = Assert.IsType<PdfArray>(Get(form.Dictionary, "BBox")); Assert.Equal(4, box.Count);
        long[] expected = key == "TrimBox" && shape is "valid" or "refvalid" ? [0, 0, 100, 100] : [10, 10, 90, 90];
        for (int index = 0; index < 4; index++) { Assert.Equal(expected[index], Assert.IsType<PdfInteger>(Resolve(box[index])).Value); }
    }
}
