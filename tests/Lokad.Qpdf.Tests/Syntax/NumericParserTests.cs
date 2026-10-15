using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Syntax;

public sealed class NumericParserTests
{
    [Fact]
    public async Task PublicReaderKeepsLargeFollowingStringsWithinRepresentativeWorkLimits()
    {
        const int length = 1024 * 1024;
        byte[] FileFor(string prefix)
        {
            List<(int, string, byte[]?)> objects = CheckTests.SinglePage("<< /Length 4 >>", "q Q\n"u8.ToArray());
            objects.Add((6, "[ " + prefix + "(" + new string('A', length) + ") ]", null));
            return CheckTests.Assemble(objects, 1);
        }
        InMemoryQpdfHost controlHost = new();
        controlHost.AddInput(QpdfPath.From("control.pdf"), FileFor(string.Empty), QpdfInputOwnership.Copy);
        long controlWork;
        await using (QpdfDocument control = await QpdfDocument.OpenAsync(controlHost, QpdfPath.From("control.pdf"), CancellationToken.None))
        {
            Assert.IsType<PdfArray>(control.GetObject(6));
            controlWork = control.Counters.TotalWork;
        }
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("in.pdf"), FileFor("1 2 "), QpdfInputOwnership.Copy);
        // Leave a coarse allowance for the two values and public cache ownership.
        // Repeatedly scanning the megabyte string still exceeds this allowance.
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = controlWork + 256L };
        PdfByteString kept;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("in.pdf"), limits, CancellationToken.None))
        {
            PdfArray values = Assert.IsType<PdfArray>(document.GetObject(6));
            Assert.Equal(3, values.Count);
            Assert.Equal(1L, Assert.IsType<PdfInteger>(values[0]).Value);
            Assert.Equal(2L, Assert.IsType<PdfInteger>(values[1]).Value);
            kept = Assert.IsType<PdfByteString>(values[2]);
        }
        Assert.Equal(Enumerable.Repeat((byte)'A', length).ToArray(), kept.Value);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, controlHost.OpenLeaseCount);
        QpdfLimits insufficient = limits with { MaxWork = controlWork / 2L };
        await Assert.ThrowsAsync<QpdfLimitException>(async () =>
        {
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("in.pdf"), insufficient, CancellationToken.None);
            document.GetObject(6);
        });
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("1", false)]
    [InlineData("1 2", true)]
    public void IntegerLookaheadDoesNotConsumeTheFollowingLargeStringWorkBudget(string integers, bool repair)
    {
        const int length = 65536;
        byte[] input = Encoding.ASCII.GetBytes("[ " + integers + " (" + new string('A', length) + ") ]");
        (PdfParser parser, ResourceGovernor governor, DiagnosticBag bag) = Create(QpdfLimits.Default with { MaxWork = 64L }, repair);
        // Complete jobs hold private parsed values in their existing scope.
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfArray value = Assert.IsType<PdfArray>(parser.ParseObjectValue(new PdfTokenizer(input), 0, memory));
        int count = integers == "1" ? 1 : 2;
        Assert.Equal(count + 1, value.Count);
        for (int index = 0; index < count; index++) { Assert.Equal(index + 1L, Assert.IsType<PdfInteger>(value[index]).Value); }
        Assert.Equal(Enumerable.Repeat((byte)'A', length).ToArray(), Assert.IsType<PdfByteString>(value[count]).Value);
        Assert.Empty(bag.Entries);
    }

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
        using (var display = CanonicalDisplay.Render(value, limits, governor)) { Assert.Equal(expected, display.Value); }
        Assert.Equal(warning, bag.Entries.Any());
        Assert.All(bag.Entries, d => Assert.Equal(QpdfDiagnosticCode.RepairedObject, d.Code));
    }

    [Theory]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("+0001", 1L)]
    [InlineData("-0000", 0L)]
    [InlineData("0", 0L)]
    [InlineData("9", 9L)]
    [InlineData("00", 0L)]
    [InlineData("-0", 0L)]
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

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("7", 7L)]
    [InlineData("9", 9L)]
    [InlineData("10", 10L)]
    [InlineData("17", 17L)]
    [InlineData("200", 200L)]
    [InlineData("255", 255L)]
    public void CanonicalSmallIntegersShareImmutableValuesAcrossParsers(string text, long expected)
    {
        PdfInteger first = ParseInteger(text);
        PdfInteger second = ParseInteger(text);
        Assert.Same(first, second);
        Assert.Equal(text, first.Text);
        Assert.Equal(expected, first.Value);
        Assert.False(new PdfInteger(text, expected).IsSharedValue);
    }

    [Theory]
    [InlineData("+7", 7L)]
    [InlineData("007", 7L)]
    [InlineData("-0", 0L)]
    [InlineData("+17", 17L)]
    [InlineData("017", 17L)]
    [InlineData("-17", -17L)]
    [InlineData("256", 256L)]
    public void OtherIntegerSpellingsKeepTheirOriginalText(string text, long expected)
    {
        PdfInteger first = ParseInteger(text);
        Assert.NotSame(first, ParseInteger(text));
        Assert.Equal(text, first.Text);
        Assert.Equal(expected, first.Value);
    }

    private static PdfInteger ParseInteger(string text)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
        return Assert.IsType<PdfInteger>(parser.ParseValue(new PdfTokenizer(System.Text.Encoding.ASCII.GetBytes(text)), 0));
    }
}
