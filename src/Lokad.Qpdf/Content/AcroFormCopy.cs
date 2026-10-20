using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

/// <summary>
/// One source document's preserved interactive form, already copied into its
/// destination pool: the source-side filtered field references plus the other
/// catalog-level form entries needed to merge and rebuild the output form.
/// </summary>
internal sealed record CopiedForm(
    IReadOnlyList<PdfReference> Fields,
    bool HasFields,
    bool HasNeedAppearances,
    bool NeedAppearances,
    QpdfObject? DefaultResources,
    bool HasCalculationOrder,
    IReadOnlyList<QpdfObject> CalculationOrder,
    IReadOnlyDictionary<PdfName, QpdfObject> Extras);

/// <summary>
/// Interactive-form preservation through the shared copy engine. Ordinary
/// selection and rewrite paths carry the input catalog's form dictionary
/// (field registration, values, parent links and default resources) into the
/// fresh output catalog; only the explicit <c>--remove-acroform</c> prune drops
/// it. Explicit selections and merges prune unreachable form trees, matching the
/// upstream reference: a field stays when at least one widget in its subtree
/// survives, memberless tops drop, and an emptied form drops entirely. Whole-file
/// rewrites keep widget-free registrations verbatim.
/// Merges concatenate per-source registrations with upstream per-source-snapshot
/// `+N` collision renaming, keep the first default-resources dictionary, and OR the
/// NeedAppearances flags.
/// </summary>
internal static class AcroFormCopy
{
    internal static CopiedForm? TryCopySourceForm(
        DocumentEngine engine,
        GraphCopier copier,
        IReadOnlyList<PageLocation> keptPages,
        bool pruneUnreachableFields,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
        => ReadSourceForm(engine, copier, keptPages, pruneUnreachableFields, limits,
            governor, diagnostics, cancellationToken, false);

    private static CopiedForm? ReadSourceForm(
        DocumentEngine engine, GraphCopier copier, IReadOnlyList<PageLocation> keptPages,
        bool pruneUnreachableFields, QpdfLimits limits, ResourceGovernor governor,
        DiagnosticBag diagnostics, CancellationToken cancellationToken, bool retainSourceObjects)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (copier is null)
        {
            throw new ArgumentNullException(nameof(copier));
        }

        if (keptPages is null)
        {
            throw new ArgumentNullException(nameof(keptPages));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        limits.Validate();
        PdfDictionary? source = TryGetSourceForm(engine);
        if (source is null)
        {
            return null;
        }

        IIndirectResolver resolver = (IIndirectResolver)engine;
        HashSet<int> keptWidgets = CollectKeptWidgets(resolver, keptPages, limits, governor, cancellationToken);
        bool hasFields = IsFieldArrayPresent(resolver, source);
        List<QpdfObject> keptEntries = FilterTopEntries(resolver, source, keptWidgets, pruneUnreachableFields, limits, governor, cancellationToken);
        List<PdfReference> copiedFields = new(keptEntries.Count);
        foreach (QpdfObject entry in keptEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (entry is PdfReference)
            {
                if ((retainSourceObjects ? copier.RetainSourceValue(entry) : copier.CopyValue(entry, 0)) is PdfReference copiedReference)
                {
                    copiedFields.Add(copiedReference);
                }

                continue;
            }

            PdfReference link = retainSourceObjects
                ? copier.AddIndirectObjectWithId(copier.RetainSourceValue(entry), copier.NextId)
                : copier.AddIndirectObject(entry) ?? throw new QpdfException("Form field copy pruned unexpectedly.");
            copiedFields.Add(link);
        }
        bool hasNeed = false;
        bool need = false;
        QpdfObject? resources = null;
        bool hasOrder = false;
        List<QpdfObject> order = new();
        Dictionary<PdfName, QpdfObject> extras = new();
        foreach (PdfName key in source.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            string text = key.DisplayText;
            if (text == "Fields")
            {
                continue;
            }

            if (!source.TryGet(key, out QpdfObject? raw) || raw is null || raw is PdfNull)
            {
                continue;
            }

            if (text == "NeedAppearances" && raw is PdfBoolean flag)
            {
                hasNeed = true;
                need = flag.Value;
                continue;
            }

            QpdfObject copied = retainSourceObjects ? copier.RetainSourceValue(raw) : copier.CopyValue(raw, 0);
            if (text == "DR")
            {
                resources = copied;
                continue;
            }

            if (text == "CO" && copied is PdfArray copiedOrder)
            {
                hasOrder = true;
                foreach (QpdfObject item in copiedOrder.Items)
                {
                    order.Add(item);
                }

                continue;
            }

            governor.ReserveMemory(32L);
            extras[key] = copied;
        }

        return new CopiedForm(copiedFields, hasFields, hasNeed, need, resources, hasOrder, order, extras);
    }

    internal static PdfDictionary? BuildSingle(
        DocumentEngine engine,
        GraphCopier copier,
        IReadOnlyList<PageLocation> keptPages,
        bool pruneUnreachableFields,
        IReadOnlyList<(GraphCopier Copier, int Id)> occurrences,
        Dictionary<int, QpdfObject> pool,
        IReadOnlyList<OverlayFieldImport> overlayImports,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        bool retainSourceObjects)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (copier is null)
        {
            throw new ArgumentNullException(nameof(copier));
        }

        if (keptPages is null)
        {
            throw new ArgumentNullException(nameof(keptPages));
        }

        if (occurrences is null)
        {
            throw new ArgumentNullException(nameof(occurrences));
        }

        if (pool is null)
        {
            throw new ArgumentNullException(nameof(pool));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        if (overlayImports is null)
        {
            throw new ArgumentNullException(nameof(overlayImports));
        }

        CopiedForm? part = ReadSourceForm(engine, copier, keptPages, pruneUnreachableFields, limits,
            governor, diagnostics, cancellationToken, retainSourceObjects);
        foreach (KeyValuePair<int, QpdfObject> entry in copier.CopiedObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            pool.TryAdd(entry.Key, entry.Value);
        }
        if (part is null && overlayImports.Count == 0)
        {
            return null;
        }

        List<CopiedForm?> allForms = new((part is null ? 0 : 1) + overlayImports.Count);
        List<GraphCopier> allCopiers = new(allForms.Capacity);
        if (part is not null)
        {
            allForms.Add(part);
            allCopiers.Add(copier);
        }

        foreach (OverlayFieldImport import in overlayImports)
        {
            if (import is null)
            {
                throw new ArgumentException("Overlay imports must not contain null entries.", nameof(overlayImports));
            }

            allForms.Add(import.Part);
            allCopiers.Add(import.Copier);
        }

        RenameCollidingFields(allForms, allCopiers, limits, governor, cancellationToken);
        List<CopiedForm> present = new(allForms.Count);
        foreach (CopiedForm? form in allForms)
        {
            if (form is not null)
            {
                present.Add(form);
            }
        }

        CopiedForm? combined = Combine(present);
        if (combined is null)
        {
            return null;
        }

        if (overlayImports.Count > 0)
        {
            (PdfByteString? destAppearance, int? destQuadding) = ReadMergedDefaults(combined);
            int allocator = 1;
            foreach (int id in pool.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                if (id >= allocator)
                {
                    checked
                    {
                        allocator = id + 1;
                    }
                }
            }

            combined = ApplyOverlayImports(combined, overlayImports, destAppearance, destQuadding, overlayImports[0].Copier, pool, ref allocator, limits, governor, diagnostics, cancellationToken);
        }

        HashSet<int> survivors = CollectAnnotRefs(occurrences, governor, cancellationToken);
        return BuildCatalogForm(combined, pool, survivors, pruneUnreachableFields, limits, governor, cancellationToken);
    }

    internal static CopiedForm? Combine(IReadOnlyList<CopiedForm> parts)
    {
        if (parts is null)
        {
            throw new ArgumentNullException(nameof(parts));
        }

        List<CopiedForm> present = new(parts.Count);
        foreach (CopiedForm part in parts)
        {
            if (part is null)
            {
                throw new ArgumentException("Form parts must not contain null entries.", nameof(parts));
            }

            present.Add(part);
        }

        if (present.Count == 0)
        {
            return null;
        }

        List<PdfReference> fields = new();
        bool hasFields = false;
        bool hasNeed = false;
        bool need = false;
        QpdfObject? resources = null;
        bool hasOrder = false;
        List<QpdfObject> order = new();
        Dictionary<PdfName, QpdfObject> extras = new();
        foreach (CopiedForm part in present)
        {
            fields.AddRange(part.Fields);
            if (part.HasFields)
            {
                hasFields = true;
            }
            if (part.HasNeedAppearances)
            {
                hasNeed = true;
                need = need || part.NeedAppearances;
            }

            if (resources is null && part.DefaultResources is not null)
            {
                resources = part.DefaultResources;
            }

            if (part.HasCalculationOrder)
            {
                hasOrder = true;
                order.AddRange(part.CalculationOrder);
            }

            foreach (KeyValuePair<PdfName, QpdfObject> extra in part.Extras)
            {
                if (!extras.ContainsKey(extra.Key))
                {
                    extras[extra.Key] = extra.Value;
                }
            }
        }

        return new CopiedForm(fields, hasFields, hasNeed, need, resources, hasOrder, order, extras);
    }

    /// <summary>
    /// Renames colliding field names across merged sources with upstream
    /// `+N` suffixes: each source sees the final names of strictly earlier sources,
    /// walks field subtrees top-down recomputing fully-qualified names, keeps
    /// originals absent from that snapshot, and rewrites the smallest free `+N`
    /// suffix onto colliding named nodes, including direct-dictionary nodes whose
    /// rebuilt subtrees splice back into their parents. Single-form merges keep
    /// intra-document duplicates like the reference.
    /// </summary>
    internal static void RenameCollidingFields(
        IReadOnlyList<CopiedForm?> forms,
        IReadOnlyList<GraphCopier> copiers,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (forms is null)
        {
            throw new ArgumentNullException(nameof(forms));
        }
        if (copiers is null)
        {
            throw new ArgumentNullException(nameof(copiers));
        }
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }
        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }
        if (forms.Count != copiers.Count)
        {
            throw new ArgumentException("Form parts and copiers must align.", nameof(copiers));
        }
        limits.Validate();
        int present = 0;
        foreach (CopiedForm? form in forms)
        {
            if (form is not null)
            {
                present++;
            }
        }
        if (present < 2)
        {
            return;
        }
        using ParserMemoryScope names = ParserMemoryScope.Create(governor);
        HashSet<string> accumulated = ParserContainers.CreateSet<string>(names);
        for (int s = 0; s < forms.Count; s++)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            CopiedForm? form = forms[s];
            if (form is null) { continue; }
            GraphCopier copier = copiers[s] ?? throw new ArgumentException("Copiers must not contain null entries.", nameof(copiers));
            using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
            List<string> finals = ParserContainers.CreateList<string>(traversal);
            GovernedIntSet visited = GovernedIntSet.Create(traversal, governor, cancellationToken);
            // Accumulated names remain unchanged while this source is traversed:
            // they are already the required snapshot of strictly earlier sources.
            foreach (PdfReference fieldRef in form.Fields)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (!copier.CopiedObjects.TryGetValue(fieldRef.ObjectNumber, out QpdfObject? nodeRaw) || nodeRaw is not PdfDictionary node)
                { continue; }
                PdfDictionary renamed = RenameTree(copier, fieldRef.ObjectNumber, node, null, accumulated, finals, visited,
                    names, traversal, limits, governor, cancellationToken, 0);
                if (!ReferenceEquals(renamed, node)) { copier.ReplaceCopiedObject(fieldRef.ObjectNumber, renamed); }
            }
            foreach (string final in finals)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddSetItem(names, accumulated, final, IntPtr.Size);
            }
        }
    }

    private static PdfDictionary RenameTree(
        GraphCopier copier, int? nodeId, PdfDictionary node, string? parentName,
        HashSet<string> snapshot, List<string> finals, GovernedIntSet visited,
        ParserMemoryScope names, ParserMemoryScope traversal, QpdfLimits limits,
        ResourceGovernor governor, CancellationToken token, int depth)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (depth > limits.MaxNestingDepth || nodeId.HasValue && !visited.Add(traversal, nodeId.Value)) { return node; }
        PdfByteString? ownBytes = ReadFieldText(copier, node);
        string? own = ownBytes is null ? null : PdfText.Decode(ownBytes.ValueMemory, names, governor, token);
        string? qualified = parentName;
        if (own is not null)
        {
            qualified = parentName is null ? own : AppendFieldName(parentName, '.', own, names, governor, token);
            if (snapshot.Contains(qualified))
            {
                int suffix = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    using ParserMemoryScope candidate = ParserMemoryScope.Create(governor);
                    suffix = checked(suffix + 1);
                    candidate.Reserve(48L); // Bounded decimal suffix.
                    string digits = suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string renamedOwn = AppendFieldName(own, '+', digits, candidate, governor, token);
                    string renamed = parentName is null ? renamedOwn : AppendFieldName(parentName, '.', renamedOwn, candidate, governor, token);
                    if (snapshot.Contains(renamed)) { continue; }
                    candidate.TransferTo(names);
                    using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
                    byte[] raw = PdfText.Encode(renamedOwn, graph.Memory, governor, token);
                    graph.Add(checked(32L + raw.Length)); graph.Memory.Reserve(32L);
                    PdfByteString text = PdfByteString.FromOwnedBytes(raw, false); graph.Add(32L);
                    node = PdfDictionaryRebuild.ReplaceEntry(graph, node, "T"u8, text);
                    graph.Retain();
                    if (nodeId.HasValue) { copier.ReplaceCopiedObject(nodeId.Value, node); }
                    qualified = renamed; break;
                }
            }
            ParserContainers.AddItem(traversal, finals, qualified, IntPtr.Size);
        }

        if (!node.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw)) { return node; }
        PdfArray? kids = kidsRaw as PdfArray;
        if (kidsRaw is PdfReference kidsRef && copier.CopiedObjects.TryGetValue(kidsRef.ObjectNumber, out QpdfObject? resolvedKids))
        { kids = resolvedKids as PdfArray; }
        if (kids is null) { return node; }
        List<QpdfObject>? rebuiltKids = null;
        for (int i = 0; i < kids.Count; i++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            QpdfObject item = kids[i];
            if (item is PdfReference kidRef && copier.CopiedObjects.TryGetValue(kidRef.ObjectNumber, out QpdfObject? kidRaw) && kidRaw is PdfDictionary kid)
            { RenameTree(copier, kidRef.ObjectNumber, kid, qualified, snapshot, finals, visited, names, traversal, limits, governor, token, depth + 1); }
            else if (item is PdfDictionary directKid)
            {
                QpdfObject renamed = RenameTree(copier, null, directKid, qualified, snapshot, finals, visited, names, traversal, limits, governor, token, depth + 1);
                if (!ReferenceEquals(renamed, item) && rebuiltKids is null)
                {
                    rebuiltKids = ParserContainers.CreateList<QpdfObject>(traversal);
                    for (int j = 0; j < i; j++)
                    { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); ParserContainers.AddItem(traversal, rebuiltKids, kids[j]); }
                }
                item = renamed;
            }
            if (rebuiltKids is not null) { ParserContainers.AddItem(traversal, rebuiltKids, item); }
        }
        if (rebuiltKids is not null)
        {
            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
            long bytes = checked(64L + rebuiltKids.Count * (long)IntPtr.Size);
            graph.Memory.Reserve(bytes); PdfArray rebuilt = new(rebuiltKids); graph.Add(bytes);
            ParserContainers.ReleaseList(traversal, rebuiltKids); rebuiltKids = null;
            node = PdfDictionaryRebuild.ReplaceEntry(graph, node, "Kids"u8, rebuilt); graph.Retain();
            if (nodeId.HasValue) { copier.ReplaceCopiedObject(nodeId.Value, node); }
        }
        return node;
    }

    private static string AppendFieldName(string first, char separator, string second,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        int length = checked(first.Length + 1 + second.Length);
        memory.Reserve(GovernedText.StringBytes(length));
        return string.Create(length, (first, separator, second, governor, token), static (target, state) =>
        {
            for (int offset = 0; offset < state.first.Length; offset += Math.Min(8192, state.first.Length - offset))
            {
                state.token.ThrowIfCancellationRequested(); state.governor.CheckCancellation();
                int count = Math.Min(8192, state.first.Length - offset); state.first.AsSpan(offset, count).CopyTo(target[offset..]);
            }
            target[state.first.Length] = state.separator;
            for (int offset = 0; offset < state.second.Length; offset += Math.Min(8192, state.second.Length - offset))
            {
                state.token.ThrowIfCancellationRequested(); state.governor.CheckCancellation();
                int count = Math.Min(8192, state.second.Length - offset); state.second.AsSpan(offset, count).CopyTo(target[(state.first.Length + 1 + offset)..]);
            }
        });
    }

    /// <summary>
    /// One overlay/underlay placement batch of imported widget fields, in placement
    /// order. The part joins the merge rename and combine passes; its copier owns the
    /// destination-pool copies for pool writes.
    /// </summary>
    internal sealed record OverlayFieldImport(
        GraphCopier Copier,
        CopiedForm Part,
        PdfByteString? SourceDefaultAppearance,
        int? SourceQuadding);

    /// <summary>
    /// The resolved import top for an overlay source widget: the node and its
    /// source object number plus the walk outcome. A null number with
    /// <c>DirectTop</c> means the walk ended at a direct dictionary, which the
    /// reference refuses to import; <c>Cycled</c> marks a repeated parent link,
    /// which the reference still imports with a warning.
    /// </summary>
    internal sealed record OverlayTopMatch(PdfDictionary Node, int? Id, bool Cycled, bool DirectTop);

    /// <summary>
    /// Carries one overlay source document's form-level defaults for widget
    /// import: whether its catalog form holds a field array, the document
    /// default appearance and quadding (empty or zero when the form exists but
    /// a value is absent), the NeedAppearances flag, and the raw default
    /// resources value for per-placement copying.
    /// </summary>
    internal sealed record OverlaySourceForm(
        bool HasFields,
        PdfByteString? DefaultAppearance,
        int? Quadding,
        bool NeedAppearances,
        QpdfObject? DefaultResources,
        HashSet<int> ReachedObjects,
        bool FieldsNotArray,
        bool HasDirectNodes);

    /// <summary>
    /// Finds the import top for an overlay source widget by walking preserved
    /// /Parent links through references and direct dictionaries, mirroring the
    /// reference field lookup. Returns null when nothing importable backs the
    /// annotation: direct annotations, missing source forms, and tops without
    /// field identity all fall back to plain annotation clones. Cyclic walks
    /// keep the last indirect node so the import stays valid; direct-dictionary
    /// tops report <c>DirectTop</c> instead so callers can note the refusal.
    /// </summary>
    internal static OverlayTopMatch? FindOverlayTopField(
        PdfDictionary annot,
        int? annotId,
        bool sourceHasFields,
        IIndirectResolver source)
    {
        if (annot is null)
        {
            throw new ArgumentNullException(nameof(annot));
        }

        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (!annotId.HasValue || !sourceHasFields)
        {
            return null;
        }

        PdfDictionary cursor = annot;
        int? cursorId = annotId;
        HashSet<int> seen = new() { annotId.Value };
        bool cycled = false;
        bool first = true;
        int depth = 0;
        while (depth < 32)
        {
            depth += 1;
            if (!cursor.TryGet(PdfName.FromAscii("Parent"), out QpdfObject? parentRaw) || parentRaw is null || parentRaw is PdfNull)
            {
                break;
            }

            if (parentRaw is PdfReference parentReference)
            {
                if (!seen.Add(parentReference.ObjectNumber))
                {
                    if (!first || parentReference.ObjectNumber != annotId.Value)
                    {
                        cycled = true;
                    }

                    break;
                }

                first = false;
                if (!source.TryResolve(parentReference, out QpdfObject? resolved) || resolved is not PdfDictionary parentDict)
                {
                    break;
                }

                cursor = parentDict;
                cursorId = parentReference.ObjectNumber;
                continue;
            }

            if (parentRaw is PdfDictionary parentDirect)
            {
                first = false;
                cursor = parentDirect;
                cursorId = null;
                continue;
            }

            break;
        }

        if (!HasFieldIdentity(cursor))
        {
            return null;
        }

        if (!cursorId.HasValue)
        {
            return new OverlayTopMatch(cursor, null, cycled, true);
        }

        return new OverlayTopMatch(cursor, cursorId, cycled, false);
    }

    /// <summary>
    /// Reports whether a dictionary carries field identity: a partial name,
    /// a kid array, or a field type, mirroring the reference field check.
    /// </summary>
    internal static bool HasFieldIdentity(PdfDictionary node)
    {
        if (node is null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        foreach (string key in new[] { "T", "Kids", "FT" })
        {
            if (node.TryGet(PdfName.FromAscii(key), out QpdfObject? found) && found is not null && found is not PdfNull)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads one overlay source document's form-level defaults for widget
    /// import, plus the indirect objects reachable through its field tree.
    /// Returns null when the source has no usable catalog form, so widget
    /// annotations fall back to plain clones exactly as the reference skips
    /// field lookup without a field array. The reached set lets callers mirror
    /// the reference fallback note for widgets the field tree never mentions.
    /// </summary>
    internal static OverlaySourceForm? TryReadOverlaySourceForm(
        DocumentEngine engine,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        limits.Validate();
        PdfDictionary? source;
        try
        {
            source = TryGetSourceForm(engine);
        }
        catch (QpdfParseException)
        {
            return null;
        }

        if (source is null)
        {
            return null;
        }

        bool hasFields = source.TryGet(PdfName.FromAscii("Fields"), out QpdfObject? fieldsRaw) && fieldsRaw is not null && fieldsRaw is not PdfNull;
        PdfByteString appearance = EmptyAppearance;
        if (source.TryGet(PdfName.FromAscii("DA"), out QpdfObject? appearanceRaw) && appearanceRaw is PdfByteString appearanceBytes)
        {
            appearance = appearanceBytes;
        }

        int quadding = 0;
        if (source.TryGet(PdfName.FromAscii("Q"), out QpdfObject? quaddingRaw) && quaddingRaw is PdfInteger quaddingInteger
            && quaddingInteger.Value >= int.MinValue && quaddingInteger.Value <= int.MaxValue)
        {
            quadding = (int)quaddingInteger.Value;
        }

        bool needAppearances = source.TryGet(PdfName.FromAscii("NeedAppearances"), out QpdfObject? needRaw) && needRaw is PdfBoolean needFlag && needFlag.Value;
        QpdfObject? resources = source.TryGet(PdfName.FromAscii("DR"), out QpdfObject? resourcesRaw) && resourcesRaw is not null && resourcesRaw is not PdfNull
            ? resourcesRaw
            : null;
        (HashSet<int> reached, bool fieldsNotArray, bool hasDirectNodes) = CollectReachedFieldObjects(engine, source, hasFields, limits, governor, cancellationToken);
        return new OverlaySourceForm(hasFields, appearance, quadding, needAppearances, resources, reached, fieldsNotArray, hasDirectNodes);
    }

    private static (HashSet<int> Reached, bool FieldsNotArray, bool HasDirectNodes) CollectReachedFieldObjects(
        DocumentEngine engine,
        PdfDictionary source,
        bool hasFields,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        HashSet<int> reached = new();
        if (!hasFields)
        {
            return (reached, false, false);
        }

        if (!source.TryGet(PdfName.FromAscii("Fields"), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return (reached, false, false);
        }

        IIndirectResolver resolver = (IIndirectResolver)engine;
        QpdfObject target = raw;
        if (raw is PdfReference fieldsReference)
        {
            if (!resolver.TryResolve(fieldsReference, out QpdfObject? resolved) || resolved is null || resolved is PdfNull)
            {
                return (reached, true, false);
            }

            target = resolved;
        }

        if (target is not PdfArray fields)
        {
            return (reached, true, false);
        }

        bool hasDirectNodes = false;
        HashSet<int> visited = new();
        Stack<(QpdfObject Node, int Depth)> stack = new();
        for (int i = 0; i < fields.Count; i++)
        {
            stack.Push((fields[i], 0));
        }

        while (stack.TryPop(out (QpdfObject Node, int Depth) current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (current.Depth > limits.MaxNestingDepth)
            {
                continue;
            }

            if (current.Node is PdfReference reference)
            {
                if (!visited.Add(reference.ObjectNumber))
                {
                    continue;
                }

                if (!resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is not PdfDictionary dictionary)
                {
                    continue;
                }

                reached.Add(reference.ObjectNumber);
                if (!dictionary.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw) || kidsRaw is null || kidsRaw is PdfNull)
                {
                    continue;
                }

                QpdfObject kidsTarget = kidsRaw;
                if (kidsRaw is PdfReference kidsReference)
                {
                    if (!resolver.TryResolve(kidsReference, out QpdfObject? kidsResolved) || kidsResolved is null || kidsResolved is PdfNull)
                    {
                        continue;
                    }

                    kidsTarget = kidsResolved;
                }

                if (kidsTarget is not PdfArray kids)
                {
                    continue;
                }

                for (int i = 0; i < kids.Count; i++)
                {
                    stack.Push((kids[i], current.Depth + 1));
                }
            }
            else if (current.Node is PdfDictionary)
            {
                hasDirectNodes = true;
            }
        }

        return (reached, false, hasDirectNodes);
    }

    /// <summary>
    /// Reads the merged default appearance and quadding back out of combined
    /// form extras, so overlay imports compare against the destination values
    /// the reference would inherit. Absent or mistyped entries yield null.
    /// </summary>
    internal static (PdfByteString? Appearance, int? Quadding) ReadMergedDefaults(CopiedForm combined)
    {
        if (combined is null)
        {
            throw new ArgumentNullException(nameof(combined));
        }

        PdfByteString? appearance = null;
        int? quadding = null;
        if (combined.Extras.TryGetValue(PdfName.FromAscii("DA"), out QpdfObject? appearanceRaw) && appearanceRaw is PdfByteString appearanceBytes)
        {
            appearance = appearanceBytes;
        }

        if (combined.Extras.TryGetValue(PdfName.FromAscii("Q"), out QpdfObject? quaddingRaw) && quaddingRaw is PdfInteger quaddingInteger
            && quaddingInteger.Value >= int.MinValue && quaddingInteger.Value <= int.MaxValue)
        {
            quadding = (int)quaddingInteger.Value;
        }

        return (appearance, quadding);
    }


    /// <summary>
    /// Unions overlay-imported default resources into the combined form resources,
    /// destination wins with upstream key_N conflict renaming, then applies per-import
    /// fixups: default-appearance and quadding override, field-resource reset, and
    /// default-appearance rewriting. The destination base resolves through the assembled
    /// pool, and rewritten pool objects sync back into it, so later catalog assembly
    /// observes the union and the fixups. Returns the updated combined form.
    /// </summary>
    internal static CopiedForm ApplyOverlayImports(
        CopiedForm combined,
        IReadOnlyList<OverlayFieldImport> imports,
        PdfByteString? destDefaultAppearance,
        int? destQuadding,
        GraphCopier unionOwner,
        Dictionary<int, QpdfObject> pool,
        ref int allocator,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        if (combined is null)
        {
            throw new ArgumentNullException(nameof(combined));
        }

        if (imports is null)
        {
            throw new ArgumentNullException(nameof(imports));
        }

        if (unionOwner is null)
        {
            throw new ArgumentNullException(nameof(unionOwner));
        }

        if (pool is null)
        {
            throw new ArgumentNullException(nameof(pool));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        limits.Validate();
        if (imports.Count == 0)
        {
            return combined;
        }

        Dictionary<PdfName, QpdfObject> union = new();
        if (combined.DefaultResources is PdfReference baseReference
            && pool.TryGetValue(baseReference.ObjectNumber, out QpdfObject? baseTarget)
            && baseTarget is PdfDictionary baseDict)
        {
            foreach (PdfName key in baseDict.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                if (baseDict.TryGet(key, out QpdfObject? kept) && kept is not null)
                {
                    union[key] = kept;
                }
            }
        }
        else if (combined.DefaultResources is PdfDictionary baseDirect)
        {
            foreach (PdfName key in baseDirect.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                if (baseDirect.TryGet(key, out QpdfObject? kept) && kept is not null)
                {
                    union[key] = kept;
                }
            }
        }

        using ParserMemoryScope mapMemory = ParserMemoryScope.Create(governor);
        List<PdfNameTable<PdfName>?> maps = ParserContainers.CreateList<PdfNameTable<PdfName>?>(mapMemory);
        foreach (OverlayFieldImport import in imports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (import.Part.DefaultResources is null)
            {
                ParserContainers.AddItem(mapMemory, maps, null, IntPtr.Size);
                continue;
            }

            QpdfObject? resolved = ResolveImportValue(import.Copier, import.Part.DefaultResources);
            if (resolved is not PdfDictionary sourceDict)
            {
                ParserContainers.AddItem(mapMemory, maps, null, IntPtr.Size);
                continue;
            }

            ParserContainers.AddItem(mapMemory, maps, MergeResourceDictionaries(union, sourceDict, import.Copier, mapMemory, governor, cancellationToken), IntPtr.Size);
        }

        governor.ReserveMemory(64L);
        int unionId = allocator;
        checked { allocator += 1; }
        unionOwner.AddIndirectObjectWithId(new PdfDictionary(union), unionId);
        PdfReference unionRef = new(unionId, 0);
        CopiedForm updated = new(
            combined.Fields,
            combined.HasFields,
            combined.HasNeedAppearances,
            combined.NeedAppearances,
            unionRef,
            combined.HasCalculationOrder,
            combined.CalculationOrder,
            combined.Extras);

        for (int i = 0; i < imports.Count; i++)
        {
            PdfNameTable<PdfName>? fontMap = maps[i];
            FixImportedSubtrees(imports[i], fontMap, unionRef, destDefaultAppearance, destQuadding, limits, governor, diagnostics, cancellationToken);
        }

        HashSet<GraphCopier> owners = new() { unionOwner };
        foreach (OverlayFieldImport import in imports)
        {
            owners.Add(import.Copier);
        }

        foreach (GraphCopier owner in owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            foreach (KeyValuePair<int, QpdfObject> entry in owner.CopiedObjects)
            {
                pool[entry.Key] = entry.Value;
            }
        }

        return updated;
    }

    private static QpdfObject? ResolveImportValue(GraphCopier copier, QpdfObject value)
    {
        if (value is PdfReference reference
            && copier.CopiedObjects.TryGetValue(reference.ObjectNumber, out QpdfObject? target)
            && target is not null
            && target is not PdfNull)
        {
            return target;
        }

        if (value is PdfNull)
        {
            return null;
        }

        return value;
    }

    private static PdfNameTable<PdfName> MergeResourceDictionaries(
        Dictionary<PdfName, QpdfObject> union,
        PdfDictionary source,
        GraphCopier owner,
        ParserMemoryScope mapMemory,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        PdfNameTable<PdfName> map = PdfNameTable<PdfName>.Create(mapMemory, governor, IntPtr.Size, cancellationToken);
        foreach (PdfName rtype in source.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!source.TryGet(rtype, out QpdfObject? otherRaw) || otherRaw is null || otherRaw is PdfNull)
            {
                continue;
            }

            QpdfObject? otherResolved = ResolveImportValue(owner, otherRaw);
            if (otherResolved is null)
            {
                continue;
            }

            if (!union.TryGetValue(rtype, out QpdfObject? having) || having is null || having is PdfNull)
            {
                governor.ReserveMemory(32L);
                union[rtype] = otherResolved;
                continue;
            }

            QpdfObject? havingResolved = ResolveImportValue(owner, having);
            if (havingResolved is PdfDictionary havingDict && otherResolved is PdfDictionary otherDict)
            {
                MergeResourceSubdictionary(union, rtype, havingDict, otherDict, map, mapMemory, governor, cancellationToken);
            }
            else if (havingResolved is PdfArray havingArray && otherResolved is PdfArray otherArray)
            {
                MergeResourceArray(union, rtype, havingArray, otherArray, governor, cancellationToken);
            }
        }

        return map;
    }

    private static void MergeResourceSubdictionary(
        Dictionary<PdfName, QpdfObject> union, PdfName rtype, PdfDictionary having, PdfDictionary other,
        PdfNameTable<PdfName> fontMap, ParserMemoryScope mapMemory,
        ResourceGovernor governor, CancellationToken token)
    {
        using ParserMemoryScope temporary = ParserMemoryScope.Create(governor);
        Dictionary<int, PdfName> knownIds = ParserContainers.CreateDictionary<int, PdfName>(temporary);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfNameTable<QpdfObject> merged = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        using (PdfDictionary.EntryEnumerator entries = having.GetEntryEnumerator())
        {
            while (entries.MoveNext(governor, token))
            {
                merged.Assign(graph.Memory, governor, entries.Current.Key, entries.Current.Value, token);
                if (entries.Current.Value is PdfReference link)
                {
                    ParserContainers.EnsureEntryCapacity(temporary, knownIds, IntPtr.Size);
                    knownIds[link.ObjectNumber] = entries.Current.Key;
                }
            }
        }
        bool fonts = PdfDictionaryLookup.NameEquals(rtype, "Font"u8, governor, token);
        int suffix = 1;
        Span<byte> suffixBytes = stackalloc byte[11];
        using (PdfDictionary.EntryEnumerator entries = other.GetEntryEnumerator())
        {
            while (entries.MoveNext(governor, token))
            {
                PdfName key = entries.Current.Key; QpdfObject incoming = entries.Current.Value;
                if (incoming is PdfNull) { continue; }
                if (!merged.TryGetValue(key, governor, token, out _))
                { merged.Assign(graph.Memory, governor, key, incoming, token); continue; }
                if (incoming is PdfReference link && knownIds.TryGetValue(link.ObjectNumber, out PdfName? reuse))
                {
                    if (fonts && !key.Equals(reuse)) { fontMap.Assign(mapMemory, governor, key, reuse, token); }
                    continue;
                }
                while (true)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    System.Buffers.Text.Utf8Formatter.TryFormat(suffix, suffixBytes, out int digits);
                    int length = checked(key.RawSpan.Length + 1 + digits);
                    using ParserMemoryScope candidate = ParserMemoryScope.Create(governor);
                    long bytes = checked(56L + length); candidate.Reserve(bytes);
                    byte[] raw = new byte[length];
                    GovernedBufferStream.CopyBytes(key.RawSpan, raw.AsSpan(0, key.RawSpan.Length), governor, token, CancellationToken.None);
                    raw[key.RawSpan.Length] = (byte)'_'; suffixBytes[..digits].CopyTo(raw.AsSpan(key.RawSpan.Length + 1));
                    PdfName fresh = PdfName.FromOwnedBytes(raw);
                    if (merged.TryGetValue(fresh, governor, token, out _)) { suffix = checked(suffix + 1); continue; }
                    candidate.TransferTo(graph.Memory); graph.Add(bytes);
                    merged.Assign(graph.Memory, governor, fresh, incoming, token);
                    if (fonts) { fontMap.Assign(mapMemory, governor, key, fresh, token); }
                    break;
                }
            }
        }
        union[rtype] = PdfDictionaryRebuild.Complete(graph, merged, allocation);
        graph.Retain();
    }
    private static void MergeResourceArray(
        Dictionary<PdfName, QpdfObject> union,
        PdfName rtype,
        PdfArray having,
        PdfArray other,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        List<QpdfObject> items = new(having.Count + other.Count);
        for (int i = 0; i < having.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            items.Add(having[i]);
        }

        for (int i = 0; i < other.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (other[i] is PdfName || other[i] is PdfByteString || other[i] is PdfInteger || other[i] is PdfReal || other[i] is PdfBoolean)
            {
                bool seen = false;
                foreach (QpdfObject existing in items)
                {
                    if (existing.Equals(other[i]))
                    {
                        seen = true;
                        break;
                    }
                }

                if (!seen)
                {
                    governor.ReserveMemory(16L);
                    items.Add(other[i]);
                }
            }
        }

        governor.ReserveMemory(32L);
        union[rtype] = new PdfArray(items);
    }

    internal static PdfByteString? RewriteOverlayDa(PdfByteString appearance, PdfNameTable<PdfName>? fontMap,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (fontMap is null || fontMap.Count == 0) { return null; }
        GovernedBufferStream? output = null;
        try
        {
            ContentTokenizer tokenizer = new(appearance.ValueMemory, limits, governor, token);
            ReadOnlyMemory<byte> lastName = ReadOnlyMemory<byte>.Empty;
            int nameOffset = -1, written = 0;
            while (tokenizer.TryReadToken(out ContentToken current))
            {
                if (current.Kind == ContentTokenKind.Name)
                { lastName = current.Data; nameOffset = checked((int)current.Offset); }
                else if (current.Kind == ContentTokenKind.Operator && current.Data.Span.SequenceEqual("Tf"u8)
                    && nameOffset >= written && fontMap.TryGetValue(PdfNameBytes.Decode(lastName.Span, governor, token), governor, token, out PdfName? mapped))
                {
                    output ??= new(governor, limits.MaxDecodedBytes, nameof(QpdfLimits.MaxDecodedBytes));
                    output.Write(appearance.ValueSpan.Slice(written, nameOffset - written), token);
                    using WriterEncoding name = WriterEncoding.Name(mapped, limits, governor, token);
                    output.Write(name.Data.Span, token);
                    written = checked(nameOffset + 1 + lastName.Length);
                }
            }
            if (output is null) { return null; }
            output.Write(appearance.ValueSpan[written..], token);
            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
            byte[] bytes = output.ToArray(graph.Memory, token);
            graph.Add(bytes.Length == 0 ? 0L : bytes.LongLength + GovernedBufferStream.ArrayOverheadBytes);
            graph.Memory.Reserve(24L);
            PdfByteString result = PdfByteString.FromOwnedBytes(bytes, appearance.Hex);
            graph.Add(24L); graph.Retain(); return result;
        }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException)) { return null; }
        finally { output?.Dispose(); }
    }
    private static void FixImportedSubtrees(
        OverlayFieldImport import,
        PdfNameTable<PdfName>? fontMap,
        PdfReference unionResources,
        PdfByteString? destDefaultAppearance,
        int? destQuadding,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken)
    {
        HashSet<int> visited = new();
        foreach (PdfReference field in import.Part.Fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!import.Copier.CopiedObjects.TryGetValue(field.ObjectNumber, out QpdfObject? raw) || raw is not PdfDictionary node)
            {
                continue;
            }

            PdfDictionary repaired = FixImportedNode(import.Copier, field.ObjectNumber, node, true, import, fontMap, unionResources, destDefaultAppearance, destQuadding, visited, limits, governor, diagnostics, cancellationToken, 0);
            if (!ReferenceEquals(repaired, node))
            {
                import.Copier.ReplaceCopiedObject(field.ObjectNumber, repaired);
            }
        }
    }

    private static PdfDictionary FixImportedNode(
        GraphCopier copier,
        int? nodeId,
        PdfDictionary node,
        bool isTop,
        OverlayFieldImport import,
        PdfNameTable<PdfName>? fontMap,
        PdfReference unionResources,
        PdfByteString? destDefaultAppearance,
        int? destQuadding,
        HashSet<int> visited,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        CancellationToken cancellationToken,
        int depth)
    {
        cancellationToken.ThrowIfCancellationRequested();
        governor.NoteOperation();
        if (depth > limits.MaxNestingDepth)
        {
            return node;
        }

        if (nodeId.HasValue && !visited.Add(nodeId.Value))
        {
            return node;
        }

        Dictionary<PdfName, QpdfObject> entries = new();
        bool changed = false;
        foreach (PdfName key in node.Keys)
        {
            if (!node.TryGet(key, out QpdfObject? entry) || entry is null)
            {
                continue;
            }

            governor.NoteOperation();
            string text = key.DisplayText;
            if (text == "Parent")
            {
                bool selfParent = isTop
                    && nodeId.HasValue
                    && entry is PdfReference selfReference
                    && selfReference.ObjectNumber == nodeId.Value;
                bool poolParent = entry is PdfReference parentReference
                    && copier.CopiedObjects.TryGetValue(parentReference.ObjectNumber, out QpdfObject? parentTarget)
                    && parentTarget is PdfDictionary;
                bool keep = selfParent || (!isTop && poolParent);
                if (keep)
                {
                    entries[key] = entry;
                    continue;
                }

                if (isTop && poolParent)
                {
                    diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Dropping an imported top-level parent link that closes a field cycle.");
                }
                else
                {
                    diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Dropping an imported parent link that does not resolve inside the copied pool.");
                }

                changed = true;
                continue;
            }

            if (text == "DR")
            {
                entries[key] = unionResources;
                changed = true;
                continue;
            }

            if (text == "DA" && entry is PdfByteString appearance && fontMap is { Count: > 0 })
            {
                PdfByteString? rewritten = RewriteOverlayDa(appearance, fontMap, limits, governor, cancellationToken);
                if (rewritten is not null)
                {
                    entries[key] = rewritten;
                    changed = true;
                    continue;
                }
            }

            entries[key] = entry;
        }

        ApplyImportDefaults(entries, node, copier, import, destDefaultAppearance, destQuadding, limits, governor, cancellationToken, ref changed);
        if (changed)
        {
            governor.ReserveMemory(64L);
            node = new PdfDictionary(entries);
        }

        if (!node.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw) || kidsRaw is null || kidsRaw is PdfNull)
        {
            return node;
        }

        QpdfObject? kidsTarget = kidsRaw is PdfReference kidsReference
            && copier.CopiedObjects.TryGetValue(kidsReference.ObjectNumber, out QpdfObject? kidsResolved)
            && kidsResolved is not null
            && kidsResolved is not PdfNull
            ? kidsResolved
            : kidsRaw;
        if (kidsTarget is not PdfArray kids)
        {
            return node;
        }

        List<QpdfObject>? rebuiltKids = null;
        for (int i = 0; i < kids.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (kids[i] is PdfReference kidReference
                && copier.CopiedObjects.TryGetValue(kidReference.ObjectNumber, out QpdfObject? kidRaw)
                && kidRaw is PdfDictionary kid)
            {
                PdfDictionary fixedKid = FixImportedNode(copier, kidReference.ObjectNumber, kid, false, import, fontMap, unionResources, destDefaultAppearance, destQuadding, visited, limits, governor, diagnostics, cancellationToken, depth + 1);
                if (!ReferenceEquals(fixedKid, kid))
                {
                    copier.ReplaceCopiedObject(kidReference.ObjectNumber, fixedKid);
                    kid = fixedKid;
                }

                if (nodeId.HasValue && !HasParentLink(kid))
                {
                    Dictionary<PdfName, QpdfObject> stamped = new();
                    foreach (PdfName kidKey in kid.Keys)
                    {
                        if (kid.TryGet(kidKey, out QpdfObject? kidEntry) && kidEntry is not null)
                        {
                            stamped[kidKey] = kidEntry;
                        }
                    }

                    governor.ReserveMemory(64L);
                    stamped[PdfName.FromAscii("Parent")] = new PdfReference(nodeId.Value, 0);
                    copier.ReplaceCopiedObject(kidReference.ObjectNumber, new PdfDictionary(stamped));
                    diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Correcting an imported field kid that is missing its parent link.");
                }

                if (rebuiltKids is not null)
                {
                    rebuiltKids.Add(kids[i]);
                }
            }
            else if (kids[i] is PdfDictionary directKid)
            {
                PdfDictionary fixedKid = FixImportedNode(copier, null, directKid, false, import, fontMap, unionResources, destDefaultAppearance, destQuadding, visited, limits, governor, diagnostics, cancellationToken, depth + 1);
                if (nodeId.HasValue && !HasParentLink(fixedKid))
                {
                    Dictionary<PdfName, QpdfObject> stamped = new();
                    foreach (PdfName kidKey in fixedKid.Keys)
                    {
                        if (fixedKid.TryGet(kidKey, out QpdfObject? kidEntry) && kidEntry is not null)
                        {
                            stamped[kidKey] = kidEntry;
                        }
                    }

                    governor.ReserveMemory(64L);
                    stamped[PdfName.FromAscii("Parent")] = new PdfReference(nodeId.Value, 0);
                    fixedKid = new PdfDictionary(stamped);
                    diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Correcting an imported field kid that is missing its parent link.");
                }

                if (ReferenceEquals(fixedKid, directKid))
                {
                    if (rebuiltKids is not null)
                    {
                        rebuiltKids.Add(kids[i]);
                    }
                }
                else if (rebuiltKids is null)
                {
                    rebuiltKids = new List<QpdfObject>(kids.Count);
                    for (int j = 0; j < i; j++)
                    {
                        rebuiltKids.Add(kids[j]);
                    }

                    rebuiltKids.Add(fixedKid);
                }
                else
                {
                    rebuiltKids.Add(fixedKid);
                }
            }
            else if (rebuiltKids is not null)
            {
                rebuiltKids.Add(kids[i]);
            }
        }

        if (rebuiltKids is null)
        {
            return node;
        }

        Dictionary<PdfName, QpdfObject> kidEntries = new();
        foreach (PdfName key in node.Keys)
        {
            if (key.DisplayText == "Kids")
            {
                continue;
            }

            if (node.TryGet(key, out QpdfObject? kept) && kept is not null)
            {
                kidEntries[key] = kept;
            }
        }

        governor.ReserveMemory(64L);
        kidEntries[PdfName.FromAscii("Kids")] = new PdfArray(rebuiltKids);
        return new PdfDictionary(kidEntries);
    }

    private static readonly PdfByteString EmptyAppearance = PdfByteString.FromOwnedBytes(Array.Empty<byte>(), false);

    private static bool SameAppearance(PdfByteString source, PdfByteString? target, ResourceGovernor governor, CancellationToken token)
    {
        if (ReferenceEquals(source, target)) { return true; }
        ReadOnlySpan<byte> left = source.ValueSpan, right = target is null ? ReadOnlySpan<byte>.Empty : target.ValueSpan;
        if (left.Length != right.Length) { return false; }
        for (int at = 0; at < left.Length; at += Math.Min(8192, left.Length - at))
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation(); int count = Math.Min(8192, left.Length - at);
            if (!left.Slice(at, count).SequenceEqual(right.Slice(at, count))) { return false; }
        }
        return true;
    }

    private static void ApplyImportDefaults(
        Dictionary<PdfName, QpdfObject> entries,
        PdfDictionary node,
        GraphCopier copier,
        OverlayFieldImport import,
        PdfByteString? destDefaultAppearance,
        int? destQuadding,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken,
        ref bool changed)
    {
        PdfName appearanceKey = PdfName.FromAscii("DA");
        if (import.SourceDefaultAppearance is PdfByteString appearance
            && !HasFieldValue(node, copier, "DA")
            && !HasInheritedFieldValue(node, copier, "DA", limits, governor)
            && !SameAppearance(appearance, destDefaultAppearance, governor, cancellationToken))
        {
            // /DA is a byte program, not a PDF text string. Exclusive source
            // values transfer to the job before their document closes.
            entries[appearanceKey] = appearance;
            changed = true;
        }

        PdfName quaddingKey = PdfName.FromAscii("Q");
        if (import.SourceQuadding.HasValue
            && import.SourceQuadding.Value != (destQuadding ?? 0)
            && !HasFieldValue(node, copier, "Q")
            && !HasInheritedFieldValue(node, copier, "Q", limits, governor))
        {
            string quaddingText = import.SourceQuadding.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            governor.ReserveMemory(32L);
            entries[quaddingKey] = new PdfInteger(quaddingText, import.SourceQuadding.Value);
            changed = true;
        }
    }

    private static bool HasParentLink(PdfDictionary node)
    {
        return node.TryGet(PdfName.FromAscii("Parent"), out QpdfObject? found) && found is not null && found is not PdfNull;
    }

    private static bool HasFieldValue(PdfDictionary node, GraphCopier copier, string key)
    {
        return node.TryGet(PdfName.FromAscii(key), out QpdfObject? found) && found is not null && found is not PdfNull;
    }

    private static bool HasInheritedFieldValue(
        PdfDictionary node,
        GraphCopier copier,
        string key,
        QpdfLimits limits,
        ResourceGovernor governor)
    {
        HashSet<int> seen = new();
        PdfDictionary cursor = node;
        int depth = 0;
        while (depth < 32)
        {
            depth += 1;
            governor.NoteOperation();
            if (!cursor.TryGet(PdfName.FromAscii("Parent"), out QpdfObject? parentRaw) || parentRaw is null || parentRaw is PdfNull)
            {
                return false;
            }

            QpdfObject? parentTarget = parentRaw is PdfReference parentReference
                && copier.CopiedObjects.TryGetValue(parentReference.ObjectNumber, out QpdfObject? resolved)
                && resolved is not null
                && resolved is not PdfNull
                ? resolved
                : parentRaw;
            if (parentTarget is not PdfDictionary parent)
            {
                return false;
            }

            if (parent.TryGet(PdfName.FromAscii(key), out QpdfObject? found) && found is not null && found is not PdfNull)
            {
                return true;
            }

            cursor = parent;
        }

        return false;
    }

    private static PdfByteString? ReadFieldText(GraphCopier copier, PdfDictionary node)
    {
        if (!node.TryGet(PdfName.FromAscii("T"), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return null;
        }
        if (raw is PdfReference textRef)
        {
            if (!copier.CopiedObjects.TryGetValue(textRef.ObjectNumber, out QpdfObject? resolved) || resolved is not PdfByteString resolvedText)
            {
                return null;
            }
            return resolvedText;
        }
        return raw as PdfByteString;
    }

    internal static HashSet<int> CollectAnnotRefs(
        IReadOnlyList<(GraphCopier Copier, int Id)> occurrences,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (occurrences is null)
        {
            throw new ArgumentNullException(nameof(occurrences));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        HashSet<int> found = new();
        foreach ((GraphCopier copier, int pageId) in occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (copier is null)
            {
                throw new ArgumentException("Occurrences must not contain null copiers.", nameof(occurrences));
            }

            if (!copier.CopiedObjects.TryGetValue(pageId, out QpdfObject? raw) || raw is not PdfDictionary page)
            {
                continue;
            }

            if (!page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject? annotsRaw) || annotsRaw is null || annotsRaw is PdfNull)
            {
                continue;
            }

            PdfArray? annots = ResolvePoolArray(copier.CopiedObjects, annotsRaw);
            if (annots is null)
            {
                continue;
            }

            for (int i = 0; i < annots.Count; i++)
            {
                governor.NoteOperation();
                if (annots[i] is PdfReference reference)
                {
                    found.Add(reference.ObjectNumber);
                }
            }
        }

        return found;
    }

    internal static PdfDictionary? BuildCatalogForm(
        CopiedForm combined,
        IReadOnlyDictionary<int, QpdfObject> pool,
        HashSet<int> survivingAnnots,
        bool pruneUnreachableFields,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (combined is null)
        {
            throw new ArgumentNullException(nameof(combined));
        }

        if (pool is null)
        {
            throw new ArgumentNullException(nameof(pool));
        }

        if (survivingAnnots is null)
        {
            throw new ArgumentNullException(nameof(survivingAnnots));
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
        if (combined.Fields.Count == 0)
        {
            if (!combined.HasFields)
            {
                return Assemble(combined, [], []);
            }

            return pruneUnreachableFields ? null : Assemble(combined, [], []);
        }

        (List<PdfReference> kept, HashSet<int> closure) = CollectKeptClosure(pool, combined.Fields, survivingAnnots, pruneUnreachableFields, limits, governor, cancellationToken);
        if (kept.Count == 0)
        {
            return null;
        }

        List<QpdfObject> order = new();
        if (combined.HasCalculationOrder)
        {
            foreach (QpdfObject item in combined.CalculationOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                if (item is PdfReference reference && !closure.Contains(reference.ObjectNumber))
                {
                    continue;
                }

                order.Add(item);
            }
        }

        return Assemble(combined, kept, order);
    }

    internal static HashSet<int> CollectRefNumbers(QpdfObject value, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        HashSet<int> found = new();
        Stack<QpdfObject> stack = new();
        stack.Push(value);
        while (stack.TryPop(out QpdfObject? current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (current is PdfReference reference)
            {
                found.Add(reference.ObjectNumber);
            }
            else if (current is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    stack.Push(array[i]);
                }
            }
            else if (current is PdfDictionary dictionary)
            {
                foreach (PdfName key in dictionary.Keys)
                {
                    if (dictionary.TryGet(key, out QpdfObject? entry) && entry is not null)
                    {
                        stack.Push(entry);
                    }
                }
            }
            else if (current is PdfStream stream)
            {
                stack.Push(stream.Dictionary);
            }
        }

        return found;
    }

    private static PdfDictionary Assemble(CopiedForm combined, IReadOnlyList<PdfReference> fields, IReadOnlyList<QpdfObject> order)
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        if (combined.HasFields)
        {
            entries[PdfName.FromAscii("Fields")] = new PdfArray(fields);
        }
        if (combined.HasNeedAppearances)
        {
            entries[PdfName.FromAscii("NeedAppearances")] = new PdfBoolean(combined.NeedAppearances);
        }

        if (combined.DefaultResources is not null)
        {
            entries[PdfName.FromAscii("DR")] = combined.DefaultResources;
        }

        if (combined.HasCalculationOrder)
        {
            entries[PdfName.FromAscii("CO")] = new PdfArray(order);
        }

        foreach (KeyValuePair<PdfName, QpdfObject> extra in combined.Extras)
        {
            if (!entries.ContainsKey(extra.Key))
            {
                entries[extra.Key] = extra.Value;
            }
        }

        return new PdfDictionary(entries);
    }

    /// <summary>
    /// Applies the shared keep-decision over destination-pool field registrations:
    /// a field survives when at least one widget in its subtree is among the surviving
    /// annotations, while memberless fields survive only on whole-file paths. Returns the
    /// surviving references plus the closure of surviving field and member identifiers, which
    /// also drives appearance-generation reachability for the same output.
    /// </summary>
    internal static (List<PdfReference> Kept, HashSet<int> Closure) CollectKeptClosure(
        IReadOnlyDictionary<int, QpdfObject> pool,
        IReadOnlyList<PdfReference> fields,
        HashSet<int> survivingAnnots,
        bool pruneUnreachableFields,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        List<PdfReference> kept = new(fields.Count);
        HashSet<int> closure = new();
        foreach (PdfReference field in fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (field is null)
            {
                continue;
            }

            HashSet<int> members = WalkPoolMembers(pool, field.ObjectNumber, limits, governor, cancellationToken);
            if (members.Count == 0 && !pruneUnreachableFields)
            {
                kept.Add(field);
                closure.Add(field.ObjectNumber);
                foreach (int id in CollectPoolSubtree(pool, field.ObjectNumber, limits, governor, cancellationToken))
                {
                    closure.Add(id);
                }

                continue;
            }

            bool survives = false;
            foreach (int member in members)
            {
                if (survivingAnnots.Contains(member))
                {
                    survives = true;
                    break;
                }
            }

            if (!survives)
            {
                continue;
            }

            kept.Add(field);
            closure.Add(field.ObjectNumber);
            foreach (int id in CollectPoolSubtree(pool, field.ObjectNumber, limits, governor, cancellationToken))
            {
                closure.Add(id);
            }
        }

        return (kept, closure);
    }

    /// <summary>
    /// Copies the source form default-resources dictionary into the pool for
    /// generation-time font lookups. Missing forms, missing resources, and
    /// unresolvable or structural values yield null so callers fall back to
    /// appearance-local resources; budget exhaustion and cancellation still
    /// fail loudly.
    /// </summary>
    internal static PdfDictionary? TryCopyFormDR(
        DocumentEngine engine,
        GraphCopier copier,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }
        if (copier is null)
        {
            throw new ArgumentNullException(nameof(copier));
        }
        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }
        cancellationToken.ThrowIfCancellationRequested();
        governor.NoteOperation();
        PdfDictionary? source = TryGetSourceForm(engine);
        if (source is null)
        {
            return null;
        }
        if (!source.TryGet(PdfName.FromAscii("DR"), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return null;
        }
        try
        {
            QpdfObject copied = copier.CopyValue(raw, 0);
            if (copied is PdfReference link && copier.CopiedObjects.TryGetValue(link.ObjectNumber, out QpdfObject? target) && target is PdfDictionary dictionary)
            {
                return dictionary;
            }
            return copied as PdfDictionary;
        }
        catch (QpdfParseException)
        {
            return null;
        }
    }

    internal static PdfDictionary? TryGetSourceForm(DocumentEngine engine)
    {
        if (!engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject? root) || root is null || root is PdfNull)
        {
            return null;
        }

        IIndirectResolver resolver = (IIndirectResolver)engine;
        PdfDictionary? catalog = null;
        if (root is PdfReference rootReference)
        {
            if (resolver.TryResolve(rootReference, out QpdfObject? resolved) && resolved is PdfDictionary resolvedCatalog)
            {
                catalog = resolvedCatalog;
            }
        }
        else
        {
            catalog = root as PdfDictionary;
        }

        if (catalog is null)
        {
            return null;
        }

        if (!catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject? form) || form is null || form is PdfNull)
        {
            return null;
        }

        if (form is PdfReference formReference)
        {
            if (resolver.TryResolve(formReference, out QpdfObject? resolved))
            {
                if (resolved is PdfDictionary resolvedForm) { return resolvedForm; }
                if (resolved is PdfArray) { return null; }
            }

            throw new QpdfParseException("Unresolvable catalog /AcroForm reference.", null, formReference.ObjectNumber);
        }

        if (form is PdfDictionary direct)
        {
            return direct;
        }

        // The reference preserves array-valued catalog entries without treating
        // them as interactive field dictionaries. CatalogCopy owns that graph.
        if (form is PdfArray) { return null; }

        throw new QpdfParseException("Catalog /AcroForm must be a dictionary.", null, null);
    }

    private static HashSet<int> CollectKeptWidgets(
        IIndirectResolver resolver,
        IReadOnlyList<PageLocation> keptPages,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        HashSet<int> found = new();
        foreach (PageLocation location in keptPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (location.Page is null)
            {
                continue;
            }

            if (!location.Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject? annotsRaw) || annotsRaw is null || annotsRaw is PdfNull)
            {
                continue;
            }

            QpdfObject annotsTarget = annotsRaw is PdfReference annotsReference
                && resolver.TryResolve(annotsReference, out QpdfObject? resolved)
                ? resolved ?? annotsRaw
                : annotsRaw;
            if (annotsTarget is not PdfArray annots)
            {
                continue;
            }

            for (int i = 0; i < annots.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                if (annots[i] is not PdfReference itemReference)
                {
                    continue;
                }

                if (!resolver.TryResolve(itemReference, out QpdfObject? target) || target is not PdfDictionary annot)
                {
                    continue;
                }

                if (IsWidget(annot))
                {
                    found.Add(itemReference.ObjectNumber);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Reports whether the source catalog form carries a /Fields array, either
    /// directly or through an indirect reference. The reference drops an emptied
    /// registration list on selection but keeps a registration-free form verbatim,
    /// so callers must distinguish a missing array from an emptied one.
    /// </summary>
    private static bool IsFieldArrayPresent(IIndirectResolver resolver, PdfDictionary source)
    {
        if (resolver is null)
        {
            throw new ArgumentNullException(nameof(resolver));
        }

        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (!source.TryGet(PdfName.FromAscii("Fields"), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return false;
        }

        if (raw is PdfArray)
        {
            return true;
        }

        return raw is PdfReference reference
            && resolver.TryResolve(reference, out QpdfObject? resolved)
            && resolved is PdfArray;
    }

    private static List<QpdfObject> FilterTopEntries(
        IIndirectResolver resolver,
        PdfDictionary source,
        HashSet<int> keptWidgets,
        bool pruneUnreachableFields,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        List<QpdfObject> kept = new();
        if (!source.TryGet(PdfName.FromAscii("Fields"), out QpdfObject? raw) || raw is null || raw is PdfNull)
        {
            return kept;
        }

        QpdfObject target = raw is PdfReference fieldsReference
            && resolver.TryResolve(fieldsReference, out QpdfObject? resolved)
            ? resolved ?? raw
            : raw;
        if (target is not PdfArray fields)
        {
            throw new QpdfParseException("Catalog /AcroForm /Fields must be an array.", null, null);
        }

        for (int i = 0; i < fields.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            QpdfObject entry = fields[i];
            if (entry is null || entry is PdfNull)
            {
                continue;
            }

            if (entry is not PdfReference reference)
            {
                kept.Add(entry);
                continue;
            }

            if (!resolver.TryResolve(reference, out QpdfObject? fieldTarget) || fieldTarget is null || fieldTarget is PdfNull)
            {
                throw new QpdfParseException("Unresolvable /AcroForm /Fields entry.", null, reference.ObjectNumber);
            }

            if (fieldTarget is not PdfDictionary field)
            {
                throw new QpdfParseException("Catalog /AcroForm /Fields entries must be dictionaries.", null, reference.ObjectNumber);
            }

            HashSet<int> members = WalkSourceMembers(resolver, field, reference.ObjectNumber, limits, governor, cancellationToken);
            if (members.Count == 0 && pruneUnreachableFields)
            {
                continue;
            }

            if (members.Count == 0)
            {
                kept.Add(entry);
                continue;
            }

            bool survives = false;
            foreach (int member in members)
            {
                if (keptWidgets.Contains(member))
                {
                    survives = true;
                    break;
                }
            }

            if (survives)
            {
                kept.Add(entry);
            }
        }

        return kept;
    }

    private static HashSet<int> WalkSourceMembers(
        IIndirectResolver resolver,
        PdfDictionary root,
        int rootNumber,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        HashSet<int> members = new();
        if (IsWidget(root))
        {
            members.Add(rootNumber);
        }

        HashSet<int> visited = new() { rootNumber };
        Stack<(PdfDictionary Node, int Depth)> stack = new();
        stack.Push((root, 0));
        while (stack.TryPop(out (PdfDictionary Node, int Depth) current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (current.Depth > limits.MaxNestingDepth)
            {
                continue;
            }

            if (!current.Node.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw) || kidsRaw is null || kidsRaw is PdfNull)
            {
                continue;
            }

            QpdfObject kidsTarget = kidsRaw is PdfReference kidsReference
                && resolver.TryResolve(kidsReference, out QpdfObject? kidsResolved)
                ? kidsResolved ?? kidsRaw
                : kidsRaw;
            if (kidsTarget is not PdfArray kids)
            {
                continue;
            }

            for (int i = 0; i < kids.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                if (kids[i] is not PdfReference kidReference)
                {
                    continue;
                }

                if (!visited.Add(kidReference.ObjectNumber))
                {
                    continue;
                }

                if (!resolver.TryResolve(kidReference, out QpdfObject? kidTarget) || kidTarget is not PdfDictionary kid)
                {
                    continue;
                }

                if (IsWidget(kid))
                {
                    members.Add(kidReference.ObjectNumber);
                }

                stack.Push((kid, current.Depth + 1));
            }
        }

        return members;
    }

    private static HashSet<int> WalkPoolMembers(
        IReadOnlyDictionary<int, QpdfObject> pool,
        int rootNumber,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        HashSet<int> members = new();
        if (!pool.TryGetValue(rootNumber, out QpdfObject? root) || root is not PdfDictionary rootDict)
        {
            return members;
        }

        if (IsWidget(rootDict))
        {
            members.Add(rootNumber);
        }

        HashSet<int> visited = new() { rootNumber };
        Stack<(PdfDictionary Node, int Depth)> stack = new();
        stack.Push((rootDict, 0));
        while (stack.TryPop(out (PdfDictionary Node, int Depth) current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (current.Depth > limits.MaxNestingDepth)
            {
                continue;
            }

            if (!current.Node.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw) || kidsRaw is null || kidsRaw is PdfNull)
            {
                continue;
            }

            PdfArray? kids = ResolvePoolArray(pool, kidsRaw);
            if (kids is null)
            {
                continue;
            }

            for (int i = 0; i < kids.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                if (kids[i] is not PdfReference kidReference)
                {
                    continue;
                }

                if (!visited.Add(kidReference.ObjectNumber))
                {
                    continue;
                }

                if (!pool.TryGetValue(kidReference.ObjectNumber, out QpdfObject? kidTarget) || kidTarget is not PdfDictionary kid)
                {
                    continue;
                }

                if (IsWidget(kid))
                {
                    members.Add(kidReference.ObjectNumber);
                }

                stack.Push((kid, current.Depth + 1));
            }
        }

        return members;
    }

    private static HashSet<int> CollectPoolSubtree(
        IReadOnlyDictionary<int, QpdfObject> pool,
        int rootNumber,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        HashSet<int> found = new();
        Stack<int> stack = new();
        stack.Push(rootNumber);
        while (stack.TryPop(out int number))
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!found.Add(number))
            {
                continue;
            }

            if (found.Count > limits.MaxContainerSize)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxContainerSize), found.Count, limits.MaxContainerSize);
            }

            if (!pool.TryGetValue(number, out QpdfObject? value) || value is null)
            {
                continue;
            }

            foreach (int id in CollectRefNumbers(value, governor, cancellationToken))
            {
                if (!found.Contains(id))
                {
                    stack.Push(id);
                }
            }
        }

        return found;
    }

    private static PdfArray? ResolvePoolArray(IReadOnlyDictionary<int, QpdfObject> pool, QpdfObject raw)
    {
        if (raw is PdfArray direct)
        {
            return direct;
        }

        if (raw is PdfReference reference
            && pool.TryGetValue(reference.ObjectNumber, out QpdfObject? resolved)
            && resolved is PdfArray array)
        {
            return array;
        }

        return null;
    }

    private static bool IsWidget(PdfDictionary dictionary)
    {
        return dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject? raw)
            && raw is PdfName name
            && name.DisplayText == "Widget";
    }
}
