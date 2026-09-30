namespace Lokad.Qpdf;

/// <summary>
/// The input structurally declares encryption. Passwords, decryption, and
/// cryptographic operations are permanently out of scope; this carries no
/// password data, keys, or input content.
/// </summary>
public sealed class QpdfEncryptedInputException : QpdfException
{
    /// <summary>Gets the logical path of the encrypted input.</summary>
    public QpdfPath Path { get; }

    /// <summary>Creates an encrypted-input rejection.</summary>
    public QpdfEncryptedInputException(QpdfPath path)
        : base($"Input '{path}' declares encryption, which is permanently out of scope.")
    {
        Path = path;
    }
}
