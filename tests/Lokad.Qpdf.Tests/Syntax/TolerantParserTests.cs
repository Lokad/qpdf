using System.Globalization;
using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Syntax;

public sealed class TolerantParserTests
{
    // Authored cases and expected values checked against pinned qpdf 12.4.2.
    // Ordinary tests use no reference checkout or executable.
    public static IEnumerable<object[]> OracleCases()
    {
        (string Body, string Kept, int Errors, bool AlwaysNull)[] cases =
        [
            ("[ 1 @ 2 ]", "[ 1 (@) 2 ]", 1, false),
            ("[ @ @ @ @ ]", "[ (@) (@) (@) (@) ]", 4, false),
            ("<< 5 /A >>", "<< /QPDFFake1 5 >>", 0, false),
            ("<< /A >>", "<< >>", 0, false),
            ("<< /A 1 /A 2 >>", "<< /A 2 >>", 1, false),
            ("[ @ @ @ @ @ @ ]", "null", 6, true),
            ("[ @ 0 1 2 3 4 @ 0 1 2 3 4 @ 0 1 2 3 4 @ 0 1 2 3 4 @ 0 1 2 3 4 @ 0 1 2 3 4 ]",
                "[ (@) 0 1 2 3 4 (@) 0 1 2 3 4 (@) 0 1 2 3 4 (@) 0 1 2 3 4 (@) 0 1 2 3 4 (@) 0 1 2 3 4 ]", 6, false),
            ("[ 1 >> 2 ]", "[ 1 null 2 ]", 1, false),
            ("<< /A 1", "null", 0, true),
            ("<< 5 /QPDFFake1 3 /A 1 >>", "<< /A 1 /QPDFFake1 3 /QPDFFake2 5 >>", 0, false),
            ("@", "(@)", 0, false),
            ("[ 1 endobj 2 ]", "[ 1 (endobj) 2 ]", 1, false),
            ("[ 1x 2 ]", "[ (1x) 2 ]", 1, false),
            ("[ <ABCG> 1 ]", "[ null null 1 ]", 2, false),
            ("[ { } ]", "[ null null ]", 2, false),
            ("[ 1 > 2 ]", "[ 1 null 2 ]", 1, false),
        ];
        foreach ((string body, string kept, int errors, bool alwaysNull) in cases)
        {
            foreach (long budget in new long[] { 0L, 1L, 2L, 3L, 6L, 15L })
            {
                string expected = alwaysNull || (budget != 0L && errors >= budget) ? "null" : kept;
                yield return [body, budget, expected];
            }
        }
    }

    private static (PdfParser Parser, DiagnosticBag Bag, ResourceGovernor Governor) Create(QpdfLimits limits)
    {
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        return (new PdfParser(limits, governor, bag, null) { TolerateObjectErrors = true }, bag, governor);
    }

