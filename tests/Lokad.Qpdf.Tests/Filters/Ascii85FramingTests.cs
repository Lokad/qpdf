using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class Ascii85FramingTests
{
    [Theory]
    [InlineData("")]
    [InlineData("\0")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\f")]
    [InlineData("\r")]
    [InlineData(" ")]
    [InlineData(" \t\0\r\n\f ")]
    public void PacketLengthAndDecodingAgreeOnEndMarkerWhitespace(string whitespace)
    {
        byte[] packet = Encoding.ASCII.GetBytes("5l~" + whitespace + ">");
        byte[] input = [.. packet, .. " EI (after) Tj"u8];
        ResourceGovernor framing = new(QpdfLimits.Default with { MaxMemoryBytes = 1, MaxDecodedBytes = 1 }, CancellationToken.None);
        Assert.True(FilterChain.TryGetEncodedLength(PdfName.FromAscii("ASCII85Decode"), null, null, input, framing, CancellationToken.None, out int length));
        Assert.Equal(packet.Length, length); Assert.Equal(0L, framing.Snapshot().LiveMemoryBytes); Assert.Equal(0L, framing.Snapshot().TotalDecodedBytes);
        ResourceGovernor decoder = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(decoder))
        using (FilterPlan plan = FilterChain.Resolve(new(new Dictionary<PdfName, QpdfObject>
            { [PdfName.FromAscii("Filter")] = PdfName.FromAscii("ASCII85Decode") }), null, decoder, CancellationToken.None))
        {
            Assert.Equal("A"u8.ToArray(), FilterChain.Decode(input, plan, QpdfLimits.Default, decoder, memory, CancellationToken.None));
            Assert.Equal(1L, decoder.Snapshot().TotalDecodedBytes);
        }
        Assert.Equal(0L, decoder.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("5l~")]
    [InlineData("5l~ \t\r\n")]
    [InlineData("5l~ X~>")]
    [InlineData("5l~~>")]
    [InlineData("5l~%comment\n>")]
    [InlineData("5l~\v>")]
    public void BrokenMarkersDoNotConsumeLaterContent(string text)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.False(FilterChain.TryGetEncodedLength(PdfName.FromAscii("A85"), null, null, Encoding.ASCII.GetBytes(text), governor, CancellationToken.None, out int length));
        Assert.Equal(0, length); Assert.Equal(0L, governor.Snapshot().TotalDecodedBytes); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void LongWhitespaceMarkerNeedsNoDecodeOrAllocationAllowance()
    {
        byte[] input = Encoding.ASCII.GetBytes("5l~" + new string(' ', 65536) + "> EI");
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 1, MaxDecodedBytes = 1 }, CancellationToken.None);
        Assert.True(FilterChain.TryGetEncodedLength(PdfName.FromAscii("A85"), null, null, input, governor, CancellationToken.None, out int length));
        Assert.Equal(input.Length - 3, length); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, governor.Snapshot().TotalDecodedBytes);
    }
}
