using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Writing;

internal sealed partial class GraphCopier
{
    private static readonly PdfName FieldKidsKey = PdfName.FromAscii("Kids");
    private static readonly PdfName FieldSubtypeKey = PdfName.FromAscii("Subtype");
    private static readonly PdfName AnnotsKey = PdfName.FromAscii("Annots");
    private static readonly PdfName FormKey = PdfName.FromAscii("AcroForm");
    private static readonly PdfName FieldsKey = PdfName.FromAscii("Fields");
    private static readonly PdfName PermsKey = PdfName.FromAscii("Perms");
    private static readonly PdfName SigFlagsKey = PdfName.FromAscii("SigFlags");
    private static readonly PdfName AppearanceKey = PdfName.FromAscii("AP");
    private static readonly PdfName NormalAppearanceKey = PdfName.FromAscii("N");
    private static readonly PdfName StampSubtype = PdfName.FromAscii("Stamp");
    private Dictionary<QpdfObject, bool>? _signatureBranches;
    private Dictionary<int, PdfReference>? _fieldCopies;
    private HashSet<PdfDictionary>? _disabledSignatureWidgets;

    internal bool IsDisabledSignatureWidget(PdfDictionary value) => _disabledSignatureWidgets?.Contains(value) == true;

    // Only annotation and field edges enter this role. Private aliases use the
    // ordinary copy map and keep their values. Appearance/resource payloads also
    // use that map, so disabling a field never duplicates or modifies its artwork.
    internal QpdfObject CopyFieldValue(QpdfObject value, int depth)
    {
        _governor.CheckCancellation();
        if (depth > _limits.MaxNestingDepth)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth, _limits.MaxNestingDepth); }
        if (!NeedsSignatureCopy(value)) { return CopyValue(value, depth); }
        if (value is PdfReference reference)
        {
            if (!_resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is null)
            { throw new QpdfParseException("Unresolvable form reference during page copy.", null, reference.ObjectNumber); }
            _fieldCopies ??= ParserContainers.CreateDictionary<int, PdfReference>(_ownership);
            if (_fieldCopies.TryGetValue(reference.ObjectNumber, out PdfReference? prior)) { return prior; }
            _ownership.Reserve(32L);
            PdfReference copy = new(_nextId, 0); _nextId = checked(_nextId + 1);
            ParserContainers.EnsureEntryCapacity(_ownership, _fieldCopies, IntPtr.Size);
            _fieldCopies.Add(reference.ObjectNumber, copy);
            Store(copy.ObjectNumber, CopyFieldValue(resolved, depth + 1));
            return copy;
        }
        if (value is PdfArray array)
        {
            _ownership.Reserve(checked(64L + IntPtr.Size * (long)array.Count));
            QpdfObject[] items = new QpdfObject[array.Count];
            for (int index = 0; index < array.Count; index++)
            { _governor.CheckCancellation(); items[index] = CopyFieldValue(array[index], depth + 1); }
            return PdfArray.FromOwnedItems(items);
        }
        if (value is not PdfDictionary dictionary) { return CopyValue(value, depth); }
        bool signature = SignaturePolicy.IsSignatureField(dictionary);
        bool widget = signature && dictionary.TryGet(FieldSubtypeKey, out QpdfObject subtype)
            && ResolveFieldNode(subtype) is PdfName widgetType && widgetType.RawSpan.SequenceEqual("Widget"u8);
        PdfNameTable<QpdfObject> entries = CreateEntries(checked(dictionary.Count + (widget ? 1 : 0)));
        using PdfDictionary.EntryEnumerator source = dictionary.GetEntryEnumerator();
        while (source.MoveNext(_governor, _governor.CancellationToken))
        {
            (PdfName key, QpdfObject child) = source.Current;
            ReadOnlySpan<byte> name = key.RawSpan;
            if (signature && (name.SequenceEqual("FT"u8) || name.SequenceEqual("V"u8)
                || name.SequenceEqual("DV"u8) || name.SequenceEqual("SV"u8) || name.SequenceEqual("Lock"u8)
                || name.SequenceEqual("Parent"u8) || name.SequenceEqual("Kids"u8))) { continue; }
            if (name.SequenceEqual("Parent"u8) && ResolveFieldNode(child) is PdfDictionary parent
                && SignaturePolicy.IsPageTreeNode(parent)) { continue; }
            QpdfObject copied = widget && key.Equals(FieldSubtypeKey) ? StampSubtype
                : widget && key.Equals(AppearanceKey) ? CopySignatureAppearance(child, depth + 1)
                : name.SequenceEqual("Kids"u8) ? CopyRegisteredFields(child, depth + 1)
                : name.SequenceEqual("Parent"u8) ? CopyFieldValue(child, depth + 1) : CopyValue(child, depth + 1);
            entries.Assign(_ownership, _governor, key, copied, _governor.CancellationToken);
        }
        // A disabled widget is a static annotation. An explicit empty appearance
        // prevents a viewer from substituting its default stamp for a blank field.
        if (widget && !dictionary.TryGet(AppearanceKey, out _))
        { entries.Assign(_ownership, _governor, AppearanceKey, CopySignatureAppearance(PdfNull.Instance, depth + 1), _governor.CancellationToken); }
        PdfDictionary result = CompleteDictionary(entries);
        if (widget)
        {
            if (_disabledSignatureWidgets is null)
            { _ownership.Reserve(96L); _disabledSignatureWidgets = new(ReferenceEqualityComparer.Instance); }
            ParserContainers.AddSetItem(_ownership, _disabledSignatureWidgets, result, IntPtr.Size);
        }
        return result;
    }

    private QpdfObject CopySignatureAppearance(QpdfObject value, int depth)
    {
        PdfDictionary? source = ResolveFieldNode(value) as PdfDictionary;
        if (source is not null && source.TryGet(NormalAppearanceKey, out QpdfObject normal)
            && ResolveFieldNode(normal) is not PdfNull) { return CopyValue(value, depth); }
        PdfNameTable<QpdfObject> appearance = CreateEntries(checked((source?.Count ?? 0) + 1));
        if (source is not null)
        {
            using PdfDictionary.EntryEnumerator entries = source.GetEntryEnumerator();
            while (entries.MoveNext(_governor, _governor.CancellationToken))
            {
                (PdfName key, QpdfObject child) = entries.Current;
                if (!key.Equals(NormalAppearanceKey))
                { appearance.Assign(_ownership, _governor, key, CopyValue(child, depth + 1), _governor.CancellationToken); }
            }
        }
        _ownership.Reserve(192L); // Array, two shared numbers and the empty stream.
        PdfInteger zero = new("0", 0), one = new("1", 1);
        PdfNameTable<QpdfObject> header = CreateEntries(3);
        header.Assign(_ownership, _governor, TypeKey, PdfName.FromAscii("XObject"), _governor.CancellationToken);
        header.Assign(_ownership, _governor, FieldSubtypeKey, PdfName.FromAscii("Form"), _governor.CancellationToken);
        header.Assign(_ownership, _governor, PdfName.FromAscii("BBox"), PdfArray.FromOwnedItems([zero, zero, one, one]), _governor.CancellationToken);
        PdfReference empty = AddIndirectObjectWithId(PdfStream.FromOwnedData(CompleteDictionary(header), []), _nextId);
        appearance.Assign(_ownership, _governor, NormalAppearanceKey, empty, _governor.CancellationToken);
        return CompleteDictionary(appearance);
    }

    internal bool IsSignatureField(QpdfObject value)
        => ResolveFieldNode(value) is PdfDictionary field && SignaturePolicy.IsSignatureField(field);

    internal QpdfObject CopyRegisteredFields(QpdfObject value, int depth)
    {
        _governor.CheckCancellation();
        if (depth > _limits.MaxNestingDepth)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth, _limits.MaxNestingDepth); }
        if (ResolveFieldNode(value) is not PdfArray fields) { return CopyValue(value, depth); }
        List<QpdfObject> kept = ParserContainers.CreateList<QpdfObject>(_ownership);
        for (int index = 0; index < fields.Count; index++)
        {
            _governor.CheckCancellation();
            if (IsSignatureField(fields[index])) { continue; }
            ParserContainers.AddItem(_ownership, kept, CopyFieldValue(fields[index], depth + 1), IntPtr.Size);
        }
        return ParserContainers.CopyArray(_ownership, kept);
    }

    internal bool NeedsSignatureCopy(QpdfObject root)
    {
        if (_signatureBranches is null)
        { _ownership.Reserve(96L); _signatureBranches = new(ReferenceEqualityComparer.Instance); }
        QpdfObject resolved = ResolveFieldNode(root);
        if (_signatureBranches.TryGetValue(resolved, out bool known)) { return known; }
        using ParserMemoryScope scratch = ParserMemoryScope.Create(_governor);
        scratch.Reserve(96L);
        HashSet<QpdfObject> visited = new(ReferenceEqualityComparer.Instance);
        List<QpdfObject> pending = ParserContainers.CreateList<QpdfObject>(scratch);
        ParserContainers.AddItem(scratch, pending, resolved, IntPtr.Size);
        bool signed = false;
        while (pending.Count != 0)
        {
            _governor.CheckCancellation(); _governor.NoteOperation();
            QpdfObject current = ResolveFieldNode(pending[^1]); pending.RemoveAt(pending.Count - 1);
            if (!ParserContainers.AddSetItem(scratch, visited, current, IntPtr.Size)) { continue; }
            if (_signatureBranches.TryGetValue(current, out known)) { signed |= known; continue; }
            if (current is PdfArray array)
            {
                for (int index = 0; index < array.Count; index++)
                { _governor.CheckCancellation(); ParserContainers.AddItem(scratch, pending, array[index], IntPtr.Size); }
            }
            else if (current is PdfDictionary dictionary && !SignaturePolicy.IsPageTreeNode(dictionary))
            {
                // Non-widget annotation Parent links (for example popups) do not
                // establish a field hierarchy.
                if (dictionary.TryGet(FieldSubtypeKey, out QpdfObject subtype)
                    && ResolveFieldNode(subtype) is PdfName name && !name.RawSpan.SequenceEqual("Widget"u8)) { continue; }
                signed |= SignaturePolicy.IsSignatureField(dictionary);
                if (dictionary.TryGet(ParentKey, out QpdfObject parent))
                { ParserContainers.AddItem(scratch, pending, parent, IntPtr.Size); }
                if (dictionary.TryGet(FieldKidsKey, out QpdfObject kids))
                { ParserContainers.AddItem(scratch, pending, kids, IntPtr.Size); }
            }
        }
        // A field's Parent/Kids closure is one component; cache it together to
        // avoid walking shared ancestry for each widget. A root annotation/field
        // array can join independent components, so only cache that array itself.
        if (resolved is PdfDictionary)
        {
            foreach (QpdfObject item in visited)
            {
                _governor.CheckCancellation();
                if (item is not PdfDictionary field || SignaturePolicy.IsPageTreeNode(field)) { continue; }
                if (!_signatureBranches.ContainsKey(field))
                { ParserContainers.EnsureEntryCapacity(_ownership, _signatureBranches, sizeof(bool)); }
                _signatureBranches[field] = signed;
            }
        }
        if (!_signatureBranches.ContainsKey(resolved))
        { ParserContainers.EnsureEntryCapacity(_ownership, _signatureBranches, sizeof(bool)); }
        _signatureBranches[resolved] = signed;
        return signed;
    }

    private QpdfObject ResolveFieldNode(QpdfObject value)
    {
        if (value is PdfReference reference)
        {
            PdfObjectLookup.TryResolve(reference, _resolver, _governor, _governor.CancellationToken, out QpdfObject? resolved);
            return resolved ?? PdfNull.Instance;
        }
        return value;
    }

    // JSON publication already has a fully resolved, numbered pool. Reuse the
    // same field-role copier with identity mappings for ordinary graph values.
    internal static void DisablePublishedSignatures(Dictionary<int, QpdfObject> pool, int catalogId,
        ResourceGovernor governor, QpdfLimits limits, ParserMemoryScope ownership)
    {
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        Content.PoolResolver resolver = Content.PoolResolver.Create(scratch, pool);
        List<int> numbers = ParserContainers.CreateList<int>(scratch);
        int next = 1;
        foreach (int number in pool.Keys)
        {
            governor.CheckCancellation();
            ParserContainers.AddItem(scratch, numbers, number, sizeof(int)); next = Math.Max(next, checked(number + 1));
        }
        ownership.Reserve(192L);
        GraphCopier copier = new(resolver, pool.Count, governor, limits, next, ownership);
        foreach (int number in numbers) { governor.CheckCancellation(); copier.Map(number, number); }
        copier._copied = pool;
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, governor.CancellationToken);
        foreach (int number in numbers)
        {
            governor.CheckCancellation();
            if (pool[number] is not PdfDictionary page || copier.SignaturePolicy.ReadType(page) != "Page"
                || !page.TryGet(AnnotsKey, out QpdfObject annots)
                || !copier.NeedsSignatureCopy(annots)) { continue; }
            pool[number] = PdfDictionaryRebuild.ReplaceEntry(graph, page, "Annots"u8, copier.CopyFieldValue(annots, 0));
        }
        PdfDictionary catalog = (PdfDictionary)pool[catalogId];
        if (catalog.TryGet(PermsKey, out _))
        { catalog = PdfDictionaryRebuild.RemoveEntry(graph, catalog, "Perms"u8); }
        if (catalog.TryGet(FormKey, out QpdfObject raw)
            && copier.ResolveFieldNode(raw) is PdfDictionary form)
        {
            PdfDictionary changed = form;
            if (form.TryGet(SigFlagsKey, out _))
            { changed = PdfDictionaryRebuild.RemoveEntry(graph, changed, "SigFlags"u8); }
            if (form.TryGet(FieldsKey, out QpdfObject fields) && copier.NeedsSignatureCopy(fields))
            { changed = PdfDictionaryRebuild.ReplaceEntry(graph, changed, "Fields"u8, copier.CopyRegisteredFields(fields, 0)); }
            if (PdfDictionaryLookup.TryGet(form, "CO"u8, governor, governor.CancellationToken, out QpdfObject order)
                && copier.NeedsSignatureCopy(order))
            { changed = PdfDictionaryRebuild.ReplaceEntry(graph, changed, "CO"u8, copier.CopyRegisteredFields(order, 0)); }
            if (!ReferenceEquals(changed, form))
            { catalog = PdfDictionaryRebuild.ReplaceEntry(graph, catalog, "AcroForm"u8, changed); }
        }
        pool[catalogId] = catalog;
        graph.Memory.TransferTo(ownership, graph.Bytes);
    }
}
