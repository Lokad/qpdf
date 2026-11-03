using System.IO.Compression;
using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class FlateFramingTests
{
    public static IEnumerable<object[]> Packets()
    {
        foreach (CompressionLevel level in new[] { CompressionLevel.NoCompression, CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize })
        foreach (string content in new[] { "empty", "short", "zeros", "pattern", "random", "skewed" })
        foreach (bool flush in new[] { false, true }) { yield return [level, content, flush]; }
    }

    private static byte[] Encode(CompressionLevel level, string content, bool flush)
    {
        byte[] data = content switch
        {
            "empty" => [], "zeros" => new byte[8192],
            "short" => "hello hello hello"u8.ToArray(),
            "pattern" => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(" EI q Q (opaque) Tj 0123456789\n", 256))),
            _ => new byte[8192],
        };
        // An unwritten BCL stream emits no packet. Use the canonical empty
        // zlib packet, as the production encoder does for empty input.
        if (data.Length == 0) { return [0x78, 0x9c, 3, 0, 0, 0, 0, 1]; }
        if (content is "random" or "skewed")
        {
            new Random(41).NextBytes(data);
            if (content == "skewed") { for (int index = 0; index < data.Length; index++) { data[index] = (byte)(data[index] % (index % 31 == 0 ? 256 : 5)); } }
        }
        using MemoryStream output = new();
        using (ZLibStream encoder = new(output, level, true))
        {
            for (int offset = 0; offset < data.Length; offset += Math.Min(1024, data.Length - offset))
            { encoder.Write(data.AsSpan(offset, Math.Min(1024, data.Length - offset))); if (flush) { encoder.Flush(); } }
        }
        return output.ToArray();
    }

    [Theory]
    [MemberData(nameof(Packets))]
    public void PacketEndDoesNotDependOnDecodedSizeOrTrailingPdfTokens(CompressionLevel level, string content, bool flush)
    {
        byte[] packet = Encode(level, content, flush);
        byte[] source = [.. packet, .. " EI /F1 12 Tf (after) Tj"u8];
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxDecodedBytes = 1 }, CancellationToken.None);
        Assert.True(FlateFilter.TryGetEncodedLength(source, governor, CancellationToken.None, out int length));
        Assert.Equal(packet.Length, length); Assert.Equal(0L, governor.Snapshot().TotalDecodedBytes);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        using MemoryStream input = new(source, 0, length, false);
        using ZLibStream decoder = new(input, CompressionMode.Decompress);
        decoder.CopyTo(Stream.Null);
    }

    [Fact]
    public void TruncatedFramesAndReservedBlocksHaveNoAdmittedEnd()
    {
        byte[] packet = Encode(CompressionLevel.NoCompression, "pattern", false);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        foreach (int length in new[] { 0, 1, 2, 4, 8, packet.Length / 2, packet.Length - 1 })
        { Assert.False(FlateFilter.TryGetEncodedLength(packet.AsSpan(0, length), governor, CancellationToken.None, out _)); }
        Assert.False(FlateFilter.TryGetEncodedLength(new byte[] { 0x78, 0x9c, 7, 0, 0, 0, 0 }, governor, CancellationToken.None, out _));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void FrameTablesReceiveAdmissionBeforeUseAndRefundOnRefusal()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        byte[] packet = Encode(CompressionLevel.Optimal, "pattern", false);
        Assert.Throws<QpdfLimitException>(() => FlateFilter.TryGetEncodedLength(packet, governor, CancellationToken.None, out _));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
