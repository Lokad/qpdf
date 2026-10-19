namespace Lokad.Qpdf.Runtime;

/// <summary>Shared in-place heapsort with value-type comparisons and bounded scratch.</summary>
internal static class GovernedSort
{
    internal static void Sort<T, TComparer>(Span<T> items, TComparer comparer, ResourceGovernor governor,
        CancellationToken cancellationToken) where TComparer : struct, IComparer<T>
    {
        for (int root = items.Length / 2 - 1; root >= 0; root--)
        {
            SiftDown(items, root, items.Length, comparer, governor, cancellationToken);
        }
        for (int end = items.Length - 1; end > 0; end--)
        {
            Swap(items, 0, end, governor, cancellationToken);
            SiftDown(items, 0, end, comparer, governor, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
    }

    private static void SiftDown<T, TComparer>(Span<T> items, int root, int count, TComparer comparer,
        ResourceGovernor governor, CancellationToken token) where TComparer : struct, IComparer<T>
    {
        while (root < count / 2)
        {
            int child = checked(2 * root + 1);
            if (child + 1 < count && Compare(items[child], items[child + 1], comparer, governor, token) < 0) { child++; }
            if (Compare(items[root], items[child], comparer, governor, token) >= 0) { return; }
            Swap(items, root, child, governor, token);
            root = child;
        }
    }

    private static int Compare<T, TComparer>(T left, T right, TComparer comparer, ResourceGovernor governor,
        CancellationToken token) where TComparer : struct, IComparer<T>
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        return comparer.Compare(left, right);
    }

    private static void Swap<T>(Span<T> items, int left, int right, ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        (items[left], items[right]) = (items[right], items[left]);
    }
}
