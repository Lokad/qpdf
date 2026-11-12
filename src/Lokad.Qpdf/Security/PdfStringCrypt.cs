using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Security;

// Transform only direct string values inside one physical object. References
// and object-stream membership belong to the reader/writer object boundary.
internal static class PdfStringCrypt
{
    internal static byte[] Bytes(ReadOnlySpan<byte> value, OwnedAllocationBatch graph, ReadOnlySpan<byte> key, bool encrypt)
    {
        byte[] bytes = encrypt ? PdfAesCipher.Encrypt(value, key, graph.Memory, graph.Governor)
            : PdfAesCipher.Decrypt(value, key, graph.Memory, graph.Governor);
        graph.Add(bytes.Length == 0 ? 0L : bytes.Length + GovernedBufferStream.ArrayOverheadBytes);
        return bytes;
    }

    internal static QpdfObject Value(QpdfObject value, OwnedAllocationBatch graph, int depth, ReadOnlySpan<byte> key, bool encrypt)
    {
        ResourceGovernor governor = graph.Governor; governor.CheckCancellation();
        if (depth > governor.MaxNestingDepth)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth, governor.MaxNestingDepth); }
        if (value is PdfByteString text)
        {
            byte[] bytes = Bytes(text.ValueSpan, graph, key, encrypt);
            graph.Memory.Reserve(32L); graph.Add(32L);
            // Hex output has a fixed length regardless of the randomized bytes.
            return PdfByteString.FromOwnedBytes(bytes, encrypt || text.Hex);
        }
        if (value is PdfDictionary dictionary) { return Dictionary(dictionary, graph, depth, key, encrypt); }
        if (value is not PdfArray array) { return value; }
        QpdfObject[]? items = null;
        for (int index = 0; index < array.Count; index++)
        {
            QpdfObject next = Value(array[index], graph, depth + 1, key, encrypt);
            if (items is null && !ReferenceEquals(next, array[index]))
            {
                long size = checked(56L + (long)array.Count * IntPtr.Size);
                graph.Memory.Reserve(size); graph.Add(size); items = new QpdfObject[array.Count];
                for (int prefix = 0; prefix < index; prefix++) { governor.NoteOperation(); items[prefix] = array[prefix]; }
            }
            if (items is not null) { items[index] = next; }
        }
        return items is null ? array : PdfArray.FromOwnedItems(items);
    }

    internal static PdfDictionary Dictionary(PdfDictionary dictionary, OwnedAllocationBatch graph, int depth, ReadOnlySpan<byte> key, bool encrypt)
    {
        ResourceGovernor governor = graph.Governor;
        bool signature = IsSignature(dictionary, governor);
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        using PdfDictionary.EntryEnumerator iterator = dictionary.GetEntryEnumerator();
        while (iterator.MoveNext(governor, graph.Token))
        {
            (PdfName name, QpdfObject value) = iterator.Current;
            bool contents = signature && name.RawSpan.SequenceEqual("Contents"u8) && value is PdfByteString { Hex: true };
            entries.Assign(graph.Memory, governor, name, contents ? value : Value(value, graph, depth + 1, key, encrypt), graph.Token);
        }
        return PdfDictionaryRebuild.Complete(graph, entries, allocation);
    }

    internal static bool IsSignature(PdfDictionary dictionary, ResourceGovernor governor)
    {
        if (HasType(dictionary, "Sig"u8, governor) || HasType(dictionary, "DocTimeStamp"u8, governor)) { return true; }
        // Type is optional; handler and byte-range shape distinguish signatures
        // from ordinary page Contents, which must still be transformed.
        return (!Get(dictionary, "Type"u8, governor, out QpdfObject type) || type is PdfNull)
            && Get(dictionary, "Filter"u8, governor, out QpdfObject filter) && filter is PdfName
            && Get(dictionary, "ByteRange"u8, governor, out QpdfObject range) && range is PdfArray;
    }

    internal static bool HasType(PdfDictionary dictionary, ReadOnlySpan<byte> type, ResourceGovernor governor)
        => Get(dictionary, "Type"u8, governor, out QpdfObject value) && value is PdfName name && name.RawSpan.SequenceEqual(type);

    private static bool Get(PdfDictionary dictionary, ReadOnlySpan<byte> name, ResourceGovernor governor, out QpdfObject value)
        => PdfDictionaryLookup.TryGet(dictionary, name, governor, governor.CancellationToken, out value);
}
