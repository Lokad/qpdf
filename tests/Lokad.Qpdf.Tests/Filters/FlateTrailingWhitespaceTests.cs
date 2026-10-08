using System.IO.Compression;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class FlateTrailingWhitespaceTests
{
    private static byte[] Encode(byte[] bytes)
    {
        using MemoryStream output = new();
        using (ZLibStream encoder = new(output, CompressionLevel.Optimal, true)) { encoder.Write(bytes); }
        return output.ToArray();
    }
    public static IEnumerable<object[]> Frames()
    {
        // Each one-byte payload makes the checksum's last byte a PDF whitespace
        // byte. The original complete frame must be checked before its suffix.
        foreach (byte value in new byte[] { 255, 8, 9, 11, 12, 31, 97 })
        {
            foreach (string suffix in new[] { "", " ", "\t", "\r\n", "\0\f\t \n" })
            { yield return [value, suffix]; }
        }
    }
    [Theory]
    [MemberData(nameof(Frames))]
    public void FramedChecksumSurvivesInlineDelimiterWhitespace(byte value, string suffix)
    {
        byte[] raw = [value]; byte[] framed = [.. Encode(raw), .. System.Text.Encoding.ASCII.GetBytes(suffix)];
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(raw, FlateFilter.Decode(framed, null, QpdfLimits.Default, governor));
    }
    [Theory]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    [InlineData("non-whitespace")]
    public void WhitespaceNeverMakesAnInvalidTrailerValid(string shape)
    {
        byte[] framed = Encode("abcd"u8.ToArray());
        if (shape == "corrupt") { framed[^1] ^= 0x80; }
        if (shape == "truncated") { framed = framed[..^1]; }
        framed = [.. framed, .. shape == "non-whitespace" ? "! "u8 : " \t\r\n"u8];
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Throws<QpdfFilterException>(() => FlateFilter.Decode(framed, null, QpdfLimits.Default, governor));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
