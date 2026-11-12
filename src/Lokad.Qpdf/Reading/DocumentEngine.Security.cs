using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

internal sealed partial class DocumentEngine
{
    private DocumentSecurity? _security;
    private int _encryptionNumber;
    private int _rootMetadataNumber;
    internal DocumentSecurity? Security => _security;

    // Authenticate before loading content. Encryption dictionaries cannot be
    // compressed, and their strings must never pass through ordinary decryption.
    internal PasswordAuthentication Authenticate(PdfReference reference, ReadOnlySpan<byte> password)
    {
        _governor.CheckCancellation();
        if (_security is not null) { throw new InvalidOperationException("Document security is already installed."); }
        if ((Cache.Count != 0 && (Cache.Count != 1 || !Cache.ContainsKey(reference.ObjectNumber))) || _cachedNulls is not null)
        { throw new InvalidOperationException("Authentication must precede document content loading."); }
        if (!Table.Entries.TryGetValue(reference.ObjectNumber, out XrefEntry entry)
            || entry.Kind != XrefEntryKind.Uncompressed || entry.Second != reference.Generation)
        { throw new QpdfParseException("The encryption dictionary must be an uncompressed indirect object.", null, reference.ObjectNumber); }
        if (Load(reference.ObjectNumber) is not PdfDictionary encryption)
        { throw new QpdfParseException("Encrypt must refer to a dictionary.", null, reference.ObjectNumber); }
        PasswordAuthentication authentication = DocumentSecurity.TryCreate(encryption, password, _governor, out DocumentSecurity? security);
        if (authentication == PasswordAuthentication.None) { return authentication; }
        _security = security ?? throw new InvalidOperationException("Authentication did not produce a security context.");
        _encryptionNumber = reference.ObjectNumber;
        // The exception is for document-level metadata, not every /Metadata
        // stream. Resolve the catalog while other payloads still use defaults.
        if (!_security.EncryptMetadata
            && PdfDictionaryLookup.TryGet(Trailer, "Root"u8, _governor, _governor.CancellationToken, out QpdfObject root)
            && root is PdfReference rootReference
            && PdfObjectLookup.TryResolve(rootReference, this, _governor, _governor.CancellationToken, out QpdfObject? catalog)
            && catalog is PdfDictionary dictionary
            && PdfDictionaryLookup.TryGet(dictionary, "Metadata"u8, _governor, _governor.CancellationToken, out QpdfObject metadata)
            && metadata is PdfReference metadataReference)
        { _rootMetadataNumber = metadataReference.ObjectNumber; }
        return authentication;
    }
}
