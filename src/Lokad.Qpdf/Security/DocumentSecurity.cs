using System.Security.Cryptography;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Security;

internal enum PdfCipher : byte { Identity, Aes256, Unsupported }

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

    private DocumentSecurity(ResourceGovernor governor, ParserMemoryScope ownership, byte[] key,
        Dictionary<PdfName, CryptFilter> filters, PdfCipher strings, PdfCipher streams, PdfCipher files,
        bool metadata, int permissions, PasswordAuthentication authentication)
    {
        _governor = governor; _ownership = ownership; _key = key; _filters = filters;
        _strings = strings; _streams = streams; _files = files;
        EncryptMetadata = metadata; Permissions = permissions; Authentication = authentication;
    }

    internal bool EncryptMetadata { get; }
    internal int Permissions { get; }
    internal PasswordAuthentication Authentication { get; }

    internal static PasswordAuthentication TryCreate(PdfDictionary encryption, ReadOnlySpan<byte> password,
        ResourceGovernor governor, out DocumentSecurity? security)
    {
        security = null;
        governor.CheckCancellation();
        if (!PdfDictionaryLookup.TryGet(encryption, "Filter"u8, governor, governor.CancellationToken, out QpdfObject handler)
            || handler is not PdfName handlerName)
        { throw new QpdfParseException("An encryption dictionary requires a security-handler name.", null, null); }
        if (handlerName.RawSpan.SequenceEqual("Standard"u8) is false
            || ReadInteger(encryption, "V"u8, governor) != 5 || ReadInteger(encryption, "R"u8, governor) != 6)
        { throw new QpdfSecurityException(QpdfSecurityFailure.UnsupportedHandler); }
        if (PdfDictionaryLookup.TryGet(encryption, "Length"u8, governor, governor.CancellationToken, out QpdfObject length)
            && length is not PdfNull && (length is not PdfInteger bits || bits.Value != 256))
        { throw new QpdfParseException("Invalid revision 6 encryption key length.", null, null); }
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
                        : method.RawSpan.SequenceEqual("AESV3"u8) ? PdfCipher.Aes256 : PdfCipher.Unsupported;
                    if (cipher == PdfCipher.Aes256 && PdfDictionaryLookup.TryGet(definition, "Length"u8, governor, governor.CancellationToken, out QpdfObject size)
                        && size is not PdfNull && (size is not PdfInteger count || count.Value != 32))
                    { throw new QpdfParseException("Invalid AES-256 crypt-filter key length.", null, null); }
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
            key = new byte[32];
            PasswordAuthentication match = Revision6Encryption.Authenticate(encryption, password, key, governor);
            if (match == PasswordAuthentication.None) { return match; }
            bool metadata = !PdfDictionaryLookup.TryGet(encryption, "EncryptMetadata"u8, governor, governor.CancellationToken, out QpdfObject flag)
                || flag is not PdfBoolean boolean || boolean.Value;
            security = new(governor, memory, key, filters, strings, streams, files, metadata, ReadInteger(encryption, "P"u8, governor), match);
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

    private static PdfName ReadName(PdfDictionary dictionary, ReadOnlySpan<byte> key, PdfName fallback, ResourceGovernor governor)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, key, governor, governor.CancellationToken, out QpdfObject raw) || raw is PdfNull) { return fallback; }
        return raw as PdfName ?? throw new QpdfParseException("An encryption name must be a direct name.", null, null);
    }

    private static int ReadInteger(PdfDictionary dictionary, ReadOnlySpan<byte> key, ResourceGovernor governor)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, key, governor, governor.CancellationToken, out QpdfObject raw)
            || raw is not PdfInteger integer || integer.Value < int.MinValue || integer.Value > int.MaxValue)
        { throw new QpdfParseException("An encryption integer must be a direct 32-bit integer.", null, null); }
        return (int)integer.Value;
    }

    internal void CheckOwner(ResourceGovernor governor)
        => (_ownership ?? throw new ObjectDisposedException(nameof(DocumentSecurity))).CheckOwner(governor);

    public void Dispose()
    {
        byte[]? key = _key; _key = null;
        if (key is not null) { CryptographicOperations.ZeroMemory(key); }
        ParserMemoryScope? memory = _ownership; _ownership = null;
        _filters.Clear();
        memory?.Dispose();
    }
}
