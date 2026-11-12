using System.Globalization;
using System.Security.Cryptography;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Security;

// One prepared output owns its key and encryption dictionary through emission.
// Passwords are borrowed only during creation and never retained in this owner.
internal sealed class OutputSecurity : IDisposable
{
    private ParserMemoryScope? _memory;
    private byte[]? _key;
    private PdfDictionary? _dictionary;
    internal bool EncryptMetadata { get; }

    private OutputSecurity(ParserMemoryScope memory, byte[] key, PdfDictionary dictionary, bool metadata)
    { _memory = memory; _key = key; _dictionary = dictionary; EncryptMetadata = metadata; }

    internal PdfDictionary Dictionary => _dictionary ?? throw new ObjectDisposedException(nameof(OutputSecurity));

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
            Revision6Encryption.Generate(userPassword, ownerPassword, permissions, metadata, key, values, governor);
            PdfNameTable<QpdfObject> filter = Table();
            Add(filter, "CFM", Name("AESV3")); Add(filter, "AuthEvent", Name("DocOpen"));
            Add(filter, "Length", PdfInteger.GetSharedValue(32));
            PdfNameTable<QpdfObject> filters = Table(); Add(filters, "StdCF", Complete(filter));
            PdfNameTable<QpdfObject> root = Table();
            Add(root, "Filter", Name("Standard")); Add(root, "V", PdfInteger.GetSharedValue(5));
            Add(root, "R", PdfInteger.GetSharedValue(6));
            memory.Reserve(136L); // Two integer objects and their bounded decimal text.
            Add(root, "Length", new PdfInteger("256", 256));
            Add(root, "P", new PdfInteger(permissions.ToString(CultureInfo.InvariantCulture), permissions));
            Add(root, "EncryptMetadata", metadata ? PdfBoolean.True : PdfBoolean.False);
            Add(root, "CF", Complete(filters));
            PdfName selected = Name("StdCF"); Add(root, "StmF", selected); Add(root, "StrF", selected);
            Add(root, "U", Bytes(values[..48])); Add(root, "O", Bytes(values.Slice(48, 48)));
            Add(root, "UE", Bytes(values.Slice(96, 32))); Add(root, "OE", Bytes(values.Slice(128, 32)));
            Add(root, "Perms", Bytes(values.Slice(160, 16)));
            PdfDictionary dictionary = Complete(root);
            governor.CheckCancellation(); return new(memory, key, dictionary, metadata);

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
        catch { if (key is not null) { CryptographicOperations.ZeroMemory(key); } memory.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(values); }
    }

    internal byte[] Encrypt(ReadOnlySpan<byte> value, ParserMemoryScope owner, ResourceGovernor governor)
    {
        ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(OutputSecurity));
        memory.CheckOwner(governor);
        byte[] key = _key ?? throw new ObjectDisposedException(nameof(OutputSecurity));
        return PdfAesCipher.Encrypt(value, key, owner, governor);
    }

    internal QpdfObject EncryptObject(QpdfObject value, bool rootMetadata, IIndirectResolver resolver,
        ParserMemoryScope destination, ResourceGovernor governor)
    {
        ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(OutputSecurity));
        memory.CheckOwner(governor); destination.CheckOwner(governor); governor.CheckCancellation();
        byte[] key = _key ?? throw new ObjectDisposedException(nameof(OutputSecurity));
        if (ReferenceEquals(value, Dictionary) || value is not (PdfByteString or PdfArray or PdfDictionary or PdfStream)) { return value; }
        if (value is PdfStream xref && PdfStringCrypt.HasType(xref.Dictionary, "XRef"u8, governor)) { return value; }
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
            PdfDictionary header = PdfStringCrypt.Dictionary(stream.Dictionary, graph, 0, key, true);
            if (identity)
            {
                graph.Memory.Reserve(32L); graph.Add(32L); result = stream.WithDictionary(header);
            }
            else
            {
                byte[] bytes = PdfStringCrypt.Bytes(stream.RawMemory.Span, graph, key, true);
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
        else { result = PdfStringCrypt.Value(value, graph, 0, key, true); }
        graph.Memory.TransferTo(destination, graph.Bytes);
        return result;
    }

    public void Dispose()
    {
        byte[]? key = _key; _key = null; _dictionary = null;
        ParserMemoryScope? memory = _memory; _memory = null;
        if (key is not null) { CryptographicOperations.ZeroMemory(key); }
        memory?.Dispose();
    }
}
