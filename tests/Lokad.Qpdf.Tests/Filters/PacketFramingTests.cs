using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class PacketFramingTests
{
    internal static byte[] WithJpegComment(byte[] jpeg, byte[] comment)
        => [.. jpeg.AsSpan(0, 2), 255, 254, (byte)((comment.Length + 2) >> 8), (byte)(comment.Length + 2), .. comment, .. jpeg.AsSpan(2)];

    public static IEnumerable<object[]> Filters()
    {
        foreach (string filter in new[] { "ASCIIHexDecode", "ASCII85Decode", "RunLengthDecode", "LZWDecode", "FlateDecode", "DCTDecode" })
        foreach (bool alias in new[] { false, true }) { yield return [filter, alias]; }
    }

    private static PdfName Filter(string filter, bool alias)
        => PdfName.FromAscii(alias ? filter switch
        { "ASCIIHexDecode" => "AHx", "ASCII85Decode" => "A85", "RunLengthDecode" => "RL", "LZWDecode" => "LZW", "FlateDecode" => "Fl", _ => "DCT" } : filter);

    private static byte[] Packet(string filter)
        => filter == "DCTDecode" ? WithJpegComment(DctDecodeTests.EncodeGrayJpeg(8, 8), " EI q Q q Q q Q "u8.ToArray())
            : DecoderOwnershipFixtures.Encode(filter, "opaque EI q Q q Q q Q bytes"u8.ToArray());

    [Theory]
    [MemberData(nameof(Filters))]
    public void BoundariesRequireNoCodecOrDecodedSampleBudget(string filter, bool alias)
    {
        byte[] packet = Packet(filter);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxDecodedBytes = 1 }, CancellationToken.None);
        Assert.True(FilterChain.TryGetEncodedLength(Filter(filter, alias), null, null, [.. packet, .. " EI (after) Tj"u8], governor, CancellationToken.None, out int length));
        Assert.Equal(packet.Length, length);
        Assert.Equal(0L, governor.Snapshot().TotalDecodedBytes); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.False(FilterChain.TryGetEncodedLength(Filter(filter, alias), null, null, packet.AsSpan(0, packet.Length - 1), governor, CancellationToken.None, out _));
    }

    [Theory]
    [MemberData(nameof(LzwDictionaryTests.Boundaries), MemberType = typeof(LzwDictionaryTests))]
    public void LzwEndFollowsBothWidthSchedulesWithoutExpandingReferences(bool early, int count)
    {
        byte[] packet = LzwTests.PackCodes(LzwDictionaryTests.RepeatedCodes(count, early));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxDecodedBytes = 1, MaxMemoryBytes = 128 }, CancellationToken.None);
        Assert.True(LzwFilter.TryGetEncodedLength([.. packet, .. " EI q Q"u8], early, governor, CancellationToken.None, out int length));
        Assert.Equal(packet.Length, length); Assert.Equal(0L, governor.Snapshot().TotalDecodedBytes);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LzwRejectsMissingEndAndInvalidCodesAndHonorsClear(bool early)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.False(LzwFilter.TryGetEncodedLength(DecoderOwnershipFixtures.Pack([256, 65, 66]), early, governor, CancellationToken.None, out _));
        Assert.False(LzwFilter.TryGetEncodedLength(DecoderOwnershipFixtures.Pack([256, 258, 257]), early, governor, CancellationToken.None, out _));
        byte[] packet = DecoderOwnershipFixtures.Pack([256, 65, 258, 256, 66, 257]);
        Assert.True(LzwFilter.TryGetEncodedLength(packet, early, governor, CancellationToken.None, out int length)); Assert.Equal(packet.Length, length);
    }

    [Fact]
    public void JpegEntropyFramingHonorsStuffingRestartsFillAndDnl()
    {
        // Marker framing only: this does not claim a renderable JPEG frame.
        byte[] packet = [255, 216, 255, 218, 0, 2, 42, 255, 0, 43, 255, 208, 44, 255, 255, 1, 45,
            255, 220, 0, 4, 0, 8, .. " EI q Q "u8, 255, 217];
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.True(DctFilter.TryGetEncodedLength([.. packet, .. " EI"u8], governor, CancellationToken.None, out int length));
        Assert.Equal(packet.Length, length);
        Assert.False(DctFilter.TryGetEncodedLength([255, 216, 255, 254, 0, 50, 255, 217], governor, CancellationToken.None, out _));
        Assert.False(DctFilter.TryGetEncodedLength([255, 216, 255, 254, 0, 1, 255, 217], governor, CancellationToken.None, out _));
    }

    [Theory]
    [InlineData("images-progressive-gray-1page.pdf")]
    [InlineData("images-progressive-rgb-1page.pdf")]
    [InlineData("images-progressive-gray-large-1page.pdf")]
    [InlineData("images-progressive-rgb-large-1page.pdf")]
    public async Task ProgressiveScanPacketsHaveOneCompleteEnd(string fixture)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        PdfStream image = Assert.Single(document.Engine.XRefEntries.Where(entry => entry.Value.Kind != XrefEntryKind.Free).Select(entry => document.GetObject(entry.Key)).OfType<PdfStream>(),
            s => s.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject subtype) && subtype is PdfName name && name.RawSpan.SequenceEqual("Image"u8));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxDecodedBytes = 1 }, CancellationToken.None);
        Assert.True(DctFilter.TryGetEncodedLength(image.RawMemory.Span, governor, CancellationToken.None, out int length));
        Assert.Equal(image.RawMemory.Length, length); Assert.Equal(0L, governor.Snapshot().TotalDecodedBytes);
    }

    [Theory]
    [MemberData(nameof(Filters))]
    public void CallerAndGovernorCancellationPrecedePacketWork(string filter, bool governorRoute)
    {
        byte[] packet = Packet(filter); using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 32 }, governorRoute ? cancellation.Token : CancellationToken.None);
        Assert.Throws<OperationCanceledException>(() => FilterChain.TryGetEncodedLength(Filter(filter, false), null, null,
            packet, governor, governorRoute ? CancellationToken.None : cancellation.Token, out _));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void FlateFrameStateAlsoReceivesSharedAdmission()
    {
        RecordingMemoryBudget root = new(1); using IMemoryScope caller = root.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, caller);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
        byte[] packet = Packet("FlateDecode");
        Assert.Throws<QpdfMemoryAdmissionException>(() => FlateFilter.TryGetEncodedLength(packet, governor, CancellationToken.None, out _));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }
}
