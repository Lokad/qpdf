using System.IO.Compression;
using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class FlateTests
{
    private static (QpdfLimits Limits, ResourceGovernor Governor) Governed()
    {
        QpdfLimits limits = QpdfLimits.Default;
        return (limits, new ResourceGovernor(limits, CancellationToken.None));
    }

    private static byte[] ZLibCompress(byte[] input)
    {
        using MemoryStream output = new();
        using (ZLibStream compressor = new(output, CompressionLevel.Optimal))
        {
            compressor.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }

    private static PdfDictionary StreamDict(string filter, PdfDictionary? parms)
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        if (filter is not null)
        {
            entries[PdfName.FromAscii("Filter")] = PdfName.FromAscii(filter);
        }

        if (parms is not null)
        {
            entries[PdfName.FromAscii("DecodeParms")] = parms;
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

    [Fact]
    public void RoundTripsHelloWorld()
    {
        byte[] payload = Encoding.ASCII.GetBytes("Hello, governed Flate world. Hello, governed Flate world.");
        byte[] decoded = DecodeThroughChain(ZLibCompress(payload), StreamDict("FlateDecode", null));
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void RoundTripsLargePseudoRandomPayload()
    {
        Random random = new(12345);
        byte[] payload = new byte[100_000];
        random.NextBytes(payload);
        byte[] decoded = DecodeThroughChain(ZLibCompress(payload), StreamDict("Fl", null));
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void RejectsShortInputs()
    {
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        QpdfFilterException empty = Assert.Throws<QpdfFilterException>(
            () => FlateFilter.Decode([], null, limits, governor));
        Assert.Equal(QpdfFilterFailureReason.TruncatedInput, empty.Reason);
        QpdfFilterException tiny = Assert.Throws<QpdfFilterException>(
            () => FlateFilter.Decode([0x78, 0x9C, 0x03], null, limits, governor));
        Assert.Equal(QpdfFilterFailureReason.TruncatedInput, tiny.Reason);
    }

    [Fact]
    public void RejectsBadZlibHeaders()
    {
        byte[] valid = ZLibCompress(Encoding.ASCII.GetBytes("header checks"));
        (QpdfLimits limits, ResourceGovernor governor) = Governed();

        byte[] badMethod = (byte[])valid.Clone();
        badMethod[0] = 0x70;
        Assert.Equal(QpdfFilterFailureReason.InvalidData, Assert.Throws<QpdfFilterException>(
            () => FlateFilter.Decode(badMethod, null, limits, governor)).Reason);

        byte[] badCheck = (byte[])valid.Clone();
        badCheck[1] ^= 0x01;
        Assert.Equal(QpdfFilterFailureReason.InvalidData, Assert.Throws<QpdfFilterException>(
            () => FlateFilter.Decode(badCheck, null, limits, governor)).Reason);

        byte[] preset = (byte[])valid.Clone();
        preset[1] |= 0x20;
        int check = (31 - ((preset[0] << 8 | (preset[1] & 0xE0)) % 31)) % 31;
        preset[1] = (byte)((preset[1] & 0xE0) | check);
        Assert.Equal(QpdfFilterFailureReason.InvalidData, Assert.Throws<QpdfFilterException>(
            () => FlateFilter.Decode(preset, null, limits, governor)).Reason);

        byte[] window = (byte[])valid.Clone();
        window[0] = 0x88;
        window[1] = 0x1C;
        Assert.Equal(QpdfFilterFailureReason.InvalidData, Assert.Throws<QpdfFilterException>(
            () => FlateFilter.Decode(window, null, limits, governor)).Reason);
    }

    [Fact]
    public void RejectsTruncationAndCorruption()
    {
        byte[] payload = Encoding.ASCII.GetBytes(new string('a', 4096));
        byte[] compressed = ZLibCompress(payload);
        (QpdfLimits limits, ResourceGovernor governor) = Governed();

        byte[] halved = new byte[compressed.Length / 2];
        Buffer.BlockCopy(compressed, 0, halved, 0, halved.Length);
        Assert.Equal(QpdfFilterFailureReason.TruncatedInput, Assert.Throws<QpdfFilterException>(
            () => FlateFilter.Decode(halved, null, limits, governor)).Reason);

        byte[] badAdler = (byte[])compressed.Clone();
        badAdler[^1] ^= 0xFF;
        Assert.Equal(QpdfFilterFailureReason.TruncatedInput, Assert.Throws<QpdfFilterException>(
            () => FlateFilter.Decode(badAdler, null, limits, governor)).Reason);
    }

    [Fact]
    public void EnforcesExpansionBudgets()
    {
        byte[] zeros = new byte[1_000_000];
        byte[] bomb = ZLibCompress(zeros);
        Assert.True(bomb.Length < 10_000);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = 65536L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException ex = Assert.Throws<QpdfLimitException>(
            () => FlateFilter.Decode(bomb, null, limits, governor));
        Assert.Equal(nameof(QpdfLimits.MaxDecodedBytes), ex.LimitName);
    }

    [Fact]
    public void AppliesFilterChainsInOrder()
    {
        byte[] payload = Encoding.ASCII.GetBytes("Chained flate twice for order proof. Chained flate twice.");
        byte[] twice = ZLibCompress(ZLibCompress(payload));
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Filter")] = new PdfArray([PdfName.FromAscii("Fl"), PdfName.FromAscii("FlateDecode")]),
        };
        byte[] decoded = DecodeThroughChain(twice, new PdfDictionary(entries));
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void UnsupportedFiltersFailExplicitly()
    {
        // DCTDecode now decodes through the governed JPEG adapter, so non-JPEG bytes fail as invalid data;
        // JPXDecode below still pins the genuinely-unimplemented filter path.
        byte[] payload = ZLibCompress(Encoding.ASCII.GetBytes("unsupported stage"));
        QpdfFilterException dct = Assert.Throws<QpdfFilterException>(
            () => DecodeThroughChain(payload, StreamDict("DCTDecode", null)));
        Assert.Equal(QpdfFilterFailureReason.InvalidData, dct.Reason);
        Assert.Equal("DCTDecode", dct.FilterName);

        QpdfFilterException jpx = Assert.Throws<QpdfFilterException>(
            () => DecodeThroughChain(payload, StreamDict("JPXDecode", null)));
        Assert.Equal(QpdfFilterFailureReason.UnsupportedFilter, jpx.Reason);

        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Filter")] = new PdfArray([PdfName.FromAscii("FlateDecode"), PdfName.FromAscii("DCTDecode")]),
        };
        QpdfFilterException ordered = Assert.Throws<QpdfFilterException>(
            () => DecodeThroughChain(payload, new PdfDictionary(entries)));
        Assert.Equal(QpdfFilterFailureReason.InvalidData, ordered.Reason);
        Assert.Equal("DCTDecode", ordered.FilterName);
    }

    [Fact]
    public void AppliesMixedChainsInOrder()
    {
        byte[] payload = Encoding.ASCII.GetBytes("Mixed supported chains decode in order. Mixed.");
        string hex = Convert.ToHexString(ZLibCompress(payload));
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Filter")] = new PdfArray([PdfName.FromAscii("ASCIIHexDecode"), PdfName.FromAscii("FlateDecode")]),
        };
        byte[] decoded = DecodeThroughChain(Encoding.ASCII.GetBytes(hex), new PdfDictionary(entries));
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void RejectsMalformedFilterDeclarations()
    {
        byte[] payload = ZLibCompress(Encoding.ASCII.GetBytes("shapes"));
        Dictionary<PdfName, QpdfObject> integerFilter = new()
        {
            [PdfName.FromAscii("Filter")] = new PdfInteger("42", 42L),
        };
        Assert.Throws<QpdfParseException>(
            () => DecodeThroughChain(payload, new PdfDictionary(integerFilter)));

        Dictionary<PdfName, QpdfObject> mixedArray = new()
        {
            [PdfName.FromAscii("Filter")] = new PdfArray([PdfName.FromAscii("FlateDecode"), new PdfInteger("1", 1L)]),
        };
        Assert.Throws<QpdfParseException>(
            () => DecodeThroughChain(payload, new PdfDictionary(mixedArray)));

        Dictionary<PdfName, QpdfObject> bogus = new()
        {
            [PdfName.FromAscii("Filter")] = PdfName.FromAscii("BogusNoSuchFilter"),
        };
        QpdfFilterException unsupported = Assert.Throws<QpdfFilterException>(
            () => DecodeThroughChain(payload, new PdfDictionary(bogus)));
        Assert.Equal(QpdfFilterFailureReason.UnsupportedFilter, unsupported.Reason);
    }

    [Fact]
    public void AlignsDecodeParmsWithFilters()
    {
        byte[] payload = Encoding.ASCII.GetBytes("Parms alignment proof payload. Parms alignment proof.");
        byte[] twice = ZLibCompress(ZLibCompress(payload));
        Dictionary<PdfName, QpdfObject> parms = new()
        {
            [PdfName.FromAscii("Predictor")] = new PdfInteger("1", 1L),
        };
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Filter")] = new PdfArray([PdfName.FromAscii("FlateDecode"), PdfName.FromAscii("Fl")]),
            [PdfName.FromAscii("DecodeParms")] = new PdfArray([new PdfDictionary(parms), PdfNull.Instance]),
        };
        byte[] decoded = DecodeThroughChain(twice, new PdfDictionary(entries));
        Assert.Equal(payload, decoded);

        Dictionary<PdfName, QpdfObject> shortParms = new()
        {
            [PdfName.FromAscii("Filter")] = new PdfArray([PdfName.FromAscii("FlateDecode"), PdfName.FromAscii("Fl")]),
            [PdfName.FromAscii("DecodeParms")] = new PdfArray([new PdfDictionary(parms)]),
        };
        Assert.Throws<QpdfParseException>(
            () => DecodeThroughChain(twice, new PdfDictionary(shortParms)));

        Dictionary<PdfName, QpdfObject> badEntry = new()
        {
            [PdfName.FromAscii("Filter")] = PdfName.FromAscii("FlateDecode"),
            [PdfName.FromAscii("DecodeParms")] = new PdfInteger("7", 7L),
        };
        Assert.Throws<QpdfParseException>(
            () => DecodeThroughChain(payload, new PdfDictionary(badEntry)));
    }

    [Fact]
    public void IgnoresUnknownParmsKeys()
    {
        byte[] payload = Encoding.ASCII.GetBytes("Unknown parms keys are ignored like upstream.");
        Dictionary<PdfName, QpdfObject> parms = new()
        {
            [PdfName.FromAscii("Predictor")] = new PdfInteger("1", 1L),
            [PdfName.FromAscii("BogusKey")] = new PdfInteger("5", 5L),
        };
        byte[] decoded = DecodeThroughChain(ZLibCompress(payload), StreamDict("FlateDecode", new PdfDictionary(parms)));
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void RejectsNonIntegerParmsValues()
    {
        byte[] payload = ZLibCompress(Encoding.ASCII.GetBytes("parms types"));
        Dictionary<PdfName, QpdfObject> parms = new()
        {
            [PdfName.FromAscii("Predictor")] = PdfName.FromAscii("Two"),
        };
        QpdfFilterException ex = Assert.Throws<QpdfFilterException>(
            () => DecodeThroughChain(payload, StreamDict("FlateDecode", new PdfDictionary(parms))));
        Assert.Equal(QpdfFilterFailureReason.InvalidData, ex.Reason);
    }

    [Fact]
    public void PassesThroughUnfilteredStreams()
    {
        byte[] payload = Encoding.ASCII.GetBytes("no filters here");
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Length")] = new PdfInteger(payload.Length.ToString(), payload.Length),
        };
        byte[] decoded = DecodeThroughChain(payload, new PdfDictionary(entries));
        Assert.Equal(payload, decoded);
    }
    [Fact]
    public void AlreadyCanceledGovernorFailsFast()
    {
        byte[] compressed = ZLibCompress(new byte[1024]);
        using CancellationTokenSource source = new();
        source.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, source.Token);
        Assert.Throws<OperationCanceledException>(() => FlateFilter.Decode(compressed, null, QpdfLimits.Default, governor));
    }

    [Fact]
    public void EmptyInputRoundTripsAsValidZlib()
    {
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        byte[] compressed = FlateFilter.Encode([], null, limits, governor);
        Assert.True(compressed.Length >= 8);
        byte[] decoded = FlateFilter.Decode(compressed, null, limits, new ResourceGovernor(limits, CancellationToken.None));
        Assert.Empty(decoded);
    }
}
