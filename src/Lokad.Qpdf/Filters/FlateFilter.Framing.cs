using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Filters;

internal static partial class FlateFilter
{
    // Packet framing reads the DEFLATE syntax, without expanding LZ references
    // or allocating decoded samples. Actual decoding still validates Adler-32.
    internal static bool TryGetEncodedLength(ReadOnlySpan<byte> data, ResourceGovernor governor,
        CancellationToken token, out int length)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation(); length = 0;
        if (data.Length < 6 || (data[0] & 15) != 8 || data[0] >> 4 > 7
            || ((data[0] << 8) | data[1]) % 31 != 0 || (data[1] & 32) != 0) { return false; }
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(4096L);
        Span<byte> sizes = stackalloc byte[320]; Span<byte> codeSizes = stackalloc byte[19];
        Span<int> literalCounts = stackalloc int[16], distanceCounts = stackalloc int[16], codeCounts = stackalloc int[16];
        Span<short> literals = stackalloc short[288], distances = stackalloc short[32], codes = stackalloc short[19];
        FrameBits bits = new(data, governor, token); bits.SkipBytes(2);
        bool final;
        do
        {
            if (!bits.Read(1, out int last) || !bits.Read(2, out int kind)) { return false; }
            final = last != 0;
            if (kind == 0)
            {
                bits.Align();
                if (!bits.Read(16, out int count) || !bits.Read(16, out int inverse) || (count ^ inverse) != 65535 || !bits.SkipBytes(count)) { return false; }
                continue;
            }
            if (kind == 3) { return false; }
            int literalSize = 288, distanceSize = 32;
            sizes.Clear();
            if (kind == 1)
            {
                sizes[..144].Fill(8); sizes[144..256].Fill(9); sizes[256..280].Fill(7); sizes[280..288].Fill(8); sizes[288..].Fill(5);
            }
            else
            {
                if (!bits.Read(5, out int hlit) || !bits.Read(5, out int hdist) || !bits.Read(4, out int hclen) || hlit > 29) { return false; }
                literalSize = hlit + 257; distanceSize = hdist + 1;
                ReadOnlySpan<byte> order = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
                codeSizes.Clear();
                for (int index = 0; index < hclen + 4; index++)
                { if (!bits.Read(3, out int size)) { return false; } codeSizes[order[index]] = (byte)size; }
                if (!BuildFrameCodes(codeSizes, codeCounts, codes)) { return false; }
                int position = 0;
                while (position < literalSize + distanceSize)
                {
                    if (!ReadFrameCode(ref bits, codeCounts, codes, out int code)) { return false; }
                    if (code < 16) { sizes[position++] = (byte)code; continue; }
                    if (code == 16 && position == 0) { return false; }
                    int extra = code == 16 ? 2 : code == 17 ? 3 : 7;
                    if (!bits.Read(extra, out int repeat)) { return false; }
                    repeat += code == 18 ? 11 : 3;
                    if (repeat > literalSize + distanceSize - position) { return false; }
                    sizes.Slice(position, repeat).Fill(code == 16 ? sizes[position - 1] : (byte)0); position += repeat;
                }
            }
            if (sizes[256] == 0 || !BuildFrameCodes(sizes[..literalSize], literalCounts, literals)
                || !BuildFrameCodes(sizes.Slice(literalSize, distanceSize), distanceCounts, distances)) { return false; }
            while (true)
            {
                if (!ReadFrameCode(ref bits, literalCounts, literals, out int symbol)) { return false; }
                if (symbol < 256) { continue; }
                if (symbol == 256) { break; }
                if (symbol > 285) { return false; }
                int extra = symbol < 265 || symbol == 285 ? 0 : (symbol - 261) / 4;
                if (!bits.Read(extra, out _) || !ReadFrameCode(ref bits, distanceCounts, distances, out int distance) || distance > 29
                    || !bits.Read(distance < 4 ? 0 : distance / 2 - 1, out _)) { return false; }
            }
        } while (!final);
        bits.Align();
        if (!bits.SkipBytes(4)) { return false; }
        length = bits.Position; return true;
    }

    private static bool BuildFrameCodes(ReadOnlySpan<byte> sizes, Span<int> counts, Span<short> symbols)
    {
        counts.Clear();
        foreach (byte size in sizes) { if (size > 15) { return false; } if (size != 0) { counts[size]++; } }
        int available = 1, offset = 0; Span<int> positions = stackalloc int[16];
        for (int size = 1; size <= 15; size++)
        {
            available = (available << 1) - counts[size]; if (available < 0) { return false; }
            positions[size] = offset; offset += counts[size];
        }
        for (short symbol = 0; symbol < sizes.Length; symbol++)
        { int size = sizes[symbol]; if (size != 0) { symbols[positions[size]++] = symbol; } }
        return true;
    }

    private static bool ReadFrameCode(scoped ref FrameBits bits, scoped ReadOnlySpan<int> counts, scoped ReadOnlySpan<short> symbols, out int symbol)
    {
        int code = 0, first = 0, offset = 0;
        for (int size = 1; size <= 15; size++)
        {
            if (!bits.Read(1, out int bit)) { break; }
            code = (code << 1) | bit;
            int count = counts[size];
            if (code >= first && code - first < count) { symbol = symbols[offset + code - first]; return true; }
            offset += count; first = (first + count) << 1;
        }
        symbol = 0; return false;
    }

    private ref struct FrameBits(ReadOnlySpan<byte> data, ResourceGovernor governor, CancellationToken token)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position, _bit;
        internal int Position => _position;
        internal void Align() { if (_bit != 0) { _position++; _bit = 0; } }
        internal bool SkipBytes(int count)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (count > _data.Length - _position) { return false; }
            _position += count; return true;
        }
        internal bool Read(int count, out int value)
        {
            value = 0;
            for (int index = 0; index < count; index++)
            {
                if ((_position & 255) == 0 && _bit == 0) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); }
                if (_position >= _data.Length) { return false; }
                value |= ((_data[_position] >> _bit) & 1) << index;
                if (++_bit == 8) { _bit = 0; _position++; }
            }
            return true;
        }
    }
}
