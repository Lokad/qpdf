using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Security;

/// <summary>Validates and consumes the leading crypt stage without changing payload bytes.</summary>
internal static class StreamCryptFilter
{
    internal static readonly PdfName IdentityName = PdfName.FromAscii("Identity");

    internal static bool MayContain(PdfStream stream, ResourceGovernor governor)
    {
        CancellationToken token = governor.CancellationToken;
        if (!PdfDictionaryLookup.TryGet(stream.Dictionary, "Filter"u8, governor, token, out QpdfObject raw)
            || raw is PdfNull) { return false; }
        if (raw is PdfName name) { return PdfDictionaryLookup.NameEquals(name, "Crypt"u8, governor, token); }
        if (raw is not PdfArray array) { return true; }
        for (int index = 0; index < array.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (array[index] is not PdfName entry || PdfDictionaryLookup.NameEquals(entry, "Crypt"u8, governor, token)) { return true; }
        }
        return false;
    }

    // Copying may keep opaque streams without interpreting their filters. Only
    // a fully validated no-op can change before content transforms; output
    // security still rejects unconsumed/unknown stages before publication.
    internal static PdfDictionary RemoveIdentity(PdfStream stream, IIndirectResolver resolver, OwnedAllocationBatch graph)
    {
        if (!MayContain(stream, graph.Governor)) { return stream.Dictionary; }
        PdfName? name;
        int count;
        try { name = ReadName(stream.Dictionary, resolver, graph.Governor, graph.Token, out count); }
        catch (QpdfParseException) { return stream.Dictionary; }
        if (name is null || !PdfDictionaryLookup.NameEquals(name, "Identity"u8, graph.Governor, graph.Token))
        { return stream.Dictionary; }
        return Remove(stream.Dictionary, count, resolver, graph);
    }

    internal static PdfName? ReadName(PdfDictionary header, IIndirectResolver resolver,
        ResourceGovernor governor, CancellationToken token, out int count)
    {
        using FilterPlan filters = FilterChain.ResolveScoped(header, resolver, governor, token);
        count = filters.Count;
        bool crypt = false;
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (filters[index].Name != "Crypt") { continue; }
            if (index != 0 || crypt)
            { throw new QpdfParseException("A stream Crypt filter must occur only once and before decoding filters.", null, null); }
            crypt = true;
        }
        if (!crypt) { return null; }
        PdfDictionary? parms = filters[0].Parms;
        if (parms is null || !PdfDictionaryLookup.TryGet(parms, "Name"u8, governor, token, out QpdfObject rawName))
        { return IdentityName; }
        QpdfObject name = Resolve(rawName, resolver, governor, token);
        return name is PdfNull ? IdentityName : name as PdfName
            ?? throw new QpdfParseException("A Crypt filter Name must be a name.", null, null);
    }

    internal static PdfDictionary Remove(PdfDictionary header, int count, IIndirectResolver resolver, OwnedAllocationBatch graph)
    {
        ResourceGovernor governor = graph.Governor; CancellationToken token = graph.Token;
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (count == 1)
        {
            header = PdfDictionaryRebuild.RemoveEntry(graph, header, "Filter"u8);
            return PdfDictionaryRebuild.RemoveEntry(graph, header, "DecodeParms"u8);
        }
        if (!PdfDictionaryLookup.TryGet(header, "Filter"u8, governor, token, out QpdfObject rawFilters)
            || Resolve(rawFilters, resolver, governor, token) is not PdfArray names)
        { throw new QpdfParseException("Invalid Crypt filter chain.", null, null); }
        header = PdfDictionaryRebuild.ReplaceEntry(graph, header, "Filter"u8, Tail(names, graph));
        if (PdfDictionaryLookup.TryGet(header, "DecodeParms"u8, governor, token, out QpdfObject rawParms)
            && Resolve(rawParms, resolver, governor, token) is PdfArray parameters && parameters.Count > 0)
        { header = PdfDictionaryRebuild.ReplaceEntry(graph, header, "DecodeParms"u8, Tail(parameters, graph)); }
        return header;
    }

    private static PdfArray Tail(PdfArray array, OwnedAllocationBatch graph)
    {
        long size = checked(56L + (long)(array.Count - 1) * IntPtr.Size);
        graph.Memory.Reserve(size); graph.Add(size);
        QpdfObject[] items = new QpdfObject[array.Count - 1];
        for (int index = 0; index < items.Length; index++)
        { graph.Token.ThrowIfCancellationRequested(); graph.Governor.NoteOperation(); items[index] = array[index + 1]; }
        return PdfArray.FromOwnedItems(items);
    }

    internal static QpdfObject Resolve(QpdfObject raw, IIndirectResolver resolver, ResourceGovernor governor, CancellationToken token)
    {
        for (int depth = 0; raw is PdfReference reference; depth++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (depth >= governor.MaxNestingDepth)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth + 1L, governor.MaxNestingDepth); }
            if (!PdfObjectLookup.TryResolve(reference, resolver, governor, token, out QpdfObject? value) || value is null)
            { throw new QpdfParseException("Unresolvable crypt-filter parameter.", null, reference.ObjectNumber); }
            raw = value;
        }
        return raw;
    }
}
