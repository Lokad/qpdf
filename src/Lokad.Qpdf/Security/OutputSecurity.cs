using System.Globalization;
using System.Security.Cryptography;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Security;

// One prepared output owns its key and encryption dictionary through emission.
// Passwords are borrowed only during creation and never retained in this owner.
internal sealed partial class OutputSecurity : IDisposable
{
    private ParserMemoryScope? _memory;
    private byte[]? _key;
    private PdfDictionary? _dictionary;
    private byte[]? _preservedIdentifier;
    internal bool EncryptMetadata { get; }
    internal int Revision { get; }
    private PdfCipher Cipher => Revision < 4 ? PdfCipher.Rc4 : Revision == 4 ? PdfCipher.Aes128 : PdfCipher.Aes256;

    private OutputSecurity(ParserMemoryScope memory, byte[] key, PdfDictionary dictionary, int revision, bool metadata, byte[]? identifier)
    { _memory = memory; _key = key; _dictionary = dictionary; Revision = revision; EncryptMetadata = metadata; _preservedIdentifier = identifier; }

    internal PdfDictionary Dictionary => _dictionary ?? throw new ObjectDisposedException(nameof(OutputSecurity));

    internal ReadOnlyMemory<byte>? PreservedIdentifier
    {
        get
        {
            ObjectDisposedException.ThrowIf(_memory is null, this);
            return _preservedIdentifier is null ? null : new ReadOnlyMemory<byte>(_preservedIdentifier);
        }
    }

    internal static OutputSecurity CreateRevision6(ReadOnlySpan<byte> userPassword, ReadOnlySpan<byte> ownerPassword,
        int permissions, bool metadata, ResourceGovernor governor)
    {
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        byte[]? key = null;
        Span<byte> values = stackalloc byte[176];
        try
        {
            memory.Reserve(160L); // Context, fixed construction state and the file-key array.
            key = new byte[32];
            Aes256Encryption.Generate(userPassword, ownerPassword, permissions, metadata, key, values, governor);
            PdfDictionary dictionary = BuildDictionary(values, 5, 6, 256, permissions, metadata, memory, governor);
            governor.CheckCancellation(); return new(memory, key, dictionary, 6, metadata, null);
        }
        catch { if (key is not null) { CryptographicOperations.ZeroMemory(key); } memory.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(values); }
    }

    private static PdfDictionary BuildDictionary(ReadOnlySpan<byte> values, int version, int revision, int keyBits, int permissions, bool metadata,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        bool modern = revision >= 5;
        PdfNameTable<QpdfObject> root = Table();
        Add(root, "Filter", Name("Standard")); Add(root, "V", PdfInteger.GetSharedValue(version));
        Add(root, "R", PdfInteger.GetSharedValue(revision));
        memory.Reserve(136L); // Two integer objects and their bounded decimal text.
        Add(root, "Length", new PdfInteger(keyBits.ToString(CultureInfo.InvariantCulture), keyBits));
        Add(root, "P", new PdfInteger(permissions.ToString(CultureInfo.InvariantCulture), permissions));
        if (revision >= 4)
        {
            PdfNameTable<QpdfObject> filter = Table();
            Add(filter, "CFM", Name(modern ? "AESV3" : "AESV2")); Add(filter, "AuthEvent", Name("DocOpen"));
            Add(filter, "Length", PdfInteger.GetSharedValue(keyBits / 8));
            PdfNameTable<QpdfObject> filters = Table(); Add(filters, "StdCF", Complete(filter));
            Add(root, "EncryptMetadata", metadata ? PdfBoolean.True : PdfBoolean.False);
            Add(root, "CF", Complete(filters));
            PdfName selected = Name("StdCF"); Add(root, "StmF", selected); Add(root, "StrF", selected);
        }
        int entryLength = modern ? 48 : 32;
        Add(root, "U", Bytes(values[..entryLength])); Add(root, "O", Bytes(values.Slice(entryLength, entryLength)));
        if (modern)
        {
            Add(root, "UE", Bytes(values.Slice(96, 32))); Add(root, "OE", Bytes(values.Slice(128, 32)));
            Add(root, "Perms", Bytes(values.Slice(160, 16)));
        }
        return Complete(root);

        PdfNameTable<QpdfObject> Table() => PdfNameTable<QpdfObject>.Create(memory, governor, IntPtr.Size, governor.CancellationToken);
        PdfName Name(string value)
        { memory.Reserve(56L + value.Length); return PdfName.FromAscii(value); }
        void Add(PdfNameTable<QpdfObject> table, string name, QpdfObject value)
        { table.Assign(memory, governor, Name(name), value, governor.CancellationToken); }
        PdfDictionary Complete(PdfNameTable<QpdfObject> table)
        {
            table.Freeze(memory, governor, governor.CancellationToken); memory.Reserve(24L);
            return PdfDictionary.FromOwnedEntries(table);
        }
        PdfByteString Bytes(ReadOnlySpan<byte> value)
        { memory.Reserve(56L + value.Length); return PdfByteString.FromOwnedBytes(value.ToArray(), true); }
    }

