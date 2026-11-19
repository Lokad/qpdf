using System.Runtime.InteropServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Operations;

internal static class SplitAssembly
{
    internal static List<(int First, int Last)> PartitionGroups(int total, int groupSize,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        memory.CheckOwner(governor);
        List<(int First, int Last)> groups = ParserContainers.CreateList<(int, int)>(memory);
        for (int first = 1; first <= total;)
        {
            governor.CheckCancellation();
            int last = (int)Math.Min(total, (long)first + groupSize - 1L);
            ParserContainers.AddItem(memory, groups, (first, last), 8L);
            if (last == total) { break; }
            first = last + 1;
        }

        return groups;
    }

    internal static List<string> GenerateSplitNames(string pattern, int total, int groupSize,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        int width = 1;
        for (int remaining = total; remaining >= 10; remaining /= 10) { width++; }
        governor.CheckCancellation();
        int marker = pattern.IndexOf("%d", StringComparison.Ordinal);
        bool extension = marker < 0 && pattern.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        int prefix = marker >= 0 ? marker : extension ? pattern.Length - 4 : pattern.Length;
        int suffix = marker >= 0 ? marker + 2 : prefix;
        List<string> names = ParserContainers.CreateList<string>(memory);
        for (int first = 1; first <= total;)
        {
            governor.CheckCancellation();
            int last = (int)Math.Min(total, (long)first + groupSize - 1L);
            int length = checked(prefix + pattern.Length - suffix + (marker < 0 ? 1 : 0)
                + width + (first == last ? 0 : width + 1));
            if (length > QpdfPath.MaxLength)
            { throw new QpdfParseException("A split output name exceeds the path budget.", null, null); }
            memory.Reserve(GovernedText.StringBytes(length));
            string name = string.Create(length, (pattern, prefix, suffix, marker, first, last, width, governor),
                static (target, state) =>
                {
                    CopySplitNamePart(state.pattern.AsSpan(0, state.prefix), target, state.governor);
                    int position = state.prefix;
                    if (state.marker < 0) { target[position++] = '-'; }
                    WriteSplitNumber(target.Slice(position, state.width), state.first, state.governor);
                    position += state.width;
                    if (state.first != state.last)
                    {
                        target[position++] = '-';
                        WriteSplitNumber(target.Slice(position, state.width), state.last, state.governor);
                        position += state.width;
                    }
                    CopySplitNamePart(state.pattern.AsSpan(state.suffix), target[position..], state.governor);
                });
            ParserContainers.AddItem(memory, names, name, IntPtr.Size);
            if (last == total) { break; }
            first = last + 1;
        }
        return names;
    }

    internal static void CopySplitNamePart(ReadOnlySpan<char> source, Span<char> target, ResourceGovernor governor)
    {
        for (int offset = 0; offset < source.Length; offset += Math.Min(256, source.Length - offset))
        {
            int count = Math.Min(256, source.Length - offset);
            governor.CheckCancellation();
            source.Slice(offset, count).CopyTo(target.Slice(offset, count));
        }
    }

    internal static void WriteSplitNumber(Span<char> target, int value, ResourceGovernor governor)
    {
        governor.CheckCancellation();
        target.Fill('0');
        int digits = 1;
        for (int remaining = value; remaining >= 10; remaining /= 10) { digits++; }
        if (!value.TryFormat(target[(target.Length - digits)..], out _, provider: System.Globalization.CultureInfo.InvariantCulture))
        { throw new InvalidOperationException("Split page number exceeds its prepared width."); }
    }

    internal sealed class SplitResolver(IReadOnlyDictionary<int, QpdfObject> pool, IReadOnlySet<int> pages,
        IReadOnlySet<int> groupPages) : IIndirectResolver
    {
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        {
            if ((!pages.Contains(reference.ObjectNumber) || groupPages.Contains(reference.ObjectNumber))
                && pool.TryGetValue(reference.ObjectNumber, out value)) { return true; }
            value = null; return false;
        }
    }

