using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceGenerationRegressionTests
{
    [Theory]
    [InlineData(32768, "/Yes")]
    [InlineData(65536, "/Yes")]
    [InlineData(0, "null")]
    public async Task SkippedButtonsSurviveBesideUpdatedWidgets(int flags, string value)
    {
        foreach (bool indirect in new[] { false, true })
        foreach (bool job in new[] { false, true })
        {
            byte[] input = CheckTests.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R] /NeedAppearances true >> >>", null),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots " + (indirect ? "9 0 R" : "[5 0 R 6 0 R]") + " >>", null),
                (4, "null", null),
                (5, "<< /Subtype /Widget /FT /Btn /Ff " + flags + " /T (Skipped) /V " + value + " /AS /Off /Rect [0 0 100 20] /AP << /N << /Off 7 0 R /Yes 8 0 R >> >> >>", null),
                (6, "<< /Subtype /Widget /FT /Btn /T (Updated) /V /Yes /AS /Off /Rect [0 30 100 50] /AP << /N << /Off 7 0 R /Yes 8 0 R >> >> >>", null),
                (7, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] /Length 1 >>", "q"u8.ToArray()),
                (8, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] /Length 1 >>", "Q"u8.ToArray()),
                (9, "[5 0 R 6 0 R]", null)], 1);
            using InMemoryQpdfHost host = new();
            byte[] output = await Generate(host, input, job, QpdfExitCode.Warning);
            host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
            PdfDictionary page = document.PageReferences[0].Page;
            Assert.True(page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject raw));
            PdfArray annotations = Assert.IsType<PdfArray>(Resolve(document, raw)); Assert.Equal(2, annotations.Count);
            PdfDictionary skipped = Assert.IsType<PdfDictionary>(Resolve(document, annotations[0]));
            PdfDictionary updated = Assert.IsType<PdfDictionary>(Resolve(document, annotations[1]));
            Assert.True(skipped.TryGet(PdfName.FromAscii("AS"), out QpdfObject oldState)); Assert.Equal(PdfName.FromAscii("Off"), oldState);
            Assert.True(updated.TryGet(PdfName.FromAscii("AS"), out QpdfObject newState)); Assert.Equal(PdfName.FromAscii("Yes"), newState);
        }
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    private static async Task<byte[]> Generate(InMemoryQpdfHost host, byte[] input, bool job, QpdfExitCode expected)
    {
        host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1"}],"generateAppearances":true}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--generate-appearances", "--pages", ".", "1", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(expected, result.ExitCode);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); return output;
    }
}
