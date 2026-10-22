using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class RotationAppearanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObsoleteAppearanceChainsOnlyRemainWhenPreservationIsRequested(bool preserve)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("states", true), QpdfInputOwnership.Transfer);
        List<string> arguments = ["input", "--flatten-rotation", "--stream-data=uncompress"];
        if (preserve) { arguments.Add("--preserve-unreferenced"); }
        arguments.Add("output");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        // A unique appearance payload must not be serialized again merely
        // because its original AP/state containers remained in the copied pool.
        string text = System.Text.Encoding.ASCII.GetString(output);
        Assert.Equal(preserve ? 2 : 1, text.Split("q Q", StringSplitOptions.None).Length - 1);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(0, "argv")]
    [InlineData(0, "selection")]
    [InlineData(0, "job")]
    [InlineData(270, "argv")]
    [InlineData(270, "selection")]
    [InlineData(270, "job")]
    public async Task SharedAppearancesStayLocalToTheirPageAndKeepFieldIdentities(int secondAngle, string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), SharedFixture(secondAngle), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, route, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
        PdfStream first, second;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None))
        {
            first = Appearance(document, 0, 0); second = Appearance(document, 1, 0);
            Assert.Same(first, Appearance(document, 0, 1)); Assert.NotSame(first, second);
            PdfArray annots = Assert.IsType<PdfArray>(Value(document.PageReferences[0].Page, "Annots"));
            PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(document, annots[0]));
            PdfDictionary reply = Assert.IsType<PdfDictionary>(Resolve(document, annots[1]));
            Assert.Equal(annots[0], Value(reply, "IRT"));
            PdfDictionary ap = Assert.IsType<PdfDictionary>(Resolve(document, Value(widget, "AP")));
            Assert.Same(first, Resolve(document, Value(ap, "D")));
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(document, Value(document.Engine.Trailer, "Root")));
            PdfDictionary form = Assert.IsType<PdfDictionary>(Resolve(document, Value(catalog, "AcroForm")));
            PdfArray fields = Assert.IsType<PdfArray>(Resolve(document, Value(form, "Fields")));
            Assert.Contains(annots[0], fields.Items);
        }
        host.Dispose();
        Assert.Equal(new double[] { 0, -1, 1, 0, 0, 612 }, Matrix(first));
        if (secondAngle == 0) { Assert.False(second.Dictionary.TryGet(PdfName.FromAscii("Matrix"), out _)); }
        else { Assert.Equal(new double[] { 0, 1, -1, 0, 792, 0 }, Matrix(second)); }
        Assert.Equal("q Q"u8.ToArray(), first.Data); Assert.Equal(first.Data, second.Data);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    internal static byte[] SharedFixture(int secondAngle) => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R] >> >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Rotate 90 /Annots [5 0 R 8 0 R] >>", null),
        (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Rotate " + secondAngle + " /Annots [6 0 R] >>", null),
        (5, "<< /Subtype /Widget /FT /Btn /T (first) /V /On /AS /On /Rect [10 20 50 40] /AP 9 0 R >>", null),
        (6, "<< /Subtype /Widget /FT /Btn /T (second) /V /On /AS /On /Rect [10 20 50 40] /AP 9 0 R >>", null),
        (7, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 10] /Length 3 >>", "q Q"u8.ToArray()),
        (8, "<< /Subtype /Stamp /Rect [20 30 60 50] /IRT 5 0 R /AP << /N 7 0 R >> >>", null),
        (9, "<< /N 10 0 R /D 7 0 R >>", null),
        (10, "<< /On 7 0 R /Off 7 0 R >>", null)], 1);

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
            {
                escaped = Appearance(document, 0, 0);
                PdfArray annots = Assert.IsType<PdfArray>(Value(document.PageReferences[0].Page, "Annots"));
                PdfDictionary annot = Assert.IsType<PdfDictionary>(Resolve(document, annots[0]));
                QpdfObject ap = Value(annot, "AP");
                Assert.Equal(indirect, ap is PdfReference);
                if (mode == "states")
                {
                    QpdfObject normal = Value(Assert.IsType<PdfDictionary>(Resolve(document, ap)), "N");
                    Assert.Equal(indirect, normal is PdfReference);
                }
            }
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
