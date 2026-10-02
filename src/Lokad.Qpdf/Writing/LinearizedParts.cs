using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>
/// Graph classification for linearized files, mirroring upstream categories.
/// Parts use original object numbers: the catalog opens part 4; the first
/// page leads part 6 followed by its private then shared objects; other pages
/// form consecutive page-plus-private runs in part 7; remaining shared
/// objects fill part 8; the pages tree leads part 9 followed by everything
/// else. Page thumbnails land in part 9 in page order; outlines ride the
/// first page when the catalog requests outline mode and close part 9
/// otherwise.
/// </summary>
internal sealed record ClassifiedGraph(
    IReadOnlyList<int> Part4,
    IReadOnlyList<int> Part6,
    IReadOnlyList<IReadOnlyList<int>> Part7,
    IReadOnlyList<int> Part8,
    IReadOnlyList<int> Part9,
    IReadOnlyList<int> Pages,
    IReadOnlyList<HashSet<int>> PageUsers,
    IReadOnlyList<int> Outlines,
    bool OutlinesInFirstPage);

/// <summary>
/// Part assignment for linearized publication, mirroring upstream categories.
/// The catalog opens part 4; the first page leads part 6 followed by its
/// private then shared objects; other pages form consecutive page-plus-private
/// runs in part 7; remaining shared objects fill part 8; the pages tree leads
/// part 9 followed by everything else. Page thumbnails land in part 9 in
/// page order; outlines ride the first page when the catalog requests
/// outline mode and close part 9 otherwise.
/// </summary>
internal sealed class LinearizedParts
{
    internal LinearizedParts(
        IReadOnlyList<int> part4,
        IReadOnlyList<int> part6,
        IReadOnlyList<IReadOnlyList<int>> part7,
        IReadOnlyList<int> part8,
        IReadOnlyList<int> part9,
        IReadOnlyList<int> pages,
        IReadOnlyList<HashSet<int>> pageUsers,
        IReadOnlyList<int> outlines,
        bool outlinesInFirstPage,
        Dictionary<int, int> renumber,
        int lindictNumber,
        int hintNumber,
        Dictionary<int, QpdfObject> lookup)
    {
        Part4 = part4;
        Part6 = part6;
        Part7 = part7;
        Part8 = part8;
        Part9 = part9;
        Pages = pages;
        PageUsers = pageUsers;
        Outlines = outlines;
        OutlinesInFirstPage = outlinesInFirstPage;
        Renumber = renumber;
        LindictNumber = lindictNumber;
        HintNumber = hintNumber;
        Lookup = lookup;
    }

    internal IReadOnlyList<int> Part4 { get; }

    internal IReadOnlyList<int> Part6 { get; }

    internal IReadOnlyList<IReadOnlyList<int>> Part7 { get; }

    internal IReadOnlyList<int> Part8 { get; }

    internal IReadOnlyList<int> Part9 { get; }

    internal IReadOnlyList<int> Pages { get; }

    internal IReadOnlyList<HashSet<int>> PageUsers { get; }

    internal IReadOnlyList<int> Outlines { get; }

    internal bool OutlinesInFirstPage { get; }

    internal Dictionary<int, int> Renumber { get; }

    internal int LindictNumber { get; }

    internal int HintNumber { get; }

    internal IReadOnlyDictionary<int, QpdfObject> Lookup { get; }

    internal static LinearizedParts Classify(
        List<NumberedObject> model,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (model is null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        Dictionary<int, QpdfObject> lookup = new(model.Count);
        foreach (NumberedObject numbered in model)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!lookup.ContainsKey(numbered.Number))
            {
                lookup[numbered.Number] = numbered.Value;
            }
        }

        ClassifiedGraph graph = ClassifyGraph(lookup, 1, governor, cancellationToken);
        Dictionary<int, int> renumber = AssignNumbers(graph.Part7, graph.Part8, graph.Part9, graph.Part4, graph.Part6, out int lindictNumber, out int hintNumber);
        if (renumber.Count != lookup.Count)
        {
            throw new QpdfParseException("Linearized output lost objects while ordering parts.", null, null);
        }

