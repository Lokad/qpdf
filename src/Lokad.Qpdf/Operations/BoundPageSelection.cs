using System.Runtime.InteropServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Operations;

internal static class BoundPageSelection
{
    internal readonly record struct Prepared(PdfDictionary Trailer, PdfVersion Version, List<PageLocation> Pages,
        int FirstId, int NextId);

    private readonly record struct Occurrence(GraphCopier Owner, PageLocation Page, SourceLabels? Labels, int SourcePage);

    internal static async ValueTask<Prepared> SelectAsync(IQpdfHost host, QpdfPath input, ImportedModel model,
        PdfDictionary trailer, List<PageLocation> original, Dictionary<int, PdfReference>? articleAliases, PoolResolver resolver,
        IReadOnlyList<PdfPageSource> sources, PdfPageCollation? collation, bool keepLabels, bool preserveDetached, string? pruneMode,
        QpdfRecoveryMode recovery, QpdfLimits limits, ParserMemoryScope memory,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        if (!PdfDictionaryLookup.TryGet(trailer, "Root"u8, governor, token, out QpdfObject root)
            || CatalogCopy.ResolveDictionary(root, resolver, governor, token) is not PdfDictionary catalog)
        { throw new QpdfParseException("Imported trailer /Root must resolve to a dictionary.", null, null); }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        HashSet<int>? before = preserveDetached ? GraphReachability.Find(resolver, trailer, scratch, governor) : null;
        Dictionary<string, List<int>> groups = PageAssembly.GroupPageSources(sources, input.Value, scratch, governor, token);
        try { PageAssembly.ValidateSourceCredentials(sources, groups, governor); }
        catch (ArgumentException error)
        { throw new QpdfParseException("Repeated bound page sources must use the same credential.", null, null, error); }
        int first = ImportedPublication.NextObjectNumber(model, governor, token);
        int pagesNumber = first;
        GraphCopier primary = GraphCopier.CreateBoundOwned(model.Objects, resolver, governor, limits, checked(first + 1), memory);
        PdfReference newPages = primary.AddIndirectObjectWithId(OutputCatalogPreparation.PagesNode([], memory, governor, token), pagesNumber);
        int last = first;
        CopiedForm? primaryForm = AcroFormCopy.ReadBoundForm(catalog, resolver, model.Objects, ref last, true, memory, governor, token);
        primary.AdvanceNextId(checked(last + 1));
        SourceLabels? primaryLabels = keepLabels ? PageLabelMerge.ReadBoundSource(resolver, catalog, limits, governor, token) : null;
        scratch.Reserve(32L);
        ContentPrunePolicy pruning = new(true, pruneMode);
        bool prunePrimary = groups.ContainsKey(input.Value) && pruning.ShouldPrune(resolver, trailer, limits, governor, token);
        List<GraphCopier> imports = ParserContainers.CreateList<GraphCopier>(scratch);
        Dictionary<GraphCopier, CopiedForm?> forms = ParserContainers.CreateDictionary<GraphCopier, CopiedForm?>(scratch);
        RegisterForm(primary, primaryForm);
        scratch.Reserve(64L + (IntPtr.Size + sizeof(int)) * (long)sources.Count);
        List<Occurrence>[] perGroup = new List<Occurrence>[sources.Count];
        int[] counts = new int[sources.Count];
        QpdfDocument.VersionParts? importedVersion = null;
        int importedExtension = 0;
        long selectedCount = 0;
        foreach ((string path, List<int> indices) in groups)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (string.Equals(path, input.Value, StringComparison.Ordinal))
            {
                foreach (int group in indices) { RecordGroup(group, original, primary, primaryLabels); }
                continue;
            }

            PdfPageSource source = sources[indices[0]];
            await using QpdfDocument document = await QpdfDocument.OpenInspectionCore(host, source.Input, source.Password,
                limits, governor, diagnostics, recovery, token, false).ConfigureAwait(false);
            try
            {
                GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, limits, primary.NextId, memory);
                SourceLabels? labels = keepLabels ? PageLabelMerge.ReadSource(document.Engine, copier, limits, governor, token) : null;
                List<PageLocation> retained = ParserContainers.CreateList<PageLocation>(scratch);
                foreach (int group in indices)
                {
                    RecordGroup(group, document.PageReferences, copier, labels);
                    foreach (Occurrence occurrence in perGroup[group])
                    {
                        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                        copier.RegisterPageRoot(occurrence.Page.Source?.ObjectNumber);
                        ParserContainers.AddItem(scratch, retained, occurrence.Page, 16L);
                    }
                }
                copier.HintPageCapacity(retained.Count);
                using PageInheritance.Batch? inheritance = PageInheritance.Batch.TryCreate(retained.Count > 1, document.Engine,
                    PageAssembly.WriterInherited, limits, governor, token);
                memory.Reserve(32L);
                PdfReference parent = new(pagesNumber, 0);
                foreach (int group in indices)
                {
                    List<Occurrence> list = perGroup[group];
                    for (int i = 0; i < list.Count; i++)
                    {
                        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                        Occurrence occurrence = list[i];
                        PageLocation sourcePage = occurrence.Page;
                        var inherited = PageAssembly.MaterializeAncestors(document.Engine, sourcePage, copier,
                            governor, limits, token, memory, inheritance, diagnostics);
                        (int id, PdfDictionary page) = copier.CopyPageRoot(sourcePage.Page, sourcePage.Source?.ObjectNumber, inherited, false, parent);
                        memory.Reserve(32L);
                        list[i] = occurrence with { Page = new(new PdfReference(id, 0), page) };
                    }
                }
                CopiedForm? form = AcroFormCopy.TryCopySourceForm(document.Engine, copier, retained, true,
                    limits, governor, memory, token, false);
                (form, _, _, _) = SourceActions.BindDocument(document.Engine, copier, form, form, null,
                    false, true, null, limits, governor, memory, token);
                if (retained.Count != 0 && pruning.ShouldPrune(document.Engine, limits, governor, token))
                {
                    using ParserMemoryScope selection = ParserMemoryScope.Create(governor);
                    List<int> pageIds = ParserContainers.CreateList<int>(selection, retained.Count, sizeof(int));
                    foreach (int group in indices)
                    {
                        foreach (Occurrence occurrence in perGroup[group])
                        {
                            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                            pageIds.Add((occurrence.Page.Source ?? throw new InvalidOperationException("Missing copied page.")).ObjectNumber);
                        }
                    }
                    ContentTransforms.ApplyResourcePruning(copier, pageIds, limits, governor, diagnostics, token);
                }
                RegisterForm(copier, form);
                OptionalContent.Capture(document.Engine, copier, null, memory, governor, token);
                CatalogCopy.IncludeSourceVersion(ref importedVersion, ref importedExtension, document, governor, token);
                foreach ((int id, QpdfObject value) in copier.CopiedObjects)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    _ = primary.AddIndirectObjectWithId(value, id);
                }
                // Action binding can replace a copied page header.
                foreach (int group in indices)
                {
                    List<Occurrence> list = perGroup[group];
                    for (int index = 0; index < list.Count; index++)
                    {
                        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                        Occurrence occurrence = list[index];
                        PdfReference reference = occurrence.Page.Source ?? throw new InvalidOperationException("Missing copied page.");
                        list[index] = occurrence with { Page = new(reference, (PdfDictionary)model.Objects[reference.ObjectNumber]) };
                    }
                }
                primary.AdvanceNextId(copier.NextId);
                ParserContainers.AddItem(scratch, imports, copier, IntPtr.Size);
            }
            finally { document.Engine.TransferExclusiveValuesTo(memory); }
        }

        List<(int Source, int Slot)> order = PageAssembly.CollatePageOrder(counts, collation, governor, scratch);
        List<Occurrence> ordered = ParserContainers.CreateList<Occurrence>(scratch, order.Count, 40L);
        List<PageLocation> pageHeaders = ParserContainers.CreateList<PageLocation>(scratch, order.Count, 16L);
        bool changesTopology = order.Count != original.Count;
        foreach ((int group, int slot) in order)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            Occurrence occurrence = perGroup[group][slot];
            pageHeaders.Add(occurrence.Page); ordered.Add(occurrence);
            if (!changesTopology && (!ReferenceEquals(occurrence.Owner, primary) || occurrence.SourcePage != ordered.Count))
            { changesTopology = true; }
        }

        ArticleThreads? articles = PdfDictionaryLookup.TryGet(catalog, "Threads"u8, governor, token, out QpdfObject threadRole)
            ? ArticleThreads.Read(threadRole, resolver, limits, scratch, governor, token) : null;
        last = primary.NextId - 1;
        HashSet<int> selectedPages = ParserContainers.CreateSet<int>(scratch);
        List<PdfReference> kids = BoundPageAssembly.PreparePages(pageHeaders, model.Objects, resolver, pagesNumber,
            0, ref last, selectedPages, ref articleAliases, ref changesTopology, limits, memory, governor, diagnostics, token);
        primary.AdvanceNextId(checked(last + 1));
        foreach (GraphCopier imported in imports) { imported.UseOutputPool(model.Objects, token); }
        List<(GraphCopier Copier, int Id)> occurrences = ParserContainers.CreateList<(GraphCopier, int)>(scratch, kids.Count, 16L);
        List<(SourceLabels Source, int Page)>? labelOrder = keepLabels
            ? ParserContainers.CreateList<(SourceLabels, int)>(scratch, kids.Count, 16L) : null;
        scratch.Reserve(32L);
        SourceLabels unlabeled = new(false, []);
        List<int>? primaryPrune = prunePrimary
            ? ParserContainers.CreateList<int>(scratch) : null;
        for (int index = 0; index < kids.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            Occurrence occurrence = ordered[index];
            occurrences.Add((occurrence.Owner, kids[index].ObjectNumber));
            labelOrder?.Add((occurrence.Labels ?? unlabeled, occurrence.SourcePage));
            if (primaryPrune is not null && ReferenceEquals(occurrence.Owner, primary))
            { ParserContainers.AddItem(scratch, primaryPrune, kids[index].ObjectNumber, sizeof(int)); }
        }
        if (primaryPrune is { Count: > 0 })
        { ContentTransforms.ApplyResourcePruning(primary, primaryPrune, true, limits, governor, diagnostics, token); }
        int next = primary.NextId;
        IReadOnlyList<(GraphCopier Copier, CopiedForm Part, int Occurrence)> duplicates =
            AnnotationCopies.IsolateWidgets(occurrences, forms, governor, memory, token, ref next);
        primary.AdvanceNextId(next);
        List<(GraphCopier Copier, CopiedForm Part, int Occurrence)> participants = ParserContainers.CreateList<(GraphCopier, CopiedForm, int)>(scratch);
        foreach (GraphCopier imported in imports)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (forms[imported] is CopiedForm form)
            { ParserContainers.AddItem(scratch, participants, (imported, form, 0), 24L); }
        }
        foreach (var duplicate in duplicates)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            ParserContainers.AddItem(scratch, participants, duplicate, 24L);
        }
        PdfDictionary? outputForm = AcroFormCopy.BuildSingle(primaryForm, primary, true, occurrences,
            model.Objects, [], participants, limits, governor, diagnostics, memory, token);
        if (outputForm is not null)
        {
            HashSet<int> survivors = AcroFormCopy.CollectAnnotRefs(occurrences, governor, scratch, token);
            outputForm = SourceActions.ProjectOutputForm(outputForm, model.Objects, primary, CollectionsMarshal.AsSpan(kids),
                survivors, null, limits, governor, memory, token);
            primary.AdvanceNextId(ImportedPublication.NextObjectNumber(model, governor, token));
        }

        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfVersion version = model.HeaderVersion;
        PdfDictionary updated = PlacementCatalog.ApplySources(catalog, primary, resolver, imports, importedVersion, importedExtension,
            ref version, memory, graph);
        primary.ReplaceCopiedObject(pagesNumber, OutputCatalogPreparation.PagesNode(kids, memory, governor, token));
        updated = PdfDictionaryRebuild.ReplaceEntry(graph, updated, "Pages"u8, newPages);
        updated = outputForm is null ? PdfDictionaryRebuild.RemoveEntry(graph, updated, "AcroForm"u8)
            : PdfDictionaryRebuild.ReplaceEntry(graph, updated, "AcroForm"u8, primary.AddIndirectObjectWithId(outputForm, primary.NextId));
        if (labelOrder is not null)
        {
            List<(int Index, PdfDictionary Label)> labels = PageLabelMerge.Merge(labelOrder, model.Objects, limits, governor, token, memory);
            updated = labels.Count == 0 ? PdfDictionaryRebuild.RemoveEntry(graph, updated, "PageLabels"u8)
                : PdfDictionaryRebuild.ReplaceEntry(graph, updated, "PageLabels"u8, PageLabelAssembly.NumbersDictionary(labels, governor));
        }
        if (changesTopology)
        {
            updated = CatalogCopy.WithoutActiveStructure(updated, resolver, memory, governor, diagnostics, token);
            if (PdfDictionaryLookup.TryGet(catalog, "ViewerPreferences"u8, governor, token, out QpdfObject preferences))
            {
                PdfDictionary? projected = PrintPageRanges.Project(preferences, resolver, original.Count, ordered, primary,
                    static (item, owner) => ReferenceEquals(item.Owner, owner) ? item.SourcePage : 0,
                    limits, memory, governor, token);
                if (projected is not null)
                { updated = PdfDictionaryRebuild.ReplaceEntry(graph, updated, "ViewerPreferences"u8, projected); }
            }
        }
        (updated, Dictionary<int, QpdfObject>? navigation) = SourceActions.ProjectSplitNavigation(updated, resolver,
            CollectionsMarshal.AsSpan(kids), selectedPages, primary.NextId - 1, limits, memory, governor, token);
        ApplyEdits(navigation);
        if (articles is not null || ArticleThreads.HasRegistrations(model.Objects, CollectionsMarshal.AsSpan(kids), governor, token))
        {
            (updated, Dictionary<int, QpdfObject>? edits) = ArticleThreads.Project(articles, updated, resolver,
                CollectionsMarshal.AsSpan(kids), articleAliases, null, null, primary.NextId - 1, limits, memory, governor, token);
            ApplyEdits(edits);
        }
        trailer = PdfDictionaryRebuild.ReplaceEntry(graph, trailer, "Root"u8, primary.AddIndirectObjectWithId(updated, primary.NextId));
        JsonImport.CheckObjectCount(model, limits);
        if (before is not null)
        { GraphReachability.RetireSuperseded(model.Objects, model.CompressedObjects, before, trailer, resolver, scratch, governor, token); }
        List<PageLocation> pages = ParserContainers.CreateList<PageLocation>(memory, kids.Count, 16L);
        foreach (PdfReference page in kids)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            pages.Add(new(page, (PdfDictionary)model.Objects[page.ObjectNumber]));
        }
        graph.Memory.TransferTo(memory, graph.Bytes);
        return new(trailer, version, pages, first, primary.NextId);

        void RecordGroup(int index, IReadOnlyList<PageLocation> sourcePages, GraphCopier owner, SourceLabels? labels)
        {
            using ParserMemoryScope request = ParserMemoryScope.Create(governor);
            IReadOnlyList<int> requested;
            try { requested = sources[index].Selection.Resolve(sourcePages.Count, limits.MaxContainerSize, governor, request); }
            catch (ArgumentException error)
            { throw new QpdfParseException("Invalid page range for a bound source.", null, null, error); }
            selectedCount = checked(selectedCount + requested.Count);
            if (selectedCount > limits.MaxContainerSize)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), selectedCount, limits.MaxContainerSize); }
            bool included = collation is null || collation.GroupSizes[collation.GroupSizes.Count == 1 ? 0 : index] > 0;
            List<Occurrence> list = ParserContainers.CreateList<Occurrence>(scratch, included ? requested.Count : 0, 40L);
            perGroup[index] = list;
            foreach (int page in requested)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (page > sourcePages.Count)
                { throw new QpdfParseException("Selected page exceeds the bound page-source count.", null, null); }
                if (included) { list.Add(new(owner, sourcePages[page - 1], labels, page)); }
            }
            counts[index] = list.Count;
        }

        void RegisterForm(GraphCopier copier, CopiedForm? form)
        {
            ParserContainers.EnsureEntryCapacity(scratch, forms, IntPtr.Size);
            forms.Add(copier, form);
        }

        void ApplyEdits(Dictionary<int, QpdfObject>? edits)
        {
            if (edits is null) { return; }
            foreach ((int id, QpdfObject value) in edits)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                InspectionGraphPreparation.StoreInspectionObject(primary, id, value);
            }
        }
    }
}
