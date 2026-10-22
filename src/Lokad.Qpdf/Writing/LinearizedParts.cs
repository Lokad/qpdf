using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

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
internal sealed class ClassifiedGraph : IDisposable
{
    internal ClassifiedGraph(IReadOnlyList<int> part4, IReadOnlyList<int> part6,
        IReadOnlyList<IReadOnlyList<int>> part7, IReadOnlyList<int> part8, IReadOnlyList<int> part9,
        IReadOnlyList<int> pages, IReadOnlyList<IReadOnlyCollection<int>> pageUsers, IReadOnlyList<int> outlines,
        bool outlinesInFirstPage)
    {
        Part4 = part4; Part6 = part6; Part7 = part7; Part8 = part8; Part9 = part9;
        Pages = pages; PageUsers = pageUsers; Outlines = outlines; OutlinesInFirstPage = outlinesInFirstPage;
    }

    internal IReadOnlyList<int> Part4 { get; private set; }
    internal IReadOnlyList<int> Part6 { get; private set; }
    internal IReadOnlyList<IReadOnlyList<int>> Part7 { get; private set; }
    internal IReadOnlyList<int> Part8 { get; private set; }
    internal IReadOnlyList<int> Part9 { get; private set; }
    internal IReadOnlyList<int> Pages { get; private set; }
    internal IReadOnlyList<IReadOnlyCollection<int>> PageUsers { get; private set; }
    internal IReadOnlyList<int> Outlines { get; private set; }
    internal bool OutlinesInFirstPage { get; }

    public void Dispose()
    {
        Part4 = Array.Empty<int>(); Part6 = Array.Empty<int>();
        Part7 = Array.Empty<IReadOnlyList<int>>(); Part8 = Array.Empty<int>(); Part9 = Array.Empty<int>();
        Pages = Array.Empty<int>(); PageUsers = Array.Empty<IReadOnlyCollection<int>>(); Outlines = Array.Empty<int>();
    }
}

/// <summary>
/// Compressed-object layout for streamed linearized publication. Parts hold
/// emission units (direct model objects plus synthetic object-stream
/// containers); members resolve to their container during classification and
/// take type-2 cross-reference entries. Page dictionaries and the catalog
/// never compress, mirroring upstream exclusions.
/// </summary>
internal sealed class StreamedParts : IDisposable
{
    private GovernedIntDictionary<int>? _renumber;
    private GovernedIntDictionary<(int Container, int Index)>? _members;
    private GovernedIntDictionary<IReadOnlyList<int>>? _containerMembers;
    private GovernedIntSet? _containers;
    private GovernedIntDictionary<QpdfObject>? _lookup;
    internal StreamedParts(
        IReadOnlyList<int> part4,
        IReadOnlyList<int> part6,
        IReadOnlyList<IReadOnlyList<int>> part7,
        IReadOnlyList<int> part8,
        IReadOnlyList<int> part9,
        IReadOnlyList<int> pages,
        IReadOnlyList<IReadOnlyCollection<int>> pageUsers,
        IReadOnlyList<int> outlines,
        bool outlinesInFirstPage,
        bool outlinesDirect,
        GovernedIntDictionary<int> renumber,
        GovernedIntDictionary<(int Container, int Index)> members,
        GovernedIntDictionary<IReadOnlyList<int>> containerMembers,
        GovernedIntSet containers,
        int lindictNumber,
        int hintNumber,
        int xrefFirstNumber,
        int xrefMainNumber,
        int firstSize,
        int mainSize,
        int maxNumber,
        int maxMemberIndex,
        GovernedIntDictionary<QpdfObject> lookup)
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
        OutlinesDirect = outlinesDirect;
        _renumber = renumber;
        _members = members;
        _containerMembers = containerMembers;
        _containers = containers;
        LindictNumber = lindictNumber;
        HintNumber = hintNumber;
        XrefFirstNumber = xrefFirstNumber;
        XrefMainNumber = xrefMainNumber;
        FirstSize = firstSize;
        MainSize = mainSize;
        MaxNumber = maxNumber;
        MaxMemberIndex = maxMemberIndex;
        _lookup = lookup;
    }

    internal IReadOnlyList<int> Part4 { get; private set; }

    internal IReadOnlyList<int> Part6 { get; private set; }

    internal IReadOnlyList<IReadOnlyList<int>> Part7 { get; private set; }

    internal IReadOnlyList<int> Part8 { get; private set; }

    internal IReadOnlyList<int> Part9 { get; private set; }

    internal IReadOnlyList<int> Pages { get; private set; }

    internal IReadOnlyList<IReadOnlyCollection<int>> PageUsers { get; private set; }

    internal IReadOnlyList<int> Outlines { get; private set; }

    internal bool OutlinesInFirstPage { get; }

    internal bool OutlinesDirect { get; }

    internal GovernedIntDictionary<int> Renumber => _renumber ?? throw new ObjectDisposedException(nameof(StreamedParts));

    internal GovernedIntDictionary<(int Container, int Index)> Members => _members ?? throw new ObjectDisposedException(nameof(StreamedParts));

    internal GovernedIntDictionary<IReadOnlyList<int>> ContainerMembers => _containerMembers ?? throw new ObjectDisposedException(nameof(StreamedParts));

    internal GovernedIntSet Containers => _containers ?? throw new ObjectDisposedException(nameof(StreamedParts));

    internal int LindictNumber { get; }

    internal int HintNumber { get; }

    internal int XrefFirstNumber { get; }

    internal int XrefMainNumber { get; }

    internal int FirstSize { get; }

    internal int MainSize { get; }

    internal int MaxNumber { get; }

    internal int MaxMemberIndex { get; }

    internal GovernedIntDictionary<QpdfObject> Lookup => _lookup ?? throw new ObjectDisposedException(nameof(StreamedParts));

    public void Dispose()
    {
        Part4 = Array.Empty<int>(); Part6 = Array.Empty<int>();
        Part7 = Array.Empty<IReadOnlyList<int>>(); Part8 = Array.Empty<int>(); Part9 = Array.Empty<int>();
        Pages = Array.Empty<int>(); PageUsers = Array.Empty<IReadOnlyCollection<int>>(); Outlines = Array.Empty<int>();
        _renumber = null; _members = null; _containerMembers = null; _containers = null; _lookup = null;
    }
}

