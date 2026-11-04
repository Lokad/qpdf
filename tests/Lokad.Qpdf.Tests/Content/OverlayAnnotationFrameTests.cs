using System.Globalization;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayAnnotationFrameTests
{
    private static readonly byte[] Payload = "1 0 0 rg 0 0 30 20 re f 0 1 0 rg 0 0 10 10 re f"u8.ToArray();

    public static IEnumerable<object[]> Frames()
    {
        foreach (int angle in new[] { 0, 90, 180, 270 })
        foreach (double unit in new[] { .5, 1, 2 })
        foreach (bool underlay in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [angle, unit, underlay, job]; }
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public async Task SourceRotationAndUnitMatchIndependentlyBakedAnnotationFrames(
        int angle, double unit, bool underlay, bool job)
    {
        double[] matrix = angle switch
        {
            90 => [0, -unit, unit, 0, 0, 100 * unit],
            180 => [-unit, 0, 0, -unit, 100 * unit, 200 * unit],
            270 => [0, unit, -unit, 0, 200 * unit, 0],
            _ => [unit, 0, 0, unit, 0, 0],
        };
        double width = (angle is 90 or 270 ? 200 : 100) * unit;
        double height = (angle is 90 or 270 ? 100 : 200) * unit;
        double[] rect = Bounds(matrix, [10, 20, 40, 40]);
        double[] expected = [rect[0] + (400 - width) / 2, rect[1] + (400 - height) / 2,
            rect[2] + (400 - width) / 2, rect[3] + (400 - height) / 2];
        (double[] originalRect, double[] originalAppearance) = await Place(
            Source(angle, unit, null, null, job), [1, 0, 0, 1, 0, 0], underlay, job);
        (double[] bakedRect, double[] bakedAppearance) = await Place(
            Source(0, 1, [0, 0, width, height], (rect, matrix), job), matrix, underlay, job);
        Assert.Equal(expected, originalRect);
        Assert.Equal(expected, bakedRect);
        // Appearance translations are normalized against their transformed BBox.
        Assert.Equal(bakedAppearance[..4], originalAppearance[..4]);
    }

    private static async Task<(double[] Rect, double[] Appearance)> Place(
        byte[] source, double[] sourceMatrix, bool underlay, bool job)
    {
        RecordingMemoryBudget budget = new(32L * 1048576L);
        using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        host.AddInput(QpdfPath.From("stamp"), source, QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("input"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << >> >>", null)], 1), QpdfInputOwnership.Transfer);
        string mode = underlay ? "underlay" : "overlay";
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new Dictionary<string, object>
                { ["inputFile"] = "input", ["outputFile"] = "output", ["compressStreams"] = "n", ["decodeLevel"] = "none", [mode] = new { file = "stamp" } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--compress-streams=n", "--decode-level=none", "--" + mode, "stamp", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        PdfStream original, appearance; double[] rect, transform;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
        {
            PdfArray annotations = Assert.IsType<PdfArray>(Get(document, document.PageReferences[0].Page, "Annots"));
            Assert.Equal(2, annotations.Count);
            PdfDictionary first = Assert.IsType<PdfDictionary>(Resolve(document, annotations[0]));
            PdfDictionary second = Assert.IsType<PdfDictionary>(Resolve(document, annotations[1]));
            rect = Numbers(document, Assert.IsType<PdfArray>(Get(document, first, "Rect")));
            Assert.Equal(rect, Numbers(document, Assert.IsType<PdfArray>(Get(document, second, "Rect"))));
            original = Assert.IsType<PdfStream>(Get(document, first, "Private"));
            Assert.Same(original, Get(document, second, "Private"));
            appearance = Assert.IsType<PdfStream>(Get(document, Assert.IsType<PdfDictionary>(Get(document, first, "AP")), "N"));
            Assert.Same(appearance, Get(document, Assert.IsType<PdfDictionary>(Get(document, second, "AP")), "N"));
            // The opaque alias still has the exact source header, not the placed one.
            Assert.True(original.Dictionary.TryGet(PdfName.FromAscii("Matrix"), out QpdfObject originalMatrix));
            Assert.Equal(sourceMatrix, Numbers(document,
                Assert.IsType<PdfArray>(Get(document, first, "PrivateMatrix"))));
            Assert.Same(Get(document, first, "PrivateMatrix"), Resolve(document, originalMatrix));
            transform = Numbers(document, Assert.IsType<PdfArray>(Get(document, appearance.Dictionary, "Matrix")));
        }
        host.Dispose();
        Assert.True(original.RawMemory.Span.SequenceEqual(Payload));
        Assert.True(appearance.RawMemory.Span.SequenceEqual(Payload));
        owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
        return (rect, transform);
    }

    private static byte[] Source(int angle, double unit, double[]? media,
        (double[] Rect, double[] Matrix)? baked, bool indirect)
    {
        string Array(double[] values) => "[" + string.Join(" ", values.Select(value => value.ToString(CultureInfo.InvariantCulture))) + "]";
        string rect = baked is { } geometry ? Array(geometry.Rect) : "[10 20 40 40]";
        string matrix = baked is { } appearance ? Array(appearance.Matrix) : "[1 0 0 1 0 0]";
        string Annot() => "<< /Type /Annot /Subtype /Stamp /Rect " + rect
            + " /F 4 /AP << /N 7 0 R >> /Private 7 0 R /PrivateMatrix 9 0 R >>";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1" + (indirect ? " /Rotate 10 0 R" : "") + " >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox " + (media is null ? "[0 0 100 200]" : Array(media))
                + (indirect ? " /UserUnit 11 0 R" : " /Rotate " + angle + " /UserUnit " + unit.ToString(CultureInfo.InvariantCulture))
                + " /Resources << >> /Annots [5 0 R 6 0 R] >>", null),
            (4, "null", null), (5, Annot(), null), (6, Annot(), null),
            (7, "<< /Subtype /Form /BBox [0 0 30 20] /Resources << >> /Matrix 9 0 R /Length " + Payload.Length + " >>", Payload),
            (8, "null", null),
            (9, matrix, null), (10, angle.ToString(CultureInfo.InvariantCulture), null),
            (11, unit.ToString(CultureInfo.InvariantCulture), null)], 1);
    }

    private static double[] Bounds(double[] matrix, double[] rect)
    {
        (double X, double Y)[] points = [(rect[0], rect[1]), (rect[0], rect[3]), (rect[2], rect[1]), (rect[2], rect[3])];
        var moved = points.Select(point => (X: matrix[0] * point.X + matrix[2] * point.Y + matrix[4],
            Y: matrix[1] * point.X + matrix[3] * point.Y + matrix[5])).ToArray();
        return [moved.Min(point => point.X), moved.Min(point => point.Y), moved.Max(point => point.X), moved.Max(point => point.Y)];
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    private static QpdfObject Get(QpdfDocument document, PdfDictionary owner, string key)
    { Assert.True(owner.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(document, value); }

    private static double[] Numbers(QpdfDocument document, PdfArray array)
        => array.Items.Select(item => Resolve(document, item) is PdfInteger integer ? (double)integer.Value
            : Assert.IsType<PdfReal>(Resolve(document, item)).Value).ToArray();
}
