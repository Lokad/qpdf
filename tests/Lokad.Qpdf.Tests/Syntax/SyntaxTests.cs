using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Syntax;

internal sealed class MapResolver : IIndirectResolver
{
    private readonly Dictionary<int, QpdfObject> _objects = new();

    internal void Add(int number, QpdfObject value)
    {
        _objects[number] = value;
    }

    public bool TryResolve(PdfReference reference, out QpdfObject? value)
    {
        return _objects.TryGetValue(reference.ObjectNumber, out value);
    }
}

public sealed class TokenizerTests
{
    private static PdfTokenizer Tokenizer(string text)
    {
        return new PdfTokenizer(Encoding.ASCII.GetBytes(text));
    }

    private static string SliceText(PdfToken token)
    {
        return Encoding.ASCII.GetString(token.Data.ToArray());
    }

    [Fact]
    public void SkipsWhitespaceAndComments()
    {
        PdfTokenizer tokenizer = Tokenizer("%PDF-1.4 comment\r\n  true % trailing");
        Assert.True(tokenizer.TryReadToken(out PdfToken token));
        Assert.Equal(PdfTokenKind.True, token.Kind);
        Assert.False(tokenizer.TryReadToken(out PdfToken _));
    }

    [Fact]
    public void ReadsKeywords()
    {
        PdfTokenizer tokenizer = Tokenizer("null true false R obj endobj stream endstream xref trailer startxref");
        PdfTokenKind[] expected =
        [
            PdfTokenKind.Null, PdfTokenKind.True, PdfTokenKind.False, PdfTokenKind.Reference,
            PdfTokenKind.Object, PdfTokenKind.EndObject, PdfTokenKind.Stream, PdfTokenKind.EndStream,
            PdfTokenKind.XRef, PdfTokenKind.Trailer, PdfTokenKind.StartXRef,
        ];
        foreach (PdfTokenKind kind in expected)
        {
            Assert.True(tokenizer.TryReadToken(out PdfToken token));
            Assert.Equal(kind, token.Kind);
        }
    }

    [Fact]
    public void ReadsNumbersNamesAndDelimiters()
    {
        PdfTokenizer tokenizer = Tokenizer("<< /Type /Page 12 -3 +4.5 >> [ ]");
        Assert.True(tokenizer.TryReadToken(out PdfToken open));
        Assert.Equal(PdfTokenKind.DictionaryOpen, open.Kind);
        Assert.True(tokenizer.TryReadToken(out PdfToken name));
        Assert.Equal(PdfTokenKind.Name, name.Kind);
        Assert.Equal("Type", SliceText(name));
        Assert.True(tokenizer.TryReadToken(out PdfToken page));
        Assert.Equal(PdfTokenKind.Name, page.Kind);
        Assert.Equal("Page", SliceText(page));
        Assert.True(tokenizer.TryReadToken(out PdfToken integer));
        Assert.Equal(PdfTokenKind.Integer, integer.Kind);
        Assert.Equal("12", SliceText(integer));
        Assert.True(tokenizer.TryReadToken(out PdfToken negative));
        Assert.Equal("-3", SliceText(negative));
        Assert.True(tokenizer.TryReadToken(out PdfToken real));
        Assert.Equal(PdfTokenKind.Real, real.Kind);
        Assert.Equal("+4.5", SliceText(real));
    }

    [Fact]
    public void RejectsMalformedTokens()
    {
        Assert.Throws<QpdfParseException>(() => Tokenizer(">").TryReadToken(out PdfToken _));
        Assert.Throws<QpdfParseException>(() => Tokenizer("qpdf").TryReadToken(out PdfToken _));
        Assert.Throws<QpdfParseException>(() => Tokenizer("- x").TryReadToken(out PdfToken _));
        Assert.Throws<QpdfParseException>(() => Tokenizer("(abc").TryReadToken(out PdfToken _));
        Assert.Throws<QpdfParseException>(() => Tokenizer("<AB").TryReadToken(out PdfToken _));
    }

    [Fact]
    public void RecordsAbsoluteOffsets()
    {
        PdfTokenizer tokenizer = Tokenizer("  true");
        Assert.True(tokenizer.TryReadToken(out PdfToken token));
        Assert.Equal(2L, token.Offset);
    }
}

