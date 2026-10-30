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
    internal static bool HasFieldEntries(PdfDictionary node, ResourceGovernor governor, CancellationToken token)
    {
        using PdfDictionary.EntryEnumerator entries = node.GetEntryEnumerator();
        while (entries.MoveNext())
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (entries.Current.Value is PdfNull) { continue; }
            ReadOnlySpan<byte> key = entries.Current.Key.RawSpan;
            if (key.SequenceEqual("T"u8) || key.SequenceEqual("FT"u8) || key.SequenceEqual("TU"u8)
                || key.SequenceEqual("TM"u8) || key.SequenceEqual("Ff"u8) || key.SequenceEqual("V"u8)
                || key.SequenceEqual("DV"u8) || key.SequenceEqual("DA"u8) || key.SequenceEqual("Q"u8)
                || key.SequenceEqual("Opt"u8) || key.SequenceEqual("MaxLen"u8)) { return true; }
        }
        return false;
    }


    internal static CopiedForm? TryCopySourceForm(
        DocumentEngine engine,
        GraphCopier copier,
        IReadOnlyList<PageLocation> keptPages,
        bool pruneUnreachableFields,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope ownership,
        CancellationToken cancellationToken)
        => ReadSourceForm(engine, copier, keptPages, pruneUnreachableFields, limits,
            governor, ownership, cancellationToken, false);

    private static CopiedForm? ReadSourceForm(
        DocumentEngine engine, GraphCopier copier, IReadOnlyList<PageLocation> keptPages,
        bool pruneUnreachableFields, QpdfLimits limits, ResourceGovernor governor,
        ParserMemoryScope ownership, CancellationToken cancellationToken, bool retainSourceObjects)
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

        ownership.CheckOwner(governor);
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        limits.Validate();
        PdfDictionary? source = TryGetSourceForm(engine);
        if (source is null)
        {
            return null;
        }

        using ParserMemoryScope result = ParserMemoryScope.Create(governor);
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        IIndirectResolver resolver = (IIndirectResolver)engine;
        HashSet<int> keptWidgets = CollectKeptWidgets(resolver, keptPages, governor, scratch, cancellationToken);
        bool hasFields = IsFieldArrayPresent(resolver, source);
        List<QpdfObject> keptEntries = FilterTopEntries(resolver, source, keptWidgets, pruneUnreachableFields, limits, governor, scratch, cancellationToken);
        List<PdfReference> copiedFields = ParserContainers.CreateList<PdfReference>(result);
        foreach (QpdfObject entry in keptEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!retainSourceObjects) { copier.ValidateFieldCopy(entry); }
            if (entry is PdfReference)
            {
                if ((retainSourceObjects ? copier.RetainSourceValue(entry) : copier.CopyValue(entry, 0)) is PdfReference copiedReference)
                {
                    ParserContainers.AddItem(result, copiedFields, copiedReference, IntPtr.Size);
                }

                continue;
            }

            PdfReference link = retainSourceObjects
                ? copier.AddIndirectObjectWithId(copier.RetainSourceValue(entry), copier.NextId)
                : copier.AddIndirectObject(entry) ?? throw new QpdfException("Form field copy pruned unexpectedly.");
            ParserContainers.AddItem(result, copiedFields, link, IntPtr.Size);
        }
        bool hasNeed = false;
        bool need = false;
        QpdfObject? resources = null;
        bool hasOrder = false;
        IReadOnlyList<QpdfObject> order = Array.Empty<QpdfObject>();
        Dictionary<PdfName, QpdfObject> extras = ParserContainers.CreateDictionary<PdfName, QpdfObject>(result);
        using PdfDictionary.EntryEnumerator sourceEntries = source.GetEntryEnumerator();
        while (sourceEntries.MoveNext(governor, cancellationToken))
        {
            PdfName key = sourceEntries.Current.Key;
            QpdfObject raw = sourceEntries.Current.Value;
            ReadOnlySpan<byte> text = key.RawSpan;
            if (text.SequenceEqual("Fields"u8))
            {
                continue;
            }

            if (raw is null || raw is PdfNull)
            {
                continue;
            }

            if (text.SequenceEqual("NeedAppearances"u8)
                && CatalogCopy.ResolveValue(raw, engine, governor, cancellationToken) is PdfBoolean flag)
            {
                hasNeed = true;
                need = flag.Value;
                continue;
            }

            QpdfObject copied = retainSourceObjects ? copier.RetainSourceValue(raw) : copier.CopyValue(raw, 0);
            if (text.SequenceEqual("DR"u8))
            {
                resources = copied;
                continue;
            }

            if (text.SequenceEqual("CO"u8) && copied is PdfArray copiedOrder)
            {
                hasOrder = true;
                order = copiedOrder.BorrowedItems;

                continue;
            }

            ParserContainers.EnsureEntryCapacity(result, extras, IntPtr.Size);
            extras[key] = copied;
        }

        result.Reserve(80L);
        CopiedForm form = new(copiedFields, hasFields, hasNeed, need, resources, hasOrder, order, extras);
        result.TransferTo(ownership); return form;
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
        ParserMemoryScope ownership,
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

        ownership.CheckOwner(governor);
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        CopiedForm? part = ReadSourceForm(engine, copier, keptPages, pruneUnreachableFields, limits,
            governor, scratch, cancellationToken, retainSourceObjects);
        foreach (KeyValuePair<int, QpdfObject> entry in copier.CopiedObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (pool.TryGetValue(entry.Key, out QpdfObject? existing) && !ReferenceEquals(existing, entry.Value))
            {
                throw new QpdfException("Form copy encountered conflicting output object identities.");
            }
            if (existing is null)
            {
                ParserContainers.EnsureEntryCapacity(ownership, pool, IntPtr.Size);
                pool.Add(entry.Key, entry.Value);
            }
        }
        if (part is null && overlayImports.Count == 0)
        {
            return null;
        }

        List<CopiedForm?> allForms = ParserContainers.CreateList<CopiedForm?>(scratch);
        List<GraphCopier> allCopiers = ParserContainers.CreateList<GraphCopier>(scratch);
        if (part is not null)
        {
            ParserContainers.AddItem(scratch, allForms, part, IntPtr.Size);
            ParserContainers.AddItem(scratch, allCopiers, copier, IntPtr.Size);
        }

        foreach (OverlayFieldImport import in overlayImports)
        {
            if (import is null)
            {
                throw new ArgumentException("Overlay imports must not contain null entries.", nameof(overlayImports));
            }

            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            ParserContainers.AddItem(scratch, allForms, import.Part, IntPtr.Size);
            ParserContainers.AddItem(scratch, allCopiers, import.Copier, IntPtr.Size);
        }

        RenameCollidingFields(allForms, allCopiers, limits, governor, cancellationToken);
        // Renaming replaces copier headers; publish those owned identities into
        // the earlier pool snapshot before defaults and field fixups read it.
        foreach (GraphCopier sourceCopier in allCopiers)
        {
            foreach (KeyValuePair<int, QpdfObject> entry in sourceCopier.CopiedObjects)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                pool[entry.Key] = entry.Value;
            }
        }

        CopiedForm? combined = Combine(allForms, governor, scratch, cancellationToken);
        if (combined is null)
        {
            return null;
        }

        if (overlayImports.Count > 0)
        {
            (PdfByteString? destAppearance, int? destQuadding) = ReadMergedDefaults(combined, pool, governor, cancellationToken);
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

        HashSet<int> survivors = CollectAnnotRefs(occurrences, governor, scratch, cancellationToken);
        return BuildCatalogForm(combined, pool, survivors, pruneUnreachableFields, limits, governor, cancellationToken);
    }

    internal static CopiedForm? Combine(IReadOnlyList<CopiedForm?> parts, ResourceGovernor governor,
        ParserMemoryScope ownership, CancellationToken token)
    {
        if (parts is null)
        {
            throw new ArgumentNullException(nameof(parts));
        }

        ownership.CheckOwner(governor);
        int count = 0; CopiedForm? only = null;
        foreach (CopiedForm? part in parts)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (part is not null) { count++; only = part; }
        }
        if (count < 2) { return only; }
        using ParserMemoryScope result = ParserMemoryScope.Create(governor);
        List<PdfReference> fields = ParserContainers.CreateList<PdfReference>(result);
        bool hasFields = false;
        bool hasNeed = false;
        bool need = false;
        QpdfObject? resources = null;
        bool hasOrder = false;
        List<QpdfObject> order = ParserContainers.CreateList(result);
        Dictionary<PdfName, QpdfObject> extras = ParserContainers.CreateDictionary<PdfName, QpdfObject>(result);
        foreach (CopiedForm? part in parts)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (part is null) { continue; }
            foreach (PdfReference field in part.Fields)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddItem(result, fields, field, IntPtr.Size);
            }
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
                foreach (QpdfObject item in part.CalculationOrder)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    ParserContainers.AddItem(result, order, item);
                }
            }

            foreach (KeyValuePair<PdfName, QpdfObject> extra in part.Extras)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (!extras.ContainsKey(extra.Key))
                {
                    ParserContainers.EnsureEntryCapacity(result, extras, IntPtr.Size);
                    extras[extra.Key] = extra.Value;
                }
            }
        }

        result.Reserve(80L);
        CopiedForm form = new(fields, hasFields, hasNeed, need, resources, hasOrder, order, extras);
        result.TransferTo(ownership); return form;
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
        Dictionary<string, int> suffixes = ParserContainers.CreateDictionary<string, int>(names);
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
                PdfDictionary renamed = RenameTree(copier, fieldRef.ObjectNumber, node, null, accumulated, suffixes, finals, visited,
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
        HashSet<string> snapshot, Dictionary<string, int> suffixes, List<string> finals, GovernedIntSet visited,
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
                // Earlier-source names only accumulate, so a prior free suffix
                // is a lower bound. Recheck it: this source's duplicates must
                // still pick the same free name until its finals are accumulated.
                bool hasSuffix = suffixes.TryGetValue(qualified, out int suffix);
                suffix = hasSuffix ? suffix - 1 : 0;
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
                    if (!hasSuffix) { ParserContainers.EnsureEntryCapacity(names, suffixes, sizeof(int)); }
                    suffixes[qualified] = suffix;
                    candidate.TransferTo(names);
                    using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
                    int length = PdfText.GetEncodedLength(renamedOwn, governor, token);
                    graph.Memory.Reserve(checked(32L + length));
                    byte[] raw = new byte[length];
                    PdfText.Encode(renamedOwn, raw, governor, token);
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
            { RenameTree(copier, kidRef.ObjectNumber, kid, qualified, snapshot, suffixes, finals, visited, names, traversal, limits, governor, token, depth + 1); }
            else if (item is PdfDictionary directKid)
            {
                QpdfObject renamed = RenameTree(copier, null, directKid, qualified, snapshot, suffixes, finals, visited, names, traversal, limits, governor, token, depth + 1);
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
    /// The resolved import top for an overlay source widget: its source object
    /// reference plus the walk outcome. A null reference with
    /// <c>DirectTop</c> means the walk ended at a direct dictionary, which the
    /// reference refuses to import; <c>Cycled</c> marks a repeated parent link,
    /// which the reference still imports with a warning.
    /// </summary>
    internal sealed record OverlayTopMatch(PdfReference? Reference, bool Cycled, bool DirectTop);

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
    /// Parent edges count from zero at the widget; exceeding the governor's
    /// nesting limit fails instead of returning an intermediate field.
    /// </summary>
    internal static OverlayTopMatch? FindOverlayTopField(
        PdfDictionary annot,
        PdfReference? annotationReference,
        bool sourceHasFields,
        IIndirectResolver source,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(annot);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(governor);
        cancellationToken.ThrowIfCancellationRequested();
        governor.CheckCancellation();
        if (annotationReference is null || !sourceHasFields)
        {
            return null;
        }

        PdfDictionary cursor = annot;
        PdfReference? cursorReference = annotationReference;
        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        GovernedIntSet seen = GovernedIntSet.Create(traversal, governor, cancellationToken);
        seen.Add(traversal, annotationReference.ObjectNumber);
        bool cycled = false;
        bool first = true;
        int depth = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.CheckCancellation();
            if (!PdfDictionaryLookup.TryGet(cursor, "Parent"u8, governor, cancellationToken, out QpdfObject parentRaw) || parentRaw is PdfNull)
            {
                break;
            }

            if (parentRaw is PdfReference parentReference)
            {
                if (!PdfObjectLookup.TryResolve(parentReference, source, governor, cancellationToken, out QpdfObject? resolved)
                    || resolved is not PdfDictionary parentDict)
                {
                    break;
                }

                if (seen.Contains(parentReference.ObjectNumber))
                {
                    if (!first || parentReference.ObjectNumber != annotationReference.ObjectNumber)
                    {
                        cycled = true;
                    }

                    break;
                }

                first = false;
                CheckParentDepth();
                seen.Add(traversal, parentReference.ObjectNumber);
                cursor = parentDict;
                cursorReference = parentReference;
                continue;
            }

            if (parentRaw is PdfDictionary parentDirect)
            {
                CheckParentDepth();
                first = false;
                cursor = parentDirect;
                cursorReference = null;
                continue;
            }

            break;
        }

        cancellationToken.ThrowIfCancellationRequested();
        governor.CheckCancellation();
        if (!HasFieldIdentity(cursor, governor, cancellationToken))
        {
            return null;
        }

        if (cursorReference is null)
        {
            return new OverlayTopMatch(null, cycled, true);
        }

        return new OverlayTopMatch(cursorReference, cycled, false);

        void CheckParentDepth()
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.CheckCancellation();
            depth++;
            if (depth > governor.MaxNestingDepth)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth, governor.MaxNestingDepth);
            }
        }
    }

    /// <summary>
    /// Reports whether a dictionary carries field identity: a partial name,
    /// a kid array, or a field type, mirroring the reference field check.
    /// </summary>
    private static bool HasFieldIdentity(PdfDictionary node, ResourceGovernor governor, CancellationToken token)
    {
        if (node is null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        return (PdfDictionaryLookup.TryGet(node, "T"u8, governor, token, out QpdfObject title) && title is not PdfNull)
            || (PdfDictionaryLookup.TryGet(node, "Kids"u8, governor, token, out QpdfObject kids) && kids is not PdfNull)
            || (PdfDictionaryLookup.TryGet(node, "FT"u8, governor, token, out QpdfObject type) && type is not PdfNull);
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
        DocumentEngine engine, QpdfLimits limits, ResourceGovernor governor,
        ParserMemoryScope ownership, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine); ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(governor); ownership.CheckOwner(governor);
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); limits.Validate();
        PdfDictionary? source;
        try { source = TryGetSourceForm(engine); }
        catch (QpdfParseException) { return null; }
        if (source is null) { return null; }

        using ParserMemoryScope result = ParserMemoryScope.Create(governor);
        result.Reserve(72L);
        bool hasFields = PdfDictionaryLookup.TryGet(source, "Fields"u8, governor, cancellationToken, out QpdfObject fieldsRaw)
            && fieldsRaw is not PdfNull;
        PdfByteString appearance = PdfDictionaryLookup.TryGet(source, "DA"u8, governor, cancellationToken, out QpdfObject daRaw)
            && CatalogCopy.ResolveValue(daRaw, engine, governor, cancellationToken) is PdfByteString da ? da : EmptyAppearance;
        int quadding = PdfDictionaryLookup.TryGet(source, "Q"u8, governor, cancellationToken, out QpdfObject qRaw)
            && CatalogCopy.ResolveValue(qRaw, engine, governor, cancellationToken) is PdfInteger q
            && q.Value is >= int.MinValue and <= int.MaxValue ? (int)q.Value : 0;
        bool need = PdfDictionaryLookup.TryGet(source, "NeedAppearances"u8, governor, cancellationToken, out QpdfObject needRaw)
            && CatalogCopy.ResolveValue(needRaw, engine, governor, cancellationToken) is PdfBoolean flag && flag.Value;
        QpdfObject? resources = PdfDictionaryLookup.TryGet(source, "DR"u8, governor, cancellationToken, out QpdfObject drRaw)
            && drRaw is not PdfNull ? drRaw : null;
        (HashSet<int> reached, bool fieldsNotArray, bool hasDirectNodes) = CollectReachedFieldObjects(
            engine, fieldsRaw, hasFields, limits, governor, result, cancellationToken);
        OverlaySourceForm form = new(hasFields, appearance, quadding, need, resources, reached, fieldsNotArray, hasDirectNodes);
        result.TransferTo(ownership); return form;
    }

    private static (HashSet<int> Reached, bool FieldsNotArray, bool HasDirectNodes) CollectReachedFieldObjects(
        DocumentEngine engine, QpdfObject fieldsRaw, bool hasFields, QpdfLimits limits,
        ResourceGovernor governor, ParserMemoryScope result, CancellationToken token)
    {
        HashSet<int> reached = ParserContainers.CreateSet<int>(result);
        if (!hasFields) { return (reached, false, false); }
        IIndirectResolver resolver = engine;
        QpdfObject target = fieldsRaw;
        if (target is PdfReference link)
        {
            if (!resolver.TryResolve(link, out QpdfObject? resolved) || resolved is null || resolved is PdfNull)
            { return (reached, true, false); }
            target = resolved;
        }
        if (target is not PdfArray fields) { return (reached, true, false); }

        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        HashSet<int> visited = ParserContainers.CreateSet<int>(scratch);
        List<(PdfArray Nodes, int Next, int Depth)> frames = ParserContainers.CreateList<(PdfArray, int, int)>(scratch);
        ParserContainers.AddItem(scratch, frames, (fields, 0, 0), 16L);
        bool hasDirectNodes = false;
        while (frames.Count != 0)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.NoteOperation();
            var frame = frames[^1];
            if (frame.Next == frame.Nodes.Count) { frames.RemoveAt(frames.Count - 1); continue; }
            // Preserve the former stack's last-to-first order without queuing
            // a complete array of pending references.
            QpdfObject node = frame.Nodes[frame.Nodes.Count - 1 - frame.Next];
            frames[^1] = (frame.Nodes, frame.Next + 1, frame.Depth);
            if (node is not PdfReference reference)
            { if (node is PdfDictionary) { hasDirectNodes = true; } continue; }
            if (!ParserContainers.AddSetItem(scratch, visited, reference.ObjectNumber, sizeof(int))) { continue; }
            if (!resolver.TryResolve(reference, out QpdfObject? resolvedNode) || resolvedNode is not PdfDictionary dictionary) { continue; }
            ParserContainers.AddSetItem(result, reached, reference.ObjectNumber, sizeof(int));
            if (frame.Depth >= limits.MaxNestingDepth
                || !PdfDictionaryLookup.TryGet(dictionary, "Kids"u8, governor, token, out QpdfObject kidsRaw) || kidsRaw is PdfNull) { continue; }
            QpdfObject kidsTarget = kidsRaw;
            if (kidsRaw is PdfReference kidsLink)
            {
                if (!resolver.TryResolve(kidsLink, out QpdfObject? kidsResolved) || kidsResolved is null || kidsResolved is PdfNull) { continue; }
                kidsTarget = kidsResolved;
            }
            if (kidsTarget is PdfArray kids)
            { ParserContainers.AddItem(scratch, frames, (kids, 0, frame.Depth + 1), 16L); }
        }
        return (reached, false, hasDirectNodes);
    }

    /// <summary>
    /// Reads the merged default appearance and quadding back out of combined
    /// form extras, so overlay imports compare against the destination values
    /// the reference would inherit. Absent or mistyped entries yield null.
    /// </summary>
    internal static (PdfByteString? Appearance, int? Quadding) ReadMergedDefaults(CopiedForm combined,
        IReadOnlyDictionary<int, QpdfObject> pool, ResourceGovernor governor, CancellationToken token)
    {
        if (combined is null)
        {
            throw new ArgumentNullException(nameof(combined));
        }

        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        PdfByteString? appearance = null;
        int? quadding = null;
        combined.Extras.TryGetValue(PdfName.FromAscii("DA"), out QpdfObject? appearanceRaw);
        if (appearanceRaw is PdfReference appearanceReference)
        { PdfObjectLookup.TryResolve(appearanceReference, pool, governor, token, out appearanceRaw); }
        combined.Extras.TryGetValue(PdfName.FromAscii("Q"), out QpdfObject? quaddingRaw);
        if (quaddingRaw is PdfReference quaddingReference)
        { PdfObjectLookup.TryResolve(quaddingReference, pool, governor, token, out quaddingRaw); }
        if (appearanceRaw is PdfByteString appearanceBytes)
        {
            appearance = appearanceBytes;
        }

        if (quaddingRaw is PdfInteger quaddingInteger
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

        using OwnedAllocationBatch unionGraph = OwnedAllocationBatch.Create(governor, cancellationToken);
        long unionStart = unionGraph.Memory.ReservedBytes;
        Dictionary<PdfName, QpdfObject> union = ParserContainers.CreateDictionary<PdfName, QpdfObject>(unionGraph.Memory);
        PdfDictionary? baseResources = combined.DefaultResources as PdfDictionary;
        if (combined.DefaultResources is PdfReference baseReference
            && pool.TryGetValue(baseReference.ObjectNumber, out QpdfObject? baseTarget)) { baseResources = baseTarget as PdfDictionary; }
        if (baseResources is not null)
        {
            using PdfDictionary.EntryEnumerator entries = baseResources.GetEntryEnumerator();
            while (entries.MoveNext(governor, cancellationToken))
            {
                ParserContainers.EnsureEntryCapacity(unionGraph.Memory, union, IntPtr.Size);
                union.Add(entries.Current.Key, entries.Current.Value);
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

            ParserContainers.AddItem(mapMemory, maps, MergeResourceDictionaries(union, sourceDict, import.Copier, unionGraph.Memory, mapMemory, governor, cancellationToken), IntPtr.Size);
        }

        unionGraph.Add(unionGraph.Memory.ReservedBytes - unionStart);
        unionGraph.Memory.Reserve(136L); unionGraph.Add(136L); // Dictionary, reference and CopiedForm wrappers.
        int unionId = allocator; allocator = checked(allocator + 1);
        PdfReference unionRef = unionOwner.AddIndirectObjectWithId(PdfDictionary.FromOwnedEntries(union), unionId);
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

        HashSet<GraphCopier> owners = ParserContainers.CreateSet<GraphCopier>(mapMemory);
        ParserContainers.AddSetItem(mapMemory, owners, unionOwner, IntPtr.Size);
        foreach (OverlayFieldImport import in imports)
        { ParserContainers.AddSetItem(mapMemory, owners, import.Copier, IntPtr.Size); }

        foreach (GraphCopier owner in owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            foreach (KeyValuePair<int, QpdfObject> entry in owner.CopiedObjects)
            {
                pool[entry.Key] = entry.Value;
            }
        }

        unionGraph.Retain(); return updated;
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
        ParserMemoryScope unionMemory,
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
                if (!union.ContainsKey(rtype)) { ParserContainers.EnsureEntryCapacity(unionMemory, union, IntPtr.Size); }
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
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (other.Count == 0) { union[rtype] = having; return; }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        List<QpdfObject>? additions = null;
        for (int i = 0; i < other.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            QpdfObject incoming = other[i];
            if (incoming is not (PdfName or PdfByteString or PdfInteger or PdfReal or PdfBoolean)) { continue; }
            bool seen = false;
            for (int j = 0; j < having.Count; j++)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (having[j].Equals(incoming)) { seen = true; break; }
            }
            if (!seen && additions is not null)
            {
                for (int j = 0; j < additions.Count; j++)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    if (additions[j].Equals(incoming)) { seen = true; break; }
                }
            }
            if (seen) { continue; }
            additions ??= ParserContainers.CreateList<QpdfObject>(scratch);
            ParserContainers.AddItem(scratch, additions, incoming);
        }
        if (additions is null) { union[rtype] = having; return; }
        long count = (long)having.Count + additions.Count;
        if (count > Array.MaxLength) { throw new QpdfLimitException("Resource array entries", count, Array.MaxLength); }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellationToken);
        long bytes = checked(32L + count * IntPtr.Size);
        graph.Memory.Reserve(bytes); QpdfObject[] items = new QpdfObject[(int)count]; graph.Add(bytes);
        for (int i = 0; i < items.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            items[i] = i < having.Count ? having[i] : additions[i - having.Count];
        }
        graph.Memory.Reserve(24L); PdfArray result = PdfArray.FromOwnedItems(items); graph.Add(24L);
        graph.Retain(); union[rtype] = result;
    }

    internal static PdfByteString? RewriteOverlayDa(PdfByteString appearance, PdfNameTable<PdfName>? fontMap,
        QpdfLimits limits, OwnedAllocationBatch graph)
    {
        ResourceGovernor governor = graph.Governor; CancellationToken token = graph.Token;
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (fontMap is null || fontMap.Count == 0) { return null; }
        GovernedBufferStream? output = null;
        try
        {
            ContentTokenizer tokenizer = new(appearance.ValueMemory, governor, token);
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
            byte[] bytes = output.ToArray(graph.Memory, token);
            graph.Add(bytes.Length == 0 ? 0L : bytes.LongLength + GovernedBufferStream.ArrayOverheadBytes);
            graph.Memory.Reserve(24L);
            PdfByteString result = PdfByteString.FromOwnedBytes(bytes, appearance.Hex);
            graph.Add(24L); return result;
        }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException)) { return null; }
        finally { output?.Dispose(); }
    }
    private static void FixImportedSubtrees(
        OverlayFieldImport import, PdfNameTable<PdfName>? fontMap, PdfReference unionResources,
        PdfByteString? destDefaultAppearance, int? destQuadding, QpdfLimits limits,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        HashSet<int> visited = ParserContainers.CreateSet<int>(traversal);
        foreach (PdfReference field in import.Part.Fields)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (!import.Copier.CopiedObjects.TryGetValue(field.ObjectNumber, out QpdfObject? raw) || raw is not PdfDictionary node) { continue; }
            PdfDictionary repaired = FixImportedNode(import.Copier, field.ObjectNumber, node, true, import, fontMap,
                unionResources, destDefaultAppearance, destQuadding, visited, traversal, limits, governor, diagnostics, token, 0);
            if (!ReferenceEquals(repaired, node)) { import.Copier.ReplaceCopiedObject(field.ObjectNumber, repaired); }
        }
    }

    private static PdfDictionary FixImportedNode(
        GraphCopier copier, int? nodeId, PdfDictionary node, bool isTop, OverlayFieldImport import,
        PdfNameTable<PdfName>? fontMap, PdfReference unionResources, PdfByteString? destDefaultAppearance,
        int? destQuadding, HashSet<int> visited, ParserMemoryScope traversal, QpdfLimits limits,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token, int depth)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (depth > limits.MaxNestingDepth) { return node; }
        if (nodeId.HasValue && !ParserContainers.AddSetItem(traversal, visited, nodeId.Value, sizeof(int))) { return node; }
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        bool removeParent = false;
        if (PdfDictionaryLookup.TryGet(node, "Parent"u8, governor, token, out QpdfObject parent))
        {
            bool self = isTop && nodeId.HasValue && parent is PdfReference selfLink && selfLink.ObjectNumber == nodeId.Value;
            bool inPool = parent is PdfReference parentLink && copier.CopiedObjects.TryGetValue(parentLink.ObjectNumber, out QpdfObject? parentNode)
                && parentNode is PdfDictionary;
            removeParent = !self && (isTop || !inPool);
            if (removeParent)
            { diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, isTop && inPool
                ? "Dropping an imported top-level parent link that closes a field cycle."
                : "Dropping an imported parent link that does not resolve inside the copied pool."); }
        }
        bool replaceResources = PdfDictionaryLookup.TryGet(node, "DR"u8, governor, token, out _);
        bool hasDa = PdfDictionaryLookup.TryGet(node, "DA"u8, governor, token, out QpdfObject da);
        PdfByteString? correctedDa = da is PdfByteString appearance && fontMap is { Count: > 0 }
            ? RewriteOverlayDa(appearance, fontMap, limits, graph) : null;
        if (import.SourceDefaultAppearance is PdfByteString sourceDa && (!hasDa || da is PdfNull)
            && !HasInheritedFieldValue(node, copier, "DA"u8, governor, token)
            && !SameAppearance(sourceDa, destDefaultAppearance, governor, token)) { correctedDa = sourceDa; }
        bool hasQ = PdfDictionaryLookup.TryGet(node, "Q"u8, governor, token, out QpdfObject q);
        PdfInteger? correctedQ = null;
        if (import.SourceQuadding is int sourceQ && sourceQ != (destQuadding ?? 0) && (!hasQ || q is PdfNull)
            && !HasInheritedFieldValue(node, copier, "Q"u8, governor, token))
        {
            graph.Memory.Reserve(32L + GovernedText.StringBytes(11));
            string text = sourceQ.ToString(System.Globalization.CultureInfo.InvariantCulture);
            correctedQ = new(text, sourceQ); graph.Add(32L + GovernedText.StringBytes(text.Length));
        }

        QpdfObject[]? rebuiltKids = null;
        if (PdfDictionaryLookup.TryGet(node, "Kids"u8, governor, token, out QpdfObject kidsRaw)
            && ResolveImportValue(copier, kidsRaw) is PdfArray kids)
        {
            for (int i = 0; i < kids.Count; i++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation(); QpdfObject item = kids[i];
                int? kidId = (item as PdfReference)?.ObjectNumber;
                if (ResolveImportValue(copier, item) is not PdfDictionary kid) { continue; }
                PdfDictionary fixedKid = FixImportedNode(copier, kidId, kid, false, import, fontMap, unionResources,
                    destDefaultAppearance, destQuadding, visited, traversal, limits, governor, diagnostics, token, depth + 1);
                if (nodeId.HasValue && (!PdfDictionaryLookup.TryGet(fixedKid, "Parent"u8, governor, token, out QpdfObject fixedParent) || fixedParent is PdfNull))
                {
                    graph.Memory.Reserve(32L); PdfReference parentLink = new(nodeId.Value, 0); graph.Add(32L);
                    fixedKid = PdfDictionaryRebuild.ReplaceEntry(graph, fixedKid, "Parent"u8, parentLink);
                    diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Correcting an imported field kid that is missing its parent link.");
                }
                if (ReferenceEquals(fixedKid, kid)) { continue; }
                if (kidId.HasValue) { copier.ReplaceCopiedObject(kidId.Value, fixedKid); continue; }
                if (rebuiltKids is null)
                {
                    long bytes = checked(32L + IntPtr.Size * (long)kids.Count);
                    graph.Memory.Reserve(bytes); rebuiltKids = new QpdfObject[kids.Count]; graph.Add(bytes);
                    for (int at = 0; at < kids.Count; at++)
                    { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); rebuiltKids[at] = kids[at]; }
                }
                rebuiltKids[i] = fixedKid;
            }
        }
        PdfArray? correctedKids = null;
        if (rebuiltKids is not null)
        { graph.Memory.Reserve(24L); correctedKids = PdfArray.FromOwnedItems(rebuiltKids); graph.Add(24L); }
        PdfDictionary result = node;
        if (removeParent || replaceResources || correctedDa is not null || correctedQ is not null || correctedKids is not null)
        {
            PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
            using PdfDictionary.EntryEnumerator source = node.GetEntryEnumerator();
            while (source.MoveNext(governor, token))
            {
                PdfName key = source.Current.Key; QpdfObject value = source.Current.Value;
                if (removeParent && key.RawSpan.SequenceEqual("Parent"u8)) { continue; }
                if (replaceResources && key.RawSpan.SequenceEqual("DR"u8)) { value = unionResources; }
                else if (correctedDa is not null && key.RawSpan.SequenceEqual("DA"u8)) { value = correctedDa; }
                else if (correctedQ is not null && key.RawSpan.SequenceEqual("Q"u8)) { value = correctedQ; }
                else if (correctedKids is not null && key.RawSpan.SequenceEqual("Kids"u8)) { value = correctedKids; }
                entries.Assign(graph.Memory, governor, key, value, token);
            }
            if (correctedDa is not null && !hasDa)
            { entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "DA"u8), correctedDa, token); }
            if (correctedQ is not null && !hasQ)
            { entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "Q"u8), correctedQ, token); }
            result = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        }
        if (graph.Bytes != 0L) { graph.Retain(); }
        return result;
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

    private static bool HasInheritedFieldValue(PdfDictionary node, GraphCopier copier, ReadOnlySpan<byte> key,
        ResourceGovernor governor, CancellationToken token)
    {
        PdfDictionary cursor = node;
        for (int depth = 0; depth < 32; depth++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (!PdfDictionaryLookup.TryGet(cursor, "Parent"u8, governor, token, out QpdfObject raw)
                || ResolveImportValue(copier, raw) is not PdfDictionary parent) { return false; }
            if (PdfDictionaryLookup.TryGet(parent, key, governor, token, out QpdfObject value) && value is not PdfNull) { return true; }
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
        ParserMemoryScope ownership,
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

        ownership.CheckOwner(governor);
        using ParserMemoryScope result = ParserMemoryScope.Create(governor);
        HashSet<int> found = ParserContainers.CreateSet<int>(result);
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
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                if (annots[i] is PdfReference reference)
                {
                    ParserContainers.AddSetItem(result, found, reference.ObjectNumber, sizeof(int));
                }
            }
        }

        result.TransferTo(ownership); return found;
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
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (combined.Fields.Count == 0)
        {
            if (!combined.HasFields)
            {
                return Assemble(combined, [], null, governor, cancellationToken);
            }

            return pruneUnreachableFields ? null : Assemble(combined, [], null, governor, cancellationToken);
        }

        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        (List<PdfReference> kept, HashSet<int> closure) = CollectKeptClosure(pool, combined.Fields, survivingAnnots, pruneUnreachableFields, limits, governor, scratch, cancellationToken);
        if (kept.Count == 0)
        {
            return null;
        }

        return Assemble(combined, kept, closure, governor, cancellationToken);
    }

    internal static HashSet<int> CollectRefNumbers(QpdfObject value, ResourceGovernor governor,
        ParserMemoryScope ownership, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        ownership.CheckOwner(governor);
        using ParserMemoryScope result = ParserMemoryScope.Create(governor);
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        HashSet<int> found = ParserContainers.CreateSet<int>(result);
        List<QpdfObject> stack = ParserContainers.CreateList(scratch);
        ParserContainers.AddItem(scratch, stack, value);
        while (stack.Count != 0)
        {
            QpdfObject current = stack[^1]; stack.RemoveAt(stack.Count - 1);
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (current is PdfReference reference)
            {
                ParserContainers.AddSetItem(result, found, reference.ObjectNumber, sizeof(int));
            }
            else if (current is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    ParserContainers.AddItem(scratch, stack, array[i]);
                }
            }
            else if (current is PdfDictionary dictionary)
            {
                using PdfDictionary.EntryEnumerator entries = dictionary.GetEntryEnumerator();
                while (entries.MoveNext(governor, cancellationToken))
                {
                    ParserContainers.AddItem(scratch, stack, entries.Current.Value);
                }
            }
            else if (current is PdfStream stream)
            {
                ParserContainers.AddItem(scratch, stack, stream.Dictionary);
            }
        }

        result.TransferTo(ownership); return found;
    }

    private static PdfDictionary Assemble(CopiedForm combined, IReadOnlyList<PdfReference> fields,
        HashSet<int>? closure, ResourceGovernor governor, CancellationToken token)
    {
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        if (combined.HasFields)
        {
            entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "Fields"u8),
                CopyFormArray(graph, fields, null), token);
        }
        if (combined.HasNeedAppearances)
        {
            graph.Memory.Reserve(24L); graph.Add(24L);
            entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "NeedAppearances"u8), new PdfBoolean(combined.NeedAppearances), token);
        }

        if (combined.DefaultResources is not null)
        {
            entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "DR"u8), combined.DefaultResources, token);
        }

        if (combined.HasCalculationOrder)
        {
            // A registration-free form historically clears its calculation order.
            IReadOnlyList<QpdfObject> order = fields.Count == 0 ? Array.Empty<QpdfObject>() : combined.CalculationOrder;
            entries.Assign(graph.Memory, governor, PdfDictionaryRebuild.CreateName(graph, "CO"u8), CopyFormArray(graph, order, closure), token);
        }

        foreach (KeyValuePair<PdfName, QpdfObject> extra in combined.Extras)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            entries.TryAdd(graph.Memory, governor, extra.Key, extra.Value, token);
        }

        PdfDictionary form = PdfDictionaryRebuild.Complete(graph, entries, allocation);
        graph.Retain(); return form;
    }

    private static PdfArray CopyFormArray(OwnedAllocationBatch graph, IReadOnlyList<QpdfObject> items, HashSet<int>? closure)
    {
        int count = 0;
        foreach (QpdfObject item in items)
        {
            graph.Token.ThrowIfCancellationRequested(); graph.Governor.CheckCancellation();
            if (closure is null || item is not PdfReference link || closure.Contains(link.ObjectNumber)) { count++; }
        }
        long bytes = checked(64L + (long)IntPtr.Size * count);
        graph.Memory.Reserve(bytes);
        QpdfObject[] kept = new QpdfObject[count]; int next = 0;
        foreach (QpdfObject item in items)
        {
            graph.Token.ThrowIfCancellationRequested(); graph.Governor.CheckCancellation();
            if (closure is null || item is not PdfReference link || closure.Contains(link.ObjectNumber)) { kept[next++] = item; }
        }
        graph.Add(bytes); return PdfArray.FromOwnedItems(kept);
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
        ParserMemoryScope ownership,
        CancellationToken cancellationToken)
    {
        ownership.CheckOwner(governor);
        using ParserMemoryScope result = ParserMemoryScope.Create(governor);
        List<PdfReference> kept = ParserContainers.CreateList<PdfReference>(result);
        HashSet<int> closure = ParserContainers.CreateSet<int>(result);
        foreach (PdfReference field in fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (field is null)
            {
                continue;
            }

            using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
            HashSet<int> members = WalkPoolMembers(pool, field.ObjectNumber, limits, governor, traversal, cancellationToken);
            bool survives = members.Count == 0 && !pruneUnreachableFields;
            foreach (int member in members)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
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

            ParserContainers.AddItem(result, kept, field, IntPtr.Size);
            // An earlier retained root may already have reached this entire
            // subtree. Keep registration order/duplicates without walking it again.
            if (closure.Contains(field.ObjectNumber)) { continue; }
            foreach (int id in CollectPoolSubtree(pool, field.ObjectNumber, limits, governor, traversal, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddSetItem(result, closure, id, sizeof(int));
            }
        }

        result.TransferTo(ownership); return (kept, closure);
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
        ResourceGovernor governor,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        HashSet<int> found = ParserContainers.CreateSet<int>(memory);
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
                    ParserContainers.AddSetItem(memory, found, itemReference.ObjectNumber, sizeof(int));
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
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        List<QpdfObject> kept = ParserContainers.CreateList(memory);
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
                ParserContainers.AddItem(memory, kept, entry);
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

            using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
            HashSet<int> members = WalkSourceMembers(resolver, field, reference.ObjectNumber, limits, governor, traversal, cancellationToken);
            if (members.Count == 0 && pruneUnreachableFields)
            {
                continue;
            }

            if (members.Count == 0)
            {
                ParserContainers.AddItem(memory, kept, entry);
                continue;
            }

            bool survives = false;
            foreach (int member in members)
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (keptWidgets.Contains(member))
                {
                    survives = true;
                    break;
                }
            }

            if (survives)
            {
                ParserContainers.AddItem(memory, kept, entry);
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
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        HashSet<int> members = ParserContainers.CreateSet<int>(memory);
        if (IsWidget(root))
        {
            ParserContainers.AddSetItem(memory, members, rootNumber, sizeof(int));
        }

        HashSet<int> visited = ParserContainers.CreateSet<int>(memory);
        ParserContainers.AddSetItem(memory, visited, rootNumber, sizeof(int));
        List<(PdfDictionary Node, int Depth)> stack = ParserContainers.CreateList<(PdfDictionary, int)>(memory);
        ParserContainers.AddItem(memory, stack, (root, 0), 16L);
        while (stack.Count != 0)
        {
            var current = stack[^1]; stack.RemoveAt(stack.Count - 1);
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

                if (!ParserContainers.AddSetItem(memory, visited, kidReference.ObjectNumber, sizeof(int)))
                {
                    continue;
                }

                if (!resolver.TryResolve(kidReference, out QpdfObject? kidTarget) || kidTarget is not PdfDictionary kid)
                {
                    continue;
                }

                if (IsWidget(kid))
                {
                    ParserContainers.AddSetItem(memory, members, kidReference.ObjectNumber, sizeof(int));
                }

                ParserContainers.AddItem(memory, stack, (kid, current.Depth + 1), 16L);
            }
        }

        return members;
    }

    private static HashSet<int> WalkPoolMembers(
        IReadOnlyDictionary<int, QpdfObject> pool,
        int rootNumber,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        HashSet<int> members = ParserContainers.CreateSet<int>(memory);
        if (!pool.TryGetValue(rootNumber, out QpdfObject? root) || root is not PdfDictionary rootDict)
        {
            return members;
        }

        if (IsWidget(rootDict))
        {
            ParserContainers.AddSetItem(memory, members, rootNumber, sizeof(int));
        }

        HashSet<int> visited = ParserContainers.CreateSet<int>(memory);
        ParserContainers.AddSetItem(memory, visited, rootNumber, sizeof(int));
        List<(PdfDictionary Node, int Depth)> stack = ParserContainers.CreateList<(PdfDictionary, int)>(memory);
        ParserContainers.AddItem(memory, stack, (rootDict, 0), 16L);
        while (stack.Count != 0)
        {
            var current = stack[^1]; stack.RemoveAt(stack.Count - 1);
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

                if (!ParserContainers.AddSetItem(memory, visited, kidReference.ObjectNumber, sizeof(int)))
                {
                    continue;
                }

                if (!pool.TryGetValue(kidReference.ObjectNumber, out QpdfObject? kidTarget) || kidTarget is not PdfDictionary kid)
                {
                    continue;
                }

                if (IsWidget(kid))
                {
                    ParserContainers.AddSetItem(memory, members, kidReference.ObjectNumber, sizeof(int));
                }

                ParserContainers.AddItem(memory, stack, (kid, current.Depth + 1), 16L);
            }
        }

        return members;
    }

    private static HashSet<int> CollectPoolSubtree(
        IReadOnlyDictionary<int, QpdfObject> pool,
        int rootNumber,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope memory,
        CancellationToken cancellationToken)
    {
        HashSet<int> found = ParserContainers.CreateSet<int>(memory);
        List<int> stack = ParserContainers.CreateList<int>(memory);
        ParserContainers.AddItem(memory, stack, rootNumber, sizeof(int));
        while (stack.Count != 0)
        {
            int number = stack[^1]; stack.RemoveAt(stack.Count - 1);
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            if (!ParserContainers.AddSetItem(memory, found, number, sizeof(int)))
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

            using ParserMemoryScope references = ParserMemoryScope.Create(governor);
            foreach (int id in CollectRefNumbers(value, governor, references, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (!found.Contains(id))
                {
                    ParserContainers.AddItem(memory, stack, id, sizeof(int));
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
            && name.RawSpan.SequenceEqual("Widget"u8);
    }
}
