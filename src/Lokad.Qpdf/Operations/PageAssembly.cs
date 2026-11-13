using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;
using static Lokad.Qpdf.Runtime.ExecutionFailures;
using static Lokad.Qpdf.Writing.OutputCatalogPreparation;

namespace Lokad.Qpdf.Operations;

internal static class PageAssembly
{
    internal static HashSet<int>? CollectGenerateReached(IReadOnlyList<(IReadOnlyDictionary<int, QpdfObject> Pool, Content.CopiedForm? Part)> parts, HashSet<int> survivors, bool pruneUnreachableFields, QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope memory, CancellationToken cancellationToken)
    {
        if (parts is null)
        {
            throw new ArgumentNullException(nameof(parts));
        }

        if (survivors is null)
        {
            throw new ArgumentNullException(nameof(survivors));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        limits.Validate();
        bool anyFields = false;
        HashSet<int> reached = ParserContainers.CreateSet<int>(memory);
        foreach ((IReadOnlyDictionary<int, QpdfObject> pool, Content.CopiedForm? part) in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (part is null || !part.HasFields || pool is null)
            {
                continue;
            }

            using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
            (List<PdfReference> kept, HashSet<int> closure) = Content.AcroFormCopy.CollectKeptClosure(pool, part.Fields, survivors, pruneUnreachableFields, limits, governor, scratch, cancellationToken);
            if (kept.Count == 0)
            {
                continue;
            }

            anyFields = true;
            foreach (int id in closure)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddSetItem(memory, reached, id, sizeof(int));
            }
        }

        return anyFields ? reached : null;
    }

