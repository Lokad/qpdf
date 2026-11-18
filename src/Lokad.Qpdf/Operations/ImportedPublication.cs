using Lokad.Qpdf.Content;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Operations;

internal static class ImportedPublication
{
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

        RetireSuperseded(model, trailer, resolver, governor, token);
        graph.Memory.Reserve(48L); graph.Add(48L);
        ImportedModel result = model with { Trailer = trailer };
        graph.Memory.TransferTo(memory, graph.Bytes); return result;
    }

    private static void RetireSuperseded(ImportedModel model, PdfDictionary trailer, IIndirectResolver resolver,
        ResourceGovernor governor, CancellationToken token)
    {
        using ParserMemoryScope walk = ParserMemoryScope.Create(governor);
        HashSet<int> before = GraphReachability.Find(resolver, model.Trailer, walk, governor);
        List<QpdfObject> roots = ParserContainers.CreateList<QpdfObject>(walk);
        ParserContainers.AddItem(walk, roots, trailer);
        // JSON already preserves unrelated unreferenced declarations. Their private
        // edges are independent roots too; removing catalog roles must not break them.
        foreach ((int number, QpdfObject value) in model.Objects)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (!before.Contains(number)) { ParserContainers.AddItem(walk, roots, value); }
        }
        walk.Reserve(checked(56L + IntPtr.Size * (long)roots.Count));
        QpdfObject[] items = new QpdfObject[roots.Count];
        for (int index = 0; index < items.Length; index++)
        { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); items[index] = roots[index]; }
        HashSet<int> after = GraphReachability.Find(resolver, PdfArray.FromOwnedItems(items), walk, governor);
        foreach (int number in before)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (after.Contains(number)) { continue; }
            model.Objects.Remove(number); model.CompressedObjects?.Remove(number);
        }
    }
}
