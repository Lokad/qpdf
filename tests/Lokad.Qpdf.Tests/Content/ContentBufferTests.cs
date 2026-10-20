using System.Text;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Filters;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class ContentBufferTests
{
    private static (byte[] Data, bool Decoded) Decode(PdfStream stream, ResourceGovernor governor, ParserMemoryScope memory)
    {
        Assert.True(AnalysisChunks.TryDecode(stream, new PoolResolver(new Dictionary<int, QpdfObject>()),
            QpdfLimits.Default, governor, memory, CancellationToken.None, out byte[] bytes, out bool decoded));
        return (bytes, decoded);
    }
    private static byte[] Normalize(byte[] data, ResourceGovernor governor, ParserMemoryScope memory, long maximum)
        => ContentTransforms.NormalizeContentBytesOwned(data, QpdfLimits.Default, governor,
            CancellationToken.None, maximum, nameof(QpdfLimits.MaxDecodedBytes), memory);
    private static PdfStream Stream(string filter, byte[] data)
        => new(filter == "" ? DecoderOwnershipFixtures.Dictionary() : DecoderOwnershipFixtures.Dictionary(("Filter", filter == "bad-plan" ? new PdfInteger("1", 1L) : PdfName.FromAscii(filter))),
            filter is "" or "DCTDecode" or "bad-plan" ? data : DecoderOwnershipFixtures.Encode(filter, data));

    [Theory]
    [InlineData("")]
    [InlineData("FlateDecode")]
    [InlineData("ASCIIHexDecode")]
    [InlineData("ASCII85Decode")]
    [InlineData("LZWDecode")]
    [InlineData("RunLengthDecode")]
    public void DecodingBorrowsFallbacksAndScopesOnlyFreshOutput(string filter)
    {
        byte[] raw = "/F1 12 Tf"u8.ToArray(); PdfStream stream = Stream(filter, raw); byte[] original = stream.Data;
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(23L); long live = governor.Snapshot().LiveMemoryBytes;
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            var decoded = Decode(stream, governor, memory); bool supported = filter is not ("" or "DCTDecode" or "bad-plan");
            Assert.Equal(supported, decoded.Decoded); Assert.Equal(raw, decoded.Data);
            if (supported) { Assert.NotSame(stream.AllocationCarrier, decoded.Data); Assert.Equal(raw.Length + 32L, memory.ReservedBytes); }
            else { Assert.Same(stream.AllocationCarrier, decoded.Data); Assert.Equal(0L, memory.ReservedBytes); }
            decoded = default;
        }
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(original, stream.Data);
    }

    [Fact]
    public void InvalidSupportedDataIsRejectedAndRetiresDecoderScratch()
    {
        PdfStream stream = new(DecoderOwnershipFixtures.Dictionary(("Filter", PdfName.FromAscii("ASCIIHexDecode"))), "ABCG>"u8.ToArray());
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        Assert.False(AnalysisChunks.TryDecode(stream, new PoolResolver(new Dictionary<int, QpdfObject>()),
            QpdfLimits.Default, governor, memory, CancellationToken.None, out byte[] bytes, out bool decoded));
        Assert.Empty(bytes); Assert.False(decoded); Assert.Equal(0L, memory.ReservedBytes);
        Assert.Equal(64L, governor.Snapshot().LiveMemoryBytes); Assert.Equal("ABCG>"u8.ToArray(), stream.Data);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("q Q", "q Q")]
    [InlineData("q\r\nQ\rq", "q\nQ\nq")]
    [InlineData("/Binary#00Name\r\n(hello\\012\\377) Tj", "/Binary#00Name\n(hello\\n\\377) Tj")]
    [InlineData("(a\\t\\300) Tj", "(a\\t\\300) Tj")]
    public void NormalizationScopesItsExactReturnedBytes(string source, string expected)
    {
        byte[] input = Encoding.ASCII.GetBytes(source); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(23L); long live = governor.Snapshot().LiveMemoryBytes;
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            byte[]? result = Normalize(input, governor, memory, 32768L);
            Assert.Equal(Encoding.ASCII.GetBytes(expected), result);
            Assert.Equal(result.Length == 0 ? 0L : result.Length + 32L, memory.ReservedBytes);
            result = null;
        }
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(Encoding.ASCII.GetBytes(source), input);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NormalizationErrorsRetireOnlyTheirScratch(bool size)
    {
        byte[] input = Encoding.ASCII.GetBytes(size ? "q Q" : "(unterminated");
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(23L); long live = governor.Snapshot().LiveMemoryBytes;
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        { Assert.ThrowsAny<QpdfException>(() => Normalize(input, governor, memory, size ? 2L : 32768L)); Assert.Equal(0L, memory.ReservedBytes); }
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
    }
}
