using System.IO.Compression;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class FlateFrameBoundaryTests
{
    public static IEnumerable<object[]> CompleteFrames()
    {
        foreach (string shape in new[] { "empty", "fixed", "stored", "dynamic", "boundary" })
        foreach (string tail in new[] { "empty", "white", "text", "binary", "frame", "long" })
        { yield return [shape, tail]; }
    }

    private static (byte[] Raw, byte[] Frame) Frame(string shape)
    {
        if (shape == "empty") { return ([], Convert.FromHexString("7801030000000001")); }
        if (shape == "fixed") { return ("q Q"u8.ToArray(), Convert.FromHexString("78012B5408040001E700E3")); }
        byte[] raw = Enumerable.Range(0, shape == "stored" ? 137 : shape == "boundary" ? 8192 : 32768)
            .Select(index => (byte)(index % 251)).ToArray();
        using MemoryStream bytes = new();
        using (ZLibStream encoder = new(bytes, shape == "stored" ? CompressionLevel.NoCompression : CompressionLevel.SmallestSize, true))
        { encoder.Write(raw); }
        return (raw, bytes.ToArray());
    }

    private static byte[] Tail(string shape) => shape switch
    {
        "empty" => [],
        "white" => [0, 9, 10, 12, 13, 32],
        "text" => " EI endstream 17 0 obj"u8.ToArray(),
        "binary" => [255, 0, 11, 1],
        "frame" => Frame("fixed").Frame,
        "long" => Enumerable.Repeat((byte)255, 20000).ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    [Theory]
    [MemberData(nameof(CompleteFrames))]
    public void CompleteFrameEndsBeforeContainerTail(string shape, string tail)
    {
        (byte[] raw, byte[] frame) = Frame(shape);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        using (FilterPlan plan = FilterPlan.Create(governor, CancellationToken.None))
        {
            plan.Allocate(1); plan.Set(0, new("FlateDecode", null, null));
            byte[] result = FilterChain.Decode([.. frame, .. Tail(tail)], plan, QpdfLimits.Default, governor, memory, CancellationToken.None);
            Assert.Equal(raw, result); Assert.Equal(raw.Length, governor.Snapshot().TotalDecodedBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    public static IEnumerable<object[]> TruncatedFrames()
    {
        foreach (string shape in new[] { "empty", "fixed", "stored", "dynamic", "boundary" })
        foreach (int missing in new[] { 1, 2, 3, 4, 5 }) { yield return [shape, missing]; }
    }

    [Theory]
    [MemberData(nameof(TruncatedFrames))]
    public void ExhaustionCannotCompleteAFrame(string shape, int missing)
    {
        byte[] input = Frame(shape).Frame[..^missing];
        AssertInvalid(input);
    }

    [Theory]
    [InlineData("789C000300FCFF71205101E700E3")]
    [InlineData("789C0300000001")]
    [InlineData("78012B5408040001E700E2")]
    public void ACorrectChecksumInLaterBytesDoesNotRepairTheFirstFrame(string hex)
    {
        foreach (byte[] tail in new[] { Array.Empty<byte>(), " \t\r\n"u8.ToArray(), Convert.FromHexString("01E700E3"), Frame("fixed").Frame })
        { AssertInvalid([.. Convert.FromHexString(hex), .. tail]); }
    }

    [Theory]
    [InlineData("fixed")]
    [InlineData("stored")]
    [InlineData("dynamic")]
    public void CorruptFrameCannotBorrowTheCorrectTrailerFromItsTail(string shape)
    {
        byte[] frame = Frame(shape).Frame; byte[] checksum = frame[^4..];
        frame[^1] ^= 128;
        AssertInvalid([.. frame, .. checksum]);
    }

    private static void AssertInvalid(byte[] input)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope prior = ParserMemoryScope.Create(governor))
        {
            prior.Reserve(97L); long before = governor.Snapshot().LiveMemoryBytes;
            QpdfFilterException failure = Assert.Throws<QpdfFilterException>(() => FlateFilter.Decode(input, null, QpdfLimits.Default, governor));
            Assert.Equal(QpdfFilterFailureReason.TruncatedInput, failure.Reason);
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, governor.Snapshot().TotalDecodedBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
