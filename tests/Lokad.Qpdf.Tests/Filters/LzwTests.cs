using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class LzwTests
{
    private static (QpdfLimits Limits, ResourceGovernor Governor) Governed()
    {
        QpdfLimits limits = QpdfLimits.Default;
        return (limits, new ResourceGovernor(limits, CancellationToken.None));
    }

    private static PdfDictionary LzwDict(int? earlyChange)
    {
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Filter")] = PdfName.FromAscii("LZWDecode"),
        };
        if (earlyChange.HasValue)
        {
            entries[PdfName.FromAscii("DecodeParms")] = new PdfDictionary(new Dictionary<PdfName, QpdfObject>
            {
                [PdfName.FromAscii("EarlyChange")] = new PdfInteger(earlyChange.Value.ToString(), earlyChange.Value),
            });
        }

        return new PdfDictionary(entries);
    }

    private static byte[] DecodeThroughChain(byte[] input, PdfDictionary dict)
    {
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        using FilterPlan stages = FilterChain.Resolve(dict, null, governor, governor.CancellationToken);
        using ParserMemoryScope decodedMemory = ParserMemoryScope.Create(governor);
        return FilterChain.Decode(input, stages, limits, governor, decodedMemory);
    }

    private static byte[] LzwEncode(byte[] raw, bool earlyChange)
    {
        // The decoder skips exactly one table add (the first code after
        // clear) which has no counterpart on the encoding side, so the
        // encoder reaches every table size one emitted code later than the
        // decoder reaches it while reading. Growth therefore triggers one
        // count later here than the decoder thresholds {253, 765, 1789}.
        int[] thresholds = earlyChange ? [254, 766, 1790] : [255, 767, 1791];
        Dictionary<string, int> table = new(StringComparer.Ordinal);
        List<byte> output = new();
        int buffer = 0;
        int held = 0;
        int width = 9;
        void Emit(int code)
        {
            if (code < 0 || code >= (1 << width))
            {
                throw new InvalidOperationException($"Encoder emitted code {code} at width {width} (table {table.Count}).");
            }

            buffer = (buffer << width) | code;
            held += width;
            while (held >= 8)
            {
                held -= 8;
                output.Add((byte)((buffer >> held) & 0xFF));
            }
        }

        int CodeOf(string prefix)
        {
            return prefix.Length == 1 ? prefix[0] : table[prefix];
        }

        Emit(256);
        if (raw.Length == 0)
        {
            Emit(257);
        }
        else
        {
            string prefix = new string((char)raw[0], 1);
            for (int i = 1; i < raw.Length; i++)
            {
                string extended = prefix + (char)raw[i];
                if (table.TryGetValue(extended, out int known))
                {
                    prefix = extended;
                    continue;
                }

                Emit(CodeOf(prefix));
                if (table.Count == 3837)
                {
                    Emit(256);
                    table.Clear();
                    width = 9;
                    prefix = new string((char)raw[i], 1);
                    continue;
                }

                table[extended] = 258 + table.Count;
                if (thresholds.Contains(table.Count))
                {
                    width += 1;
                }

                prefix = new string((char)raw[i], 1);
            }

            Emit(CodeOf(prefix));
            Emit(257);
        }

        if (held > 0)
        {
            output.Add((byte)((buffer << (8 - held)) & 0xFF));
        }

        return [.. output];
    }

    [Theory]
    [InlineData(252, 9, true)]
    [InlineData(252, 10, false)]
    [InlineData(253, 9, false)]
    [InlineData(253, 10, true)]
    public void DecoderGrowthBoundary(int adds, int trailingWidth, bool expectPass)
    {
        List<(int Code, int Width)> codes = new() { (256, 9) };
        for (int i = 0; i <= adds; i++)
        {
            codes.Add((i, 9));
        }

        codes.Add((65, trailingWidth));
        codes.Add((257, trailingWidth));
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        bool passed;
        try
        {
            byte[] actual = LzwFilter.Decode(PackCodes(codes), true, limits, governor);
            byte[] expected = new byte[adds + 2];
            for (int i = 0; i <= adds; i++)
            {
                expected[i] = (byte)i;
            }

            expected[adds + 1] = 65;
            passed = actual.AsSpan().SequenceEqual(expected);
        }
        catch (QpdfFilterException)
        {
            passed = false;
        }

        Assert.Equal(expectPass, passed);
    }

    [Fact]
    public void DecoderWidensAfter253Adds()
    {
        List<(int Code, int Width)> codes = new() { (256, 9) };
        for (int i = 0; i < 254; i++)
        {
            codes.Add((i, 9));
        }

        codes.Add((65, 9));
        codes.Add((257, 9));
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        byte[] expected = new byte[256];
        for (int i = 0; i < 254; i++)
        {
            expected[i] = (byte)i;
        }

        expected[254] = 65;
        expected[255] = 65;
        bool widened;
        try
        {
            byte[] actual = LzwFilter.Decode(PackCodes(codes), true, limits, governor);
            widened = !actual.AsSpan().SequenceEqual(expected);
        }
        catch (QpdfFilterException)
        {
            widened = true;
        }

        Assert.True(widened, "Decoder should have widened to 10 bits after 253 adds.");
    }


    [Theory]
    [InlineData(100, true)]
    [InlineData(300, true)]
    [InlineData(600, true)]
    [InlineData(1200, true)]
    [InlineData(2500, true)]
    [InlineData(5000, true)]
    [InlineData(150, true)]
    [InlineData(200, true)]
    [InlineData(250, true)]
    [InlineData(280, true)]
    [InlineData(100, false)]
    [InlineData(2000, true)]
    [InlineData(2000, false)]
    public void RoundTripsGradedSizes(int size, bool earlyChange)
    {
        Random random = new(424242);
        byte[] raw = new byte[size];
        random.NextBytes(raw);
        byte[] encoded = LzwEncode(raw, earlyChange);
        int? setting = earlyChange ? 1 : 0;
        byte[] decoded = DecodeThroughChain(encoded, LzwDict(setting));
        Assert.Equal(raw, decoded);
    }

    [Fact]
    public void DecodesAcrossWidthGrowth()
    {
        List<(int Code, int Width)> codes = new() { (256, 9) };
        for (int i = 0; i < 254; i++)
        {
            codes.Add((i, 9));
        }

        codes.Add((65, 10));
        codes.Add((258, 10));
        codes.Add((257, 10));
        codes.Add((257, 10));
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        byte[] decoded = LzwFilter.Decode(PackCodes(codes), true, limits, governor);
        byte[] expected = new byte[257];
        for (int i = 0; i < 254; i++)
        {
            expected[i] = (byte)i;
        }

        expected[254] = 65;
        expected[255] = 0;
        expected[256] = 1;
        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void EncodesShortSequenceExactly()
    {
        byte[] raw = [0x41, 0x42, 0x41, 0x42, 0x41, 0x42];
        byte[] encoded = LzwEncode(raw, true);
        Assert.Equal([0x80, 0x10, 0x48, 0x50, 0x28, 0x14, 0x04], encoded);
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        Assert.Equal(raw, LzwFilter.Decode(encoded, true, limits, governor));
    }

    [Fact]
    public void DecodesHandVectors()
    {
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        Assert.Empty(LzwFilter.Decode([0x80, 0x40, 0x40], true, limits, governor));
        Assert.Equal([0x41], LzwFilter.Decode(PackCodes([256, 65, 257], 9), true, limits, governor));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundTripsText(bool earlyChange)
    {
        byte[] raw = Encoding.ASCII.GetBytes("TOBEORNOTTOBEORTOBEORNOTTOBEORNOT!");
        byte[] encoded = LzwEncode(raw, earlyChange);
        int? setting = earlyChange ? 1 : 0;
        byte[] decoded = DecodeThroughChain(encoded, LzwDict(setting));
        Assert.Equal(raw, decoded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundTripsBinaryAcrossWidthGrowth(bool earlyChange)
    {
        Random random = new(987654);
        byte[] raw = new byte[20_000];
        random.NextBytes(raw);
        byte[] encoded = LzwEncode(raw, earlyChange);
        int? setting = earlyChange ? 1 : 0;
        byte[] decoded = DecodeThroughChain(encoded, LzwDict(setting));
        Assert.Equal(raw, decoded);
    }

    [Fact]
    public void EarlyChangeAltersEncoding()
    {
        Random random = new(13579);
        byte[] raw = new byte[20_000];
        random.NextBytes(raw);
        Assert.NotEqual(LzwEncode(raw, true), LzwEncode(raw, false));
    }

    [Fact]
    public void AppliesHexBeforeLzwInOrder()
    {
        byte[] raw = Encoding.ASCII.GetBytes("Hex outside, LZW inside, in order.");
        string hex = Convert.ToHexString(LzwEncode(raw, true));
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Filter")] = new PdfArray([PdfName.FromAscii("ASCIIHexDecode"), PdfName.FromAscii("LZWDecode")]),
        };
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        using FilterPlan stages = FilterChain.Resolve(new PdfDictionary(entries), null, governor, governor.CancellationToken);
        using ParserMemoryScope decodedMemory = ParserMemoryScope.Create(governor);
        byte[] decoded = FilterChain.Decode(Encoding.ASCII.GetBytes(hex), stages, limits, governor, decodedMemory);
        Assert.Equal(raw, decoded);
    }

    [Fact]
    public void EarlyChangeDefaultsToOne()
    {
        byte[] raw = Encoding.ASCII.GetBytes("Default EarlyChange is one.");
        byte[] encoded = LzwEncode(raw, true);
        byte[] decoded = DecodeThroughChain(encoded, LzwDict(null));
        Assert.Equal(raw, decoded);
    }

    [Fact]
    public void RejectsBadEarlyChange()
    {
        byte[] raw = LzwEncode(Encoding.ASCII.GetBytes("bad setting"), true);
        QpdfFilterException two = Assert.Throws<QpdfFilterException>(
            () => DecodeThroughChain(raw, LzwDict(2)));
        Assert.Equal(QpdfFilterFailureReason.InvalidData, two.Reason);
        Dictionary<PdfName, QpdfObject> parms = new()
        {
            [PdfName.FromAscii("EarlyChange")] = PdfName.FromAscii("One"),
        };
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Filter")] = PdfName.FromAscii("LZWDecode"),
            [PdfName.FromAscii("DecodeParms")] = new PdfDictionary(parms),
        };
        QpdfFilterException text = Assert.Throws<QpdfFilterException>(
            () => DecodeThroughChain(raw, new PdfDictionary(entries)));
        Assert.Equal(QpdfFilterFailureReason.InvalidData, text.Reason);
    }

    [Fact]
    public void RejectsBadCodes()
    {
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        byte[] bad = PackCodes([256, 400, 257], 9);
        QpdfFilterException ex = Assert.Throws<QpdfFilterException>(
            () => LzwFilter.Decode(bad, true, limits, governor));
        Assert.Equal(QpdfFilterFailureReason.InvalidData, ex.Reason);
        Assert.Equal("LZWDecode", ex.FilterName);
    }

    [Fact]
    public void IgnoresTrailingPartialCodesAndPostEodData()
    {
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        Assert.Empty(LzwFilter.Decode([0x80], true, limits, governor));
        byte[] withGarbage = [.. LzwEncode([0x41], true), 0xFF, 0xFF];
        Assert.Equal([0x41], LzwFilter.Decode(withGarbage, true, limits, governor));
    }

    [Fact]
    public void EnforcesExpansionBudgets()
    {
        byte[] zeros = new byte[1_000_000];
        byte[] encoded = LzwEncode(zeros, true);
        Assert.True(encoded.Length < 100_000);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = 65536L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException ex = Assert.Throws<QpdfLimitException>(
            () => LzwFilter.Decode(encoded, true, limits, governor));
        Assert.Equal(nameof(QpdfLimits.MaxDecodedBytes), ex.LimitName);
    }

    [Fact]
    public void AppliesPredictorsAfterLzw()
    {
        byte[] raw = [10, 20, 30, 40, 50, 60, 70, 80];
        byte[] predicted = [10, 10, 10, 10, 10, 10, 10, 10];
        byte[] encoded = LzwEncode(predicted, true);
        Dictionary<PdfName, QpdfObject> parms = new()
        {
            [PdfName.FromAscii("Predictor")] = new PdfInteger("2", 2L),
            [PdfName.FromAscii("Columns")] = new PdfInteger("8", 8L),
        };
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Filter")] = PdfName.FromAscii("LZWDecode"),
            [PdfName.FromAscii("DecodeParms")] = new PdfDictionary(parms),
        };
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        using FilterPlan stages = FilterChain.Resolve(new PdfDictionary(entries), null, governor, governor.CancellationToken);
        using ParserMemoryScope decodedMemory = ParserMemoryScope.Create(governor);
        byte[] decoded = FilterChain.Decode(encoded, stages, limits, governor, decodedMemory);
        Assert.Equal(raw, decoded);
    }

    private static byte[] PackCodes(IEnumerable<(int Code, int Width)> codes)
    {
        List<byte> output = new();
        int buffer = 0;
        int held = 0;
        foreach ((int code, int width) in codes)
        {
            buffer = (buffer << width) | code;
            held += width;
            while (held >= 8)
            {
                held -= 8;
                output.Add((byte)((buffer >> held) & 0xFF));
            }
        }

        if (held > 0)
        {
            output.Add((byte)((buffer << (8 - held)) & 0xFF));
        }

        return [.. output];
    }

    private static byte[] PackCodes(int[] codes, int width)
    {
        List<(int Code, int Width)> paired = new();
        foreach (int code in codes)
        {
            paired.Add((code, width));
        }

        return PackCodes(paired);
    }
}

