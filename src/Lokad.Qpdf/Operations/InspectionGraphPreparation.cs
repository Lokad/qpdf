using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;
using static Lokad.Qpdf.Operations.PageAssembly;
using static Lokad.Qpdf.Writing.OutputCatalogPreparation;
using static Lokad.Qpdf.Writing.OutputPublication;
using static Lokad.Qpdf.Writing.OutputSecurityPreparation;

namespace Lokad.Qpdf.Operations;

internal static partial class InspectionGraphPreparation
{
    internal static readonly PdfName InspectionParent = PdfName.FromAscii("Parent");
    internal static readonly PdfName InspectionPages = PdfName.FromAscii("Pages");
    internal static readonly PdfName InspectionRoot = PdfName.FromAscii("Root");
    internal static readonly PdfName InspectionType = PdfName.FromAscii("Type");
    internal static readonly PdfName InspectionKids = PdfName.FromAscii("Kids");
    internal static readonly PdfName InspectionCount = PdfName.FromAscii("Count");
    internal static readonly PdfName InspectionRotate = PdfName.FromAscii("Rotate");
    internal static readonly PdfName[] InspectionInherited =
        [PdfName.FromAscii("MediaBox"), PdfName.FromAscii("CropBox"), PdfName.FromAscii("Resources"), InspectionRotate];

    internal readonly record struct InspectionPage(int Number, int Generation, PdfDictionary Page);

    private sealed class InspectionCatalog
    {
        internal readonly Dictionary<int, (Content.SourceLabels? Labels, int Page, GraphCopier Copier)> Pages;
        internal readonly Dictionary<GraphCopier, Content.CopiedForm?> Forms;
        internal readonly List<GraphCopier> Sources;
        internal QpdfDocument.VersionParts? Version;
        internal int Extension;

        internal InspectionCatalog(ParserMemoryScope memory)
        {
            memory.Reserve(56L);
            Pages = ParserContainers.CreateDictionary<int, (Content.SourceLabels?, int, GraphCopier)>(memory);
            Forms = ParserContainers.CreateDictionary<GraphCopier, Content.CopiedForm?>(memory);
            Sources = ParserContainers.CreateList<GraphCopier>(memory);
        }
    }

