using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayScalarTests
{
    private static byte[] Page(string property, string scalar, bool indirect, bool inherited)
    {
        string entry = property.Length == 0 ? "" : "/" + property + " " + (indirect ? "5 0 R" : scalar);
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 " + (inherited ? entry : "") + " >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> /Contents 4 0 R " + (inherited ? "" : entry) + " >>", null),
            (4, "<< /Length 4 >>", "q Q\n"u8.ToArray()), (5, scalar, null)], 1);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string route in new[] { "argv", "job" })
        foreach (string mode in new[] { "overlay", "underlay" })
        foreach (bool source in new[] { false, true })
        foreach ((string property, string scalar, bool inherited) in new[] { ("Rotate", "90", false), ("Rotate", "90", true),
            ("UserUnit", "2", false), ("UserUnit", "2.0", false) })
        { yield return [route, mode, source, property, scalar, inherited]; }
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static QpdfObject Get(QpdfDocument document, PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject raw)); return Resolve(document, raw); }
    private static double[] Matrix(QpdfDocument document, PdfDictionary dictionary)
    {
        if (!dictionary.TryGet(PdfName.FromAscii("Matrix"), out QpdfObject raw)) { return [1, 0, 0, 1, 0, 0]; }
        PdfArray array = Assert.IsType<PdfArray>(Resolve(document, raw));
        double[] values = new double[array.Count];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = Resolve(document, array[index]) switch
            { PdfInteger integer => (double)integer.Value, PdfReal real => real.Value, _ => throw new InvalidOperationException() };
        }
        return values;
    }

    private static async Task<(double[] Base, double[] Stamp, byte[] Program)> Run(
        string route, string mode, bool source, string property, string scalar, bool inherited, bool indirect)
    {
        using InMemoryQpdfHost host = new();
        byte[] subject = Page(property, scalar, indirect, inherited);
        byte[] plain = Page("", "0", false, false);
        host.AddInput(QpdfPath.From("input"), source ? plain : subject, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("stamp"), source ? subject : plain, QpdfInputOwnership.Copy);
        string[] argv = ["input", "--stream-data=uncompress", "--" + mode, "stamp", "--", "output"];
        Dictionary<string, object> job = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["streamData"] = "uncompress", [mode] = new { file = "stamp" } };
        QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("readback"), bytes, QpdfInputOwnership.Copy);
        double[] baseMatrix, stampMatrix; byte[] program;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
        {
            PdfDictionary page = document.PageReferences[0].Page;
            PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(document, page, "Resources"));
            PdfDictionary forms = Assert.IsType<PdfDictionary>(Get(document, resources, "XObject"));
            baseMatrix = Matrix(document, Assert.IsType<PdfStream>(Get(document, forms, "Fx0")).Dictionary);
            stampMatrix = Matrix(document, Assert.IsType<PdfStream>(Get(document, forms, "Fx1")).Dictionary);
            program = Assert.IsType<PdfStream>(Get(document, page, "Contents")).Data;
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        return (baseMatrix, stampMatrix, program);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task IndirectStampScalarsMatchDirectTransforms(string route, string mode, bool source, string property, string scalar, bool inherited)
    {
        var direct = await Run(route, mode, source, property, scalar, inherited, false);
        var indirect = await Run(route, mode, source, property, scalar, inherited, true);
        Assert.Equal(direct.Base, indirect.Base); Assert.Equal(direct.Stamp, indirect.Stamp); Assert.Equal(direct.Program, indirect.Program);
    }

    [Theory]
    [InlineData(true, "Rotate", "null")]
    [InlineData(false, "Rotate", "null")]
    [InlineData(true, "Rotate", "/Bad")]
    [InlineData(false, "Rotate", "/Bad")]
    [InlineData(true, "UserUnit", "/Bad")]
    [InlineData(false, "UserUnit", "/Bad")]
    public async Task UnusableIndirectValuesKeepTheDirectFallback(bool source, string property, string scalar)
    {
        var direct = await Run("argv", "overlay", source, property, scalar, false, false);
        var indirect = await Run("argv", "overlay", source, property, scalar, false, true);
        Assert.Equal(direct.Base, indirect.Base); Assert.Equal(direct.Stamp, indirect.Stamp); Assert.Equal(direct.Program, indirect.Program);
    }

    public static IEnumerable<object[]> RotationCases()
    {
        foreach (string route in new[] { "argv", "job" })
        foreach (string mode in new[] { "overlay", "underlay" })
        foreach (bool source in new[] { false, true })
        foreach (bool inherited in new[] { false, true })
        foreach ((string angle, string equivalent) in new[]
        {
            ("-9223372036854775800", "0"), ("-9223372036854775710", "90"),
            ("-9223372036854775620", "180"), ("-9223372036854775530", "270"),
            ("9223372036854775620", "180"), ("-90", "270"), ("-360", "0"),
        }) { yield return [route, mode, source, inherited, angle, equivalent]; }
    }

    [Theory]
    [MemberData(nameof(RotationCases))]
    public async Task EquivalentRotationsKeepPlacementAcrossSigned64BitValues(
        string route, string mode, bool source, bool inherited, string angle, string equivalent)
    {
        var control = await Run(route, mode, source, "Rotate", equivalent, inherited, inherited);
        var actual = await Run(route, mode, source, "Rotate", angle, inherited, inherited);
        Assert.Equal(control.Base, actual.Base); Assert.Equal(control.Stamp, actual.Stamp); Assert.Equal(control.Program, actual.Program);
    }
}
