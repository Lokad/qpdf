using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ResourceConsumerPruningTests
{
    private static byte[] Fixture(string kind, bool local, bool indirect)
    {
        bool graphicsFont = kind.StartsWith("gs-", StringComparison.Ordinal);
        string resources = " /Resources 4 0 R";
        string page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100]" + (indirect ? "" : resources) + " /Contents 9 0 R >>";
        string content = graphicsFont ? "BT /Gs gs 20 20 Td (A) Tj ET\n"
            : kind is "type3" or "type3-empty" ? "BT /F3 60 Tf 20 20 Td (A) Tj ET\n"
            : kind is "mask" or "mask-empty" ? "q /Gs gs 0 0 200 100 re f Q\n"
            : kind == "nested-mask" ? "/Outer Do\n" : "BT /F1 20 Tf 20 20 Td (A) Tj ET\n";
        string outer = "q /Gs gs 0 0 200 100 re f Q\n";
        string glyph = "1000 0 d0 q 1000 0 0 1000 0 0 cm /Im Do Q\n";
        string group = "1 g BT /F1 70 Tf 20 20 Td (A) Tj ET\n";
        string mask = "<< /S /Luminosity /G 11 0 R >>";
        string fontArray = "[" + (kind == "gs-ordinary" ? "12" : "5") + " 0 R 60]";
        string state = graphicsFont ? "<< /Type /ExtGState /Font " + (indirect ? "18 0 R" : fontArray) + " >>"
            : "<< /Type /ExtGState /SMask " + (indirect ? "16 0 R" : mask) + " >>";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 13 0 R] /Count 2" + (indirect ? resources : "") + " >>", null),
            (3, page, null),
            (4, "<< /Font << /F3 5 0 R /F1 12 0 R /Unused 12 0 R >> /XObject << /Im 7 0 R /Spare 8 0 R"
                + (kind == "nested-mask" ? " /Outer 17 0 R" : "") + " >> /ExtGState << /Gs "
                + (kind == "gs-type3-direct" ? state : "10 0 R") + " >> >>", null),
            (5, "<< /Type /Font /Subtype " + (indirect ? "14 0 R" : "/Type3")
                + " /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs " + (indirect ? "15 0 R" : "<< /A 6 0 R >>")
                + " /Encoding << /Type /Encoding /Differences [65 /A] >> /FirstChar 65 /LastChar 65 /Widths [1000]"
                + (local ? kind is "type3-empty" or "gs-type3-empty" ? " /Resources << >>" : " /Resources << /XObject << /Im 7 0 R >> >>" : "") + " >>", null),
            (6, "<< /Length " + glyph.Length + " >>", Encoding.ASCII.GetBytes(glyph)),
            (7, "<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 64 >>", new byte[64]),
            (8, "<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 64 >>", Enumerable.Repeat((byte)255, 64).ToArray()),
            (9, "<< /Length " + content.Length + " >>", Encoding.ASCII.GetBytes(content)),
            (10, state, null),
            (11, "<< /Type /XObject /Subtype /Form /BBox [0 0 200 100] /Group << /S /Transparency /CS /DeviceGray >>"
                + (local ? kind == "mask-empty" ? " /Resources << >>" : " /Resources << /Font << /F1 12 0 R >> >>" : "") + " /Length " + group.Length + " >>", Encoding.ASCII.GetBytes(group)),
            (12, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (13, page, null), (14, "/Type3", null), (15, "<< /A 6 0 R >>", null), (16, mask, null),
            (17, "<< /Type /XObject /Subtype /Form /BBox [0 0 200 100] /Length " + outer.Length + " >>", Encoding.ASCII.GetBytes(outer)),
            (18, fontArray, null)], 1);
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    private static PdfDictionary Get(QpdfDocument document, PdfDictionary parent, string key)
    { Assert.True(parent.TryGet(PdfName.FromAscii(key), out QpdfObject raw)); return Assert.IsType<PdfDictionary>(Resolve(document, raw)); }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string route in new[] { "argv", "job" })
        {
            foreach (string kind in new[] { "type3", "mask", "gs-type3" })
            {
                foreach (bool local in new[] { false, true })
                foreach (bool indirect in new[] { false, true }) { yield return [route, kind, local, indirect, "yes"]; }
                foreach (string policy in new[] { "auto", "no" }) { yield return [route, kind, false, false, policy]; }
            }
            foreach (bool indirect in new[] { false, true }) { yield return [route, "ordinary", false, indirect, "yes"]; }
            foreach (bool local in new[] { false, true }) { yield return [route, "nested-mask", local, true, "yes"]; }
            foreach (string kind in new[] { "type3-empty", "mask-empty", "gs-type3-empty" }) { yield return [route, kind, true, false, "yes"]; }
            yield return [route, "gs-type3-direct", false, false, "yes"];
            yield return [route, "gs-ordinary", false, true, "yes"];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ResourceConsumersKeepTheirEffectiveDependencies(string route, string kind, bool local, bool indirect, string policy)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(kind, local, indirect), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result;
        if (route == "job")
        {
            Dictionary<string, object> job = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["pages"] = new[] { new { file = ".", range = "1" } } };
            if (policy != "auto") { job["removeUnreferencedResources"] = policy; }
            result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
        }
        else
        {
            List<string> argv = ["input"];
            if (policy != "auto") { argv.Add("--remove-unreferenced-resources=" + policy); }
            argv.AddRange(["--pages", ".", "1", "--", "output"]);
            result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        }
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("again"), bytes, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None))
        {
            PdfDictionary resources = Get(document, Assert.IsType<PdfDictionary>(document.GetObject(3)), "Resources");
            PdfDictionary fonts = Get(document, resources, "Font"), images = Get(document, resources, "XObject");
            bool preserve = policy == "no" || kind is not ("ordinary" or "gs-ordinary");
            Assert.Equal(preserve, images.TryGet(PdfName.FromAscii("Spare"), out _));
            Assert.Equal(preserve, fonts.TryGet(PdfName.FromAscii("Unused"), out _));
            if (kind is "type3" or "type3-empty" or "gs-type3" or "gs-type3-empty" or "gs-type3-direct")
            {
                PdfDictionary font = Get(document, fonts, "F3");
                if (kind is "type3-empty" or "gs-type3-empty") { Assert.Equal(0, Get(document, font, "Resources").Count); Assert.True(images.TryGet(PdfName.FromAscii("Im"), out _)); }
                else if (local) { Assert.True(Get(document, Get(document, font, "Resources"), "XObject").TryGet(PdfName.FromAscii("Im"), out _)); }
                else { Assert.True(images.TryGet(PdfName.FromAscii("Im"), out _)); }
            }
            else if (kind is "mask" or "mask-empty" or "nested-mask")
            {
                if (local)
                {
                    PdfDictionary mask = Get(document, Get(document, Get(document, resources, "ExtGState"), "Gs"), "SMask");
                    Assert.True(mask.TryGet(PdfName.FromAscii("G"), out QpdfObject raw));
                    PdfStream group = Assert.IsType<PdfStream>(Resolve(document, raw));
                    PdfDictionary own = Get(document, group.Dictionary, "Resources");
                    if (kind == "mask-empty") { Assert.Equal(0, own.Count); Assert.True(fonts.TryGet(PdfName.FromAscii("F1"), out _)); }
                    else { Assert.True(Get(document, own, "Font").TryGet(PdfName.FromAscii("F1"), out _)); }
                }
                else { Assert.True(fonts.TryGet(PdfName.FromAscii("F1"), out _)); }
            }
            else if (kind == "gs-ordinary")
            {
                PdfDictionary state = Get(document, Get(document, resources, "ExtGState"), "Gs");
                Assert.True(state.TryGet(PdfName.FromAscii("Font"), out QpdfObject raw));
                PdfArray selection = Assert.IsType<PdfArray>(Resolve(document, raw));
                PdfDictionary font = Assert.IsType<PdfDictionary>(Resolve(document, selection[0]));
                Assert.True(font.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject subtype));
                Assert.True(Assert.IsType<PdfName>(Resolve(document, subtype)).RawSpan.SequenceEqual("Type1"u8));
            }
            else { Assert.True(fonts.TryGet(PdfName.FromAscii("F1"), out _)); }
        }
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
    }
}
