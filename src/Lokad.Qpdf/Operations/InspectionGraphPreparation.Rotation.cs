using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Operations;

internal static partial class InspectionGraphPreparation
{
    private static readonly PdfName[] RotationInputs =
    [
        PdfName.FromAscii("Rotate"), PdfName.FromAscii("MediaBox"), PdfName.FromAscii("CropBox"), PdfName.FromAscii("BleedBox"),
        PdfName.FromAscii("TrimBox"), PdfName.FromAscii("ArtBox"), PdfName.FromAscii("Contents"), PdfName.FromAscii("Annots"),
        PdfName.FromAscii("VP"),
    ];

    private static IReadOnlyDictionary<int, double[]>? ApplyInspectionRotation(QpdfDocument document, GraphCopier pool,
        IReadOnlyList<InspectionPage> pages, IInspectionGraphPlan plan, InspectionGraph lifetime, ParserMemoryScope memory,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        List<(GraphCopier Copier, int Id)> occurrences = ParserContainers.CreateList<(GraphCopier, int)>(scratch, pages.Count, 16L);
        List<PageLocation> annotated = ParserContainers.CreateList<PageLocation>(scratch);
        foreach (InspectionPage occurrence in pages)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            occurrences.Add((pool, occurrence.Number));
            PdfDictionary page = (PdfDictionary)pool.CopiedObjects[occurrence.Number];
            // The rotation owner visits unrotated annotations as well to find
            // objects shared with a rotated occurrence.
            foreach (PdfName key in RotationInputs)
            {
                governor.NoteOperation();
                if (page.TryGet(key, out QpdfObject value) && value is PdfReference or PdfArray or PdfDictionary or PdfStream)
                { pool.RetainSourceValue(value); }
            }
            if (PdfDictionaryLookup.TryGet(page, "Annots"u8, governor, token, out _))
            { ParserContainers.AddItem(scratch, annotated, new PageLocation(null, page), 16L); }
        }

        CopiedForm? source = annotated.Count == 0 ? null : AcroFormCopy.TryCopySourceForm(document.Engine, pool,
            annotated, false, limits, governor, scratch, token, true);
        Dictionary<GraphCopier, CopiedForm?> forms = ParserContainers.CreateDictionary<GraphCopier, CopiedForm?>(scratch);
        ParserContainers.EnsureEntryCapacity(scratch, forms, IntPtr.Size); forms.Add(pool, source);
        (int nextId, var fieldCopies, IReadOnlyDictionary<int, double[]>? frames) = FlattenRotation.Apply(occurrences,
            forms, false, null, limits, governor, diagnostics, token, pool.NextId, memory);
        pool.AdvanceNextId(nextId);
        if (frames is null) { return null; }

        if (fieldCopies.Count != 0)
        {
            PdfDictionary? form = BuildInspectionForm(pool, source, occurrences, Array.Empty<AcroFormCopy.FieldImport>(),
                fieldCopies, plan.Sources.Count > 0, memory, limits, governor, diagnostics, token);
            if (form is not null)
            {
                ReplaceInspectionCatalogEntry(document, pool, PdfName.FromAscii("AcroForm"),
                    pool.AddIndirectObjectWithId(form, pool.NextId), memory, limits, governor);
            }
        }
        PdfDictionary trailer = document.Engine.Trailer;
        if (!PdfDictionaryLookup.TryGet(trailer, "Root"u8, governor, token, out QpdfObject root)
            || CatalogCopy.ResolveDictionary(root, document.Engine, governor, token) is not PdfDictionary catalog)
        { return frames; }
        List<PdfReference> references = ParserContainers.CreateList<PdfReference>(scratch, pages.Count, IntPtr.Size);
        foreach (InspectionPage page in pages)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation(); memory.Reserve(32L);
            references.Add(new PdfReference(page.Number, page.Generation));
        }
        PdfDictionary updated = SourceActions.BindCatalogRotation(pool, references, catalog, frames, governor, memory, token);
        if (!ReferenceEquals(updated, catalog))
        {
            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
            document.Engine.SetInspectionTrailer(ReplaceInspectionCatalogRoot(pool, graph, trailer, root, updated, lifetime));
            graph.Memory.TransferTo(memory, graph.Bytes);
        }
        return frames;
    }
}
