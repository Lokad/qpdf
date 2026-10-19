using System.Buffers.Text;
using System.Globalization;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Syntax;

/// <summary>Converts decimal PDF tokens through a bounded binary64 conversion unit.</summary>
internal static class PdfRealConversion
{
    // A binary64 rounding midpoint is a dyadic rational with at most a 54-bit
    // numerator and denominator 2^1075. Its terminating decimal has at most
    // 768 significant digits. Keeping 1024 digits and a nonzero-tail indicator
    // preserves which side of every midpoint the original decimal occupies.
    private const int SignificantDigits = 1024;
    private const long WorkingBytes = 8192L;

    internal static double Convert(ReadOnlySpan<byte> token, long offset, ResourceGovernor governor)
    {
        governor.CheckCancellation();
        // Covers our fixed stack buffer and the BCL conversion's fixed state.
        // Retained token text is separately admitted by the object parser.
        governor.ReserveMemory(WorkingBytes);
        try
        {
            return ConvertCore(token, offset, governor);
        }
        finally
        {
            governor.ReleaseMemory(WorkingBytes);
        }
    }

    private static double ConvertCore(ReadOnlySpan<byte> token, long offset, ResourceGovernor governor)
    {
        if (token.IsEmpty)
        {
            throw new QpdfParseException("Empty real token.", offset, null);
        }

        bool negative = token[0] == (byte)'-';
        int start = token[0] is (byte)'+' or (byte)'-' ? 1 : 0;
        Span<byte> compact = stackalloc byte[SignificantDigits + 32];
        int written = 0;
        if (negative)
        {
            compact[written++] = (byte)'-';
        }

        int digits = 0;
        int beforePoint = -1;
        int leadingZeros = 0;
        int kept = 0;
        bool nonzeroTail = false;
        for (int i = start; i < token.Length; i++)
        {
            if ((i & 4095) == 0)
            {
                governor.CheckCancellation();
            }

            byte digit = token[i];
            if (digit == (byte)'.' && beforePoint < 0)
            {
                beforePoint = digits;
                continue;
            }

            if (digit < (byte)'0' || digit > (byte)'9')
            {
                throw new QpdfParseException("Malformed real token.", offset, null);
            }

            digits++;
            if (kept == 0 && digit == (byte)'0')
            {
                leadingZeros++;
            }
            else if (kept < SignificantDigits)
            {
                if (kept == 1)
                {
                    compact[written++] = (byte)'.';
                }

                compact[written++] = digit;
                kept++;
            }
            else
            {
                nonzeroTail |= digit != (byte)'0';
            }
        }

        governor.CheckCancellation();
        if (digits == 0)
        {
            throw new QpdfParseException("Malformed real token.", offset, null);
        }

        long exponent = (long)(beforePoint < 0 ? digits : beforePoint) - leadingZeros - 1L;
        if (kept == 0 || exponent < -324L)
        {
            return negative ? -0.0 : 0.0;
        }

        if (exponent > 308L)
        {
            return negative ? double.NegativeInfinity : double.PositiveInfinity;
        }

        if (nonzeroTail)
        {
            // No midpoint has a significant digit this far out. An arbitrary
            // positive tail preserves a strict inequality instead of creating
            // an artificial tie when all omitted digits are not zero.
            compact[written++] = (byte)'1';
        }

        compact[written++] = (byte)'E';
        if (!Utf8Formatter.TryFormat(exponent, compact[written..], out int exponentLength)
            || !double.TryParse(compact[..(written + exponentLength)], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double value))
        {
            throw new QpdfParseException("Real conversion failed.", offset, null);
        }

        governor.CheckCancellation();
        return value;
    }
}
