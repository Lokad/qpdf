using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf;

public static partial class Qpdf
{
    private static readonly PdfName InspectionParent = PdfName.FromAscii("Parent");
    private static readonly PdfName InspectionPages = PdfName.FromAscii("Pages");
    private static readonly PdfName InspectionRoot = PdfName.FromAscii("Root");
    private static readonly PdfName InspectionType = PdfName.FromAscii("Type");
    private static readonly PdfName InspectionKids = PdfName.FromAscii("Kids");
    private static readonly PdfName InspectionCount = PdfName.FromAscii("Count");
    private static readonly PdfName InspectionRotate = PdfName.FromAscii("Rotate");
    private static readonly PdfName[] InspectionInherited =
        [PdfName.FromAscii("MediaBox"), PdfName.FromAscii("CropBox"), PdfName.FromAscii("Resources"), InspectionRotate];

    private readonly record struct InspectionPage(int Number, int Generation, PdfDictionary Page);

    private sealed class InspectionSource(QpdfDocument document, GraphCopier copier)
    {
        internal QpdfDocument Document { get; } = document;
        internal GraphCopier Copier { get; } = copier;
    }

    // The input engine borrows this graph only while its owner is alive. Source
    // cache values remain independent; new dictionaries, arrays and copy pools
    // belong to this scope and are detached before their admission is released.
    private sealed class InspectionGraph : IDisposable
    {
        private QpdfDocument? _document;
        private ParserMemoryScope? _memory;

        internal InspectionGraph(QpdfDocument document, ParserMemoryScope memory)
        {
            _document = document;
            _memory = memory;
        }

        public void Dispose()
        {
            if (_document is null) { return; }
            _document.SetInspectionPages(null);
            _document.Engine.SetInspectionObjects(null);
            _document = null;
            _memory?.Dispose();
            _memory = null;
        }
    }

