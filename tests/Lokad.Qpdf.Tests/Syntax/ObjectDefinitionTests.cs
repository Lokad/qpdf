using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Syntax;

public sealed class ObjectDefinitionTests
{
    public static IEnumerable<object[]> References()
    {
        foreach (string body in new[] { "6 0 R", "5 0 R" })
        foreach (bool member in new[] { false, true })
        foreach (bool repair in new[] { false, true }) { yield return [body, member, repair]; }
    }

    [Theory]
    [MemberData(nameof(References))]
    public void BareReferencesRejectStrictlyOrRepairToNullWithAWarning(string body, bool member, bool repair)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        PdfParser parser = new(QpdfLimits.Default, governor, diagnostics, null) { TolerateObjectErrors = repair, AllowStreams = !member };
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            PdfTokenizer tokenizer = new(Encoding.ASCII.GetBytes(member ? body : "5 0 obj " + body + " endobj"));
            Func<QpdfObject> parse = () => member ? parser.ParseObjectValue(tokenizer, 0, memory) : parser.ParseIndirectObject(tokenizer, 0, memory).Value;
            if (repair)
            {
                Assert.Same(PdfNull.Instance, parse()); Assert.True(parser.ObjectAborted); Assert.Equal(1L, parser.ToleratedErrors);
                Assert.Equal(0L, memory.ReservedBytes);
                QpdfDiagnostic diagnostic = Assert.Single(diagnostics.Entries);
                Assert.Equal(QpdfDiagnosticCode.RepairedObject, diagnostic.Code);
                Assert.Contains("reference", diagnostic.Message, StringComparison.Ordinal);
            }
            else
            {
                QpdfParseException error = Assert.Throws<QpdfParseException>(parse);
                Assert.Contains("indirect reference", error.Message, StringComparison.Ordinal); Assert.Empty(diagnostics.Entries);
            }
        }
        if (!repair) { Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); }
    }

    public static IEnumerable<object[]> Values()
    {
        (string Body, Type Kind)[] values = [("42", typeof(PdfInteger)), ("null", typeof(PdfNull)), ("/Name", typeof(PdfName)),
            ("(Keep)", typeof(PdfByteString)), ("[6 0 R]", typeof(PdfArray)), ("<< /A 6 0 R >>", typeof(PdfDictionary))];
        foreach (var value in values)
        foreach (bool member in new[] { false, true })
        foreach (bool repair in new[] { false, true }) { yield return [value.Body, value.Kind, member, repair]; }
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void ScalarsAndReferencesInsideContainersRemainLegal(string body, Type kind, bool member, bool repair)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        PdfParser parser = new(QpdfLimits.Default, governor, diagnostics, null) { TolerateObjectErrors = repair, AllowStreams = !member };
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            PdfTokenizer tokenizer = new(Encoding.ASCII.GetBytes(member ? body : "5 0 obj " + body + " endobj"));
            QpdfObject value = member ? parser.ParseObjectValue(tokenizer, 0, memory) : parser.ParseIndirectObject(tokenizer, 0, memory).Value;
            Assert.Equal(kind, value.GetType()); Assert.False(parser.ObjectAborted); Assert.Empty(diagnostics.Entries);
            if (value is PdfArray array) { Assert.Equal(6, Assert.IsType<PdfReference>(Assert.Single(array.Items)).ObjectNumber); }
            if (value is PdfDictionary dictionary)
            { Assert.True(dictionary.TryGet(PdfName.FromAscii("A"), out QpdfObject nested)); Assert.Equal(6, Assert.IsType<PdfReference>(nested).ObjectNumber); }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void TheOrdinaryValueParserStillAcceptsAnIndirectReference()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfReference value = Assert.IsType<PdfReference>(parser.ParseValue(new PdfTokenizer("6 0 R"u8.ToArray()), 0, memory));
        Assert.Equal(6, value.ObjectNumber);
    }

    [Theory]
    [InlineData("refuse")]
    [InlineData("caller")]
    [InlineData("governor")]
    public void DefinitionFailureRespectsAdmissionAndBothCancellationRoutes(string failure)
    {
        using CancellationTokenSource caller = new(), operation = new();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = failure == "refuse" ? 1L : 4096L };
        ResourceGovernor governor = new(limits, operation.Token);
        PdfParser parser = new(limits, governor, new(governor), null, caller.Token) { TolerateObjectErrors = true };
        Action parse = () =>
        {
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            if (failure == "caller") { caller.Cancel(); }
            if (failure == "governor") { operation.Cancel(); }
            _ = parser.ParseObjectValue(new PdfTokenizer("6 0 R"u8.ToArray()), 0, memory);
        };
        if (failure == "refuse") { Assert.Throws<QpdfLimitException>(parse); }
        else { Assert.ThrowsAny<OperationCanceledException>(parse); }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
