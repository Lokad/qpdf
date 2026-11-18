using System.Runtime.InteropServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;
using static Lokad.Qpdf.Operations.SplitAssembly;
using static Lokad.Qpdf.Writing.OutputPublication;

namespace Lokad.Qpdf.Operations;

internal static class JsonSplitPublication
{
    private static readonly PdfName ParentKey = PdfName.FromAscii("Parent");

    internal static async ValueTask PublishAsync(IQpdfHost host, QpdfPath outputPath, int groupSize,
        JsonImportWriterSettings writer, PdfVersion version, JsonImport.RenumberedModel graph,
        IReadOnlyList<PageLocation> pages, List<(int Index, PdfDictionary Label)>? authoredLabels,
        HashSet<int>? originalPacking, byte[]? originalId,
        IReadOnlyDictionary<int, (int Number, int Generation)>? originalIds, OutputSecurity? security,
        PublicationInputAliases inputs, QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
    {
        using ParserMemoryScope split = ParserMemoryScope.Create(governor);
        List<string> names = GenerateSplitNames(outputPath.Value, pages.Count, groupSize, split, governor);
        await inputs.ValidateTargetsAsync(host, names, null, "Split", token).ConfigureAwait(false);
        using PreparedSplitPublications preparation = PreparedSplitPublications.Create(governor);
        if (pages.Count != 0)
        {
            Dictionary<int, QpdfObject> pool = graph.Objects;
            PdfDictionary catalog = (PdfDictionary)pool[1];
            PoolResolver resolver = PoolResolver.Create(split, pool);
            List<(int Index, PdfDictionary Label)> labels = authoredLabels
                ?? PageLabelMerge.ReadBoundLabels(resolver, catalog, pages.Count, limits, governor, split, token);
            int maximum = 0;
            foreach (int number in pool.Keys)
            { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); maximum = Math.Max(maximum, number); }
            int pagesNumber = checked(++maximum);
            CopiedForm? form = AcroFormCopy.ReadBoundForm(catalog, resolver, pool, ref maximum, split, governor, token);
            HashSet<int> pageNumbers = ParserContainers.CreateSet<int>(split);
            List<PdfReference> kids = PreparePages(pages, pool, resolver, pagesNumber, ref maximum, pageNumbers,
                limits, split, governor, token);
            form = IsolateWidgets(kids, form, pool, resolver, graph.DisabledSignatureWidgets, maximum,
                limits, split, governor, token);
            if (pool.Count > limits.MaxContainerSize)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), pool.Count, limits.MaxContainerSize); }
            // The per-group catalog and page tree replace their active roles.
            // Their references remap without walking the original full tree.
            pool.Remove(1);
            HashSet<int> compressed = originalPacking
                ?? ObjectStreamPreparation.ResolveGeneratedCompressedSet(pool, 1, pagesNumber, split, governor, token);
            List<(int First, int Last)> groups = PartitionGroups(pages.Count, groupSize, split, governor);
            for (int group = 0; group < groups.Count; group++)
            {
                using ParserMemoryScope groupMemory = ParserMemoryScope.Create(governor);
                (int first, int last) = groups[group];
                SplitPart part = PreparePart(graph, catalog, kids, pageNumbers, pagesNumber, labels, form, first, last,
                    limits, groupMemory, governor, token);
                HashSet<int> packing = ObjectStreamPreparation.RemapCompressedSet(compressed, part.Numbers.Map,
                    groupMemory, governor, token);
                IReadOnlyDictionary<int, (int Number, int Generation)>? ids = MapOriginalIds(originalIds, part.Numbers,
                    groupMemory, governor, token);
                using OutputSecurity? fresh = group == 0 ? null : writer.Encryption.CreateOutput(governor);
                OutputSecurity? groupSecurity = fresh ?? security;
                PreparedOutputModel output = OutputModelPreparation.Finish(part.Objects, packing, groupSecurity,
                    groupMemory, governor, token);
                PreparedSplitPublication publication = await PreparePresentationOutputAsync(version, output.Model,
                    output.ObjectStreams, false, null, part.Info, originalId, writer.StaticId, writer.NewlineBeforeEndstream,
                    writer.Qdf, ids, writer.Linearize, null, QpdfPath.From(names[group]), limits, governor, token,
                    groupSecurity, output.EncryptionObject, part.Metadata).ConfigureAwait(false);
                preparation.Add(publication);
            }
        }

        using PublicationReservation admission = preparation.ReservePublication(governor);
        await preparation.PublishAsync(host, admission, governor, token).ConfigureAwait(false);
    }

    private static List<PdfReference> PreparePages(IReadOnlyList<PageLocation> pages, Dictionary<int, QpdfObject> pool,
        IIndirectResolver resolver, int pagesNumber, ref int maximum, HashSet<int> pageNumbers, QpdfLimits limits,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        List<PdfReference> kids = ParserContainers.CreateList<PdfReference>(memory, pages.Count, IntPtr.Size);
        memory.Reserve(32L);
        PdfReference parent = new(pagesNumber, 0);
        using OwnedAllocationBatch headers = OwnedAllocationBatch.Create(governor, token);
        foreach (PageLocation location in pages)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            int number = location.Source?.ObjectNumber ?? checked(++maximum);
            if (number == 1 || pageNumbers.Contains(number)) { number = checked(++maximum); }
            ParserContainers.AddSetItem(memory, pageNumbers, number, sizeof(int));
            PageInheritance.InheritedValues inherited = PageInheritance.FindAncestors(resolver, location,
                PageAssembly.WriterInherited, limits, governor, token);
            PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(headers, out long allocation);
            using PdfDictionary.EntryEnumerator original = location.Page.GetEntryEnumerator();
            while (original.MoveNext(governor, token))
            { entries.Assign(headers.Memory, governor, original.Current.Key, original.Current.Value, token); }
            entries.Assign(headers.Memory, governor, ParentKey, parent, token);
            for (int i = 0; i < PageAssembly.WriterInherited.Length; i++)
            {
                if (inherited[i] is QpdfObject value)
                { entries.Assign(headers.Memory, governor, PageAssembly.WriterInherited[i], value, token); }
            }
            PdfDictionary page = PdfDictionaryRebuild.Complete(headers, entries, allocation);
            PageAssembly.RequireMediaBox(page);
            GovernedCollections.Assign(memory, governor, pool, number, page, IntPtr.Size);
            memory.Reserve(32L);
            kids.Add(new PdfReference(number, 0));
        }
        headers.Memory.TransferTo(memory, headers.Bytes);
        return kids;
    }

    private static CopiedForm? IsolateWidgets(List<PdfReference> kids, CopiedForm? form, Dictionary<int, QpdfObject> pool,
        IIndirectResolver resolver, HashSet<PdfDictionary>? disabledWidgets, int maximum, QpdfLimits limits,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        if (kids.Count < 2) { return form; }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        int next = checked(maximum + 1);
        GraphCopier copier = GraphCopier.CreateBoundOwned(pool, resolver, governor, limits, next, memory);
        copier.UseDisabledSignatureWidgets(disabledWidgets);
        List<(GraphCopier Copier, int Id)> occurrences =
            ParserContainers.CreateList<(GraphCopier, int)>(scratch, kids.Count, 2L * IntPtr.Size);
        foreach (PdfReference page in kids)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            occurrences.Add((copier, page.ObjectNumber));
        }
        Dictionary<GraphCopier, CopiedForm?> forms = ParserContainers.CreateDictionary<GraphCopier, CopiedForm?>(scratch);
        if (form is not null)
        {
            ParserContainers.EnsureEntryCapacity(scratch, forms, IntPtr.Size);
            forms.Add(copier, form);
        }
        IReadOnlyList<(GraphCopier Copier, CopiedForm Part, int Occurrence)> isolated =
            AnnotationCopies.Isolate(occurrences, forms, true, governor, memory, token, ref next);
        if (isolated.Count == 0) { return form; }

        int count = checked(1 + isolated.Count);
        List<CopiedForm?> registrations = ParserContainers.CreateList<CopiedForm?>(scratch, count, IntPtr.Size);
        List<GraphCopier> owners = ParserContainers.CreateList<GraphCopier>(scratch, count, IntPtr.Size);
        registrations.Add(form);
        owners.Add(copier);
        foreach (var item in isolated)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            registrations.Add(item.Part);
            owners.Add(item.Copier);
        }
        AcroFormCopy.RenameCollidingFields(registrations, owners, limits, governor, token);
        return AcroFormCopy.CombineRegistrations(registrations, governor, memory, token);
    }

    private readonly record struct SplitPart(List<NumberedObject> Objects, GroupNumbers Numbers, int? Info, int Metadata);

    private static SplitPart PreparePart(JsonImport.RenumberedModel graph, PdfDictionary catalog, List<PdfReference> kids,
        HashSet<int> pageNumbers, int pagesNumber, List<(int Index, PdfDictionary Label)> labels, CopiedForm? form,
        int first, int last, QpdfLimits limits, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        ReadOnlySpan<PdfReference> group = CollectionsMarshal.AsSpan(kids).Slice(first - 1, last - first + 1);
        HashSet<int> groupPages = ParserContainers.CreateSet<int>(memory);
        List<QpdfObject> roots = ParserContainers.CreateList<QpdfObject>(memory);
        foreach (PdfReference page in group)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            ParserContainers.AddSetItem(memory, groupPages, page.ObjectNumber, sizeof(int));
            ParserContainers.AddItem(memory, roots, page, IntPtr.Size);
        }
        using OwnedAllocationBatch headers = OwnedAllocationBatch.Create(governor, token);
        headers.Memory.Reserve(32L); headers.Add(32L);
        catalog = PdfDictionaryRebuild.ReplaceEntry(headers, catalog, "Pages"u8, new PdfReference(pagesNumber, 0));
        PdfDictionary? groupLabels = PageLabelAssembly.SlicePageLabels(labels, first, last, governor);
        catalog = WithRole(headers, catalog, "PageLabels"u8, groupLabels);
        PdfDictionary? groupForm = form is null ? null : AcroFormCopy.BuildCatalogForm(form, graph.Objects,
            AcroFormCopy.CollectAnnotRefs(graph.Objects, group, governor, memory, token), true, limits, governor, token);
        catalog = WithRole(headers, catalog, "AcroForm"u8, groupForm);
        ParserContainers.AddItem(memory, roots, catalog, IntPtr.Size);
        if (graph.InfoNumber is int info)
        {
            memory.Reserve(32L);
            ParserContainers.AddItem(memory, roots, new PdfReference(info, 0), IntPtr.Size);
        }

        using ParserMemoryScope walk = ParserMemoryScope.Create(governor);
        memory.Reserve(40L);
        SplitResolver resolver = new(graph.Objects, pageNumbers, groupPages);
        HashSet<int> reachable = GraphReachability.Find(resolver, ParserContainers.CopyArray(memory, roots), walk, governor);
        GroupNumbers numbers = NumberGroup(graph.Objects, reachable, 1, pagesNumber, memory, governor, token);
        List<NumberedObject> objects = ParserContainers.CreateList<NumberedObject>(memory);
        ParserContainers.AddItem(memory, objects,
            new NumberedObject(1, ObjectRemap.RemapSplit(catalog, numbers.Map, pageNumbers, memory, governor, token)), 16L);
        List<PdfReference> mappedKids = MapKids(group, numbers.Map, memory, governor, token);
        ParserContainers.AddItem(memory, objects,
            new NumberedObject(2, OutputCatalogPreparation.PagesNode(mappedKids, memory, governor, token)), 16L);
        AppendGroupObjects(objects, graph.Objects, numbers, pageNumbers, memory, governor, token);
        headers.Memory.TransferTo(memory, headers.Bytes);
        int? mappedInfo = graph.InfoNumber is int originalInfo && numbers.Map.TryGetValue(originalInfo, out int infoNumber)
            ? infoNumber : null;
        return new(objects, numbers, mappedInfo, numbers.Map.GetValueOrDefault(graph.MetadataNumber));
    }

    private static PdfDictionary WithRole(OwnedAllocationBatch headers, PdfDictionary catalog, ReadOnlySpan<byte> key,
        PdfDictionary? value)
    {
        if (value is not null) { return PdfDictionaryRebuild.ReplaceEntry(headers, catalog, key, value); }
        return PdfDictionaryLookup.TryGet(catalog, key, headers.Governor, headers.Token, out _)
            ? PdfDictionaryRebuild.RemoveEntry(headers, catalog, key) : catalog;
    }
}
