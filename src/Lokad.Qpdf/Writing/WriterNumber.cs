using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Formats bounded invariant PDF numbers.</summary>
internal static class WriterNumber
{
    internal static int Format(long value, Span<byte> target, ResourceGovernor governor)
    {
        governor.NoteOperation();
        if (!Utf8Formatter.TryFormat(value, target, out int written))
        {
            throw new QpdfException("PDF integer exceeded its bounded formatting buffer.");
        }
        return written;
    }

    internal static void WriteUInt(Span<byte> output, long value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(output.Length, sizeof(long));
        for (int index = 0; index < output.Length; index++)
        {
            output[index] = (byte)((value >> (8 * (output.Length - index - 1))) & 0xFFL);
        }
    }

    internal static int FormatGeometry(double value, Span<char> target)
    {
        if (!double.IsFinite(value))
        { throw new QpdfParseException("Transformation produced non-finite geometry.", null, null); }
        // The shortest round-trip form preserves nonzero dimensions/scales.
        // Expand scientific notation because PDF number tokens have no exponent.
        Span<char> compact = stackalloc char[32];
        if (!value.TryFormat(compact, out int length, "R", CultureInfo.InvariantCulture))
        { throw new QpdfParseException("Transformation coordinate exceeds its numeric format.", null, null); }
        if (value == 0.0) { compact[0] = '0'; length = 1; }
        int exponentAt = compact[..length].IndexOf('E');
        int written;
        if (exponentAt < 0)
        {
            if (!compact[..length].TryCopyTo(target))
            { throw new QpdfParseException("Transformation coordinate exceeds its numeric format.", null, null); }
            written = length;
        }
        else
        {
            int sign = compact[0] == '-' ? 1 : 0;
            ReadOnlySpan<char> mantissa = compact[sign..exponentAt];
            int point = mantissa.IndexOf('.');
            int digits = mantissa.Length - (point < 0 ? 0 : 1);
            int exponent = int.Parse(compact[(exponentAt + 1)..length], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            int decimalAt = (point < 0 ? digits : point) + exponent;
            written = sign + (decimalAt <= 0 ? 2 - decimalAt + digits
                : Math.Max(decimalAt, digits) + (decimalAt < digits ? 1 : 0));
            if (written > target.Length)
            { throw new QpdfParseException("Transformation coordinate exceeds its numeric format.", null, null); }
            target[..written].Fill('0');
            if (sign != 0) { target[0] = '-'; }
            int destination = sign;
            if (decimalAt <= 0) { target[sign + 1] = '.'; destination = sign + 2 - decimalAt; }
            int copied = 0;
            foreach (char digit in mantissa)
            {
                if (digit == '.') { continue; }
                if (decimalAt > 0 && copied == decimalAt) { target[destination++] = '.'; }
                target[destination++] = digit; copied++;
            }
        }
        // Large integral reals must remain real tokens: integer parsing is 64-bit.
        if (!target[..written].Contains('.')
            && !long.TryParse(target[..written], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
        {
            if (target.Length - written < 2)
            { throw new QpdfParseException("Transformation coordinate exceeds its numeric format.", null, null); }
            target[written++] = '.'; target[written++] = '0';
        }
        return written;
    }

    internal static void AppendGeometry(GovernedBufferStream output, double value)
    {
        Span<char> text = stackalloc char[384];
        int length = FormatGeometry(value, text);
        Span<byte> bytes = stackalloc byte[384];
        int written = Encoding.ASCII.GetBytes(text[..length], bytes);
        output.Write(bytes[..written]);
    }
}
