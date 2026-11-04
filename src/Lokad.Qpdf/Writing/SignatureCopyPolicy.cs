using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Writing;

/// <summary>The existing signature-copy exclusion, shared with JSON publication.</summary>
internal sealed class SignatureCopyPolicy
{
    private static readonly PdfName TypeKey = PdfName.FromAscii("Type");
    private static readonly PdfName ParentKey = PdfName.FromAscii("Parent");
    private static readonly PdfName PageType = PdfName.FromAscii("Page");
    private static readonly PdfName PagesType = PdfName.FromAscii("Pages");
    private static readonly PdfName CatalogType = PdfName.FromAscii("Catalog");
    private static readonly PdfName FieldTypeKey = PdfName.FromAscii("FT");
    private static readonly PdfName ValueKey = PdfName.FromAscii("V");
    private static readonly PdfName SignatureType = PdfName.FromAscii("Sig");
    private static readonly PdfName SubtypeKey = PdfName.FromAscii("Subtype");
    private static readonly PdfName WidgetType = PdfName.FromAscii("Widget");
    private static readonly PdfName KidsKey = PdfName.FromAscii("Kids");
    private const int UnresolvedFieldType = -2;
    private const int ResolvingFieldType = -1;
    private readonly IIndirectResolver _resolver;
    private readonly ResourceGovernor _governor;
    private readonly QpdfLimits _limits;
    private readonly ParserMemoryScope _ownership;
    private Dictionary<PdfDictionary, (bool Signature, int Distance, bool Validated)>? _fieldTypes;
    private List<PdfDictionary>? _fieldPath;
    private List<PdfDictionary>? _fieldPending;

    internal SignatureCopyPolicy(IIndirectResolver resolver, ResourceGovernor governor,
        QpdfLimits limits, ParserMemoryScope ownership)
    {
        ownership.CheckOwner(governor);
        _resolver = resolver; _governor = governor; _limits = limits; _ownership = ownership;
    }

    // Rewriting bytes cannot preserve a source signature. Reject its value before
    // following auxiliary references into the source catalog. Empty signature
    // fields remain ordinary form structure; read-only retention never calls here.
    internal void RejectDictionary(PdfDictionary dictionary)
    {
        _governor.CheckCancellation();
        if (dictionary.TryGet(TypeKey, out QpdfObject type)
            && ResolveFieldAttribute(type) is PdfName name && name.Equals(SignatureType))
        { throw new QpdfCryptoException("/Sig"); }
        if (dictionary.TryGet(SubtypeKey, out QpdfObject subtype)
            && ResolveFieldAttribute(subtype) is PdfName widget && widget.Equals(WidgetType))
        { ValidateField(dictionary); }
    }

    // Only widgets and roots from AcroForm.Fields establish field context. Private
    // dictionaries may use V/Parent for unrelated purposes, including cycles.
    internal void ValidateField(QpdfObject root)
    {
        _governor.CheckCancellation();
        if (ResolveFieldAttribute(root) is not PdfDictionary first) { return; }
        if (_fieldTypes is null)
        {
            _ownership.Reserve(96L);
            _fieldPath = ParserContainers.CreateList<PdfDictionary>(_ownership);
            _fieldPending = ParserContainers.CreateList<PdfDictionary>(_ownership);
            _fieldTypes = new(ReferenceEqualityComparer.Instance);
        }
        List<PdfDictionary> pending = _fieldPending ?? throw new InvalidOperationException("Missing field traversal storage.");
        ParserContainers.AddItem(_ownership, pending, first, IntPtr.Size);
        try
        {
            while (pending.Count != 0)
            {
                _governor.CheckCancellation(); _governor.NoteOperation();
                PdfDictionary current = pending[^1]; pending.RemoveAt(pending.Count - 1);
                if (IsPageTreeNode(current)) { continue; }
                if (_fieldTypes.TryGetValue(current, out var known) && known.Validated) { continue; }
                if (current.TryGet(ValueKey, out QpdfObject value) && value is not PdfNull)
                {
                    var fieldType = ResolveFieldType(current);
                    if (fieldType.Signature && ResolveFieldAttribute(value) is not PdfNull)
                    { throw new QpdfCryptoException("/Sig"); }
                }
                if (_fieldTypes.TryGetValue(current, out known))
                { _fieldTypes[current] = (known.Signature, known.Distance, true); }
                else
                {
                    ParserContainers.EnsureEntryCapacity(_ownership, _fieldTypes, 16);
                    _fieldTypes.Add(current, (false, UnresolvedFieldType, true));
                }
                if (current.TryGet(ParentKey, out QpdfObject parent)
                    && ResolveFieldAttribute(parent) is PdfDictionary parentDictionary)
                { ParserContainers.AddItem(_ownership, pending, parentDictionary, IntPtr.Size); }
                if (current.TryGet(KidsKey, out QpdfObject kids)
                    && ResolveFieldAttribute(kids) is PdfArray children)
                {
                    for (int i = 0; i < children.Count; i++)
                    {
                        _governor.CheckCancellation();
                        if (ResolveFieldAttribute(children[i]) is PdfDictionary child)
                        { ParserContainers.AddItem(_ownership, pending, child, IntPtr.Size); }
                    }
                }
            }
        }
        finally { pending.Clear(); }
    }

