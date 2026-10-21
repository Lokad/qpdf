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
    internal static QpdfObject RemapImported(QpdfObject value, Dictionary<int, int> renumber,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        memory.CheckOwner(governor);
        return RemapIds(value, new NumberMap(renumber, null, null) { DiscardUnmapped = true }, memory, governor, token, 0L);
    }

    internal static QpdfObject RemapIds(QpdfObject value, Dictionary<int, int> renumber,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(renumber);
        ArgumentNullException.ThrowIfNull(governor);
        memory.CheckOwner(governor);
        return RemapIds(value, new NumberMap(renumber, null, null), memory, governor, cancellationToken, 0L);
    }

    internal static QpdfObject RemapIds(QpdfObject value, GovernedIntDictionary<int> renumber,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        memory.CheckOwner(governor);
        renumber.CheckOwner(governor);
        return RemapIds(value, new NumberMap(null, renumber, null), memory, governor, cancellationToken, 0L);
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
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        return RemapDictionary(header, new NumberMap(null, null, renumber), memory, governor, cancellationToken, 0L, lengthNumber);
    }

    private readonly record struct NumberMap(Dictionary<int, int>? Plain, GovernedIntDictionary<int>? Governed,
        WriterIndex<int>? Writer)
    {
        internal bool DiscardUnmapped { get; init; }
        internal bool TryGetValue(int number, out int mapped)
        {
            if (Writer is not null) { return Writer.TryGetValue(number, out mapped); }
            if (Governed is not null) { return Governed.TryGetValue(number, out mapped); }
            if (Plain is not null) { return Plain.TryGetValue(number, out mapped); }
            throw new InvalidOperationException("Object remapping requires an index.");
        }
    }

    private static QpdfObject RemapIds(QpdfObject value, NumberMap renumber, ParserMemoryScope memory,
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
        governor.NoteOperation();
        if (value is PdfReference reference)
        {
            if (!renumber.TryGetValue(reference.ObjectNumber, out int mapped))
            {
                if (renumber.DiscardUnmapped) { return PdfNull.Instance; }
                throw new QpdfException($"Publication reached unmapped object {reference.ObjectNumber}.");
            }

            if (mapped == reference.ObjectNumber && reference.Generation == 0) { return reference; }
            memory.Reserve(32L);
            return new PdfReference(mapped, 0);
        }

        if (value is PdfArray array)
        {
            governor.NoteOperation();
            QpdfObject[]? items = null;
            for (int i = 0; i < array.Count; i++)
            {
                QpdfObject mapped = RemapIds(array[i], renumber, memory, governor, cancellationToken, checked(depth + 1L));
                if (items is null && !ReferenceEquals(mapped, array[i]))
                {
                    memory.Reserve(checked(64L + IntPtr.Size * (long)array.Count));
                    items = new QpdfObject[array.Count];
                    governor.NoteOperation();
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
            memory.Reserve(32L);
            return stream.WithDictionary(remapped);
        }

        return value;
    }

    private static PdfDictionary RemapDictionary(PdfDictionary dictionary, NumberMap renumber,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken cancellationToken, long depth, int? lengthNumber)
    {
        PdfNameTable<QpdfObject>? entries = lengthNumber.HasValue
            ? CreateEntries(memory, governor, cancellationToken, checked(dictionary.Count + 1)) : null;
        int processed = 0;
        PdfName? lengthKey = null;
        using PdfDictionary.EntryEnumerator source = dictionary.GetEntryEnumerator();
        while (source.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            KeyValuePair<PdfName, QpdfObject> entry = source.Current;
            if (lengthNumber.HasValue && entry.Key.RawSpan.SequenceEqual("Length"u8))
            { lengthKey = entry.Key; continue; }
            if (entry.Value is not null)
            {
                QpdfObject mapped = RemapIds(entry.Value, renumber, memory, governor, cancellationToken, checked(depth + 1L));
                if (entries is null && !ReferenceEquals(mapped, entry.Value))
                { entries = CopyPrefix(dictionary, processed, memory, governor, cancellationToken); }
                if (entries is not null) { entries.TryAdd(memory, governor, entry.Key, mapped, cancellationToken); }
            }
            else if (entries is null)
            { entries = CopyPrefix(dictionary, processed, memory, governor, cancellationToken); }
            processed++;
        }
        if (lengthNumber.HasValue)
        {
            entries ??= CreateEntries(memory, governor, cancellationToken, checked(dictionary.Count + 1));
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (lengthKey is null)
            {
                // Admit the fixed lookup name before creating its owned bytes.
                memory.Reserve(108L);
                lengthKey = PdfName.FromAscii("Length");
                memory.Release(38L);
            }
            memory.Reserve(32L);
            PdfReference length = new(lengthNumber.Value, 0);
            entries.TryAdd(memory, governor, lengthKey, length, cancellationToken);
        }
        if (entries is null) { return dictionary; }
        memory.Reserve(32L);
        entries.Freeze(memory, governor, cancellationToken);
        return PdfDictionary.FromOwnedEntries(entries);
    }

    private static PdfNameTable<QpdfObject> CreateEntries(ParserMemoryScope memory,
        ResourceGovernor governor, CancellationToken token, int count)
    {
        PdfNameTable<QpdfObject> names = PdfNameTable<QpdfObject>.Create(memory, governor, IntPtr.Size, token);
        names.EnsureCapacity(memory, governor, count, token);
        return names;
    }

    private static PdfNameTable<QpdfObject> CopyPrefix(PdfDictionary dictionary, int count,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        // Earlier children were validated and stayed identical. Their immutable
        // storage remains owned by the source model through publication.
        PdfNameTable<QpdfObject> entries = CreateEntries(memory, governor, token, dictionary.Count);
        using PdfDictionary.EntryEnumerator prefix = dictionary.GetEntryEnumerator();
        for (int index = 0; index < count && prefix.MoveNext(); index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            KeyValuePair<PdfName, QpdfObject> entry = prefix.Current;
            if (entry.Value is not null) { entries.TryAdd(memory, governor, entry.Key, entry.Value, token); }
        }
        return entries;
    }

}
