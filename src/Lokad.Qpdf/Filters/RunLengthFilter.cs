using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Filters;

/// <summary>
/// Governed RunLengthDecode mirroring the reference state machine: length
/// bytes below 128 copy, above repeat, and 128 ends decoding.
/// Truncated tails before EOD are dropped like upstream instead of
/// failing; output stays budget-bounded.
/// </summary>
internal static class RunLengthFilter
{
    internal const string FilterName = "RunLengthDecode";

    internal static byte[] Decode(byte[] input, QpdfLimits limits, ResourceGovernor governor)
    {
        return Decode(input, limits, governor, CancellationToken.None);
    }

    internal static byte[] Decode(byte[] input, QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
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
        using FilterOutput output = FilterOutput.Create(governor, limits, token);
        int position = 0;
        while (position < input.Length)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            byte length = input[position];
            position += 1;
            if (length == 128) { break; }
            if (length < 128)
            {
                int count = length + 1;
                if (position + count > input.Length)
                {
                    break;
                }

                for (int i = 0; i < count; i++)
                {
                    output.WriteByte(input[position + i]);
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
                    output.WriteByte(input[position]);
                }

                position += 1;
            }
        }

        return output.ToArray();
    }

}
