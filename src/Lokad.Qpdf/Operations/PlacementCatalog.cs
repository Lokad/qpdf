using Lokad.Qpdf.Content;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;
using static Lokad.Qpdf.Writing.OutputCatalogPreparation;
using static Lokad.Qpdf.Writing.OutputVersionPolicy;

namespace Lokad.Qpdf.Operations;

internal static class PlacementCatalog
{
    internal static async ValueTask<(PdfDictionary Trailer, PdfVersion Version, OverlayApplication? Placements, int First, int Next)> ApplyBoundAsync(
        IQpdfHost host, QpdfPath input, ImportedModel model, PdfDictionary trailer, PoolResolver resolver,
        IReadOnlyList<PageLocation> pages, IReadOnlyList<OverlaySpec> underlays, IReadOnlyList<OverlaySpec> overlays,
        bool generateAppearances, QpdfRecoveryMode recovery, QpdfLimits limits, ParserMemoryScope memory,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        PdfVersion version = model.HeaderVersion;
        if (pages.Count == 0) { return (trailer, version, null, 0, 0); }
        if (!PdfDictionaryLookup.TryGet(trailer, "Root"u8, governor, token, out QpdfObject root)
            || CatalogCopy.ResolveDictionary(root, resolver, governor, token) is not PdfDictionary catalog)
        { throw new QpdfParseException("Imported trailer /Root must resolve to a dictionary.", null, null); }
        int first = ImportedPublication.NextObjectNumber(model, governor, token);
        GraphCopier pool = GraphCopier.CreateBoundOwned(model.Objects, resolver, governor, limits, first, memory);
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        List<(GraphCopier Copier, int Id)> occurrences = ParserContainers.CreateList<(GraphCopier, int)>(scratch, pages.Count, 16L);
        foreach (PageLocation location in pages)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            PdfReference page = location.Source ?? throw new InvalidOperationException("Missing prepared page identity.");
            occurrences.Add((pool, page.ObjectNumber));
        }
        bool needAppearances = generateAppearances && FlattenAnnotations.NeedsAppearances(
            PdfDictionaryLookup.TryGet(catalog, "AcroForm"u8, governor, token, out QpdfObject role)
                ? CatalogCopy.ResolveDictionary(role, resolver, governor, token) : null, resolver, governor, token);
        OverlayApplication applied = await Overlays.ApplyAsync(host, input, occurrences, underlays, overlays,
            false, null, recovery, limits, governor, diagnostics, memory, token, first, null, null, true,
            generateAppearances, needAppearances).ConfigureAwait(false);
        MergeObjects(pool, applied, governor, token);
        pool.AdvanceNextId(applied.NextId);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfDictionary edited = Apply(catalog, pool, resolver, applied, ref version, memory, graph);
        if (applied.FieldImports.Count > 0)
        {
            int last = pool.NextId - 1;
            CopiedForm? primary = AcroFormCopy.ReadBoundForm(catalog, resolver, model.Objects, ref last, true, memory, governor, token);
            pool.AdvanceNextId(checked(last + 1));
            PdfDictionary? form = AcroFormCopy.BuildSingle(primary, pool, false, occurrences, model.Objects, applied.FieldImports,
                [], limits, governor, diagnostics, memory, token);
            if (form is not null)
            {
                PdfReference reference = pool.AddIndirectObjectWithId(form, pool.NextId);
                edited = PdfDictionaryRebuild.ReplaceEntry(graph, edited, "AcroForm"u8, reference);
            }
        }
        if (!ReferenceEquals(edited, catalog))
        {
            trailer = PdfDictionaryRebuild.ReplaceEntry(graph, trailer, "Root"u8, pool.AddIndirectObjectWithId(edited, pool.NextId));
            graph.Memory.TransferTo(memory, graph.Bytes);
        }
        return (trailer, version, applied, first, pool.NextId);
    }

    internal static void MergeObjects(GraphCopier pool, OverlayApplication applied, ResourceGovernor governor, CancellationToken token)
    {
        foreach (GraphCopier source in applied.Copiers)
        {
            foreach ((int id, QpdfObject value) in source.CopiedObjects)
            {
                token.ThrowIfCancellationRequested(); governor.NoteOperation();
                InspectionGraphPreparation.StoreInspectionObject(pool, id, value);
            }
        }
    }

    internal static PdfDictionary Apply(PdfDictionary catalog, GraphCopier pool, PoolResolver resolver,
        OverlayApplication placements, ref PdfVersion version, ParserMemoryScope memory, OwnedAllocationBatch graph)
    {
        ResourceGovernor governor = graph.Governor;
        CancellationToken token = graph.Token;
        PdfDictionary updated = catalog;
        PdfVersion baseline = version;
        int extension = CatalogCopy.ReadExtensionLevel(catalog, resolver, governor, token);
        IncludeVersionRequirement(ref version, ref extension, placements.Version, placements.ExtensionLevel, governor);
        QpdfObject? optional = OptionalContent.CombinePlacementLayers(catalog, pool, resolver, placements.Copiers, memory, governor, token);
        if (optional is not null)
        {
            IncludeVersionRequirement(ref version, ref extension, new(1, 5), 0, governor);
            updated = PdfDictionaryRebuild.ReplaceEntry(graph, updated, "OCProperties"u8, optional);
        }
        if (version.Major != baseline.Major || version.Minor != baseline.Minor || placements.ExtensionLevel > 0)
        {
            PdfDictionaryLookup.TryGet(updated, "Extensions"u8, governor, token, out QpdfObject role);
            QpdfObject? extensions = PrepareExtensions(role, resolver, version, extension, graph);
            if (!ReferenceEquals(extensions, role))
            {
                updated = extensions is null ? PdfDictionaryRebuild.RemoveEntry(graph, updated, "Extensions"u8)
                    : PdfDictionaryRebuild.ReplaceEntry(graph, updated, "Extensions"u8, extensions);
            }
        }
        return updated;
    }
}
