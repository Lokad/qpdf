using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Security;

internal sealed partial class DocumentSecurity
{
    private PdfDictionary ReadStreamCipher(PdfDictionary header, IIndirectResolver resolver, bool embedded,
        OwnedAllocationBatch graph, ref PdfCipher cipher)
    {
        using FilterPlan filters = FilterChain.ResolveScoped(header, resolver, _governor, graph.Token);
        bool crypt = false;
        for (int index = 0; index < filters.Count; index++)
        {
            _governor.CheckCancellation();
            if (filters[index].Name != "Crypt") { continue; }
            if (index != 0 || crypt)
            { throw new QpdfParseException("A stream Crypt filter must occur only once and before decoding filters.", null, null); }
            crypt = true;
        }
        if (!crypt) { return header; }
        PdfDictionary? parms = filters[0].Parms;
        PdfName name = IdentityName;
        if (parms is not null && TryGet(parms, "Name"u8, out QpdfObject rawName))
        {
            QpdfObject resolved = Resolve(rawName, resolver);
            name = resolved is PdfNull ? IdentityName : resolved as PdfName
                ?? throw new QpdfParseException("A Crypt filter Name must be a name.", null, null);
        }
        cipher = Select(_filters, name, embedded);
        if (filters.Count == 1)
        {
            header = PdfDictionaryRebuild.RemoveEntry(graph, header, "Filter"u8);
            return PdfDictionaryRebuild.RemoveEntry(graph, header, "DecodeParms"u8);
        }
        if (!TryGet(header, "Filter"u8, out QpdfObject rawFilters) || Resolve(rawFilters, resolver) is not PdfArray names)
        { throw new QpdfParseException("Invalid Crypt filter chain.", null, null); }
        header = PdfDictionaryRebuild.ReplaceEntry(graph, header, "Filter"u8, Tail(names, graph));
        if (TryGet(header, "DecodeParms"u8, out QpdfObject rawParms) && Resolve(rawParms, resolver) is PdfArray parameters)
        { header = PdfDictionaryRebuild.ReplaceEntry(graph, header, "DecodeParms"u8, Tail(parameters, graph)); }
        return header;
    }

    private PdfArray Tail(PdfArray array, OwnedAllocationBatch graph)
    {
        long size = checked(56L + (long)(array.Count - 1) * IntPtr.Size);
        graph.Memory.Reserve(size); graph.Add(size);
        QpdfObject[] items = new QpdfObject[array.Count - 1];
        for (int index = 0; index < items.Length; index++)
        { _governor.NoteOperation(); items[index] = array[index + 1]; }
        return PdfArray.FromOwnedItems(items);
    }

    private QpdfObject Resolve(QpdfObject raw, IIndirectResolver resolver)
    {
        for (int depth = 0; raw is PdfReference reference; depth++)
        {
            if (depth >= _governor.MaxNestingDepth)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth + 1L, _governor.MaxNestingDepth); }
            if (!PdfObjectLookup.TryResolve(reference, resolver, _governor, _governor.CancellationToken, out QpdfObject? value) || value is null)
            { throw new QpdfParseException("Unresolvable crypt-filter parameter.", null, reference.ObjectNumber); }
            raw = value;
        }
        return raw;
    }
}
