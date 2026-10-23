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
        if (value > -0.00001 && value < 0.00001) { value = 0.0; }
        double rounded = Math.Round(value, 5, MidpointRounding.AwayFromZero);
        if (!rounded.TryFormat(target, out int written, "0.#####", CultureInfo.InvariantCulture))
        { throw new QpdfParseException("Transformation coordinate exceeds its numeric format.", null, null); }
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
