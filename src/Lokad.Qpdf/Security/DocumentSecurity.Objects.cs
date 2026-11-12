using System.Globalization;
using System.Security.Cryptography;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Security;

internal sealed partial class DocumentSecurity
{
    // Called only for physical indirect objects, never object-stream members.
    internal QpdfObject Decrypt(QpdfObject value, int objectNumber, int generation, bool rootMetadata,
        IIndirectResolver resolver, ParserMemoryScope destination)
    {
        CheckOwner(_governor); destination.CheckOwner(_governor);
        _governor.CheckCancellation();
        if (value is not (PdfByteString or PdfArray or PdfDictionary or PdfStream)) { return value; }
        if (value is PdfStream xref && HasType(xref.Dictionary, "XRef"u8)) { return value; }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(_governor, _governor.CancellationToken);
        Span<byte> derived = stackalloc byte[16];
        try
        {
            scoped ReadOnlySpan<byte> key = _key ?? throw new ObjectDisposedException(nameof(DocumentSecurity));
            if (Revision == 4)
            { Revision4Encryption.DeriveObjectKey(key, objectNumber, generation, derived, _governor); key = derived; }
            return DecryptObject(value, rootMetadata, resolver, destination, graph, key);
        }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }

    private QpdfObject DecryptObject(QpdfObject value, bool rootMetadata, IIndirectResolver resolver,
        ParserMemoryScope destination, OwnedAllocationBatch graph, ReadOnlySpan<byte> key)
    {
        QpdfObject result;
        if (value is PdfStream stream)
        {
            bool embedded = TryGet(stream.Dictionary, "Type"u8, out QpdfObject rawType)
                && Resolve(rawType, resolver) is PdfName type && type.RawSpan.SequenceEqual("EmbeddedFile"u8);
            PdfCipher cipher = embedded ? _files : _streams;
            if (rootMetadata && !EncryptMetadata) { cipher = PdfCipher.Identity; }
            PdfDictionary header = DecryptDictionary(stream.Dictionary, graph, 0, key);
            header = ReadStreamCipher(header, resolver, embedded, graph, ref cipher);
            if (FilterChain.HasExternalData(stream, resolver, _governor, graph.Token)) { cipher = PdfCipher.Identity; }
            if (cipher == PdfCipher.Identity)
            {
                graph.Memory.Reserve(32L); graph.Add(32L);
                result = stream.WithDictionary(header);
            }
            else
            {
                byte[] bytes = DecryptBytes(stream.RawMemory.Span, graph, key);
                PdfInteger length;
                if (bytes.Length < PdfInteger.SharedValueCount) { length = PdfInteger.GetSharedValue(bytes.Length); }
                else
                {
                    graph.Memory.Reserve(88L); graph.Add(88L);
                    length = new(bytes.Length.ToString(CultureInfo.InvariantCulture), bytes.Length);
                }
                header = PdfDictionaryRebuild.ReplaceEntry(graph, header, "Length"u8, length);
                graph.Memory.Reserve(32L); graph.Add(32L);
                result = PdfStream.FromOwnedData(header, bytes);
            }
        }
        else { result = DecryptValue(value, graph, 0, key); }
        graph.Memory.TransferTo(destination, graph.Bytes);
        return result;
    }

    private byte[] DecryptBytes(ReadOnlySpan<byte> value, OwnedAllocationBatch graph, ReadOnlySpan<byte> key)
    {
        byte[] bytes = PdfAesCipher.Decrypt(value, key, graph.Memory, _governor);
        graph.Add(bytes.Length == 0 ? 0L : bytes.Length + GovernedBufferStream.ArrayOverheadBytes);
        return bytes;
    }

    private QpdfObject DecryptValue(QpdfObject value, OwnedAllocationBatch graph, int depth, ReadOnlySpan<byte> key)
    {
        _governor.CheckCancellation();
        if (depth > _governor.MaxNestingDepth)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth, _governor.MaxNestingDepth); }
        if (_strings == PdfCipher.Identity) { return value; }
        if (value is PdfByteString text)
        {
            byte[] bytes = DecryptBytes(text.ValueSpan, graph, key);
            graph.Memory.Reserve(32L); graph.Add(32L);
            return PdfByteString.FromOwnedBytes(bytes, text.Hex);
        }
        if (value is PdfDictionary dictionary) { return DecryptDictionary(dictionary, graph, depth, key); }
        if (value is not PdfArray array) { return value; }
        QpdfObject[]? items = null;
        for (int index = 0; index < array.Count; index++)
        {
            QpdfObject next = DecryptValue(array[index], graph, depth + 1, key);
            if (items is null && !ReferenceEquals(next, array[index]))
            {
                long size = checked(56L + (long)array.Count * IntPtr.Size);
                graph.Memory.Reserve(size); graph.Add(size);
                items = new QpdfObject[array.Count];
                for (int prefix = 0; prefix < index; prefix++)
                { _governor.NoteOperation(); items[prefix] = array[prefix]; }
            }
            if (items is not null) { items[index] = next; }
        }
        return items is null ? array : PdfArray.FromOwnedItems(items);
    }

    private PdfDictionary DecryptDictionary(PdfDictionary dictionary, OwnedAllocationBatch graph, int depth, ReadOnlySpan<byte> key)
    {
        if (_strings == PdfCipher.Identity) { return dictionary; }
        bool signature = IsSignature(dictionary);
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        using PdfDictionary.EntryEnumerator iterator = dictionary.GetEntryEnumerator();
        while (iterator.MoveNext(_governor, graph.Token))
        {
            (PdfName name, QpdfObject value) = iterator.Current;
            bool contents = signature && name.RawSpan.SequenceEqual("Contents"u8) && value is PdfByteString { Hex: true };
            entries.Assign(graph.Memory, _governor, name, contents ? value : DecryptValue(value, graph, depth + 1, key), graph.Token);
        }
        return PdfDictionaryRebuild.Complete(graph, entries, allocation);
    }

    private bool IsSignature(PdfDictionary dictionary)
    {
        if (HasType(dictionary, "Sig"u8) || HasType(dictionary, "DocTimeStamp"u8)) { return true; }
        // Type is optional in signature dictionaries. The required signature
        // handler and byte-range shape distinguish them from page Contents.
        return (!TryGet(dictionary, "Type"u8, out QpdfObject type) || type is PdfNull)
            && TryGet(dictionary, "Filter"u8, out QpdfObject filter) && filter is PdfName
            && TryGet(dictionary, "ByteRange"u8, out QpdfObject range) && range is PdfArray;
    }

    private bool HasType(PdfDictionary dictionary, ReadOnlySpan<byte> type)
        => TryGet(dictionary, "Type"u8, out QpdfObject value) && value is PdfName name && name.RawSpan.SequenceEqual(type);

    private bool TryGet(PdfDictionary dictionary, ReadOnlySpan<byte> name, out QpdfObject value)
        => PdfDictionaryLookup.TryGet(dictionary, name, _governor, _governor.CancellationToken, out value);
}
