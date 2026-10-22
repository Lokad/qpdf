using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class RotationAppearanceTests
{
    [Theory]
    [InlineData("states", "argv")]
    [InlineData("states", "selection")]
    [InlineData("states", "job")]
    [InlineData("matrix", "argv")]
    [InlineData("matrix", "selection")]
    [InlineData("matrix", "job")]
    [InlineData("numbers", "argv")]
    [InlineData("numbers", "selection")]
    [InlineData("numbers", "job")]
    public async Task DirectAndIndirectAppearanceValuesAreEquivalent(string mode, string route)
    {
        double[] expected = mode == "states" ? [0, -1, 1, 0, 0, 612] : [-0.5, -3, 2, 1, 311, 1843];
        foreach (bool indirect in new[] { false, true })
        {
            using InMemoryQpdfHost host = new();
            host.AddInput(QpdfPath.From("input"), Fixture(mode, indirect), QpdfInputOwnership.Transfer);
            QpdfExecutionResult result = await Run(host, route, CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
            PdfStream escaped;
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None))
            { escaped = Appearance(document, 0, 0); }
            host.Dispose();
            Assert.Equal(expected, Matrix(escaped));
            Assert.Equal("q Q"u8.ToArray(), escaped.Data);
            Assert.Equal(0L, host.OpenLeaseCount);
        }
    }

    [Theory]
    [InlineData("6 0 R", "6 0 R", false)]
    [InlineData("<< /On 6 0 R >>", "<< /Loop 6 0 R >>", false)]
    [InlineData("<< /On 99 0 R >>", "null", true)]
    public async Task DamagedAppearanceReferencesStayBounded(string normal, string extra, bool fails)
    {
        using InMemoryQpdfHost host = new();
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Rotate 90 /Annots [4 0 R] >>", null),
            (4, "<< /Subtype /Stamp /Rect [10 20 50 40] /AP << /N " + normal + " >> >>", null),
            (5, "null", null),
            (6, extra, null)], 1);
        host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, "argv", CancellationToken.None);
        Assert.Equal(0L, host.OpenLeaseCount);
        if (fails)
        {
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.False(host.TryGetOutput(QpdfPath.From("output"), out _));
            Assert.Contains(result.Diagnostics, d => d.Severity == QpdfDiagnosticSeverity.Error);
            return;
        }
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
        Assert.False(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Rotate"), out _));
    }

    internal static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, string route, CancellationToken token)
        => route == "job" ? Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input","outputFile":"output","flattenRotation":true,"streamData":"uncompress"}""", token)
            : Qpdf.ExecuteAsync(host, new(route == "selection"
                ? ["input", "--flatten-rotation", "--stream-data=uncompress", "--pages", ".", "1-z", "--", "output"]
                : ["input", "--flatten-rotation", "--stream-data=uncompress", "output"]), token);

    private static byte[] Fixture(string mode, bool indirect)
    {
        string normal = mode == "states" ? indirect ? "6 0 R" : "<< /On 5 0 R >>" : "5 0 R";
        string matrix = mode == "states" ? "" : mode == "matrix" && indirect ? "/Matrix 6 0 R"
            : mode == "numbers" && indirect ? "/Matrix [7 0 R 1 0.5 3 5 7]" : "/Matrix [2 1 0.5 3 5 7]";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Rotate 90 /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Subtype /Stamp /Rect [10 20 50 40] /AS /On /AP " + (indirect ? "8 0 R" : "<< /N " + normal + " >>") + " >>", null),
            (5, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 10] /Length 3 " + matrix + " >>", "q Q"u8.ToArray()),
            (6, mode == "states" ? "<< /On 5 0 R >>" : "[2 1 0.5 3 5 7]", null),
            (7, "2", null),
            (8, "<< /N " + normal + " >>", null)], 1);
    }

    internal static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;

    internal static QpdfObject Value(PdfDictionary dictionary, string name)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return value; }

    internal static PdfStream Appearance(QpdfDocument document, int page, int annotation)
    {
        PdfArray annots = Assert.IsType<PdfArray>(Resolve(document, Value(document.PageReferences[page].Page, "Annots")));
        PdfDictionary annot = Assert.IsType<PdfDictionary>(Resolve(document, annots[annotation]));
        PdfDictionary ap = Assert.IsType<PdfDictionary>(Resolve(document, Value(annot, "AP")));
        QpdfObject normal = Resolve(document, Value(ap, "N"));
        return Assert.IsType<PdfStream>(normal is PdfDictionary states ? Resolve(document, Value(states, "On")) : normal);
    }

    internal static double[] Matrix(PdfStream stream)
        => Assert.IsType<PdfArray>(Value(stream.Dictionary, "Matrix")).Items
            .Select(v => v is PdfInteger integer ? (double)integer.Value : Assert.IsType<PdfReal>(v).Value).ToArray();
}
