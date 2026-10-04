using System.Globalization;
using System.Numerics;
using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Syntax;

public sealed class RealParserTests
{
    private static (PdfParser Parser, ResourceGovernor Governor) Create(QpdfLimits limits, bool repair)
    {
        ResourceGovernor governor = new(limits, CancellationToken.None);
        return (new PdfParser(limits, governor, new DiagnosticBag(governor), null) { TolerateObjectErrors = repair }, governor);
    }

    private static string FixedDecimal(BigInteger numerator, int scale)
    {
        string digits = numerator.ToString(CultureInfo.InvariantCulture);
        return digits.Length > scale
            ? digits.Insert(digits.Length - scale, ".")
            : "0." + new string('0', scale - digits.Length) + digits;
    }

    private static (BigInteger Mantissa, int Power) BinaryValue(ulong bits)
    {
        int exponent = (int)((bits >> 52) & 2047UL);
        ulong fraction = bits & ((1UL << 52) - 1UL);
        return exponent == 0
            ? (new BigInteger(fraction), -1074)
            : (new BigInteger((1UL << 52) | fraction), exponent - 1023 - 52);
    }

    public static IEnumerable<object[]> RoundingCases()
    {
        // Exact midpoint rationals, independently constructed from IEEE bits.
        // Adding/removing one tiny decimal unit tests tails beyond the compact
        // conversion prefix, rather than treating a BCL parser as the oracle.
        ulong[] lowerValues =
        [
            0UL, 1UL, 2UL, 3UL,
            0x000FFFFFFFFFFFFEUL, 0x000FFFFFFFFFFFFFUL,
            0x0010000000000000UL, 0x0010000000000001UL,
            0x3FEFFFFFFFFFFFFFUL, 0x3FF0000000000000UL, 0x3FF0000000000001UL,
            0x7FEFFFFFFFFFFFFEUL, 0x7FEFFFFFFFFFFFFFUL,
        ];
        foreach (ulong lowerBits in lowerValues)
        {
            (BigInteger lower, int lowerPower) = BinaryValue(lowerBits);
            // At the overflow boundary the virtual next value is 2^1024.
            (BigInteger upper, int upperPower) = lowerBits == 0x7FEFFFFFFFFFFFFFUL
                ? (BigInteger.One, 1024) : BinaryValue(lowerBits + 1UL);
            int power = Math.Min(lowerPower, upperPower) - 1;
            BigInteger numerator = (lower << (lowerPower - power - 1)) + (upper << (upperPower - power - 1));
            int baseScale = Math.Max(0, -power);
            numerator = power < 0 ? numerator * BigInteger.Pow(5, baseScale) : numerator << power;
            int scale = baseScale + 2048;
            numerator *= BigInteger.Pow(10, 2048);
            foreach (int delta in new[] { -1, 0, 1 })
            {
                ulong expected = delta < 0 ? lowerBits
                    : delta > 0 ? lowerBits + 1UL
                    : (lowerBits & 1UL) == 0 ? lowerBits : lowerBits + 1UL;
                string text = FixedDecimal(numerator + delta, scale);
                foreach (bool negative in new[] { false, true })
                {
                    foreach (bool repair in new[] { false, true })
                    {
                        yield return [negative ? "-" + text : text,
                            negative ? expected | (1UL << 63) : expected, repair];
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(RoundingCases))]
    public void LongDecimalsRoundAtExactBinaryMidpoints(string text, ulong expectedBits, bool repair)
    {
        (PdfParser parser, _) = Create(QpdfLimits.Default, repair);
        PdfReal value = Assert.IsType<PdfReal>(parser.ParseValue(new PdfTokenizer(Encoding.ASCII.GetBytes(text)), 0));
        Assert.Equal(text, value.Text);
        Assert.Equal(expectedBits, BitConverter.DoubleToUInt64Bits(value.Value));
    }

    [Fact]
    public void LongSignificantDigitsAgreeWithBclForVariedDecimals()
    {
        Random random = new(73429);
        for (int sample = 0; sample < 128; sample++)
        {
            int length = random.Next(1025, 8192);
            char[] digits = new char[length];
            for (int i = 0; i < digits.Length; i++)
            {
                digits[i] = (char)('0' + random.Next(10));
            }

            string text = new string(digits).Insert(random.Next(0, Math.Min(400, length)), ".");
            if (sample % 2 == 0) { text = "-" + text; }
            Assert.True(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double expected));
            (PdfParser parser, _) = Create(QpdfLimits.Default, false);
            PdfReal value = Assert.IsType<PdfReal>(parser.ParseValue(new PdfTokenizer(Encoding.ASCII.GetBytes(text)), 0));
            Assert.Equal(BitConverter.DoubleToUInt64Bits(expected), BitConverter.DoubleToUInt64Bits(value.Value));
        }
    }

    [Fact]
    public void RetainedTextDoesNotNeedAnotherFullSizeCharacterArray()
    {
        (PdfParser warmup, _) = Create(QpdfLimits.Default, false);
        warmup.ParseValue(new PdfTokenizer("0.5"u8.ToArray()), 0);
        byte[] bytes = Encoding.ASCII.GetBytes("0." + new string('1', 262144));
        (PdfParser parser, _) = Create(QpdfLimits.Default, false);
        PdfTokenizer tokenizer = new(bytes);
        long before = GC.GetAllocatedBytesForCurrentThread();
        PdfReal value = Assert.IsType<PdfReal>(parser.ParseValue(tokenizer, 0));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(bytes.Length, value.Text.Length);
        Assert.True(allocated < (bytes.Length * 2L) + 4096L, $"Real parsing allocated {allocated} bytes for {bytes.Length} source bytes.");
    }

    [Fact]
    public void RealTextAdmissionPrecedesMaterialization()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("0." + new string('0', 65536) + "1");
        (PdfParser parser, _) = Create(QpdfLimits.Default with { MaxMemoryBytes = 100000L }, true);
        PdfTokenizer tokenizer = new(bytes);
        long before = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => parser.ParseObjectValue(tokenizer, 0));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), error.LimitName);
        Assert.True(allocated < bytes.Length, $"Rejected real allocated {allocated} bytes before returning.");
    }

    [Fact]
    public void ExhaustedConversionWorkReleasesWorkingAndTextAdmissions()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("0." + new string('0', 65536) + "1");
        (PdfParser parser, ResourceGovernor governor) = Create(QpdfLimits.Default with { MaxWork = 20L }, true);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => parser.ParseObjectValue(new PdfTokenizer(bytes), 0));
        Assert.Equal(nameof(QpdfLimits.MaxWork), error.LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void CopyingNumericTextIsGovernedAfterConversion()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("0." + new string('0', 65536) + "1");
        (PdfParser parser, ResourceGovernor governor) = Create(QpdfLimits.Default with { MaxWork = 40L }, true);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => parser.ParseObjectValue(new PdfTokenizer(bytes), 0));
        Assert.Equal(nameof(QpdfLimits.MaxWork), error.LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