    private (bool Signature, int Distance) ResolveFieldType(PdfDictionary dictionary)
    {
        Dictionary<PdfDictionary, (bool Signature, int Distance, bool Validated)> types =
            _fieldTypes ?? throw new InvalidOperationException("Missing field type storage.");
        List<PdfDictionary> path = _fieldPath ?? throw new InvalidOperationException("Missing field path storage.");
        PdfDictionary? current = dictionary;
        bool signature = false;
        int distance = -1;
        try
        {
            while (current is not null)
            {
                _governor.CheckCancellation(); _governor.NoteOperation();
                bool found = types.TryGetValue(current, out var cached);
                if (found && cached.Distance != UnresolvedFieldType)
                {
                    if (cached.Distance == ResolvingFieldType)
                    { throw new QpdfParseException("Field parent cycle during page copy.", null, null); }
                    signature = cached.Signature; distance = cached.Distance;
                    break;
                }
                if (path.Count > _limits.MaxNestingDepth)
                { throw new QpdfParseException("Field parent chain exceeds the copy nesting limit.", null, null); }
                ParserContainers.AddItem(_ownership, path, current, IntPtr.Size);
                if (!found) { ParserContainers.EnsureEntryCapacity(_ownership, types, 16); }
                types[current] = (false, ResolvingFieldType, cached.Validated);
                if (current.TryGet(FieldTypeKey, out QpdfObject fieldType)
                    && ResolveFieldAttribute(fieldType) is QpdfObject resolvedType && resolvedType is not PdfNull)
                { signature = resolvedType is PdfName name && name.Equals(SignatureType); break; }
                if (!current.TryGet(ParentKey, out QpdfObject parent)) { break; }
                current = ResolveFieldAttribute(parent) as PdfDictionary;
                if (current is not null && IsPageTreeNode(current)) { break; }
            }
            for (int i = path.Count - 1; i >= 0; i--)
            {
                _governor.CheckCancellation();
                if (++distance > _limits.MaxNestingDepth)
                { throw new QpdfParseException("Field parent chain exceeds the copy nesting limit.", null, null); }
                types[path[i]] = (signature, distance, types[path[i]].Validated);
            }
            var result = types[dictionary];
            return (result.Signature, result.Distance);
        }
        finally
        {
            foreach (PdfDictionary visited in path)
            {
                if (types.TryGetValue(visited, out var cached) && cached.Distance == ResolvingFieldType) { types.Remove(visited); }
            }
            path.Clear();
        }
    }

    private QpdfObject ResolveFieldAttribute(QpdfObject value)
    {
        for (int depth = 0; value is PdfReference reference; depth++)
        {
            _governor.CheckCancellation(); _governor.NoteOperation();
            if (depth >= _limits.MaxNestingDepth)
            { throw new QpdfParseException("Field attribute reference chain exceeds the copy nesting limit.", null, reference.ObjectNumber); }
            if (!_resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is null) { return PdfNull.Instance; }
            value = resolved;
        }
        return value;
    }

    // JSON serializes every mapped object, including loose values. References
    // need no graph walk: each target is already in this pool and is visited once.
    // Direct containers still require their ordinary bounded syntax traversal.
    internal void ValidatePublishedModel(IReadOnlyDictionary<int, QpdfObject> objects, PdfDictionary catalog)
    {
        _governor.CheckCancellation();
        if (PdfDictionaryLookup.TryGet(catalog, "AcroForm"u8, _governor, _governor.CancellationToken, out QpdfObject raw)
            && ResolveFieldAttribute(raw) is PdfDictionary form
            && PdfDictionaryLookup.TryGet(form, "Fields"u8, _governor, _governor.CancellationToken, out QpdfObject fieldRoots)
            && ResolveFieldAttribute(fieldRoots) is PdfArray fields)
        {
            for (int index = 0; index < fields.Count; index++)
            { _governor.CheckCancellation(); ValidateField(fields[index]); }
        }
        foreach (QpdfObject value in objects.Values)
        { _governor.CheckCancellation(); ValidatePublishedValue(value, 0); }
    }

    private void ValidatePublishedValue(QpdfObject value, int depth)
    {
        _governor.CheckCancellation();
        if (depth > _limits.MaxNestingDepth)
        { throw new QpdfParseException("Published object exceeds the copy nesting limit.", null, null); }
        if (value is PdfStream stream) { value = stream.Dictionary; }
        if (value is PdfDictionary dictionary)
        {
            RejectDictionary(dictionary);
            using PdfDictionary.EntryEnumerator entries = dictionary.GetEntryEnumerator();
            while (entries.MoveNext(_governor, _governor.CancellationToken))
            { ValidatePublishedValue(entries.Current.Value, depth + 1); }
        }
        else if (value is PdfArray array)
        {
            for (int index = 0; index < array.Count; index++)
            { ValidatePublishedValue(array[index], depth + 1); }
        }
    }

    internal bool IsPageTreeNode(PdfDictionary dictionary)
    {
        string type = ReadType(dictionary);
        return type == "Page" || type == "Pages" || type == "Catalog";
    }

    internal string ReadType(PdfDictionary dictionary)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, "Type"u8, _governor, _governor.CancellationToken, out QpdfObject raw) || raw is PdfNull)
        {
            return string.Empty;
        }

        QpdfObject? resolved = raw;
        if (raw is PdfReference reference
            && !PdfObjectLookup.TryResolve(reference, _resolver, _governor, _governor.CancellationToken, out resolved))
        { return string.Empty; }
        if (resolved is not PdfName name) { return string.Empty; }
        return name.Equals(PageType) ? "Page" : name.Equals(PagesType) ? "Pages"
            : name.Equals(CatalogType) ? "Catalog" : string.Empty;
    }
}
