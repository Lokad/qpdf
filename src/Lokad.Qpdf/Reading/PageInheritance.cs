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

    internal static bool HasDictionaryAncestor(DocumentEngine engine, PageLocation location, ReadOnlySpan<byte> name,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
        => FindCore(engine, location, name, true, true, limits, governor, cancellationToken) is not null;

    private static QpdfObject? FindCore(DocumentEngine engine, PageLocation location, ReadOnlySpan<byte> name,
        bool dictionaryOnly, bool skipLeaf, QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ParserMemoryScope? memory = null;
        try
        {
            HashSet<(int Number, int Generation)>? visited = null;
            PdfReference? firstParent = null;
            PdfDictionary current = location.Page;
            for (int depth = 0; ; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.CheckCancellation();
                governor.ChargeWork(1L);
                if ((!skipLeaf || depth != 0) && PdfDictionaryLookup.TryGet(current, name, governor, cancellationToken, out QpdfObject value))
                {
                    QpdfObject resolved = Resolve(engine, value, limits, governor);
                    if (resolved is not PdfNull && (!dictionaryOnly || resolved is PdfDictionary)) { return value; }
                }
                if (!PdfDictionaryLookup.TryGet(current, "Parent"u8, governor, cancellationToken, out QpdfObject raw) || raw is PdfNull) { return null; }
                if (depth >= limits.MaxNestingDepth) { throw new QpdfParseException("Page ancestry exceeds the nesting limit.", null, null); }
                if (raw is PdfDictionary direct) { current = direct; }
                else if (raw is PdfReference reference)
                {
                    // A single parent can be checked without traversal storage.
                    // Root-level optional attributes normally end after this hop.
                    if (firstParent is null)
                    {
                        if (location.Source is PdfReference source && reference.Equals(source))
                        { throw new QpdfParseException("Page ancestry contains a cycle.", null, reference.ObjectNumber); }
                        firstParent = reference;
                    }
                    else
                    {
                        ParserMemoryScope owner = memory ??= ParserMemoryScope.Create(governor);
                        if (visited is null)
                        {
                            visited = ParserContainers.CreateSet<(int, int)>(owner);
                            if (location.Source is not null)
                            { ParserContainers.AddSetItem(owner, visited, (location.Source.ObjectNumber, location.Source.Generation), 8L); }
                            ParserContainers.AddSetItem(owner, visited, (firstParent.ObjectNumber, firstParent.Generation), 8L);
                        }
                        if (!ParserContainers.AddSetItem(owner, visited, (reference.ObjectNumber, reference.Generation), 8L))
                        { throw new QpdfParseException("Page ancestry contains a cycle.", null, reference.ObjectNumber); }
                    }
                    if (!((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? resolved) || resolved is not PdfDictionary parent)
                    {
                        throw new QpdfParseException("Unresolvable page parent.", null, reference.ObjectNumber);
                    }
                    current = parent;
                }
                else { throw new QpdfParseException("Page parent must be a dictionary or a reference.", null, null); }
            }
        }
        finally { memory?.Dispose(); }
    }

    internal static QpdfObject Resolve(DocumentEngine engine, QpdfObject value, QpdfLimits limits, ResourceGovernor governor)
    {
        for (int depth = 0; value is PdfReference reference; depth++)
        {
            governor.ChargeWork(1L);
            if (depth >= limits.MaxNestingDepth) { throw new QpdfParseException("Attribute reference chain exceeds the nesting limit.", null, null); }
            if (!((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? resolved) || resolved is null) { return PdfNull.Instance; }
            value = resolved;
        }
        return value;
    }
}
