using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Operations;

internal static partial class InspectionGraphPreparation
{
    private static void ApplyInspectionPublication(QpdfDocument document, GraphCopier pool, JsonPlan plan,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        DocumentPublicationPolicy policy = new(plan.RemoveInfo, plan.RemoveMetadata,
            plan.RemoveAcroform, plan.RemoveStructure, plan.RemoveLabels);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PoolResolver resolver = pool.CreateResolver(graph.Memory);
        PdfDictionary original = document.Engine.Trailer, trailer = original;
        if (PdfDictionaryLookup.TryGet(trailer, "Root"u8, governor, token, out QpdfObject root)
            && CatalogCopy.ResolveDictionary(root, resolver, governor, token) is PdfDictionary catalog)
        {
            PdfDictionary selected = policy.SelectCatalog(catalog, graph);
            if (plan.Labels.Count > 0)
            {
                PdfDictionary labels = PageLabelAssembly.BuildPageLabels(plan.Labels, document.PageCount, governor)
                    ?? throw new InvalidOperationException("Page-label specifications produced no labels.");
                PdfReference reference = pool.AddIndirectObjectWithId(labels, pool.NextId);
                selected = PdfDictionaryRebuild.ReplaceEntry(graph, selected, "PageLabels"u8, reference);
            }
            // Publish a new role; source catalog aliases keep their original header.
            if (!ReferenceEquals(selected, catalog))
            {
                PdfReference reference = pool.AddIndirectObjectWithId(selected, pool.NextId);
                trailer = PdfDictionaryRebuild.ReplaceEntry(graph, trailer, "Root"u8, reference);
            }
        }
        if (policy.RemoveInfo)
        {
            QpdfObject? info = policy.SelectInfo(trailer, resolver, graph);
            if (info is PdfDictionary dictionary) { info = pool.AddIndirectObjectWithId(dictionary, pool.NextId); }
            if (info is not null) { trailer = PdfDictionaryRebuild.ReplaceEntry(graph, trailer, "Info"u8, info); }
            else if (PdfDictionaryLookup.TryGet(trailer, "Info"u8, governor, token, out _))
            { trailer = PdfDictionaryRebuild.RemoveEntry(graph, trailer, "Info"u8); }
        }
        if (!ReferenceEquals(trailer, original)) { document.Engine.SetInspectionTrailer(trailer); }
        graph.Memory.TransferTo(memory, graph.Bytes);
    }
}
