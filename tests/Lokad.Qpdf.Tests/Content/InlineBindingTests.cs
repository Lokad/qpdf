using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class InlineBindingTests
{
    private static byte[] Program(bool named) => [.. Encoding.ASCII.GetBytes("q 30 0 0 30 10 10 cm BI /W 1 /H 1 /BPC 8 /CS " + (named ? "/CS1" : "/RGB") + " ID "), 255, 0, 0, .. " EI Q\nBT /F1 14 Tf 10 70 Td (VISIBLE) Tj ET\n"u8];

    private static byte[] Fixture(string scope, bool named)
    {
        byte[] raw = Program(named);
        string resources = scope switch
        {
            "absent" => "", "null" => "/Resources null", "indirect-local" => "/Resources 8 0 R",
            "font-only" => "/Resources << /Font << /F1 9 0 R >> >>",
            "bad-xobjects" or "null-xobjects" => "/Resources << /Font << /F1 9 0 R >> /ColorSpace << /CS1 /DeviceRGB >> /XObject 7 0 R >>",
            "indirect-color" => "/Resources << /Font << /F1 9 0 R >> /ColorSpace 12 0 R >>",
            _ => "/Resources << /Font << /F1 9 0 R >> /ColorSpace << /CS1 /DeviceRGB >> >>"
        };
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 6 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << /Font << /F1 9 0 R >> /ColorSpace << /CS1 /DeviceRGB >> /XObject << /Fm 5 0 R >> >> /Contents 4 0 R >>", null),
            (4, "<< /Length 7 >>", "/Fm Do\n"u8.ToArray()),
            (5, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] " + resources + " /Length " + raw.Length + " >>", raw),
            (6, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << /Font << /F1 10 0 R >> /ColorSpace << /CS1 /DeviceRGB >> /XObject << /Fm 5 0 R >> >> /Contents 4 0 R >>", null),
            (7, scope == "bad-xobjects" ? "17" : "null", null),
            (8, "<< /Font << /F1 9 0 R >> /ColorSpace << /CS1 /DeviceRGB >> >>", null),
            (9, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (10, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", null),
            (11, "/DeviceRGB", null), (12, "<< /CS1 11 0 R >>", null)], 1);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string scope in new[] { "absent", "null", "font-only", "local", "indirect-local", "indirect-color", "bad-xobjects", "null-xobjects" })
        foreach (bool named in new[] { false, true })
        foreach (string mode in new[] { "whole", "select", "preserve" })
        foreach (bool job in new[] { false, true }) { yield return [scope, named, mode, job]; }
        foreach (string scope in new[] { "absent", "local", "indirect-local" })
        foreach (string mode in new[] { "qdf", "split", "duplicate" })
        foreach (bool job in new[] { false, true }) { yield return [scope, false, mode, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ConversionRequiresBindableResourcesAndPreservesSharedContext(string scope, bool named, string mode, bool job)
    {
        bool convert = scope is "local" or "indirect-local" or "indirect-color" or "null-xobjects" || scope == "font-only" && !named;
        List<PdfStream> escaped = [];
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Fixture(scope, named), QpdfInputOwnership.Copy);
            List<string> argv = ["input", "--externalize-inline-images", "--ii-min-bytes=0", "--stream-data=uncompress", "--remove-unreferenced-resources=no"];
            Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["externalizeInlineImages"] = true, ["iiMinBytes"] = "0", ["streamData"] = "uncompress", ["removeUnreferencedResources"] = "no" };
            if (mode is "select" or "duplicate")
            { string range = mode == "duplicate" ? "1,1,2" : "1-2"; argv.AddRange(["--pages", ".", range, "--"]); settings["pages"] = new[] { new { file = ".", range } }; }
            if (mode == "preserve") { argv.Add("--preserve-unreferenced"); settings["preserveUnreferenced"] = true; }
            if (mode == "qdf") { argv.Add("--qdf"); settings["qdf"] = true; }
            if (mode == "split") { argv.Add("--split-pages=1"); settings["splitPages"] = "1"; }
            argv.Add("output");
            QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
            Assert.Equal(convert ? QpdfExitCode.Success : QpdfExitCode.Warning, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
            if (convert) { Assert.Empty(result.Diagnostics); }
            else { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.UnsupportedFeature); }
            for (int group = 1; group <= (mode == "split" ? 2 : 1); group++)
            {
                Assert.True(host.TryGetOutput(QpdfPath.From(mode == "split" ? "output-" + group : "output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
                QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
                QpdfObject Get(PdfDictionary node, string key) { Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
                foreach (var page in document.PageReferences)
                {
                    PdfDictionary resources = Assert.IsType<PdfDictionary>(Resolve(Get(page.Page, "Resources")));
                    PdfDictionary xobjects = Assert.IsType<PdfDictionary>(Resolve(Get(resources, "XObject")));
                    PdfStream form = Assert.IsType<PdfStream>(Resolve(Get(xobjects, "Fm"))); escaped.Add(form);
                    if (!convert) { Assert.Equal(Program(named), form.RawMemory.ToArray()); continue; }
                    Assert.DoesNotContain(" BI ", Encoding.Latin1.GetString(form.RawMemory.Span));
                    PdfDictionary local = Assert.IsType<PdfDictionary>(Resolve(Get(form.Dictionary, "Resources")));
                    PdfDictionary images = Assert.IsType<PdfDictionary>(Resolve(Get(local, "XObject")));
                    PdfStream image = Assert.IsType<PdfStream>(Resolve(Get(images, "IIm1")));
                    Assert.Equal(PdfName.FromAscii("Image"), Get(image.Dictionary, "Subtype"));
                    Assert.Equal(PdfName.FromAscii("DeviceRGB"), Resolve(Get(image.Dictionary, "ColorSpace")));
                }
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        foreach (PdfStream form in escaped) { Assert.True(form.RawMemory.Length > 0); }
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("null")]
    [InlineData("font-only")]
    [InlineData("bad-xobjects")]
    public async Task IneligibleImagesDoNotRequireResourceBindingOrWarnings(string scope)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(scope, true), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--externalize-inline-images", "--ii-min-bytes=1024", "--remove-unreferenced-resources=no", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
    }
}
