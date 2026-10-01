using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class TextFilterTests
{
    private static (QpdfLimits Limits, ResourceGovernor Governor) Governed()
    {
        QpdfLimits limits = QpdfLimits.Default;
        return (limits, new ResourceGovernor(limits, CancellationToken.None));
    }

    private static PdfDictionary Filtered(string filter)
    {
        return new PdfDictionary(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Filter")] = PdfName.FromAscii(filter),
        });
    }

    private static byte[] DecodeThroughChain(byte[] input, PdfDictionary dict)
    {
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        IReadOnlyList<FilterStage> stages = FilterChain.Resolve(dict);
        return FilterChain.Decode(input, stages, limits, governor);
    }

    private static string Ascii85Encode(byte[] raw)
    {
        StringBuilder output = new();
        for (int i = 0; i < raw.Length; i += 4)
        {
            int count = Math.Min(4, raw.Length - i);
            uint value = 0U;
            for (int j = 0; j < 4; j++)
            {
                value = (value << 8) | (j < count ? raw[i + j] : 0U);
            }

            if (count == 4 && value == 0U)
            {
                output.Append('z');
                continue;
            }

            char[] digits = new char[5];
            for (int j = 4; j >= 0; j--)
            {
                digits[j] = (char)((value % 85U) + 33U);
                value /= 85U;
            }

            for (int j = 0; j < count + 1; j++)
            {
                output.Append(digits[j]);
            }
        }

        return output.ToString();
    }

    private static byte[] RunLengthEncode(byte[] raw)
    {
        List<byte> output = new();
        int i = 0;
        while (i < raw.Length)
        {
            int run = 1;
            while (i + run < raw.Length && raw[i + run] == raw[i] && run < 128)
            {
                run += 1;
            }

            if (run > 1)
            {
                output.Add((byte)(257 - run));
                output.Add(raw[i]);
                i += run;
                continue;
            }

            int literal = 1;
            while (i + literal < raw.Length && literal < 128)
            {
                if (i + literal + 1 < raw.Length && raw[i + literal] == raw[i + literal + 1])
                {
                    break;
                }

                literal += 1;
            }

            output.Add((byte)(literal - 1));
            for (int j = 0; j < literal; j++)
            {
                output.Add(raw[i + j]);
            }

            i += literal;
        }

        output.Add(128);
        return [.. output];
    }

    [Fact]
    public void AsciiHexRoundTrips()
    {
        byte[] raw = Encoding.ASCII.GetBytes("Hello, hex world!");
        StringBuilder encoded = new();
        foreach (byte b in raw)
        {
            encoded.Append(b.ToString("X2"));
        }

        encoded.Append("  \n>");
        byte[] decoded = DecodeThroughChain(Encoding.ASCII.GetBytes(encoded.ToString()), Filtered("ASCIIHexDecode"));
        Assert.Equal(raw, decoded);
    }

    [Fact]
    public void AsciiHexHandlesOddDigitsAndMissingEnd()
    {
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        Assert.Equal([0xAB, 0xC0], AsciiHexFilter.Decode(Encoding.ASCII.GetBytes("AB C"), limits, governor));
        Assert.Equal([0xAB, 0xC0], AsciiHexFilter.Decode(Encoding.ASCII.GetBytes("ab c>garbage"), limits, governor));
        Assert.Equal(Array.Empty<byte>(), AsciiHexFilter.Decode(Encoding.ASCII.GetBytes("  >"), limits, governor));
    }

    [Fact]
    public void AsciiHexRejectsBadDigits()
    {
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        QpdfFilterException ex = Assert.Throws<QpdfFilterException>(
            () => AsciiHexFilter.Decode(Encoding.ASCII.GetBytes("AB CG>"), limits, governor));
        Assert.Equal(QpdfFilterFailureReason.InvalidData, ex.Reason);
        Assert.Equal("ASCIIHexDecode", ex.FilterName);
    }

    [Fact]
    public void Ascii85RoundTrips()
    {
        byte[] raw = Encoding.ASCII.GetBytes("Hello, 85 world! Zero quads \0\0\0\0 end.");
        string encoded = Ascii85Encode(raw) + "~>";
        byte[] decoded = DecodeThroughChain(Encoding.ASCII.GetBytes(encoded), Filtered("ASCII85Decode"));
        Assert.Equal(raw, decoded);
    }

    [Fact]
    public void Ascii85HandlesPartialsAndMissingEnd()
    {
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        Assert.Equal(
            Encoding.ASCII.GetBytes("Hi"),
            Ascii85Filter.Decode(Encoding.ASCII.GetBytes(Ascii85Encode(Encoding.ASCII.GetBytes("Hi"))), limits, governor));
        Assert.Equal(
            Encoding.ASCII.GetBytes("Hey"),
            Ascii85Filter.Decode(Encoding.ASCII.GetBytes(Ascii85Encode(Encoding.ASCII.GetBytes("Hey")) + "~ "),
                limits, governor));
    }

    [Fact]
    public void Ascii85RejectsBadGroups()
    {
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        Assert.Throws<QpdfFilterException>(
            () => Ascii85Filter.Decode(Encoding.ASCII.GetBytes("abzcd~>"), limits, governor));
        Assert.Throws<QpdfFilterException>(
            () => Ascii85Filter.Decode(Encoding.ASCII.GetBytes("abc~x"), limits, governor));
        Assert.Throws<QpdfFilterException>(
            () => Ascii85Filter.Decode(Encoding.ASCII.GetBytes("ab\u007Fcd~>"), limits, governor));
    }

    [Fact]
    public void RunLengthRoundTrips()
    {
        byte[] raw = [1, 1, 1, 2, 3, 4, 4, 5, 6, 6, 6, 6, 7];
        byte[] decoded = DecodeThroughChain(RunLengthEncode(raw), Filtered("RunLengthDecode"));
        Assert.Equal(raw, decoded);
    }

    [Fact]
    public void RunLengthContinuesPastEndMarkers()
    {
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        byte[] decoded = RunLengthFilter.Decode([0, 65, 128, 0, 66], limits, governor);
        Assert.Equal([65, 66], decoded);
    }

    [Fact]
    public void RunLengthDropsTruncatedTails()
    {
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        Assert.Equal([65], RunLengthFilter.Decode([0, 65, 2, 66], limits, governor));
        Assert.Empty(RunLengthFilter.Decode([254], limits, governor));
    }

    [Fact]
    public void TextFiltersRejectParms()
    {
        byte[] payload = Encoding.ASCII.GetBytes("parms refused");
        foreach (string name in new[] { "ASCIIHexDecode", "ASCII85Decode", "RunLengthDecode" })
        {
            Dictionary<PdfName, QpdfObject> parms = new()
            {
                [PdfName.FromAscii("Predictor")] = new PdfInteger("1", 1L),
            };
            Dictionary<PdfName, QpdfObject> entries = new()
            {
                [PdfName.FromAscii("Filter")] = PdfName.FromAscii(name),
                [PdfName.FromAscii("DecodeParms")] = new PdfDictionary(parms),
            };
            QpdfFilterException ex = Assert.Throws<QpdfFilterException>(
                () => DecodeThroughChain(payload, new PdfDictionary(entries)));
            Assert.Equal(QpdfFilterFailureReason.InvalidData, ex.Reason);
        }
    }

    [Fact]
    public void TextFiltersEnforceBudgets()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = 4L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        Assert.Throws<QpdfLimitException>(
            () => AsciiHexFilter.Decode(Encoding.ASCII.GetBytes("AABBCCDDEE>"), limits, governor));
    }
}
