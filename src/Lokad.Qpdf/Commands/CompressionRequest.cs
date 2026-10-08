namespace Lokad.Qpdf.Commands;

/// <summary>Raw stream-compression request shared by every entry path.</summary>
internal sealed record CompressionRequest(
    string? StreamData,
    string? CompressStreams,
    string? DecodeLevel,
    bool RecompressFlate,
    string? CompressionLevel)
{
    internal static CompressionRequest Default { get; } = new(null, null, null, false, null);

    internal static int ParseLevel(string text, string option)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (text.Length == 0 || text.Length > 10)
        {
            throw new QpdfParseException(option + " must be an integer 1..9.", 0L, null);
        }

        long value = 0L;
        foreach (char c in text)
        {
            if (c < (char)48 || c > (char)57)
            {
                throw new QpdfParseException(option + " must be an integer 1..9.", 0L, null);
            }

            value = checked(value * 10L + (c - (char)48));
        }

        if (value < 1L || value > 9L)
        {
            throw new QpdfParseException(option + " must be an integer 1..9.", 0L, null);
        }

        return (int)value;
    }
}
