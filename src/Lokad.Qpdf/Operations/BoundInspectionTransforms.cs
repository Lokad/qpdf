using System.Runtime.InteropServices;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Operations;

internal static class BoundInspectionTransforms
{
    internal static (PdfDictionary Trailer, int First, int Next) Apply(ImportedModel model, PoolResolver resolver,
        PdfDictionary trailer, IReadOnlyList<PageLocation> pages, Dictionary<int, PdfReference>? articleAliases, JsonPlan plan, QpdfLimits limits,
        ParserMemoryScope memory, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        if (!PdfDictionaryLookup.TryGet(trailer, "Root"u8, governor, token, out QpdfObject root)
            || CatalogCopy.ResolveDictionary(root, resolver, governor, token) is not PdfDictionary catalog)
        { throw new QpdfParseException("Imported trailer /Root must resolve to a dictionary.", null, null); }
        PdfDictionary? form = PdfDictionaryLookup.TryGet(catalog, "AcroForm"u8, governor, token, out QpdfObject formRole)
            ? CatalogCopy.ResolveDictionary(formRole, resolver, governor, token) : null;
        bool needAppearances = FlattenAnnotations.NeedsAppearances(form, resolver, governor, token);
        bool generate = plan.GenerateAppearances && needAppearances;
        if (!generate && plan.FlattenAnnotations is null && !plan.FlattenRotation) { return (trailer, 0, 0); }

        int first = ImportedPublication.NextObjectNumber(model, governor, token);
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateBoundOwned(model.Objects, resolver, governor, limits, first, memory);
        List<(GraphCopier Copier, int Id)> occurrences = ParserContainers.CreateList<(GraphCopier, int)>(scratch, pages.Count, 16L);
        List<PdfReference> references = ParserContainers.CreateList<PdfReference>(scratch, pages.Count, IntPtr.Size);
        bool annotated = false;
        foreach (PageLocation location in pages)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            PdfReference page = location.Source ?? throw new InvalidOperationException("Missing prepared page identity.");
            references.Add(page);
            occurrences.Add((copier, page.ObjectNumber));
            if (model.Objects[page.ObjectNumber] is PdfDictionary current
                && PdfDictionaryLookup.TryGet(current, "Annots"u8, governor, token, out _)) { annotated = true; }
        }
        int last = first - 1;
        CopiedForm? part = form is not null && (generate || annotated)
            ? AcroFormCopy.ReadBoundForm(catalog, resolver, model.Objects, ref last, true, memory, governor, token) : null;
        copier.AdvanceNextId(checked(last + 1));
        Dictionary<GraphCopier, CopiedForm?> forms = ParserContainers.CreateDictionary<GraphCopier, CopiedForm?>(scratch);
        ParserContainers.EnsureEntryCapacity(scratch, forms, IntPtr.Size);
        forms.Add(copier, part);

        PdfDictionary? updatedForm = form;
        if (generate)
        {
            HashSet<int> survivors = AcroFormCopy.CollectAnnotRefs(occurrences, governor, scratch, token);
            scratch.Reserve(48L);
            HashSet<int>? reached = PageAssembly.CollectGenerateReached([(model.Objects, part)], survivors,
                false, limits, governor, scratch, token);
            (int next, bool complete) = GenerateAppearances.Apply(occurrences, forms, limits, governor, diagnostics,
                token, reached, true, null, copier.NextId);
            copier.AdvanceNextId(next);
            needAppearances = !complete;
        }
        if (plan.FlattenAnnotations is string mode)
        {
            (int next, bool removedWidgets) = FlattenAnnotations.Apply(occurrences, mode, needAppearances,
                false, null, limits, governor, diagnostics, token, copier.NextId);
            copier.AdvanceNextId(next);
            if (removedWidgets && part is not null)
            {
                HashSet<int> survivors = AcroFormCopy.CollectAnnotRefs(occurrences, governor, scratch, token);
                updatedForm = AcroFormCopy.BuildCatalogForm(part, model.Objects, survivors, false, limits, governor, token);
                updatedForm = SourceActions.ProjectOutputForm(updatedForm, model.Objects, copier, CollectionsMarshal.AsSpan(references),
                    survivors, null, limits, governor, memory, token);
                // Field projection can introduce new identities outside the copier.
                copier.AdvanceNextId(ImportedPublication.NextObjectNumber(model, governor, token));
            }
        }

        IReadOnlyDictionary<int, double[]>? frames = null;
        if (plan.FlattenRotation)
        {
            (int next, var fieldCopies, var transforms) = FlattenRotation.Apply(occurrences, forms, false, null,
                limits, governor, diagnostics, token, copier.NextId, memory);
            frames = transforms;
            copier.AdvanceNextId(next);
            if (fieldCopies.Count != 0)
            {
                updatedForm = AcroFormCopy.BuildSingle(part, copier, false, occurrences, model.Objects, [], fieldCopies,
                    limits, governor, diagnostics, memory, token);
            }
        }

        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        if (generate && !needAppearances) { updatedForm = PageAssembly.ClearNeedAppearances(updatedForm, governor, token); }
        PdfDictionary edited = catalog;
        if (!ReferenceEquals(updatedForm, form))
        {
            edited = updatedForm is null ? PdfDictionaryRebuild.RemoveEntry(graph, catalog, "AcroForm"u8)
                : PdfDictionaryRebuild.ReplaceEntry(graph, catalog, "AcroForm"u8, copier.AddIndirectObjectWithId(updatedForm, copier.NextId));
        }
        if (frames is { Count: > 0 })
        {
            edited = SourceActions.BindCatalogRotation(copier, references, edited, frames, governor, memory, token);
            edited = BindArticles(copier, resolver, edited, CollectionsMarshal.AsSpan(references), articleAliases, frames, limits, memory, governor, token);
        }
        if (!ReferenceEquals(edited, catalog))
        {
            PdfReference replacement = copier.AddIndirectObjectWithId(edited, copier.NextId);
            trailer = PdfDictionaryRebuild.ReplaceEntry(graph, trailer, "Root"u8, replacement);
        }
        graph.Memory.TransferTo(memory, graph.Bytes);
        return (trailer, first, copier.NextId);
    }

    private static PdfDictionary BindArticles(GraphCopier copier, PoolResolver resolver, PdfDictionary catalog,
        ReadOnlySpan<PdfReference> pages, IReadOnlyDictionary<int, PdfReference>? articleAliases, IReadOnlyDictionary<int, double[]> frames, QpdfLimits limits,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        bool hasThreads = PdfDictionaryLookup.TryGet(catalog, "Threads"u8, governor, token, out QpdfObject role);
        if (!hasThreads && !ArticleThreads.HasRegistrations(copier.CopiedObjects, pages, governor, token)) { return catalog; }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        ArticleThreads? articles = hasThreads ? ArticleThreads.Read(role, resolver, limits, scratch, governor, token) : null;
        (PdfDictionary edited, Dictionary<int, QpdfObject>? changes) = ArticleThreads.Project(articles, catalog, resolver,
            pages, articleAliases, frames, null, copier.NextId - 1, limits, memory, governor, token);
        if (changes is not null)
        {
            foreach ((int id, QpdfObject value) in changes)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                InspectionGraphPreparation.StoreInspectionObject(copier, id, value);
            }
        }
        return edited;
    }
}
