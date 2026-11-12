namespace Lokad.Qpdf;

/// <summary>Non-secret facts about an authenticated input's password encryption.</summary>
/// <remarks>Permission bits describe PDF reader restrictions, not host authorization.</remarks>
public readonly record struct QpdfEncryptionInfo
{
    internal QpdfEncryptionInfo(int revision, int keyLengthBits, int permissions, bool encryptMetadata, bool ownerPasswordMatched)
    { Revision = revision; KeyLengthBits = keyLengthBits; Permissions = permissions; EncryptMetadata = encryptMetadata; OwnerPasswordMatched = ownerPasswordMatched; }

    /// <summary>Gets the standard security-handler revision.</summary>
    public int Revision { get; }
    /// <summary>Gets the file encryption key length in bits.</summary>
    public int KeyLengthBits { get; }
    /// <summary>Gets the signed 32-bit PDF permissions word.</summary>
    public int Permissions { get; }
    /// <summary>Gets whether document-level metadata uses default encryption.</summary>
    public bool EncryptMetadata { get; }
    /// <summary>Gets whether authentication matched the owner password.</summary>
    public bool OwnerPasswordMatched { get; }
}
