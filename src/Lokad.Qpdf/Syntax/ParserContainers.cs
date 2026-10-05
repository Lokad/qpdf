using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Syntax;

/// <summary>Admits parser container capacities and immutable copies before allocation.</summary>
internal static class ParserContainers
{
    internal static List<QpdfObject> CreateList(ParserMemoryScope memory)
        => CreateList<QpdfObject>(memory);

    internal static List<ParsedValue> CreateOwnedList(ParserMemoryScope memory)
        => CreateList<ParsedValue>(memory);

    private static List<T> CreateList<T>(ParserMemoryScope memory)
    {
        memory.Reserve(32L);
        return new List<T>();
    }

    internal static void AddItem(ParserMemoryScope memory, List<QpdfObject> items, QpdfObject value)
        => AddItem(memory, items, value, IntPtr.Size);

    internal static void AddOwnedItem(ParserMemoryScope memory, List<ParsedValue> items, ParsedValue value)
        => AddItem(memory, items, value, 16L);

    private static void AddItem<T>(ParserMemoryScope memory, List<T> items, T value, long itemBytes)
    {
        if (items.Count == items.Capacity)
        {
            int capacity = (int)Math.Min(Array.MaxLength, Math.Max(4L, items.Capacity * 2L));
            if (capacity <= items.Count)
            {
                throw new QpdfParseException("Array exceeds the supported capacity.", null, null);
            }
            long previous = ArrayBytes(items.Capacity, itemBytes);
            memory.Reserve(ArrayBytes(capacity, itemBytes));
            items.Capacity = capacity;
            memory.Release(previous);
        }
        items.Add(value);
    }

    internal static PdfArray CopyArray(ParserMemoryScope memory, List<QpdfObject> items)
    {
        memory.Reserve(64L + IntPtr.Size * (long)items.Count);
        PdfArray value = new(items);
        ReleaseList(memory, items);
        return value;
    }

    internal static void ReleaseList(ParserMemoryScope memory, List<QpdfObject> items)
    {
        memory.Release(32L + ArrayBytes(items.Capacity, IntPtr.Size));
    }

    internal static void ReleaseOwnedList(ParserMemoryScope memory, List<ParsedValue> items)
    {
        memory.Release(32L + ArrayBytes(items.Capacity, 16L));
    }

    internal static Dictionary<PdfName, ParsedValue> CreateDictionary(ParserMemoryScope memory)
    {
        memory.Reserve(96L);
        return new Dictionary<PdfName, ParsedValue>();
    }

    internal static void EnsureEntryCapacity(ParserMemoryScope memory, Dictionary<PdfName, ParsedValue> entries)
    {
        int previous = entries.EnsureCapacity(0);
        if (entries.Count < previous) { return; }
        int required = checked(entries.Count + 1);
        long upper = DictionaryArrays(PrimeCapacityAllowance(required), 16L);
        // The pinned .NET 10 prime table fits this conservative allowance.
        // EnsureCapacity reports the actual storage; release excess admission.
        memory.Reserve(upper);
        int capacity = entries.EnsureCapacity(required);
        memory.Release(checked(upper - DictionaryArrays(capacity, 16L) + DictionaryArrays(previous, 16L)));
    }

    internal static PdfDictionary CopyDictionary(
        ParserMemoryScope memory, ResourceGovernor governor, Dictionary<PdfName, ParsedValue> entries, bool preserveReceipts)
    {
        long upper = DictionaryArrays(PrimeCapacityAllowance(entries.Count), IntPtr.Size);
        memory.Reserve(128L + upper);
        Dictionary<PdfName, QpdfObject> kept = new(entries.Count);
        foreach (KeyValuePair<PdfName, ParsedValue> entry in entries)
        {
            governor.CheckCancellation();
            kept.Add(entry.Key, entry.Value.Value);
        }
        PdfDictionary value = PdfDictionary.FromOwnedEntries(kept);
        long temporary = preserveReceipts ? 0L : checked(96L + DictionaryArrays(entries.EnsureCapacity(0), 16L));
        memory.Release(checked(upper - DictionaryArrays(value.StorageCapacity, IntPtr.Size) + temporary));
        return value;
    }

    private static long ArrayBytes(int capacity, long itemBytes) => capacity == 0 ? 0L : 32L + itemBytes * capacity;

    private static int PrimeCapacityAllowance(int required) => (int)Math.Min(Array.MaxLength, required * 3L);

    private static long DictionaryArrays(int capacity, long valueBytes) => capacity == 0 ? 0L : checked(64L + (12L + IntPtr.Size + valueBytes) * capacity);
}
