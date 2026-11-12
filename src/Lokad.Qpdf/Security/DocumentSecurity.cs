using System.Security.Cryptography;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using static Lokad.Qpdf.Security.EncryptionDictionary;

namespace Lokad.Qpdf.Security;

internal enum PdfCipher : byte { Identity, Rc4, Aes128, Aes256, Unsupported }

// One document owns the authenticated key. Parsed encryption parameters are
// borrowed from that document; passwords are used only during construction.
internal sealed partial class DocumentSecurity : IDisposable
{
    private readonly record struct CryptFilter(PdfCipher Cipher, bool EmbeddedOnly);
    private static readonly PdfName IdentityName = PdfName.FromAscii("Identity");
    private readonly ResourceGovernor _governor;
    private readonly Dictionary<PdfName, CryptFilter> _filters;
    private readonly PdfCipher _strings;
    private readonly PdfCipher _streams;
    private readonly PdfCipher _files;
    private ParserMemoryScope? _ownership;
    private byte[]? _key;
    private PdfDictionary? _encryption;

    private DocumentSecurity(ResourceGovernor governor, ParserMemoryScope ownership, byte[] key,
        Dictionary<PdfName, CryptFilter> filters, PdfCipher strings, PdfCipher streams, PdfCipher files,
        EncryptionParameters parameters, PasswordAuthentication authentication, PdfDictionary encryption)
    {
        _governor = governor; _ownership = ownership; _key = key; _filters = filters;
        _strings = strings; _streams = streams; _files = files;
        Parameters = parameters; Authentication = authentication;
        _encryption = encryption;
    }

    internal EncryptionParameters Parameters { get; }
    internal bool EncryptMetadata => Parameters.EncryptMetadata;
    internal int Permissions => Parameters.Permissions;
    internal PasswordAuthentication Authentication { get; }
    internal int Revision => Parameters.Revision;
    internal int KeyLengthBits => Parameters.KeyLengthBits;

    internal static PasswordAuthentication TryCreate(PdfDictionary encryption, ReadOnlySpan<byte> identifier, ReadOnlySpan<byte> password,
        ResourceGovernor governor, out DocumentSecurity? security)
        => TryCreate(encryption, identifier, password, governor, out security, out _);

