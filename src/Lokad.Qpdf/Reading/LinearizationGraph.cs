using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Reading;

/// <summary>Shares logical classification while resolving actual physical owners.</summary>
internal static class LinearizationGraph
{
    internal static ClassifiedGraph Classify(DocumentEngine engine, int lindictObject, int hintObject,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        memory.CheckOwner(governor);
        using ParserMemoryScope temporary = ParserMemoryScope.Create(governor);
        GovernedIntDictionary<QpdfObject> lookup = GovernedIntDictionary<QpdfObject>.Create(temporary, governor, IntPtr.Size, cancellationToken);
        GovernedIntDictionary<int> owners = GovernedIntDictionary<int>.Create(temporary, governor, sizeof(int), cancellationToken);
        long overflowOffset = OverflowOffset(engine, lindictObject, governor, cancellationToken);
        foreach (KeyValuePair<int, XrefEntry> entry in engine.XRefEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (entry.Key <= 0 || entry.Value.Kind == XrefEntryKind.Free
                || entry.Key == lindictObject || entry.Key == hintObject
                || entry.Value.Kind == XrefEntryKind.Uncompressed && entry.Value.First == overflowOffset) { continue; }
            QpdfObject value = engine.Load(entry.Key);
            if (value is null or PdfNull) { continue; }
            if (value is PdfStream stream && PdfDictionaryLookup.TryGet(stream.Dictionary, "Type"u8,
                governor, cancellationToken, out QpdfObject type) && type is PdfName name && name.RawSpan.SequenceEqual("XRef"u8)) { continue; }
            lookup.TryAdd(temporary, entry.Key, value);
            if (entry.Value.Kind == XrefEntryKind.Compressed)
            {
                owners.TryAdd(temporary, entry.Key, checked((int)entry.Value.First));
            }
        }
        if (!PdfDictionaryLookup.TryGet(engine.Trailer, "Root"u8, governor, cancellationToken, out QpdfObject rootRaw)
            || rootRaw is not PdfReference rootReference
            || !lookup.TryGetValue(rootReference.ObjectNumber, out QpdfObject? catalog) || catalog is not PdfDictionary)
        {
            throw new QpdfParseException("Linearization needs a reachable indirect catalog dictionary.", null, null);
        }
        return LinearizedParts.ClassifyGraph(lookup, rootReference.ObjectNumber, owners,
            Array.Empty<int>(), memory, governor, cancellationToken);
    }

    internal static long OverflowOffset(DocumentEngine engine, int lindictObject,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        if (engine.Load(lindictObject) is PdfDictionary dictionary
            && PdfDictionaryLookup.TryGet(dictionary, "H"u8, governor, cancellationToken, out QpdfObject hints)
            && hints is PdfArray { Count: 4 } spans && spans[2] is PdfInteger offset)
        {
            return offset.Value;
        }
        return -1L;
    }
}
