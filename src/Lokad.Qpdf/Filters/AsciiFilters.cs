using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Filters;

/// <summary>
/// Governed ASCIIHexDecode mirroring the reference state machine: ASCII
/// whitespace ignored, `&gt;` ends input with a trailing zero pad, missing
/// end markers are tolerated at finish, and later bytes are ignored.
/// </summary>
internal static class AsciiHexFilter
{
    internal const string FilterName = "ASCIIHexDecode";

    internal static byte[] Decode(byte[] input, QpdfLimits limits, ResourceGovernor governor)
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
        List<byte> output = new();
        int pending = -1;
        foreach (byte b in input)
        {
            if (IsWhitespace(b))
            {
                continue;
            }

            if (b == (byte)'>')
            {
                if (pending >= 0)
                {
                    Emit(output, (byte)(pending << 4), governor, limits);
                }

                return [.. output];
            }

            int digit = HexValue(b);
            if (digit < 0)
            {
                throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, $"Byte 0x{b:X2} is out of range.");
            }

            if (pending < 0)
            {
                pending = digit;
            }
            else
            {
                Emit(output, (byte)((pending << 4) | digit), governor, limits);
                pending = -1;
            }
        }

        if (pending >= 0)
        {
            Emit(output, (byte)(pending << 4), governor, limits);
        }

        return [.. output];
    }

    private static bool IsWhitespace(byte b)
    {
        return b == (byte)' ' || b == (byte)'\f' || b == 0x0B || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n';
    }

    private static int HexValue(byte b)
    {
        byte upper = b >= (byte)'a' && b <= (byte)'z' ? (byte)(b - 32) : b;
        if (upper >= (byte)'0' && upper <= (byte)'9')
        {
            return upper - (byte)'0';
        }

        if (upper >= (byte)'A' && upper <= (byte)'F')
        {
            return upper - (byte)'A' + 10;
        }

        return -1;
    }

    private static void Emit(List<byte> output, byte value, ResourceGovernor governor, QpdfLimits limits)
    {
        long next = (long)output.Count + 1L;
        if (next > limits.MaxDecodedBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), next, limits.MaxDecodedBytes);
        }

        governor.ReserveMemory(1L);
        output.Add(value);
    }
}

/// <summary>
/// Governed ASCII85Decode mirroring the reference state machine: ASCII
/// whitespace ignored, `z` only as a full group, `~&gt;` ends input, partial
/// final groups pad with `u`, and later bytes are ignored. Group overflow
/// wraps like the reference without error.
/// </summary>
internal static class Ascii85Filter
{
    internal const string FilterName = "ASCII85Decode";

    internal static byte[] Decode(byte[] input, QpdfLimits limits, ResourceGovernor governor)
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
        List<byte> output = new();
        byte[] group = [ (byte)'u', (byte)'u', (byte)'u', (byte)'u', (byte)'u' ];
        int position = 0;
        bool tilde = false;
        bool done = false;
        foreach (byte b in input)
        {
            if (done)
            {
                break;
            }

            if (IsWhitespace(b))
            {
                continue;
            }

            if (tilde)
            {
                if (b != (byte)'>')
                {
                    throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Broken end-of-data sequence.");
                }

                Flush(output, group, position, governor, limits);
                done = true;
                continue;
            }

            if (b == (byte)'~')
            {
                tilde = true;
                continue;
            }

            if (b == (byte)'z')
            {
                if (position != 0)
                {
                    throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Unexpected z inside a group.");
                }

                Emit(output, 0, governor, limits);
                Emit(output, 0, governor, limits);
                Emit(output, 0, governor, limits);
                Emit(output, 0, governor, limits);
                continue;
            }

            if (b < 33 || b > 117)
            {
                throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, $"Byte 0x{b:X2} is out of range.");
            }

            group[position] = b;
            position += 1;
            if (position == 5)
            {
                Flush(output, group, position, governor, limits);
                position = 0;
                group[0] = (byte)'u';
                group[1] = (byte)'u';
                group[2] = (byte)'u';
                group[3] = (byte)'u';
                group[4] = (byte)'u';
            }
        }

        if (!done)
        {
            Flush(output, group, position, governor, limits);
        }

        return [.. output];
    }

    private static void Flush(List<byte> output, byte[] group, int position, ResourceGovernor governor, QpdfLimits limits)
    {
        if (position == 0)
        {
            return;
        }

        ulong value = 0UL;
        for (int i = 0; i < 5; i++)
        {
            value = (value * 85UL) + (ulong)(group[i] - 33);
        }

        for (int i = 0; i < position - 1; i++)
        {
            Emit(output, (byte)((value >> (8 * (3 - i))) & 0xFFUL), governor, limits);
        }
    }

    private static bool IsWhitespace(byte b)
    {
        return b == (byte)' ' || b == (byte)'\f' || b == 0x0B || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n';
    }

    private static void Emit(List<byte> output, byte value, ResourceGovernor governor, QpdfLimits limits)
    {
        long next = (long)output.Count + 1L;
        if (next > limits.MaxDecodedBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), next, limits.MaxDecodedBytes);
        }

        governor.ReserveMemory(1L);
        output.Add(value);
    }
}
