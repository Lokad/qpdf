using Lokad.Qpdf.Content;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;
using System.Runtime.InteropServices;

namespace Lokad.Qpdf.Operations;

internal static partial class InspectionGraphPreparation
{
    private static void ApplyInspectionAppearances(QpdfDocument document, GraphCopier pool,
        IReadOnlyList<InspectionPage> pages, IInspectionGraphPlan plan, InspectionGraph lifetime, ParserMemoryScope memory,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken token, DiagnosticBag diagnostics, OverlayApplication? placements)
    {
        bool needAppearances = FlattenAnnotations.NeedsAppearances(document.Engine, governor, token);
        if (!needAppearances && plan.FlattenAnnotations is null) { return; }
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

        CopiedForm? form = null;
        if (plan.GenerateAppearances && needAppearances)
        {
            foreach (PageLocation page in annotated)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (page.Page.TryGet(annots, out QpdfObject annotations)) { pool.RetainSourceValue(annotations); }
            }
            bool selected = plan.Sources.Count > 0;
            form = AcroFormCopy.TryCopySourceForm(document.Engine, pool, annotated, false, limits, governor, scratch, token, true);
            Dictionary<GraphCopier, CopiedForm?> forms = ParserContainers.CreateDictionary<GraphCopier, CopiedForm?>(scratch);
            ParserContainers.EnsureEntryCapacity(scratch, forms, IntPtr.Size);
            forms.Add(pool, form);
            HashSet<int> survivors = AcroFormCopy.CollectAnnotRefs(occurrences, governor, scratch, token);
            scratch.Reserve(48L);
            HashSet<int>? reached = PageAssembly.CollectGenerateReached([(pool.CopiedObjects, form)], survivors,
                selected, limits, governor, scratch, token);
            (int allocator, bool generated) = GenerateAppearances.Apply(occurrences, forms, limits, governor, diagnostics,
                token, reached, !selected, placements?.GeneratedWidgets, pool.NextId);
            pool.AdvanceNextId(allocator);
            if (generated && (placements is null || placements.GenerationComplete))
            {
                ClearInspectionAppearanceFlag(document, pool, lifetime, memory, governor, token);
                needAppearances = false;
            }
        }

        if (plan.FlattenAnnotations is not string mode) { return; }
        (int nextId, bool removedWidgets) = FlattenAnnotations.Apply(occurrences, mode, needAppearances,
            false, null, limits, governor, diagnostics, token, pool.NextId);
        pool.AdvanceNextId(nextId);
        if (!removedWidgets) { return; }

        form ??= AcroFormCopy.TryCopySourceForm(document.Engine, pool, annotated,
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
        if (updatedForm is not null)
        {
            List<PdfReference> pageRefs = ParserContainers.CreateList<PdfReference>(scratch, pages.Count, IntPtr.Size);
            foreach (InspectionPage page in pages)
            {
                governor.NoteOperation(); scratch.Reserve(32L);
                pageRefs.Add(new PdfReference(page.Number, page.Generation));
            }
            updatedForm = ProjectInspectionForm(updatedForm, pool, CollectionsMarshal.AsSpan(pageRefs),
                surviving, limits, governor, memory, token);
        }

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

    private static void ClearInspectionAppearanceFlag(QpdfDocument document, GraphCopier pool,
        InspectionGraph lifetime, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        PdfDictionary trailer = document.Engine.Trailer;
        if (!PdfDictionaryLookup.TryGet(trailer, "Root"u8, governor, token, out QpdfObject root)
            || CatalogCopy.ResolveDictionary(root, document.Engine, governor, token) is not PdfDictionary catalog
            || !PdfDictionaryLookup.TryGet(catalog, "AcroForm"u8, governor, token, out QpdfObject rawForm)
            || CatalogCopy.ResolveDictionary(rawForm, document.Engine, governor, token) is not PdfDictionary form) { return; }
        PdfDictionary? updatedForm = PageAssembly.ClearNeedAppearances(form, governor, token);
        if (updatedForm is null || ReferenceEquals(updatedForm, form)) { return; }
        if (rawForm is PdfReference reference)
        { StoreInspectionObject(pool, reference.ObjectNumber, updatedForm); return; }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfDictionary updated = PdfDictionaryRebuild.ReplaceEntry(graph, catalog, "AcroForm"u8, updatedForm);
        document.Engine.SetInspectionTrailer(ReplaceInspectionCatalogRoot(pool, graph, trailer, root, updated, lifetime));
        graph.Memory.TransferTo(memory, graph.Bytes);
    }

    private static PdfDictionary? ProjectSelectedInspectionForm(PdfDictionary form, GraphCopier pool,
        DocumentEngine engine, IReadOnlyList<(GraphCopier Copier, int Id)> occurrences, QpdfLimits limits,
        ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        List<PdfReference> pages = ParserContainers.CreateList<PdfReference>(scratch, occurrences.Count, IntPtr.Size);
        foreach ((_, int number) in occurrences)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation(); scratch.Reserve(32L);
            pages.Add(new PdfReference(number, engine.InspectionObjectGeneration(number)));
        }
        ReadOnlySpan<PdfReference> pageRefs = CollectionsMarshal.AsSpan(pages);
        HashSet<int> survivors = AcroFormCopy.CollectAnnotRefs(pool.CopiedObjects, pageRefs, governor, scratch, token);
        return ProjectInspectionForm(form, pool, pageRefs, survivors, limits, governor, memory, token);
    }

    private static PdfDictionary? ProjectInspectionForm(PdfDictionary form, GraphCopier pool,
        ReadOnlySpan<PdfReference> pages, HashSet<int> survivors,
        QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        int? maximum = null;
        var (projected, objects) = SourceActions.ProjectForm(form, pool.CopiedObjects, pool.CreateResolver(scratch),
            pages, survivors, ref maximum, pool.NextId - 1, limits, governor, memory, token);
        if (objects is null) { return projected; }
        foreach ((int number, QpdfObject value) in objects)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            StoreInspectionObject(pool, number, value);
        }
        return projected;
    }
}
