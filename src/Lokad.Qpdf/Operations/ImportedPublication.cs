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

        using (ParserMemoryScope walk = ParserMemoryScope.Create(governor))
        {
            HashSet<int> before = GraphReachability.Find(resolver, model.Trailer, walk, governor);
            GraphReachability.RetireSuperseded(model.Objects, model.CompressedObjects, before, trailer, resolver, walk, governor, token);
        }
        graph.Memory.Reserve(48L); graph.Add(48L);
        ImportedModel result = model with { Trailer = trailer };
        graph.Memory.TransferTo(memory, graph.Bytes); return result;
    }
}