    // The input engine borrows this graph only while its owner is alive. Source
    // cache values remain independent; new dictionaries, arrays and copy pools
    // belong to this scope and are detached before their admission is released.
    internal sealed class InspectionGraph : IDisposable
    {
        private QpdfDocument? _document;
        private ParserMemoryScope? _memory;
        internal PdfReference? ReplacedCatalog { get; set; }

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
            ReplacedCatalog = null;
            _document = null;
            _memory?.Dispose();
            _memory = null;
        }
    }

    internal static async ValueTask<InspectionGraph?> PrepareInspectionGraphAsync(
        IQpdfHost host, QpdfDocument document, IInspectionGraphPlan plan, QpdfLimits limits,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        bool overlay = plan.Overlays.Count > 0 || plan.Underlays.Count > 0;
        bool externalize = plan.ExternalizeInlineImages || plan.OptimizeImages && !plan.KeepInlineImages;
        bool content = plan.CoalesceContents || externalize || plan.OptimizeImages;
        bool authoredLabels = plan.Labels.Count > 0;
        bool publication = authoredLabels || plan.PublicationPolicy.HasRemovals;
        bool editAttachments = plan.HasAttachmentEdits;
        bool preparePages = plan.Sources.Count > 0 || plan.Rotations.Count > 0 || overlay || content || authoredLabels
            || plan.FlattenAnnotations is not null;
        if (!preparePages && !publication && !editAttachments) { return null; }
        if (plan.Sources.Count > 0 && plan.Collation is PdfPageCollation collation
            && collation.GroupSizes.Count != 1 && collation.GroupSizes.Count != plan.Sources.Count)
        { throw new ArgumentException("Collation requires one size or one size per source."); }
        bool pruning = plan.Sources.Count > 0 && plan.RemoveUnreferencedResources != "no";
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        InspectionGraph? lifetime = null;
        try
        {
            memory.Reserve(40L);
            lifetime = new(document, memory);
            using ParserMemoryScope? pruningMemory = pruning ? ParserMemoryScope.Create(governor) : null;
            Content.ContentPrunePolicy? prunePolicy = null;
            bool[]? pruneGroups = null;
            bool prunePrimary = false;
            if (pruningMemory is not null)
            {
                pruningMemory.Reserve(64L + plan.Sources.Count);
                prunePolicy = new(true, plan.RemoveUnreferencedResources);
                pruneGroups = new bool[plan.Sources.Count];
                // Inspect source sharing before inherited-resource overlays can change it.
                for (int index = 0; index < plan.Sources.Count; index++)
                {
                    governor.NoteOperation();
                    string path = plan.Sources[index].Input.Value;
                    if ((path == "." || string.Equals(path, plan.Input.Value, StringComparison.Ordinal))
                        && InspectionGroupIncluded(plan, index))
                    { prunePrimary = prunePolicy.ShouldPrune(document.Engine, limits, governor, cancellationToken); break; }
                }
            }
            bool promoteEditedPages = (plan.Rotations.Count > 0 || plan.FlattenAnnotations is not null) && plan.Sources.Count == 0;
            using ParserMemoryScope? preparation = content || authoredLabels || promoteEditedPages
                ? ParserMemoryScope.Create(governor) : null;
            PageTreePreparation.Prepared? prepared = preparation is null ? null
                : PageTreePreparation.PrepareForInspection(document.Engine, plan.Sources.Count > 0 || overlay,
                    limits, memory, preparation, governor, cancellationToken);
            if (prepared is not null)
            {
                document.Engine.SetInspectionObjects(prepared.Objects);
                document.Engine.SetInspectionTrailer(prepared.Trailer);
                document.SetInspectionPages(prepared.Pages ?? throw new InvalidOperationException("Missing prepared inspection pages."));
            }
            int greatest = 0;
            foreach (int number in document.Engine.XRefEntries.Keys)
            {
                governor.NoteOperation();
                greatest = Math.Max(greatest, number);
            }
            if (prepared is not null)
            {
                foreach (int number in prepared.Objects.Keys)
                { governor.NoteOperation(); greatest = Math.Max(greatest, number); }
            }
            if (greatest == int.MaxValue) { throw new QpdfParseException("No object identity is available for page transformations.", null, null); }
            GraphCopier pool = GraphCopier.CreateInspectionOwned(document.Engine, governor, limits, greatest + 1, memory, true);
            if (!preparePages)
            {
                document.Engine.SetInspectionObjects(pool.CopiedObjects);
                document.Engine.InspectionCalledAllPages = false;
                document.Engine.InspectionPushedInheritedResources = false;
                if (publication)
                { ApplyInspectionPublication(document, pool, plan, lifetime, memory, governor, cancellationToken); }
                if (editAttachments)
                { await ApplyInspectionAttachmentsAsync(host, document, pool, plan, limits, governor, diagnostics, memory, lifetime, cancellationToken).ConfigureAwait(false); }
                return lifetime;
            }
            if (prepared is not null)
            {
                foreach (KeyValuePair<int, QpdfObject> entry in prepared.Objects)
                { governor.NoteOperation(); StoreInspectionObject(pool, entry.Key, entry.Value); }
            }
            List<InspectionPage> original = ParserContainers.CreateList<InspectionPage>(memory);
            memory.Reserve(96L);
            Dictionary<QpdfObject, PdfReference> promoted = new(ReferenceEqualityComparer.Instance);
            bool materialize = plan.Sources.Count > 0 || overlay;
            using (PageInheritance.Batch? ancestry = PageInheritance.Batch.TryCreate(materialize && document.PageReferences.Count > 1,
                document.Engine, InspectionInherited, limits, governor, cancellationToken))
            {
                foreach (PageLocation location in document.PageReferences)
                {
                    governor.NoteOperation();
                    Dictionary<PdfName, QpdfObject> extra = ParserContainers.CreateDictionary<PdfName, QpdfObject>(memory);
                    if (materialize)
                    {
                        PageInheritance.InheritedValues inheritedValues = ancestry?.Find(location) ?? PageInheritance.FindAncestors(document.Engine, location,
                            InspectionInherited, limits, governor, cancellationToken);
                        for (int index = 0; index < InspectionInherited.Length; index++)
                        {
                            PdfName key = InspectionInherited[index];
                            QpdfObject? inherited = inheritedValues[index];
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
                    else if (externalize || plan.OptimizeImages)
                    {
                        PdfName key = PdfName.FromAscii("Resources");
                        QpdfObject? resources = PageInheritance.Find(document.Engine, location, key, limits, governor, cancellationToken);
                        if (resources is not null) { SetInspectionEntry(extra, key, resources, memory); }
                    }
                    (int id, PdfDictionary page) = pool.CopyPageRoot(location.Page, location.Source?.ObjectNumber, extra, true, null);
                    ParserContainers.AddItem(memory, original, new InspectionPage(id, location.Source?.Generation ?? 0, page), 16L);
                }
            }

            if (overlay)
            {
                foreach (InspectionPage page in original)
                {
                    if (page.Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject annotations))
                    {
                        pool.RetainSourceValue(annotations);
                    }
                }
            }

            InspectionCatalog? catalogAssembly = plan.Sources.Count > 0 ? new(memory) : null;
            bool preserveLabels = catalogAssembly is not null && !authoredLabels && !plan.PublicationPolicy.RemoveLabels;
            if (catalogAssembly is not null)
            { Content.CatalogCopy.IncludeSourceVersion(ref catalogAssembly.Version, ref catalogAssembly.Extension, document, governor, cancellationToken); }
            List<List<InspectionPage>> groups = await PrepareInspectionSourcesAsync(host, document, original,
                pool, plan, catalogAssembly, preserveLabels, prunePolicy, prunePrimary, pruneGroups,
                limits, governor, diagnostics, memory, cancellationToken).ConfigureAwait(false);
            if (catalogAssembly is not null)
            {
                using ParserMemoryScope keptMemory = ParserMemoryScope.Create(governor);
                List<PageLocation> kept = ParserContainers.CreateList<PageLocation>(keptMemory);
                foreach (var source in catalogAssembly.Pages.Values)
                {
                    governor.NoteOperation();
                    if (ReferenceEquals(source.Copier, pool))
                    { ParserContainers.AddItem(keptMemory, kept, document.PageReferences[source.Page - 1], 16L); }
                }
                Content.CopiedForm? primaryForm = Content.AcroFormCopy.TryCopySourceForm(document.Engine, pool, kept,
                    true, limits, governor, memory, cancellationToken, true);
                ParserContainers.EnsureEntryCapacity(memory, catalogAssembly.Forms, IntPtr.Size);
                catalogAssembly.Forms.Add(pool, primaryForm);
            }

            List<int> counts = ParserContainers.CreateList<int>(memory);
            foreach (List<InspectionPage> group in groups) { ParserContainers.AddItem(memory, counts, group.Count, sizeof(int)); }
            List<(int Source, int Slot)> order = CollatePageOrder(counts, plan.Collation, governor, memory);
            List<InspectionPage> selectedPages = ParserContainers.CreateList<InspectionPage>(memory);
            List<int>? pruneIds = null;
            HashSet<int> used = ParserContainers.CreateSet<int>(memory);
            List<(Content.SourceLabels Source, int Page)>? labelOccurrences = preserveLabels
                ? ParserContainers.CreateList<(Content.SourceLabels, int)>(memory) : null;
            List<(GraphCopier Copier, int Id)>? occurrences = catalogAssembly is null ? null
                : ParserContainers.CreateList<(GraphCopier, int)>(memory);
            Dictionary<int, PdfReference>? articleAliases = null;
            bool hasBeads = false;
            bool anyLabels = false;
            foreach ((int source, int slot) in order)
            {
                governor.NoteOperation();
                InspectionPage page = groups[source][slot];
                GraphCopier pageOwner = pool;
                if (catalogAssembly is not null)
                {
                    var sourcePage = catalogAssembly.Pages[page.Number];
                    pageOwner = sourcePage.Copier;
                    if (labelOccurrences is not null)
                    {
                        Content.SourceLabels labels = sourcePage.Labels
                            ?? throw new InvalidOperationException("Missing labels for a selected page source.");
                        anyLabels |= labels.HasTree;
                        ParserContainers.AddItem(memory, labelOccurrences, (labels, sourcePage.Page), 16L);
                    }
                }
                if (!ParserContainers.AddSetItem(memory, used, page.Number, sizeof(int)))
                {
                    Dictionary<PdfName, QpdfObject> extra = ParserContainers.CreateDictionary<PdfName, QpdfObject>(memory);
                    PdfDictionary header = (PdfDictionary)pool.CopiedObjects[page.Number];
                    (int id, PdfDictionary duplicate) = pool.CopyPageRoot(header, null, extra, true, null);
                    if (catalogAssembly is not null)
                    { Content.ArticleThreads.RememberDuplicate(page.Page, id, page.Number, page.Generation, ref articleAliases, memory); }
                    page = new InspectionPage(id, 0, duplicate);
                    if (!ReferenceEquals(pageOwner, pool)) { StoreInspectionObject(pageOwner, id, duplicate); }
                }
                if (catalogAssembly is not null) { hasBeads |= Content.ArticleThreads.HasPageBeads(page.Page); }
                ParserContainers.AddItem(memory, selectedPages, page, 16L);
                if (pruneGroups?[source] == true && pruningMemory is not null)
                {
                    pruneIds ??= ParserContainers.CreateList<int>(pruningMemory);
                    ParserContainers.AddItem(pruningMemory, pruneIds, page.Number, sizeof(int));
                }
                if (occurrences is not null) { ParserContainers.AddItem(memory, occurrences, (pageOwner, page.Number), 16L); }
            }

            IReadOnlyList<(GraphCopier Copier, Content.CopiedForm Part, int Occurrence)> duplicatedFields =
                Array.Empty<(GraphCopier, Content.CopiedForm, int)>();
            if (catalogAssembly is not null && occurrences is not null)
            {
                foreach ((GraphCopier owner, int id) in occurrences)
                {
                    governor.NoteOperation();
                    if (ReferenceEquals(owner, pool) && pool.CopiedObjects[id] is PdfDictionary page
                        && page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject annotations))
                    { pool.RetainSourceValue(annotations); }
                }
                int allocator = pool.NextId;
                Dictionary<int, int>? generations = null;
                foreach (InspectionPage page in selectedPages)
                {
                    governor.NoteOperation();
                    if (page.Generation == 0) { continue; }
                    generations ??= ParserContainers.CreateDictionary<int, int>(memory);
                    ParserContainers.EnsureEntryCapacity(memory, generations, sizeof(int));
                    generations.Add(page.Number, page.Generation);
                }
                duplicatedFields = Content.AnnotationCopies.IsolateWidgets(occurrences, catalogAssembly.Forms,
                    generations, governor, memory, cancellationToken, ref allocator);
                pool.AdvanceNextId(allocator);
                foreach (GraphCopier copier in catalogAssembly.Sources)
                {
                    foreach (KeyValuePair<int, QpdfObject> entry in copier.CopiedObjects)
                    { governor.NoteOperation(); StoreInspectionObject(pool, entry.Key, entry.Value); }
                }
            }
            document.Engine.SetInspectionObjects(pool.CopiedObjects);
            if (prepared is not null)
            {
                document.Engine.SetInspectionTrailer(prepared.Trailer);
                document.Engine.InspectionPushedInheritedResources = plan.Sources.Count > 0 || overlay;
            }
            if (labelOccurrences is not null)
            {
                PdfReference? labels = null;
                if (anyLabels)
                {
                    List<(int Index, PdfDictionary Label)> items = Content.PageLabelMerge.Merge(labelOccurrences,
                        pool.CopiedObjects, limits, governor, cancellationToken, memory);
                    if (items.Count > 0)
                    { labels = pool.AddIndirectObjectWithId(PageLabelAssembly.NumbersDictionary(items, governor), pool.NextId); }
                }
                ReplaceInspectionCatalogEntry(document, pool, PdfName.FromAscii("PageLabels"), labels, memory, limits, governor);
            }
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
                for (int index = 0; index < selectedPages.Count; index++)
                {
                    InspectionPage selected = selectedPages[index];
                    PdfDictionary current = pool.CopiedObjects[selected.Number] as PdfDictionary
                        ?? throw new QpdfParseException("Selected page is not a dictionary.", null, selected.Number);
                    Dictionary<PdfName, QpdfObject> entries = CopyInspectionEntries(current, memory, governor);
                    SetInspectionEntry(entries, InspectionParent, pagesReference, memory);
                    memory.Reserve(56L);
                    PdfDictionary page = PdfDictionary.FromOwnedEntries(entries);
                    StoreInspectionObject(pool, selected.Number, page);
                    if (occurrences is not null && !ReferenceEquals(occurrences[index].Copier, pool))
                    { StoreInspectionObject(occurrences[index].Copier, selected.Number, page); }
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
                    governor.NoteOperation();
                    if (!used.Contains(originalPage.Number)) { StoreInspectionObject(pool, originalPage.Number, PdfNull.Instance); }
                }
            }

            if (catalogAssembly is not null && occurrences is not null)
            {
                AssembleInspectionCatalog(document, pool, catalogAssembly, occurrences, duplicatedFields,
                    memory, limits, governor, diagnostics, cancellationToken);
                ProjectInspectionArticles(document, pool, selectedPages, hasBeads, articleAliases, lifetime,
                    limits, memory, governor, cancellationToken);
                if (document.Engine.Trailer.TryGet(InspectionRoot, out QpdfObject root)
                    && Content.CatalogCopy.ResolveDictionary(root, document.Engine, governor, cancellationToken) is PdfDictionary catalog
                    && catalog.TryGet(Content.PrintPageRanges.ViewerPreferences, out QpdfObject rawPreferences))
                {
                    PdfDictionary? preferences = Content.PrintPageRanges.Project(rawPreferences, document.Engine, original.Count,
                        order, (groups, catalogAssembly, pool), static (entry, state) =>
                        {
                            var page = state.catalogAssembly.Pages[state.groups[entry.Source][entry.Slot].Number];
                            return ReferenceEquals(page.Copier, state.pool) ? page.Page : 0;
                        }, limits, memory, governor, cancellationToken);
                    if (preferences is not null)
                    {
                        ReplaceInspectionCatalogEntry(document, pool, Content.PrintPageRanges.ViewerPreferences,
                            preferences, memory, limits, governor);
                    }
                }
            }

            if (content || pruneIds is not null)
            {
                using ParserMemoryScope contentMemory = ParserMemoryScope.Create(governor);
                IReadOnlyList<int> pageIds = Array.Empty<int>();
                if (content)
                {
                    List<int> contentPages = ParserContainers.CreateList<int>(contentMemory, selectedPages.Count, sizeof(int));
                    foreach (InspectionPage page in selectedPages)
                    { governor.NoteOperation(); ParserContainers.AddItem(contentMemory, contentPages, page.Number, sizeof(int)); }
                    pageIds = contentPages;
                }
                if (plan.CoalesceContents || externalize)
                {
                    memory.Reserve(48L);
                    Content.ContentTransformRequest request = new(plan.CoalesceContents, false, false, null, externalize, plan.IiMinBytes);
                    Content.ContentTransforms.ApplyPageTransforms(pool, pageIds, request, limits, governor, diagnostics, cancellationToken);
                    if (externalize)
                    { Content.ContentTransforms.ExternalizeForms(pool, pageIds, request, null, limits, governor, diagnostics, cancellationToken); }
                }
                if (pruneIds is not null)
                { Content.ContentTransforms.ApplyResourcePruning(pool, pruneIds, limits, governor, diagnostics, cancellationToken); }
                if (plan.OptimizeImages)
                {
                    memory.Reserve(64L);
                    Content.ImageOptimizeRequest request = new(true, plan.OiMinWidth, plan.OiMinHeight, plan.OiMinArea, plan.KeepInlineImages, plan.JpegQuality);
                    Content.ImageOptimizer.Apply(pool, pageIds, request, limits, governor, diagnostics, cancellationToken);
                }
            }
            List<List<(PdfRotationMode Mode, int Angle)>?> folds = ResolveRotationFolds(plan.Rotations, selectedPages.Count, governor, limits, memory);
            for (int i = 0; i < selectedPages.Count; i++)
            {
                governor.CheckCancellation();
                if (folds[i] is not { Count: > 0 } specifications) { continue; }
                InspectionPage selected = selectedPages[i];
                PdfDictionary page = pool.CopiedObjects[selected.Number] as PdfDictionary
                    ?? throw new QpdfParseException("Selected page was removed from the graph.", null, selected.Number);
                QpdfObject? inherited = ReadInheritedRotation(document.Engine, new(new PdfReference(selected.Number, selected.Generation), page),
                    limits, governor, cancellationToken);
                ApplyRotation(pool, selected.Number, page, inherited, specifications, i + 1, governor, memory);
            }
            if (overlay)
            {
                List<(GraphCopier Copier, int Id)> overlayPages = ParserContainers.CreateList<(GraphCopier, int)>(memory);
                foreach (InspectionPage selected in selectedPages)
                {
                    ParserContainers.AddItem(memory, overlayPages, (pool, selected.Number), 16L);
                }
                Content.OverlayApplication applied = await Content.Overlays.ApplyAsync(host, plan.Input,
                    overlayPages, plan.Underlays, plan.Overlays, false, null, plan.Recovery,
                    limits, governor, diagnostics, memory, cancellationToken, pool.NextId, document.Engine, null, false, false).ConfigureAwait(false);
                foreach (GraphCopier source in applied.Copiers)
                {
                    foreach (KeyValuePair<int, QpdfObject> entry in source.CopiedObjects)
                    {
                        governor.NoteOperation();
                        StoreInspectionObject(pool, entry.Key, entry.Value);
                    }
                }
                if (applied.FieldImports.Count > 0)
                {
                    UpdateInspectionForm(document, pool, overlayPages, applied.FieldImports,
                        plan.Sources.Count > 0, memory, limits, governor, diagnostics, cancellationToken);
                }
            }
            if (plan.FlattenAnnotations is string flatten)
            {
                FlattenInspectionAnnotations(document, pool, selectedPages, flatten, lifetime, memory,
                    limits, governor, cancellationToken, diagnostics);
            }
            List<PageLocation> transformed = ParserContainers.CreateList<PageLocation>(memory);
            foreach (InspectionPage selected in selectedPages)
            {
                governor.NoteOperation();
                memory.Reserve(32L);
                PdfDictionary page = pool.CopiedObjects[selected.Number] as PdfDictionary
                    ?? throw new QpdfParseException("Selected page was removed from the graph.", null, selected.Number);
                ParserContainers.AddItem(memory, transformed, new PageLocation(new PdfReference(selected.Number, selected.Generation), page), 16L);
            }
            document.SetInspectionPages(transformed);
            if (publication)
            { ApplyInspectionPublication(document, pool, plan, lifetime, memory, governor, cancellationToken); }
            if (editAttachments)
            { await ApplyInspectionAttachmentsAsync(host, document, pool, plan, limits, governor, diagnostics, memory, lifetime, cancellationToken).ConfigureAwait(false); }
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
    }

    private static bool InspectionGroupIncluded(IInspectionGraphPlan plan, int group)
        => plan.Collation is null || plan.Collation.GroupSizes[plan.Collation.GroupSizes.Count == 1 ? 0 : group] > 0;

    private static async ValueTask<List<List<InspectionPage>>> PrepareInspectionSourcesAsync(
        IQpdfHost host, QpdfDocument document, List<InspectionPage> original, GraphCopier pool,
        IInspectionGraphPlan plan, InspectionCatalog? catalogAssembly, bool preserveLabels,
        Content.ContentPrunePolicy? prunePolicy, bool prunePrimary, bool[]? pruneGroups,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics,
        ParserMemoryScope memory, CancellationToken token)
    {
        List<List<InspectionPage>> groups = ParserContainers.CreateList<List<InspectionPage>>(memory);
        if (plan.Sources.Count == 0)
        {
            ParserContainers.AddItem(memory, groups, original, IntPtr.Size);
            return groups;
        }
        using ParserMemoryScope planning = ParserMemoryScope.Create(governor);
        Dictionary<string, List<int>> sources = ParserContainers.CreateDictionary<string, List<int>>(planning);
        for (int index = 0; index < plan.Sources.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            string path = plan.Sources[index].Input.Value;
            if (path == ".") { path = plan.Input.Value; }
            if (!sources.TryGetValue(path, out List<int>? indices))
            {
                indices = ParserContainers.CreateList<int>(planning);
                ParserContainers.EnsureEntryCapacity(planning, sources, IntPtr.Size);
                sources.Add(path, indices);
            }
            ParserContainers.AddItem(planning, indices, index, sizeof(int));
            ParserContainers.AddItem(memory, groups, ParserContainers.CreateList<InspectionPage>(memory), IntPtr.Size);
        }
        foreach (KeyValuePair<string, List<int>> source in sources)
        {
            if (!string.Equals(source.Key, plan.Input.Value, StringComparison.Ordinal))
            { ValidateSourceCredentials(plan.Sources, source.Value, governor); }
        }
        foreach (KeyValuePair<string, List<int>> source in sources)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            bool primary = string.Equals(source.Key, plan.Input.Value, StringComparison.Ordinal);
            await using QpdfDocument? secondary = primary ? null : await QpdfDocument.OpenInspectionCore(host,
                QpdfPath.From(source.Key), plan.Sources[source.Value[0]].Password,
                limits, governor, diagnostics, plan.Recovery, token, false).ConfigureAwait(false);
            QpdfDocument reader = secondary ?? document;
            GraphCopier copier = primary ? pool : GraphCopier.CreateInspectionOwned(reader.Engine, governor, limits, pool.NextId, memory, false);
            using ParserMemoryScope ranges = ParserMemoryScope.Create(governor);
            List<(int Group, IReadOnlyList<int> Pages, bool Included)> requests =
                ParserContainers.CreateList<(int, IReadOnlyList<int>, bool)>(ranges);
            long includedPages = 0L;
            foreach (int group in source.Value)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                IReadOnlyList<int> pages = Pages.PageRanges.Expand(plan.Sources[group].Selection.Range,
                    reader.PageCount, limits.MaxContainerSize, governor, ranges);
                bool included = InspectionGroupIncluded(plan, group);
                ParserContainers.AddItem(ranges, requests, (group, pages, included), 24L);
                if (included) { includedPages += pages.Count; }
                if (primary || !included) { continue; }
                foreach (int number in pages)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    copier.RegisterPageRoot(reader.PageReferences[number - 1].Source?.ObjectNumber);
                }
            }
            bool hasPages = includedPages != 0L;
            bool prune = primary ? prunePrimary
                : hasPages && prunePolicy is not null && prunePolicy.ShouldPrune(reader.Engine, limits, governor, token);
            Content.SourceLabels? sourceLabels = preserveLabels && hasPages
                ? Content.PageLabelMerge.ReadSource(reader.Engine, copier, limits, governor, token) : null;
            if (catalogAssembly is not null && hasPages)
            { Content.CatalogCopy.IncludeSourceVersion(ref catalogAssembly.Version, ref catalogAssembly.Extension, reader, governor, token); }
            List<PageLocation>? kept = catalogAssembly is not null && !primary && hasPages
                ? ParserContainers.CreateList<PageLocation>(ranges) : null;
            using (PageInheritance.Batch? ancestry = PageInheritance.Batch.TryCreate(!primary && includedPages > 1L,
                reader.Engine, WriterInherited, limits, governor, token))
            {
                foreach ((int group, IReadOnlyList<int> pages, bool included) in requests)
                {
                    if (!included) { continue; }
                    if (pruneGroups is not null) { pruneGroups[group] = prune; }
                    foreach (int number in pages)
                    {
                        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                        InspectionPage selected;
                        if (primary) { selected = original[number - 1]; }
                        else
                        {
                            PageLocation location = reader.PageReferences[number - 1];
                            if (kept is not null) { ParserContainers.AddItem(ranges, kept, location, 16L); }
                            IReadOnlyDictionary<PdfName, QpdfObject> extra = MaterializeAncestors(reader.Engine,
                                location, copier, governor, limits, token, memory, ancestry);
                            (int id, PdfDictionary page) = copier.CopyPageRoot(location.Page, location.Source?.ObjectNumber, extra, false, null);
                            selected = new(id, 0, page);
                        }
                        ParserContainers.AddItem(memory, groups[group], selected, 16L);
                        if (catalogAssembly is not null)
                        {
                            if (!catalogAssembly.Pages.ContainsKey(selected.Number))
                            { ParserContainers.EnsureEntryCapacity(memory, catalogAssembly.Pages, 24L); }
                            catalogAssembly.Pages[selected.Number] = (sourceLabels, number, copier);
                        }
                    }
                }
            }
            if (primary || copier.CopiedObjects.Count == 0) { continue; }
            if (catalogAssembly is not null && kept is not null)
            {
                Content.CopiedForm? form = Content.AcroFormCopy.TryCopySourceForm(reader.Engine, copier, kept,
                    true, limits, governor, memory, token, false);
                (form, _, _, _) = Content.SourceActions.BindDocument(reader.Engine, copier, form, form, null,
                    false, true, null, limits, governor, memory, token);
                ParserContainers.EnsureEntryCapacity(memory, catalogAssembly.Forms, IntPtr.Size);
                catalogAssembly.Forms.Add(copier, form);
                Content.OptionalContent.Capture(reader.Engine, copier, null, memory, governor, token);
                ParserContainers.AddItem(memory, catalogAssembly.Sources, copier, IntPtr.Size);
            }
            foreach (KeyValuePair<int, QpdfObject> entry in copier.CopiedObjects)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                StoreInspectionObject(pool, entry.Key, entry.Value);
            }
            // Copied headers borrow immutable values and possibly stream slices.
            // Move their admission before closing this private reader; subsequent
            // sources and inspection use only the completed destination pool.
            reader.Engine.TransferExclusiveValuesTo(memory);
        }
        return groups;
    }

    internal static Dictionary<PdfName, QpdfObject> CopyInspectionEntries(PdfDictionary dictionary,
        ParserMemoryScope memory, ResourceGovernor governor)
    {
        Dictionary<PdfName, QpdfObject> entries = ParserContainers.CreateDictionary<PdfName, QpdfObject>(memory);
        using var enumerator = dictionary.GetEntryEnumerator();
        while (enumerator.MoveNext())
        {
            governor.NoteOperation();
            SetInspectionEntry(entries, enumerator.Current.Key, enumerator.Current.Value, memory);
        }
        return entries;
    }

    internal static void SetInspectionEntry(Dictionary<PdfName, QpdfObject> entries, PdfName key, QpdfObject value, ParserMemoryScope memory)
    {
        if (!entries.ContainsKey(key)) { ParserContainers.EnsureEntryCapacity(memory, entries, IntPtr.Size); }
        entries[key] = value;
    }

    internal static void StoreInspectionObject(GraphCopier pool, int id, QpdfObject value)
    {
        if (pool.CopiedObjects.ContainsKey(id)) { pool.ReplaceCopiedObject(id, value); }
        else { _ = pool.AddIndirectObjectWithId(value, id); }
    }

    private static void ReplaceInspectionCatalogEntry(QpdfDocument document, GraphCopier pool, PdfName key,
        QpdfObject? value, ParserMemoryScope memory, QpdfLimits limits, ResourceGovernor governor)
    {
        QpdfObject root = document.Engine.Trailer.TryGet(InspectionRoot, out QpdfObject raw) ? raw
            : throw new QpdfParseException("Trailer has no catalog root.", null, null);
        PdfDictionary catalog = PageInheritance.Resolve(document.Engine, root, limits, governor) as PdfDictionary
            ?? throw new QpdfParseException("Catalog root is not a dictionary.", null, null);
        Dictionary<PdfName, QpdfObject> entries = CopyInspectionEntries(catalog, memory, governor);
        if (value is null) { entries.Remove(key); }
        else { SetInspectionEntry(entries, key, value, memory); }
        memory.Reserve(56L);
        PdfDictionary updated = PdfDictionary.FromOwnedEntries(entries);
        if (root is PdfReference reference) { StoreInspectionObject(pool, reference.ObjectNumber, updated); }
        else
        {
            Dictionary<PdfName, QpdfObject> trailer = CopyInspectionEntries(document.Engine.Trailer, memory, governor);
            SetInspectionEntry(trailer, InspectionRoot, updated, memory);
            memory.Reserve(56L);
            document.Engine.SetInspectionTrailer(PdfDictionary.FromOwnedEntries(trailer));
        }
    }

    private static void AssembleInspectionCatalog(QpdfDocument document, GraphCopier pool, InspectionCatalog assembly,
        IReadOnlyList<(GraphCopier Copier, int Id)> occurrences,
        IReadOnlyList<(GraphCopier Copier, Content.CopiedForm Part, int Occurrence)> duplicatedFields,
        ParserMemoryScope memory, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        List<(GraphCopier Copier, Content.CopiedForm Part, int Occurrence)> additional =
            ParserContainers.CreateList<(GraphCopier, Content.CopiedForm, int)>(memory);
        foreach (GraphCopier copier in assembly.Sources)
        {
            governor.NoteOperation();
            if (assembly.Forms[copier] is Content.CopiedForm part)
            { ParserContainers.AddItem(memory, additional, (copier, part, -1), 24L); }
        }
        foreach (var part in duplicatedFields) { governor.NoteOperation(); ParserContainers.AddItem(memory, additional, part, 24L); }
        PdfDictionary? form = BuildInspectionForm(pool, assembly.Forms[pool], occurrences,
            Array.Empty<Content.AcroFormCopy.FieldImport>(), additional, true, memory, limits, governor, diagnostics, token);
        if (form is not null)
        { form = ProjectSelectedInspectionForm(form, pool, document.Engine, occurrences, limits, governor, memory, token); }
        if (form is not null)
        {
            ReplaceInspectionCatalogEntry(document, pool, PdfName.FromAscii("AcroForm"),
                pool.AddIndirectObjectWithId(form, pool.NextId), memory, limits, governor);
        }
        QpdfObject root = document.Engine.Trailer.TryGet(InspectionRoot, out QpdfObject raw) ? raw
            : throw new QpdfParseException("Trailer has no catalog root.", null, null);
        PdfDictionary catalog = PageInheritance.Resolve(document.Engine, root, limits, governor) as PdfDictionary
            ?? throw new QpdfParseException("Catalog root is not a dictionary.", null, null);
        Dictionary<PdfName, QpdfObject>? extras = null;
        PdfName optionalKey = PdfName.FromAscii("OCProperties");
        if (catalog.TryGet(optionalKey, out QpdfObject properties))
        {
            extras = ParserContainers.CreateDictionary<PdfName, QpdfObject>(memory);
            SetInspectionEntry(extras, optionalKey, pool.RetainSourceValue(properties), memory);
            Content.OptionalContent.Capture(document.Engine, pool, extras, memory, governor, token);
        }
        QpdfObject? optional = Content.OptionalContent.Combine(pool, assembly.Sources,
            Array.Empty<GraphCopier>(), extras, pool.CopiedObjects, governor, token);
        if (optional is not null)
        { ReplaceInspectionCatalogEntry(document, pool, optionalKey, optional, memory, limits, governor); }
        if (assembly.Version is QpdfDocument.VersionParts version)
        {
            document.Engine.InspectionVersionParts = version;
            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
            PdfDictionaryLookup.TryGet(catalog, "Extensions"u8, governor, token, out QpdfObject original);
            QpdfObject? extensions = PrepareExtensions(original, document.Engine, governor.PrivateVersion(version.Major, version.Minor), assembly.Extension, graph);
            if (!ReferenceEquals(extensions, original))
            { ReplaceInspectionCatalogEntry(document, pool, PdfName.FromAscii("Extensions"), extensions, memory, limits, governor); }
            graph.Memory.TransferTo(memory, graph.Bytes);
        }
    }

    private static PdfDictionary? BuildInspectionForm(GraphCopier pool, Content.CopiedForm? sourceForm,
        IReadOnlyList<(GraphCopier Copier, int Id)> occurrences, IReadOnlyList<Content.AcroFormCopy.FieldImport> imports,
        IReadOnlyList<(GraphCopier Copier, Content.CopiedForm Part, int Occurrence)> additional, bool pruneFields,
        ParserMemoryScope memory, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        if (sourceForm is null && imports.Count == 0 && additional.Count == 0) { return null; }
        Dictionary<int, QpdfObject> assembled = ParserContainers.CreateDictionary<int, QpdfObject>(memory);
        foreach (KeyValuePair<int, QpdfObject> entry in pool.CopiedObjects)
        {
            governor.NoteOperation(); ParserContainers.EnsureEntryCapacity(memory, assembled, IntPtr.Size);
            assembled.Add(entry.Key, entry.Value);
        }
        PdfDictionary? form = Content.AcroFormCopy.BuildSingle(sourceForm, pool, pruneFields, occurrences,
            assembled, imports, additional, limits, governor, diagnostics, memory, token);
        foreach (KeyValuePair<int, QpdfObject> entry in assembled)
        { governor.NoteOperation(); StoreInspectionObject(pool, entry.Key, entry.Value); }
        return form;
    }

    internal static void UpdateInspectionForm(QpdfDocument document, GraphCopier pool,
        IReadOnlyList<(GraphCopier Copier, int Id)> occurrences,
        IReadOnlyList<Content.AcroFormCopy.FieldImport> imports, bool pruneFields,
        ParserMemoryScope memory, QpdfLimits limits, ResourceGovernor governor,
        DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        List<PageLocation> kept = ParserContainers.CreateList<PageLocation>(memory);
        foreach ((GraphCopier copier, int id) in occurrences)
        {
            governor.NoteOperation();
            PdfDictionary page = copier.CopiedObjects[id] as PdfDictionary
                ?? throw new QpdfParseException("Selected page is not a dictionary.", null, id);
            int generation = document.Engine.XRefEntries.TryGetValue(id, out XrefEntry entry)
                && entry.Kind == XrefEntryKind.Uncompressed ? entry.Second : 0;
            ParserContainers.AddItem(memory, kept, new PageLocation(new PdfReference(id, generation), page), 16L);
        }
        Content.CopiedForm? sourceForm = Content.AcroFormCopy.TryCopySourceForm(document.Engine, pool, kept,
            pruneFields, limits, governor, memory, cancellationToken, true);
        PdfDictionary? form = BuildInspectionForm(pool, sourceForm, occurrences, imports,
            Array.Empty<(GraphCopier, Content.CopiedForm, int)>(), pruneFields, memory, limits, governor, diagnostics, cancellationToken);
        if (form is null) { return; }
        if (pruneFields && ProjectSelectedInspectionForm(form, pool, document.Engine, occurrences,
            limits, governor, memory, cancellationToken) is PdfDictionary projected && !ReferenceEquals(projected, form))
        {
            ReplaceInspectionCatalogEntry(document, pool, PdfName.FromAscii("AcroForm"),
                pool.AddIndirectObjectWithId(projected, pool.NextId), memory, limits, governor);
            return;
        }
        QpdfObject root = document.Engine.Trailer.TryGet(InspectionRoot, out QpdfObject raw) ? raw
            : throw new QpdfParseException("Trailer has no catalog root.", null, null);
        PdfDictionary catalog = PageInheritance.Resolve(document.Engine, root, limits, governor) as PdfDictionary
            ?? throw new QpdfParseException("Catalog root is not a dictionary.", null, null);
        PdfName formKey = PdfName.FromAscii("AcroForm");
        if (catalog.TryGet(formKey, out QpdfObject originalForm) && originalForm is PdfReference formReference)
        {
            StoreInspectionObject(pool, formReference.ObjectNumber, form);
            return;
        }
        Dictionary<PdfName, QpdfObject> entries = CopyInspectionEntries(catalog, memory, governor);
        QpdfObject formValue = originalForm is PdfDictionary ? form : pool.AddIndirectObjectWithId(form, pool.NextId);
        SetInspectionEntry(entries, formKey, formValue, memory);
        memory.Reserve(32L);
        PdfDictionary updated = PdfDictionary.FromOwnedEntries(entries);
        if (root is PdfReference catalogReference)
        {
            StoreInspectionObject(pool, catalogReference.ObjectNumber, updated);
        }
        else
        {
            Dictionary<PdfName, QpdfObject> trailer = CopyInspectionEntries(document.Engine.Trailer, memory, governor);
            SetInspectionEntry(trailer, InspectionRoot, updated, memory);
            memory.Reserve(32L);
            document.Engine.SetInspectionTrailer(PdfDictionary.FromOwnedEntries(trailer));
        }
    }

}
