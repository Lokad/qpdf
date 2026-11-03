using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceStateGenerationTests
{
    private const string Artwork = "q 1 0 0 rg 0 0 100 20 re f Q\n";

    private static byte[] Fixture(string state, bool states)
    {
        byte[] program = Encoding.ASCII.GetBytes(Artwork + "/Tx BMC\nEMC\n");
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /NeedAppearances true /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 6 0 R >> >> >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Text) /V (Hello) /Rect [10 20 110 40] /AS " + state
                + " /AP << /N " + (states ? "<< /On 5 0 R >>" : "5 0 R") + " >> >>", null),
            (5, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] /Resources << /Font << /Helv 6 0 R >> >> /Length " + program.Length + " >>", program),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (7, "/On", null), (8, "null", null), (9, "42", null)], 1);
    }

    public static IEnumerable<object[]> States()
    {
        foreach (bool job in new[] { false, true })
        foreach (bool flatten in new[] { false, true })
        foreach ((string state, bool states, bool artwork) in new[]
        {
            ("/On", true, true), ("7 0 R", true, true), ("7 0 R", false, true),
            ("null", true, false), ("8 0 R", true, false), ("42", true, false), ("9 0 R", true, false),
        }) { yield return [job, flatten, state, states, artwork]; }
    }

    [Theory]
    [MemberData(nameof(States))]
    public async Task GenerationResolvesTheSelectedStateBeforeReplacingItsProgram(bool job, bool flatten, string state, bool states, bool artwork)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(state, states), QpdfInputOwnership.Transfer);
        List<string> argv = ["input", "--generate-appearances", "--compress-streams=n"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["generateAppearances"] = true, ["compressStreams"] = "n" };
        if (flatten) { argv.Add("--flatten-annotations=all"); settings.Add("flattenAnnotations", "all"); }
        argv.Add("output");
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("readback"), bytes, QpdfInputOwnership.Transfer);
        PdfStream surviving;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
        {
            QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
            QpdfObject Get(PdfDictionary node, PdfName key) { Assert.True(node.TryGet(key, out QpdfObject value)); return Resolve(value); }
            PdfDictionary page = document.PageReferences[0].Page;
            if (flatten)
            {
                Assert.False(page.TryGet(PdfName.FromAscii("Annots"), out _));
                PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(page, PdfName.FromAscii("Resources")));
                PdfDictionary xobjects = Assert.IsType<PdfDictionary>(Get(resources, PdfName.FromAscii("XObject")));
                surviving = Assert.IsType<PdfStream>(Get(xobjects, Assert.Single(xobjects.Keys)));
            }
            else
            {
                PdfArray annotations = Assert.IsType<PdfArray>(Get(page, PdfName.FromAscii("Annots")));
                Assert.Equal(1, annotations.Count);
                PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(annotations[0]));
                PdfDictionary appearances = Assert.IsType<PdfDictionary>(Get(widget, PdfName.FromAscii("AP")));
                surviving = Assert.IsType<PdfStream>(Get(appearances, PdfName.FromAscii("N")));
            }
            Assert.IsType<PdfDictionary>(Get(surviving.Dictionary, PdfName.FromAscii("Resources")));
        }
        host.Dispose();
        string program = Encoding.ASCII.GetString(surviving.RawMemory.Span);
        Assert.Contains("(Hello) Tj", program);
        Assert.Equal(artwork, program.StartsWith(Artwork, StringComparison.Ordinal));
    }
}
