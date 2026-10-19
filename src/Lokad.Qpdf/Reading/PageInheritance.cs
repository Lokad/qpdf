using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

/// <summary>Shared bounded lookup of a page attribute through its ancestry.</summary>
internal static class PageInheritance
{
    internal static QpdfObject? Find(DocumentEngine engine, PageLocation location, PdfName name,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
        => FindCore(engine, location, name.RawSpan, false, false, limits, governor, cancellationToken);

    [InlineArray(4)]
    internal struct InheritedValues
    {
        private QpdfObject? _first;
    }

    // Copying needs the same four attributes. Walk each parent once, retaining
    // only the nearest non-null value for keys absent (or null) on the leaf.
    internal static InheritedValues FindAncestors(DocumentEngine engine, PageLocation location,
        ReadOnlySpan<PdfName> names, QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        if (names.Length > 4) { throw new ArgumentOutOfRangeException(nameof(names)); }
        InheritedValues values = default;
        int missing = 0;
        for (int index = 0; index < names.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (!location.Page.TryGet(names[index], out QpdfObject value)
                || Resolve(engine, value, limits, governor) is PdfNull) { missing |= 1 << index; }
        }
        if (missing == 0) { return values; }
        Ancestors ancestors = new(engine, location, limits, governor, cancellationToken);
        try
        {
            ancestors.MoveNext(out _); // The leaf has already been checked.
            while (ancestors.MoveNext(out PdfDictionary current))
            {
                for (int index = 0; index < names.Length; index++)
                {
                    if ((missing & (1 << index)) == 0) { continue; }
                    if (PdfDictionaryLookup.TryGet(current, names[index].RawSpan, governor, cancellationToken, out QpdfObject value)
                        && Resolve(engine, value, limits, governor) is not PdfNull)
                    { values[index] = value; missing &= ~(1 << index); }
                }
                if (missing == 0) { break; }
            }
            return values;
        }
        finally { ancestors.Dispose(); }
    }

    internal static bool HasDictionaryAncestor(DocumentEngine engine, PageLocation location, ReadOnlySpan<byte> name,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
        => FindCore(engine, location, name, true, true, limits, governor, cancellationToken) is not null;

    private static QpdfObject? FindCore(DocumentEngine engine, PageLocation location, ReadOnlySpan<byte> name,
        bool dictionaryOnly, bool skipLeaf, QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        Ancestors ancestors = new(engine, location, limits, governor, cancellationToken);
        try
        {
            bool leaf = true;
            while (ancestors.MoveNext(out PdfDictionary current))
            {
                if ((!skipLeaf || !leaf) && PdfDictionaryLookup.TryGet(current, name, governor, cancellationToken, out QpdfObject value))
                {
                    QpdfObject resolved = Resolve(engine, value, limits, governor);
                    if (resolved is not PdfNull && (!dictionaryOnly || resolved is PdfDictionary)) { return value; }
                }
                leaf = false;
            }
            return null;
        }
        finally { ancestors.Dispose(); }
    }

    // Single-key inspection and multi-key copying share parent resolution,
    // cycle detection, depth admission and scoped traversal storage.
    private ref struct Ancestors
    {
        private readonly DocumentEngine _engine;
        private readonly PageLocation _location;
        private readonly QpdfLimits _limits;
        private readonly ResourceGovernor _governor;
        private readonly CancellationToken _token;
        private PdfDictionary _current;
        private int _depth;
        private bool _started;
        private ParserMemoryScope? _memory;
        private HashSet<(int Number, int Generation)>? _visited;
        private PdfReference? _firstParent;

        internal Ancestors(DocumentEngine engine, PageLocation location, QpdfLimits limits,
            ResourceGovernor governor, CancellationToken token)
        {
            _engine = engine; _location = location; _limits = limits;
            _governor = governor; _token = token; _current = location.Page;
        }

        internal bool MoveNext(out PdfDictionary current)
        {
            _token.ThrowIfCancellationRequested(); _governor.CheckCancellation();
            if (_started)
            {
                if (!PdfDictionaryLookup.TryGet(_current, "Parent"u8, _governor, _token, out QpdfObject raw) || raw is PdfNull)
                { current = _current; return false; }
                if (_depth >= _limits.MaxNestingDepth)
                { throw new QpdfParseException("Page ancestry exceeds the nesting limit.", null, null); }
                if (raw is PdfDictionary direct) { _current = direct; }
                else if (raw is PdfReference reference)
                {
                    // A single parent needs no traversal allocation.
                    if (_firstParent is null)
                    {
                        if (_location.Source is PdfReference source && reference.Equals(source))
                        { throw new QpdfParseException("Page ancestry contains a cycle.", null, reference.ObjectNumber); }
                        _firstParent = reference;
                    }
                    else
                    {
                        ParserMemoryScope owner = _memory ??= ParserMemoryScope.Create(_governor);
                        if (_visited is null)
                        {
                            _visited = ParserContainers.CreateSet<(int, int)>(owner);
                            if (_location.Source is not null)
                            { ParserContainers.AddSetItem(owner, _visited, (_location.Source.ObjectNumber, _location.Source.Generation), 8L); }
                            ParserContainers.AddSetItem(owner, _visited, (_firstParent.ObjectNumber, _firstParent.Generation), 8L);
                        }
                        if (!ParserContainers.AddSetItem(owner, _visited, (reference.ObjectNumber, reference.Generation), 8L))
                        { throw new QpdfParseException("Page ancestry contains a cycle.", null, reference.ObjectNumber); }
                    }
                    if (!((IIndirectResolver)_engine).TryResolve(reference, out QpdfObject? resolved) || resolved is not PdfDictionary parent)
                    { throw new QpdfParseException("Unresolvable page parent.", null, reference.ObjectNumber); }
                    _current = parent;
                }
                else { throw new QpdfParseException("Page parent must be a dictionary or a reference.", null, null); }
                _depth++;
            }
            _started = true; _governor.NoteOperation(); current = _current; return true;
        }

        internal void Dispose() => _memory?.Dispose();
    }

    internal static QpdfObject Resolve(DocumentEngine engine, QpdfObject value, QpdfLimits limits, ResourceGovernor governor)
    {
        for (int depth = 0; value is PdfReference reference; depth++)
        {
            governor.NoteOperation();
            if (depth >= limits.MaxNestingDepth) { throw new QpdfParseException("Attribute reference chain exceeds the nesting limit.", null, null); }
            if (!((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? resolved) || resolved is null) { return PdfNull.Instance; }
            value = resolved;
        }
        return value;
    }
}
