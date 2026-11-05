using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayClippingGenerationTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach ((int rotate, int unit) in new[] { (0, 1), (90, 2), (270, 1) })
        foreach (bool template in new[] { false, true })
        foreach (bool flatten in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [rotate, unit, template, flatten, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task GeneratedSourceArtworkIsClippedBeforeOptionalFlattening(int rotate, int unit, bool template, bool flatten, bool job)
    {
        RecordingMemoryBudget budget = new(16L * 1024 * 1024);
        using (IMemoryScope owner = budget.CreateScope())
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
            host.AddInput(QpdfPath.From("input"), OverlayAnnotationClippingTests.Target(), QpdfInputOwnership.Transfer);
            host.AddInput(QpdfPath.From("stamp"), Source(rotate, unit, template), QpdfInputOwnership.Transfer);
            List<string> argv = ["input", "--overlay", "stamp", "--", "--generate-appearances", "--compress-streams=n"];
            Dictionary<string, object> model = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["overlay"] = new { file = "stamp" }, ["generateAppearances"] = true, ["compressStreams"] = "n" };
            if (flatten) { argv.Add("--flatten-annotations=all"); model["flattenAnnotations"] = "all"; }
            argv.Add("output");
            QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(model), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
            {
                PdfDictionary page = document.PageReferences[0].Page;
                PdfArray? annotations = Value(document, page, "Annots") as PdfArray;
                Assert.Equal(flatten ? 0 : 1, annotations?.Count ?? 0);
                PdfStream wrapper;
                if (flatten)
                {
                    PdfDictionary resources = Assert.IsType<PdfDictionary>(Value(document, page, "Resources"));
                    PdfDictionary objects = Assert.IsType<PdfDictionary>(Value(document, resources, "XObject"));
                    PdfName name = Assert.Single(objects.Keys, key => key.DisplayText.StartsWith("Fxo", StringComparison.Ordinal));
                    wrapper = Assert.IsType<PdfStream>(Value(document, objects, name.DisplayText));
                }
                else
                {
                    PdfArray entries = Assert.IsType<PdfArray>(annotations);
                    PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(document, entries[0]));
                    PdfDictionary appearance = Assert.IsType<PdfDictionary>(Value(document, widget, "AP"));
                    wrapper = Assert.IsType<PdfStream>(Value(document, appearance, "N"));
                    PdfDictionary privateValues = Assert.IsType<PdfDictionary>(Value(document, widget, "Private"));
                    PdfStream original = Assert.IsType<PdfStream>(Value(document, privateValues, "Original"));
                    Assert.Contains("(OLD)", Encoding.ASCII.GetString(original.CopyData(owner, CancellationToken.None)), StringComparison.Ordinal);
                }
                string program = Encoding.ASCII.GetString(wrapper.CopyData(owner, CancellationToken.None));
                Assert.Contains("re W n", program, StringComparison.Ordinal); Assert.Contains("/Original Do", program, StringComparison.Ordinal);
                PdfDictionary wrapperResources = Assert.IsType<PdfDictionary>(Value(document, wrapper.Dictionary, "Resources"));
                PdfDictionary xobjects = Assert.IsType<PdfDictionary>(Value(document, wrapperResources, "XObject"));
                PdfStream generated = Assert.IsType<PdfStream>(Value(document, xobjects, "Original"));
                string generatedProgram = Encoding.ASCII.GetString(generated.CopyData(owner, CancellationToken.None));
                Assert.Contains("(NEW)", generatedProgram, StringComparison.Ordinal); Assert.DoesNotContain("(OLD)", generatedProgram, StringComparison.Ordinal);
                PdfArray box = Assert.IsType<PdfArray>(Value(document, generated.Dictionary, "BBox"));
                Assert.Equal(4, box.Count);
                Assert.Equal(new double[] { 0, 0, 60, 20 }, Enumerable.Range(0, box.Count).Select(index => Resolve(document, box[index]) switch
                { PdfInteger integer => (double)integer.Value, PdfReal real => real.Value, _ => throw new InvalidOperationException() }).ToArray());
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(Value(document, document.Engine.Trailer, "Root"));
                if (Value(document, catalog, "AcroForm") is PdfDictionary form)
                { Assert.False(Value(document, form, "NeedAppearances") is PdfBoolean { Value: true }); }
            }
            Assert.Equal(0, host.OpenLeaseCount);
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    private static byte[] Source(int rotate, int unit, bool template)
    {
        byte[] body = "/Tx BMC BT /F1 10 Tf 2 5 Td (OLD) Tj ET EMC"u8.ToArray();
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Rotate " + rotate + " /UserUnit " + unit + " /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (field) /V (NEW) /F 4 /Rect [80 40 140 60] /Private << /Original 5 0 R >>" + (template ? " /AP << /N 5 0 R >>" : "") + " >>", null),
            (5, "<< /Type /XObject /Subtype /Form /BBox [0 0 60 20] /Resources << /Font << /F1 7 0 R >> >> /Length " + body.Length + " >>", body),
            (6, "<< /Fields [4 0 R] /NeedAppearances true /DA (/F1 10 Tf 0 g) /DR << /Font << /F1 7 0 R >> >> >>", null),
            (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", null)], 1);
    }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static QpdfObject Value(QpdfDocument document, PdfDictionary dictionary, string key)
        => dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value) ? Resolve(document, value) : PdfNull.Instance;
}
