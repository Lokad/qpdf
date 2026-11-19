using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Writing;

internal static class ObjectStreamPreparation
{
    internal static int PageTreeRootNumber(QpdfObject? catalog, ResourceGovernor governor, CancellationToken token)
        => catalog is PdfDictionary dictionary
            && PdfDictionaryLookup.TryGet(dictionary, "Pages"u8, governor, token, out QpdfObject pages)
            && pages is PdfReference reference ? reference.ObjectNumber : 0;

    internal static string ResolveObjectStreamMode(string? raw)
    {
        if (raw is null)
        {
            return "preserve";
        }

        if (raw == "disable" || raw == "preserve" || raw == "generate")
        {
            return raw;
        }

        throw new QpdfParseException("--object-streams mode must be disable, preserve, or generate.", 0L, null);
    }

    internal static HashSet<int> CollectCompressedOriginals(DocumentEngine engine, bool preserve,
        ParserMemoryScope ownership, ResourceGovernor governor, CancellationToken token)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        ownership.CheckOwner(governor); token.ThrowIfCancellationRequested();
        using ParserMemoryScope candidate = ParserMemoryScope.Create(governor);
        HashSet<int> compressed = ParserContainers.CreateSet<int>(candidate);
        if (preserve)
        {
            foreach (KeyValuePair<int, XrefEntry> entry in engine.XRefEntries)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (entry.Value.Kind == XrefEntryKind.Compressed)
                {
                    ParserContainers.AddSetItem(candidate, compressed, entry.Key, sizeof(int));
                }
            }
        }

        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        candidate.TransferTo(ownership);
        return compressed;
    }

    internal static bool IsCompressibleValue(int number, QpdfObject value, int catalogNumber, int pagesNumber,
        ResourceGovernor governor, CancellationToken token)
    {
        if (number == catalogNumber || number == pagesNumber)
        {
            return false;
        }

        if (value is PdfStream)
        {
            return false;
        }

        if (value is PdfDictionary dictionary)
        {
            if (PdfDictionaryLookup.TryGet(dictionary, "Type"u8, governor, token, out QpdfObject typeRaw)
                && typeRaw is PdfName typeName && typeName.RawSpan.SequenceEqual("Sig"u8)
                && PdfDictionaryLookup.TryGet(dictionary, "ByteRange"u8, governor, token, out QpdfObject byteRange)
                && byteRange is not PdfNull
                && PdfDictionaryLookup.TryGet(dictionary, "Contents"u8, governor, token, out QpdfObject contents)
                && contents is not PdfNull)
            {
                return false;
            }
        }

        return true;
    }

    internal static HashSet<int> ResolveGeneratedCompressedSet(IReadOnlyDictionary<int, QpdfObject> pool,
        int catalogNumber, int pagesNumber, ParserMemoryScope ownership, ResourceGovernor governor, CancellationToken token)
    {
        ownership.CheckOwner(governor);
        using ParserMemoryScope candidate = ParserMemoryScope.Create(governor);
        HashSet<int> compressed = ParserContainers.CreateSet<int>(candidate);
        foreach ((int number, QpdfObject value) in pool)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (IsCompressibleValue(number, value, catalogNumber, pagesNumber, governor, token))
            { ParserContainers.AddSetItem(candidate, compressed, number, sizeof(int)); }
        }
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        candidate.TransferTo(ownership);
        return compressed;
    }

    internal static HashSet<int> ResolveGeneratedCompressedSet(IReadOnlyList<NumberedObject> objects,
        ParserMemoryScope ownership, ResourceGovernor governor, CancellationToken token)
    {
        HashSet<int> compressed = ParserContainers.CreateSet<int>(ownership);
        foreach (NumberedObject item in objects)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (IsCompressibleValue(item.Number, item.Value, 1, 2, governor, token))
            { ParserContainers.AddSetItem(ownership, compressed, item.Number, sizeof(int)); }
        }
        return compressed;
    }

    internal static HashSet<int> ResolveCompressedSet(
        string mode,
        IReadOnlyDictionary<int, QpdfObject> pool,
        List<(IReadOnlyDictionary<int, int> Map, HashSet<int> CompressedOrig)> sources,
        ParserMemoryScope ownership, ResourceGovernor governor, CancellationToken token)
        => ResolveCompressedSet(mode, pool, sources, 1, 2, ownership, governor, token);

    internal static HashSet<int> ResolveCompressedSet(
        string mode,
        IReadOnlyDictionary<int, QpdfObject> pool,
        List<(IReadOnlyDictionary<int, int> Map, HashSet<int> CompressedOrig)> sources,
        int catalogNumber, int pagesNumber,
        ParserMemoryScope ownership, ResourceGovernor governor, CancellationToken token)
    {
        if (mode is null)
        {
            throw new ArgumentNullException(nameof(mode));
        }

        if (pool is null)
        {
            throw new ArgumentNullException(nameof(pool));
        }

        if (sources is null)
        {
            throw new ArgumentNullException(nameof(sources));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        ownership.CheckOwner(governor); token.ThrowIfCancellationRequested();
        if (mode == "generate")
        { return ResolveGeneratedCompressedSet(pool, catalogNumber, pagesNumber, ownership, governor, token); }
        using ParserMemoryScope candidate = ParserMemoryScope.Create(governor);
        HashSet<int> compressed = ParserContainers.CreateSet<int>(candidate);
        if (mode == "disable")
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            candidate.TransferTo(ownership);
            return compressed;
        }

        if (mode != "preserve")
        {
            throw new QpdfParseException("--object-streams mode must be disable, preserve, or generate.", 0L, null);
        }

        foreach ((IReadOnlyDictionary<int, int> map, HashSet<int> origs) in sources)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            foreach (int orig in origs)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (map.TryGetValue(orig, out int dest)
                    && pool.TryGetValue(dest, out QpdfObject? value)
                    && value is not null
                    && IsCompressibleValue(dest, value, catalogNumber, pagesNumber, governor, token))
                {
                    ParserContainers.AddSetItem(candidate, compressed, dest, sizeof(int));
                }
            }
        }

        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        candidate.TransferTo(ownership);
        return compressed;
    }

    internal static HashSet<int> RemapCompressedSet(HashSet<int> compressed, Dictionary<int, int> renumber,
        ParserMemoryScope ownership, ResourceGovernor governor, CancellationToken token)
    {
        if (compressed is null)
        {
            throw new ArgumentNullException(nameof(compressed));
        }

        if (renumber is null)
        {
            throw new ArgumentNullException(nameof(renumber));
        }

        ownership.CheckOwner(governor); token.ThrowIfCancellationRequested();
        using ParserMemoryScope candidate = ParserMemoryScope.Create(governor);
        HashSet<int> remapped = ParserContainers.CreateSet<int>(candidate);
        if (compressed.Count <= renumber.Count)
        {
            foreach (int number in compressed)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.NoteOperation();
                if (renumber.TryGetValue(number, out int mapped))
                { ParserContainers.AddSetItem(candidate, remapped, mapped, sizeof(int)); }
            }
        }
        else
        {
            foreach ((int number, int mapped) in renumber)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.NoteOperation();
                if (compressed.Contains(number))
                { ParserContainers.AddSetItem(candidate, remapped, mapped, sizeof(int)); }
            }
        }

        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        candidate.TransferTo(ownership);
        return remapped;
    }

}