    internal static PdfDictionary? ClearNeedAppearances(PdfDictionary? form, ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(governor);
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (form is null) { return null; }
        if (!PdfDictionaryLookup.TryGet(form, "NeedAppearances"u8, governor, cancellationToken, out QpdfObject current)
            || current is PdfNull)
        {
            return form;
        }

        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellationToken);
        PdfDictionary result = PdfDictionaryRebuild.RemoveEntry(graph, form, "NeedAppearances"u8);
        graph.Retain(); return result;
    }

    internal static Content.ContentTransformRequest? PrepareSourceContentTransforms(
        bool coalesceContents,
        bool normalizeContent,
        bool externalizeInlineImages,
        long iiMinBytes,
        bool compress,
        int? level,
        byte filterOptions,
        Content.ContentPrunePolicy prune,
        DocumentEngine engine,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken,
        out bool pruneHere)
    {
        pruneHere = prune.Enabled && (prune.Mode == "yes"
            || (prune.Mode is null or "auto" && Content.ContentTransforms.SharedResourcesHeuristic(engine, limits, governor, cancellationToken)));
        if (!coalesceContents && !normalizeContent && !externalizeInlineImages)
        {
            return null;
        }

        return new Content.ContentTransformRequest(coalesceContents, normalizeContent, compress, level, externalizeInlineImages, iiMinBytes)
        { FilterOptions = filterOptions };
    }

    internal static void FinishContentTransforms(
        Dictionary<int, QpdfObject> pool, IReadOnlyList<(GraphCopier Copier, int Id)> occurrences,
        Content.ContentTransformRequest? request, HashSet<GraphCopier>? pruneSources,
        HashSet<int>? processedForms, Content.ImageOptimizeRequest images, QpdfLimits limits,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        if (occurrences.Count == 0 || request is not { Externalize: true } && pruneSources is null && !images.Optimize) { return; }
        GraphCopier copier = occurrences[0].Copier;
        copier.UseOutputPool(pool, token);
        using ParserMemoryScope phase = ParserMemoryScope.Create(governor);
        List<int> pageIds = ParserContainers.CreateList<int>(phase, occurrences.Count, sizeof(int));
        bool allPruned = true, anyPruned = false;
        foreach ((GraphCopier source, int id) in occurrences)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            pageIds.Add(id);
            bool prune = pruneSources is not null && pruneSources.Contains(source);
            allPruned &= prune; anyPruned |= prune;
        }
        if (request is { Externalize: true })
        { Content.ContentTransforms.ExternalizeForms(copier, pageIds, request, processedForms, limits, governor, diagnostics, token); }
        if (anyPruned)
        {
            IReadOnlyList<int> pruneIds = pageIds;
            if (!allPruned)
            {
                List<int> selected = ParserContainers.CreateList<int>(phase);
                foreach ((GraphCopier source, int id) in occurrences)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    if (pruneSources is not null && pruneSources.Contains(source))
                    { ParserContainers.AddItem(phase, selected, id, sizeof(int)); }
                }
                pruneIds = selected;
            }
            Content.ContentTransforms.ApplyResourcePruning(copier, pruneIds, limits, governor, diagnostics, token);
        }
        Content.ImageOptimizer.Apply(copier, pageIds, images, limits, governor, diagnostics, token);
    }

    internal static List<List<(PdfRotationMode Mode, int Angle)>?> ResolveRotationFolds(
        IReadOnlyList<OutputRotation> rotations, int total, ResourceGovernor governor,
        QpdfLimits limits, ParserMemoryScope ownership)
    {
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        ownership.CheckOwner(governor);
        List<List<(PdfRotationMode Mode, int Angle)>?> folds = ParserContainers.CreateList<List<(PdfRotationMode Mode, int Angle)>?>(ownership);
        for (int i = 0; i < total; i++)
        {
            governor.CheckCancellation();
            ParserContainers.AddItem(ownership, folds, null, IntPtr.Size);
        }

        foreach (OutputRotation specification in rotations)
        {
            governor.CheckCancellation();
            IReadOnlyList<int> positions = specification.ResolvePositions(total, limits.MaxContainerSize, governor, ownership);
            foreach (int position in positions)
            {
                governor.CheckCancellation();
                List<(PdfRotationMode Mode, int Angle)> page = folds[position - 1] ??= ParserContainers.CreateList<(PdfRotationMode, int)>(ownership);
                ParserContainers.AddItem(ownership, page, (specification.Mode, specification.Angle), 8L);
            }
        }

        return folds;
    }

    internal static void ApplyRotations(
        List<(GraphCopier Copier, int Id)> occurrences,
        List<List<(PdfRotationMode Mode, int Angle)>?> folds,
        ResourceGovernor governor, ParserMemoryScope ownership)
    {
        ownership.CheckOwner(governor);
        for (int i = 0; i < occurrences.Count; i++)
        {
            governor.CheckCancellation();
            if (folds[i] is not { Count: > 0 } specifications) { continue; }
            (GraphCopier copier, int id) = occurrences[i];
            if (!copier.CopiedObjects.TryGetValue(id, out QpdfObject? value) || value is not PdfDictionary page)
            {
                throw new QpdfException("Rotation reached a missing copied page dictionary.");
            }
            ApplyRotation(copier, id, page, specifications, i + 1, governor, ownership);
        }
    }

    internal static void ApplyRotation(GraphCopier copier, int id, PdfDictionary page,
        IReadOnlyList<(PdfRotationMode Mode, int Angle)> specifications, int pageNumber,
        ResourceGovernor governor, ParserMemoryScope ownership)
    {
        using ParserMemoryScope header = ParserMemoryScope.Create(governor);
        header.Reserve(176L);
        PdfName rotateKey = PdfName.FromAscii("Rotate");
        int current = 0;
        if (page.TryGet(rotateKey, out QpdfObject raw))
        {
            if (raw is PdfReference reference && PdfObjectLookup.TryResolve(reference, copier.CopiedObjects,
                governor, governor.CancellationToken, out QpdfObject? resolved))
            { raw = resolved ?? raw; }
            if (raw is PdfNull)
            {
                current = 0;
            }
            else if (raw is PdfInteger inherited)
            {
                current = NormalizeRotation(inherited.Value);
            }
            else
            {
                throw new QpdfParseException($"Output page {pageNumber} has an invalid /Rotate value.", null, null);
            }
        }

        foreach ((PdfRotationMode mode, int angle) in specifications)
        {
            governor.CheckCancellation();
            current = mode switch
            {
                PdfRotationMode.Set => angle,
                PdfRotationMode.Add => NormalizeRotation((long)current + angle),
                PdfRotationMode.Subtract => NormalizeRotation((long)current - angle),
                _ => throw new QpdfException($"Unknown rotation mode {(int)mode}."),
            };
        }

        Dictionary<PdfName, QpdfObject> entries = ParserContainers.CreateDictionary<PdfName, QpdfObject>(header);
        using PdfDictionary.EntryEnumerator source = page.GetEntryEnumerator();
        while (source.MoveNext(governor, governor.CancellationToken))
        {
            ParserContainers.EnsureEntryCapacity(header, entries, IntPtr.Size);
            entries[source.Current.Key] = source.Current.Value;
        }
        if (!entries.ContainsKey(rotateKey)) { ParserContainers.EnsureEntryCapacity(header, entries, IntPtr.Size); }
        entries[rotateKey] = new PdfInteger(current.ToString(System.Globalization.CultureInfo.InvariantCulture), current);
        PdfDictionary rotated = PdfDictionary.FromOwnedEntries(entries);
        header.TransferTo(ownership); copier.ReplaceCopiedObject(id, rotated);
    }

    internal static int NormalizeRotation(long value)
    {
        return (int)(((value % 360L) + 360L) % 360L);
    }

    // Groups keep their output order/mapping; one logical document supplies the
    // complete selected-root set to its reader and copier before any descent.

    internal static Dictionary<string, List<int>> GroupPageSources(IReadOnlyList<PdfPageSource> sources,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        memory.CheckOwner(governor);
        Dictionary<string, List<int>> grouped = ParserContainers.CreateDictionary<string, List<int>>(memory);
        for (int index = 0; index < sources.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            string path = sources[index].Input.Value;
            if (!grouped.TryGetValue(path, out List<int>? groups))
            {
                groups = ParserContainers.CreateList<int>(memory);
                ParserContainers.EnsureEntryCapacity(memory, grouped, IntPtr.Size);
                grouped.Add(path, groups);
            }
            ParserContainers.AddItem(memory, groups, index, sizeof(int));
        }
        return grouped;
    }

    internal static List<(int Source, int Slot)> CollateOrder(
        IReadOnlyList<List<(GraphCopier Copier, int Id, int SourcePage)>> perSource,
        PdfPageCollation? collation, ResourceGovernor governor, ParserMemoryScope memory)
    {
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        List<int> counts = ParserContainers.CreateList<int>(scratch);
        foreach (var source in perSource)
        {
            governor.CheckCancellation();
            ParserContainers.AddItem(scratch, counts, source.Count, sizeof(int));
        }
        return CollatePageOrder(counts, collation, governor, memory);
    }

    internal static PdfDictionary? TryGetInputCatalog(DocumentEngine engine)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (!engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject? root) || root is null || root is PdfNull)
        {
            return null;
        }

        if (root is PdfReference rootReference)
        {
            if (((IIndirectResolver)engine).TryResolve(rootReference, out QpdfObject? resolved) && resolved is PdfDictionary catalog)
            {
                return catalog;
            }

            return null;
        }

        return root as PdfDictionary;
    }

    internal static HashSet<int> PreservationExclusions(
        DocumentEngine engine,
        IReadOnlyList<PageLocation> kept,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (kept is null)
        {
            throw new ArgumentNullException(nameof(kept));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        limits.Validate();
        memory.CheckOwner(governor);
        HashSet<int> excluded = ParserContainers.CreateSet<int>(memory);
        HashSet<int> visited = ParserContainers.CreateSet<int>(memory);
        if (engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject? root) && root is PdfReference rootReference)
        {
            ParserContainers.AddSetItem(memory, excluded, rootReference.ObjectNumber, sizeof(int));
        }

        IIndirectResolver resolver = (IIndirectResolver)engine;
        PdfName parentKey = PdfName.FromAscii("Parent");
        foreach (PageLocation location in kept)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            PdfReference? source = location.Source;
            if (source is null)
            {
                continue;
            }

            visited.Clear(); ParserContainers.AddSetItem(memory, visited, source.ObjectNumber, sizeof(int));
            ParserContainers.AddSetItem(memory, excluded, source.ObjectNumber, sizeof(int));
            PdfDictionary current = location.Page;
            int depth = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                if (!current.TryGet(parentKey, out QpdfObject? raw) || raw is not PdfReference next)
                {
                    break;
                }

                // Source syntax alone does not establish containment. Failed
                // generations and private Parent values must remain preservable.
                if (!PdfObjectLookup.TryResolve(next, resolver, governor, cancellationToken, out QpdfObject? node)
                    || node is not PdfDictionary parent
                    || !PdfDictionaryLookup.HasName(parent, "Type"u8, "Pages"u8, resolver, governor, cancellationToken))
                {
                    break;
                }

                if (!ParserContainers.AddSetItem(memory, visited, next.ObjectNumber, sizeof(int)))
                {
                    break;
                }

                ParserContainers.AddSetItem(memory, excluded, next.ObjectNumber, sizeof(int));
                depth += 1;
                if (depth > limits.MaxNestingDepth)
                {
                    break;
                }

                current = parent;
            }
        }

        return excluded;
    }

    internal static List<PageLocation> KeptLocations(QpdfDocument document, IReadOnlyList<int> requested,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        if (requested is null)
        {
            throw new ArgumentNullException(nameof(requested));
        }

        memory.CheckOwner(governor); cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        List<PageLocation> kept = ParserContainers.CreateList<PageLocation>(memory, requested.Count, 16L);
        foreach (int pageNumber in requested)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            kept.Add(document.PageReferences[pageNumber - 1]);
        }

        return kept;
    }

    internal static (PdfReference? InfoRef, PdfReference? MetadataRef) CopyDocumentMetadata(
        GraphCopier copier,
        DocumentEngine engine,
        bool removeInfo,
        bool removeMetadata,
        DiagnosticBag diagnostics)
    {
        if (copier is null)
        {
            throw new ArgumentNullException(nameof(copier));
        }

        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        PdfReference? infoRef = null;
        PdfReference? metadataRef = null;
        // Upstream --remove-info also omits the catalog metadata stream.
        if (!removeMetadata && !removeInfo)
        {
            int firstId = copier.BeginCopyAttempt();
            try
            {
                PdfDictionary? catalog = TryGetInputCatalog(engine);
                if (catalog is not null
                    && catalog.TryGet(PdfName.FromAscii("Metadata"), out QpdfObject? raw)
                    && raw is not null && raw is not PdfNull)
                {
                    metadataRef = copier.AddIndirectObject(raw);
                }
            }
            catch (QpdfParseException)
            {
                copier.RollbackCopyAttempt(firstId, CancellationToken.None);
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped unpreservable document metadata.");
                metadataRef = null;
            }
            finally { copier.EndCopyAttempt(); }
        }

        if (!removeInfo)
        {
            int firstId = copier.BeginCopyAttempt();
            try
            {
                if (engine.Trailer.TryGet(PdfName.FromAscii("Info"), out QpdfObject? raw)
                    && raw is not null && raw is not PdfNull)
                {
                    infoRef = copier.AddIndirectObject(raw);
                }
            }
            catch (QpdfParseException)
            {
                copier.RollbackCopyAttempt(firstId, CancellationToken.None);
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped unpreservable document info.");
                infoRef = null;
            }
            finally { copier.EndCopyAttempt(); }
        }
        else
        {
            int firstId = copier.BeginCopyAttempt();
            try
            {
                PdfDictionary? infoDict = null;
                if (engine.Trailer.TryGet(PdfName.FromAscii("Info"), out QpdfObject? raw)
                    && raw is not null && raw is not PdfNull)
                {
                    if (raw is PdfReference infoReference)
                    {
                        if (((IIndirectResolver)engine).TryResolve(infoReference, out QpdfObject? resolved) && resolved is PdfDictionary indirect)
                        {
                            infoDict = indirect;
                        }
                    }
                    else if (raw is PdfDictionary direct)
                    {
                        infoDict = direct;
                    }
                }

                if (infoDict is not null
                    && infoDict.TryGet(PdfName.FromAscii("ModDate"), out QpdfObject? modDate)
                    && modDate is not null && modDate is not PdfNull)
                {
                    PdfDictionary reduced = new(new Dictionary<PdfName, QpdfObject>
                    {
                        [PdfName.FromAscii("ModDate")] = modDate,
                    });
                    infoRef = copier.AddIndirectObject(reduced);
                }
            }
            catch (QpdfParseException)
            {
                copier.RollbackCopyAttempt(firstId, CancellationToken.None);
                diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Skipped unpreservable document info.");
                infoRef = null;
            }
            finally { copier.EndCopyAttempt(); }
        }

        return (infoRef, metadataRef);
    }

    internal static readonly PdfName WriterMediaBox = PdfName.FromAscii("MediaBox");

    internal static readonly PdfName[] WriterInherited =
        [PdfName.FromAscii("Resources"), WriterMediaBox, PdfName.FromAscii("CropBox"), PdfName.FromAscii("Rotate")];

    internal static IReadOnlyDictionary<PdfName, QpdfObject> MaterializeAncestors(
        DocumentEngine engine, PageLocation location, GraphCopier copier,
        ResourceGovernor governor, QpdfLimits limits, CancellationToken cancellationToken,
        ParserMemoryScope ownership)
    {
        ownership.CheckOwner(governor);
        Dictionary<PdfName, QpdfObject>? extra = null;
        PageInheritance.InheritedValues inherited = PageInheritance.FindAncestors(engine, location, WriterInherited,
            limits, governor, cancellationToken);
        for (int index = 0; index < WriterInherited.Length; index++)
        {
            governor.CheckCancellation();
            PdfName key = WriterInherited[index];
            QpdfObject? value = inherited[index];
            if (value is null) { continue; }
            QpdfObject copied = copier.CopyValue(value, 0);
            if (copied is PdfNull) { continue; }
            extra ??= ParserContainers.CreateDictionary<PdfName, QpdfObject>(ownership);
            ParserContainers.EnsureEntryCapacity(ownership, extra, IntPtr.Size);
            extra[key] = copied;
        }
        return extra is null ? System.Collections.ObjectModel.ReadOnlyDictionary<PdfName, QpdfObject>.Empty : extra;
    }

    internal static void RequireMediaBox(PdfDictionary page)
    {
        if (!page.TryGet(WriterMediaBox, out QpdfObject box) || box is PdfNull)
        {
            throw new QpdfParseException("Selected page has no /MediaBox in its ancestry.", null, null);
        }
    }

    internal static void ValidateSourceCredentials(IReadOnlyList<PdfPageSource> sources,
        Dictionary<string, List<int>> groups, ResourceGovernor governor)
    {
        foreach (List<int> indices in groups.Values)
        {
            ValidateSourceCredentials(sources, indices, governor);
        }
    }

    internal static void ValidateSourceCredentials(IReadOnlyList<PdfPageSource> sources, List<int> indices, ResourceGovernor governor)
    {
        QpdfPassword? first = sources[indices[0]].Password;
        foreach (int index in indices)
        {
            governor.CheckCancellation();
            QpdfPassword? password = sources[index].Password;
            if (ReferenceEquals(first, password)) { continue; }
            if (first is null || password is null || !first.HasSameInput(password, governor))
            { throw new ArgumentException("Repeated page sources must use the same credential value and representation.", nameof(sources)); }
        }
    }

    internal static List<(int Source, int Slot)> CollatePageOrder(IReadOnlyList<int> counts,
        PdfPageCollation? collation, ResourceGovernor governor, ParserMemoryScope memory)
    {
        memory.CheckOwner(governor); governor.CheckCancellation();
        if (collation is not null && collation.GroupSizes.Count != 1 && collation.GroupSizes.Count != counts.Count)
        {
            throw new ArgumentException("Collation group sizes must hold one value or one per source.", nameof(collation));
        }
        using ParserMemoryScope result = ParserMemoryScope.Create(governor);
        List<(int Source, int Slot)> order = ParserContainers.CreateList<(int, int)>(result);
        long cursorBytes = checked(32L + sizeof(int) * (long)counts.Count);
        result.Reserve(cursorBytes);
        int[] cursors = new int[counts.Count];
        bool more = true;
        while (more)
        {
            more = false;
            for (int source = 0; source < counts.Count; source++)
            {
                governor.CheckCancellation();
                int size = collation is null ? counts[source]
                    : collation.GroupSizes.Count == 1 ? collation.GroupSizes[0] : collation.GroupSizes[source];
                for (int i = 0; i < size && cursors[source] < counts[source]; i++)
                {
                    governor.CheckCancellation();
                    ParserContainers.AddItem(result, order, (source, cursors[source]++), 8L);
                    more = true;
                }
            }
        }
        result.Release(cursorBytes); result.TransferTo(memory); return order;
    }

}
