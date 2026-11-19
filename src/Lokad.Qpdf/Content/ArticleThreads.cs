using System.Runtime.InteropServices;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

internal sealed class ArticleThreads
{
    private static readonly PdfName First = PdfName.FromAscii("F");
    private static readonly PdfName Next = PdfName.FromAscii("N");
    private static readonly PdfName Previous = PdfName.FromAscii("V");
    private static readonly PdfName Page = PdfName.FromAscii("P");
    private static readonly PdfName Thread = PdfName.FromAscii("T");
    private static readonly PdfName Rectangle = PdfName.FromAscii("R");
    private static readonly PdfName Registrations = PdfName.FromAscii("B");

    private readonly record struct Article(PdfReference Reference, PdfDictionary Header, int FirstBead, int Count);
    private readonly record struct Bead(int Article, int Position, PdfDictionary Header, PdfReference? Page);
    private readonly record struct Occurrence(Bead Bead, int Order, PdfReference Source, PdfReference Reference, PdfReference Page);
    private readonly record struct OccurrenceOrder : IComparer<Occurrence>
    {
        public int Compare(Occurrence left, Occurrence right)
        {
            int article = left.Bead.Article.CompareTo(right.Bead.Article);
            if (article != 0) { return article; }
            int position = left.Bead.Position.CompareTo(right.Bead.Position);
            return position != 0 ? position : left.Order.CompareTo(right.Order);
        }
    }

    private readonly PdfArray _originalOrder;
    private readonly List<Article> _articles;
    private readonly Dictionary<PdfReference, Bead> _beads;
    private readonly List<PdfReference> _beadOrder;
    private readonly ParserMemoryScope _memory;
    private Dictionary<string, PdfReference>? _titles;

    private ArticleThreads(PdfArray originalOrder, List<Article> articles, Dictionary<PdfReference, Bead> beads,
        List<PdfReference> beadOrder, ParserMemoryScope memory)
    {
        _originalOrder = originalOrder;
        _articles = articles;
        _beads = beads;
        _beadOrder = beadOrder;
        _memory = memory;
    }