public sealed class ParserTests
{
    private static (PdfParser Parser, DiagnosticBag Bag, ResourceGovernor Governor) CreateParser()
    {
        QpdfLimits limits = QpdfLimits.Default;
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        return (new PdfParser(limits, governor, bag, null), bag, governor);
    }

    private static QpdfObject Parse(string text)
    {
        (PdfParser parser, DiagnosticBag _, ResourceGovernor governor) = CreateParser();
        PdfTokenizer tokenizer = new(Encoding.ASCII.GetBytes(text));
        using ParserMemoryScope parsedMemory0 = ParserMemoryScope.Create(governor);
        var parsed = parser.ParseValue(tokenizer, 0, parsedMemory0);
        parsedMemory0.RetainGraph();
        return parsed;
    }

    [Fact]
    public void ParsesScalarsWithTextPreserved()
    {
        Assert.Equal(PdfNull.Instance, Parse("null"));
        Assert.Equal(PdfBoolean.True, Parse("true"));
        PdfInteger integer = Assert.IsType<PdfInteger>(Parse("+42"));
        Assert.Equal("+42", integer.Text);
        Assert.Equal(42L, integer.Value);
        PdfReal real = Assert.IsType<PdfReal>(Parse("-0.5"));
        Assert.Equal("-0.5", real.Text);
        Assert.Equal(-0.5, real.Value);
    }

    [Fact]
    public void ParsesReferencesButNotMereIntegers()
    {
        PdfReference reference = Assert.IsType<PdfReference>(Parse("12 0 R"));
        Assert.Equal(12, reference.ObjectNumber);
        Assert.Equal(0, reference.Generation);
        PdfArray pair = Assert.IsType<PdfArray>(Parse("[1 2]"));
        Assert.Equal(2, pair.Count);
        Assert.IsType<PdfInteger>(pair[0]);
    }

    [Fact]
    public void ParsesNamesStringsAndHex()
    {
        PdfName name = Assert.IsType<PdfName>(Parse("/A#20B"));
        Assert.Equal([65, 32, 66], name.RawBytes);
        PdfByteString literal = Assert.IsType<PdfByteString>(Parse("(a(b)c\\n)"));
        Assert.False(literal.Hex);
        Assert.Equal([97, 40, 98, 41, 99, 10], literal.Value);
        PdfByteString hex = Assert.IsType<PdfByteString>(Parse("<AB C>"));
        Assert.True(hex.Hex);
        Assert.Equal([0xAB, 0xC0], hex.Value);
        PdfByteString odd = Assert.IsType<PdfByteString>(Parse("<A>"));
        Assert.Equal([0xA0], odd.Value);
    }

    [Fact]
    public void RejectsInvalidHexAndNameEscapes()
    {
        Assert.Throws<QpdfParseException>(() => Parse("<ABCG>"));
        Assert.Throws<QpdfParseException>(() => Parse("/A#2G"));
        Assert.Throws<QpdfParseException>(() => Parse("/A#2"));
    }