/// <summary>
/// Part assignment for linearized publication, mirroring upstream categories.
/// The catalog opens part 4; the first page leads part 6 followed by its
/// private then shared objects; other pages form consecutive page-plus-private
/// runs in part 7; remaining shared objects fill part 8; the pages tree leads
/// part 9 followed by everything else. Page thumbnails land in part 9 in
/// page order; outlines ride the first page when the catalog requests
/// outline mode and close part 9 otherwise.
/// </summary>
internal sealed class LinearizedParts : IDisposable
{
    private GovernedIntDictionary<int>? _renumber;
    private GovernedIntDictionary<QpdfObject>? _lookup;
    internal LinearizedParts(
        IReadOnlyList<int> part4,
        IReadOnlyList<int> part6,
        IReadOnlyList<IReadOnlyList<int>> part7,
        IReadOnlyList<int> part8,
        IReadOnlyList<int> part9,
        IReadOnlyList<int> pages,
        IReadOnlyList<IReadOnlyCollection<int>> pageUsers,
        IReadOnlyList<int> outlines,
        bool outlinesInFirstPage,
        GovernedIntDictionary<int> renumber,
        int lindictNumber,
        int hintNumber,
        GovernedIntDictionary<QpdfObject> lookup)
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
        _renumber = renumber;
        LindictNumber = lindictNumber;
        HintNumber = hintNumber;
        _lookup = lookup;
    }

    internal IReadOnlyList<int> Part4 { get; private set; }

    internal IReadOnlyList<int> Part6 { get; private set; }

    internal IReadOnlyList<IReadOnlyList<int>> Part7 { get; private set; }

    internal IReadOnlyList<int> Part8 { get; private set; }

    internal IReadOnlyList<int> Part9 { get; private set; }

    internal IReadOnlyList<int> Pages { get; private set; }

    internal IReadOnlyList<IReadOnlyCollection<int>> PageUsers { get; private set; }

    internal IReadOnlyList<int> Outlines { get; private set; }

    internal bool OutlinesInFirstPage { get; }

    internal GovernedIntDictionary<int> Renumber => _renumber ?? throw new ObjectDisposedException(nameof(LinearizedParts));

    internal int LindictNumber { get; }

    internal int HintNumber { get; }

    internal GovernedIntDictionary<QpdfObject> Lookup => _lookup ?? throw new ObjectDisposedException(nameof(LinearizedParts));

    public void Dispose()
    {
        Part4 = Array.Empty<int>(); Part6 = Array.Empty<int>();
        Part7 = Array.Empty<IReadOnlyList<int>>(); Part8 = Array.Empty<int>(); Part9 = Array.Empty<int>();
        Pages = Array.Empty<int>(); PageUsers = Array.Empty<IReadOnlyCollection<int>>(); Outlines = Array.Empty<int>();
        _renumber = null; _lookup = null;
    }

    internal static LinearizedParts Classify(List<NumberedObject> model, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(governor);
        memory.CheckOwner(governor);
        cancellationToken.ThrowIfCancellationRequested();
        using ParserMemoryScope retained = ParserMemoryScope.Create(governor);
        GovernedIntDictionary<QpdfObject> lookup = ModelLookup(model, retained, governor, cancellationToken);
        using ClassifiedGraph graph = ClassifyGraph(lookup, 1, retained, governor, cancellationToken);
        GovernedIntDictionary<int> renumber = AssignNumbers(graph.Part7, graph.Part8, graph.Part9, graph.Part4,
            graph.Part6, retained, governor, cancellationToken, out int lindictNumber, out int hintNumber);
        if (renumber.Count != lookup.Count)
        {
            throw new QpdfParseException("Linearized output lost objects while ordering parts.", null, null);
        }
        retained.Reserve(128L);
        LinearizedParts result = new(graph.Part4, graph.Part6, graph.Part7, graph.Part8, graph.Part9,
            graph.Pages, graph.PageUsers, graph.Outlines, graph.OutlinesInFirstPage, renumber, lindictNumber, hintNumber, lookup);
        retained.TransferTo(memory);
        return result;
    }

    internal static StreamedParts ClassifyStreamed(List<NumberedObject> model,
        IReadOnlyList<IReadOnlyList<int>> groups, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(governor);
        memory.CheckOwner(governor);
        cancellationToken.ThrowIfCancellationRequested();
        using ParserMemoryScope retained = ParserMemoryScope.Create(governor);
        using ParserMemoryScope temporary = ParserMemoryScope.Create(governor);
        GovernedIntDictionary<QpdfObject> lookup = ModelLookup(model, retained, governor, cancellationToken);
        if (!lookup.TryGetValue(1, out QpdfObject? catalogRaw) || catalogRaw is not PdfDictionary catalog)
        {
            throw new QpdfParseException("Linearized output needs object 1 to hold the catalog.", null, 1);
        }
        List<int> pages = CollectPages(catalog, lookup, temporary, governor, cancellationToken);
        if (pages.Count == 0) { throw new QpdfParseException("Linearized output needs at least one page.", null, 1); }
        PlanContainers(lookup, pages, groups, retained, temporary, governor, cancellationToken,
            out List<int> containers, out GovernedIntDictionary<int> owners, out GovernedIntDictionary<List<int>> containerMembers);
        using ClassifiedGraph graph = ClassifyGraph(lookup, 1, owners, containers, retained, governor, cancellationToken);
        AssignStreamedNumbers(graph, containerMembers, retained, governor, cancellationToken,
            out GovernedIntDictionary<int> renumber, out GovernedIntDictionary<(int Container, int Index)> members,
            out int lindictNumber, out int hintNumber, out int xrefFirstNumber, out int xrefMainNumber,
            out int firstSize, out int mainSize, out int maxNumber, out int maxMemberIndex);
        if (renumber.Count != lookup.Count + containers.Count)
        {
            throw new QpdfParseException("Linearized output lost objects while ordering parts.", null, null);
        }
        bool outlinesDirect = true;
        GovernedIntSet containerSet = CopySet(containers, retained, governor, cancellationToken);
        for (int index = 0; index < graph.Outlines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (containerSet.Contains(graph.Outlines[index])) { outlinesDirect = false; break; }
        }
        GovernedIntDictionary<IReadOnlyList<int>> readonlyMembers = GovernedIntDictionary<IReadOnlyList<int>>.Create(retained, governor, IntPtr.Size, cancellationToken);
        foreach (KeyValuePair<int, List<int>> entry in containerMembers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GovernedCollections.Assign(retained, governor, readonlyMembers, entry.Key, (IReadOnlyList<int>)entry.Value);
        }
        retained.Reserve(168L);
        StreamedParts result = new(graph.Part4, graph.Part6, graph.Part7, graph.Part8, graph.Part9,
            graph.Pages, graph.PageUsers, graph.Outlines, graph.OutlinesInFirstPage, outlinesDirect, renumber,
            members, readonlyMembers, containerSet, lindictNumber, hintNumber, xrefFirstNumber, xrefMainNumber,
            firstSize, mainSize, maxNumber, maxMemberIndex, lookup);
        retained.TransferTo(memory);
        return result;
    }

    private static GovernedIntDictionary<QpdfObject> ModelLookup(List<NumberedObject> model,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        GovernedIntDictionary<QpdfObject> lookup = GovernedIntDictionary<QpdfObject>.Create(memory, governor, IntPtr.Size, cancellationToken);
        foreach (NumberedObject numbered in model)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            lookup.TryAdd(memory, numbered.Number, numbered.Value);
        }
        return lookup;
    }

    private static void PlanContainers(GovernedIntDictionary<QpdfObject> lookup, IReadOnlyList<int> pages,
        IReadOnlyList<IReadOnlyList<int>> groups, ParserMemoryScope memory, ParserMemoryScope temporary,
        ResourceGovernor governor, CancellationToken cancellationToken, out List<int> containers,
        out GovernedIntDictionary<int> owners, out GovernedIntDictionary<List<int>> containerMembers)
    {
        GovernedIntSet pageSet = CopySet(pages, temporary, governor, cancellationToken);
        int max = 0;
        foreach (KeyValuePair<int, QpdfObject> entry in lookup)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (entry.Key > max) { max = entry.Key; }
        }
        containers = ParserContainers.CreateList<int>(temporary);
        owners = GovernedIntDictionary<int>.Create(temporary, governor, sizeof(int), cancellationToken);
        containerMembers = GovernedIntDictionary<List<int>>.Create(temporary, governor, IntPtr.Size, cancellationToken);
        int next = checked(max + 1);
        for (int index = 0; index < groups.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            IReadOnlyList<int> group = groups[index];
            if (group is null) { continue; }
            using ParserMemoryScope groupMemory = ParserMemoryScope.Create(governor);
            List<int> kept = ParserContainers.CreateList<int>(groupMemory);
            for (int memberIndex = 0; memberIndex < group.Count; memberIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                int member = group[memberIndex];
                if (!lookup.TryGetValue(member, out QpdfObject? value) || value is null
                    || member == 1 || member == 2 || pageSet.Contains(member) || value is PdfStream || owners.ContainsKey(member)) { continue; }
                Add(groupMemory, governor, kept, member);
            }
            if (kept.Count == 0) { continue; }
            if (kept.Count > 100) { throw new QpdfException("Object-stream groups must hold 1..100 members."); }
            int container = next;
            next = checked(next + 1);
            Add(temporary, governor, containers, container);
            List<int> stored = ParserContainers.CreateList<int>(memory);
            for (int memberIndex = 0; memberIndex < kept.Count; memberIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int member = kept[memberIndex];
                GovernedCollections.Assign(temporary, governor, owners, member, container);
                Add(memory, governor, stored, member);
            }
            GovernedCollections.Assign(temporary, governor, containerMembers, container, stored);
        }
    }

    // Copy only the borrowed index. Its PDF values keep their existing owners.
    internal static ClassifiedGraph ClassifyGraph(
        GovernedIntDictionary<QpdfObject> lookup,
        int catalogNumber,
        ParserMemoryScope memory,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        memory.CheckOwner(governor);
        lookup.CheckOwner(governor);
        cancellationToken.ThrowIfCancellationRequested();
        using ParserMemoryScope temporary = ParserMemoryScope.Create(governor);
        GovernedIntDictionary<int> owners = GovernedIntDictionary<int>.Create(temporary, governor, sizeof(int), cancellationToken);
        return ClassifyGraph(lookup, catalogNumber, owners, Array.Empty<int>(), memory, governor, cancellationToken);
    }

    internal static ClassifiedGraph ClassifyGraph(
        GovernedIntDictionary<QpdfObject> lookup,
        int catalogNumber,
        GovernedIntDictionary<int> owners,
        IReadOnlyList<int> extraIds,
        ParserMemoryScope memory,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (lookup is null)
        {
            throw new ArgumentNullException(nameof(lookup));
        }

        if (owners is null)
        {
            throw new ArgumentNullException(nameof(owners));
        }

        if (extraIds is null)
        {
            throw new ArgumentNullException(nameof(extraIds));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        memory.CheckOwner(governor);
        lookup.CheckOwner(governor);
        owners.CheckOwner(governor);
        cancellationToken.ThrowIfCancellationRequested();
        using ParserMemoryScope retained = ParserMemoryScope.Create(governor);
        using ParserMemoryScope temporary = ParserMemoryScope.Create(governor);
        if (!lookup.TryGetValue(catalogNumber, out QpdfObject? catalogRaw) || catalogRaw is not PdfDictionary catalog)
        {
            throw new QpdfParseException($"Linearized output needs object {catalogNumber} to hold the catalog.", null, catalogNumber);
        }

        List<int> pages = CollectPages(catalog, lookup, retained, governor, cancellationToken);
        if (pages.Count == 0)
        {
            throw new QpdfParseException("Linearized output needs at least one page.", null, catalogNumber);
        }

        List<GovernedIntSet> closures = ParserContainers.CreateList<GovernedIntSet>(retained);
        List<int> thumbImages = ParserContainers.CreateList<int>(temporary);
        for (int i = 0; i < pages.Count; i++)
        {
            PageWalk walk = WalkPage(lookup, pages[i], owners, retained, governor, cancellationToken);
            GovernedCollections.AddItem(retained, governor, closures, walk.Closure, IntPtr.Size);
            Add(temporary, governor, thumbImages, walk.Thumb);
        }

        GovernedIntDictionary<GovernedIntSet> users = GovernedIntDictionary<GovernedIntSet>.Create(temporary, governor, IntPtr.Size, cancellationToken);
        for (int i = 0; i < pages.Count; i++)
        {
            foreach (int number in closures[i])
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                if (!users.TryGetValue(number, out GovernedIntSet? set))
                {
                    set = GovernedIntSet.Create(temporary, governor, cancellationToken);
                    GovernedCollections.Assign(temporary, governor, users, number, set);
                }

                GovernedCollections.AddNumber(temporary, governor, set, i);
            }
        }

        GovernedIntSet openDocuments = CollectOpenDocuments(lookup, catalog, owners, temporary, governor, cancellationToken);
        GovernedIntSet pageSet = CopySet(pages, temporary, governor, cancellationToken);
        List<int> tree = CollectTree(catalog, lookup, pageSet, owners, temporary, governor, cancellationToken);
        GovernedCollections.SortNumbers(temporary, governor, tree);
        int treeCount = 0;
        for (int index = 0; index < tree.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!users.ContainsKey(tree[index])) { tree[treeCount++] = tree[index]; }
        }
        tree.RemoveRange(treeCount, tree.Count - treeCount);
        GovernedIntSet treeSet = CopySet(tree, temporary, governor, cancellationToken);
        List<int> outlineOrder = CollectOutlines(lookup, catalog, catalogNumber, closures, openDocuments, treeSet, owners, retained, governor, cancellationToken);
        GovernedIntSet outlinePlaced = CopySet(outlineOrder, temporary, governor, cancellationToken);
        bool outlinesInFirstPage = outlineOrder.Count > 0 && IsUseOutlines(catalog, governor, cancellationToken);
        List<List<int>> thumbRuns = CollectThumbs(lookup, thumbImages, users, openDocuments, treeSet, outlinePlaced, owners, temporary, governor, cancellationToken);
        GovernedIntSet thumbPlaced = GovernedIntSet.Create(temporary, governor, cancellationToken);
        foreach (List<int> run in thumbRuns)
        {
            foreach (int number in run)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GovernedCollections.AddNumber(temporary, governor, thumbPlaced, number);
            }
        }
        // Physical object-stream owners can be extra IDs absent from lookup.
        List<int> ordered = SortedKeys(lookup, temporary, governor, cancellationToken);
        AddRange(temporary, governor, ordered, extraIds, cancellationToken);
        GovernedCollections.SortNumbers(temporary, governor, ordered);
        List<int> part4 = ParserContainers.CreateList<int>(retained);
        Add(retained, governor, part4, catalogNumber);
        foreach (int number in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (number != catalogNumber && !owners.ContainsKey(number) && openDocuments.Contains(number))
            {
                Add(retained, governor, part4, number);
            }
        }

        List<int> part6 = ParserContainers.CreateList<int>(retained);
        Add(retained, governor, part6, pages[0]);
        List<int> part6Private = ParserContainers.CreateList<int>(temporary);
        List<int> part6Shared = ParserContainers.CreateList<int>(temporary);
        List<List<int>> part7 = ParserContainers.CreateList<List<int>>(retained);
        for (int i = 1; i < pages.Count; i++)
        {
            List<int> run = ParserContainers.CreateList<int>(retained);
            Add(retained, governor, run, pages[i]);
            GovernedCollections.AddItem(retained, governor, part7, run, IntPtr.Size);
        }

        List<int> part8 = ParserContainers.CreateList<int>(retained);
        List<int> part9Rest = ParserContainers.CreateList<int>(temporary);
        foreach (int number in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (owners.ContainsKey(number))
            {
                continue;
            }

            if (number == catalogNumber || pageSet.Contains(number) || treeSet.Contains(number) || openDocuments.Contains(number) || outlinePlaced.Contains(number) || thumbPlaced.Contains(number))
            {
                continue;
            }

            if (!users.TryGetValue(number, out GovernedIntSet? use) || use.Count == 0)
            {
                Add(temporary, governor, part9Rest, number);
                continue;
            }

            if (use.Contains(0))
            {
                if (use.Count == 1)
                {
                    Add(temporary, governor, part6Private, number);
                }
                else
                {
                    Add(temporary, governor, part6Shared, number);
                }
            }
            else if (use.Count >= 2)
            {
                Add(retained, governor, part8, number);
            }
            else
            {
                Add(retained, governor, part7[FirstOf(use) - 1], number);
            }
        }

        AddRange(retained, governor, part6, part6Private, cancellationToken);
        AddRange(retained, governor, part6, part6Shared, cancellationToken);
        if (outlinesInFirstPage)
        {
            AddRange(retained, governor, part6, outlineOrder, cancellationToken);
        }

        List<int> part9 = CopyNumbers(tree, retained, governor, cancellationToken);
        foreach (List<int> run in thumbRuns)
        {
            AddRange(retained, governor, part9, run, cancellationToken);
        }

        if (!outlinesInFirstPage)
        {
            AddRange(retained, governor, part9, outlineOrder, cancellationToken);
        }

        AddRange(retained, governor, part9, part9Rest, cancellationToken);

        int placed = checked(part4.Count + part6.Count + part8.Count + part9.Count);
        foreach (IReadOnlyList<int> run in part7)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            placed = checked(placed + run.Count);
        }

        if (placed != checked(lookup.Count - owners.Count + extraIds.Count))
        {
            throw new QpdfParseException("Linearized output lost objects while ordering parts.", null, null);
        }

        retained.Reserve(96L);
        ClassifiedGraph result = new(part4, part6, part7, part8, part9, pages, closures, outlineOrder, outlinesInFirstPage);
        retained.TransferTo(memory);
        return result;
    }

    private static List<List<int>> CollectThumbs(
        GovernedIntDictionary<QpdfObject> lookup,
        IReadOnlyList<int> thumbImages,
        GovernedIntDictionary<GovernedIntSet> users,
        GovernedIntSet openDocuments,
        GovernedIntSet treeSet,
        GovernedIntSet outlinePlaced,
        GovernedIntDictionary<int> owners,
        ParserMemoryScope memory,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope temporary = ParserMemoryScope.Create(governor);
        if (owners is null)
        {
            throw new ArgumentNullException(nameof(owners));
        }

        List<GovernedIntSet> thumbClosures = ParserContainers.CreateList<GovernedIntSet>(temporary);
        GovernedIntDictionary<GovernedIntSet> thumbUsers = GovernedIntDictionary<GovernedIntSet>.Create(temporary, governor, IntPtr.Size, cancellationToken);
        for (int i = 0; i < thumbImages.Count; i++)
        {
            GovernedIntSet thumbClosure = GovernedIntSet.Create(temporary, governor, cancellationToken);
            if (thumbImages[i] > 0 && lookup.TryGetValue(thumbImages[i], out QpdfObject? image) && image is not null)
            {
                GovernedCollections.AddNumber(temporary, governor, thumbClosure, thumbImages[i]);
                WalkSubtree(lookup, image, false, thumbClosure, owners, temporary, governor, cancellationToken);
            }

            // users already indexes the union of all page closures. Inspect
            // only this thumbnail's candidates, rather than visiting every
            // page closure for every page (including absent thumbnails).
            if (thumbClosure.Count != 0)
            {
                using ParserMemoryScope candidatesMemory = ParserMemoryScope.Create(governor);
                List<int> candidates = CopyNumbers(thumbClosure, candidatesMemory, governor, cancellationToken);
                foreach (int number in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    governor.NoteOperation();
                    if (users.ContainsKey(number)) { thumbClosure.Remove(number); }
                }
            }

            Except(thumbClosure, openDocuments, governor, cancellationToken);
            Except(thumbClosure, treeSet, governor, cancellationToken);
            Except(thumbClosure, outlinePlaced, governor, cancellationToken);
            GovernedCollections.AddItem(temporary, governor, thumbClosures, thumbClosure, IntPtr.Size);
            foreach (int number in thumbClosure)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                if (!thumbUsers.TryGetValue(number, out GovernedIntSet? set))
                {
                    set = GovernedIntSet.Create(temporary, governor, cancellationToken);
                    GovernedCollections.Assign(temporary, governor, thumbUsers, number, set);
                }

                GovernedCollections.AddNumber(temporary, governor, set, i);
            }
        }

        GovernedIntSet thumbShared = GovernedIntSet.Create(temporary, governor, cancellationToken);
        foreach (int number in SortedKeys(lookup, temporary, governor, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (thumbUsers.TryGetValue(number, out GovernedIntSet? use) && use.Count >= 2)
            {
                GovernedCollections.AddNumber(temporary, governor, thumbShared, number);
            }
        }

        List<List<int>> runs = ParserContainers.CreateList<List<int>>(memory);
        for (int i = 0; i < thumbImages.Count; i++)
        {
            List<int> run = ParserContainers.CreateList<int>(memory);
            using ParserMemoryScope rowMemory = ParserMemoryScope.Create(governor);
            List<int> ordered = CopyNumbers(thumbClosures[i], rowMemory, governor, cancellationToken);
            GovernedCollections.SortNumbers(rowMemory, governor, ordered);
            foreach (int number in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                if (thumbShared.Contains(number) || users.ContainsKey(number))
                {
                    continue;
                }

                if (number == thumbImages[i])
                {
                    Add(memory, governor, run, number);
                    for (int position = run.Count - 1; position > 0; position--)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        governor.NoteOperation();
                        run[position] = run[position - 1];
                    }
                    run[0] = number;
                }
                else
                {
                    Add(memory, governor, run, number);
                }
            }

            GovernedCollections.AddItem(memory, governor, runs, run, IntPtr.Size);
        }

        List<int> shared = CopyNumbers(thumbShared, temporary, governor, cancellationToken);
        GovernedCollections.SortNumbers(temporary, governor, shared);
        List<int> tail = ParserContainers.CreateList<int>(memory);
        foreach (int number in shared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!users.ContainsKey(number))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Add(memory, governor, tail, number);
            }
        }

        if (tail.Count > 0)
        {
            GovernedCollections.AddItem(memory, governor, runs, tail, IntPtr.Size);
        }

        return runs;
    }

    private static List<int> CollectOutlines(
        GovernedIntDictionary<QpdfObject> lookup,
        PdfDictionary catalog,
        int catalogNumber,
        IReadOnlyList<GovernedIntSet> closures,
        GovernedIntSet openDocuments,
        GovernedIntSet treeSet,
        GovernedIntDictionary<int> owners,
        ParserMemoryScope memory,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope temporary = ParserMemoryScope.Create(governor);
        List<int> ordered = ParserContainers.CreateList<int>(memory);
        if (!GovernedCollections.TryGet(catalog, "Outlines"u8, governor, cancellationToken, out QpdfObject? outlinesRaw) || outlinesRaw is null || outlinesRaw is PdfNull)
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

        if (owners is null)
        {
            throw new ArgumentNullException(nameof(owners));
        }

        GovernedIntSet outlineSet = GovernedIntSet.Create(temporary, governor, cancellationToken);
        WalkSubtree(lookup, outlinesReference, true, outlineSet, owners, temporary, governor, cancellationToken);
        int outlineRoot = OwnerOf(owners, outlinesReference.ObjectNumber);
        if (!outlineSet.Contains(outlineRoot))
        {
            throw new QpdfParseException("Linearized output with fragmented outlines is not implemented.", null, catalogNumber);
        }

        GovernedIntSet placedElsewhere = GovernedIntSet.Create(temporary, governor, cancellationToken);
        for (int pageIndex = 0; pageIndex < closures.Count; pageIndex++)
        {
            Union(temporary, governor, placedElsewhere, closures[pageIndex], cancellationToken);
        }

        Union(temporary, governor, placedElsewhere, openDocuments, cancellationToken);
        Union(temporary, governor, placedElsewhere, treeSet, cancellationToken);
        GovernedCollections.AddNumber(temporary, governor, placedElsewhere, catalogNumber);
        GovernedIntSet orderedAdded = GovernedIntSet.Create(temporary, governor, cancellationToken);
        if (!placedElsewhere.Contains(outlineRoot))
        {
            Add(memory, governor, ordered, outlineRoot);
            GovernedCollections.AddNumber(temporary, governor, orderedAdded, outlineRoot);
        }

        foreach (int number in SortedKeys(lookup, temporary, governor, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            int resolved = OwnerOf(owners, number);
            if (!outlineSet.Contains(resolved) || placedElsewhere.Contains(resolved) || !GovernedCollections.AddNumber(temporary, governor, orderedAdded, resolved))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Add(memory, governor, ordered, resolved);
        }

        return ordered;
    }

    private static bool IsUseOutlines(PdfDictionary catalog, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        return GovernedCollections.TryGet(catalog, "PageMode"u8, governor, cancellationToken, out QpdfObject? mode)
            && mode is PdfName modeName
            && modeName.RawSpan.SequenceEqual("UseOutlines"u8);
    }

    private static GovernedIntSet CollectOpenDocuments(GovernedIntDictionary<QpdfObject> lookup,
        PdfDictionary catalog, GovernedIntDictionary<int> owners, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        GovernedIntSet open = GovernedIntSet.Create(memory, governor, cancellationToken);
        CollectOpen(lookup, catalog, owners, open, "ViewerPreferences"u8, memory, governor, cancellationToken);
        CollectOpen(lookup, catalog, owners, open, "PageMode"u8, memory, governor, cancellationToken);
        CollectOpen(lookup, catalog, owners, open, "Threads"u8, memory, governor, cancellationToken);
        CollectOpen(lookup, catalog, owners, open, "OpenAction"u8, memory, governor, cancellationToken);
        CollectOpen(lookup, catalog, owners, open, "AcroForm"u8, memory, governor, cancellationToken);
        return open;
    }

    private static void CollectOpen(GovernedIntDictionary<QpdfObject> lookup, PdfDictionary catalog,
        GovernedIntDictionary<int> owners, GovernedIntSet open, ReadOnlySpan<byte> key,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (GovernedCollections.TryGet(catalog, key, governor, cancellationToken, out QpdfObject value)
            && value is not PdfNull)
        {
            // Walk the catalog value itself: direct containers have indirect
            // descendants, and an indirect root belongs to the open-document part.
            WalkSubtree(lookup, value, false, open, owners, memory, governor, cancellationToken);
        }
    }

    private static void WalkSubtree(
        GovernedIntDictionary<QpdfObject> lookup,
        QpdfObject seed,
        bool top,
        GovernedIntSet seen,
        GovernedIntDictionary<int> owners,
        ParserMemoryScope memory,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope temporary = ParserMemoryScope.Create(governor);
        if (owners is null)
        {
            throw new ArgumentNullException(nameof(owners));
        }

        GovernedIntSet visited = GovernedIntSet.Create(temporary, governor, cancellationToken);
        List<(QpdfObject Value, bool Top)> stack = ParserContainers.CreateList<(QpdfObject Value, bool Top)>(temporary);
        Push(temporary, governor, stack, seed, top);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            (QpdfObject current, bool currentTop) = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            if (current is PdfReference reference)
            {
                if (!lookup.TryGetValue(reference.ObjectNumber, out QpdfObject? target) || target is null)
                {
                    continue;
                }

                if (!GovernedCollections.AddNumber(temporary, governor, visited, reference.ObjectNumber))
                {
                    continue;
                }

                if (!currentTop && target is PdfDictionary dict
                    && GovernedCollections.TryGet(dict, "Type"u8, governor, cancellationToken, out QpdfObject kind)
                    && kind is PdfName name
                    && name.RawSpan.SequenceEqual("Page"u8))
                {
                    continue;
                }

                GovernedCollections.AddNumber(memory, governor, seen, OwnerOf(owners, reference.ObjectNumber));
                cancellationToken.ThrowIfCancellationRequested();
                Push(temporary, governor, stack, target, false);
                continue;
            }

            if (current is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Push(temporary, governor, stack, array[i], false);
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
                using PdfDictionary.EntryEnumerator dictionaryEntries = dictionary.GetEntryEnumerator();
                while (dictionaryEntries.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    governor.NoteOperation();
                    PdfName key = dictionaryEntries.Current.Key;
                    if (current is PdfStream
                        && (key.RawSpan.SequenceEqual("Length"u8) || key.RawSpan.SequenceEqual("Filter"u8) || key.RawSpan.SequenceEqual("DecodeParms"u8)))
                    {
                        continue;
                    }

                    QpdfObject entry = dictionaryEntries.Current.Value;
                    if (entry is not PdfNull)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Push(temporary, governor, stack, entry, false);
                    }
                }
            }
        }
    }
    private static List<int> CollectPages(
        PdfDictionary catalog,
        GovernedIntDictionary<QpdfObject> lookup,
        ParserMemoryScope memory,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope temporary = ParserMemoryScope.Create(governor);
        if (!GovernedCollections.TryGet(catalog, "Pages"u8, governor, cancellationToken, out QpdfObject pagesRaw)
            || pagesRaw is not PdfReference pagesReference)
        {
            throw new QpdfParseException("Linearized output needs a reachable pages tree.", null, 1);
        }

        List<int> pages = ParserContainers.CreateList<int>(memory);
        GovernedIntSet seen = GovernedIntSet.Create(temporary, governor, cancellationToken);
        List<int> stack = ParserContainers.CreateList<int>(temporary);
        Add(temporary, governor, stack, pagesReference.ObjectNumber);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            int number = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            if (!GovernedCollections.AddNumber(temporary, governor, seen, number))
            {
                continue;
            }

            if (!lookup.TryGetValue(number, out QpdfObject? value) || value is not PdfDictionary node)
            {
                continue;
            }

            if (GovernedCollections.TryGet(node, "Type"u8, governor, cancellationToken, out QpdfObject kind)
                && kind is PdfName name
                && name.RawSpan.SequenceEqual("Page"u8))
            {
                Add(memory, governor, pages, number);
                continue;
            }

            if (GovernedCollections.TryGet(node, "Kids"u8, governor, cancellationToken, out QpdfObject kids) && kids is PdfArray array)
            {
                for (int i = array.Count - 1; i >= 0; i--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    governor.NoteOperation();
                    if (array[i] is PdfReference kid)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Add(temporary, governor, stack, kid.ObjectNumber);
                    }
                }
            }
        }

        return pages;
    }

    private readonly record struct PageWalk(GovernedIntSet Closure, int Thumb);

    private static PageWalk WalkPage(
        GovernedIntDictionary<QpdfObject> lookup,
        int pageNumber,
        GovernedIntDictionary<int> owners,
        ParserMemoryScope memory,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope temporary = ParserMemoryScope.Create(governor);
        if (owners is null)
        {
            throw new ArgumentNullException(nameof(owners));
        }

        GovernedIntSet seen = GovernedIntSet.Create(memory, governor, cancellationToken);
        GovernedCollections.AddNumber(memory, governor, seen, pageNumber);
        GovernedIntSet visited = GovernedIntSet.Create(temporary, governor, cancellationToken);
        int thumb = -1;
        List<(QpdfObject Value, bool Top)> stack = ParserContainers.CreateList<(QpdfObject Value, bool Top)>(temporary);
        if (lookup.TryGetValue(pageNumber, out QpdfObject? page))
        {
            Push(temporary, governor, stack, page, true);
        }

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            (QpdfObject current, bool top) = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            if (current is PdfReference reference)
            {
                if (!lookup.TryGetValue(reference.ObjectNumber, out QpdfObject? target))
                {
                    continue;
                }

                if (!GovernedCollections.AddNumber(temporary, governor, visited, reference.ObjectNumber))
                {
                    continue;
                }

                if (target is PdfDictionary dict)
                {
                    if (GovernedCollections.TryGet(dict, "Type"u8, governor, cancellationToken, out QpdfObject kind)
                        && kind is PdfName name)
                    {
                        if (name.RawSpan.SequenceEqual("Page"u8) && !top)
                        {
                            continue;
                        }

                        if (name.RawSpan.SequenceEqual("Pages"u8))
                        {
                            continue;
                        }
                    }
                }

                GovernedCollections.AddNumber(memory, governor, seen, OwnerOf(owners, reference.ObjectNumber));
                cancellationToken.ThrowIfCancellationRequested();
                Push(temporary, governor, stack, target, false);
                continue;
            }

            if (current is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Push(temporary, governor, stack, array[i], false);
                }

                continue;
            }

            if (current is PdfStream contentStream)
            {
                using PdfDictionary.EntryEnumerator streamEntries = contentStream.Dictionary.GetEntryEnumerator();
                while (streamEntries.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    governor.NoteOperation();
                    PdfName key = streamEntries.Current.Key;
                    if (key.RawSpan.SequenceEqual("Length"u8) || key.RawSpan.SequenceEqual("Filter"u8) || key.RawSpan.SequenceEqual("DecodeParms"u8))
                    {
                        continue;
                    }

                    QpdfObject entry = streamEntries.Current.Value;
                    if (entry is not PdfNull)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Push(temporary, governor, stack, entry, false);
                    }
                }

                continue;
            }

            if (current is PdfDictionary dictionary)
            {
                bool topPage = top
                    && GovernedCollections.TryGet(dictionary, "Type"u8, governor, cancellationToken, out QpdfObject pageKind)
                    && pageKind is PdfName pageName
                    && pageName.RawSpan.SequenceEqual("Page"u8);
                using PdfDictionary.EntryEnumerator dictionaryEntries = dictionary.GetEntryEnumerator();
                while (dictionaryEntries.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    governor.NoteOperation();
                    PdfName key = dictionaryEntries.Current.Key;
                    if (topPage && key.RawSpan.SequenceEqual("Parent"u8))
                    {
                        continue;
                    }

                    if (topPage && key.RawSpan.SequenceEqual("Thumb"u8))
                    {
                        if (dictionaryEntries.Current.Value is PdfReference thumbReference)
                        {
                            thumb = thumbReference.ObjectNumber;
                        }

                        continue;
                    }

                    QpdfObject entry = dictionaryEntries.Current.Value;
                    if (entry is not PdfNull)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Push(temporary, governor, stack, entry, false);
                    }
                }
            }
        }

        return new PageWalk(seen, thumb);
    }
    private static List<int> CollectTree(
        PdfDictionary catalog,
        GovernedIntDictionary<QpdfObject> lookup,
        GovernedIntSet pageSet,
        GovernedIntDictionary<int> owners,
        ParserMemoryScope memory,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope temporary = ParserMemoryScope.Create(governor);
        if (owners is null)
        {
            throw new ArgumentNullException(nameof(owners));
        }

        List<int> tree = ParserContainers.CreateList<int>(memory);
        GovernedIntSet added = GovernedIntSet.Create(temporary, governor, cancellationToken);
        if (!GovernedCollections.TryGet(catalog, "Pages"u8, governor, cancellationToken, out QpdfObject pagesRaw)
            || pagesRaw is not PdfReference pagesReference)
        {
            return tree;
        }

        GovernedIntSet visited = GovernedIntSet.Create(temporary, governor, cancellationToken);
        List<int> stack = ParserContainers.CreateList<int>(temporary);
        Add(temporary, governor, stack, pagesReference.ObjectNumber);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            int number = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            if (!GovernedCollections.AddNumber(temporary, governor, visited, number))
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

            if (GovernedCollections.AddNumber(temporary, governor, added, OwnerOf(owners, number)))
            {
                Add(memory, governor, tree, OwnerOf(owners, number));
            }
            if (GovernedCollections.TryGet(node, "Kids"u8, governor, cancellationToken, out QpdfObject kids) && kids is PdfArray array)
            {
                for (int i = array.Count - 1; i >= 0; i--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    governor.NoteOperation();
                    if (array[i] is PdfReference kid)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Add(temporary, governor, stack, kid.ObjectNumber);
                    }
                }
            }
        }

        return tree;
    }

    private static int OwnerOf(GovernedIntDictionary<int> owners, int number)
    {
        return owners.TryGetValue(number, out int container) ? container : number;
    }

    private static List<int> SortedKeys(GovernedIntDictionary<QpdfObject> lookup, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        List<int> keys = ParserContainers.CreateList<int>(memory);
        foreach (KeyValuePair<int, QpdfObject> entry in lookup)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(memory, governor, keys, entry.Key);
        }
        GovernedCollections.SortNumbers(memory, governor, keys);
        return keys;
    }

    private static void Add(ParserMemoryScope memory, ResourceGovernor governor, List<int> values, int value)
        => GovernedCollections.AddItem(memory, governor, values, value, sizeof(int));

    private static void Push(ParserMemoryScope memory, ResourceGovernor governor,
        List<(QpdfObject Value, bool Top)> values, QpdfObject value, bool top)
        => GovernedCollections.AddItem(memory, governor, values, (value, top), 16L);

    private static void AddRange(ParserMemoryScope memory, ResourceGovernor governor, List<int> target,
        IReadOnlyList<int> source, CancellationToken cancellationToken)
    {
        for (int index = 0; index < source.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(memory, governor, target, source[index]);
        }
    }

    private static List<int> CopyNumbers(IReadOnlyList<int> source, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        List<int> result = ParserContainers.CreateList<int>(memory);
        AddRange(memory, governor, result, source, cancellationToken);
        return result;
    }

    private static List<int> CopyNumbers(GovernedIntSet source, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        List<int> result = ParserContainers.CreateList<int>(memory);
        foreach (int number in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(memory, governor, result, number);
        }
        return result;
    }

    private static GovernedIntSet CopySet(IReadOnlyList<int> source, ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        GovernedIntSet result = GovernedIntSet.Create(memory, governor, cancellationToken);
        for (int index = 0; index < source.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GovernedCollections.AddNumber(memory, governor, result, source[index]);
        }
        return result;
    }

    private static void Union(ParserMemoryScope memory, ResourceGovernor governor, GovernedIntSet target,
        GovernedIntSet source, CancellationToken cancellationToken)
    {
        foreach (int number in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GovernedCollections.AddNumber(memory, governor, target, number);
        }
    }

    private static void Except(GovernedIntSet target, GovernedIntSet source,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        foreach (int number in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            target.Remove(number);
        }
    }

    private static int FirstOf(GovernedIntSet set)
    {
        foreach (int value in set)
        {
            return value;
        }

        throw new QpdfParseException("Linearized output reached an empty page set.", null, null);
    }

    private static GovernedIntDictionary<int> AssignNumbers(
        IReadOnlyList<IReadOnlyList<int>> part7,
        IReadOnlyList<int> part8,
        IReadOnlyList<int> part9,
        IReadOnlyList<int> part4,
        IReadOnlyList<int> part6,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken cancellationToken,
        out int lindictNumber,
        out int hintNumber)
    {
        GovernedIntDictionary<int> renumber = GovernedIntDictionary<int>.Create(memory, governor, sizeof(int), cancellationToken);
        int next = 1;
        for (int runIndex = 0; runIndex < part7.Count; runIndex++)
        {
            IReadOnlyList<int> run = part7[runIndex];
            for (int index = 0; index < run.Count; index++)
            {
                int number = run[index];
                cancellationToken.ThrowIfCancellationRequested();
                GovernedCollections.Assign(memory, governor, renumber, number, next);
                next = checked(next + 1);
            }
        }

        for (int index = 0; index < part8.Count; index++)
        {
            int number = part8[index];
            cancellationToken.ThrowIfCancellationRequested();
            GovernedCollections.Assign(memory, governor, renumber, number, next);
            next = checked(next + 1);
        }

        for (int index = 0; index < part9.Count; index++)
        {
            int number = part9[index];
            cancellationToken.ThrowIfCancellationRequested();
            GovernedCollections.Assign(memory, governor, renumber, number, next);
            next = checked(next + 1);
        }

        lindictNumber = next;
        next = checked(next + 1);
        for (int index = 0; index < part4.Count; index++)
        {
            int number = part4[index];
            cancellationToken.ThrowIfCancellationRequested();
            GovernedCollections.Assign(memory, governor, renumber, number, next);
            next = checked(next + 1);
        }

        hintNumber = next;
        next = checked(next + 1);
        for (int index = 0; index < part6.Count; index++)
        {
            int number = part6[index];
            cancellationToken.ThrowIfCancellationRequested();
            GovernedCollections.Assign(memory, governor, renumber, number, next);
            next = checked(next + 1);
        }

        return renumber;
    }

    private static void AssignStreamedNumbers(
        ClassifiedGraph graph,
        GovernedIntDictionary<List<int>> containerMembers,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken cancellationToken,
        out GovernedIntDictionary<int> renumber,
        out GovernedIntDictionary<(int Container, int Index)> members,
        out int lindictNumber,
        out int hintNumber,
        out int xrefFirstNumber,
        out int xrefMainNumber,
        out int firstSize,
        out int mainSize,
        out int maxNumber,
        out int maxMemberIndex)
    {
        renumber = GovernedIntDictionary<int>.Create(memory, governor, sizeof(int), cancellationToken);
        members = GovernedIntDictionary<(int Container, int Index)>.Create(memory, governor, 8L, cancellationToken);
        int next = 1;
        for (int runIndex = 0; runIndex < graph.Part7.Count; runIndex++)
        {
            IReadOnlyList<int> run = graph.Part7[runIndex];
            for (int index = 0; index < run.Count; index++)
            {
                int number = run[index];
                cancellationToken.ThrowIfCancellationRequested();
                GovernedCollections.Assign(memory, governor, renumber, number, next);
                next = checked(next + 1);
            }
        }

        for (int index = 0; index < graph.Part8.Count; index++)
        {
            int number = graph.Part8[index];
            cancellationToken.ThrowIfCancellationRequested();
            GovernedCollections.Assign(memory, governor, renumber, number, next);
            next = checked(next + 1);
        }

        for (int index = 0; index < graph.Part9.Count; index++)
        {
            int number = graph.Part9[index];
            cancellationToken.ThrowIfCancellationRequested();
            GovernedCollections.Assign(memory, governor, renumber, number, next);
            next = checked(next + 1);
        }

        int xrefMain = next;
        next = checked(next + 1);
        maxMemberIndex = 0;
        for (int runIndex = 0; runIndex < graph.Part7.Count; runIndex++)
        {
            IReadOnlyList<int> run = graph.Part7[runIndex];
            NumberMembers(run, containerMembers, renumber, members, memory, governor, cancellationToken, ref next, ref maxMemberIndex);
        }

        NumberMembers(graph.Part8, containerMembers, renumber, members, memory, governor, cancellationToken, ref next, ref maxMemberIndex);
        NumberMembers(graph.Part9, containerMembers, renumber, members, memory, governor, cancellationToken, ref next, ref maxMemberIndex);
        mainSize = next;

        int lindict = next;
        next = checked(next + 1);
        int xrefFirst = next;
        next = checked(next + 1);
        for (int index = 0; index < graph.Part4.Count; index++)
        {
            int number = graph.Part4[index];
            cancellationToken.ThrowIfCancellationRequested();
            GovernedCollections.Assign(memory, governor, renumber, number, next);
            next = checked(next + 1);
        }

        int hint = next;
        next = checked(next + 1);
        for (int index = 0; index < graph.Part6.Count; index++)
        {
            int number = graph.Part6[index];
            cancellationToken.ThrowIfCancellationRequested();
            GovernedCollections.Assign(memory, governor, renumber, number, next);
            next = checked(next + 1);
        }

        NumberMembers(graph.Part4, containerMembers, renumber, members, memory, governor, cancellationToken, ref next, ref maxMemberIndex);
        NumberMembers(graph.Part6, containerMembers, renumber, members, memory, governor, cancellationToken, ref next, ref maxMemberIndex);
        lindictNumber = lindict;
        hintNumber = hint;
        xrefFirstNumber = xrefFirst;
        xrefMainNumber = xrefMain;
        firstSize = next;
        maxNumber = checked(next - 1);
    }

    private static void NumberMembers(
        IReadOnlyList<int> units,
        GovernedIntDictionary<List<int>> containerMembers,
        GovernedIntDictionary<int> renumber,
        GovernedIntDictionary<(int Container, int Index)> members,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken cancellationToken,
        ref int next,
        ref int maxMemberIndex)
    {
        for (int unitIndex = 0; unitIndex < units.Count; unitIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            int unit = units[unitIndex];
            if (!containerMembers.TryGetValue(unit, out List<int>? owned) || owned is null)
            {
                continue;
            }

            if (!renumber.TryGetValue(unit, out int containerNew))
            {
                throw new QpdfException("Linearized publication lost an object-stream container.");
            }

            for (int i = 0; i < owned.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GovernedCollections.Assign(memory, governor, renumber, owned[i], next);
                GovernedCollections.Assign(memory, governor, members, owned[i], (containerNew, i));
                if (i > maxMemberIndex)
                {
                    maxMemberIndex = i;
                }

                next = checked(next + 1);
            }
        }
    }
}
