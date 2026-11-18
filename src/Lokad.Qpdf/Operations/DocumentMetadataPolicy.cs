using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Operations;

internal readonly record struct DocumentMetadataPolicy(bool RemoveInfo, bool RemoveMetadata)
{
    internal bool KeepXmlMetadata => !RemoveInfo && !RemoveMetadata;

    internal PdfDictionary SelectCatalog(PdfDictionary catalog, OwnedAllocationBatch graph)
        => !KeepXmlMetadata && PdfDictionaryLookup.TryGet(catalog, "Metadata"u8, graph.Governor, graph.Token, out _)
            ? PdfDictionaryRebuild.RemoveEntry(graph, catalog, "Metadata"u8) : catalog;

    internal QpdfObject? SelectInfo(PdfDictionary trailer, IIndirectResolver resolver, OwnedAllocationBatch graph)
    {
        if (!PdfDictionaryLookup.TryGet(trailer, "Info"u8, graph.Governor, graph.Token, out QpdfObject raw)
            || raw is PdfNull) { return null; }
        if (!RemoveInfo) { return raw; }
        if (Resolve(raw, resolver, graph) is not PdfDictionary info
            || !PdfDictionaryLookup.TryGet(info, "ModDate"u8, graph.Governor, graph.Token, out QpdfObject date)
            || Resolve(date, resolver, graph) is null or PdfNull) { return null; }

        // Presence uses the resolved value; emission keeps the live source edge.
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        entries.Assign(graph.Memory, graph.Governor, PdfDictionaryRebuild.CreateName(graph, "ModDate"u8), date, graph.Token);
        return PdfDictionaryRebuild.Complete(graph, entries, allocation);
    }

    private static QpdfObject? Resolve(QpdfObject raw, IIndirectResolver resolver, OwnedAllocationBatch graph)
        => raw is PdfReference reference
            ? PdfObjectLookup.TryResolve(reference, resolver, graph.Governor, graph.Token, out QpdfObject? value) ? value : null
            : raw;
}
