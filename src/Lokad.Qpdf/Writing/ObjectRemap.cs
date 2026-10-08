using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Writing;

/// <summary>
/// Shared object-identity remapping for publication paths that renumber the
/// destination graph (split groups, QDF length insertion). References remap
/// through the table; arrays, dictionaries and stream headers rebuild;
/// anything else passes through.
/// </summary>
internal static class ObjectRemap
{
    internal static QpdfObject RemapIds(QpdfObject value, Dictionary<int, int> renumber, ResourceGovernor governor)
    {
        ArgumentNullException.ThrowIfNull(renumber);
        ArgumentNullException.ThrowIfNull(governor);
        return RemapIds(value, new NumberMap(renumber, null, null), null, governor, governor.CancellationToken, 0L);
    }

    internal static QpdfObject RemapIds(QpdfObject value, GovernedIntDictionary<int> renumber, ResourceGovernor governor)
    {
        ArgumentNullException.ThrowIfNull(renumber);
        ArgumentNullException.ThrowIfNull(governor);
        renumber.CheckOwner(governor);
        return RemapIds(value, new NumberMap(null, renumber, null), null, governor, governor.CancellationToken, 0L);
    }

    internal static QpdfObject RemapIds(QpdfObject value, GovernedIntDictionary<int> renumber,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        memory.CheckOwner(governor);
        renumber.CheckOwner(governor);
        return RemapIds(value, new NumberMap(null, renumber, null), memory, governor, cancellationToken, 0L);
    }

    internal static QpdfObject RemapIds(QpdfObject value, WriterIndex<int> renumber,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(renumber);
        ArgumentNullException.ThrowIfNull(governor);
        renumber.CheckOwner(governor);
        return RemapIds(value, new NumberMap(null, null, renumber), null, governor, cancellationToken, 0L);
    }

    internal static QpdfObject RemapIds(QpdfObject value, WriterIndex<int> renumber,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        memory.CheckOwner(governor);
        renumber.CheckOwner(governor);
        return RemapIds(value, new NumberMap(null, null, renumber), memory, governor, cancellationToken, 0L);
    }

    internal static PdfDictionary RemapStreamHeader(PdfDictionary header, int lengthNumber,
        WriterIndex<int> renumber, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        memory.CheckOwner(governor); renumber.CheckOwner(governor);
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(1L);
        return RemapDictionary(header, new NumberMap(null, null, renumber), memory, governor, cancellationToken, 0L, lengthNumber);
    }

    private readonly record struct NumberMap(Dictionary<int, int>? Plain, GovernedIntDictionary<int>? Governed,
        WriterIndex<int>? Writer)
    {
        internal bool TryGetValue(int number, out int mapped)
        {
            if (Writer is not null) { return Writer.TryGetValue(number, out mapped); }
            if (Governed is not null) { return Governed.TryGetValue(number, out mapped); }
            if (Plain is not null) { return Plain.TryGetValue(number, out mapped); }
            throw new InvalidOperationException("Object remapping requires an index.");
        }
    }