    internal static ArticleThreads? Read(QpdfObject? role, IIndirectResolver resolver, QpdfLimits limits,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        if (CatalogCopy.ResolveValue(role, resolver, governor, token) is not PdfArray threads) { return null; }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        List<Article> articles = ParserContainers.CreateList<Article>(memory);
        Dictionary<PdfReference, Bead> beads = ParserContainers.CreateDictionary<PdfReference, Bead>(memory);
        List<PdfReference> beadOrder = ParserContainers.CreateList<PdfReference>(memory);
        HashSet<PdfReference> visited = ParserContainers.CreateSet<PdfReference>(scratch);
        List<(PdfReference Reference, PdfDictionary Header, PdfReference? Page)> ring =
            ParserContainers.CreateList<(PdfReference, PdfDictionary, PdfReference?)>(scratch);
        foreach (QpdfObject value in threads.BorrowedItems)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (value is not PdfReference thread
                || CatalogCopy.ResolveDictionary(thread, resolver, governor, token) is not PdfDictionary article
                || !HasOptionalType(article, "Thread"u8, resolver, governor, token)
                || !article.TryGet(First, out QpdfObject firstValue) || firstValue is not PdfReference first)
            { continue; }
            ring.Clear();
            if (!ReadRing(thread, first, resolver, visited, ring, limits, scratch, governor, token)) { continue; }
            int index = articles.Count;
            ParserContainers.AddItem(memory, articles, new Article(thread, article, beadOrder.Count, ring.Count), 24L);
            for (int position = 0; position < ring.Count; position++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                var bead = ring[position];
                ParserContainers.EnsureEntryCapacity(memory, beads, 24L);
                beads.Add(bead.Reference, new(index, position, bead.Header, bead.Page));
                ParserContainers.AddItem(memory, beadOrder, bead.Reference, IntPtr.Size);
            }
        }
        if (articles.Count == 0) { return null; }
        memory.Reserve(64L);
        return new(threads, articles, beads, beadOrder, memory);
    }

    internal (PdfReference? Thread, PdfReference? Bead) ResolveAction(QpdfObject? destination, QpdfObject? bead,
        IIndirectResolver resolver, IReadOnlyDictionary<PdfReference, PdfReference> replacements,
        ResourceGovernor governor, CancellationToken token)
    {
        PdfReference? thread = CatalogCopy.ResolveValue(destination, resolver, governor, token) switch
        {
            PdfInteger ordinal when ordinal.Value >= 0 && ordinal.Value < _originalOrder.Count
                => _originalOrder[(int)ordinal.Value] as PdfReference,
            PdfByteString title => ResolveTitle(title, resolver, governor, token),
            PdfDictionary => destination as PdfReference,
            _ => null,
        };
        if (thread is null || !replacements.TryGetValue(thread, out PdfReference? projectedThread))
        { return default; }
        // A dead copied reference can become PDF null. Only an absent
        // selector supplies the thread's default entry point.
        if (bead is null) { return (projectedThread, null); }

        PdfReference? reference = CatalogCopy.ResolveValue(bead, resolver, governor, token) switch
        {
            PdfInteger ordinal when ordinal.Value >= 0 => FindBead(thread, ordinal.Value, resolver, governor, token),
            PdfDictionary => bead as PdfReference,
            _ => null,
        };
        if (reference is null || !_beads.TryGetValue(reference, out Bead sourceBead)
            || !_articles[sourceBead.Article].Reference.Equals(thread)
            || !replacements.TryGetValue(reference, out PdfReference? projectedBead))
        { return default; }
        return (projectedThread, projectedBead);
    }

    private PdfReference? FindBead(PdfReference thread, long ordinal, IIndirectResolver resolver,
        ResourceGovernor governor, CancellationToken token)
    {
        if (CatalogCopy.ResolveDictionary(thread, resolver, governor, token) is not PdfDictionary header
            || !header.TryGet(First, out QpdfObject first) || first is not PdfReference reference
            || !_beads.TryGetValue(reference, out Bead bead)) { return null; }
        Article article = _articles[bead.Article];
        return ordinal < article.Count ? _beadOrder[article.FirstBead + (int)ordinal] : null;
    }

    private PdfReference? ResolveTitle(PdfByteString title, IIndirectResolver resolver,
        ResourceGovernor governor, CancellationToken token)
    {
        if (_titles is null)
        {
            _titles = ParserContainers.CreateDictionary<string, PdfReference>(_memory);
            foreach (QpdfObject value in _originalOrder.BorrowedItems)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (value is not PdfReference reference
                    || CatalogCopy.ResolveDictionary(reference, resolver, governor, token) is not PdfDictionary header
                    || !PdfDictionaryLookup.TryGet(header, "I"u8, governor, token, out QpdfObject info)
                    || CatalogCopy.ResolveDictionary(info, resolver, governor, token) is not PdfDictionary information
                    || !PdfDictionaryLookup.TryGet(information, "Title"u8, governor, token, out QpdfObject rawTitle)
                    || CatalogCopy.ResolveValue(rawTitle, resolver, governor, token) is not PdfByteString text)
                { continue; }
                using ParserMemoryScope textMemory = ParserMemoryScope.Create(governor);
                string name = PdfText.Decode(text.ValueMemory, textMemory, governor, token);
                if (_titles.ContainsKey(name)) { continue; }
                ParserContainers.EnsureEntryCapacity(_memory, _titles, IntPtr.Size * 2L);
                textMemory.TransferTo(_memory);
                _titles.Add(name, reference);
            }
        }
        using ParserMemoryScope lookup = ParserMemoryScope.Create(governor);
        return _titles.GetValueOrDefault(PdfText.Decode(title.ValueMemory, lookup, governor, token));
    }

    private static bool ReadRing(PdfReference thread, PdfReference first, IIndirectResolver resolver,
        HashSet<PdfReference> visited, List<(PdfReference Reference, PdfDictionary Header, PdfReference? Page)> ring,
        QpdfLimits limits, ParserMemoryScope scratch, ResourceGovernor governor, CancellationToken token)
    {
        PdfReference current = first;
        PdfReference? prior = null, last = null;
        Span<double> rectangle = stackalloc double[4];
        while (true)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (visited.Contains(current)
                || CatalogCopy.ResolveDictionary(current, resolver, governor, token) is not PdfDictionary header
                || !HasOptionalType(header, "Bead"u8, resolver, governor, token)
                || !header.TryGet(Previous, out QpdfObject v) || v is not PdfReference previous
                || prior is not null && !prior.Equals(previous)
                || !header.TryGet(Next, out QpdfObject n) || n is not PdfReference next
                || !header.TryGet(Page, out QpdfObject owner) || owner is not (PdfReference or PdfNull)
                || !header.TryGet(Rectangle, out QpdfObject rect)
                || !PdfGeometry.TryReadFiniteNumbers(CatalogCopy.ResolveValue(rect, resolver, governor, token), resolver, rectangle))
            { return false; }
            if (header.TryGet(Thread, out QpdfObject owningThread) && owningThread is not PdfNull)
            {
                if (!thread.Equals(owningThread)) { return false; }
            }
            else if (prior is null) { return false; }

            if (visited.Count >= limits.MaxContainerSize)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), visited.Count + 1L, limits.MaxContainerSize); }
            _ = ParserContainers.AddSetItem(scratch, visited, current, IntPtr.Size);
            ParserContainers.AddItem(scratch, ring, (current, header, owner as PdfReference), 24L);
            last ??= previous;
            if (next.Equals(first)) { return current.Equals(last); }
            prior = current;
            current = next;
        }
    }

    private static bool HasOptionalType(PdfDictionary dictionary, ReadOnlySpan<byte> expected, IIndirectResolver resolver,
        ResourceGovernor governor, CancellationToken token)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, "Type"u8, governor, token, out QpdfObject raw)) { return true; }
        return CatalogCopy.ResolveValue(raw, resolver, governor, token) switch
        {
            PdfNull => true,
            PdfName name => PdfDictionaryLookup.NameEquals(name, expected, governor, token),
            _ => false,
        };
    }

    internal static bool HasRegistrations(IReadOnlyDictionary<int, QpdfObject> pool, ReadOnlySpan<PdfReference> pages,
        ResourceGovernor governor, CancellationToken token)
    {
        foreach (PdfReference page in pages)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (pool.TryGetValue(page.ObjectNumber, out QpdfObject? value)
                && value is PdfDictionary header && HasPageBeads(header))
            { return true; }
        }
        return false;
    }

    internal static bool HasPageBeads(PdfDictionary header) => header.TryGet(Registrations, out _);

    internal static void RememberDuplicate(PdfDictionary header, int occurrence, int canonical, int generation,
        ref Dictionary<int, PdfReference>? aliases, ParserMemoryScope memory)
    {
        if (occurrence == canonical || !header.TryGet(Registrations, out _)) { return; }
        aliases ??= ParserContainers.CreateDictionary<int, PdfReference>(memory);
        ParserContainers.EnsureEntryCapacity(memory, aliases, IntPtr.Size);
        memory.Reserve(32L);
        aliases.Add(occurrence, new PdfReference(canonical, generation));
    }

    internal static (PdfDictionary Catalog, Dictionary<int, QpdfObject>? Edits) Project(ArticleThreads? source,
        PdfDictionary catalog, IIndirectResolver resolver, ReadOnlySpan<PdfReference> pages,
        IReadOnlyDictionary<int, PdfReference>? canonicalPages, IReadOnlyDictionary<int, double[]>? rotationFrames,
        Dictionary<int, QpdfObject>? edits, int nextNumber, QpdfLimits limits,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        List<Occurrence>? occurrences = null;
        foreach (PdfReference page in pages)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (CatalogCopy.ResolveDictionary(page, resolver, governor, token) is not PdfDictionary header
                || !PdfDictionaryLookup.TryGet(header, "B"u8, governor, token, out QpdfObject raw))
            { continue; }

            PdfReference canonical = canonicalPages?.GetValueOrDefault(page.ObjectNumber) ?? page;
            PdfArray? registrations = null;
            if (source is not null && CatalogCopy.ResolveValue(raw, resolver, governor, token) is PdfArray array)
            {
                using ParserMemoryScope pageScratch = ParserMemoryScope.Create(governor);
                List<QpdfObject> refs = ParserContainers.CreateList(pageScratch);
                HashSet<PdfReference> unique = ParserContainers.CreateSet<PdfReference>(pageScratch);
                foreach (QpdfObject entry in array.BorrowedItems)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    if (entry is not PdfReference link || !source._beads.TryGetValue(link, out Bead bead)
                        || !canonical.Equals(bead.Page) || !ParserContainers.AddSetItem(pageScratch, unique, link, IntPtr.Size))
                    { continue; }
                    occurrences ??= ParserContainers.CreateList<Occurrence>(scratch);
                    if (occurrences.Count >= limits.MaxContainerSize)
                    { throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), occurrences.Count + 1L, limits.MaxContainerSize); }
                    PdfReference replacement = Allocate(ref nextNumber, graph);
                    ParserContainers.AddItem(scratch, occurrences, new(bead, occurrences.Count, link, replacement, page), 56L);
                    ParserContainers.AddItem(pageScratch, refs, replacement);
                }
                if (refs.Count != 0) { registrations = CopyArray(refs, graph); }
            }

            PdfDictionary edited = registrations is null ? PdfDictionaryRebuild.RemoveEntry(graph, header, "B"u8)
                : PdfDictionaryRebuild.ReplaceEntry(graph, header, "B"u8, registrations);
            edits ??= ParserContainers.CreateDictionary<int, QpdfObject>(memory);
            Store(edits, page.ObjectNumber, edited, limits, memory, governor);
        }

        Dictionary<PdfReference, PdfReference>? replacements = null;
        if (source is not null && occurrences is { Count: > 0 })
        {
            GovernedSort.Sort(CollectionsMarshal.AsSpan(occurrences), new OccurrenceOrder(), governor, token);
            replacements = ParserContainers.CreateDictionary<PdfReference, PdfReference>(scratch);
            List<QpdfObject> threads = ParserContainers.CreateList(scratch);
            int start = 0;
            while (start < occurrences.Count)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                int article = occurrences[start].Bead.Article;
                int end = start + 1;
                while (end < occurrences.Count && occurrences[end].Bead.Article == article)
                { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); end++; }
                PdfReference newThread = Allocate(ref nextNumber, graph);
                ParserContainers.AddItem(scratch, threads, newThread);
                Article original = source._articles[article];
                Remember(replacements, original.Reference, newThread);
                PdfDictionary thread = PdfDictionaryRebuild.ReplaceEntry(graph, original.Header,
                    "F"u8, occurrences[start].Reference);
                edits ??= ParserContainers.CreateDictionary<int, QpdfObject>(memory);
                Store(edits, newThread.ObjectNumber, thread, limits, memory, governor);
                for (int index = start; index < end; index++)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    Occurrence current = occurrences[index];
                    Remember(replacements, current.Source, current.Reference);
                    PdfReference previous = occurrences[index == start ? end - 1 : index - 1].Reference;
                    PdfReference next = occurrences[index + 1 == end ? start : index + 1].Reference;
                    double[]? frame = rotationFrames?.GetValueOrDefault(current.Page.ObjectNumber);
                    PdfDictionary bead = BuildBead(current, next, previous, index == start ? newThread : null, frame, resolver, graph);
                    Store(edits, current.Reference.ObjectNumber, bead, limits, memory, governor);
                }
                start = end;
            }
            catalog = PdfDictionaryRebuild.ReplaceEntry(graph, catalog, "Threads"u8, CopyArray(threads, graph));
        }
        else if (PdfDictionaryLookup.TryGet(catalog, "Threads"u8, governor, token, out _))
        { catalog = PdfDictionaryRebuild.RemoveEntry(graph, catalog, "Threads"u8); }
        graph.Memory.TransferTo(memory, graph.Bytes);
        (catalog, Dictionary<int, QpdfObject>? actionEdits) = SourceActions.ProjectArticleActions(
            catalog, resolver, pages, source, replacements, edits, nextNumber, limits, memory, governor, token);
        if (actionEdits is not null)
        {
            if (edits is null) { return (catalog, actionEdits); }
            foreach ((int number, QpdfObject value) in actionEdits)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                Store(edits, number, value, limits, memory, governor);
            }
        }
        return (catalog, edits);

        void Remember(Dictionary<PdfReference, PdfReference> map, PdfReference old, PdfReference replacement)
        {
            if (map.ContainsKey(old)) { return; }
            ParserContainers.EnsureEntryCapacity(scratch, map, IntPtr.Size * 2L);
            map.Add(old, replacement);
        }
    }

    private static PdfDictionary BuildBead(Occurrence occurrence, PdfReference next, PdfReference previous,
        PdfReference? thread, double[]? frame, IIndirectResolver resolver, OwnedAllocationBatch graph)
    {
        PdfArray? transformed = null;
        if (frame is not null && occurrence.Bead.Header.TryGet(Rectangle, out QpdfObject rectangle))
        {
            Span<double> rect = stackalloc double[4], moved = stackalloc double[4];
            if (!PdfGeometry.TryReadFiniteNumbers(CatalogCopy.ResolveValue(rectangle, resolver, graph.Governor, graph.Token), resolver, rect))
            { throw new QpdfParseException("An article bead rectangle changed during projection.", null, occurrence.Reference.ObjectNumber); }
            PdfGeometry.TransformRect(rect, frame, moved);
            transformed = FlattenRotation.NumberArray(moved, graph);
        }
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        using PdfDictionary.EntryEnumerator source = occurrence.Bead.Header.GetEntryEnumerator();
        while (source.MoveNext(graph.Governor, graph.Token))
        {
            (PdfName key, QpdfObject value) = source.Current;
            if (key.Equals(Next) || key.Equals(Previous) || key.Equals(Page) || key.Equals(Thread)) { continue; }
            if (transformed is not null && key.Equals(Rectangle)) { value = transformed; }
            entries.Assign(graph.Memory, graph.Governor, key, value, graph.Token);
        }
        entries.Assign(graph.Memory, graph.Governor, Next, next, graph.Token);
        entries.Assign(graph.Memory, graph.Governor, Previous, previous, graph.Token);
        entries.Assign(graph.Memory, graph.Governor, Page, occurrence.Page, graph.Token);
        if (thread is not null) { entries.Assign(graph.Memory, graph.Governor, Thread, thread, graph.Token); }
        return PdfDictionaryRebuild.Complete(graph, entries, allocation);
    }

    private static PdfReference Allocate(ref int nextNumber, OwnedAllocationBatch graph)
    {
        graph.Memory.Reserve(32L); graph.Add(32L);
        nextNumber = checked(nextNumber + 1);
        return new(nextNumber, 0);
    }

    private static PdfArray CopyArray(List<QpdfObject> refs, OwnedAllocationBatch graph)
    {
        long bytes = checked(64L + IntPtr.Size * (long)refs.Count);
        graph.Memory.Reserve(bytes); graph.Add(bytes);
        return new(refs);
    }

    private static void Store(Dictionary<int, QpdfObject> edits, int number, QpdfObject value,
        QpdfLimits limits, ParserMemoryScope memory, ResourceGovernor governor)
    {
        if (!edits.ContainsKey(number))
        {
            if (edits.Count >= limits.MaxContainerSize)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), edits.Count + 1L, limits.MaxContainerSize); }
            ParserContainers.EnsureEntryCapacity(memory, edits, IntPtr.Size, (int)Math.Min(int.MaxValue, limits.MaxContainerSize));
        }
        governor.CheckCancellation();
        edits[number] = value;
    }
}
