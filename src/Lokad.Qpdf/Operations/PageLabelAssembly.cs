using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Operations;

internal static class PageLabelAssembly
{
    internal static PdfDictionary? BuildPageLabels(
        IReadOnlyList<PageLabelSpec> labels,
        int total,
        ResourceGovernor governor)
    {
        if (labels.Count == 0) { return null; }
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        List<(int Index, PdfDictionary Label)> items = ResolveLabelItems(labels, total, governor, memory);
        if (items.Count == 0)
        {
            return null;
        }

        return NumbersDictionary(items, governor);
    }

    internal static List<(int Index, PdfDictionary Label)> ResolveLabelItems(
        IReadOnlyList<PageLabelSpec> labels,
        int total,
        ResourceGovernor governor,
        ParserMemoryScope memory)
    {
        memory.CheckOwner(governor);
        List<(int Index, PdfDictionary Label)> items = ParserContainers.CreateList<(int Index, PdfDictionary Label)>(memory);
        if (labels.Count == 0)
        {
            return items;
        }

        if (total == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(labels), "Page labels need at least one output page.");
        }

        foreach (PageLabelSpec specification in labels)
        {
            governor.NoteOperation();
            int index = specification.ResolveFirstPage(total) - 1;
            PdfDictionary label = specification.ToDictionary(governor, governor.CancellationToken);
            GovernedCollections.AddItem(memory, governor, items, (index, label), 16L);
        }

        if (items[0].Index != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(labels), "The first page label must start at page 1.");
        }

        for (int i = 1; i < items.Count; i++)
        {
            if (items[i].Index <= items[i - 1].Index)
            {
                throw new ArgumentOutOfRangeException(nameof(labels), "Page labels must start on increasing pages.");
            }
        }

        return items;
    }

    internal static PdfDictionary NumbersDictionary(List<(int Index, PdfDictionary Label)> items, ResourceGovernor governor)
        => Content.PageLabelStorage.NumberTree(items, governor, governor.CancellationToken);

    internal static PdfDictionary? SlicePageLabels(List<(int Index, PdfDictionary Label)> items, int first, int last,
        ResourceGovernor governor)
    {
        if (items.Count == 0) { return null; }
        int firstIndex = first - 1, low = 0, high = items.Count;
        while (low < high)
        {
            governor.CheckCancellation();
            int middle = low + (high - low) / 2;
            if (items[middle].Index <= firstIndex) { low = middle + 1; }
            else { high = middle; }
        }
        (int Index, PdfDictionary Label) carry = items[Math.Max(0, low - 1)];
        PdfDictionary firstLabel = carry.Label;
        if (carry.Index < firstIndex
            && PdfDictionaryLookup.TryGet(carry.Label, "S"u8, governor, governor.CancellationToken, out QpdfObject style)
            && style is not PdfNull)
        {
            long start = PdfDictionaryLookup.TryGet(carry.Label, "St"u8, governor, governor.CancellationToken, out QpdfObject rawStart)
                && rawStart is PdfInteger number ? number.Value : 1L;
            try { start = checked(start + ((long)firstIndex - carry.Index)); }
            catch (OverflowException ex)
            { throw new QpdfParseException("Page-label start overflows its output position.", null, null, ex); }
            firstLabel = Content.PageLabelStorage.Merged(
                style,
                PdfDictionaryLookup.TryGet(carry.Label, "P"u8, governor, governor.CancellationToken, out QpdfObject prefix) ? prefix : null,
                start, governor, governor.CancellationToken);
        }
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        List<(int Index, PdfDictionary Label)> sliced = ParserContainers.CreateList<(int Index, PdfDictionary Label)>(memory);
        GovernedCollections.AddItem(memory, governor, sliced, (0, firstLabel), 16L);
        for (int i = low; i < items.Count; i++)
        {
            governor.CheckCancellation();
            (int index, PdfDictionary label) = items[i];
            if (index >= last) { break; }
            GovernedCollections.AddItem(memory, governor, sliced, (index - firstIndex, label), 16L);
        }
        return NumbersDictionary(sliced, governor);
    }

}
