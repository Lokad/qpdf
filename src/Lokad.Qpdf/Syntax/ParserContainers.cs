using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Syntax;

/// <summary>Admits parser container capacities and immutable copies before allocation.</summary>
internal static class ParserContainers
{
    internal static List<QpdfObject> CreateList(ParserMemoryScope memory)
    {
        memory.Reserve(32L);
        return new List<QpdfObject>();
    }

    internal static void AddItem(ParserMemoryScope memory, List<QpdfObject> items, QpdfObject value)
    {
        if (items.Count == items.Capacity)
        {
            int capacity = (int)Math.Min(Array.MaxLength, Math.Max(4L, items.Capacity * 2L));
            if (capacity <= items.Count)
            {
                throw new QpdfParseException("Array exceeds the supported capacity.", null, null);
            }
            long previous = ArrayBytes(items.Capacity);
            memory.Reserve(ArrayBytes(capacity));
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
        memory.Release(32L + ArrayBytes(items.Capacity));
    }

    internal static Dictionary<PdfName, QpdfObject> CreateDictionary(ParserMemoryScope memory)
    {
        memory.Reserve(96L);
        return new Dictionary<PdfName, QpdfObject>();
    }

    internal static void EnsureEntryCapacity(ParserMemoryScope memory, Dictionary<PdfName, QpdfObject> entries)
    {
        int previous = entries.EnsureCapacity(0);
        if (entries.Count < previous) { return; }
        int required = checked(entries.Count + 1);
        long upper = DictionaryArrays(PrimeCapacityAllowance(required));
        // The pinned .NET 10 prime table fits this conservative allowance.
        // EnsureCapacity reports the actual storage; release excess admission.
        memory.Reserve(upper);
        int capacity = entries.EnsureCapacity(required);
        memory.Release(checked(upper - DictionaryArrays(capacity) + DictionaryArrays(previous)));
    }

    internal static PdfDictionary CopyDictionary(ParserMemoryScope memory, Dictionary<PdfName, QpdfObject> entries)
    {
        long upper = DictionaryArrays(PrimeCapacityAllowance(entries.Count));
        memory.Reserve(128L + upper);
        PdfDictionary value = new(entries);
        memory.Release(checked(upper - DictionaryArrays(value.StorageCapacity) + 96L + DictionaryArrays(entries.EnsureCapacity(0))));
        return value;
    }

    private static long ArrayBytes(int capacity) => capacity == 0 ? 0L : 32L + IntPtr.Size * (long)capacity;

    private static int PrimeCapacityAllowance(int required) => (int)Math.Min(Array.MaxLength, required * 3L);

    private static long DictionaryArrays(int capacity) => capacity == 0 ? 0L : checked(64L + (12L + 2L * IntPtr.Size) * capacity);
}
