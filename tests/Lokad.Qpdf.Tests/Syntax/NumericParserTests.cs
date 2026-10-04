using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Syntax;

public sealed class NumericParserTests
{
    public static IEnumerable<object[]> OracleCases()
    {
        // Expected values come from authored inputs checked against qpdf 12.4.2.
        // Broken-xref trial parsing has the same numeric conversion outcomes.
        (string Body, string Kept, bool Warning, bool Budgeted)[] cases =
        [
            ("[ -9223372036854775808 2 ]", "[ -9223372036854775808 2 ]", false, false),
            ("[ 9223372036854775807 2 ]", "[ 9223372036854775807 2 ]", false, false),
            ("[ +001 -00 0001 ]", "[ 1 0 1 ]", false, false),
            ("[ " + new string('0', 2048) + "1 2 ]", "[ 1 2 ]", false, false),
            ("[ 9223372036854775808 2 ]", "null", true, false),
            ("[ -9223372036854775809 2 ]", "null", true, false),
            ("9223372036854775808", "null", true, false),
            ("[ " + new string('9', 2048) + " 2 ]", "null", true, false),
            ("[ 2147483648 0 R 2 ]", "null", true, false),
            ("[ -2147483649 0 R 2 ]", "null", true, false),
            ("[ 1 2147483648 R 2 ]", "null", true, false),
            ("[ 1 9223372036854775808 R 2 ]", "null", true, false),
            ("[ 1 65536 R 2 ]", "[ null 2 ]", true, true),
            ("[ 1 65535 R 2 ]", "[ null 2 ]", true, true),
            ("[ 1 -1 R 2 ]", "[ null 2 ]", true, true),
            ("[ -1 0 R 2 ]", "[ null 2 ]", true, true),
            ("[ 0 0 R 2 ]", "[ null 2 ]", true, true),
        ];
        foreach ((string body, string kept, bool warning, bool budgeted) in cases)
        {
            foreach (long budget in new long[] { 0L, 1L, 2L, 3L, 6L, 15L })
            {
                foreach (bool sanity in new[] { false, true })
                {
                    yield return [body, budget, sanity, budgeted && budget == 1L ? "null" : kept, warning];
                }
            }
        }
    }

    private static (PdfParser Parser, ResourceGovernor Governor, DiagnosticBag Bag) Create(QpdfLimits limits, bool repair)
    {
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        return (new PdfParser(limits, governor, bag, null) { TolerateObjectErrors = repair }, governor, bag);
    }

    [Theory]
    [MemberData(nameof(OracleCases))]
    public void IntegerAndReferenceRepairsMatchPinnedValues(string body, long budget, bool sanity, string expected, bool warning)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxParserErrors = budget };
        (PdfParser parser, ResourceGovernor governor, DiagnosticBag bag) = Create(limits, true);
        parser.SanityChecks = sanity;
        (_, _, QpdfObject value) = parser.ParseIndirectObject(new PdfTokenizer(Encoding.ASCII.GetBytes("5 0 obj " + body + " endobj")), 0);
        Assert.Equal(expected, CanonicalDisplay.Render(value, limits, governor));
        Assert.Equal(warning, bag.Entries.Any());
        Assert.All(bag.Entries, d => Assert.Equal(QpdfDiagnosticCode.RepairedObject, d.Code));
    }

    [Theory]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("+0001", 1L)]
    [InlineData("-0000", 0L)]
    public void StrictIntegersPreserveTheirTextAndFullSignedRange(string text, long expected)
    {
        (PdfParser parser, _, DiagnosticBag bag) = Create(QpdfLimits.Default, false);
        PdfInteger value = Assert.IsType<PdfInteger>(parser.ParseValue(new PdfTokenizer(Encoding.ASCII.GetBytes(text)), 0));
        Assert.Equal(text, value.Text);
        Assert.Equal(expected, value.Value);
        Assert.Empty(bag.Entries);
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("[ 1 65536 R ]")]
    public void StrictNumericDamageStillFails(string body)
    {
        (PdfParser parser, _, _) = Create(QpdfLimits.Default, false);
        Assert.Throws<QpdfParseException>(() => parser.ParseValue(new PdfTokenizer(Encoding.ASCII.GetBytes(body)), 0));
    }

    [Fact]
    public void LongIntegerTextIsAdmittedBeforeAllocation()
    {
        byte[] data = Encoding.ASCII.GetBytes(new string('0', 65536) + "1");
        (PdfParser parser, _, _) = Create(QpdfLimits.Default with { MaxMemoryBytes = 100000L }, true);
        PdfTokenizer tokenizer = new(data);
        long before = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => parser.ParseObjectValue(tokenizer, 0));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), error.LimitName);
        Assert.True(allocated < data.Length, $"Rejected integer allocated {allocated} bytes before returning.");
    }

    [Fact]
    public void IntegerConversionChargesWorkAfterTokenScanning()
    {
        byte[] data = Encoding.ASCII.GetBytes(new string('0', 65536) + "1");
        (PdfParser parser, _, _) = Create(QpdfLimits.Default with { MaxWork = 20L }, true);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => parser.ParseObjectValue(new PdfTokenizer(data), 0));
        Assert.Equal(nameof(QpdfLimits.MaxWork), error.LimitName);
    }

    [Fact]
    public void NumericRepairDoesNotSwallowDiagnosticBudgetFailure()
    {
        (PdfParser parser, _, _) = Create(QpdfLimits.Default with { MaxDiagnostics = 1L }, true);
        parser.ParseObjectValue(new PdfTokenizer("9223372036854775808"u8.ToArray()), 0);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => parser.ParseObjectValue(new PdfTokenizer("9223372036854775808"u8.ToArray()), 0));
        Assert.Equal(nameof(QpdfLimits.MaxDiagnostics), error.LimitName);
    }
}
