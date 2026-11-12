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
        EncryptionInspection inspection = InspectSecurity(engine, password, governor);
        if (inspection.Parameters is not null && inspection.Authentication == PasswordAuthentication.None)
        { throw new QpdfSecurityException(password is null ? QpdfSecurityFailure.PasswordRequired : QpdfSecurityFailure.InvalidPassword); }
    }

    internal static async ValueTask<EncryptionInspection> InspectSecurityAsync(IQpdfHost host, QpdfPath path,
        QpdfPassword? password, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics,
        QpdfRecoveryMode recovery, CancellationToken cancellationToken)
    {
        (_, EncryptionInspection security) = await OpenSecurityInspectionCore(host, path, password, limits,
            governor, diagnostics, recovery, cancellationToken, false).ConfigureAwait(false);
        return security;
    }

    internal static async ValueTask<(QpdfDocument? Document, EncryptionInspection Security)> OpenSecurityInspectionCore(
        IQpdfHost host, QpdfPath path, QpdfPassword? password, QpdfLimits limits, ResourceGovernor governor,
        DiagnosticBag diagnostics, QpdfRecoveryMode recovery, CancellationToken cancellationToken, bool needsDocument)
    {
        IQpdfInput input = await GovernedHost.OpenInputAsync(host, path, governor, cancellationToken).ConfigureAwait(false);
        DocumentEngine? engine = null;
        bool transferred = false;
        try
        {
            (DocumentEngine loaded, VersionParts header) = await ReadInputEngine(input, limits, governor, diagnostics,
                recovery, cancellationToken, false, false, false).ConfigureAwait(false);
            engine = loaded;
            EncryptionInspection security = InspectSecurity(engine, password, governor);
            if (!needsDocument || security.Parameters is not null && security.Authentication == PasswordAuthentication.None)
            { return (null, security); }
            QpdfDocument document = CompleteDocumentOpen(input, path, engine, header, limits, governor,
                diagnostics, cancellationToken, false, false);
            transferred = true;
            return (document, security);
        }
        finally
        {
            if (!transferred)
            {
                try { engine?.Dispose(); }
                finally { await input.DisposeAsync().ConfigureAwait(false); }
            }
        }
    }

    private static EncryptionInspection InspectSecurity(DocumentEngine engine, QpdfPassword? password, ResourceGovernor governor)
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
        if (encryption is null) { return default; }
        // Authentication may load streams outside the selected page ranges.
        // Retry on the same stable lease with the complete admitted input.
        if (engine.HasSelectiveInput) { throw new CompleteInputRequiredException(); }
        if (explicitNull) { throw new QpdfParseException("Incremental trailers disagree about encryption.", null, null); }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(1024L); // Bounded text encoding and password prefix.
        Span<byte> bytes = stackalloc byte[127];
        Span<byte> legacy = stackalloc byte[32];
        try
        {
            int revision = engine.GetEncryptionRevision(encryption);
            int count = password is null ? 0 : password.WriteModernBytes(bytes, governor);
            PasswordAuthentication authentication = engine.Authenticate(encryption, bytes[..count], out EncryptionParameters parameters);
            if (authentication == PasswordAuthentication.None && revision <= 4 && password is not null
                && password.TryWritePdfDocBytes(bytes[..count], legacy, out int legacyCount)
                && !bytes[..Math.Min(count, 32)].SequenceEqual(legacy[..legacyCount]))
            { authentication = engine.Authenticate(encryption, legacy[..legacyCount], out parameters); }
            return new(parameters, authentication);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(legacy); }
    }
}
