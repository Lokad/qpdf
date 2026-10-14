using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Syntax;

public sealed class LexicalParserTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(17)]
    public void ArrayValuesPreserveOrderAndDefensiveCopies(int count)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new DiagnosticBag(governor), null);
        string text = "[" + string.Join(" ", Enumerable.Range(1, count)) + "]";
        PdfArray value = Assert.IsType<PdfArray>(parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(text)), 0));
        Assert.Equal(count, value.Count);
        QpdfObject[] publicCopy = value.Items;
        Array.Fill(publicCopy, PdfNull.Instance);
        for (int index = 0; index < count; index++)
        { Assert.Equal(index + 1L, Assert.IsType<PdfInteger>(value[index]).Value); }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void ArrayLimitRejectsBeforeParsingAnotherValueAndReleasesStorage(int count)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxContainerSize = count };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null);
        string text = "[" + string.Join(" ", Enumerable.Range(1, count)) + " [@]]";
        QpdfParseException error = Assert.Throws<QpdfParseException>(() =>
            parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(text)), 0));
        Assert.Contains("top-level items", error.Message, StringComparison.Ordinal);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    // Independently authored inputs and exact values checked with qpdf 12.4.2.
    // No reference tool or checkout is required by these tests.
    internal static IEnumerable<(string Body, string Kept, int Errors, bool Warns)> Cases(bool sanity)
    {
        yield return ("[ 1{2 3}4 ]", "[ 1 null 2 3 null 4 ]", 2, true);
        yield return ("[ true{false}null ]", "[ true null false null null ]", 2, true);
        yield return ("[ /A{2}/B ]", "[ /A null 2 null /B ]", 2, true);
        yield return ("[ /A#00B 2 ]", "[ null 2 ]", 1, true);
        yield return ("/A#00B", "null", 0, true);
        yield return ("<< /A#00B 1 /C 2 >>", sanity ? "<< /C 2 >>" : "<< /C 2 /QPDFFake2 1 >>", 1, true);
        yield return ("<< 5 /A >>", sanity ? "<< >>" : "<< /QPDFFake1 5 >>", 0, true);
        yield return ("<< 5 /QPDFFake1 3 /A 1 >>", sanity ? "<< /A 1 /QPDFFake1 3 >>" : "<< /A 1 /QPDFFake1 3 /QPDFFake2 5 >>", 0, true);
        yield return ("[ /A#2300B 2 ]", "[ /A#2300B 2 ]", 0, false);
        yield return ("[ /A#7BB#7DC 2 ]", "[ /A#7bB#7dC 2 ]", 0, false);
        yield return ("[ <ABCD> <ff00> ]", "[ <abcd> <ff00> ]", 0, false);
        yield return ("[ (a\r\nb\rc\nd) ]", "[ (a\\nb\\nc\\nd) ]", 0, false);
        yield return ("[ (a\n\rb) ]", "[ (a\\n\\nb) ]", 0, false);
        yield return ("[ (a\\\r\nb\\\rc\\\nd) ]", "[ (abcd) ]", 0, false);
        yield return ("[ (a\\rb\\n) ]", "[ (a\\rb\\n) ]", 0, false);
    }

    public static IEnumerable<object[]> OracleCases()
    {
        foreach (bool sanity in new[] { false, true })
        {
            foreach ((string body, string kept, int errors, bool warns) in Cases(sanity))
            {
                foreach (long budget in new long[] { 0L, 1L, 2L, 3L, 6L, 15L })
                {
                    string expected = budget != 0L && errors >= budget ? "null" : kept;
                    yield return [body, budget, sanity, expected, warns];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(OracleCases))]
    public void LexicalRepairsMatchPinnedValues(string body, long budget, bool sanity, string expected, bool warns)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxParserErrors = budget };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        PdfParser parser = new(limits, governor, diagnostics, null) { TolerateObjectErrors = true, SanityChecks = sanity };
        (_, _, QpdfObject value) = parser.ParseIndirectObject(new PdfTokenizer(Encoding.ASCII.GetBytes("5 0 obj " + body + " endobj")), 0);
        using (var display = CanonicalDisplay.Render(value, limits, governor)) { Assert.Equal(expected, display.Value); }
        Assert.Equal(warns, diagnostics.Entries.Count != 0);
    }

    [Theory]
    [InlineData("[ 1{2 3}4 ]")]
    [InlineData("[ true{false}null ]")]
    [InlineData("[ /A{2}/B ]")]
    [InlineData("[ /A#00B 2 ]")]
    [InlineData("/A#00B")]
    [InlineData("<< /A#00B 1 /C 2 >>")]
    public void StrictParsingRejectsLexicalDamage(string body)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new DiagnosticBag(governor), null);
        Assert.Throws<QpdfParseException>(() => parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(body)), 0));
    }

    [Theory]
    [InlineData("(a\r\nb\rc\nd)", "a\nb\nc\nd")]
    [InlineData("(a\\\r\nb\\\rc\\\nd)", "abcd")]
    [InlineData("(a\\rb\\n)", "a\rb\n")]
    [InlineData("(a\r\r\nb)", "a\n\nb")]
    [InlineData("(a\n\rb)", "a\n\nb")]
    public void StrictLiteralStringsPreserveDecodedNewlineBytes(string body, string expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new DiagnosticBag(governor), null);
        PdfByteString value = Assert.IsType<PdfByteString>(parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(body)), 0));
        Assert.Equal(Encoding.ASCII.GetBytes(expected), value.Value);
    }

    [Fact]
    public void EscapedHashIsNotDecodedTwiceIntoNul()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new DiagnosticBag(governor), null);
        PdfName value = Assert.IsType<PdfName>(parser.ParseObjectValue(new PdfTokenizer("/A#2300B"u8.ToArray()), 0));
        Assert.Equal("A#00B"u8.ToArray(), value.RawBytes);
    }

    [Fact]
    public void EscapedBracesRemainNameBytes()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new DiagnosticBag(governor), null);
        PdfName value = Assert.IsType<PdfName>(parser.ParseObjectValue(new PdfTokenizer("/A#7BB#7DC"u8.ToArray()), 0));
        Assert.Equal("A{B}C"u8.ToArray(), value.RawBytes);
    }

    [Fact]
    public void CanonicalHexSpellingPreservesStringBytes()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new DiagnosticBag(governor), null);
        PdfArray value = Assert.IsType<PdfArray>(parser.ParseObjectValue(new PdfTokenizer("[ <ABCD> <ff00> ]"u8.ToArray()), 0));
        Assert.Equal(new byte[] { 0xAB, 0xCD }, Assert.IsType<PdfByteString>(value[0]).Value);
        Assert.Equal(new byte[] { 0xFF, 0x00 }, Assert.IsType<PdfByteString>(value[1]).Value);
        using (var display = CanonicalDisplay.Render(value, QpdfLimits.Default, governor)) { Assert.Equal("[ <abcd> <ff00> ]", display.Value); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void UnescapingChargesWorkAfterTokenScanningAndReleasesAdmission(bool name, bool escapes)
    {
        string text = name
            ? escapes ? "/A" + string.Concat(Enumerable.Repeat("#41", 21846)) : "/" + new string('A', 65536)
            : escapes ? "(a" + string.Concat(Enumerable.Repeat("\\n", 32768)) + ")" : "(" + new string('a', 65536) + ")";
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 20L };
        ResourceGovernor scanGovernor = new(limits, CancellationToken.None);
        Assert.True(new PdfTokenizer(bytes).TryReadToken(out _, false, scanGovernor));
        ResourceGovernor governor = new(limits, CancellationToken.None);
        PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null) { TolerateObjectErrors = true };
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => parser.ParseObjectValue(new PdfTokenizer(bytes), 0));
        Assert.Equal(nameof(QpdfLimits.MaxWork), error.LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void NulNameRepairDoesNotSwallowDiagnosticBudgetFailure()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnostics = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null) { TolerateObjectErrors = true };
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => parser.ParseObjectValue(new PdfTokenizer("[ /A#00B /C#00D ]"u8.ToArray()), 0));
        Assert.Equal(nameof(QpdfLimits.MaxDiagnostics), error.LimitName);
    }
}
