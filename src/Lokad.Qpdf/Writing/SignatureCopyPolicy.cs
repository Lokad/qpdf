using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Writing;

/// <summary>Bounded field-type resolution for signature disabling in derived output.</summary>
internal sealed class SignatureCopyPolicy
{
    private static readonly PdfName ParentKey = PdfName.FromAscii("Parent");
    private static readonly PdfName PageType = PdfName.FromAscii("Page");
    private static readonly PdfName PagesType = PdfName.FromAscii("Pages");
    private static readonly PdfName CatalogType = PdfName.FromAscii("Catalog");
    private static readonly PdfName FieldTypeKey = PdfName.FromAscii("FT");
    private static readonly PdfName SignatureType = PdfName.FromAscii("Sig");
    private const int UnresolvedFieldType = -2;
    private const int ResolvingFieldType = -1;
    private readonly IIndirectResolver _resolver;
    private readonly ResourceGovernor _governor;
    private readonly QpdfLimits _limits;
    private readonly ParserMemoryScope _ownership;
    private Dictionary<PdfDictionary, (bool Signature, int Distance)>? _fieldTypes;
    private List<PdfDictionary>? _fieldPath;

    internal SignatureCopyPolicy(IIndirectResolver resolver, ResourceGovernor governor,
        QpdfLimits limits, ParserMemoryScope ownership)
    {
        ownership.CheckOwner(governor);
        _resolver = resolver; _governor = governor; _limits = limits; _ownership = ownership;
    }

    internal bool IsSignatureField(PdfDictionary dictionary)
    {
        _governor.CheckCancellation();
        if (_fieldTypes is null)
        {
            _ownership.Reserve(96L);
            _fieldPath = ParserContainers.CreateList<PdfDictionary>(_ownership);
            _fieldTypes = new(ReferenceEqualityComparer.Instance);
        }
        return !IsPageTreeNode(dictionary) && ResolveFieldType(dictionary).Signature;
    }

    private (bool Signature, int Distance) ResolveFieldType(PdfDictionary dictionary)
    {
        Dictionary<PdfDictionary, (bool Signature, int Distance)> types =
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
                { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), path.Count, _limits.MaxNestingDepth); }
                ParserContainers.AddItem(_ownership, path, current, IntPtr.Size);
                if (!found) { ParserContainers.EnsureEntryCapacity(_ownership, types, 16); }
                types[current] = (false, ResolvingFieldType);
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
                { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), distance, _limits.MaxNestingDepth); }
                types[path[i]] = (signature, distance);
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
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth + 1L, _limits.MaxNestingDepth); }
            if (!_resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is null) { return PdfNull.Instance; }
            value = resolved;
        }
        return value;
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