    private static QpdfObject RemapIds(QpdfObject value, NumberMap renumber, ParserMemoryScope? memory,
        ResourceGovernor governor, CancellationToken cancellationToken, long depth)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        cancellationToken.ThrowIfCancellationRequested();
        governor.CheckCancellation();
        if (depth > governor.MaxNestingDepth)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxNestingDepth), depth, governor.MaxNestingDepth);
        }
        governor.ChargeWork(1L);
        if (value is PdfReference reference)
        {
            if (!renumber.TryGetValue(reference.ObjectNumber, out int mapped))
            {
                throw new QpdfException($"Publication reached unmapped object {reference.ObjectNumber}.");
            }

            memory?.Reserve(32L);
            return new PdfReference(mapped, 0);
        }

        if (value is PdfArray array)
        {
            if (memory is not null)
            {
                memory.Reserve(checked(64L + IntPtr.Size * (long)array.Count));
                governor.ChargeWork(array.Count);
                QpdfObject[] owned = new QpdfObject[array.Count];
                for (int i = 0; i < owned.Length; i++)
                {
                    owned[i] = RemapIds(array[i], renumber, memory, governor, cancellationToken, checked(depth + 1L));
                }
                return PdfArray.FromOwnedItems(owned);
            }
            List<QpdfObject> items = new(array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                items.Add(RemapIds(array[i], renumber, null, governor, cancellationToken, checked(depth + 1L)));
            }

            return new PdfArray(items);
        }

        if (value is PdfDictionary dictionary)
        {
            return RemapDictionary(dictionary, renumber, memory, governor, cancellationToken, depth, null);
        }

        if (value is PdfStream stream)
        {
            if (RemapIds(stream.Dictionary, renumber, memory, governor, cancellationToken, depth) is not PdfDictionary remapped)
            {
                throw new QpdfException("Publication reached a non-dictionary stream header.");
            }

            memory?.Reserve(32L);
            return stream.WithDictionary(remapped);
        }

        return value;
    }

    private static PdfDictionary RemapDictionary(PdfDictionary dictionary, NumberMap renumber,
        ParserMemoryScope? memory, ResourceGovernor governor, CancellationToken cancellationToken, long depth, int? lengthNumber)
    {
        IReadOnlyDictionary<PdfName, QpdfObject> entries;
        if (memory is null) { entries = new Dictionary<PdfName, QpdfObject>(); }
        else
        {
            PdfNameTable<QpdfObject> names = PdfNameTable<QpdfObject>.Create(memory, governor, IntPtr.Size, cancellationToken);
            names.EnsureCapacity(memory, governor, checked(dictionary.Count + (lengthNumber.HasValue ? 1 : 0)), cancellationToken);
            entries = names;
        }
        PdfName? lengthKey = null;
        using PdfDictionary.EntryEnumerator source = dictionary.GetEntryEnumerator();
        while (source.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(1L);
            KeyValuePair<PdfName, QpdfObject> entry = source.Current;
            if (lengthNumber.HasValue && entry.Key.RawSpan.SequenceEqual("Length"u8))
            { lengthKey = entry.Key; continue; }
            if (entry.Value is not null)
            {
                QpdfObject mapped = RemapIds(entry.Value, renumber, memory, governor, cancellationToken, checked(depth + 1L));
                AddEntry(memory, governor, cancellationToken, entries, entry.Key, mapped);
            }
        }
        if (lengthNumber.HasValue)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(7L);
            if (lengthKey is null)
            {
                // FromAscii allocates one temporary array and one immutable copy.
                memory?.Reserve(108L);
                lengthKey = PdfName.FromAscii("Length");
                memory?.Release(38L);
            }
            memory?.Reserve(32L);
            PdfReference length = new(lengthNumber.Value, 0);
            AddEntry(memory, governor, cancellationToken, entries, lengthKey, length);
        }
        memory?.Reserve(32L);
        if (memory is not null && entries is PdfNameTable<QpdfObject> owned)
        { owned.Freeze(memory, governor, cancellationToken); return PdfDictionary.FromOwnedEntries(owned); }
        return PdfDictionary.FromOwnedEntries((Dictionary<PdfName, QpdfObject>)entries);
    }

    private static void AddEntry(ParserMemoryScope? memory, ResourceGovernor governor, CancellationToken token,
        IReadOnlyDictionary<PdfName, QpdfObject> entries, PdfName key, QpdfObject value)
    {
        if (memory is not null && entries is PdfNameTable<QpdfObject> owned)
        { owned.TryAdd(memory, governor, key, value, token); return; }
        governor.ChargeWork(checked(Math.Min(key.RawSpan.Length, 16) + 1L));
        ((Dictionary<PdfName, QpdfObject>)entries).Add(key, value);
    }
}
