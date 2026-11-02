using System.Globalization;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AnnotationVisibilityTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (int flags in new[] { 0, 1, 2, 3, 4, 6, 32, 36 })
        foreach (string mode in new[] { "all", "screen", "print" })
        foreach (bool job in new[] { false, true })
        { yield return [flags, mode, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task VisibilityControlsStampingWithoutRetainingFlattenedAnnotations(int flags, string mode, bool job)
    {
        string bits = flags.ToString(CultureInfo.InvariantCulture);
        byte[] fixture = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Subtype /Stamp /Rect [10 20 30 40] /F " + (job ? "5 0 R" : bits) + " /AP << /N 6 0 R >> >>", null),
            (5, bits, null),
            (6, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length 4 >>", "q Q\n"u8.ToArray())], 1);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), fixture, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", flattenAnnotations = mode, streamData = "uncompress" }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--flatten-annotations=" + mode, "--stream-data=uncompress", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
        PdfDictionary page = document.PageReferences[0].Page;
        Assert.False(page.TryGet(PdfName.FromAscii("Annots"), out _));
        bool visible = (flags & 3) == 0 && (mode != "screen" || (flags & 32) == 0)
            && (mode != "print" || (flags & 4) != 0);
        PdfArray contents = Assert.IsType<PdfArray>(Get(page, "Contents"));
        string commands = string.Concat(contents.Items.Select(item => Encoding.ASCII.GetString(
            Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(item).ObjectNumber)).RawMemory.Span)));
        Assert.Equal(visible, commands.Contains("/Fxo1 Do", StringComparison.Ordinal));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(page, "Resources"));
        Assert.Equal(visible, resources.TryGet(PdfName.FromAscii("XObject"), out _));
    }

    private static QpdfObject Get(PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value;
    }
}
