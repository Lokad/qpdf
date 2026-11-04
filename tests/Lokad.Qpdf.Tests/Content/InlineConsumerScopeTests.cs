using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class InlineConsumerScopeTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string route in new[] { "xobject", "softmask", "softmask-alias", "pattern", "glyph", "appearance" })
        foreach (bool job in new[] { false, true })
        { yield return [route, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OnlyXObjectResourceBindingsEnterFormExternalization(string route, bool job)
    {
        byte[] inline = [.. "BI /W 1 /H 1 /BPC 8 /CS /G ID "u8, 255, .. " EI"u8];
        byte[] program = route == "glyph" ? [.. "100 0 d0 "u8, .. inline] : inline;
        string descriptor = route == "pattern"
            ? "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 100 200] /XStep 100 /YStep 200 /Resources <<>>"
            : route == "glyph" ? "" : "/Type /XObject /Subtype /Form /BBox [0 0 100 200] /Resources <<>> /Group << /S /Transparency /CS /DeviceGray >>";
        string auxiliary = route == "glyph"
            ? "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 100 200] /FontMatrix [0.01 0 0 0.01 0 0] /CharProcs << /A 5 0 R >> /Encoding << /Type /Encoding /Differences [65 /A] >> /FirstChar 65 /LastChar 65 /Widths [100] /Resources <<>> >>"
            : "<< /S /Luminosity /G 5 0 R >>";
        string resources = route switch
        {
            "xobject" => "<< /XObject << /F 5 0 R >> >>",
            "softmask" or "softmask-alias" => "<< /ExtGState << /G << /SMask " + (job ? "6 0 R" : "<< /S /Luminosity /G 5 0 R >>") + " >> >> " + (route == "softmask-alias" ? "/XObject << /F 5 0 R >>" : "") + " >>",
            "pattern" => "<< /Pattern << /P 5 0 R >> >>",
            "glyph" => "<< /Font << /F 6 0 R >> >>",
            _ => "<<>>",
        };
        byte[] contents = route switch
        {
            "xobject" => "/F Do"u8.ToArray(),
            "softmask" or "softmask-alias" => "/G gs 0 0 100 200 re f"u8.ToArray(),
            "pattern" => "/Pattern cs /P scn 0 0 100 200 re f"u8.ToArray(),
            "glyph" => "BT /F 100 Tf 0 0 Td (A) Tj ET"u8.ToArray(),
            _ => "q Q"u8.ToArray(),
        };
        byte[] fixture = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AuditOriginal 5 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources " + resources + " /Contents 4 0 R " + (route == "appearance" ? "/Annots [7 0 R]" : "") + " >>", null),
            (4, "<< /Length " + contents.Length + " >>", contents),
            (5, "<< " + descriptor + " /Length " + program.Length + " >>", program),
            (6, auxiliary, null),
            (7, "<< /Type /Annot /Subtype /Stamp /F 4 /Rect [0 0 100 200] /AP << /N 5 0 R >> >>", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), fixture, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", externalizeInlineImages = true, iiMinBytes = "0", streamData = "uncompress" }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--externalize-inline-images", "--ii-min-bytes=0", "--stream-data=uncompress", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        int images = document.Engine.XRefEntries.Keys.Where(number => number > 0).Select(document.GetObject).OfType<PdfStream>()
            .Count(stream => stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject type) && type.Equals(PdfName.FromAscii("Image")));
        Assert.Equal(route is "xobject" or "softmask-alias" ? 1 : 0, images);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.True(catalog.TryGet(PdfName.FromAscii("AuditOriginal"), out QpdfObject original));
        PdfReference link = Assert.IsType<PdfReference>(original);
        Assert.Equal(program, Assert.IsType<PdfStream>(document.GetObject(link.ObjectNumber)).RawMemory.ToArray());
    }
}
