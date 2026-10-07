using System.IO.Compression;
using System.Text;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Filters;

internal static class DecoderOwnershipFixtures
{
    internal static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));
    internal static byte[] Encode(string filter, byte[] raw)
    {
        if (filter == "") { return raw; }
        if (filter == "FlateDecode")
        {
            // A valid empty deflate block and Adler-32 frame; BCL's lazy encoder
            // emits no frame when its only write has zero bytes.
            if (raw.Length == 0) { return [0x78, 0xda, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]; }
            using MemoryStream buffer = new();
            using (ZLibStream encoder = new(buffer, CompressionLevel.SmallestSize, true)) { encoder.Write(raw); }
            return buffer.ToArray();
        }
        if (filter == "ASCIIHexDecode") { return Encoding.ASCII.GetBytes(Convert.ToHexString(raw) + ">"); }
        if (filter == "LZWDecode")
        {
            // Independent nine-bit literal fixture: clear before each literal.
            List<int> codes = new(); foreach (byte value in raw) { codes.Add(256); codes.Add(value); } codes.Add(257);
            return Pack(codes);
        }
        if (filter == "RunLengthDecode")
        {
            List<byte> bytes = new();
            for (int index = 0; index < raw.Length; index += 128)
            { int length = Math.Min(128, raw.Length - index); bytes.Add((byte)(length - 1)); bytes.AddRange(raw.AsSpan(index, length).ToArray()); }
            bytes.Add(128); return bytes.ToArray();
        }
        if (filter != "ASCII85Decode") { throw new ArgumentOutOfRangeException(nameof(filter)); }
        StringBuilder text = new();
        for (int index = 0; index < raw.Length; index += 4)
        {
            int length = Math.Min(4, raw.Length - index); uint value = 0;
            for (int part = 0; part < 4; part++) { value = (value << 8) | (part < length ? raw[index + part] : 0U); }
            char[] group = new char[5];
            for (int part = 4; part >= 0; part--) { group[part] = (char)(33 + value % 85); value /= 85; }
            text.Append(group, 0, length + 1);
        }
        return Encoding.ASCII.GetBytes(text.Append("~>").ToString());
    }
    internal static byte[] Pack(IEnumerable<int> codes)
    {
        List<byte> bytes = new(); int value = 0, bits = 0;
        foreach (int code in codes)
        {
            value = (value << 9) | code; bits += 9;
            while (bits >= 8) { bits -= 8; bytes.Add((byte)(value >> bits)); value &= (1 << bits) - 1; }
        }
        if (bits != 0) { bytes.Add((byte)(value << (8 - bits))); }
        return bytes.ToArray();
    }
}
