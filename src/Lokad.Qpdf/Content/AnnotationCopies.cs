using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

internal static class AnnotationCopies
{
    private readonly record struct AnnotationUse(int Owner, int LastPage, bool Shared, bool Changed, bool OwnerChanges);
    private static readonly PdfName AnnotsKey = PdfName.FromAscii("Annots");
    private static readonly PdfName ParentKey = PdfName.FromAscii("Parent");
    private static readonly PdfName KidsKey = PdfName.FromAscii("Kids");

    // Keep one original occurrence, preferring an untouched page for rotation.
    // Other occurrences copy headers, never appearance or resource payloads.
    internal static IReadOnlyList<(GraphCopier Copier, CopiedForm Part, int Occurrence)> Isolate(
        IReadOnlyList<(GraphCopier Copier, int Id)> occurrences,
        IReadOnlyDictionary<GraphCopier, CopiedForm?> forms,
        bool duplicateWidgets,
        ResourceGovernor governor, ParserMemoryScope ownership, CancellationToken token, ref int allocator)
    {
        if (occurrences.Count < 2) { return Array.Empty<(GraphCopier, CopiedForm, int)>(); }
        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        traversal.Reserve(512L); // Temporary box and content-resolution state.
        Dictionary<(GraphCopier, int), AnnotationUse> uses = ParserContainers.CreateDictionary<(GraphCopier, int), AnnotationUse>(traversal);
        bool sharedChange = false;
        for (int index = 0; index < occurrences.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            (GraphCopier copier, int id) = occurrences[index];
            if (!copier.CopiedObjects.TryGetValue(id, out QpdfObject? raw) || raw is not PdfDictionary page) { continue; }
            PoolResolver resolver = new(copier.CopiedObjects);
            if (!page.TryGet(AnnotsKey, out QpdfObject annotsRaw) || ResolveValue(annotsRaw, resolver) is not PdfArray annots) { continue; }
            bool changes = duplicateWidgets || FlattenRotation.CanFlatten(page, resolver, governor, token);
            for (int item = 0; item < annots.Count; item++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (annots[item] is not PdfReference reference || ResolveOrNull(resolver, reference) is not PdfDictionary annotation) { continue; }
                (GraphCopier, int) key = (copier, reference.ObjectNumber);
                if (!uses.TryGetValue(key, out AnnotationUse use))
                {
                    ParserContainers.EnsureEntryCapacity(traversal, uses, 32L);
                    uses.Add(key, new(index, index, false, changes, changes));
                    continue;
                }
                if (use.LastPage == index) { continue; }
                bool replaceOwner = use.OwnerChanges && !changes;
                use = new(replaceOwner ? index : use.Owner, index, true, use.Changed || changes, replaceOwner ? changes : use.OwnerChanges);
                uses[key] = use;
                // Widget copies need their page-local popup/reply bundle too.
                // Retain ordinary annotation sharing when no widget is shared.
                sharedChange |= use.Changed && (!duplicateWidgets
                    || PdfDictionaryLookup.HasName(annotation, "Subtype"u8, "Widget"u8, resolver, governor, token));
            }
        }
        if (!sharedChange) { return Array.Empty<(GraphCopier, CopiedForm, int)>(); }

        HashSet<(GraphCopier, int)> registered = ParserContainers.CreateSet<(GraphCopier, int)>(traversal);
        foreach (KeyValuePair<GraphCopier, CopiedForm?> source in forms)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (source.Value is null) { continue; }
            foreach (PdfReference root in source.Value.Fields)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddSetItem(traversal, registered, (source.Key, root.ObjectNumber), 16L);
            }
        }
        List<(GraphCopier, CopiedForm, int)> fieldCopies = ParserContainers.CreateList<(GraphCopier, CopiedForm, int)>(ownership);
        for (int index = 0; index < occurrences.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            (GraphCopier copier, int id) = occurrences[index];
            if (!copier.CopiedObjects.TryGetValue(id, out QpdfObject? raw) || raw is not PdfDictionary page) { continue; }
            PoolResolver resolver = new(copier.CopiedObjects);
            if (!page.TryGet(AnnotsKey, out QpdfObject annotsRaw) || ResolveValue(annotsRaw, resolver) is not PdfArray annots) { continue; }
            using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
            Dictionary<int, PdfReference> copies = ParserContainers.CreateDictionary<int, PdfReference>(scratch);
            HashSet<int> fieldNodes = ParserContainers.CreateSet<int>(scratch);
            List<PdfReference>? fields = null;
            forms.TryGetValue(copier, out CopiedForm? form);
            for (int item = 0; item < annots.Count; item++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (annots[item] is not PdfReference reference
                    || !uses.TryGetValue((copier, reference.ObjectNumber), out AnnotationUse use)
                    || !use.Shared || !use.Changed || use.Owner == index) { continue; }
                AddHeaderCopy(reference, copier, copies, scratch, ref allocator);
                if (ResolveOrNull(resolver, reference) is not PdfDictionary annot) { continue; }
                // A popup's Parent is an annotation relationship, not a field spine.
                if (!annot.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject subtype)
                    || ResolveValue(subtype, resolver) is not PdfName name || !name.RawSpan.SequenceEqual("Widget"u8)) { continue; }
                AcroFormCopy.OverlayTopMatch? top = AcroFormCopy.FindOverlayTopField(annot, reference, true, resolver, governor, token);
                if (top is not { Cycled: false, Reference: PdfReference root }) { continue; }
                bool newRoot = !fieldNodes.Contains(root.ObjectNumber);
                PdfReference cursor = reference;
                for (int depth = 0; ; depth++)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    if (depth > governor.MaxNestingDepth) { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth, governor.MaxNestingDepth); }
                    AddHeaderCopy(cursor, copier, copies, scratch, ref allocator);
                    ParserContainers.AddSetItem(scratch, fieldNodes, cursor.ObjectNumber, sizeof(int));
                    if (cursor.ObjectNumber == root.ObjectNumber) { break; }
                    if (ResolveOrNull(resolver, cursor) is not PdfDictionary node
                        || !node.TryGet(ParentKey, out QpdfObject parent) || parent is not PdfReference parentRef) { break; }
                    cursor = parentRef;
                }
                if (newRoot && registered.Contains((copier, root.ObjectNumber)))
                {
                    fields ??= ParserContainers.CreateList<PdfReference>(ownership);
                    ParserContainers.AddItem(ownership, fields, copies[root.ObjectNumber], IntPtr.Size);
                }
            }

            // Popup dictionaries can be reachable only through their owning annotation.
            List<int> linked = ParserContainers.CreateList<int>(scratch);
            foreach (int number in copies.Keys)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddItem(scratch, linked, number, sizeof(int));
            }
            for (int item = 0; item < linked.Count; item++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (copier.CopiedObjects[linked[item]] is not PdfDictionary node
                    || !node.TryGet(PdfName.FromAscii("Popup"), out QpdfObject popup) || popup is not PdfReference reference
                    || copies.ContainsKey(reference.ObjectNumber) || ResolveOrNull(resolver, reference) is not PdfDictionary) { continue; }
                AddHeaderCopy(reference, copier, copies, scratch, ref allocator);
                ParserContainers.AddItem(scratch, linked, reference.ObjectNumber, sizeof(int));
            }
            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, token);
            foreach (KeyValuePair<int, PdfReference> copy in copies)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                PdfDictionary original = (PdfDictionary)copier.CopiedObjects[copy.Key];
                PdfDictionary cloned = CopyAnnotationHeader(original, copies, fieldNodes.Contains(copy.Key), id, resolver, graph);
                copier.ReplaceCopiedObject(copy.Value.ObjectNumber, cloned);
            }
            bool changed = copies.Count > 0;
            linked.Clear();
            HashSet<int> rebound = ParserContainers.CreateSet<int>(scratch);
            for (int item = 0; item < annots.Count; item++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (annots[item] is not PdfReference reference
                    || !uses.TryGetValue((copier, reference.ObjectNumber), out AnnotationUse use)
                    || !use.Shared || !use.Changed || use.Owner != index) { continue; }
                if (ParserContainers.AddSetItem(scratch, rebound, reference.ObjectNumber, sizeof(int)))
                { ParserContainers.AddItem(scratch, linked, reference.ObjectNumber, sizeof(int)); }
            }
            for (int item = 0; item < linked.Count; item++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                PdfDictionary original = (PdfDictionary)copier.CopiedObjects[linked[item]];
                graph.Memory.Reserve(32L); PdfReference pageRef = new(id, 0); graph.Add(32L);
                copier.ReplaceCopiedObject(linked[item], PdfDictionaryRebuild.ReplaceEntry(graph, original, "P"u8, pageRef));
                if (original.TryGet(PdfName.FromAscii("Popup"), out QpdfObject popup) && popup is PdfReference popupRef
                    && ResolveOrNull(resolver, popupRef) is PdfDictionary
                    && ParserContainers.AddSetItem(scratch, rebound, popupRef.ObjectNumber, sizeof(int)))
                { ParserContainers.AddItem(scratch, linked, popupRef.ObjectNumber, sizeof(int)); }
            }
            if (changed)
            {
                long bytes = checked(32L + IntPtr.Size * (long)annots.Count);
                graph.Memory.Reserve(bytes); QpdfObject[] items = new QpdfObject[annots.Count]; graph.Add(bytes);
                for (int item = 0; item < annots.Count; item++)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    QpdfObject entry = annots[item];
                    if (entry is PdfReference reference && copies.TryGetValue(reference.ObjectNumber, out PdfReference? copy)) { items[item] = copy; }
                    else if (ResolveValue(entry, resolver) is PdfDictionary annotation)
                    {
                        PdfDictionary updated = RemapAnnotationLinks(annotation, copies, resolver, graph);
                        if (entry is PdfReference link) { copier.ReplaceCopiedObject(link.ObjectNumber, updated); items[item] = link; }
                        else { items[item] = updated; }
                    }
                    else { items[item] = entry; }
                }
                graph.Memory.Reserve(24L); PdfArray result = PdfArray.FromOwnedItems(items); graph.Add(24L);
                copier.ReplaceCopiedObject(id, PdfDictionaryRebuild.ReplaceEntry(graph, page, "Annots"u8, result));
            }
            if (form is not null && fields is not null)
            {
                List<QpdfObject> order = ParserContainers.CreateList<QpdfObject>(ownership);
                foreach (QpdfObject entry in form.CalculationOrder)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    if (entry is PdfReference reference && copies.TryGetValue(reference.ObjectNumber, out PdfReference? copy))
                    { ParserContainers.AddItem(ownership, order, copy, IntPtr.Size); }
                }
                ownership.Reserve(80L);
                CopiedForm part = form with { Fields = fields, CalculationOrder = order, HasCalculationOrder = order.Count > 0 };
                ParserContainers.AddItem(ownership, fieldCopies, (copier, part, index), 24L);
            }
            graph.Retain();
        }
        return fieldCopies;
    }

    private static void AddHeaderCopy(PdfReference reference, GraphCopier copier,
        Dictionary<int, PdfReference> copies, ParserMemoryScope scratch, ref int allocator)
    {
        if (copies.ContainsKey(reference.ObjectNumber)) { return; }
        ParserContainers.EnsureEntryCapacity(scratch, copies, IntPtr.Size);
        copies.Add(reference.ObjectNumber, copier.AddIndirectObjectWithId(copier.CopiedObjects[reference.ObjectNumber], allocator));
        allocator = checked(allocator + 1);
    }

    private static PdfDictionary RemapAnnotationLinks(PdfDictionary source, Dictionary<int, PdfReference> copies,
        PoolResolver resolver, OwnedAllocationBatch graph)
    {
        PdfDictionary result = source;
        using PdfDictionary.EntryEnumerator entries = source.GetEntryEnumerator();
        while (entries.MoveNext(graph.Governor, graph.Token))
        {
            PdfName key = entries.Current.Key;
            bool local = key.RawSpan.SequenceEqual("IRT"u8) || key.RawSpan.SequenceEqual("Popup"u8);
            if (!local && key.Equals(ParentKey) && source.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject subtype)
                && ResolveValue(subtype, resolver) is PdfName name && name.RawSpan.SequenceEqual("Popup"u8)) { local = true; }
            if (local && entries.Current.Value is PdfReference reference && copies.TryGetValue(reference.ObjectNumber, out PdfReference? copy))
            { result = PdfDictionaryRebuild.ReplaceEntry(graph, result, key.RawSpan, copy); }
        }
        return result;
    }

    private static PdfDictionary CopyAnnotationHeader(PdfDictionary source, Dictionary<int, PdfReference> copies,
        bool field, int pageId, PoolResolver resolver, OwnedAllocationBatch graph)
    {
        PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out long allocation);
        using PdfDictionary.EntryEnumerator fields = source.GetEntryEnumerator();
        while (fields.MoveNext(graph.Governor, graph.Token))
        {
            PdfName key = fields.Current.Key; QpdfObject value = fields.Current.Value;
            if (value is PdfReference reference && (key.RawSpan.SequenceEqual("Parent"u8)
                || key.RawSpan.SequenceEqual("Popup"u8) || key.RawSpan.SequenceEqual("IRT"u8))
                && copies.TryGetValue(reference.ObjectNumber, out PdfReference? copy)) { value = copy; }
            else if (field && key.Equals(KidsKey) && ResolveValue(value, resolver) is PdfArray kids)
            {
                using ParserMemoryScope scratch = ParserMemoryScope.Create(graph.Governor);
                List<QpdfObject> kept = ParserContainers.CreateList<QpdfObject>(scratch);
                for (int index = 0; index < kids.Count; index++)
                {
                    graph.Token.ThrowIfCancellationRequested(); graph.Governor.CheckCancellation();
                    if (kids[index] is PdfReference child && copies.TryGetValue(child.ObjectNumber, out PdfReference? copiedChild))
                    { ParserContainers.AddItem(scratch, kept, copiedChild, IntPtr.Size); }
                }
                long bytes = checked(64L + IntPtr.Size * (long)kept.Count);
                graph.Memory.Reserve(bytes); value = new PdfArray(kept); graph.Add(bytes);
            }
            entries.Assign(graph.Memory, graph.Governor, key, value, graph.Token);
        }
        // Non-widget field ancestors have no page association.
        if (!field || source.TryGet(PdfName.FromAscii("Subtype"), out _))
        {
            graph.Memory.Reserve(32L); PdfReference page = new(pageId, 0); graph.Add(32L);
            entries.Assign(graph.Memory, graph.Governor, PdfDictionaryRebuild.CreateName(graph, "P"u8), page, graph.Token);
        }
        return PdfDictionaryRebuild.Complete(graph, entries, allocation);
    }

    private static QpdfObject? ResolveValue(QpdfObject value, PoolResolver resolver)
        => value is PdfReference reference ? ResolveOrNull(resolver, reference) : value;

    private static QpdfObject? ResolveOrNull(PoolResolver resolver, PdfReference reference)
        => ((IIndirectResolver)resolver).TryResolve(reference, out QpdfObject? value) ? value : null;

}
