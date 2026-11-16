namespace Lokad.Qpdf;

/// <summary>
/// An operation refused an input that structurally declares encryption.
/// This carries no password data, keys, or input content.
/// </summary>
/// <remarks>Built-in readers report credential and handler failures through
/// <see cref="QpdfSecurityException" />.</remarks>
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
