using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Content;

/// <summary>Builds fresh resource containers while borrowing immutable keys, values and payloads.</summary>
internal static class ResourceRebuild
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

    internal static PdfDictionary ReplaceResources(OwnedAllocationBatch graph, PdfDictionary source, PdfDictionary resources)
    {
        PdfNameTable<QpdfObject> entries = CreateEntries(graph, out int allocation);
        bool replaced = false;
        using PdfDictionary.EntryEnumerator sourceEntries = source.GetEntryEnumerator();
        while (sourceEntries.MoveNext(graph.Governor, graph.Token))
        {
            PdfName key = sourceEntries.Current.Key; QpdfObject value = sourceEntries.Current.Value;
            if (value is null) { continue; }
            if (PdfDictionaryLookup.NameEquals(key, "Resources"u8, graph.Governor, graph.Token))
            { value = resources; replaced = true; }
            entries.Assign(graph.Memory, graph.Governor, key, value, graph.Token);
        }
        if (!replaced)
        {
            // This header can be synthetic or inherit its resources. Each new
            // escaping name allocation gets its own receipt; no temporary copy.
            graph.Memory.Reserve(41L); byte[] raw = "Resources"u8.ToArray(); graph.Add(raw, 41L);
            graph.Memory.Reserve(24L); PdfName key = PdfName.FromOwnedBytes(raw); graph.Add(key, 24L);
            entries.Assign(graph.Memory, graph.Governor, key, resources, graph.Token);
        }
        return Complete(graph, entries, allocation);
    }

    internal static PdfStream ReplaceResources(OwnedAllocationBatch graph, PdfStream source, PdfDictionary resources)
    {
        PdfDictionary dictionary = ReplaceResources(graph, source.Dictionary, resources);
        graph.Memory.Reserve(32L); PdfStream stream = source.WithDictionary(dictionary);
        graph.Add(stream, 32L); return stream;
    }
}
