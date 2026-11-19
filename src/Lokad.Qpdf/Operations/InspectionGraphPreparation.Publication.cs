using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;
using System.Runtime.InteropServices;

namespace Lokad.Qpdf.Operations;

internal static partial class InspectionGraphPreparation
{
    private static async ValueTask ApplyInspectionAttachmentsAsync(IQpdfHost host, QpdfDocument document, GraphCopier pool,
        IInspectionGraphPlan plan, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics,
        ParserMemoryScope memory, InspectionGraph lifetime, CancellationToken token)
    {
        IIndirectResolver resolver = document.Engine;
        PdfDictionary trailer = document.Engine.Trailer;
        if (!PdfDictionaryLookup.TryGet(trailer, "Root"u8, governor, token, out QpdfObject root)
            || CatalogCopy.ResolveDictionary(root, resolver, governor, token) is not PdfDictionary catalog)
        { throw new QpdfParseException("The attachment source has no catalog.", null, null); }
        QpdfDocument.VersionParts parts = document.EffectiveVersionParts;
        AttachmentEdits edits = new(plan.AttachmentsToAdd, plan.AttachmentsToRemove, plan.AttachmentsToCopy);
        (PdfDictionary updated, PdfVersion version, bool updatedVersion) = await AttachmentAssembler.EditCatalogAsync(host, resolver, trailer, catalog,
            pool, governor.PrivateVersion(parts.Major, parts.Minor), edits, plan.Recovery,
            limits, governor, diagnostics, memory, token).ConfigureAwait(false);
        if (updatedVersion)
        { document.Engine.InspectionVersionParts = new(version.Major, version.Minor); }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        document.Engine.SetInspectionTrailer(ReplaceInspectionCatalogRoot(pool, graph, trailer, root, updated, lifetime));
        graph.Memory.TransferTo(memory, graph.Bytes);
    }

    private static PdfDictionary ReplaceInspectionCatalogRoot(GraphCopier pool, OwnedAllocationBatch graph, PdfDictionary trailer,
        QpdfObject root, PdfDictionary catalog, InspectionGraph lifetime)
    {
        PdfReference reference = pool.AddIndirectObjectWithId(catalog, pool.NextId);
        lifetime.ReplacedCatalog ??= root as PdfReference;
        return PdfDictionaryRebuild.ReplaceEntry(graph, trailer, "Root"u8, reference);
    }

    private static void ProjectInspectionArticles(QpdfDocument document, GraphCopier pool, List<InspectionPage> pages,
        bool hasBeads, Dictionary<int, PdfReference>? canonicalPages, IReadOnlyDictionary<int, double[]>? frames, InspectionGraph lifetime,
        QpdfLimits limits, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        PdfDictionary trailer = document.Engine.Trailer;
        if (!trailer.TryGet(InspectionRoot, out QpdfObject root)
            || CatalogCopy.ResolveDictionary(root, document.Engine, governor, token) is not PdfDictionary catalog)
        { return; }
        bool hasRole = PdfDictionaryLookup.TryGet(catalog, "Threads"u8, governor, token, out QpdfObject raw);
        if (!hasRole && !hasBeads) { return; }

        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        ArticleThreads? threads = hasRole ? ArticleThreads.Read(raw, document.Engine, limits, scratch, governor, token) : null;
        List<PdfReference> references = ParserContainers.CreateList<PdfReference>(scratch, pages.Count, IntPtr.Size);
        foreach (InspectionPage page in pages)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            // A projected bead can retain its page reference after this scratch owner closes.
            memory.Reserve(32L);
            references.Add(new PdfReference(page.Number, page.Generation));
        }
        (PdfDictionary projected, Dictionary<int, QpdfObject>? edits) = ArticleThreads.Project(threads, catalog,
            document.Engine, CollectionsMarshal.AsSpan(references), canonicalPages, frames, null,
            pool.NextId - 1, limits, memory, governor, token);
        if (edits is not null)
        {
            foreach ((int id, QpdfObject value) in edits)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                StoreInspectionObject(pool, id, value);
            }
        }
        if (!ReferenceEquals(projected, catalog))
        {
            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
            document.Engine.SetInspectionTrailer(ReplaceInspectionCatalogRoot(pool, graph, trailer, root, projected, lifetime));
            graph.Memory.TransferTo(memory, graph.Bytes);
        }
    }

    private static void ApplyInspectionPublication(QpdfDocument document, GraphCopier pool, IInspectionGraphPlan plan,
        bool changesTagTopology, InspectionGraph lifetime, ParserMemoryScope memory, ResourceGovernor governor,
        DiagnosticBag diagnostics, CancellationToken token)
    {
        DocumentPublicationPolicy policy = plan.PublicationPolicy;
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PoolResolver resolver = pool.CreateResolver(graph.Memory);
        PdfDictionary original = document.Engine.Trailer, trailer = original;
        if (PdfDictionaryLookup.TryGet(trailer, "Root"u8, governor, token, out QpdfObject root)
            && CatalogCopy.ResolveDictionary(root, resolver, governor, token) is PdfDictionary catalog)
        {
            PdfDictionary selected = policy.SelectCatalog(catalog, graph);
            if (changesTagTopology && !policy.RemoveStructure)
            { selected = CatalogCopy.WithoutActiveStructure(selected, resolver, memory, governor, diagnostics, token); }
            if (plan.Labels.Count > 0)
            {
                PdfDictionary labels = PageLabelAssembly.BuildPageLabels(plan.Labels, document.PageCount, governor)
                    ?? throw new InvalidOperationException("Page-label specifications produced no labels.");
                QpdfObject labelRoot = plan is JsonPlan ? pool.AddIndirectObjectWithId(labels, pool.NextId) : labels;
                selected = PdfDictionaryRebuild.ReplaceEntry(graph, selected, "PageLabels"u8, labelRoot);
            }
            // Publish a new role; source catalog aliases keep their original header.
            if (!ReferenceEquals(selected, catalog))
            {
                trailer = ReplaceInspectionCatalogRoot(pool, graph, trailer, root, selected, lifetime);
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
