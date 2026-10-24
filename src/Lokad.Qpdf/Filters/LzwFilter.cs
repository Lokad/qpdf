using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Filters;

/// <summary>
/// Governed PDF LZW decoder mirroring the reference bit packing (MSB-first),
/// table rules (clear 256, end 257, entries 258-4095), and both EarlyChange
/// settings. Prefix links and suffix bytes replace copied dictionary strings;
/// one bounded reverse-expansion buffer supplies each output chunk. Codes after
/// end-of-data and trailing partial codes are ignored like upstream.
/// </summary>
internal static class LzwFilter
{
    internal const string FilterName = "LZWDecode";

    private const int ClearCode = 256;
    private const int EndCode = 257;
    private const int FirstCode = 258;
    private const int LastCode = 4095;

    internal static byte[] Decode(byte[] input, bool earlyChange, QpdfLimits limits, ResourceGovernor governor)
        => Decode(input, earlyChange, limits, governor, CancellationToken.None);

    internal static byte[] Decode(byte[] input, bool earlyChange, QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        limits.Validate();
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        ushort[] prefixes = Array.Empty<ushort>();
        byte[] suffixes = Array.Empty<byte>();
        byte[] expansion = Array.Empty<byte>();
        int nextCode = FirstCode;
        int delta = earlyChange ? 1 : 0;
        int codeSize = 9;
        int lastCode = ClearCode;
        using FilterOutput output = FilterOutput.Create(governor, limits, token);
        LzwBitReader reader = new(input, governor, token);
        while (reader.TryReadCode(codeSize, out int code))
        {
            if (code == ClearCode)
            {
                // Resetting nextCode invalidates old entries without clearing
                // arrays. Every new link points to a smaller active code.
                nextCode = FirstCode; codeSize = 9; lastCode = ClearCode;
                continue;
            }
            if (code == EndCode) { break; }
            if (code > nextCode || (code == nextCode && lastCode == ClearCode))
            { throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, $"Bad code {code}."); }
            if (lastCode != ClearCode && nextCode > LastCode)
            { throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Table is full."); }

            if (lastCode != ClearCode && prefixes.Length == 0)
            {
                // Allocate once, only when dictionary entries are needed. The
                // 4,096-code bound also bounds every expansion chain.
                memory.Reserve(4L * (LastCode + 1) + 3L * GovernedBufferStream.ArrayOverheadBytes);
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                prefixes = new ushort[LastCode + 1];
                suffixes = new byte[LastCode + 1];
                expansion = new byte[LastCode + 1];
            }

            byte first = (byte)code;
            int start = expansion.Length;
            if (code >= FirstCode)
            {
                int current = code;
                // KwKwK is the previous string followed by its first byte.
                if (code == nextCode) { start--; current = lastCode; }
                while (current >= FirstCode)
                {
                    if ((start & 255) == 0) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); }
                    expansion[--start] = suffixes[current]; current = prefixes[current];
                }
                first = (byte)current; expansion[--start] = first;
                if (code == nextCode) { expansion[^1] = first; }
            }

            if (lastCode != ClearCode)
            {
                prefixes[nextCode] = (ushort)lastCode; suffixes[nextCode] = first;
                int changeIndex = nextCode++ + delta;
                if (changeIndex is 511 or 1023 or 2047) { codeSize++; }
            }
            if (code < 256) { output.WriteByte(first); }
            else { output.Write(expansion.AsSpan(start)); }
            lastCode = code;
        }
        return output.ToArray();
    }

    private ref struct LzwBitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly ResourceGovernor _governor;
        private readonly CancellationToken _token;
        private int _position;
        private int _buffer;
        private int _available;

        internal LzwBitReader(ReadOnlySpan<byte> data, ResourceGovernor governor, CancellationToken token)
        {
            _data = data;
            _governor = governor;
            _token = token;
            _position = 0;
            _buffer = 0; _available = 0;
        }

        internal bool TryReadCode(int codeSize, out int code)
        {
            _token.ThrowIfCancellationRequested(); _governor.CheckCancellation();
            while (_available < codeSize)
            {
                if (_position >= _data.Length)
                {
                    code = 0;
                    return false;
                }

                _buffer = (_buffer << 8) | _data[_position];
                _position += 1;
                _available += 8;
            }

            _available -= codeSize;
            code = (_buffer >> _available) & ((1 << codeSize) - 1);
            return true;
        }
    }
}