    private static async ValueTask<InspectionGraph?> PrepareInspectionGraphAsync(
        IQpdfHost host, QpdfDocument document, CheckPlan plan, QpdfLimits limits,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        if (plan.Sources.Count == 0 && plan.Rotations.Count == 0) { return null; }
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        InspectionGraph? lifetime = null;
        Dictionary<string, InspectionSource>? sources = null;
        try
        {
            memory.Reserve(32L);
            lifetime = new(document, memory);
            sources = ParserContainers.CreateDictionary<string, InspectionSource>(memory);
            int greatest = 0;
            foreach (int number in document.Engine.XRefEntries.Keys)
            {
                governor.ChargeWork(1L);
                greatest = Math.Max(greatest, number);
            }
            if (greatest == int.MaxValue) { throw new QpdfParseException("No object identity is available for page transformations.", null, null); }
            GraphCopier pool = GraphCopier.CreateOwned(document.Engine, governor, limits, greatest + 1, memory);
            List<InspectionPage> original = ParserContainers.CreateList<InspectionPage>(memory);
            memory.Reserve(96L);
            Dictionary<QpdfObject, PdfReference> promoted = new(ReferenceEqualityComparer.Instance);
            foreach (PageLocation location in document.PageReferences)
            {
                governor.ChargeWork(1L);
                Dictionary<PdfName, QpdfObject> extra = ParserContainers.CreateDictionary<PdfName, QpdfObject>(memory);
                if (plan.Sources.Count > 0)
                {
                    foreach (PdfName key in InspectionInherited)
                    {
                        if (location.Page.TryGet(key, out QpdfObject own)
                            && PageInheritance.Resolve(document.Engine, own, limits, governor) is not PdfNull) { continue; }
                        QpdfObject? inherited = PageInheritance.Find(document.Engine, location, key, limits, governor, cancellationToken);
                        if (inherited is null) { continue; }
                        QpdfObject kept = inherited;
                        if (inherited is PdfArray or PdfDictionary)
                        {
                            if (!promoted.TryGetValue(inherited, out PdfReference? reference))
                            {
                                ParserContainers.EnsureEntryCapacity(memory, promoted, IntPtr.Size);
                                reference = pool.AddIndirectObjectWithId(inherited, pool.NextId);
                                promoted.Add(inherited, reference);
                            }
                            if (inherited is PdfArray) { kept = reference; }
                        }
                        SetInspectionEntry(extra, key, kept, memory);
                    }
                }
                (int id, PdfDictionary page) = pool.CopyPageRoot(location.Page, location.Source?.ObjectNumber, extra, true);
                ParserContainers.AddItem(memory, original, new InspectionPage(id, location.Source?.Generation ?? 0, page), 16L);
            }

            List<List<InspectionPage>> groups = ParserContainers.CreateList<List<InspectionPage>>(memory);
            if (plan.Sources.Count == 0) { ParserContainers.AddItem(memory, groups, original, IntPtr.Size); }
            foreach (PdfPageSource source in plan.Sources)
            {
                governor.ChargeWork(1L);
                bool primary = source.Input.Value == "." || string.Equals(source.Input.Value, plan.Input.Value, StringComparison.Ordinal);
                QpdfDocument sourceDocument = document;
                GraphCopier sourceCopier = pool;
                if (!primary)
                {
                    if (!sources.TryGetValue(source.Input.Value, out InspectionSource? loaded))
                    {
                        ParserContainers.EnsureEntryCapacity(memory, sources, IntPtr.Size);
                        sourceDocument = await QpdfDocument.OpenCore(host, source.Input, limits, governor,
                            diagnostics, plan.Recovery, cancellationToken).ConfigureAwait(false);
                        try
                        {
                            sourceCopier = GraphCopier.CreateOwned(sourceDocument.Engine, governor, limits, pool.NextId, memory);
                            memory.Reserve(32L);
                            loaded = new InspectionSource(sourceDocument, sourceCopier);
                            sources.Add(source.Input.Value, loaded);
                        }
                        catch { await sourceDocument.DisposeAsync().ConfigureAwait(false); throw; }
                    }
                    sourceDocument = loaded.Document;
                    sourceCopier = loaded.Copier;
                }
                IReadOnlyList<int> requested = Pages.PageRanges.Expand(source.Selection.Range, sourceDocument.PageCount,
                    limits.MaxWork, governor, memory);
                List<InspectionPage> group = ParserContainers.CreateList<InspectionPage>(memory);
                foreach (int pageNumber in requested)
                {
                    governor.ChargeWork(1L);
                    InspectionPage selected;
                    if (primary) { selected = original[pageNumber - 1]; }
                    else
                    {
                        PageLocation location = sourceDocument.PageReferences[pageNumber - 1];
                        sourceCopier.AdvanceNextId(pool.NextId);
                        Dictionary<PdfName, QpdfObject> extra = MaterializeAncestors(sourceDocument.Engine, location,
                            sourceCopier, governor, limits, cancellationToken, memory);
                        (int id, PdfDictionary page) = sourceCopier.CopyPageRoot(location.Page, location.Source?.ObjectNumber, extra);
                        selected = new InspectionPage(id, 0, page);
                        foreach (KeyValuePair<int, QpdfObject> entry in sourceCopier.CopiedObjects)
                        {
                            governor.ChargeWork(1L);
                            StoreInspectionObject(pool, entry.Key, entry.Value);
                        }
                    }
                    ParserContainers.AddItem(memory, group, selected, 16L);
                }
                ParserContainers.AddItem(memory, groups, group, IntPtr.Size);
            }

            List<int> counts = ParserContainers.CreateList<int>(memory);
            foreach (List<InspectionPage> group in groups) { ParserContainers.AddItem(memory, counts, group.Count, sizeof(int)); }
            List<(int Source, int Slot)> order = CollatePageOrder(counts, plan.Collation, governor, memory);
            List<InspectionPage> selectedPages = ParserContainers.CreateList<InspectionPage>(memory);
            HashSet<int> used = ParserContainers.CreateSet<int>(memory);
            foreach ((int source, int slot) in order)
            {
                governor.ChargeWork(1L);
                InspectionPage page = groups[source][slot];
                if (!ParserContainers.AddSetItem(memory, used, page.Number, sizeof(int)))
                {
                    Dictionary<PdfName, QpdfObject> extra = ParserContainers.CreateDictionary<PdfName, QpdfObject>(memory);
                    (int id, PdfDictionary duplicate) = pool.CopyPageRoot(page.Page, null, extra, true);
                    page = new InspectionPage(id, 0, duplicate);
                }
                ParserContainers.AddItem(memory, selectedPages, page, 16L);
            }

            document.Engine.SetInspectionObjects(pool.CopiedObjects);
            if (plan.Sources.Count > 0)
            {
                QpdfObject catalogRaw = document.Engine.Trailer.TryGet(InspectionRoot, out QpdfObject root) ? root
                    : throw new QpdfParseException("Trailer has no catalog root.", null, null);
                PdfDictionary catalog = PageInheritance.Resolve(document.Engine, catalogRaw, limits, governor) as PdfDictionary
                    ?? throw new QpdfParseException("Catalog root is not a dictionary.", null, null);
                QpdfObject pagesRaw = catalog.TryGet(InspectionPages, out QpdfObject pages) ? pages : catalogRaw;
                PdfDictionary pagesDictionary = PageInheritance.Resolve(document.Engine, pagesRaw, limits, governor) as PdfDictionary
                    ?? throw new QpdfParseException("Page root is not a dictionary.", null, null);
                if (pagesRaw is not PdfReference pagesReference) { throw new QpdfParseException("Page root has no indirect identity.", null, null); }
                List<QpdfObject> kids = ParserContainers.CreateList(memory);
                List<InspectionPage> parented = ParserContainers.CreateList<InspectionPage>(memory);
                foreach (InspectionPage selected in selectedPages)
                {
                    Dictionary<PdfName, QpdfObject> entries = CopyInspectionEntries(selected.Page, memory, governor);
                    SetInspectionEntry(entries, InspectionParent, pagesReference, memory);
                    memory.Reserve(56L);
                    PdfDictionary page = PdfDictionary.FromOwnedEntries(entries);
                    StoreInspectionObject(pool, selected.Number, page);
                    ParserContainers.AddItem(memory, parented, selected with { Page = page }, 16L);
                    ParserContainers.AddItem(memory, kids, new PdfReference(selected.Number, selected.Generation));
                }
                selectedPages = parented;
                Dictionary<PdfName, QpdfObject> rootEntries = CopyInspectionEntries(pagesDictionary, memory, governor);
                foreach (PdfName key in InspectionInherited) { rootEntries.Remove(key); }
                SetInspectionEntry(rootEntries, InspectionType, InspectionPages, memory);
                SetInspectionEntry(rootEntries, InspectionKids, ParserContainers.CopyArray(memory, kids), memory);
                memory.Reserve(112L);
                SetInspectionEntry(rootEntries, InspectionCount, new PdfInteger(selectedPages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), selectedPages.Count), memory);
                StoreInspectionObject(pool, pagesReference.ObjectNumber, PdfDictionary.FromOwnedEntries(rootEntries));
                foreach (InspectionPage originalPage in original)
                {
                    governor.ChargeWork(1L);
                    if (!used.Contains(originalPage.Number)) { StoreInspectionObject(pool, originalPage.Number, PdfNull.Instance); }
                }
            }

            List<(GraphCopier Copier, int Id, PdfDictionary Page)> occurrences = ParserContainers.CreateList<(GraphCopier, int, PdfDictionary)>(memory);
            foreach (InspectionPage selected in selectedPages)
            {
                memory.Reserve(24L);
                PdfDictionary page = selected.Page;
                QpdfObject? inherited = PageInheritance.Find(document.Engine, new PageLocation(new PdfReference(selected.Number, selected.Generation), page),
                    InspectionRotate, limits, governor, cancellationToken);
                if (inherited is not null)
                {
                    Dictionary<PdfName, QpdfObject> entries = CopyInspectionEntries(page, memory, governor);
                    SetInspectionEntry(entries, InspectionRotate, PageInheritance.Resolve(document.Engine, inherited, limits, governor), memory);
                    memory.Reserve(32L);
                    page = PdfDictionary.FromOwnedEntries(entries);
                }
                ParserContainers.AddItem(memory, occurrences, (pool, selected.Number, page), 24L);
            }
            ApplyRotations(occurrences, ResolveRotationFolds(plan.Rotations, selectedPages.Count, governor, limits, memory), governor, memory);
            List<PageLocation> transformed = ParserContainers.CreateList<PageLocation>(memory);
            foreach (InspectionPage selected in selectedPages)
            {
                governor.ChargeWork(1L);
                memory.Reserve(24L);
                PdfDictionary page = pool.CopiedObjects[selected.Number] as PdfDictionary
                    ?? throw new QpdfParseException("Selected page was removed from the graph.", null, selected.Number);
                ParserContainers.AddItem(memory, transformed, new PageLocation(new PdfReference(selected.Number, selected.Generation), page), 16L);
            }
            document.SetInspectionPages(transformed);
            return lifetime;
        }
        catch (ArgumentException ex)
        {
            if (lifetime is null) { memory.Dispose(); }
            else { lifetime.Dispose(); }
            throw new QpdfParseException("Invalid page selection or rotation range.", 0L, null, ex);
        }
        catch
        {
            if (lifetime is null) { memory.Dispose(); }
            else { lifetime.Dispose(); }
            throw;
        }
        finally
        {
            Exception? disposalFailure = null;
            foreach (InspectionSource source in sources?.Values ?? Enumerable.Empty<InspectionSource>())
            {
                try { await source.Document.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { disposalFailure ??= ex; }
            }
            if (disposalFailure is not null)
            {
                lifetime?.Dispose();
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposalFailure).Throw();
            }
        }
    }

    private static Dictionary<PdfName, QpdfObject> CopyInspectionEntries(PdfDictionary dictionary,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        Dictionary<PdfName, QpdfObject> entries = ParserContainers.CreateDictionary<PdfName, QpdfObject>(memory);
        using var enumerator = dictionary.GetEntryEnumerator();
        while (enumerator.MoveNext())
        {
            governor.ChargeWork(1L);
            SetInspectionEntry(entries, enumerator.Current.Key, enumerator.Current.Value, memory);
        }
        return entries;
    }

    private static void SetInspectionEntry(Dictionary<PdfName, QpdfObject> entries, PdfName key, QpdfObject value, ParserMemoryScope memory)
    {
        if (!entries.ContainsKey(key)) { ParserContainers.EnsureEntryCapacity(memory, entries, IntPtr.Size); }
        entries[key] = value;
    }

    private static void StoreInspectionObject(GraphCopier pool, int id, QpdfObject value)
    {
        if (pool.CopiedObjects.ContainsKey(id)) { pool.ReplaceCopiedObject(id, value); }
        else { _ = pool.AddIndirectObjectWithId(value, id); }
    }

    private static List<(int Source, int Slot)> CollatePageOrder(IReadOnlyList<int> counts,
        PdfPageCollation? collation, ResourceGovernor? governor, ParserMemoryScope? memory)
    {
        if (collation is not null && collation.GroupSizes.Count != 1 && collation.GroupSizes.Count != counts.Count)
        {
            throw new ArgumentException("Collation group sizes must hold one value or one per source.", nameof(collation));
        }
        List<(int Source, int Slot)> order = memory is null ? new() : ParserContainers.CreateList<(int, int)>(memory);
        memory?.Reserve(checked(32L + sizeof(int) * (long)counts.Count));
        int[] cursors = new int[counts.Count];
        bool more = true;
        while (more)
        {
            more = false;
            for (int source = 0; source < counts.Count; source++)
            {
                governor?.ChargeWork(1L);
                int size = collation is null ? counts[source]
                    : collation.GroupSizes.Count == 1 ? collation.GroupSizes[0] : collation.GroupSizes[source];
                for (int i = 0; i < size && cursors[source] < counts[source]; i++)
                {
                    governor?.ChargeWork(1L);
                    if (memory is null) { order.Add((source, cursors[source]++)); }
                    else { ParserContainers.AddItem(memory, order, (source, cursors[source]++), 8L); }
                    more = true;
                }
            }
        }
        return order;
    }
}
