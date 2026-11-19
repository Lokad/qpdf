using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ViewportRotationTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf");

    [Theory]
    [InlineData(90, new double[] { 20, 160, 60, 190 }, new double[] { 40, 170, 80, 135 }, new double[] { 41, 169 }, 45, 165)]
    [InlineData(180, new double[] { 160, 240, 190, 280 }, new double[] { 170, 260, 135, 220 }, new double[] { 169, 259 }, 165, 255)]
    [InlineData(270, new double[] { 240, 10, 280, 40 }, new double[] { 260, 30, 220, 65 }, new double[] { 259, 31 }, 255, 35)]
    public async Task BakedViewportPreservesOverlapAxesAndPrivateGeometry(int angle,
        double[] bounds, double[] axes, double[] origin, int x, int y)
    {
        using InMemoryQpdfHost host = Host(angle, "/Subtype /RL", "/O 9 0 R /Y [<< /U (ft) /C 2 >>] /CYX 1", false);
        Assert.Equal(QpdfExitCode.Success, (await Run(host, "input.pdf", "--flatten-rotation", "output.pdf")).ExitCode);
        await using QpdfDocument output = await ReadOutput(host);
        PdfDictionary firstPage = output.PageReferences[0].Page;
        Assert.False(firstPage.TryGet(PdfName.FromAscii("Rotate"), out _));
        PdfArray active = Array(output, Value(firstPage, "VP"));
        PdfDictionary boundsOnly = Dict(output, active[0]);
        PdfDictionary measured = Dict(output, active[1]);
        double[] boundsOnlyBox = Numbers(output, Value(boundsOnly, "BBox"));
        double[] measuredBox = Numbers(output, Value(measured, "BBox"));
        Assert.Equal(bounds, boundsOnlyBox);
        Assert.Equal(axes, measuredBox);
        Assert.Equal(PdfName.FromAscii("Measured"), Value(measured, "AuditRole"));
        Assert.True(Contains(boundsOnlyBox, x, y));
        Assert.True(Contains(measuredBox, x, y));

        PdfDictionary measure = Dict(output, Value(measured, "Measure"));
        Assert.Equal(origin, Numbers(output, Value(measure, "O")));
        Assert.Equal(1L, Factor(output, measure, "X"));
        Assert.Equal(2L, Factor(output, measure, "Y"));
        Assert.Equal(active[1], active[2]);

        PdfDictionary catalog = Dict(output, Value(output.Engine.Trailer, "Root"));
        QpdfObject original = Value(catalog, "PrivateViewports");
        Assert.Equal(original, Value(output.PageReferences[1].Page, "VP"));
        PdfArray unchanged = Array(output, original);
        Assert.NotEqual(unchanged[1], active[1]);
        PdfDictionary other = Dict(output, unchanged[1]);
        Assert.Equal([30d, 40d, 65d, 80d], Numbers(output, Value(other, "BBox")));
        Assert.Equal([31d, 41d], Numbers(output, Value(Dict(output, Value(other, "Measure")), "O")));
    }

    [Fact]
    public async Task BoundsKeepTheDefaultMeasurementOriginAndSharedFormats()
    {
        using InMemoryQpdfHost host = Host(180, string.Empty, string.Empty, true);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input.pdf","flattenRotation":"","outputFile":"output.pdf"}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument output = await ReadOutput(host);
        PdfDictionary baked = Dict(output, Array(output, Value(output.PageReferences[0].Page, "VP"))[1]);
        PdfDictionary unchanged = Dict(output, Array(output, Value(output.PageReferences[1].Page, "VP"))[1]);
        Assert.Equal([170d, 260d, 135d, 220d], Numbers(output, Value(baked, "BBox")));
        Assert.Equal(Value(unchanged, "Measure"), Value(baked, "Measure"));
        Assert.False(Dict(output, Value(baked, "Measure")).TryGet(PdfName.FromAscii("O"), out _));
    }

    [Theory]
    [InlineData("/Subtype /Other", "")]
    [InlineData("/Subtype /RL", "/O [10 /bad]")]
    public async Task UnusableViewportMeasurementRefusesBeforePublication(string subtype, string origin)
    {
        using InMemoryQpdfHost host = Host(90, subtype, origin, false);
        QpdfExecutionResult result = await Run(host, "input.pdf", "--flatten-rotation", "output.pdf");
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.UnsupportedFeature);
        Assert.False(host.TryGetOutput(Output, out _));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static InMemoryQpdfHost Host(int angle, string measureType, string measureEntries, bool direct)
    {
        byte[] program = "q 30 40 35 40 re f Q\n"u8.ToArray();
        const string viewport = "<< /Type /Viewport /BBox 7 0 R /Measure 8 0 R /AuditRole /Measured >>";
        string viewports = direct ? $"[5 0 R {viewport}]" : "10 0 R";
        InMemoryQpdfHost host = new();
        host.AddInput(Input, PdfTestDocument.Assemble(
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /PrivateViewports 10 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 11 0 R] /Count 2 >>", null),
            (3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Rotate {angle} /Resources << >> /Contents 4 0 R /VP {viewports} >>", null),
            (4, $"<< /Length {program.Length} >>", program),
            (5, "<< /Type /Viewport /BBox [10 20 40 60] >>", null),
            (6, viewport, null),
            (7, "[30 40 65 80]", null),
            (8, $"<< /Type /Measure {measureType} /R (scale) /X [<< /U (ft) /C 1 >>] /D [<< /U (ft) /C 1 >>] /A [<< /U (ft2) /C 1 >>] {measureEntries} >>", null),
            (9, "[31 41]", null),
            (10, "[5 0 R 6 0 R 6 0 R]", null),
            (11, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << >> /VP 10 0 R >>", null),
        ], 1, null, "1.6", string.Empty), QpdfInputOwnership.Transfer);
        return host;
    }

    private static ValueTask<QpdfExecutionResult> Run(InMemoryQpdfHost host, params string[] args)
        => Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(args), CancellationToken.None);

    private static async ValueTask<QpdfDocument> ReadOutput(InMemoryQpdfHost host)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        host.AddInput(Output, bytes, QpdfInputOwnership.Transfer);
        return await QpdfDocument.OpenAsync(host, Output, CancellationToken.None);
    }

    private static long Factor(QpdfDocument pdf, PdfDictionary measure, string axis)
        => Assert.IsType<PdfInteger>(Value(Dict(pdf, Assert.Single(Array(pdf, Value(measure, axis)).BorrowedItems)), "C")).Value;

    private static double[] Numbers(QpdfDocument pdf, QpdfObject value)
        => Array(pdf, value).BorrowedItems.Select(number => number is PdfInteger integer ? integer.Value : Assert.IsType<PdfReal>(number).Value).ToArray();

    private static bool Contains(double[] bounds, int x, int y)
        => Math.Min(bounds[0], bounds[2]) <= x && x <= Math.Max(bounds[0], bounds[2])
            && Math.Min(bounds[1], bounds[3]) <= y && y <= Math.Max(bounds[1], bounds[3]);

    private static PdfDictionary Dict(QpdfDocument pdf, QpdfObject value) => Assert.IsType<PdfDictionary>(Resolve(pdf, value));

    private static PdfArray Array(QpdfDocument pdf, QpdfObject value) => Assert.IsType<PdfArray>(Resolve(pdf, value));

    private static QpdfObject Resolve(QpdfDocument pdf, QpdfObject value) => value is PdfReference reference ? pdf.GetObject(reference) : value;

    private static QpdfObject Value(PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value), key);
        return value;
    }
}
