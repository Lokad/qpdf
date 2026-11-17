using System.Text;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Syntax;

public sealed class PdfLiteralCodecTests
{
    public static IEnumerable<object[]> Values()
    {
        (string Body, string Value)[] values = [
            ("(a\rb)", "a\nb"), ("(a\r\nb)", "a\nb"), ("(a\nb)", "a\nb"),
            ("(a\r\r\nb)", "a\n\nb"), ("(a\n\rb)", "a\n\nb"),
            ("(a\\rb)", "a\rb"), ("(a\\nb)", "a\nb"), ("(a\\015\\012b)", "a\r\nb"),
            ("(a\\\r\nb\\\rc\\\nd)", "abcd"), ("(a\\\r\r\nb)", "a\nb"),
            ("(a(b)c)", "a(b)c"), ("(a\\(b\\)c)", "a(b)c"), ("(a\0b)", "a\0b"),
            ("(\r\n)", "\n"), ("(" + new string('x', 8191) + "\r\nY)", new string('x', 8191) + "\nY")];
        foreach (var value in values) { yield return [value.Body, value.Value]; }
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void LiteralValuesBelongToTheSharedCodec(string body, string expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] raw = Encoding.Latin1.GetBytes(body[1..^1]);
        int length = PdfLiteralCodec.DecodedLength(raw, 19L, governor, CancellationToken.None);
        byte[] decoded = new byte[length]; PdfLiteralCodec.Decode(raw, decoded, 19L, governor, CancellationToken.None);
        Assert.Equal(Encoding.Latin1.GetBytes(expected), decoded);
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Fact]
    public void EveryOctalValueUsesAtMostThreeDigitsAndTruncatesToOneByte()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        for (int value = 0; value < 512; value++)
        {
            byte[] source = Encoding.ASCII.GetBytes("\\" + Convert.ToString(value, 8).PadLeft(3, '0') + "8");
            Assert.Equal(2, PdfLiteralCodec.DecodedLength(source, 0L, governor, CancellationToken.None));
            byte[] decoded = new byte[2]; PdfLiteralCodec.Decode(source, decoded, 0L, governor, CancellationToken.None);
            Assert.Equal(new byte[] { (byte)value, (byte)'8' }, decoded);
        }
        byte[] shortSource = "\\1\\77\\q"u8.ToArray(), shortValue = new byte[3];
        PdfLiteralCodec.Decode(shortSource, shortValue, 0L, governor, CancellationToken.None);
        Assert.Equal(new byte[] { 1, 63, (byte)'q' }, shortValue);
    }

    [Fact]
    public void CanonicalEscapesCoverNamedDelimiterPrintableAndOctalClasses()
    {
        byte[] value = [10, 13, 9, 8, 12, 40, 41, 92, 0, 31, 32, 126, 127, 255];
        byte[] expected = "(\\n\\r\\t\\b\\f\\(\\)\\\\\\000\\037 ~\\177\\377)"u8.ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        long length = PdfLiteralCodec.EncodedLength(value, 1024L, "literal", governor, CancellationToken.None, null);
        Assert.Equal(expected.LongLength, length);
        byte[] encoded = new byte[(int)length]; PdfLiteralCodec.Encode(value, encoded, governor, CancellationToken.None, null);
        Assert.Equal(expected, encoded);
        byte[] decoded = new byte[value.Length]; PdfLiteralCodec.Decode(encoded.AsSpan(1, encoded.Length - 2), decoded, 0L, governor, CancellationToken.None);
        Assert.Equal(value, decoded);
    }

    [Fact]
    public void TrailingBackslashRetainsTheCallersErrorOffset()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfParseException failure = Assert.Throws<QpdfParseException>(() => PdfLiteralCodec.DecodedLength("abc\\"u8, 37L, governor, CancellationToken.None));
        Assert.Equal(37L, failure.ByteOffset); Assert.Contains("Trailing backslash", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1L, 2L)]
    [InlineData(5L, 6L)]
    public void EncodedLengthUsesTheCallersOutputCeiling(long maximum, long requested)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
            PdfLiteralCodec.EncodedLength(new byte[] { 0 }, maximum, "literal-cap", governor, CancellationToken.None, null));
        Assert.Equal("literal-cap", failure.LimitName); Assert.Equal(requested, failure.Requested); Assert.Equal(maximum, failure.Allowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContinuationOnlyDecodingChecksCancellationAfterActualWork(bool caller)
    {
        byte[] source = Enumerable.Range(0, 32768).SelectMany(_ => new byte[] { (byte)'\\', 13, 10 }).ToArray();
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        int observations = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => PdfLiteralCodec.DecodedLength(source, 0L, governor,
            caller ? cancellation.Token : CancellationToken.None, () => { observations++; cancellation.Cancel(); }));
        Assert.Equal(1, observations); Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EncodingChecksBothTokensBetweenCompletedBlocks(bool caller, bool fill)
    {
        byte[] source = new byte[32768], target = new byte[4 * 32768 + 2];
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        int observations = 0; Action observe = () => { observations++; cancellation.Cancel(); };
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            if (fill) { PdfLiteralCodec.Encode(source, target, governor, caller ? cancellation.Token : CancellationToken.None, observe); }
            else { _ = PdfLiteralCodec.EncodedLength(source, target.Length, "literal", governor, caller ? cancellation.Token : CancellationToken.None, observe); }
        });
        Assert.Equal(1, observations); Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }
}
