using System.Globalization;
using System.Runtime.InteropServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

internal readonly record struct PrintPageRanges(PdfDictionary Preferences, List<(int First, int Last)>? Ranges)
{
    internal static readonly PdfName ViewerPreferences = PdfName.FromAscii("ViewerPreferences");

    internal static PrintPageRanges? Read(QpdfObject? raw, IIndirectResolver resolver, int total,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        PdfDictionary? preferences = CatalogCopy.ResolveDictionary(raw, resolver, governor, token);
        if (preferences is null
            || !PdfDictionaryLookup.TryGet(preferences, "PrintPageRange"u8, governor, token, out QpdfObject value))
        { return null; }

        if (CatalogCopy.ResolveValue(value, resolver, governor, token) is not PdfArray array || (array.Count & 1) != 0)
        { return new(preferences, null); }

        List<(int First, int Last)> ranges = ParserContainers.CreateList<(int, int)>(memory, array.Count / 2, 8L);
        for (int index = 0; index < array.Count; index += 2)
        {
            if (CatalogCopy.ResolveValue(array[index], resolver, governor, token) is not PdfInteger first
                || CatalogCopy.ResolveValue(array[index + 1], resolver, governor, token) is not PdfInteger last
                || first.Value < 1 || first.Value > last.Value || last.Value > total)
            { return new(preferences, null); }
            ranges.Add(((int)first.Value, (int)last.Value));
        }

        GovernedSort.Sort(CollectionsMarshal.AsSpan(ranges), new RangeOrder(), governor, token);
        int kept = 0;
        foreach ((int first, int last) in CollectionsMarshal.AsSpan(ranges))
        {
            governor.CheckCancellation();
            if (kept > 0 && first <= ranges[kept - 1].Last + 1L)
            {
                (int priorFirst, int priorLast) = ranges[kept - 1];
                ranges[kept - 1] = (priorFirst, Math.Max(priorLast, last));
            }
            else { ranges[kept++] = (first, last); }
        }
        ranges.RemoveRange(kept, ranges.Count - kept);
        return new(preferences, ranges);
    }

    internal static PdfDictionary? Project<T, TState>(QpdfObject? raw, IIndirectResolver resolver, int total,
        IReadOnlyList<T> occurrences, TState state, Func<T, TState, int> sourcePage,
        QpdfLimits limits, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        if (raw is null) { return null; }
        if (occurrences.Count == total)
        {
            int index = 0;
            for (; index < occurrences.Count; index++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (sourcePage(occurrences[index], state) != index + 1) { break; }
            }
            if (index == occurrences.Count) { return null; }
        }

        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        if (Read(raw, resolver, total, scratch, governor, token) is not PrintPageRanges preset) { return null; }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        List<QpdfObject> result = ParserContainers.CreateList(scratch);
        if (preset.Ranges is { Count: > 0 } ranges)
        {
            int first = 0;
            for (int index = 0; index < occurrences.Count; index++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                int page = sourcePage(occurrences[index], state);
                int candidate = LowerBound(ranges, page, governor, token);
                if (candidate < ranges.Count && page >= ranges[candidate].First)
                { if (first == 0) { first = index + 1; } }
                else if (first != 0) { AddRange(result, first, index, limits, scratch, graph); first = 0; }
            }
            if (first != 0) { AddRange(result, first, occurrences.Count, limits, scratch, graph); }
        }
        PdfDictionary projected = preset.ReplaceRange(result, graph);
        graph.Memory.TransferTo(memory, graph.Bytes);
        return projected;
    }

    internal PdfDictionary Slice(int first, int last, QpdfLimits limits, ParserMemoryScope scratch, OwnedAllocationBatch graph)
    {
        List<QpdfObject> result = ParserContainers.CreateList(scratch);
        if (Ranges is { Count: > 0 } ranges)
        {
            for (int index = LowerBound(ranges, first, graph.Governor, graph.Token);
                index < ranges.Count && ranges[index].First <= last; index++)
            {
                (int start, int end) = ranges[index];
                AddRange(result, Math.Max(first, start) - first + 1, Math.Min(last, end) - first + 1,
                    limits, scratch, graph);
            }
        }
        return ReplaceRange(result, graph);
    }

    private PdfDictionary ReplaceRange(List<QpdfObject> result, OwnedAllocationBatch graph)
    {
        if (result.Count == 0) { return PdfDictionaryRebuild.RemoveEntry(graph, Preferences, "PrintPageRange"u8); }
        long bytes = checked(64L + IntPtr.Size * (long)result.Count);
        graph.Memory.Reserve(bytes);
        PdfArray replacement = new(result);
        graph.Add(bytes);
        return PdfDictionaryRebuild.ReplaceEntry(graph, Preferences, "PrintPageRange"u8, replacement);
    }

    private static int LowerBound(List<(int First, int Last)> ranges, int page, ResourceGovernor governor, CancellationToken token)
    {
        int low = 0, high = ranges.Count;
        while (low < high)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            int middle = low + (high - low) / 2;
            if (ranges[middle].Last < page) { low = middle + 1; }
            else { high = middle; }
        }
        return low;
    }

    private static void AddRange(List<QpdfObject> output, int first, int last, QpdfLimits limits,
        ParserMemoryScope scratch, OwnedAllocationBatch graph)
    {
        long count = output.Count + 2L;
        if (count > limits.MaxContainerSize)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), count, limits.MaxContainerSize); }
        graph.Memory.Reserve(160L);
        graph.Add(160L);
        ParserContainers.AddItem(scratch, output, new PdfInteger(first.ToString(CultureInfo.InvariantCulture), first));
        ParserContainers.AddItem(scratch, output, new PdfInteger(last.ToString(CultureInfo.InvariantCulture), last));
    }

    private readonly struct RangeOrder : IComparer<(int First, int Last)>
    {
        public int Compare((int First, int Last) left, (int First, int Last) right) => left.First.CompareTo(right.First);
    }
}
