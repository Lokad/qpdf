using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Writing;

internal readonly record struct DocumentPublicationPolicy(
    bool RemoveInfo, bool RemoveMetadata, bool RemoveAcroform, bool RemoveStructure, bool RemoveLabels)
{
    internal DocumentPublicationPolicy(bool removeInfo, bool removeMetadata)
        : this(removeInfo, removeMetadata, false, false, false) { }

    internal bool KeepXmlMetadata => !RemoveInfo && !RemoveMetadata;
    internal bool HasRemovals => RemoveInfo || RemoveMetadata || RemoveAcroform || RemoveStructure || RemoveLabels;

    internal PdfDictionary SelectCatalog(PdfDictionary catalog, OwnedAllocationBatch graph)
    {
        bool changes = false;
        using (PdfDictionary.EntryEnumerator probe = catalog.GetEntryEnumerator())
        {
            while (probe.MoveNext(graph.Governor, graph.Token))
            {
                if (Removes(probe.Current.Key, graph)) { changes = true; break; }
            }
        }
        if (!changes) { return catalog; }
        // Filter all requested roles into one owned header. The preliminary scan
        // avoids constructing another table when none of those roles is present.
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        using PdfDictionary.EntryEnumerator source = catalog.GetEntryEnumerator();
        while (source.MoveNext(graph.Governor, graph.Token))
        {
            (PdfName key, QpdfObject value) = source.Current;
            if (value is null || Removes(key, graph)) { continue; }
            entries.Assign(graph.Memory, graph.Governor, key, value, graph.Token);
        }
        return PdfDictionaryRebuild.Complete(graph, entries, allocation);
    }

    private bool Removes(PdfName key, OwnedAllocationBatch graph)
        => !KeepXmlMetadata && PdfDictionaryLookup.NameEquals(key, "Metadata"u8, graph.Governor, graph.Token)
            || RemoveAcroform && PdfDictionaryLookup.NameEquals(key, "AcroForm"u8, graph.Governor, graph.Token)
            || RemoveStructure && (PdfDictionaryLookup.NameEquals(key, "StructTreeRoot"u8, graph.Governor, graph.Token)
                || PdfDictionaryLookup.NameEquals(key, "MarkInfo"u8, graph.Governor, graph.Token))
            || RemoveLabels && PdfDictionaryLookup.NameEquals(key, "PageLabels"u8, graph.Governor, graph.Token);

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
