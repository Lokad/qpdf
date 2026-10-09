using System.Collections.ObjectModel;

namespace Lokad.Qpdf.Runtime;

/// <summary>Admits independently escaping selection arrays and readonly views.</summary>
internal static class SelectionResultStorage
{
    internal static IReadOnlyList<T> Copy<T>(OwnedAllocationBatch storage, IReadOnlyList<T> source, long itemBytes)
    {
        if (source.Count == 0) { return Array.Empty<T>(); }
        long bytes = checked((24L + itemBytes * source.Count + 7L) & ~7L);
        storage.Memory.Reserve(bytes);
        T[] entries = new T[source.Count];
        for (int index = 0; index < entries.Length; index++)
        {
            storage.Token.ThrowIfCancellationRequested();
            storage.Governor.CheckCancellation();
            storage.Governor.ChargeWork(1L);
            entries[index] = source[index];
        }
        storage.Add(entries, bytes);
        return View(storage, entries);
    }

    internal static IReadOnlyList<T> Single<T>(OwnedAllocationBatch storage, T value, long itemBytes)
    {
        long bytes = checked((24L + itemBytes + 7L) & ~7L);
        storage.Memory.Reserve(bytes);
        T[] entries = [value];
        storage.Add(entries, bytes);
        return View(storage, entries);
    }

    private static ReadOnlyCollection<T> View<T>(OwnedAllocationBatch storage, T[] entries)
    {
        storage.Memory.Reserve(24L);
        ReadOnlyCollection<T> view = Array.AsReadOnly(entries);
        storage.Add(view, 24L);
        return view;
    }

    internal static void AdoptMappings(OwnedAllocationBatch storage, ParserMemoryScope source,
        IReadOnlyList<PdfPageSelectionMapping> mapping)
    {
        source.TransferTo(storage.Memory, checked(24L * mapping.Count));
        foreach (PdfPageSelectionMapping entry in mapping) { storage.Add(entry, 24L); }
    }
}
