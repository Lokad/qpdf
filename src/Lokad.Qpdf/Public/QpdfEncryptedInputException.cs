namespace Lokad.Qpdf;

/// <summary>
/// The input structurally declares encryption on an operation route that does
/// not yet support it. This carries no password data, keys, or input content.
/// </summary>
public sealed class QpdfEncryptedInputException : QpdfException
{
    /// <summary>Gets the logical path of the encrypted input.</summary>
    public QpdfPath Path { get; }

    /// <summary>Creates an encrypted-input rejection.</summary>
    public QpdfEncryptedInputException(QpdfPath path)
        : base($"Input '{path}' declares encryption, which this operation does not support.")
    {
        Path = path;
    }
}
