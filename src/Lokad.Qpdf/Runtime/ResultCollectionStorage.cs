using System.Collections.ObjectModel;

namespace Lokad.Qpdf.Runtime;

/// <summary>Admits copied result arrays and readonly views under their operation or retained owner.</summary>
internal static class ResultCollectionStorage
{
    internal static IReadOnlyList<T> Copy<T>(OwnedAllocationBatch storage, IReadOnlyList<T> source, long itemBytes)
    {
        if (source.Count == 0) { return Array.Empty<T>(); }
        T[] entries = CopyItems(storage.Memory, source, itemBytes, storage.Governor, storage.Token);
        storage.Add(entries, ArrayBytes(source.Count, itemBytes));
        ReadOnlyCollection<T> view = View(storage.Memory, entries);
        storage.Add(view, 24L);
        return view;
    }

    internal static IReadOnlyList<T> Copy<T>(ParserMemoryScope memory, IReadOnlyList<T> source, long itemBytes,
        ResourceGovernor governor, CancellationToken token)
    {
        memory.CheckOwner(governor);
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (source.Count == 0) { return Array.Empty<T>(); }
        return View(memory, CopyItems(memory, source, itemBytes, governor, token));
    }

    private static T[] CopyItems<T>(ParserMemoryScope memory, IReadOnlyList<T> source, long itemBytes,
        ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        memory.Reserve(ArrayBytes(source.Count, itemBytes));
        T[] entries = new T[source.Count];
        for (int index = 0; index < entries.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            governor.CheckCancellation();
            governor.ChargeWork(1L);
            entries[index] = source[index];
        }
        return entries;
    }

    internal static IReadOnlyList<T> Single<T>(ParserMemoryScope memory, T value, long itemBytes,
        ResourceGovernor governor, CancellationToken token)
    {
        memory.CheckOwner(governor);
        token.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(1L);
        memory.Reserve(ArrayBytes(1, itemBytes));
        T[] entries = [value];
        return View(memory, entries);
    }

    private static long ArrayBytes(int count, long itemBytes) => checked((24L + itemBytes * count + 7L) & ~7L);

    private static ReadOnlyCollection<T> View<T>(ParserMemoryScope memory, T[] entries)
    {
        memory.Reserve(24L);
        return Array.AsReadOnly(entries);
    }
}
