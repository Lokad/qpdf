using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Operations;

internal static class BoundPageAssembly
{
    private static readonly PdfName ParentKey = PdfName.FromAscii("Parent");

    internal static List<PdfReference> PreparePages(IReadOnlyList<PageLocation> pages, Dictionary<int, QpdfObject> pool,
        IIndirectResolver resolver, int pagesNumber, int reservedCatalog, IReadOnlySet<int>? prepared,
        ref int maximum, HashSet<int> pageNumbers,
        ref Dictionary<int, PdfReference>? articleAliases, ref bool changesTagTopology, QpdfLimits limits,
        ParserMemoryScope memory, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        List<PdfReference> kids = ParserContainers.CreateList<PdfReference>(memory, pages.Count, IntPtr.Size);
        memory.Reserve(32L);
        PdfReference parent = new(pagesNumber, 0);
        using OwnedAllocationBatch headers = OwnedAllocationBatch.Create(governor, token);
        using PageInheritance.Batch? ancestry = PageInheritance.Batch.TryCreate(pages.Count > 1, resolver,
            PageAssembly.WriterInherited, limits, governor, token);
        foreach (PageLocation location in pages)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            int number = location.Source?.ObjectNumber ?? checked(++maximum);
            if (number == reservedCatalog || pageNumbers.Contains(number))
            { number = checked(++maximum); changesTagTopology = true; }
            changesTagTopology |= location.Source is null;
            ParserContainers.AddSetItem(memory, pageNumbers, number, sizeof(int));
            if (prepared?.Contains(number) == true && location.Source is PdfReference existing)
            {
                PageAssembly.RequireMediaBox(location.Page);
                kids.Add(existing);
                continue;
            }
            PageInheritance.InheritedValues inherited = ancestry?.Find(location) ?? PageInheritance.FindAncestors(resolver, location,
                PageAssembly.WriterInherited, limits, governor, token);
            PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(headers, out long allocation);
            using PdfDictionary.EntryEnumerator original = location.Page.GetEntryEnumerator();
            while (original.MoveNext(governor, token))
            { entries.Assign(headers.Memory, governor, original.Current.Key, original.Current.Value, token); }
            entries.Assign(headers.Memory, governor, ParentKey, parent, token);
            for (int i = 0; i < PageAssembly.WriterInherited.Length; i++)
            {
                PdfName key = PageAssembly.WriterInherited[i];
                QpdfObject? value = inherited[i];
                if (key.Equals(PageResources.Key))
                { value = PageResources.PrepareSelection(location.Page, value, resolver, limits, governor, diagnostics, token); }
                if (value is not null) { entries.Assign(headers.Memory, governor, key, value, token); }
            }
            PdfDictionary page = PdfDictionaryRebuild.Complete(headers, entries, allocation);
            PageAssembly.RequireMediaBox(page);
            if (location.Source is PdfReference canonical)
            { ArticleThreads.RememberDuplicate(page, number, canonical.ObjectNumber, canonical.Generation, ref articleAliases, memory); }
            // Replacing a visited parent changes its dictionary. A newly
            // resolvable value can also change an earlier absence observation.
            if (ancestry is not null && (ancestry.Knows(number, 0)
                || !pool.TryGetValue(number, out QpdfObject? previous) || previous is not PdfDictionary))
            { ancestry.Clear(); }
            GovernedCollections.Assign(memory, governor, pool, number, page, IntPtr.Size);
            memory.Reserve(32L);
            kids.Add(new PdfReference(number, 0));
        }
        headers.Memory.TransferTo(memory, headers.Bytes);
        return kids;
    }
}
