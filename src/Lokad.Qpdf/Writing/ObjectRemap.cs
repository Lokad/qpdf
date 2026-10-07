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
        return RemapIds(value, new NumberMap(renumber, null), null, governor, governor.CancellationToken, 0L);
    }

    internal static QpdfObject RemapIds(QpdfObject value, GovernedIntDictionary<int> renumber, ResourceGovernor governor)
    {
        ArgumentNullException.ThrowIfNull(renumber);
        ArgumentNullException.ThrowIfNull(governor);
        renumber.CheckOwner(governor);
        return RemapIds(value, new NumberMap(null, renumber), null, governor, governor.CancellationToken, 0L);
    }

    internal static QpdfObject RemapIds(QpdfObject value, GovernedIntDictionary<int> renumber,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        memory.CheckOwner(governor);
        renumber.CheckOwner(governor);
        return RemapIds(value, new NumberMap(null, renumber), memory, governor, cancellationToken, 0L);
    }

    private readonly record struct NumberMap(Dictionary<int, int>? Plain, GovernedIntDictionary<int>? Governed)
    {
        internal bool TryGetValue(int number, out int mapped)
        {
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
            Dictionary<PdfName, QpdfObject> entries = memory is null
                ? new() : ParserContainers.CreateDictionary<PdfName, QpdfObject>(memory);
            using Dictionary<PdfName, QpdfObject>.Enumerator source = dictionary.GetEntryEnumerator();
            while (source.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                KeyValuePair<PdfName, QpdfObject> entry = source.Current;
                if (entry.Value is not null)
                {
                    QpdfObject mapped = RemapIds(entry.Value, renumber, memory, governor, cancellationToken, checked(depth + 1L));
                    if (memory is not null)
                    {
                        if (entries.Count == entries.EnsureCapacity(0)) { governor.ChargeWork(checked(4L * (entries.Count + 1L))); }
                        ParserContainers.EnsureEntryCapacity(memory, entries, IntPtr.Size);
                    }
                    // Name-key hashing/collision work retains the shared dictionary audit.
                    governor.ChargeWork(checked(Math.Min(entry.Key.RawSpan.Length, 16) + 1L));
                    entries.Add(entry.Key, mapped);
                }
            }
            memory?.Reserve(32L);
            return PdfDictionary.FromOwnedEntries(entries);
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
}
