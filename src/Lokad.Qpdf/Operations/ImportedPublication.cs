using Lokad.Qpdf.Content;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Operations;

internal static class ImportedPublication
{
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
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
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
            { trailer = PdfDictionaryRebuild.ReplaceEntry(graph, trailer, "Root"u8, selected); }
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
