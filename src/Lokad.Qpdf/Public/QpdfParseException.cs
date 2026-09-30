namespace Lokad.Qpdf;

/// <summary>
/// Strict syntactic failure. Carries only bounded positions, never input content.
/// </summary>
public sealed class QpdfParseException : QpdfException
{
    /// <summary>Gets the absolute byte offset under analysis, if known.</summary>
    public long? ByteOffset { get; }

    /// <summary>Gets the object number under analysis, if known.</summary>
    public int? ObjectNumber { get; }

    /// <summary>Creates a parse failure.</summary>
    public QpdfParseException(string message, long? byteOffset, int? objectNumber)
        : this(message, byteOffset, objectNumber, null)
    {
    }

    /// <summary>Creates a parse failure wrapping a lower-level cause.</summary>
    public QpdfParseException(string message, long? byteOffset, int? objectNumber, Exception? innerException)
        : base(Format(message, byteOffset, objectNumber), innerException)
    {
        ByteOffset = byteOffset;
        ObjectNumber = objectNumber;
    }

    private static string Format(string message, long? byteOffset, int? objectNumber)
    {
        if (byteOffset.HasValue && objectNumber.HasValue)
        {
            return $"{message} (offset {byteOffset.Value}, object {objectNumber.Value}).";
        }

        if (byteOffset.HasValue)
        {
            return $"{message} (offset {byteOffset.Value}).";
        }

        if (objectNumber.HasValue)
        {
            return $"{message} (object {objectNumber.Value}).";
        }

        return message;
    }
}

