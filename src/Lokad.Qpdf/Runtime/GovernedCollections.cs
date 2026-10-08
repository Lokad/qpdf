using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Runtime;

/// <summary>Admits comparer owners and bounds sorting/set-growth work.</summary>
internal static class GovernedCollections
{
    internal static bool AddNumber(ParserMemoryScope memory, ResourceGovernor governor, GovernedIntSet items, int number)
    {
        memory.CheckOwner(governor);
        items.CheckOwner(governor);
        return items.Add(memory, number);
    }

    internal static void Assign<T>(ParserMemoryScope memory, ResourceGovernor governor,
        GovernedIntDictionary<T> entries, int number, T value)
    {
        memory.CheckOwner(governor);
        entries.CheckOwner(governor);
        entries.Assign(memory, number, value);
    }

    internal static void AddItem<T>(ParserMemoryScope memory, ResourceGovernor governor,
        List<T> items, T value, long itemBytes)
    {
        memory.CheckOwner(governor);
        governor.CheckCancellation();
        governor.ChargeWork(1L);
        if (items.Count == items.Capacity) { governor.ChargeWork(checked(2L * items.Count)); }
        ParserContainers.AddItem(memory, items, value, itemBytes);
    }

    internal static void Assign<T>(ParserMemoryScope memory, ResourceGovernor governor,
        Dictionary<int, T> entries, int number, T value, long valueBytes)
    {
        memory.CheckOwner(governor);
        governor.CheckCancellation();
        governor.ChargeWork(2L);
        if (!entries.ContainsKey(number))
        {
            if (entries.Count == entries.EnsureCapacity(0)) { governor.ChargeWork(checked(4L * (entries.Count + 1L))); }
            ParserContainers.EnsureEntryCapacity(memory, entries, valueBytes);
        }
        entries[number] = value;
    }

    // Borrow dictionary entries directly. Literal-key lookup needs no temporary
    // PdfName, boxed key enumerator, or display string for unknown names.
    internal static bool TryGet(PdfDictionary dictionary, ReadOnlySpan<byte> key,
        ResourceGovernor governor, CancellationToken cancellationToken, out QpdfObject value)
        => PdfDictionaryLookup.TryGet(dictionary, key, governor, cancellationToken, out value);

    internal static bool AddNumber(ParserMemoryScope memory, ResourceGovernor governor, HashSet<int> items, int number)
    {
        memory.CheckOwner(governor);
        governor.ChargeWork(2L);
        if (items.Contains(number)) { return false; }
        if (items.Count == items.EnsureCapacity(0)) { governor.ChargeWork(checked(4L * (items.Count + 1L))); }
        return ParserContainers.AddSetItem(memory, items, number, sizeof(int));
    }

    internal static void SortNumbers(ParserMemoryScope memory, ResourceGovernor governor, List<int> items)
    {
        memory.CheckOwner(governor);
        memory.Reserve(32L);
        try { Sort(items, new NumberComparer(governor)); }
        finally { memory.Release(32L); }
    }

    internal static void SortNames(ParserMemoryScope memory, ResourceGovernor governor, List<PdfName> items)
    {
        memory.CheckOwner(governor);
        memory.Reserve(32L);
        try { Sort(items, new NameComparer(governor)); }
        finally { memory.Release(32L); }
    }

    private static void Sort<T>(List<T> items, IComparer<T> comparer)
    {
        try { items.Sort(comparer); }
        catch (InvalidOperationException ex) when (ex.InnerException is QpdfException or OperationCanceledException)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw();
        }
    }

    private sealed class NumberComparer(ResourceGovernor governor) : IComparer<int>
    {
        public int Compare(int left, int right)
        {
            governor.CheckCancellation();
            governor.ChargeWork(1L);
            return left.CompareTo(right);
        }
    }

    private sealed class NameComparer(ResourceGovernor governor) : IComparer<PdfName>
    {
        public int Compare(PdfName? left, PdfName? right)
        {
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);
            return CompareNames(governor, left, right);
        }
    }

    // Match ordinal DisplayText ordering using the shared escape rule, without
    // constructing either display string or any per-byte temporary strings.
    internal static int CompareNames(ResourceGovernor governor, PdfName left, PdfName right)
        => PdfNameDisplay.Compare(left, right, governor, CancellationToken.None);
}