    [Theory]
    [InlineData(16384L, true)]
    [InlineData(2048L, false)]
    public void WhitespaceHeavyHexAdmitsDecodedStorageAndPreservesItsMemoryLimit(long budget, bool fits)
    {
        byte[] input = Encoding.ASCII.GetBytes("<" + string.Concat(Enumerable.Repeat("4 \t1 \r\n", 4096)) + ">");
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = budget };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null);
        if (fits)
        {
            using ParserMemoryScope parsedMemory1 = ParserMemoryScope.Create(governor);
            PdfByteString parsed = Assert.IsType<PdfByteString>(parser.ParseValue(new PdfTokenizer(input), 0, parsedMemory1));
            parsedMemory1.RetainGraph(); parsedMemory1.Dispose();
            Assert.Equal(Enumerable.Repeat((byte)'A', 4096), parsed.Value);
            Array.Clear(input);
            Array.Clear(parsed.Value);
            Assert.Equal(Enumerable.Repeat((byte)'A', 4096), parsed.Value);
        }
        else
        {
            QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
            {
                using ParserMemoryScope parsedMemory2 = ParserMemoryScope.Create(governor);
                _ = parser.ParseValue(new PdfTokenizer(input), 0, parsedMemory2);
            });
            Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 0L, budget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscapeHeavyValuesFitDecodedStorageWithoutTrimmingOverlap(bool name)
    {
        string spelling = name ? "/" + string.Concat(Enumerable.Repeat("#41", 4096))
            : "(" + string.Concat(Enumerable.Repeat("\\101", 4096)) + ")";
        byte[] input = Encoding.ASCII.GetBytes(spelling);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 8192L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null);
        using ParserMemoryScope parsedMemory3 = ParserMemoryScope.Create(governor);
        QpdfObject parsed = parser.ParseValue(new PdfTokenizer(input), 0, parsedMemory3);
        parsedMemory3.RetainGraph(); parsedMemory3.Dispose();
        Array.Clear(input);
        byte[] decoded = name ? Assert.IsType<PdfName>(parsed).RawBytes : Assert.IsType<PdfByteString>(parsed).Value;
        Assert.Equal(Enumerable.Repeat((byte)'A', 4096), decoded);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 4096L, limits.MaxMemoryBytes);
    }

    [Fact]
    public void DuplicateKeysKeepLastWithWarning()
    {
        (PdfParser parser, DiagnosticBag bag, ResourceGovernor governor) = CreateParser();
        PdfTokenizer tokenizer = new(Encoding.ASCII.GetBytes("<</A 1 /A 2>>"));
        using ParserMemoryScope parsedMemory4 = ParserMemoryScope.Create(governor);
        PdfDictionary dictionary = Assert.IsType<PdfDictionary>(parser.ParseValue(tokenizer, 0, parsedMemory4));
        parsedMemory4.RetainGraph(); parsedMemory4.Dispose();
        Assert.True(dictionary.TryGet(new PdfName([(byte)'A']), out QpdfObject value));
        Assert.Equal(new PdfInteger("2", 2L), value);
        Assert.Single(bag.Entries);
        Assert.Equal(QpdfDiagnosticCode.DuplicateKey, bag.Entries[0].Code);
        Assert.Equal(QpdfDiagnosticSeverity.Warning, bag.Entries[0].Severity);
    }

    [Fact]
    public void ParsesStreamsWithDirectLength()
    {
        PdfStream stream = Assert.IsType<PdfStream>(Parse("<</Length 5>>\nstream\nhello\nendstream"));
        Assert.Equal([104, 101, 108, 108, 111], stream.Data);
    }

    [Fact]
    public void ParsedStreamSurvivesInputAndReturnedCopyMutation()
    {
        byte[] payload = Enumerable.Range(0, 32768).Select(index => (byte)index).ToArray();
        byte[] source = [.."<</Length 32768>>\nstream\n"u8.ToArray(), ..payload, .."\nendstream"u8.ToArray()];
        (PdfParser parser, DiagnosticBag _, ResourceGovernor governor) = CreateParser();
        using ParserMemoryScope parsedMemory5 = ParserMemoryScope.Create(governor);
        PdfStream stream = Assert.IsType<PdfStream>(parser.ParseValue(new PdfTokenizer(source), 0, parsedMemory5));
        parsedMemory5.RetainGraph(); parsedMemory5.Dispose();
        Array.Clear(source);
        byte[] returned = stream.Data;
        Array.Clear(returned);
        Assert.Equal(payload, stream.Data);
        Assert.Equal(32768, stream.RawMemory.Length);
    }

    [Fact]
    public void ParsedEscapedNameSurvivesInputAndReturnedCopyMutation()
    {
        byte[] source = "/A#20B"u8.ToArray();
        (PdfParser parser, DiagnosticBag _, ResourceGovernor governor) = CreateParser();
        using ParserMemoryScope parsedMemory6 = ParserMemoryScope.Create(governor);
        PdfName name = Assert.IsType<PdfName>(parser.ParseValue(new PdfTokenizer(source), 0, parsedMemory6));
        parsedMemory6.RetainGraph(); parsedMemory6.Dispose();
        Array.Clear(source);
        byte[] returned = name.RawBytes;
        Array.Clear(returned);
        Assert.Equal("A B"u8.ToArray(), name.RawBytes);
    }

    [Fact]
    public void ParsesStreamsWithIndirectLength()
    {
        QpdfLimits limits = QpdfLimits.Default;
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        MapResolver resolver = new();
        resolver.Add(7, new PdfInteger("5", 5L));
        PdfParser parser = new(limits, governor, bag, resolver);
        PdfTokenizer tokenizer = new(Encoding.ASCII.GetBytes("<</Length 7 0 R>>\nstream\nhello\nendstream"));
        using ParserMemoryScope parsedMemory7 = ParserMemoryScope.Create(governor);
        PdfStream stream = Assert.IsType<PdfStream>(parser.ParseValue(tokenizer, 0, parsedMemory7));
        parsedMemory7.RetainGraph(); parsedMemory7.Dispose();
        Assert.Equal([104, 101, 108, 108, 111], stream.Data);
    }

    [Fact]
    public void RejectsBadStreams()
    {
        Assert.Throws<QpdfParseException>(() => Parse("<</Length 5>>\nstream\nhi\nendstream"));
        Assert.Throws<QpdfParseException>(() => Parse("<</Length 9 0 R>>\nstream\nhello\nendstream"));
        Assert.Throws<QpdfParseException>(() => Parse("<</Length 5>>\nstream hello\nendstream"));
        Assert.Throws<QpdfParseException>(() => Parse("<</Length 5>>\nstream\nhello"));
        Assert.Throws<QpdfParseException>(() => Parse("<</Length -1>>\nstream\n\nendstream"));
        Assert.Throws<QpdfParseException>(() => Parse("<</Length /Foo>>\nstream\n\nendstream"));
    }

    [Fact]
    public void EnforcesNestingDepth()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = 2L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        PdfParser parser = new(limits, governor, bag, null);
        PdfTokenizer tokenizer = new(Encoding.ASCII.GetBytes("[[[1]]]"));
        Assert.Throws<QpdfParseException>(() =>
        {
            using ParserMemoryScope parsedMemory8 = ParserMemoryScope.Create(governor);
            _ = parser.ParseValue(tokenizer, 0, parsedMemory8);
        });
    }

    [Fact]
    public void RejectsOverflowingIntegers()
    {
        QpdfParseException ex = Assert.Throws<QpdfParseException>(() => Parse("9999999999999999999999"));
        Assert.NotNull(ex.ByteOffset);
    }

    [Fact]
    public void ParsesIndirectObjects()
    {
        (PdfParser parser, DiagnosticBag _, ResourceGovernor governor) = CreateParser();
        PdfTokenizer tokenizer = new(Encoding.ASCII.GetBytes("5 0 obj [1] endobj"));
        using ParserMemoryScope parsedMemory9 = ParserMemoryScope.Create(governor);
        (int number, int generation, QpdfObject value) = parser.ParseIndirectObject(tokenizer, 0, parsedMemory9);
        parsedMemory9.RetainGraph(); parsedMemory9.Dispose();
        Assert.Equal(5, number);
        Assert.Equal(0, generation);
        Assert.IsType<PdfArray>(value);
    }

    [Fact]
    public void RejectsMalformedIndirectObjects()
    {
        (PdfParser parser, DiagnosticBag _, ResourceGovernor governor) = CreateParser();
        Assert.Throws<QpdfParseException>(() =>
        {
            using ParserMemoryScope parsedMemory10 = ParserMemoryScope.Create(governor);
            _ = parser.ParseIndirectObject(new PdfTokenizer(Encoding.ASCII.GetBytes("5 0 obj [1]")), 0, parsedMemory10);
        });
        Assert.Throws<QpdfParseException>(() =>
        {
            using ParserMemoryScope parsedMemory11 = ParserMemoryScope.Create(governor);
            _ = parser.ParseIndirectObject(new PdfTokenizer(Encoding.ASCII.GetBytes("5 x obj 1 endobj")), 0, parsedMemory11);
        });
        Assert.Throws<QpdfParseException>(() =>
        {
            using ParserMemoryScope parsedMemory12 = ParserMemoryScope.Create(governor);
            _ = parser.ParseIndirectObject(new PdfTokenizer(Encoding.ASCII.GetBytes("5 0 foo 1 endobj")), 0, parsedMemory12);
        });
    }
}

