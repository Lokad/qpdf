using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class TransformationGeometryTests
{
    [Theory]
    [InlineData("annotation", false)]
    [InlineData("annotation", true)]
    [InlineData("rotation", false)]
    [InlineData("rotation", true)]
    [InlineData("rectangle", false)]
    [InlineData("rectangle", true)]
    [InlineData("appearance", false)]
    [InlineData("appearance", true)]
    public async Task NonFiniteComputedGeometryFailsBeforePublication(string mode, bool job)
    {
        RecordingMemoryBudget root = new(16777216L);
        using (IMemoryScope caller = root.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, caller))
        {
            string huge = "1" + new string('0', 308) + ".0";
            string upper = "11" + new string('0', 307) + ".0";
            string tiny = "." + new string('0', 307) + "1";
            string media = mode == "rotation" ? $"{huge} 0 {upper} 100" : mode == "annotation" ? "0 0 100 100" : $"0 0 {huge} 100";
            string rect = mode == "rectangle" ? $"-{upper} 0 -{huge} 10" : "0 0 20 20";
            string box = mode == "annotation" ? $"0 0 {tiny} {tiny}" : "0 0 10 10";
            string matrix = mode == "appearance" ? $"1 0 0 1 0 {huge}" : "1 0 0 1 0 0";
            host.AddInput(QpdfPath.From("input"), Fixture(media, rect, box, matrix), QpdfInputOwnership.Copy);
            await using (QpdfDocument original = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None))
            { for (int number = 1; number <= 6; number++) { _ = original.GetObject(number); } }
            await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
            {
                await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
                await prior.CommitAsync(CancellationToken.None);
            }
            QpdfExecutionResult result = await Run(host, mode == "annotation", job);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains("non-finite geometry", StringComparison.Ordinal));
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            Assert.Equal("prior"u8.ToArray(), output);
            Assert.Equal(0, host.OpenLeaseCount);
        }
        Assert.Equal(0L, root.ReservedBytes);
        Assert.Equal(0, root.LiveReservations);
    }

    [Theory]
    [InlineData("0 0 100000000000000000000.0 100", false)]
    [InlineData("0 0 100000000000000000000.0 100", true)]
    [InlineData("-100000000000000000000.0 0 -90000000000000000000.0 100", false)]
    [InlineData("-100000000000000000000.0 0 -90000000000000000000.0 100", true)]
    public async Task LargeFiniteResultsRoundTripAndEscapeDocumentDisposal(string media, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(media, "0 0 20 20", "0 0 10 10", "1 0 0 1 0 0"), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, false, job);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Copy);
        PdfReal escaped;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
        {
            foreach (Match match in Regex.Matches(Encoding.ASCII.GetString(output), @"(?m)^(\d+) 0 obj$"))
            { _ = document.GetObject(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)); }
            PdfDictionary page = document.PageReferences[0].Page;
            Assert.True(page.TryGet(PdfName.FromAscii("MediaBox"), out QpdfObject raw));
            escaped = Assert.IsType<PdfReal>(Assert.IsType<PdfArray>(raw)[3]);
        }
        Assert.True(double.IsFinite(escaped.Value));
        Assert.Contains('.', escaped.Text);
        Assert.True(Math.Abs(escaped.Value) >= 9e19);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, bool annotation, bool job)
        => job ? Qpdf.ExecuteJobAsync(host, annotation
            ? "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"flattenAnnotations\":\"all\",\"streamData\":\"uncompress\"}"
            : "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"flattenRotation\":true,\"streamData\":\"uncompress\"}", CancellationToken.None)
            : Qpdf.ExecuteAsync(host, new(["input", annotation ? "--flatten-annotations=all" : "--flatten-rotation", "--stream-data=uncompress", "output"]), CancellationToken.None);

    private static byte[] Fixture(string media, string rect, string box, string matrix)
        => CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, $"<< /Type /Page /Parent 2 0 R /MediaBox [{media}] /Rotate 90 /Resources << >> /Annots [5 0 R] /Contents 4 0 R >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (5, $"<< /Type /Annot /Subtype /Square /Rect [{rect}] /AP << /N 6 0 R >> >>", null),
            (6, $"<< /Type /XObject /Subtype /Form /BBox [{box}] /Matrix [{matrix}] /Length 3 >>", "q Q"u8.ToArray()),
        ], 1);
}