    internal void ValidateIdentifier(ReadOnlySpan<byte> identifier, ResourceGovernor governor)
    {
        (_memory ?? throw new ObjectDisposedException(nameof(OutputSecurity))).CheckOwner(governor);
        governor.CheckCancellation();
        if (_preservedIdentifier is not null && !identifier.SequenceEqual(_preservedIdentifier))
        { throw new QpdfException("Preserved encryption requires the original first document identifier."); }
    }

    internal byte[] Encrypt(ReadOnlySpan<byte> value, int number, int generation, ParserMemoryScope owner, ResourceGovernor governor)
    {
        ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(OutputSecurity));
        memory.CheckOwner(governor);
        Span<byte> derived = stackalloc byte[16];
        try
        {
            ReadOnlySpan<byte> key = ObjectKey(number, generation, derived, governor);
            return Cipher == PdfCipher.Rc4 ? PdfRc4Cipher.Copy(value, key, owner, governor) : PdfAesCipher.Encrypt(value, key, owner, governor);
        }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }

    internal int EncryptedLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return Cipher == PdfCipher.Rc4 ? length : PdfAesCipher.EncryptedLength(length);
    }

    internal QpdfObject EncryptObject(QpdfObject value, int number, int generation, bool rootMetadata, IIndirectResolver resolver,
        ParserMemoryScope destination, ResourceGovernor governor)
    {
        ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(OutputSecurity));
        memory.CheckOwner(governor); destination.CheckOwner(governor); governor.CheckCancellation();
        if (ReferenceEquals(value, Dictionary) || value is not (PdfByteString or PdfArray or PdfDictionary or PdfStream)) { return value; }
        if (value is PdfStream xref && PdfStringCrypt.HasType(xref.Dictionary, "XRef"u8, governor)) { return value; }
        Span<byte> derived = stackalloc byte[16];
        try { return EncryptObjectCore(value, rootMetadata, resolver, destination, ObjectKey(number, generation, derived, governor), governor); }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }

    private ReadOnlySpan<byte> ObjectKey(int number, int generation, Span<byte> derived, ResourceGovernor governor)
    {
        byte[] key = _key ?? throw new ObjectDisposedException(nameof(OutputSecurity));
        if (Revision >= 5) { return key; }
        int length = LegacyEncryption.DeriveObjectKey(key, number, generation, derived, Cipher == PdfCipher.Aes128, governor);
        return derived[..length];
    }

    private QpdfObject EncryptObjectCore(QpdfObject value, bool rootMetadata, IIndirectResolver resolver,
        ParserMemoryScope destination, ReadOnlySpan<byte> key, ResourceGovernor governor)
    {
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, governor.CancellationToken);
        QpdfObject result;
        if (value is PdfStream stream)
        {
            using (FilterPlan filters = FilterChain.ResolveScoped(stream.Dictionary, resolver, governor, graph.Token))
            {
                for (int index = 0; index < filters.Count; index++)
                {
                    governor.CheckCancellation();
                    if (filters[index].Name == "Crypt")
                    { throw new QpdfUnsupportedFeatureException("Explicit stream Crypt filters are not supported for this output."); }
                }
            }
            bool identity = rootMetadata && !EncryptMetadata || FilterChain.HasExternalData(stream, resolver, governor, graph.Token);
            PdfDictionary header = PdfStringCrypt.Dictionary(stream.Dictionary, graph, 0, key, Cipher, true);
            if (identity)
            {
                graph.Memory.Reserve(32L); graph.Add(32L); result = stream.WithDictionary(header);
            }
            else
            {
                byte[] bytes = PdfStringCrypt.Bytes(stream.RawMemory.Span, graph, key, Cipher, true);
                PdfInteger length;
                if (bytes.Length < PdfInteger.SharedValueCount) { length = PdfInteger.GetSharedValue(bytes.Length); }
                else
                {
                    graph.Memory.Reserve(88L); graph.Add(88L);
                    length = new(bytes.Length.ToString(CultureInfo.InvariantCulture), bytes.Length);
                }
                header = PdfDictionaryRebuild.ReplaceEntry(graph, header, "Length"u8, length);
                graph.Memory.Reserve(32L); graph.Add(32L); result = PdfStream.FromOwnedData(header, bytes);
            }
        }
        else { result = PdfStringCrypt.Value(value, graph, 0, key, Cipher, true); }
        graph.Memory.TransferTo(destination, graph.Bytes);
        return result;
    }

    public void Dispose()
    {
        byte[]? key = _key; _key = null; _dictionary = null; _preservedIdentifier = null;
        ParserMemoryScope? memory = _memory; _memory = null;
        if (key is not null) { CryptographicOperations.ZeroMemory(key); }
        memory?.Dispose();
    }
}
