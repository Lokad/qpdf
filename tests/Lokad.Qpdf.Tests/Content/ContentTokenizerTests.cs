using System.Text;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ContentTokenizerTests
{
    private static List<ContentToken> TokenizeAll(string text)
    {
        return TokenizeAll(Encoding.ASCII.GetBytes(text));
    }

    private static List<ContentToken> TokenizeAll(byte[] bytes)
    {
        QpdfLimits limits = QpdfLimits.Default;
        ResourceGovernor governor = new(limits, CancellationToken.None);
        ContentTokenizer tokenizer = new(bytes, limits, governor);
        List<ContentToken> tokens = new();
        while (tokenizer.TryReadToken(out ContentToken token))
        {
            tokens.Add(token);
        }

        return tokens;
    }

    private static string Text(ContentToken token)
    {
        return Encoding.ASCII.GetString(token.Data.Span);
    }

    [Fact]
    public void OperatorsAndOperands()
    {
        List<ContentToken> tokens = TokenizeAll("BT /F1 12 Tf 14.5 -3 +4 .5 72 712 Td (Hello) Tj ET");
        string[] kinds = tokens.Select(static token => token.Kind.ToString()).ToArray();
        Assert.Equal(
            ["Operator", "Name", "Integer", "Operator", "Real", "Integer", "Integer", "Real", "Integer", "Integer", "Operator", "LiteralString", "Operator", "Operator"],
            kinds);
        Assert.Equal("BT", Text(tokens[0]));
        Assert.Equal("F1", Text(tokens[1]));
        Assert.Equal("12", Text(tokens[2]));
        Assert.Equal(".5", Text(tokens[7]));
    }

    [Fact]
    public void KeywordsAndWords()
    {
        List<ContentToken> tokens = TokenizeAll("true false null XYZ nullable trueX ' \" *");
        string[] kinds = tokens.Select(static token => token.Kind.ToString()).ToArray();
        Assert.Equal(
            ["Boolean", "Boolean", "Null", "Operator", "Operator", "Operator", "Operator", "Operator", "Operator"],
            kinds);
    }

    [Fact]
    public void NumbersAndBoundaries()
    {
        List<ContentToken> tokens = TokenizeAll("+5 -12 0.5 .5 5. 0 007 12ET");
        string[] kinds = tokens.Select(static token => token.Kind.ToString()).ToArray();
        Assert.Equal(
            ["Integer", "Integer", "Real", "Real", "Real", "Integer", "Integer", "Integer", "Operator"],
            kinds);
        Assert.Equal("5.", Text(tokens[4]));
        Assert.Equal("12", Text(tokens[7]));
        Assert.Equal("ET", Text(tokens[8]));
    }

    [Fact]
    public void StringsArraysAndDicts()
    {
        List<ContentToken> tokens = TokenizeAll("(a(b)c) (x\\(y) <48656c6c6f> <AB CD> <ABC> [1 2] << /K 1 >>");
        string[] kinds = tokens.Select(static token => token.Kind.ToString()).ToArray();
        Assert.Equal(
            ["LiteralString", "LiteralString", "HexString", "HexString", "HexString", "ArrayOpen", "Integer", "Integer", "ArrayClose", "DictionaryOpen", "Name", "Integer", "DictionaryClose"],
            kinds);
        Assert.Equal("(a(b)c)", Text(tokens[0]));
        Assert.Equal("<AB CD>", Text(tokens[3]));
    }

    [Fact]
    public void SkipsCommentsAndWhitespace()
    {
        List<ContentToken> tokens = TokenizeAll("BT % comment with (paren\n\x00 /F1\tTf\r\n% trailing");
        Assert.Equal(3, tokens.Count);
        Assert.Equal("BT", Text(tokens[0]));
        Assert.Equal("F1", Text(tokens[1]));
        Assert.Equal("Tf", Text(tokens[2]));
    }

    [Theory]
    [InlineData("(abc")]
    [InlineData("<XY>")]
    [InlineData("12 34 )")]
    [InlineData(">")]
    [InlineData("- BT")]
    [InlineData(". BT")]
    public void MalformedContentThrows(string text)
    {
        Assert.Throws<QpdfParseException>(() => TokenizeAll(text));
    }
    private static (List<ContentToken> Prefix, ContentToken Image, List<ContentToken> Suffix) TokenizeImage(byte[] bytes)
    {
        QpdfLimits limits = QpdfLimits.Default;
        ResourceGovernor governor = new(limits, CancellationToken.None);
        ContentTokenizer tokenizer = new(bytes, limits, governor);
        List<ContentToken> prefix = new();
        while (tokenizer.TryReadToken(out ContentToken token))
        {
            if (token.Kind == ContentTokenKind.Operator && Encoding.ASCII.GetString(token.Data.Span) == "ID")
            {
                break;
            }

            prefix.Add(token);
        }

        Assert.True(tokenizer.TryReadInlineImageData(out ContentToken image));
        List<ContentToken> suffix = new();
        while (tokenizer.TryReadToken(out ContentToken rest))
        {
            suffix.Add(rest);
        }

        return (prefix, image, suffix);
    }

    [Fact]
    public void InlineImageBasic()
    {
        byte[] head = Encoding.ASCII.GetBytes("BI /W 1 /H 1 /CS /G /BPC 1 ID ");
        byte[] data = [0x80];
        byte[] tail = Encoding.ASCII.GetBytes(" EI ET");
        byte[] bytes = new byte[head.Length + data.Length + tail.Length];
        Buffer.BlockCopy(head, 0, bytes, 0, head.Length);
        Buffer.BlockCopy(data, 0, bytes, head.Length, data.Length);
        Buffer.BlockCopy(tail, 0, bytes, head.Length + data.Length, tail.Length);
        (List<ContentToken> prefix, ContentToken image, List<ContentToken> suffix) = TokenizeImage(bytes);
        Assert.Equal(ContentTokenKind.InlineImage, image.Kind);
        Assert.Equal([0x80, 0x20], image.Data.ToArray());
        Assert.Equal(head.Length, image.Offset);
        Assert.Single(suffix);
        Assert.Equal("ET", Text(suffix[0]));
        Assert.Equal("BI", Text(prefix[0]));
    }

    [Fact]
    public void InlineImageIgnoresEmbeddedEIWithoutDelimiters()
    {
        byte[] head = Encoding.ASCII.GetBytes("BI /W 2 ID ");
        byte[] data = Encoding.ASCII.GetBytes("xEIyBEIzEIw");
        byte[] tail = Encoding.ASCII.GetBytes(" EI Q");
        byte[] bytes = new byte[head.Length + data.Length + tail.Length];
        Buffer.BlockCopy(head, 0, bytes, 0, head.Length);
        Buffer.BlockCopy(data, 0, bytes, head.Length, data.Length);
        Buffer.BlockCopy(tail, 0, bytes, head.Length + data.Length, tail.Length);
        (List<ContentToken> _, ContentToken image, List<ContentToken> suffix) = TokenizeImage(bytes);
        byte[] expected = new byte[data.Length + 1];
        Buffer.BlockCopy(data, 0, expected, 0, data.Length);
        expected[expected.Length - 1] = 0x20;
        Assert.Equal(expected, image.Data.ToArray());
        Assert.Single(suffix);
        Assert.Equal("Q", Text(suffix[0]));
    }

    [Fact]
    public void InlineImageLookaheadRejectsBinaryLead()
    {
        byte[] head = Encoding.ASCII.GetBytes("BI ID ");
        byte[] data = Encoding.ASCII.GetBytes("AB EI ");
        byte[] garbage = [0x01, 0x02];
        byte[] middle = Encoding.ASCII.GetBytes("Q RS EI");
        byte[] tail = Encoding.ASCII.GetBytes("\nET");
        List<byte[]> parts = [head, data, garbage, middle, tail];
        int total = parts.Sum(static part => part.Length);
        byte[] bytes = new byte[total];
        int at = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, bytes, at, part.Length);
            at += part.Length;
        }

        (List<ContentToken> _, ContentToken image, List<ContentToken> suffix) = TokenizeImage(bytes);
        byte[] expected = Encoding.ASCII.GetBytes("AB EI ");
        expected = expected.Concat(garbage).Concat(Encoding.ASCII.GetBytes("Q RS ")).ToArray();
        Assert.Equal(expected, image.Data.ToArray());
        Assert.Single(suffix);
        Assert.Equal("ET", Text(suffix[0]));
    }

    [Fact]
    public void InlineImageFallsBackToLastCandidate()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("BI ID AB EI \x01\x02");
        (List<ContentToken> _, ContentToken image, List<ContentToken> suffix) = TokenizeImage(bytes);
        Assert.Equal(Encoding.ASCII.GetBytes("AB "), image.Data.ToArray());
        Assert.Single(suffix);
    }

    [Fact]
    public void InlineImageEmptyAndTrailing()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("BI ID  EI");
        (List<ContentToken> _, ContentToken image, List<ContentToken> suffix) = TokenizeImage(bytes);
        Assert.Equal([0x20], image.Data.ToArray());
        Assert.Empty(suffix);
        byte[] trailing = Encoding.ASCII.GetBytes("BI ID X EI");
        (List<ContentToken> _, ContentToken second, List<ContentToken> rest) = TokenizeImage(trailing);
        Assert.Equal(Encoding.ASCII.GetBytes("X "), second.Data.ToArray());
        Assert.Empty(rest);
    }

    [Fact]
    public void InlineImageUnterminatedThrows()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("BI /W 1 ID ABC");
        QpdfLimits limits = QpdfLimits.Default;
        ResourceGovernor governor = new(limits, CancellationToken.None);
        ContentTokenizer tokenizer = new(bytes, limits, governor);
        while (tokenizer.TryReadToken(out ContentToken token))
        {
            if (token.Kind == ContentTokenKind.Operator && Text(token) == "ID")
            {
                Assert.Throws<QpdfParseException>(() => tokenizer.TryReadInlineImageData(out ContentToken _));
                return;
            }
        }

        Assert.Fail("ID token was not produced.");
    }
    [Fact]
    public void GovernorBudgetsApply()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        ContentTokenizer tokenizer = new(Encoding.ASCII.GetBytes("BT ET"), limits, governor);
        Assert.True(tokenizer.TryReadToken(out ContentToken _));
        Assert.Throws<QpdfLimitException>(() => tokenizer.TryReadToken(out ContentToken _));
    }

    [Fact]
    public void CancellationObserved()
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = 1L };
        ResourceGovernor governor = new(limits, source.Token);
        ContentTokenizer tokenizer = new(Encoding.ASCII.GetBytes("BT ET"), limits, governor);
        Assert.Throws<OperationCanceledException>(() => tokenizer.TryReadToken(out ContentToken _));
    }
}