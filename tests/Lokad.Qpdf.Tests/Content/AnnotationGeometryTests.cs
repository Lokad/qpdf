using System.Globalization;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AnnotationGeometryTests
{
    [Theory]
    [InlineData(0L, 360L, "1 0 0 1 10 40 cm", false)]
    [InlineData(90L, 450L, "0 1 -1 0 60 90 cm", false)]
    [InlineData(270L, -90L, "0 -1 1 0 -40 90 cm", false)]
    [InlineData(90L, 4294967490L, "0 1 -1 0 60 90 cm", false)]
    [InlineData(0L, 360L, "1 0 0 1 10 40 cm", true)]
    [InlineData(90L, 450L, "0 1 -1 0 60 90 cm", true)]
    [InlineData(270L, -90L, "0 -1 1 0 -40 90 cm", true)]
    [InlineData(90L, 4294967490L, "0 1 -1 0 60 90 cm", true)]
    public async Task EquivalentPageRotationsPreserveNoRotatePlacement(long canonical, long equivalent, string expected, bool job)
    {
        string first = await FlattenedContent(canonical, job);
        string second = await FlattenedContent(equivalent, job);
        Assert.Equal(first, second);
        Assert.Contains(expected, second, StringComparison.Ordinal);
    }

    private static async Task<string> FlattenedContent(long angle, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(angle), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"flattenAnnotations\":\"all\",\"streamData\":\"uncompress\"}", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--flatten-annotations=all", "--stream-data=uncompress", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.Equal(0, host.OpenLeaseCount);
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
        PdfDictionary page = document.PageReferences[0].Page;
        Assert.False(page.TryGet(PdfName.FromAscii("Annots"), out _));
        Assert.True(page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents));
        PdfArray streams = Assert.IsType<PdfArray>(contents);
        return string.Concat(streams.Items.Select(item => Encoding.ASCII.GetString(
            Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(item).ObjectNumber)).Data)));
    }

    private static byte[] Fixture(long angle)
        => CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Rotate " + angle.ToString(CultureInfo.InvariantCulture)
                + " /Resources << >> /Annots [5 0 R] /Contents 4 0 R >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (5, "<< /Type /Annot /Subtype /Text /F 16 /Rect [10 40 110 90] /AP << /N 6 0 R >> >>", null),
            (6, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 50] /Length 3 >>", "q Q"u8.ToArray()),
        ], 1);
}
