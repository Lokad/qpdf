using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Operations;

internal static partial class InspectionGraphPreparation
{
    private static void FlattenInspectionAnnotations(QpdfDocument document, GraphCopier pool,
        IReadOnlyList<InspectionPage> pages, string mode, InspectionGraph lifetime, ParserMemoryScope memory,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken token, DiagnosticBag diagnostics)
    {
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        List<(GraphCopier Copier, int Id)> occurrences = ParserContainers.CreateList<(GraphCopier, int)>(scratch, pages.Count, 16L);
        List<PageLocation> annotated = ParserContainers.CreateList<PageLocation>(scratch);
        PdfName annots = PdfName.FromAscii("Annots");
        foreach (InspectionPage page in pages)
        {
            governor.NoteOperation();
            ParserContainers.AddItem(scratch, occurrences, (pool, page.Number), 16L);
            if (pool.CopiedObjects[page.Number] is PdfDictionary current && current.TryGet(annots, out _))
            { ParserContainers.AddItem(scratch, annotated, new PageLocation(null, current), 16L); }
        }

        bool needAppearances = FlattenAnnotations.NeedsAppearances(document.Engine, governor, token);
        (int nextId, bool removedWidgets) = FlattenAnnotations.Apply(occurrences, mode, needAppearances,
            false, null, limits, governor, diagnostics, token, pool.NextId);
        pool.AdvanceNextId(nextId);
        if (!removedWidgets) { return; }

        CopiedForm? form = AcroFormCopy.TryCopySourceForm(document.Engine, pool, annotated,
            false, limits, governor, scratch, token, true);
        if (form is null) { return; }
        foreach (InspectionPage page in pages)
        {
            governor.NoteOperation();
            if (pool.CopiedObjects[page.Number] is PdfDictionary current && current.TryGet(annots, out QpdfObject annotations))
            { pool.RetainSourceValue(annotations); }
        }
        HashSet<int> surviving = AcroFormCopy.CollectAnnotRefs(occurrences, governor, scratch, token);
        PdfDictionary? updatedForm = AcroFormCopy.BuildCatalogForm(form, pool.CopiedObjects, surviving, false, limits, governor, token);

        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfDictionary trailer = document.Engine.Trailer;
        if (!PdfDictionaryLookup.TryGet(trailer, "Root"u8, governor, token, out QpdfObject root)
            || CatalogCopy.ResolveDictionary(root, document.Engine, governor, token) is not PdfDictionary catalog)
        { throw new QpdfParseException("Trailer has no catalog root.", null, null); }
        PdfDictionary updated = updatedForm is null ? PdfDictionaryRebuild.RemoveEntry(graph, catalog, "AcroForm"u8)
            : PdfDictionaryRebuild.ReplaceEntry(graph, catalog, "AcroForm"u8,
                pool.AddIndirectObjectWithId(updatedForm, pool.NextId));
        document.Engine.SetInspectionTrailer(ReplaceInspectionCatalogRoot(pool, graph, trailer, root, updated, lifetime));
        graph.Memory.TransferTo(memory, graph.Bytes);
    }
}
