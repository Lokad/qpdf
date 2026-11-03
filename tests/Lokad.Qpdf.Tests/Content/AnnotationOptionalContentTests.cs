using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AnnotationOptionalContentTests
{
    private static byte[] Fixture(string scope, bool on, int flags, bool mixed, bool widget, bool readiness)
    {
        string condition = scope switch
        {
            "absent" => "", "null" => "/OC null", "reference-null" => "/OC 12 0 R", "ocg" => "/OC 9 0 R",
            "ocmd" => "/OC << /Type /OCMD /OCGs [9 0 R] /P /AnyOn >>", "reference-ocmd" => "/OC 10 0 R", _ => "/OC 42",
        };
        string state = on ? "ON" : "OFF", type = widget ? "/Subtype /Widget /FT /Tx /T (field) /V (text)" : "/Subtype /Stamp";
        string form = widget ? "/AcroForm << /Fields [7 0 R 8 0 R] /NeedAppearances " + (readiness ? "true" : "false") + " >>" : "";
        string first = "<< /Type /Annot " + type + " /Rect [20 20 80 60] /F " + flags + " /AP << /N 5 0 R >> ";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /OriginalAnnots 11 0 R /OriginalAP 5 0 R /OCProperties << /OCGs [9 0 R] /D << /BaseState /" + state + " /" + state + " [9 0 R] >> >> " + form + " >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R /Annots 11 0 R >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (5, "<< /Subtype /Form /BBox [0 0 20 10] /Resources << >> /Length 23 >>", "1 0 0 rg 0 0 20 10 re f"u8.ToArray()),
            (6, "null", null), (7, first + condition + " >>", null), (8, first + (mixed ? "" : condition) + " >>", null),
            (9, "<< /Type /OCG /Name (layer) >>", null), (10, "<< /Type /OCMD /OCGs [9 0 R] /P /AnyOn >>", null),
            (11, "[7 0 R 8 0 R]", null), (12, "null", null)], 1);
    }

    private static QpdfObject Get(QpdfDocument document, PdfDictionary node, string key)
    { Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value; }
    private static PdfDictionary Dict(QpdfDocument document, PdfDictionary node, string key) => Assert.IsType<PdfDictionary>(Get(document, node, key));

    public static IEnumerable<object[]> Cases()
    {
        foreach (string scope in new[] { "absent", "null", "reference-null", "ocg", "ocmd", "reference-ocmd", "malformed" })
        foreach (string mode in new[] { "all", "print", "screen" })
        foreach (bool on in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [scope, mode, on, 4, false, false, false, job]; }
        foreach (string mode in new[] { "all", "print", "screen" })
        foreach (int flags in new[] { 0, 2, 32, 36 })
        foreach (bool job in new[] { false, true }) { yield return ["ocg", mode, false, flags, false, false, false, job]; }
        foreach (string scope in new[] { "ocg", "reference-null" })
        foreach (bool readiness in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [scope, "all", false, 4, false, true, readiness, job]; }
        foreach (bool job in new[] { false, true }) { yield return ["ocg", "all", false, 4, true, false, false, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FlatteningKeepsConditionalAnnotationsAndLeavesNullConditionsEligible(string scope, string mode, bool on, int flags, bool mixed, bool widget, bool readiness, bool job)
    {
        PdfStream escaped;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Fixture(scope, on, flags, mixed, widget, readiness), QpdfInputOwnership.Transfer);
            string json = JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", streamData = "uncompress", flattenAnnotations = mode });
            QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, json, CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--stream-data=uncompress", "--flatten-annotations=" + mode, "output"]), CancellationToken.None);
            bool conditional = scope is "ocg" or "ocmd" or "reference-ocmd" or "malformed", keep = conditional || readiness;
            Assert.Equal(keep ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
            Assert.Equal(keep ? 1 : 0, result.Diagnostics.Count);
            if (conditional && !readiness) { Assert.Contains("optional-content", result.Diagnostics[0].Message, StringComparison.Ordinal); }
            Assert.Equal(0L, host.OpenLeaseCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
            PdfDictionary catalog = Dict(document, document.Engine.Trailer, "Root"), page = document.PageReferences[0].Page;
            escaped = Assert.IsType<PdfStream>(Get(document, catalog, "OriginalAP"));
            if (!keep) { Assert.False(page.TryGet(PdfName.FromAscii("Annots"), out _)); }
            else
            {
                PdfArray original = Assert.IsType<PdfArray>(Get(document, catalog, "OriginalAnnots")), retained = Assert.IsType<PdfArray>(Get(document, page, "Annots"));
                Assert.Equal(mixed ? 1 : 2, retained.Count);
                if (!mixed) { Assert.Same(original, retained); Assert.IsType<PdfStream>(Get(document, page, "Contents")); }
                PdfReference link = Assert.IsType<PdfReference>(retained[0]); PdfDictionary annotation = Assert.IsType<PdfDictionary>(document.GetObject(link.ObjectNumber));
                Assert.Equal(flags, Assert.IsType<PdfInteger>(Get(document, annotation, "F")).Value);
                Assert.Same(escaped, Get(document, Dict(document, annotation, "AP"), "N"));
                if (mixed) { Assert.IsType<PdfArray>(Get(document, page, "Contents")); }
            }
        }
        Assert.Equal("1 0 0 rg 0 0 20 10 re f"u8.ToArray(), escaped.RawMemory.ToArray());
    }
}
