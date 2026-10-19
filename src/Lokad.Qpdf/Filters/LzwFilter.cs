using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Filters;

/// <summary>
/// Governed PDF LZW decoder mirroring the reference bit packing (MSB-first),
/// table rules (clear 256, end 257, entries 258-4095), and both EarlyChange
/// settings. Codes after end-of-data and trailing partial codes are ignored
/// like upstream. Output is budget-bounded per emitted chunk.
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
        List<byte[]> table = ParserContainers.CreateList<byte[]>(memory);
        int delta = earlyChange ? 1 : 0;
        int codeSize = 9;
        int lastCode = ClearCode;
        bool end = false;
        using FilterOutput output = FilterOutput.Create(governor, limits, token);
        LzwBitReader reader = new(input, governor, token);
        Span<byte> literal = stackalloc byte[1];
        try
        {
            while (reader.TryReadCode(codeSize, out int code))
            {
                if (end)
                {
                    continue;
                }

                if (code == ClearCode)
                {
                    Clear(table, memory, governor, token);
                    codeSize = 9;
                }
                else if (code == EndCode)
                {
                    end = true;
                }
                else
                {
                    if (lastCode != ClearCode)
                    {
                        byte firstChar = FirstChar(code, lastCode, table);
                        int newIndex = FirstCode + table.Count;
                        if (newIndex == 4096)
                        {
                            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Table is full.");
                        }

                        ReadOnlySpan<byte> previous = Suffix(lastCode, table, literal);
                        long bytes = checked(previous.Length + 1L + GovernedBufferStream.ArrayOverheadBytes);
                        memory.Reserve(bytes);
                        governor.NoteOperation();
                        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                        byte[] entry = new byte[previous.Length + 1];
                        GovernedBufferStream.CopyBytes(previous, entry, governor, token, CancellationToken.None);
                        entry[entry.Length - 1] = firstChar;
                        ParserContainers.AddItem(memory, table, entry, IntPtr.Size);
                        int changeIndex = newIndex + delta;
                        if (changeIndex == 511 || changeIndex == 1023 || changeIndex == 2047)
                        {
                            codeSize += 1;
                        }
                    }

                    if (code < 256)
                    {
                        output.WriteByte((byte)code);
                    }
                    else
                    {
                        int index = code - FirstCode;
                        if (index < 0 || index >= table.Count)
                        {
                            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, $"Bad code {code}.");
                        }

                        output.Write(table[index]);
                    }
                }

                lastCode = code;
            }

            return output.ToArray();
        }
        finally { table.Clear(); table.Capacity = 0; }
    }

    private static byte FirstChar(int code, int lastCode, List<byte[]> table)
    {
        if (code < 256)
        {
            return (byte)code;
        }

        if (code == ClearCode || code == EndCode)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, $"Bad code {code}.");
        }

        int index = code - FirstCode;
        if (index > table.Count)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, $"Bad code {code}.");
        }

        if (index == table.Count)
        {
            return lastCode < 256 ? (byte)lastCode : table[lastCode - FirstCode][0];
        }

        return table[index][0];
    }

    private static ReadOnlySpan<byte> Suffix(int code, List<byte[]> table, Span<byte> literal)
    {
        if (code < 256)
        {
            literal[0] = (byte)code;
            return literal;
        }

        if (code == ClearCode || code == EndCode)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, $"Bad code {code}.");
        }

        int index = code - FirstCode;
        if (index < 0 || index >= table.Count)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, $"Bad code {code}.");
        }

        return table[index];
    }

    private static void Clear(List<byte[]> table, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        for (int index = 0; index < table.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            long bytes = table[index].Length + GovernedBufferStream.ArrayOverheadBytes;
            table[index] = Array.Empty<byte>();
            memory.Release(bytes);
        }
        table.Clear();
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
