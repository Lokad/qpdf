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
    private PdfDictionary ReadEncryptionDictionary(QpdfObject declaration)
    {
        _governor.CheckCancellation();
        if (_security is not null) { throw new InvalidOperationException("Document security is already installed."); }
        int number = declaration is PdfReference source ? source.ObjectNumber : 0;
        if ((Cache.Count != 0 && (Cache.Count != 1 || !Cache.ContainsKey(number))) || _cachedNulls is not null)
        { throw new InvalidOperationException("Authentication must precede document content loading."); }
        QpdfObject raw = declaration;
        if (declaration is PdfReference reference)
        {
            if (!Table.Entries.TryGetValue(number, out XrefEntry entry)
                || entry.Kind != XrefEntryKind.Uncompressed || entry.Second != reference.Generation)
            { throw new QpdfParseException("An indirect encryption dictionary must be uncompressed.", null, number); }
            raw = Load(number);
        }
        if (raw is not PdfDictionary encryption)
        { throw new QpdfParseException("Encrypt must be a dictionary.", null, null); }
        return encryption;
    }

    internal int GetEncryptionRevision(QpdfObject declaration)
        => EncryptionDictionary.ReadSupportedRevision(ReadEncryptionDictionary(declaration), _governor);

    internal PasswordAuthentication Authenticate(QpdfObject declaration, ReadOnlySpan<byte> password)
        => Authenticate(declaration, password, out _);

    internal PasswordAuthentication Authenticate(QpdfObject declaration, ReadOnlySpan<byte> password, out EncryptionParameters parameters)
    {
        PdfDictionary encryption = ReadEncryptionDictionary(declaration);
        int revision = EncryptionDictionary.ReadSupportedRevision(encryption, _governor);
        ReadOnlySpan<byte> identifier = revision <= 4 ? EncryptionDictionary.ReadIdentifier(Trailer, _governor) : [];
        PasswordAuthentication authentication = DocumentSecurity.TryCreate(encryption, identifier, password, _governor, out DocumentSecurity? security, out parameters);
        if (authentication == PasswordAuthentication.None) { return authentication; }
        _security = security ?? throw new InvalidOperationException("Authentication did not produce a security context.");
        _encryptionNumber = declaration is PdfReference source ? source.ObjectNumber : 0;
        // The exception is for document-level metadata, not every /Metadata
        // stream. Resolve the catalog while other payloads still use defaults.
        if (!_security.EncryptMetadata
            && PdfDictionaryLookup.TryGet(Trailer, "Root"u8, _governor, _governor.CancellationToken, out QpdfObject root)
            && root is PdfReference rootReference
            && PdfObjectLookup.TryResolve(rootReference, this, _governor, _governor.CancellationToken, out QpdfObject? catalog)
            && catalog is PdfDictionary dictionary
            && PdfDictionaryLookup.TryGet(dictionary, "Metadata"u8, _governor, _governor.CancellationToken, out QpdfObject metadata)
            && metadata is PdfReference metadataReference
            && Table.Entries.TryGetValue(metadataReference.ObjectNumber, out XrefEntry metadataEntry)
            && metadataEntry.Kind == XrefEntryKind.Uncompressed && metadataEntry.Second == metadataReference.Generation)
        { _rootMetadataNumber = metadataReference.ObjectNumber; }
        return authentication;
    }
}
