using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

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

    internal static HashSet<int> ReachableIds(IIndirectResolver resolver, List<int> roots,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        List<QpdfObject> references = ParserContainers.CreateList(memory);
        foreach (int root in roots)
        {
            governor.NoteOperation();
            memory.Reserve(24L);
            ParserContainers.AddItem(memory, references, new PdfReference(root, 0));
        }
        PdfArray rootArray = ParserContainers.CopyArray(memory, references);
        return GraphReachability.Find(resolver, rootArray, memory, governor);
    }

}
