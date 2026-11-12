using System.Security.Cryptography;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;

namespace Lokad.Qpdf;

public sealed partial class QpdfDocument
{
    private static void AuthenticateEncrypted(DocumentEngine engine, QpdfPassword? password, ResourceGovernor governor)
    {
        QpdfObject? encryption = null;
        bool explicitNull = false;
        foreach (PdfDictionary trailer in engine.Trailers)
        {
            governor.CheckCancellation();
            if (!PdfDictionaryLookup.TryGet(trailer, "Encrypt"u8, governor, governor.CancellationToken, out QpdfObject raw)) { continue; }
            if (raw is PdfNull) { explicitNull = true; continue; }
            // The newest declaration governs the active revision. Older
            // trailers may repeat an equivalent direct dictionary or reference.
            encryption ??= raw;
        }
        if (encryption is null) { return; }
        if (explicitNull) { throw new QpdfParseException("Incremental trailers disagree about encryption.", null, null); }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(1024L); // Bounded text encoding and password prefix.
        Span<byte> bytes = stackalloc byte[127];
        Span<byte> legacy = stackalloc byte[32];
        try
        {
            int revision = engine.GetEncryptionRevision(encryption);
            int count = password is null ? 0 : password.WriteModernBytes(bytes);
            PasswordAuthentication authentication = engine.Authenticate(encryption, bytes[..count]);
            if (authentication == PasswordAuthentication.None && revision == 4 && password is not null
                && password.TryWritePdfDocBytes(legacy, out int legacyCount)
                && !bytes[..Math.Min(count, 32)].SequenceEqual(legacy[..legacyCount]))
            { authentication = engine.Authenticate(encryption, legacy[..legacyCount]); }
            if (authentication == PasswordAuthentication.None)
            { throw new QpdfSecurityException(password is null ? QpdfSecurityFailure.PasswordRequired : QpdfSecurityFailure.InvalidPassword); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(legacy); }
    }
}