public sealed class ObjectModelTests
{
    [Fact]
    public void CommonAsciiNamesReuseImmutableSyntaxValuesWhilePublicConstructionCopies()
    {
        string[] names = ["Type", "Page", "Pages", "Parent", "Length", "Resources",
            "Contents", "Kids", "Count", "MediaBox", "Catalog", "Font"];
        foreach (string text in names)
        {
            byte[] raw = Encoding.ASCII.GetBytes(text);
            PdfName shared = PdfName.FromAscii(text);
            Assert.Same(PdfName.FindSharedAscii(raw), shared);
            PdfName independent = new(raw);
            Assert.NotSame(shared, independent);
            raw[0] = 0;
            byte[] returned = shared.RawBytes;
            returned[0] = 0;
            Assert.Equal(text, shared.DisplayText);
            Assert.Equal(text, independent.DisplayText);
        }
        Assert.NotSame(PdfName.FromAscii("CallerKey"), PdfName.FromAscii("CallerKey"));
        Assert.Throws<ArgumentException>(() => PdfName.FromAscii("Typé"));
    }

    [Fact]
    public void NamesCompareByRawBytes()
    {
        Assert.Equal(new PdfName([(byte)'A']), new PdfName([(byte)'A']));
        Assert.NotEqual(new PdfName([(byte)'A']), new PdfName([(byte)'B']));
        Assert.Equal("A#20B", new PdfName([65, 32, 66]).DisplayText);
    }

