using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

/// <summary>Shared bounded lookup of a page attribute through its ancestry.</summary>
internal static class PageInheritance
{
    internal static QpdfObject? Find(DocumentEngine engine, PageLocation location, PdfName name,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        HashSet<(int Number, int Generation)> visited = ParserContainers.CreateSet<(int, int)>(memory);
        if (location.Source is not null)
        {
            ParserContainers.AddSetItem(memory, visited, (location.Source.ObjectNumber, location.Source.Generation), 8L);
        }
        PdfDictionary current = location.Page;
        for (int depth = 0; ; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (PdfDictionaryLookup.TryGet(current, name.RawSpan, governor, cancellationToken, out QpdfObject value)
                && Resolve(engine, value, limits, governor) is not PdfNull) { return value; }
            if (!PdfDictionaryLookup.TryGet(current, "Parent"u8, governor, cancellationToken, out QpdfObject raw) || raw is PdfNull) { return null; }
            if (depth >= limits.MaxNestingDepth) { throw new QpdfParseException("Page ancestry exceeds the nesting limit.", null, null); }
            if (raw is PdfDictionary direct) { current = direct; }
            else if (raw is PdfReference reference)
            {
                if (!ParserContainers.AddSetItem(memory, visited, (reference.ObjectNumber, reference.Generation), 8L))
                {
                    throw new QpdfParseException("Page ancestry contains a cycle.", null, reference.ObjectNumber);
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