        return new LinearizedParts(graph.Part4, graph.Part6, graph.Part7, graph.Part8, graph.Part9, graph.Pages, graph.PageUsers, graph.Outlines, graph.OutlinesInFirstPage, renumber, lindictNumber, hintNumber, lookup);
    }

    internal static ClassifiedGraph ClassifyGraph(
        Dictionary<int, QpdfObject> lookup,
        int catalogNumber,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (lookup is null)
        {
            throw new ArgumentNullException(nameof(lookup));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        if (!lookup.TryGetValue(catalogNumber, out QpdfObject? catalogRaw) || catalogRaw is not PdfDictionary catalog)
        {
            throw new QpdfParseException($"Linearized output needs object {catalogNumber} to hold the catalog.", null, catalogNumber);
        }

        List<int> pages = CollectPages(catalog, lookup, governor, cancellationToken);
        if (pages.Count == 0)
        {
            throw new QpdfParseException("Linearized output needs at least one page.", null, catalogNumber);
        }

        List<HashSet<int>> closures = new(pages.Count);
        List<int> thumbImages = new(pages.Count);
        for (int i = 0; i < pages.Count; i++)
        {
            PageWalk walk = WalkPage(lookup, pages[i], governor, cancellationToken);
            closures.Add(walk.Closure);
            thumbImages.Add(walk.Thumb);
        }

        Dictionary<int, HashSet<int>> users = new();
        for (int i = 0; i < pages.Count; i++)
        {
            foreach (int number in closures[i])
            {
                if (!users.TryGetValue(number, out HashSet<int>? set))
                {
                    set = new HashSet<int>();
                    users[number] = set;
                }

                set.Add(i);
            }
        }

        HashSet<int> openDocuments = CollectOpenDocuments(lookup, catalog, governor, cancellationToken);
        HashSet<int> pageSet = new(pages);
        List<int> tree = CollectTree(catalog, lookup, pageSet, governor, cancellationToken);
        tree.Sort();
        tree.RemoveAll(number => users.ContainsKey(number));
        HashSet<int> treeSet = new(tree);
        List<int> outlineOrder = CollectOutlines(lookup, catalog, catalogNumber, closures, openDocuments, treeSet, governor, cancellationToken);
        HashSet<int> outlinePlaced = new(outlineOrder);
        bool outlinesInFirstPage = outlineOrder.Count > 0 && IsUseOutlines(catalog);
        List<List<int>> thumbRuns = CollectThumbs(lookup, thumbImages, closures, users, openDocuments, treeSet, outlinePlaced, governor, cancellationToken);
        HashSet<int> thumbPlaced = new();
        foreach (List<int> run in thumbRuns)
        {
            foreach (int number in run)
            {
                thumbPlaced.Add(number);
            }
        }
        List<int> part4 = new() { catalogNumber };
        foreach (int number in SortedKeys(lookup))
        {
            if (number != catalogNumber && openDocuments.Contains(number))
            {
                part4.Add(number);
            }
        }

        List<int> part6 = new() { pages[0] };
        List<int> part6Private = new();
        List<int> part6Shared = new();
        List<List<int>> part7 = new(pages.Count - 1);
        for (int i = 1; i < pages.Count; i++)
        {
            part7.Add(new List<int>() { pages[i] });
        }

        List<int> part8 = new();
        List<int> part9Rest = new();
        foreach (int number in SortedKeys(lookup))
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (number == catalogNumber || pageSet.Contains(number) || treeSet.Contains(number) || openDocuments.Contains(number) || outlinePlaced.Contains(number) || thumbPlaced.Contains(number))
            {
                continue;
            }

            if (!users.TryGetValue(number, out HashSet<int>? use) || use.Count == 0)
            {
                part9Rest.Add(number);
                continue;
            }

            if (use.Contains(0))
            {
                if (use.Count == 1)
                {
                    part6Private.Add(number);
                }
                else
                {
                    part6Shared.Add(number);
                }
            }
            else if (use.Count >= 2)
            {
                part8.Add(number);
            }
            else
            {
                part7[FirstOf(use) - 1].Add(number);
            }
        }

        part6.AddRange(part6Private);
        part6.AddRange(part6Shared);
        if (outlinesInFirstPage)
        {
            part6.AddRange(outlineOrder);
        }

        List<int> part9 = new(tree);
        foreach (List<int> run in thumbRuns)
        {
            part9.AddRange(run);
        }

        if (!outlinesInFirstPage)
        {
            part9.AddRange(outlineOrder);
        }

        part9.AddRange(part9Rest);

        int placed = part4.Count + part6.Count + part8.Count + part9.Count;
        foreach (IReadOnlyList<int> run in part7)
        {
            placed = checked(placed + run.Count);
        }

        if (placed != lookup.Count)
        {
            throw new QpdfParseException("Linearized output lost objects while ordering parts.", null, null);
        }

        return new ClassifiedGraph(part4, part6, part7, part8, part9, pages, closures, outlineOrder, outlinesInFirstPage);
    }

    private static List<List<int>> CollectThumbs(
        Dictionary<int, QpdfObject> lookup,
        IReadOnlyList<int> thumbImages,
        IReadOnlyList<HashSet<int>> closures,
        Dictionary<int, HashSet<int>> users,
        HashSet<int> openDocuments,
        HashSet<int> treeSet,
        HashSet<int> outlinePlaced,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        List<HashSet<int>> thumbClosures = new(thumbImages.Count);
        Dictionary<int, HashSet<int>> thumbUsers = new();
        for (int i = 0; i < thumbImages.Count; i++)
        {
            HashSet<int> thumbClosure = new();
            if (thumbImages[i] > 0 && lookup.TryGetValue(thumbImages[i], out QpdfObject? image) && image is not null)
            {
                thumbClosure.Add(thumbImages[i]);
                WalkSubtree(lookup, image, false, thumbClosure, governor, cancellationToken);
            }

            foreach (HashSet<int> closure in closures)
            {
                thumbClosure.ExceptWith(closure);
            }

            thumbClosure.ExceptWith(openDocuments);
            thumbClosure.ExceptWith(treeSet);
            thumbClosure.ExceptWith(outlinePlaced);
            thumbClosures.Add(thumbClosure);
            foreach (int number in thumbClosure)
            {
                if (!thumbUsers.TryGetValue(number, out HashSet<int>? set))
                {
                    set = new HashSet<int>();
                    thumbUsers[number] = set;
                }

                set.Add(i);
            }
        }

        HashSet<int> thumbShared = new();
        foreach (int number in SortedKeys(lookup))
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (thumbUsers.TryGetValue(number, out HashSet<int>? use) && use.Count >= 2)
            {
                thumbShared.Add(number);
            }
        }

        List<List<int>> runs = new(thumbImages.Count);
        for (int i = 0; i < thumbImages.Count; i++)
        {
            List<int> run = new();
            List<int> ordered = new(thumbClosures[i]);
            ordered.Sort();
            foreach (int number in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                if (thumbShared.Contains(number) || users.ContainsKey(number))
                {
                    continue;
                }

                if (number == thumbImages[i])
                {
                    run.Insert(0, number);
                }
                else
                {
                    run.Add(number);
                }
            }

            runs.Add(run);
        }

        List<int> shared = new(thumbShared);
        shared.Sort();
        List<int> tail = new();
        foreach (int number in shared)
        {
            if (!users.ContainsKey(number))
            {
                tail.Add(number);
            }
        }

        if (tail.Count > 0)
        {
            runs.Add(tail);
        }

        return runs;
    }

    private static List<int> CollectOutlines(
        Dictionary<int, QpdfObject> lookup,
        PdfDictionary catalog,
        int catalogNumber,
        IReadOnlyList<HashSet<int>> closures,
        HashSet<int> openDocuments,
        HashSet<int> treeSet,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        List<int> ordered = new();
        if (!catalog.TryGet(PdfName.FromAscii("Outlines"), out QpdfObject? outlinesRaw) || outlinesRaw is null || outlinesRaw is PdfNull)
        {
            return ordered;
        }

        if (outlinesRaw is not PdfReference outlinesReference)
        {
            throw new QpdfParseException("Linearized output with a direct outlines dictionary is not implemented.", null, catalogNumber);
        }

        if (!lookup.TryGetValue(outlinesReference.ObjectNumber, out QpdfObject? outlinesTarget) || outlinesTarget is null)
        {
            throw new QpdfParseException("Linearized output needs a resolvable outlines dictionary.", null, outlinesReference.ObjectNumber);
        }

        HashSet<int> outlineSet = new();
        WalkSubtree(lookup, outlinesReference, true, outlineSet, governor, cancellationToken);
        foreach (HashSet<int> closure in closures)
        {
            outlineSet.ExceptWith(closure);
        }

        outlineSet.ExceptWith(openDocuments);
        outlineSet.ExceptWith(treeSet);
        outlineSet.Remove(catalogNumber);
        if (!outlineSet.Contains(outlinesReference.ObjectNumber))
        {
            throw new QpdfParseException("Linearized output with fragmented outlines is not implemented.", null, catalogNumber);
        }

        ordered.Add(outlinesReference.ObjectNumber);
        foreach (int number in SortedKeys(lookup))
        {
            if (number != outlinesReference.ObjectNumber && outlineSet.Contains(number))
            {
                ordered.Add(number);
            }
        }

        return ordered;
    }

    private static bool IsUseOutlines(PdfDictionary catalog)
    {
        return catalog.TryGet(PdfName.FromAscii("PageMode"), out QpdfObject? mode)
            && mode is PdfName modeName
            && modeName.DisplayText == "UseOutlines";
    }

    private static HashSet<int> CollectOpenDocuments(
        Dictionary<int, QpdfObject> lookup,
        PdfDictionary catalog,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        HashSet<int> open = new();
        string[] keys = ["ViewerPreferences", "PageMode", "Threads", "OpenAction", "AcroForm"];
        foreach (string key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!catalog.TryGet(PdfName.FromAscii(key), out QpdfObject? value) || value is null || value is PdfNull)
            {
                continue;
            }

            if (value is PdfReference reference)
            {
                if (!lookup.TryGetValue(reference.ObjectNumber, out QpdfObject? target) || target is null)
                {
                    continue;
                }

                WalkSubtree(lookup, target, false, open, governor, cancellationToken);
            }
        }

        return open;
    }

    private static void WalkSubtree(
        Dictionary<int, QpdfObject> lookup,
        QpdfObject seed,
        bool top,
        HashSet<int> seen,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        List<(QpdfObject Value, bool Top)> stack = new() { (seed, top) };
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            (QpdfObject current, bool currentTop) = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            if (current is PdfReference reference)
            {
                if (!lookup.TryGetValue(reference.ObjectNumber, out QpdfObject? target) || target is null)
                {
                    continue;
                }

                if (!seen.Add(reference.ObjectNumber))
                {
                    continue;
                }

                if (!currentTop && target is PdfDictionary dict
                    && dict.TryGet(PdfName.FromAscii("Type"), out QpdfObject kind)
                    && kind is PdfName name
                    && name.DisplayText == "Page")
                {
                    continue;
                }

                stack.Add((target, false));
                continue;
            }

            if (current is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    stack.Add((array[i], false));
                }

                continue;
            }

            PdfDictionary? dictionary = current as PdfDictionary;
            if (current is PdfStream stream)
            {
                dictionary = stream.Dictionary;
            }

            if (dictionary is not null)
            {
                foreach (PdfName key in dictionary.Keys)
                {
                    if (current is PdfStream
                        && (key.DisplayText == "Length" || key.DisplayText == "Filter" || key.DisplayText == "DecodeParms"))
                    {
                        continue;
                    }

                    if (dictionary.TryGet(key, out QpdfObject? entry) && entry is not null && entry is not PdfNull)
                    {
                        stack.Add((entry, false));
                    }
                }
            }
        }
    }
    private static List<int> CollectPages(
        PdfDictionary catalog,
        Dictionary<int, QpdfObject> lookup,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (!catalog.TryGet(PdfName.FromAscii("Pages"), out QpdfObject pagesRaw)
            || pagesRaw is not PdfReference pagesReference)
        {
            throw new QpdfParseException("Linearized output needs a reachable pages tree.", null, 1);
        }

        List<int> pages = new();
        HashSet<int> seen = new();
        List<int> stack = new() { pagesReference.ObjectNumber };
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            int number = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            if (!seen.Add(number))
            {
                continue;
            }

            if (!lookup.TryGetValue(number, out QpdfObject? value) || value is not PdfDictionary node)
            {
                continue;
            }

            if (node.TryGet(PdfName.FromAscii("Type"), out QpdfObject kind)
                && kind is PdfName name
                && name.DisplayText == "Page")
            {
                pages.Add(number);
                continue;
            }

            if (node.TryGet(PdfName.FromAscii("Kids"), out QpdfObject kids) && kids is PdfArray array)
            {
                for (int i = array.Count - 1; i >= 0; i--)
                {
                    if (array[i] is PdfReference kid)
                    {
                        stack.Add(kid.ObjectNumber);
                    }
                }
            }
        }

        return pages;
    }

    private sealed record PageWalk(HashSet<int> Closure, int Thumb);

    private static PageWalk WalkPage(
        Dictionary<int, QpdfObject> lookup,
        int pageNumber,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        HashSet<int> seen = new() { pageNumber };
        int thumb = -1;
        List<(QpdfObject Value, bool Top)> stack = new();
        if (lookup.TryGetValue(pageNumber, out QpdfObject? page))
        {
            stack.Add((page, true));
        }

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            (QpdfObject current, bool top) = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            if (current is PdfReference reference)
            {
                if (!lookup.TryGetValue(reference.ObjectNumber, out QpdfObject? target))
                {
                    continue;
                }

                if (!seen.Add(reference.ObjectNumber))
                {
                    continue;
                }

                if (target is PdfDictionary dict)
                {
                    if (dict.TryGet(PdfName.FromAscii("Type"), out QpdfObject kind)
                        && kind is PdfName name)
                    {
                        if (name.DisplayText == "Page" && !top)
                        {
                            continue;
                        }

                        if (name.DisplayText == "Pages")
                        {
                            seen.Remove(reference.ObjectNumber);
                            continue;
                        }
                    }
                }

                stack.Add((target, false));
                continue;
            }

            if (current is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    stack.Add((array[i], false));
                }

                continue;
            }

            if (current is PdfStream contentStream)
            {
                foreach (PdfName key in contentStream.Dictionary.Keys)
                {
                    if (key.DisplayText == "Length" || key.DisplayText == "Filter" || key.DisplayText == "DecodeParms")
                    {
                        continue;
                    }

                    if (contentStream.Dictionary.TryGet(key, out QpdfObject? entry) && entry is not null && entry is not PdfNull)
                    {
                        stack.Add((entry, false));
                    }
                }

                continue;
            }

            if (current is PdfDictionary dictionary)
            {
                bool topPage = top
                    && dictionary.TryGet(PdfName.FromAscii("Type"), out QpdfObject pageKind)
                    && pageKind is PdfName pageName
                    && pageName.DisplayText == "Page";
                foreach (PdfName key in dictionary.Keys)
                {
                    if (topPage && key.DisplayText == "Parent")
                    {
                        continue;
                    }

                    if (topPage && key.DisplayText == "Thumb")
                    {
                        if (dictionary.TryGet(key, out QpdfObject? thumbRef) && thumbRef is PdfReference thumbReference)
                        {
                            thumb = thumbReference.ObjectNumber;
                        }

                        continue;
                    }

                    if (dictionary.TryGet(key, out QpdfObject? entry) && entry is not null && entry is not PdfNull)
                    {
                        stack.Add((entry, false));
                    }
                }
            }
        }

        return new PageWalk(seen, thumb);
    }
    private static List<int> CollectTree(
        PdfDictionary catalog,
        Dictionary<int, QpdfObject> lookup,
        HashSet<int> pageSet,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        List<int> tree = new();
        if (!catalog.TryGet(PdfName.FromAscii("Pages"), out QpdfObject pagesRaw)
            || pagesRaw is not PdfReference pagesReference)
        {
            return tree;
        }

        HashSet<int> seen = new();
        List<int> stack = new() { pagesReference.ObjectNumber };
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            int number = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            if (!seen.Add(number))
            {
                continue;
            }

            if (!lookup.TryGetValue(number, out QpdfObject? value) || value is not PdfDictionary node)
            {
                continue;
            }

            if (pageSet.Contains(number))
            {
                continue;
            }

            tree.Add(number);
            if (node.TryGet(PdfName.FromAscii("Kids"), out QpdfObject kids) && kids is PdfArray array)
            {
                for (int i = array.Count - 1; i >= 0; i--)
                {
                    if (array[i] is PdfReference kid)
                    {
                        stack.Add(kid.ObjectNumber);
                    }
                }
            }
        }

        return tree;
    }

    private static List<int> SortedKeys(Dictionary<int, QpdfObject> lookup)
    {
        List<int> keys = new(lookup.Keys);
        keys.Sort();
        return keys;
    }

    private static int FirstOf(HashSet<int> set)
    {
        foreach (int value in set)
        {
            return value;
        }

        throw new QpdfParseException("Linearized output reached an empty page set.", null, null);
    }

    private static Dictionary<int, int> AssignNumbers(
        IReadOnlyList<IReadOnlyList<int>> part7,
        IReadOnlyList<int> part8,
        IReadOnlyList<int> part9,
        IReadOnlyList<int> part4,
        IReadOnlyList<int> part6,
        out int lindictNumber,
        out int hintNumber)
    {
        Dictionary<int, int> renumber = new();
        int next = 1;
        foreach (IReadOnlyList<int> run in part7)
        {
            foreach (int number in run)
            {
                renumber[number] = next;
                next = checked(next + 1);
            }
        }

        foreach (int number in part8)
        {
            renumber[number] = next;
            next = checked(next + 1);
        }

        foreach (int number in part9)
        {
            renumber[number] = next;
            next = checked(next + 1);
        }

        lindictNumber = next;
        next = checked(next + 1);
        foreach (int number in part4)
        {
            renumber[number] = next;
            next = checked(next + 1);
        }

        hintNumber = next;
        next = checked(next + 1);
        foreach (int number in part6)
        {
            renumber[number] = next;
            next = checked(next + 1);
        }

        return renumber;
    }
}
