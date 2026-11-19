using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;
using static Lokad.Qpdf.Writing.OutputCatalogPreparation;
using static Lokad.Qpdf.Writing.OutputVersionPolicy;

namespace Lokad.Qpdf.Operations;

internal static partial class InspectionGraphPreparation
{
    private static async ValueTask ApplyInspectionAttachmentsAsync(IQpdfHost host, QpdfDocument document, GraphCopier pool,
        IInspectionGraphPlan plan, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics,
        ParserMemoryScope memory, InspectionGraph lifetime, CancellationToken token)
    {
        memory.Reserve(128L);
        AttachmentEdits edits = new(plan.AttachmentsToAdd, plan.AttachmentsToRemove, plan.AttachmentsToCopy);
        SortedDictionary<string, AttachmentReference> existing = new(StringComparer.Ordinal);
        IIndirectResolver resolver = document.Engine;
        PdfDictionary trailer = document.Engine.Trailer;
        using (ParserMemoryScope entriesMemory = ParserMemoryScope.Create(governor))
        {
            foreach (EmbeddedFileEntry entry in EmbeddedFiles.ReadEntries(resolver, trailer, edits.Removes,
                limits, governor, memory, entriesMemory, token))
            {
                token.ThrowIfCancellationRequested(); governor.NoteOperation();
                memory.Reserve(64L);
                existing.Add(entry.Key, new(entry.Name, entry.Spec));
            }
        }
        Dictionary<int, QpdfObject> additions = ParserContainers.CreateDictionary<int, QpdfObject>(memory);
        var attached = await AttachmentAssembler.AssembleAttachmentsAsync(host, existing, additions, pool.NextId, edits,
            false, null, false, ParserContainers.CreateList<(IReadOnlyDictionary<int, int>, HashSet<int>)>(memory),
            plan.Recovery, limits, governor, diagnostics, memory, token).ConfigureAwait(false);
        foreach ((int number, QpdfObject value) in additions)
        {
            token.ThrowIfCancellationRequested(); governor.NoteOperation();
            _ = pool.AddIndirectObjectWithId(value, number);
        }

        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        if (!PdfDictionaryLookup.TryGet(trailer, "Root"u8, governor, token, out QpdfObject root)
            || CatalogCopy.ResolveDictionary(root, resolver, governor, token) is not PdfDictionary catalog)
        { throw new QpdfParseException("The attachment source has no catalog.", null, null); }
        PdfDictionaryLookup.TryGet(catalog, "Names"u8, governor, token, out QpdfObject names);
        PdfDictionary? oldNames = CatalogCopy.ResolveDictionary(names, resolver, governor, token);
        QpdfObject? newNames = attached.NamesRef;
        if (oldNames is not null)
        {
            if (attached.NamesRef is PdfReference attachedNames)
            {
                PdfDictionary wrapper = (PdfDictionary)additions[attachedNames.ObjectNumber];
                if (!PdfDictionaryLookup.TryGet(wrapper, "EmbeddedFiles"u8, governor, token, out QpdfObject tree))
                { throw new InvalidOperationException("An assembled attachment dictionary has no tree."); }
                pool.ReplaceCopiedObject(attachedNames.ObjectNumber, PdfDictionaryRebuild.ReplaceEntry(graph, oldNames, "EmbeddedFiles"u8, tree));
            }
            else
            {
                PdfDictionary remaining = PdfDictionaryRebuild.RemoveEntry(graph, oldNames, "EmbeddedFiles"u8);
                newNames = remaining.Count == 0 ? null : remaining;
            }
        }
        PdfDictionary updated = newNames is null ? PdfDictionaryRebuild.RemoveEntry(graph, catalog, "Names"u8)
            : PdfDictionaryRebuild.ReplaceEntry(graph, catalog, "Names"u8, newNames);
        if (attached.Inserted && !PdfDictionaryLookup.TryGet(updated, "PageMode"u8, governor, token, out _))
        { updated = PdfDictionaryRebuild.ReplaceEntry(graph, updated, "PageMode"u8, UseAttachmentsPageMode); }
        QpdfDocument.VersionParts parts = document.EffectiveVersionParts;
        PdfVersion version = governor.PrivateVersion(parts.Major, parts.Minor);
        int extension = CatalogCopy.ReadExtensionLevel(catalog, resolver, governor, token);
        IncludeVersionRequirement(ref version, ref extension, attached.Version, attached.ExtensionLevel, governor);
        IncludeVersionRequirement(ref version, ref extension,
            AuthoredVersionFloor(edits.Adds.Count > 0, attached.NamesRef is not null, false), 0, governor);
        if (version.Major != parts.Major || version.Minor != parts.Minor || attached.ExtensionLevel > 0)
        {
            document.Engine.InspectionVersionParts = new(version.Major, version.Minor);
            PdfDictionaryLookup.TryGet(updated, "Extensions"u8, governor, token, out QpdfObject original);
            QpdfObject? extensions = PrepareExtensions(original, resolver, version, extension, graph);
            if (!ReferenceEquals(extensions, original))
            {
                updated = extensions is null ? PdfDictionaryRebuild.RemoveEntry(graph, updated, "Extensions"u8)
                    : PdfDictionaryRebuild.ReplaceEntry(graph, updated, "Extensions"u8, extensions);
            }
        }
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

    private static void ApplyInspectionPublication(QpdfDocument document, GraphCopier pool, IInspectionGraphPlan plan,
        InspectionGraph lifetime, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        DocumentPublicationPolicy policy = plan.PublicationPolicy;
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
