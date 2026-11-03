using System.Globalization;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class GeometryPrecisionTests
{
    public static IEnumerable<object[]> Numbers()
    {
        foreach (double value in new[] { 0.0, double.Epsilon, BitConverter.UInt64BitsToDouble(0x0010000000000000),
            1e-308, 1e-20, 0.000001, 0.0000049, 0.0000051, 0.00001, 0.1, 1.0 / 3.0,
            1.0, Math.BitDecrement(1.0), Math.BitIncrement(1.0), 1.2345678901234567, 1000000.000001,
            100000000000000000000.0, (double)long.MaxValue, double.MaxValue })
        { yield return [value]; yield return [-value]; }
    }

    [Theory]
    [MemberData(nameof(Numbers))]
    public void FormattedGeometryRoundTripsThroughThePdfReader(double expected)
    {
        Span<char> target = stackalloc char[384];
        int length = WriterNumber.FormatGeometry(expected, target);
        string text = target[..length].ToString();
        Assert.DoesNotContain('E', text); Assert.DoesNotContain('e', text);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
        QpdfObject parsed = parser.ParseValue(new PdfTokenizer(Encoding.ASCII.GetBytes(text)), 0, memory);
        double actual = Number(parsed);
        Assert.Equal(BitConverter.DoubleToUInt64Bits(expected == 0.0 ? 0.0 : expected), BitConverter.DoubleToUInt64Bits(actual));
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    public void NonFiniteGeometryRejects(double value)
        => Assert.Throws<QpdfParseException>(() => WriterNumber.FormatGeometry(value, new char[384]));

    [Theory]
    [InlineData(double.Epsilon)]
    [InlineData(0.000001)]
    [InlineData(double.MaxValue)]
    public void InsufficientDecimalStorageRejects(double value)
        => Assert.Throws<QpdfParseException>(() => WriterNumber.FormatGeometry(value, new char[3]));

    public static IEnumerable<object[]> ThinPages()
    {
        foreach (bool job in new[] { false, true })
        foreach (int angle in new[] { 90, 180, 270 })
        foreach (string width in new[] { "0.000001", "0.0000049", "0.0000051", "0.00001", "0.001", "1" })
        { yield return [job, angle, width]; }
    }

    [Theory]
    [MemberData(nameof(ThinPages))]
    public async Task FlatteningPreservesNonzeroDimensionsAfterCleanup(bool job, int angle, string width)
    {
        PdfArray escaped;
        using (InMemoryQpdfHost host = new())
        {
            byte[] source = CheckTests.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + width + " 100] /Resources << >> /Rotate " + angle + " >>", null)], 1);
            host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","flattenRotation":true}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--flatten-rotation", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.Equal(0L, host.OpenLeaseCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
            Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("MediaBox"), out QpdfObject box));
            escaped = Assert.IsType<PdfArray>(box);
        }
        double dimension = double.Parse(width, CultureInfo.InvariantCulture);
        Assert.Equal(angle == 180 ? dimension : 100, Number(escaped[2]) - Number(escaped[0]));
        Assert.Equal(angle == 180 ? 100 : dimension, Number(escaped[3]) - Number(escaped[1]));
    }

    private static double Number(QpdfObject value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => throw new InvalidOperationException("Geometry must be numeric."),
    };
}
