using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AnnotationPlacementTests
{
    public static IEnumerable<object[]> CoefficientCases()
    {
        for (int coefficient = 0; coefficient < 6; coefficient++)
        {
            foreach (bool job in new[] { false, true }) { yield return [coefficient, job]; }
        }
    }

    [Theory]
    [MemberData(nameof(CoefficientCases))]
    public async Task IndirectCoefficientsPreserveNonIdentityPlacement(int coefficient, bool job)
    {
        string[] values = ["0", "1", "-1", "0", "100", "0"];
        string extra = values[coefficient];
        values[coefficient] = "7 0 R";
        string content = await FlattenedContent(Fixture(string.Join(' ', values), "20 30 180 110", extra, 0), job);
        Assert.Contains("4 0 0 1 -220 30 cm", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("null", true)]
    [InlineData("/Bad", false)]
    [InlineData("/Bad", true)]
    [InlineData("[]", false)]
    [InlineData("[]", true)]
    [InlineData("(bad)", false)]
    [InlineData("(bad)", true)]
    public async Task UnusableCoefficientsKeepIdentityFallback(string extra, bool job)
    {
        string content = await FlattenedContent(Fixture("7 0 R 0 0 1 0 0", "20 30 180 110", extra, 0), job);
        Assert.Contains("2 0 0 2 20 30 cm", content, StringComparison.Ordinal);
    }

    private static async Task<string> FlattenedContent(byte[] input, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","flattenAnnotations":"all","streamData":"uncompress"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--flatten-annotations=all", "--stream-data=uncompress", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
        PdfDictionary page = document.PageReferences[0].Page;
        Assert.False(page.TryGet(PdfName.FromAscii("Annots"), out _));
        Assert.True(page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents));
        return string.Concat(Assert.IsType<PdfArray>(contents).Items.Select(item => Encoding.ASCII.GetString(
            Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(item).ObjectNumber)).Data)));
    }

    private static byte[] Fixture(string matrix, string rect, string extra, int angle)
    {
        byte[] appearance = "0 0 m 80 0 l 0 40 l h f\n"u8.ToArray();
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Rotate {angle} /Resources << >> /Annots [4 0 R] >>", null),
            (4, $"<< /Type /Annot /Subtype /Text /F 16 /Rect [{rect}] /AP << /N 5 0 R >> >>", null),
            (5, $"<< /Type /XObject /Subtype /Form /BBox [0 0 80 40] /Matrix [{matrix}] /Resources << >> /Length {appearance.Length} >>", appearance),
            (6, "null", null),
            (7, extra, null)
        ], 1);
    }
}
