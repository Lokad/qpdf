using Lokad.Qpdf.Runtime;

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
        int delta = earlyChange ? 1 : 0;
        List<byte[]> table = new();
        int codeSize = 9;
        int lastCode = ClearCode;
        bool end = false;
        List<byte> output = new();
        LzwBitReader reader = new(input);
        while (reader.TryReadCode(codeSize, out int code))
        {
            if (end)
            {
                continue;
            }

            if (code == ClearCode)
            {
                table.Clear();
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

                    byte[] previous = Suffix(lastCode, table);
                    byte[] entry = new byte[previous.Length + 1];
                    Buffer.BlockCopy(previous, 0, entry, 0, previous.Length);
                    entry[entry.Length - 1] = firstChar;
                    governor.ReserveMemory(entry.Length);
                    table.Add(entry);
                    int changeIndex = newIndex + delta;
                    if (changeIndex == 511 || changeIndex == 1023 || changeIndex == 2047)
                    {
                        codeSize += 1;
                    }
                }

                if (code < 256)
                {
                    Emit(output, new byte[] { (byte)code }, governor, limits);
                }
                else
                {
                    int index = code - FirstCode;
                    if (index < 0 || index >= table.Count)
                    {
                        throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, $"Bad code {code}.");
                    }

                    Emit(output, table[index], governor, limits);
                }
            }

            lastCode = code;
        }

        return [.. output];
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
            return Suffix(lastCode, table)[0];
        }

        return table[index][0];
    }

    private static byte[] Suffix(int code, List<byte[]> table)
    {
        if (code < 256)
        {
            return [(byte)code];
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

    private static void Emit(List<byte> output, byte[] chunk, ResourceGovernor governor, QpdfLimits limits)
    {
        long next = (long)output.Count + chunk.Length;
        if (next > limits.MaxDecodedBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), next, limits.MaxDecodedBytes);
        }

        governor.ReserveMemory(chunk.Length);
        for (int i = 0; i < chunk.Length; i++)
        {
            output.Add(chunk[i]);
        }
    }

    private sealed class LzwBitReader
    {
        private readonly byte[] _data;
        private int _position;
        private int _buffer;
        private int _available;

        internal LzwBitReader(byte[] data)
        {
            _data = data;
            _position = 0;
        }

        internal bool TryReadCode(int codeSize, out int code)
        {
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