    [Theory]
    [MemberData(nameof(OracleCases))]
    public void RepairsMatchPinnedObjectValues(string body, long budget, string expected)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxParserErrors = budget };
        (PdfParser parser, DiagnosticBag bag, ResourceGovernor governor) = Create(limits);
        (int number, int generation, QpdfObject value) = parser.ParseIndirectObject(
            new PdfTokenizer(Encoding.ASCII.GetBytes("5 0 obj " + body + " endobj")), 0);
        Assert.Equal(5, number);
        Assert.Equal(0, generation);
        Assert.Equal(expected, CanonicalDisplay.Render(value, limits, governor));
        Assert.NotEmpty(bag.Entries);
        Assert.All(bag.Entries, d => Assert.Equal(QpdfDiagnosticSeverity.Warning, d.Severity));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void FourGoodTokensSeparateErrorClusters(int gap)
    {
        (PdfParser parser, _, _) = Create(QpdfLimits.Default with { MaxParserErrors = 0L });
        string group = " @ " + string.Join(" ", Enumerable.Range(0, gap));
        string body = "[" + string.Concat(Enumerable.Repeat(group, 6)) + " ]";
        (_, _, QpdfObject value) = parser.ParseIndirectObject(new PdfTokenizer(Encoding.ASCII.GetBytes("5 0 obj " + body + " endobj")), 0);
        if (gap < 4)
        {
            Assert.IsType<PdfNull>(value);
        }
        else
        {
            Assert.Equal(6 * (gap + 1), Assert.IsType<PdfArray>(value).Count);
        }
    }

    [Fact]
    public void ErrorStateResetsForEachIndirectObject()
    {
        (PdfParser parser, _, _) = Create(QpdfLimits.Default with { MaxParserErrors = 2L });
        for (int i = 1; i <= 20; i++)
        {
            PdfTokenizer tokenizer = new(Encoding.ASCII.GetBytes($"{i} 0 obj [ @ {i} ] endobj"));
            (int number, _, QpdfObject value) = parser.ParseIndirectObject(tokenizer, 0);
            Assert.Equal(i, number);
            Assert.Equal(2, Assert.IsType<PdfArray>(value).Count);
            Assert.Equal(1L, parser.ToleratedErrors);
        }
    }

    [Fact]
    public void MalformedNameEscapeIsRetainedWithoutConsumingErrorBudget()
    {
        (PdfParser parser, _, _) = Create(QpdfLimits.Default with { MaxParserErrors = 1L });
        (_, _, QpdfObject value) = parser.ParseIndirectObject(new PdfTokenizer("5 0 obj [ /A#2G 1 ] endobj"u8.ToArray()), 0);
        Assert.Equal("A#2G"u8.ToArray(), Assert.IsType<PdfName>(Assert.IsType<PdfArray>(value)[0]).RawBytes);
        Assert.Equal(0L, parser.ToleratedErrors);
    }

    [Fact]
    public void MissingEndobjDoesNotConsumeSemanticErrorBudgetOrNextHeader()
    {
        (PdfParser parser, DiagnosticBag bag, _) = Create(QpdfLimits.Default with { MaxParserErrors = 1L });
        PdfTokenizer tokenizer = new("1 0 obj << /A 1 >> 2 0 obj << /B 2 >> endobj"u8.ToArray());
        (_, _, QpdfObject first) = parser.ParseIndirectObject(tokenizer, 0);
        Assert.IsType<PdfDictionary>(first);
        Assert.Equal(0L, parser.ToleratedErrors);
        (int next, _, QpdfObject second) = parser.ParseIndirectObject(tokenizer, 0);
        Assert.Equal(2, next);
        Assert.IsType<PdfDictionary>(second);
        Assert.Single(bag.Entries);
    }

    [Fact]
    public void LongDamagedWordsRemainSubjectToWorkBudget()
    {
        (PdfParser parser, _, _) = Create(QpdfLimits.Default with { MaxParserErrors = 0L, MaxWork = 1L });
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => parser.ParseObjectValue(
            new PdfTokenizer(Encoding.ASCII.GetBytes(new string('@', 65536))), 0));
        Assert.Equal(nameof(QpdfLimits.MaxWork), error.LimitName);
    }

    [Fact]
    public void DiagnosticBudgetFailuresPropagateDuringRepair()
    {
        (PdfParser parser, _, _) = Create(QpdfLimits.Default with { MaxParserErrors = 1L, MaxDiagnostics = 1L });
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => parser.ParseIndirectObject(
            new PdfTokenizer("5 0 obj [ @ ] endobj"u8.ToArray()), 0));
        Assert.Equal(nameof(QpdfLimits.MaxDiagnostics), error.LimitName);
    }

    [Fact]
    public void CompressedMembersHaveIndependentBudgetsAndReadableValues()
    {
        string[] members = Enumerable.Range(6, 6).Select(n => $"[ @ {n} ]").ToArray();
        string payload = string.Join(" ", members);
        StringBuilder header = new();
        int offset = 0;
        for (int i = 0; i < members.Length; i++)
        {
            header.Append((i + 6).ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(offset.ToString(CultureInfo.InvariantCulture)).Append(' ');
            offset += members[i].Length + 1;
        }

        string decoded = header + payload;
        byte[] data = Encoding.ASCII.GetBytes($"5 0 obj << /Type /ObjStm /N 6 /First {header.Length} /Length {decoded.Length} >>\nstream\n{decoded}\nendstream\nendobj");
        QpdfLimits limits = QpdfLimits.Default with { MaxParserErrors = 2L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        Dictionary<int, XrefEntry> entries = new() { [5] = XrefEntry.Uncompressed(0L, 0) };
        for (int i = 0; i < members.Length; i++)
        {
            entries[i + 6] = XrefEntry.Compressed(5, i);
        }

        PdfDictionary trailer = new(new Dictionary<PdfName, QpdfObject>());
        XRefTable table = new(0L, entries, trailer, [trailer]);
        DocumentEngine engine = new(data, table, limits, governor, new DiagnosticBag(governor), QpdfRecoveryMode.BoundedRepair);
        for (int i = 0; i < members.Length; i++)
        {
            PdfArray value = Assert.IsType<PdfArray>(engine.Load(i + 6));
            Assert.Equal("@"u8.ToArray(), Assert.IsType<PdfByteString>(value[0]).Value);
            Assert.Equal(i + 6L, Assert.IsType<PdfInteger>(value[1]).Value);
            Assert.Same(value, engine.Load(i + 6));
        }
    }

    [Fact]
    public void StrictParserRetainsItsDamageFailure()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new DiagnosticBag(governor), null);
        Assert.Throws<QpdfParseException>(() => parser.ParseIndirectObject(new PdfTokenizer("5 0 obj [ 1 @ 2 ] endobj"u8.ToArray()), 0));
    }

    [Fact]
    public void RepairedWordPrivateCopyIsAdmittedBeforeMaterialization()
    {
        byte[] data = Encoding.ASCII.GetBytes(new string('@', 65536));
        (PdfParser parser, _, _) = Create(QpdfLimits.Default with { MaxMemoryBytes = 100000L });
        PdfTokenizer tokenizer = new(data);
        long before = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => parser.ParseObjectValue(tokenizer, 0));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), error.LimitName);
        Assert.True(allocated < data.Length, $"Rejected word allocated {allocated} bytes before returning.");
    }

}
