using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;
using static Lokad.Qpdf.Writing.OutputCatalogPreparation;
using static Lokad.Qpdf.Writing.OutputVersionPolicy;

namespace Lokad.Qpdf.Operations;

internal static class PlacementCatalog
{
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
