namespace Lokad.Qpdf.Security;

// Declared, non-secret parameters are available even when authentication fails.
// They never authorize decryption or imply that encrypted payloads were checked.
internal readonly record struct EncryptionParameters(
    int Version, int Revision, int KeyLengthBits, int Permissions, bool EncryptMetadata,
    PdfCipher Strings, PdfCipher Streams, PdfCipher Files)
{
    internal static string CipherName(PdfCipher cipher) => cipher switch
    {
        PdfCipher.Identity => "none", PdfCipher.Rc4 => "RC4", PdfCipher.Aes128 => "AESv2", PdfCipher.Aes256 => "AESv3", _ => "unknown",
    };
}

internal readonly record struct EncryptionInspection(
    EncryptionParameters? Parameters, PasswordAuthentication Authentication);
