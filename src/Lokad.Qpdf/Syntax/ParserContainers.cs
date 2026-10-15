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

    internal static List<T> CreateList<T>(ParserMemoryScope memory)
    {
        memory.Reserve(32L);
        return new List<T>();
    }

    internal static void AddItem(ParserMemoryScope memory, List<QpdfObject> items, QpdfObject value)
        => AddItem(memory, items, value, IntPtr.Size);

    internal static void AddOwnedItem(ParserMemoryScope memory, List<ParsedValue> items, ParsedValue value)
        => AddItem(memory, items, value, 16L);

    internal static void AddItem<T>(ParserMemoryScope memory, List<T> items, T value, long itemBytes)
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

    internal static PdfNameTable<ParsedValue> CreateDictionary(ParserMemoryScope memory, ResourceGovernor governor)
        => PdfNameTable<ParsedValue>.Create(memory, governor, 16L, governor.CancellationToken);

    internal static Dictionary<TKey, TValue> CreateDictionary<TKey, TValue>(ParserMemoryScope memory) where TKey : notnull
    {
        memory.Reserve(96L);
        return new Dictionary<TKey, TValue>();
    }

    internal static void EnsureEntryCapacity<TKey, TValue>(
        ParserMemoryScope memory, Dictionary<TKey, TValue> entries, long valueBytes) where TKey : notnull
        => EnsureEntryCapacity(memory, entries, valueBytes, Array.MaxLength);

    internal static void EnsureEntryCapacity<TKey, TValue>(ParserMemoryScope memory,
        Dictionary<TKey, TValue> entries, long valueBytes, int maximumCount) where TKey : notnull
    {
        int previous = entries.EnsureCapacity(0);
        if (entries.Count < previous) { return; }
        int required = checked(entries.Count + 1);
        (int target, long upper) = ReserveGrowth(memory, previous, required,
            checked(12L + IntPtr.Size + valueBytes), maximumCount);
        ResizeEntryCapacity(memory, entries, valueBytes, previous, target, upper);
    }

    // Default hints apply only to empty destinations. Validated cache fills
    // and parsed union lower bounds may opt in; tight jobs retain normal growth.
    internal static void HintEntryCapacity<TKey, TValue>(ParserMemoryScope memory,
        Dictionary<TKey, TValue> entries, int count, long valueBytes, ResourceGovernor governor) where TKey : notnull
        => HintEntryCapacity(memory, entries, count, valueBytes, governor, false);

    internal static void HintEntryCapacity<TKey, TValue>(ParserMemoryScope memory,
        Dictionary<TKey, TValue> entries, int count, long valueBytes, ResourceGovernor governor,
        bool allowPopulated) where TKey : notnull
    {
        memory.CheckOwner(governor); governor.CheckCancellation();
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        int previous = entries.EnsureCapacity(0);
        if ((!allowPopulated && entries.Count != 0) || count <= previous) { return; }
        int allowance = PrimeCapacityAllowance(count);
        long upper = DictionaryArrays(allowance, valueBytes);
        if (upper > governor.RemainingMemoryBytes / 4L
            || allowance > governor.RemainingWorkUnits / 4L) { return; }
        memory.Reserve(upper);
        ResizeEntryCapacity(memory, entries, valueBytes, previous, count, upper);
    }

    private static void ResizeEntryCapacity<TKey, TValue>(ParserMemoryScope memory,
        Dictionary<TKey, TValue> entries, long valueBytes, int previous, int target, long upper) where TKey : notnull
    {
        // The pinned .NET 10 prime table fits this conservative allowance.
        // EnsureCapacity reports the actual storage; release excess admission.
        int capacity;
        try { capacity = entries.EnsureCapacity(target); }
        catch { memory.Release(upper); throw; }
        memory.Release(checked(upper - DictionaryArrays(capacity, valueBytes) + DictionaryArrays(previous, valueBytes)));
    }

    internal static HashSet<T> CreateSet<T>(ParserMemoryScope memory)
    {
        memory.Reserve(96L);
        return new HashSet<T>();
    }

    internal static bool AddSetItem<T>(ParserMemoryScope memory, HashSet<T> items, T value, long itemBytes)
    {
        if (items.Contains(value)) { return false; }
        EnsureSetCapacity(memory, items, itemBytes);
        return items.Add(value);
    }

    internal static void EnsureSetCapacity<T>(ParserMemoryScope memory, HashSet<T> items, long itemBytes)
    {
        int previous = items.EnsureCapacity(0);
        if (items.Count == previous)
        {
            (int target, long upper) = ReserveGrowth(memory, previous, checked(items.Count + 1), checked(12L + itemBytes));
            int capacity;
            try { capacity = items.EnsureCapacity(target); }
            catch { memory.Release(upper); throw; }
            memory.Release(checked(upper - SetArrays(capacity, itemBytes) + SetArrays(previous, itemBytes)));
        }
    }

    internal static PdfDictionary CopyDictionary(ParserMemoryScope memory, ResourceGovernor governor,
        PdfNameTable<ParsedValue> entries, bool preserveReceipts)
    {
        CancellationToken token = governor.CancellationToken;
        if (!preserveReceipts && entries.TryMoveValues(memory, governor, IntPtr.Size,
            static value => value.Value, token, out PdfNameTable<QpdfObject>? moved))
        {
            memory.Reserve(32L);
            return PdfDictionary.FromOwnedEntries(moved);
        }
        PdfNameTable<QpdfObject> kept = PdfNameTable<QpdfObject>.Create(memory, governor, IntPtr.Size, token);
        kept.EnsureCapacity(memory, governor, entries.Count, token);
        using PdfNameTable<ParsedValue>.Enumerator source = entries.GetEnumerator();
        while (source.MoveNext(governor, token))
        { kept.Assign(memory, governor, source.Current.Key, source.Current.Value.Value, token); }
        kept.Freeze(memory, governor, token); memory.Reserve(32L);
        PdfDictionary value = PdfDictionary.FromOwnedEntries(kept);
        if (preserveReceipts) { entries.Freeze(memory, governor, token); }
        else { entries.Discard(memory, governor); }
        return value;
    }

    private static long ArrayBytes(int capacity, long itemBytes) => capacity == 0 ? 0L : 32L + itemBytes * capacity;

    // Explicit admission bypasses automatic resizing; request geometric growth
    // rather than the next small prime after Count + 1.
    private static int GrowthCapacity(int previous, int required)
    {
        if (required > Array.MaxLength) { throw new QpdfParseException("Collection exceeds the supported capacity.", null, null); }
        return (int)Math.Min(Array.MaxLength, Math.Max(required, previous * 2L));
    }

    private static (int Target, long Upper) ReserveGrowth(ParserMemoryScope memory, int previous, int required, long slotBytes)
        => ReserveGrowth(memory, previous, required, slotBytes, Array.MaxLength);

    private static (int Target, long Upper) ReserveGrowth(ParserMemoryScope memory, int previous,
        int required, long slotBytes, int maximumCount)
    {
        // A validated cardinality can cap geometric overshoot without eagerly
        // allocating for objects which an operation never reaches.
        int target = Math.Max(required, Math.Min(GrowthCapacity(previous, required), maximumCount));
        long upper = CollectionArrays(PrimeCapacityAllowance(target), slotBytes);
        try { memory.Reserve(upper); }
        catch (QpdfLimitException failure) when (target > required && failure.LimitName == nameof(QpdfLimits.MaxMemoryBytes))
        {
            // A denied reservation has not allocated or changed this owner.
            // Preserve bounded callers that can fund the smaller growth step.
            target = required;
            upper = CollectionArrays(PrimeCapacityAllowance(target), slotBytes);
            memory.Reserve(upper);
        }
        return (target, upper);
    }

    private static int PrimeCapacityAllowance(int required) => (int)Math.Min(Array.MaxLength, required * 3L);

    // One pointer-width key slot also covers a padded int key before a 24-byte
    // xref value on the supported 64-bit runtimes. Wrappers/arrays include
    // conservative header allowances; actual prime capacities are reconciled.
    private static long DictionaryArrays(int capacity, long valueBytes) => CollectionArrays(capacity, checked(12L + IntPtr.Size + valueBytes));

    private static long SetArrays(int capacity, long itemBytes) => CollectionArrays(capacity, checked(12L + itemBytes));

    private static long CollectionArrays(int capacity, long slotBytes) => capacity == 0 ? 0L : checked(64L + slotBytes * capacity);
}
