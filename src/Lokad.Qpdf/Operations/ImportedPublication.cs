using Lokad.Qpdf.Content;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Operations;

internal static class ImportedPublication
{
    private static readonly PdfName[] ImageResources = [PdfName.FromAscii("Resources")];

    internal static (int First, int Next) TransformPagePrograms(ImportedModel model, PoolResolver resolver, IReadOnlyList<PageLocation> pages,
        ContentTransformRequest request, ImageOptimizeRequest images, bool pruneResources,
        QpdfLimits limits, ParserMemoryScope memory, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        if (pages.Count == 0) { return (0, 0); }
        if (request.Externalize || images.Optimize)
        {
            BindImageResources(model.Objects, resolver, pages, limits, governor, token);
        }
        using ParserMemoryScope phase = ParserMemoryScope.Create(governor);
        List<int> ids = ParserContainers.CreateList<int>(phase, pages.Count, sizeof(int));
        foreach (PageLocation page in pages)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            PdfReference source = page.Source ?? throw new InvalidOperationException("Missing prepared page identity.");
            ids.Add(source.ObjectNumber);
        }
        int first = NextObjectNumber(model, governor, token);
        GraphCopier copier = GraphCopier.CreateBoundOwned(model.Objects, resolver, governor, limits, first, memory);
        ContentTransforms.ApplyPageTransforms(copier, ids, request, limits, governor, diagnostics, token);
        if (request.Externalize)
        { ContentTransforms.ExternalizeForms(copier, ids, request, null, limits, governor, diagnostics, token); }
        if (pruneResources)
        { ContentTransforms.ApplyResourcePruning(copier, ids, request.BoundPageContent, limits, governor, diagnostics, token); }
        ImageOptimizer.Apply(copier, ids, images, limits, governor, diagnostics, token);
        return (first, copier.NextId);
    }

    internal static int NextObjectNumber(ImportedModel model, ResourceGovernor governor, CancellationToken token)
    {
        int largest = 0;
        foreach (int number in model.Objects.Keys)
        { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); largest = Math.Max(largest, number); }
        if (model.OriginalPairs is { } reserved)
        {
            foreach (int number in reserved.Values)
            { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); largest = Math.Max(largest, number); }
        }
        if (largest == int.MaxValue)
        { throw new QpdfParseException("No object identity is available for content preparation.", null, null); }
        return largest + 1;
    }

    internal static async ValueTask<(ImportedModel Model, int First, int Next)> EditAttachmentsAsync(
        IQpdfHost host, ImportedModel model, AttachmentEdits edits, QpdfRecoveryMode recovery, bool preserveDetached,
        QpdfLimits limits, ParserMemoryScope memory, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        JsonImport.CheckObjectCount(model, limits);
        using ParserMemoryScope walk = ParserMemoryScope.Create(governor);
        PoolResolver resolver = PoolResolver.Create(walk, model.Objects);
        PdfDictionary catalog = JsonExecution.ReadBoundCatalog(model, resolver, governor, token);
        HashSet<int>? before = preserveDetached ? GraphReachability.Find(resolver, model.Trailer, walk, governor) : null;
        int first = NextObjectNumber(model, governor, token);
        GraphCopier pool = GraphCopier.CreateBoundOwned(model.Objects, resolver, governor, limits, first, memory);
        (PdfDictionary updated, PdfVersion version, _) = await AttachmentAssembler.EditCatalogAsync(host, resolver, model.Trailer,
            catalog, pool, model.HeaderVersion, edits, recovery, limits, governor, diagnostics, memory, token).ConfigureAwait(false);
        JsonImport.CheckObjectCount(model, limits);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfDictionary trailer = model.Trailer;
        if (!ReferenceEquals(updated, catalog))
        {
            if (before is not null)
            {
                // New assembly objects are eligible for retirement if an edit
                // superseded them before the catalog was linked.
                for (int id = first; id < pool.NextId; id++)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    if (model.Objects.ContainsKey(id)) { ParserContainers.AddSetItem(walk, before, id, sizeof(int)); }
                }
            }
            PdfReference replacement = pool.AddIndirectObjectWithId(updated, pool.NextId);
            trailer = PdfDictionaryRebuild.ReplaceEntry(graph, trailer, "Root"u8, replacement);
            if (before is not null)
            { GraphReachability.RetireSuperseded(model.Objects, model.CompressedObjects, before, trailer, resolver, walk, governor, token); }
        }
        JsonImport.CheckObjectCount(model, limits);
        graph.Memory.TransferTo(memory, graph.Bytes);
        memory.Reserve(ImportedModel.AllocationBytes);
        return (model with { Trailer = trailer, HeaderVersion = version }, first, pool.NextId);
    }

    private static void BindImageResources(Dictionary<int, QpdfObject> objects, PoolResolver resolver,
        IReadOnlyList<PageLocation> pages, QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
    {
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        using ParserMemoryScope pending = ParserMemoryScope.Create(governor);
        using PageInheritance.Batch? ancestry = PageInheritance.Batch.TryCreate(pages.Count > 1, resolver,
            ImageResources, limits, governor, token);
        List<(int Id, PdfDictionary Value)>? changes = null;
        foreach (PageLocation location in pages)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            PdfReference reference = location.Source ?? throw new InvalidOperationException("Missing prepared page identity.");
            PdfDictionary page = (PdfDictionary)objects[reference.ObjectNumber];
            PageLocation current = new(reference, page);
            QpdfObject? inherited = (ancestry?.Find(current) ?? PageInheritance.FindAncestors(resolver, current,
                ImageResources, limits, governor, token))[0];
            if (inherited is null) { continue; }
            changes ??= ParserContainers.CreateList<(int, PdfDictionary)>(pending);
            PdfDictionary replacement = PdfDictionaryRebuild.ReplaceEntry(graph, page, "Resources"u8, inherited);
            ParserContainers.AddItem(pending, changes, (reference.ObjectNumber, replacement), 2L * IntPtr.Size);
        }
        if (changes is null) { return; }
        graph.Retain();
        // Resolve the complete ancestry against the original headers.
        foreach ((int id, PdfDictionary value) in changes)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            objects[id] = value;
        }
    }

    internal static HashSet<int>? FindRetainedObjects(ImportedModel source, JsonImport.RenumberedModel graph,
        PdfDictionary catalog, JsonDetachedObjects policy, ParserMemoryScope? splitMemory,
        ParserMemoryScope scratch, ResourceGovernor governor, CancellationToken token, out List<QpdfObject>? splitRoots)
    {
        splitRoots = null;
        HashSet<int>? exclusions = source.DetachedRootExclusions;
        if (policy == JsonDetachedObjects.Preserve && splitMemory is null && exclusions is not { Count: > 0 })
        { return null; }

        PdfReference? info = null;
        if (graph.InfoNumber is int number)
        { scratch.Reserve(32L); info = new(number, 0); }

        HashSet<int>? active = policy == JsonDetachedObjects.Prune || splitMemory is not null
            ? OutputModelPreparation.FindPublishedObjects(graph.Objects, catalog, null, info, scratch, governor) : null;
        if (policy == JsonDetachedObjects.Prune) { return active; }

        ParserMemoryScope detachedMemory = splitMemory ?? scratch;
        List<QpdfObject> roots = ParserContainers.CreateList<QpdfObject>(scratch);
        ParserContainers.AddItem(scratch, roots, catalog, IntPtr.Size);
        if (info is not null) { ParserContainers.AddItem(scratch, roots, info, IntPtr.Size); }
        foreach ((int bound, int published) in graph.Mapping)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (published == 1 || active?.Contains(published) == true || exclusions?.Contains(bound) == true)
            { continue; }
            detachedMemory.Reserve(32L);
            PdfReference root = new(published, 0);
            ParserContainers.AddItem(scratch, roots, root, IntPtr.Size);
            if (splitMemory is not null)
            {
                splitRoots ??= ParserContainers.CreateList<QpdfObject>(splitMemory);
                ParserContainers.AddItem(splitMemory, splitRoots, root, IntPtr.Size);
            }
        }
        return active is not null && splitRoots is null ? active
            : GraphReachability.Find(PoolResolver.Create(scratch, graph.Objects), roots, scratch, governor);
    }

    internal static ImportedModel Apply(ImportedModel model, DocumentPublicationPolicy policy, QpdfLimits limits,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token, out PdfReference? replacedCatalog)
    {
        replacedCatalog = null;
        if (!policy.HasRemovals) { return model; }
        // Retirement must not hide an over-limit imported object table.
        JsonImport.CheckObjectCount(model, limits);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        IIndirectResolver resolver = PoolResolver.Create(graph.Memory, model.Objects);
        PdfDictionary trailer = model.Trailer;
        if (PdfDictionaryLookup.TryGet(trailer, "Root"u8, governor, token, out QpdfObject root)
            && CatalogCopy.ResolveDictionary(root, resolver, governor, token) is PdfDictionary catalog)
        {
            PdfDictionary selected = policy.SelectCatalog(catalog, graph);
            // A fresh catalog role leaves private aliases of the original intact.
            if (!ReferenceEquals(selected, catalog))
            {
                trailer = PdfDictionaryRebuild.ReplaceEntry(graph, trailer, "Root"u8, selected);
                replacedCatalog = root as PdfReference;
            }
        }
        if (policy.RemoveInfo)
        {
            QpdfObject? info = policy.SelectInfo(trailer, resolver, graph);
            if (info is not null) { trailer = PdfDictionaryRebuild.ReplaceEntry(graph, trailer, "Info"u8, info); }
            else if (PdfDictionaryLookup.TryGet(trailer, "Info"u8, governor, token, out _))
            { trailer = PdfDictionaryRebuild.RemoveEntry(graph, trailer, "Info"u8); }
        }
        if (ReferenceEquals(trailer, model.Trailer)) { return model; }

        using (ParserMemoryScope walk = ParserMemoryScope.Create(governor))
        {
            HashSet<int> before = GraphReachability.Find(resolver, model.Trailer, walk, governor);
            GraphReachability.RetireSuperseded(model.Objects, model.CompressedObjects, before, trailer, resolver, walk, governor, token);
        }
        graph.Memory.Reserve(ImportedModel.AllocationBytes); graph.Add(ImportedModel.AllocationBytes);
        ImportedModel result = model with { Trailer = trailer };
        graph.Memory.TransferTo(memory, graph.Bytes); return result;
    }
}