    [Fact]
    public void ContainersCopyAndCompareStructurally()
    {
        byte[] source = [1, 2];
        PdfByteString first = new(source, hex: false);
        source[0] = 9;
        Assert.Equal([1, 2], first.Value);
        Assert.Equal(new PdfByteString([1, 2], hex: false), first);
        Assert.NotEqual(new PdfByteString([1, 2], hex: true), first);

        PdfArray array = new([PdfNull.Instance, new PdfInteger("1", 1L)]);
        Assert.Equal(new PdfArray([PdfNull.Instance, new PdfInteger("1", 1L)]), array);

        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [new PdfName([(byte)'A'])] = new PdfInteger("1", 1L),
        };
        PdfDictionary dictionary = new(entries);
        entries.Clear();
        Assert.Equal(1, dictionary.Count);
        Assert.True(dictionary.TryGet(new PdfName([(byte)'A']), out QpdfObject value));
        Assert.Equal(new PdfInteger("1", 1L), value);
        Assert.False(dictionary.TryGet(new PdfName([(byte)'B']), out QpdfObject _));
    }
    [Fact]
    public void EscapedHashUnescapesExactlyOnceForJson()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new DiagnosticBag(governor), null);
        using ParserMemoryScope parsedMemory13 = ParserMemoryScope.Create(governor);
        PdfName name = Assert.IsType<PdfName>(parser.ParseValue(new PdfTokenizer(Encoding.ASCII.GetBytes("/#2341")), 0, parsedMemory13));
        parsedMemory13.RetainGraph(); parsedMemory13.Dispose();
        Assert.Equal("#41", Encoding.ASCII.GetString(name.RawBytes));
        using DisplayBuffer json = DisplayBuffer.CreateJson(governor);
        PdfJsonWriter.AppendName(json, name, 2);
        using GovernedText text = json.Finish();
        Assert.Equal("\"/#41\"", text.Value);
    }

    [Fact]
    public void EscapedFilterNameDoesNotMatchKnownFilter()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new DiagnosticBag(governor), null);
        using ParserMemoryScope parsedMemory14 = ParserMemoryScope.Create(governor);
        PdfDictionary dictionary = Assert.IsType<PdfDictionary>(parser.ParseValue(new PdfTokenizer(Encoding.ASCII.GetBytes("<< /Filter /#2346lateDecode >>")), 0, parsedMemory14));
        parsedMemory14.RetainGraph(); parsedMemory14.Dispose();
        using FilterPlan stages = FilterChain.Resolve(dictionary, null, governor, governor.CancellationToken);
        Assert.NotEqual("FlateDecode", stages[0].Name);
    }
}
