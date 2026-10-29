using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Filters;

/// <summary>
/// Governed PDF RunLengthDecode: length
/// bytes below 128 copy, above repeat, and 128 ends decoding.
/// Truncated literals emit their available prefix; a missing repeat byte
/// emits nothing. Missing EOD is tolerated; output stays budget-bounded.
/// </summary>
internal static class RunLengthFilter
{
    internal const string FilterName = "RunLengthDecode";

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
                int count = Math.Min(length + 1, input.Length - position);
                for (int i = 0; i < count; i++)
                {
                    output.WriteByte(input[position + i]);
                }

                position += count;
            }
            else
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
