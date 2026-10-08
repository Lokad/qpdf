using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Runtime;

/// <summary>Builds fresh dictionary storage while borrowing original keys and values.</summary>
internal static class PdfDictionaryRebuild
{
    internal static PdfNameTable<QpdfObject> CreateEntries(OwnedAllocationBatch graph, out int allocation)
    {
        PdfNameTable<QpdfObject> entries = PdfNameTable<QpdfObject>.Create(graph.Memory, graph.Governor, IntPtr.Size, graph.Token);
        allocation = graph.Add(entries, entries.StorageBytes); return entries;
    }

    internal static PdfDictionary Complete(OwnedAllocationBatch graph, PdfNameTable<QpdfObject> entries, int allocation)
    {
        graph.UpdateBytes(allocation, entries.StorageBytes);
        entries.Freeze(graph.Memory, graph.Governor, graph.Token);
        graph.Memory.Reserve(24L);
        PdfDictionary dictionary = PdfDictionary.FromOwnedEntries(entries);
        graph.Add(dictionary, 24L); return dictionary;
    }

    internal static PdfDictionary ReplaceEntry(OwnedAllocationBatch graph, PdfDictionary source, ReadOnlySpan<byte> rawKey, QpdfObject replacement)
    {
        PdfNameTable<QpdfObject> entries = CreateEntries(graph, out int allocation);
        bool replaced = false;
        using PdfDictionary.EntryEnumerator sourceEntries = source.GetEntryEnumerator();
        while (sourceEntries.MoveNext(graph.Governor, graph.Token))
        {
            PdfName key = sourceEntries.Current.Key; QpdfObject value = sourceEntries.Current.Value;
            if (value is null) { continue; }
            if (PdfDictionaryLookup.NameEquals(key, rawKey, graph.Governor, graph.Token))
            { value = replacement; replaced = true; }
            entries.Assign(graph.Memory, graph.Governor, key, value, graph.Token);
        }
        if (!replaced)
        {
            // The new name and its bytes can escape independently. Admit both
            // before allocation, without creating a temporary display string.
            PdfName key = CreateName(graph, rawKey);
            entries.Assign(graph.Memory, graph.Governor, key, replacement, graph.Token);
        }
        return Complete(graph, entries, allocation);
    }

    internal static PdfName CreateName(OwnedAllocationBatch graph, ReadOnlySpan<byte> value)
    {
        graph.Token.ThrowIfCancellationRequested(); graph.Governor.CheckCancellation();
        long bytes = checked(32L + value.Length);
        graph.Memory.Reserve(bytes); graph.Governor.ChargeWork(value.Length);
        graph.Token.ThrowIfCancellationRequested(); graph.Governor.CheckCancellation();
        byte[] raw = new byte[value.Length];
        GovernedBufferStream.CopyBytes(value, raw, graph.Governor, graph.Token, CancellationToken.None);
        graph.Add(raw, bytes); graph.Memory.Reserve(24L);
        PdfName name = PdfName.FromOwnedBytes(raw); graph.Add(name, 24L); return name;
    }

    internal static PdfDictionary StripStreamEncoding(OwnedAllocationBatch graph, PdfDictionary source)
    {
        PdfNameTable<QpdfObject> entries = CreateEntries(graph, out int allocation);
        using PdfDictionary.EntryEnumerator sourceEntries = source.GetEntryEnumerator();
        while (sourceEntries.MoveNext(graph.Governor, graph.Token))
        {
            PdfName key = sourceEntries.Current.Key;
            if (PdfDictionaryLookup.NameEquals(key, "Filter"u8, graph.Governor, graph.Token) ||
                PdfDictionaryLookup.NameEquals(key, "DecodeParms"u8, graph.Governor, graph.Token) ||
                PdfDictionaryLookup.NameEquals(key, "Length"u8, graph.Governor, graph.Token)) { continue; }
            QpdfObject value = sourceEntries.Current.Value;
            if (value is not null) { entries.Assign(graph.Memory, graph.Governor, key, value, graph.Token); }
        }
        return Complete(graph, entries, allocation);
    }
}
