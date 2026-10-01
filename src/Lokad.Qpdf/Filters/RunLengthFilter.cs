using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Filters;

/// <summary>
/// Governed RunLengthDecode mirroring the reference state machine: length
/// bytes below 128 copy, above repeat, and 128 continues decoding rather
/// than stopping. Truncated tails are dropped like upstream instead of
/// failing; output stays budget-bounded.
/// </summary>
internal static class RunLengthFilter
{
    internal const string FilterName = "RunLengthDecode";

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
        int position = 0;
        while (position < input.Length)
        {
            byte length = input[position];
            position += 1;
            if (length < 128)
            {
                int count = length + 1;
                if (position + count > input.Length)
                {
                    break;
                }

                for (int i = 0; i < count; i++)
                {
                    Emit(output, input[position + i], governor, limits);
                }

                position += count;
            }
            else if (length > 128)
            {
                if (position >= input.Length)
                {
                    break;
                }

                int count = 257 - length;
                for (int i = 0; i < count; i++)
                {
                    Emit(output, input[position], governor, limits);
                }

                position += 1;
            }
        }

        return [.. output];
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