    internal static PdfArray? FindSplitDetachedRoots(IReadOnlyDictionary<int, QpdfObject> pool,
        IReadOnlyList<PdfReference> pages, PdfArray sharedRoots, PdfDictionary? form, QpdfObject? labels,
        HashSet<int>? candidates, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        if (candidates is not { Count: > 0 }) { return null; }

        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        List<QpdfObject> roots = ParserContainers.CreateList<QpdfObject>(scratch, checked(pages.Count + 3), IntPtr.Size);
        roots.Add(sharedRoots);
        if (form is not null) { roots.Add(form); }
        if (labels is not null) { roots.Add(labels); }
        foreach (PdfReference page in pages)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            roots.Add(page);
        }
        HashSet<int> active = GraphReachability.Find(Content.PoolResolver.Create(scratch, pool), roots, scratch, governor);

        // Candidates were collected before catalog assembly. A detached root
        // can still retain an active object through an explicit private edge.
        List<QpdfObject>? detached = null;
        foreach (int id in candidates)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (active.Contains(id) || !pool.ContainsKey(id)) { continue; }
            detached ??= ParserContainers.CreateList(memory);
            memory.Reserve(32L);
            ParserContainers.AddItem(memory, detached, new PdfReference(id, 0));
        }
        return detached is null ? null : ParserContainers.CopyArray(memory, detached);
    }

    internal readonly record struct GroupNumbers(List<int> Kept, Dictionary<int, int> Map);

    internal static GroupNumbers NumberGroup(IReadOnlyDictionary<int, QpdfObject> pool, HashSet<int> reachable,
        int catalogNumber, int pagesNumber, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        memory.Reserve(checked(64L + sizeof(int) * (long)reachable.Count));
        List<int> kept = new(reachable.Count);
        foreach (int id in reachable)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (id != catalogNumber && id != pagesNumber && pool.ContainsKey(id)) { kept.Add(id); }
        }
        GovernedSort.Sort(CollectionsMarshal.AsSpan(kept), new WriterNumberOrder(), governor, token);
        Dictionary<int, int> map = ParserContainers.CreateDictionary<int, int>(memory);
        ParserContainers.HintEntryCapacity(memory, map, checked(kept.Count + (catalogNumber == 0 ? 1 : 2)), sizeof(int), governor);
        if (catalogNumber != 0) { GovernedCollections.Assign(memory, governor, map, catalogNumber, 1, sizeof(int)); }
        GovernedCollections.Assign(memory, governor, map, pagesNumber, 2, sizeof(int));
        for (int i = 0; i < kept.Count; i++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            GovernedCollections.Assign(memory, governor, map, kept[i], 3 + i, sizeof(int));
        }
        return new(kept, map);
    }

    internal static List<PdfReference> MapKids(ReadOnlySpan<PdfReference> kids, Dictionary<int, int> map,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        List<PdfReference> result = ParserContainers.CreateList<PdfReference>(memory);
        foreach (PdfReference kid in kids)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            memory.Reserve(32L);
            ParserContainers.AddItem(memory, result, new PdfReference(map[kid.ObjectNumber], 0), IntPtr.Size);
        }
        return result;
    }

    internal static void AppendGroupObjects(List<NumberedObject> objects, IReadOnlyDictionary<int, QpdfObject> pool,
        GroupNumbers numbers, IReadOnlySet<int> allPages, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        foreach (int id in numbers.Kept)
        {
            NumberedObject item = new(numbers.Map[id], ObjectRemap.RemapSplit(pool[id], numbers.Map, allPages, memory, governor, token));
            ParserContainers.AddItem(memory, objects, item, 16L);
        }
    }

    internal static Dictionary<int, (int Number, int Generation)>? MapOriginalIds(
        IReadOnlyDictionary<int, (int Number, int Generation)>? originalIds, GroupNumbers numbers,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        if (originalIds is null) { return null; }
        Dictionary<int, (int Number, int Generation)> result = ParserContainers.CreateDictionary<int, (int, int)>(memory);
        foreach ((int original, int target) in numbers.Map)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (originalIds.TryGetValue(original, out var source))
            { GovernedCollections.Assign(memory, governor, result, target, source, 2L * sizeof(int)); }
        }
        return result;
    }
}