    internal static PasswordAuthentication TryCreate(PdfDictionary encryption, ReadOnlySpan<byte> identifier, ReadOnlySpan<byte> password,
        ResourceGovernor governor, out DocumentSecurity? security, out EncryptionParameters parameters)
    {
        security = null;
        parameters = default;
        governor.CheckCancellation();
        int revision = ReadSupportedRevision(encryption, governor);
        int keyBits = revision >= 5 ? 256 : 128;
        if (PdfDictionaryLookup.TryGet(encryption, "Length"u8, governor, governor.CancellationToken, out QpdfObject length)
            && length is not PdfNull && (length is not PdfInteger bits || bits.Value != keyBits))
        { throw new QpdfParseException("Invalid encryption key length for this security revision.", null, null); }
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        byte[]? key = null;
        bool accepted = false;
        try
        {
            memory.Reserve(256L); // Context and its owned file key.
            Dictionary<PdfName, CryptFilter> filters = ParserContainers.CreateDictionary<PdfName, CryptFilter>(memory);
            if (PdfDictionaryLookup.TryGet(encryption, "CF"u8, governor, governor.CancellationToken, out QpdfObject rawFilters)
                && rawFilters is not PdfNull)
            {
                if (rawFilters is not PdfDictionary definitions)
                { throw new QpdfParseException("Encryption crypt filters must be a direct dictionary.", null, null); }
                using PdfDictionary.EntryEnumerator entries = definitions.GetEntryEnumerator();
                while (entries.MoveNext(governor, governor.CancellationToken))
                {
                    (PdfName name, QpdfObject raw) = entries.Current;
                    if (name.Equals(IdentityName)) { continue; }
                    if (raw is not PdfDictionary definition)
                    { throw new QpdfParseException("A crypt filter must be a direct dictionary.", null, null); }
                    PdfName method = ReadName(definition, "CFM"u8, PdfName.FromAscii("None"), governor);
                    PdfCipher cipher = method.RawSpan.SequenceEqual("None"u8) ? PdfCipher.Identity
                        : revision >= 5 && method.RawSpan.SequenceEqual("AESV3"u8) ? PdfCipher.Aes256
                        : revision == 4 && method.RawSpan.SequenceEqual("AESV2"u8) ? PdfCipher.Aes128
                        : revision == 4 && method.RawSpan.SequenceEqual("V2"u8) ? PdfCipher.Rc4 : PdfCipher.Unsupported;
                    if (cipher is PdfCipher.Rc4 or PdfCipher.Aes128 or PdfCipher.Aes256 && PdfDictionaryLookup.TryGet(definition, "Length"u8, governor, governor.CancellationToken, out QpdfObject size)
                        && size is not PdfNull && (size is not PdfInteger count || count.Value != keyBits / 8))
                    { throw new QpdfParseException("Invalid crypt-filter key length.", null, null); }
                    PdfName authEvent = ReadName(definition, "AuthEvent"u8, PdfName.FromAscii("DocOpen"), governor);
                    bool embeddedOnly = authEvent.RawSpan.SequenceEqual("EFOpen"u8);
                    if (!embeddedOnly && !authEvent.RawSpan.SequenceEqual("DocOpen"u8))
                    { throw new QpdfSecurityException(QpdfSecurityFailure.UnsupportedHandler); }
                    ParserContainers.EnsureEntryCapacity(memory, filters, 8L);
                    filters.Add(name, new(cipher, embeddedOnly));
                }
            }
            PdfName streamName = ReadName(encryption, "StmF"u8, IdentityName, governor);
            PdfCipher streams = Select(filters, streamName, false);
            PdfCipher strings = Select(filters, ReadName(encryption, "StrF"u8, IdentityName, governor), false);
            PdfCipher files = Select(filters, ReadName(encryption, "EFF"u8, streamName, governor), true);
            parameters = new(revision >= 5 ? 5 : 4, revision, keyBits, ReadInteger(encryption, "P"u8, governor),
                ReadMetadata(encryption, governor), strings, streams, files);
            key = new byte[keyBits / 8];
            PasswordAuthentication match = revision >= 5
                ? Aes256Encryption.Authenticate(encryption, password, key, governor)
                : Revision4Encryption.Authenticate(encryption, identifier, password, key, governor);
            if (match == PasswordAuthentication.None) { return match; }
            security = new(governor, memory, key, filters, strings, streams, files, parameters, match, encryption);
            accepted = true;
            return match;
        }
        finally
        {
            if (!accepted)
            {
                if (key is not null) { CryptographicOperations.ZeroMemory(key); }
                memory.Dispose();
            }
        }
    }

    private static PdfCipher Select(Dictionary<PdfName, CryptFilter> filters, PdfName name, bool embeddedFile)
    {
        if (name.Equals(IdentityName)) { return PdfCipher.Identity; }
        if (!filters.TryGetValue(name, out CryptFilter filter) || filter.Cipher == PdfCipher.Unsupported)
        { throw new QpdfSecurityException(QpdfSecurityFailure.UnsupportedHandler); }
        if (filter.EmbeddedOnly && !embeddedFile)
        { throw new QpdfParseException("An embedded-file crypt filter cannot encrypt ordinary objects.", null, null); }
        return filter.Cipher;
    }

    internal void CheckOwner(ResourceGovernor governor)
        => (_ownership ?? throw new ObjectDisposedException(nameof(DocumentSecurity))).CheckOwner(governor);

    internal OutputSecurity PrepareOutput(ReadOnlySpan<byte> identifier)
    {
        CheckOwner(_governor); _governor.CheckCancellation();
        byte[] key = _key ?? throw new ObjectDisposedException(nameof(DocumentSecurity));
        PdfDictionary encryption = _encryption ?? throw new ObjectDisposedException(nameof(DocumentSecurity));
        return OutputSecurity.CopyAuthenticated(encryption, key, identifier, Parameters, _governor);
    }

    public void Dispose()
    {
        byte[]? key = _key; _key = null; _encryption = null;
        if (key is not null) { CryptographicOperations.ZeroMemory(key); }
        ParserMemoryScope? memory = _ownership; _ownership = null;
        _filters.Clear();
        memory?.Dispose();
    }
}
