using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Writing;

/// <summary>
/// Shared object-identity remapping for publication paths that renumber the
/// destination graph (split groups, QDF length insertion). References remap
/// through the table; immutable containers rebuild only along changed paths.
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

            if (mapped == reference.ObjectNumber && reference.Generation == 0) { return reference; }
            memory?.Reserve(32L);
            return new PdfReference(mapped, 0);
        }

        if (value is PdfArray array)
        {
            if (memory is not null) { governor.ChargeWork(array.Count); }
            QpdfObject[]? items = null;
            for (int i = 0; i < array.Count; i++)
            {
                QpdfObject mapped = RemapIds(array[i], renumber, memory, governor, cancellationToken, checked(depth + 1L));
                if (items is null && !ReferenceEquals(mapped, array[i]))
                {
                    memory?.Reserve(checked(64L + IntPtr.Size * (long)array.Count));
                    items = new QpdfObject[array.Count];
                    governor.ChargeWork(i);
                    for (int prefix = 0; prefix < i; prefix++)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
                        items[prefix] = array[prefix];
                    }
                }
                if (items is not null) { items[i] = mapped; }
            }
            return items is null ? array : PdfArray.FromOwnedItems(items);
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

            if (ReferenceEquals(remapped, stream.Dictionary)) { return stream; }
            memory?.Reserve(32L);
            return stream.WithDictionary(remapped);
        }

        return value;
    }

    private static PdfDictionary RemapDictionary(PdfDictionary dictionary, NumberMap renumber,
        ParserMemoryScope? memory, ResourceGovernor governor, CancellationToken cancellationToken, long depth, int? lengthNumber)
    {
        IReadOnlyDictionary<PdfName, QpdfObject>? entries = lengthNumber.HasValue
            ? CreateEntries(memory, governor, cancellationToken, checked(dictionary.Count + 1)) : null;
        int processed = 0;
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
                if (entries is null && !ReferenceEquals(mapped, entry.Value))
                { entries = CopyPrefix(dictionary, processed, memory, governor, cancellationToken); }
                if (entries is not null) { AddEntry(memory, governor, cancellationToken, entries, entry.Key, mapped); }
            }
            else if (entries is null)
            { entries = CopyPrefix(dictionary, processed, memory, governor, cancellationToken); }
            processed++;
        }
        if (lengthNumber.HasValue)
        {
            entries ??= CreateEntries(memory, governor, cancellationToken, checked(dictionary.Count + 1));
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(7L);
            if (lengthKey is null)
            {
                // Admit the fixed lookup name before creating its owned bytes.
                memory?.Reserve(108L);
                lengthKey = PdfName.FromAscii("Length");
                memory?.Release(38L);
            }
            memory?.Reserve(32L);
            PdfReference length = new(lengthNumber.Value, 0);
            AddEntry(memory, governor, cancellationToken, entries, lengthKey, length);
        }
        if (entries is null) { return dictionary; }
        memory?.Reserve(32L);
        if (memory is not null && entries is PdfNameTable<QpdfObject> owned)
        { owned.Freeze(memory, governor, cancellationToken); return PdfDictionary.FromOwnedEntries(owned); }
        return PdfDictionary.FromOwnedEntries((Dictionary<PdfName, QpdfObject>)entries);
    }

    private static IReadOnlyDictionary<PdfName, QpdfObject> CreateEntries(ParserMemoryScope? memory,
        ResourceGovernor governor, CancellationToken token, int count)
    {
        if (memory is null) { return new Dictionary<PdfName, QpdfObject>(); }
        PdfNameTable<QpdfObject> names = PdfNameTable<QpdfObject>.Create(memory, governor, IntPtr.Size, token);
        names.EnsureCapacity(memory, governor, count, token);
        return names;
    }

    private static IReadOnlyDictionary<PdfName, QpdfObject> CopyPrefix(PdfDictionary dictionary, int count,
        ParserMemoryScope? memory, ResourceGovernor governor, CancellationToken token)
    {
        // Earlier children were validated and stayed identical. Their immutable
        // storage remains owned by the source model through publication.
        IReadOnlyDictionary<PdfName, QpdfObject> entries = CreateEntries(memory, governor, token, dictionary.Count);
        using PdfDictionary.EntryEnumerator prefix = dictionary.GetEntryEnumerator();
        for (int index = 0; index < count && prefix.MoveNext(); index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            KeyValuePair<PdfName, QpdfObject> entry = prefix.Current;
            if (entry.Value is not null) { AddEntry(memory, governor, token, entries, entry.Key, entry.Value); }
        }
        return entries;
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
