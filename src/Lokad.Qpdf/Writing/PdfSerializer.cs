using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>QDF output options: editable-view formatting with indirect stream lengths and original-ID comments.</summary>
internal sealed record QdfOptions(bool Enabled, bool NoOriginalIds)
{
    internal static QdfOptions Disabled { get; } = new(false, false);
}

/// <summary>Destination for serialized bytes.</summary>
internal interface IByteSink
{
    /// <summary>Gets the bytes emitted so far.</summary>
    long Length { get; }

    /// <summary>Appends a chunk.</summary>
    ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken);
}

/// <summary>Consumes borrowed bytes completely before returning.</summary>
internal interface ISynchronousByteSink : IByteSink
{
    /// <summary>Whether Append can consume caller storage synchronously.</summary>
    bool CanAppendSynchronously { get; }
    void Append(ReadOnlySpan<byte> chunk, CancellationToken cancellationToken);
}

/// <summary>Counts serialized bytes without retaining them for the replay pass.</summary>
internal sealed class CountingSink : IByteSink
{
    private long _length;

    public long Length => _length;

    public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        AppendLength(chunk.Length, cancellationToken);
        return ValueTask.CompletedTask;
    }

    internal void AppendLength(long length, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        cancellationToken.ThrowIfCancellationRequested();
        checked
        {
            _length += length;
        }
    }
}

/// <summary>An object with its destination number for serialization.</summary>
internal readonly struct NumberedObject
{
    internal NumberedObject(int number, QpdfObject value)
    {
        Number = number;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal int Number { get; }

    internal QpdfObject Value { get; }
}

/// <summary>
/// Deterministic classic-xref serializer. The same model and identifiers through
/// the same code path yield identical bytes on every pass, which the counting
/// pass verifies by length. Stream `/Length` entries are always recomputed
/// from the emitted bytes; indirect length references do not survive.
/// Trailer `/ID` is always emitted: the first word preserves the source
/// document identifier when present, otherwise it repeats the generated
/// second word. The default second word is a content hash (deterministic
/// within the contained profile, unlike upstream time-based random IDs);
/// `--static-id` selects the upstream test constant.
/// </summary>
internal sealed partial class PdfSerializer
{
    private const int WriteSplitBytes = 32768;

    private readonly QpdfLimits _limits;
    private readonly ResourceGovernor _governor;

    internal PdfSerializer(QpdfLimits limits, ResourceGovernor governor)
    {
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        _limits = limits;
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
    }

    internal async ValueTask<long> SerializeAsync(
        PdfVersion version,
        IReadOnlyList<NumberedObject> objects,
        int? infoRef,
        byte[] id1,
        byte[] id2,
        bool newlineBeforeEndstream,
        IByteSink sink,
        CancellationToken cancellationToken)
    {
        if (version is null)
        {
            throw new ArgumentNullException(nameof(version));
        }

        if (objects is null)
        {
            throw new ArgumentNullException(nameof(objects));
        }

        if (sink is null)
        {
            throw new ArgumentNullException(nameof(sink));
        }

        if (id1 is null)
        {
            throw new ArgumentNullException(nameof(id1));
        }

        if (id2 is null)
        {
            throw new ArgumentNullException(nameof(id2));
        }

        if (id1.Length == 0 || id1.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(id1), "Document identifiers must hold 1..256 bytes.");
        }

        if (id2.Length == 0 || id2.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(id2), "Document identifiers must hold 1..256 bytes.");
        }

        (int size, long xrefOffset) = await SerializeClassicBodyAsync(version, objects,
            newlineBeforeEndstream, sink, cancellationToken).ConfigureAwait(false);
        await AppendClassicTrailerAsync(size, infoRef, id1, id2, xrefOffset, sink, cancellationToken).ConfigureAwait(false);
        return sink.Length;
    }

    internal async ValueTask<(int Size, long XrefOffset)> SerializeClassicBodyAsync(PdfVersion version,
        IReadOnlyList<NumberedObject> objects, bool newlineBeforeEndstream,
        IByteSink sink, CancellationToken cancellationToken)
    {
        await AppendFormattedAsync([WriterPart.Text("%PDF-"), WriterPart.Number(version.Major), WriterPart.Text("."), WriterPart.Number(version.Minor), WriterPart.Text("\n")], sink, cancellationToken).ConfigureAwait(false);
        CountingSink? counter = sink as CountingSink;
        bool denseCounting = counter is not null && HasDenseIncreasingNumbers(objects, cancellationToken);
        using WriterOrder<long>? denseOffsets = counter is null
            ? TryCreateDenseOffsets(objects, cancellationToken) : null;
        using WriterIndex<long>? offsets = denseCounting || denseOffsets is not null ? null
            : WriterIndex<long>.Create(objects.Count, sizeof(long), _governor, cancellationToken);
        int oversizedNumber = 0;
        long oversizedOffset = 0L;
        int maxNumber = 0;
        for (int i = 0; i < objects.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.NoteOperation();
            if (objects[i].Number <= 0)
            {
                throw new QpdfException($"Publication reached invalid object number {objects[i].Number}.");
            }

            if (denseOffsets is not null)
            {
                denseOffsets.Add(sink.Length);
            }
            else if (offsets is not null) { offsets[objects[i].Number] = sink.Length; }
            else if (oversizedNumber == 0 && sink.Length > 9_999_999_999L)
            { oversizedNumber = objects[i].Number; oversizedOffset = sink.Length; }
            if (objects[i].Number > maxNumber)
            {
                maxNumber = objects[i].Number;
            }

            await AppendFormattedAsync([WriterPart.Number(objects[i].Number), WriterPart.Text(" 0 obj\n")], sink, cancellationToken).ConfigureAwait(false);
            await AppendValueAsync(objects[i].Value, sink, 0, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
            await AppendAsciiAsync("\nendobj\n", sink, cancellationToken).ConfigureAwait(false);
        }

        long xrefOffset = sink.Length;
        await AppendFormattedAsync([WriterPart.Text("xref\n0 "), WriterPart.Number(maxNumber + 1), WriterPart.Text("\n")], sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("0000000000 65535 f \n", sink, cancellationToken).ConfigureAwait(false);
        for (int number = 1; number <= maxNumber; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (denseCounting && counter is not null)
            {
                if (number == oversizedNumber)
                { throw new QpdfException($"Object offset {oversizedOffset} exceeds the classic xref field width."); }
                // Fixed-width sizing needs no stored offsets or formatted digits.
                await AppendFormattedAsync([WriterPart.Spaces(10), WriterPart.Text(" 00000 n \n")], counter,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }
            long offset;
            if (denseOffsets is not null)
            {
                offset = denseOffsets[number - 1];
            }
            else if (offsets is null) { throw new InvalidOperationException("Classic emission needs its offset index."); }
            else if (!offsets.TryGetValue(number, out offset))
            {
                await AppendAsciiAsync("0000000000 65535 f \n", sink, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (offset > 9_999_999_999L)
            {
                throw new QpdfException($"Object offset {offset} exceeds the classic xref field width.");
            }

            await AppendFormattedAsync([WriterPart.ZeroPaddedNumber(offset, 10), WriterPart.Text(" 00000 n \n")], sink, cancellationToken).ConfigureAwait(false);
        }

        offsets?.Dispose();
        denseOffsets?.Dispose();

        return (maxNumber + 1, xrefOffset);
    }

    internal async ValueTask AppendClassicTrailerAsync(int size, int? infoRef, byte[] id1, byte[] id2,
        long xrefOffset, IByteSink sink, CancellationToken cancellationToken)
    {
        await AppendFormattedAsync([WriterPart.Text("trailer\n<< /Size "), WriterPart.Number(size), WriterPart.Text(" /Root 1 0 R"), WriterPart.Text(infoRef.HasValue ? " /Info " : ""), infoRef.HasValue ? WriterPart.Number(infoRef.Value) : default, WriterPart.Text(infoRef.HasValue ? " 0 R" : ""), WriterPart.Text(" /ID [<"), WriterPart.Hex(id1), WriterPart.Text("><"), WriterPart.Hex(id2), WriterPart.Text(">] >>\n")], sink, cancellationToken).ConfigureAwait(false);
        await AppendFormattedAsync([WriterPart.Text("startxref\n"), WriterPart.Number(xrefOffset), WriterPart.Text("\n%%EOF\n")], sink, cancellationToken).ConfigureAwait(false);
    }

    private bool HasDenseIncreasingNumbers(IReadOnlyList<NumberedObject> objects, CancellationToken token)
    {
        for (int index = 0; index < objects.Count; index++)
        {
            token.ThrowIfCancellationRequested(); _governor.NoteOperation();
            if (objects[index].Number != index + 1) { return false; }
        }
        return true;
    }

    private WriterOrder<long>? TryCreateDenseOffsets(IReadOnlyList<NumberedObject> objects,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); _governor.CheckCancellation();
        long bytes = checked(160L + sizeof(long) * (long)objects.Count);
        // Validate existing model numbers, not declared counts. Leave tight jobs
        // on the established dictionary/growth path without an extra scan.
        if (objects.Count > Array.MaxLength || bytes > _governor.RemainingMemoryBytes / 4L
            || !HasDenseIncreasingNumbers(objects, token)) { return null; }
        return WriterOrder<long>.Create(objects.Count, sizeof(long), _governor, token);
    }

    /// <summary>
    /// Appends one numbered object in the standard frame shared by classic
    /// publication paths. Stream lengths are recomputed; indirect lengths
    /// do not survive.
    /// </summary>
    internal async ValueTask AppendNumberedObjectAsync(
        int number,
        QpdfObject value,
        IByteSink sink,
        bool newlineBeforeEndstream,
        CancellationToken cancellationToken)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (sink is null)
        {
            throw new ArgumentNullException(nameof(sink));
        }

        await AppendFormattedAsync([WriterPart.Number(number), WriterPart.Text(" 0 obj\n")], sink, cancellationToken).ConfigureAwait(false);
        await AppendValueAsync(value, sink, 0, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("\nendobj\n", sink, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Serializes with object streams and a cross-reference stream. Object
    /// payloads and xref entries are Flate-packed when compression is active
    /// (plain <c>/FlateDecode</c>, no predictor) and raw otherwise.
    /// Grouping, numbering, and byte layout are deterministic for a given
    /// model, so the counting pass still verifies the emitting pass.
    /// </summary>
    internal async ValueTask<long> SerializeStreamedAsync(
        PdfVersion version,
        IReadOnlyList<NumberedObject> objects,
        IReadOnlyList<IReadOnlyList<int>> groups,
        bool compressStreams,
        int? compressionLevel,
        int? infoRef,
        byte[] id1,
        byte[] id2,
        bool newlineBeforeEndstream,
        IByteSink sink,
        CancellationToken cancellationToken)
    {
        if (version is null)
        {
            throw new ArgumentNullException(nameof(version));
        }

        if (objects is null)
        {
            throw new ArgumentNullException(nameof(objects));
        }

        if (groups is null)
        {
            throw new ArgumentNullException(nameof(groups));
        }

        if (sink is null)
        {
            throw new ArgumentNullException(nameof(sink));
        }

        if (id1 is null)
        {
            throw new ArgumentNullException(nameof(id1));
        }

        if (id2 is null)
        {
            throw new ArgumentNullException(nameof(id2));
        }

        if (id1.Length == 0 || id1.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(id1), "Document identifiers must hold 1..256 bytes.");
        }

        if (id2.Length == 0 || id2.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(id2), "Document identifiers must hold 1..256 bytes.");
        }

        if (groups.Count == 0)
        {
            throw new QpdfException("Object-stream plan holds no groups.");
        }

        using WriterIndex<QpdfObject> lookup = WriterIndex<QpdfObject>.Create(objects.Count, IntPtr.Size, _governor, cancellationToken);
        int maxNumber = 0;
        foreach (NumberedObject numbered in objects)
        {
            if (lookup.ContainsKey(numbered.Number))
            {
                throw new QpdfException($"Duplicate object number {numbered.Number} during serialization.");
            }

            lookup[numbered.Number] = numbered.Value;
            if (numbered.Number > maxNumber)
            {
                maxNumber = numbered.Number;
            }
        }

        if (maxNumber != objects.Count)
        {
            throw new QpdfException("Streamed output requires dense object numbering from 1.");
        }

        using WriterIndex<(int Container, int Index)> compressed = WriterIndex<(int Container, int Index)>.Create(2L * sizeof(int), _governor, cancellationToken);
        foreach (IReadOnlyList<int> group in groups)
        {
            if (group is null || group.Count == 0 || group.Count > 100)
            {
                throw new QpdfException("Object-stream groups must hold 1..100 members.");
            }
        }

        await AppendFormattedAsync([WriterPart.Text("%PDF-"), WriterPart.Number(version.Major), WriterPart.Text("."), WriterPart.Number(version.Minor), WriterPart.Text("\n")], sink, cancellationToken).ConfigureAwait(false);
        using WriterIndex<long> offsets = WriterIndex<long>.Create(sizeof(long), _governor, cancellationToken);
        using WriterOrder<int> regular = WriterOrder<int>.Create(objects.Count, sizeof(int), _governor, cancellationToken);
        using WriterIndex<byte> hidden = WriterIndex<byte>.Create(sizeof(byte), _governor, cancellationToken);
        foreach (IReadOnlyList<int> group in groups)
        {
            foreach (int number in group)
            {
                hidden.TryAdd(number, 0);
            }
        }

        foreach (NumberedObject numbered in objects)
        {
            if (!hidden.ContainsKey(numbered.Number))
            {
                regular.Add(numbered.Number);
            }
        }

        hidden.Dispose();
        foreach (int number in regular)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.NoteOperation();
            offsets[number] = sink.Length;
            await AppendFormattedAsync([WriterPart.Number(number), WriterPart.Text(" 0 obj\n")], sink, cancellationToken).ConfigureAwait(false);
            await AppendValueAsync(lookup[number], sink, 0, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
            await AppendAsciiAsync("\nendobj\n", sink, cancellationToken).ConfigureAwait(false);
        }

        regular.Dispose();
        int nextId = maxNumber + 1;
        foreach (IReadOnlyList<int> group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int containerId = nextId;
            checked
            {
                nextId += 1;
            }

            for (int index = 0; index < group.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _governor.NoteOperation();
                compressed[group[index]] = (containerId, index);
            }
            offsets[containerId] = sink.Length;
            if (sink is CountingSink counter && !compressStreams)
            {
                CountingSink members = new();
                long first = 0L;
                foreach (int number in group)
                {
                    cancellationToken.ThrowIfCancellationRequested(); _governor.CheckCancellation();
                    first = checked(first + ObjectStreamPayload.MeasureTableRow(number, members.Length, _governor));
                    await AppendValueAsync(ReadObjectStreamMember(lookup, number), members, 0,
                        newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
                }
                long length = checked(first + members.Length);
                long maximum = Math.Min(_limits.MaxOutputBytes, Array.MaxLength);
                if (length > maximum) { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), length, maximum); }
                counter.AppendLength(ObjectStreamHeader.Measure(containerId, group.Count, length, first,
                    false, false, _limits, _governor, cancellationToken), cancellationToken);
                counter.AppendLength(length, cancellationToken);
            }
            else
            {
                using ObjectStreamCapture members = ObjectStreamCapture.Create(group.Count, _limits, _governor, cancellationToken);
                foreach (int number in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await members.AppendAsync(this, ReadObjectStreamMember(lookup, number), newlineBeforeEndstream,
                        cancellationToken).ConfigureAwait(false);
                }
                using ObjectStreamPayload payload = ObjectStreamPayload.Create(group, null, members,
                    compressStreams, compressionLevel, _limits, _governor, cancellationToken);
                using (ObjectStreamHeader header = ObjectStreamHeader.Create(containerId, group.Count, payload.Data.Length,
                    payload.First, payload.Compressed, false, _limits, _governor, cancellationToken))
                {
                    await AppendBytesAsync(header.Data, sink, cancellationToken).ConfigureAwait(false);
                }
                await AppendBytesAsync(payload.Data, sink, cancellationToken).ConfigureAwait(false);
            }
            if (newlineBeforeEndstream)
            {
                await AppendAsciiAsync("\nendstream", sink, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await AppendAsciiAsync("endstream", sink, cancellationToken).ConfigureAwait(false);
            }

            await AppendAsciiAsync("\nendobj\n", sink, cancellationToken).ConfigureAwait(false);
        }

        lookup.Dispose();
        int xrefId = nextId;
        int size = xrefId + 1;
        offsets[xrefId] = sink.Length;
        long maxOffset = 0L;
        foreach (KeyValuePair<int, long> entry in offsets)
        {
            long offset = entry.Value;
            if (offset > maxOffset)
            {
                maxOffset = offset;
            }
        }

        int maxIndex = 0;
        foreach (KeyValuePair<int, (int Container, int Index)> entry in compressed)
        {
            int index = entry.Value.Index;
            if (index > maxIndex)
            {
                maxIndex = index;
            }
        }

        int f1 = Math.Max(BytesNeeded(maxOffset), BytesNeeded(size));
        int f2 = BytesNeeded(maxIndex);
        using WriterXref xref = WriterXref.Create(size, f1, f2, _limits, _governor, cancellationToken);
        for (int number = 0; number < size; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (number == 0)
            {
                xref.Append(0, 0L, 0L);
            }
            else if (compressed.TryGetValue(number, out (int Container, int Index) packed))
            {
                xref.Append(2, packed.Container, packed.Index);
            }
            else if (offsets.TryGetValue(number, out long offset))
            {
                xref.Append(1, offset, 0L);
            }
            else
            {
                xref.Append(0, 0L, 0L);
            }
        }

        xref.Finish(compressStreams, compressionLevel, _limits);
        string xrefFilter = compressStreams ? " /Filter /FlateDecode" : string.Empty;
        long xrefOffset = offsets[xrefId];
        compressed.Dispose(); offsets.Dispose();

        await AppendFormattedAsync([WriterPart.Number(xrefId), WriterPart.Text(" 0 obj\n<< /Type /XRef /Length "), WriterPart.Number(xref.Data.Length), WriterPart.Text(xrefFilter), WriterPart.Text(" /Size "), WriterPart.Number(size), WriterPart.Text(" /Root 1 0 R"), WriterPart.Text(infoRef.HasValue ? " /Info " : ""), infoRef.HasValue ? WriterPart.Number(infoRef.Value) : default, WriterPart.Text(infoRef.HasValue ? " 0 R" : ""), WriterPart.Text(" /ID [<"), WriterPart.Hex(id1), WriterPart.Text("><"), WriterPart.Hex(id2), WriterPart.Text(">] /W [1 "), WriterPart.Number(f1), WriterPart.Text(" "), WriterPart.Number(f2), WriterPart.Text("] >>\nstream\n")], sink, cancellationToken).ConfigureAwait(false);
        await AppendBytesAsync(xref.Data, sink, cancellationToken).ConfigureAwait(false);
        xref.Dispose();
        await AppendAsciiAsync("\nendstream\nendobj\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendFormattedAsync([WriterPart.Text("startxref\n"), WriterPart.Number(xrefOffset), WriterPart.Text("\n%%EOF\n")], sink, cancellationToken).ConfigureAwait(false);
        return sink.Length;
    }

    private static QpdfObject ReadObjectStreamMember(WriterIndex<QpdfObject> lookup, int number)
    {
        if (!lookup.TryGetValue(number, out QpdfObject? value) || value is null)
        {
            throw new QpdfException($"Object-stream member {number} is missing from the model.");
        }
        if (value is PdfStream || number == 1 || number == 2)
        {
            throw new QpdfException($"Object {number} cannot be compressed into an object stream.");
        }
        return value;
    }

    internal static int BytesNeeded(long value)
    {
        int bytes = 0;
        while (value != 0)
        {
            bytes += 1;
            value >>= 8;
        }

        return bytes;
    }

    internal static void WriteUInt(Span<byte> output, long value)
        => WriterNumber.WriteUInt(output, value);


    internal ValueTask AppendValueAsync(QpdfObject value, IByteSink sink, int depth, bool newlineBeforeEndstream, CancellationToken cancellationToken)
    {
        if (sink is CountingSink counter)
        {
            counter.AppendLength(MeasureValue(value, depth, newlineBeforeEndstream, cancellationToken), cancellationToken);
            return ValueTask.CompletedTask;
        }
        CheckValueDepth(depth);
        if (value is PdfNull)
        {
            return AppendAsciiAsync("null", sink, cancellationToken);
        }

        if (value is PdfBoolean flag)
        {
            return AppendAsciiAsync(flag.Value ? "true" : "false", sink, cancellationToken);
        }

        if (value is PdfInteger integer)
        {
            return AppendAsciiAsync(integer.Text, sink, cancellationToken);
        }

        if (value is PdfReal real)
        {
            return AppendAsciiAsync(real.Text, sink, cancellationToken);
        }

        if (value is PdfName name)
        {
            return AppendNameAsync(name, sink, cancellationToken);
        }

        if (value is PdfByteString text)
        {
            return AppendStringAsync(text, sink, cancellationToken);
        }

        if (value is PdfReference reference)
        {
            return AppendFormattedAsync([WriterPart.Number(reference.ObjectNumber), WriterPart.Text(" 0 R")], sink, cancellationToken);
        }

        return AppendContainerAsync(value, sink, depth, newlineBeforeEndstream, cancellationToken);
    }

    private void CheckValueDepth(int depth)
    {
        if (depth > _limits.MaxNestingDepth)
        { throw new QpdfParseException($"Serialization depth exceeds {_limits.MaxNestingDepth}.", null, null); }
        _governor.NoteOperation();
    }

    // Length-only counting has no I/O or retained state. Reuse token sizing
    // without async container frames or a sink append for every delimiter.
    private long MeasureValue(QpdfObject value, int depth, bool newlineBeforeEndstream, CancellationToken token)
    {
        CheckValueDepth(depth);
        checked
        {
            switch (value)
            {
                case PdfNull: return MeasureAscii("null", token);
                case PdfBoolean flag: return MeasureAscii(flag.Value ? "true" : "false", token);
                case PdfInteger integer: return MeasureAscii(integer.Text, token);
                case PdfReal real: return MeasureAscii(real.Text, token);
                case PdfName name: return WriterEncoding.MeasureName(name, _limits, _governor, token);
                case PdfByteString text: return WriterEncoding.MeasureString(text, _limits, _governor, token);
                case PdfReference reference:
                    return WriterFormatting.MeasureParts([WriterPart.Number(reference.ObjectNumber), WriterPart.Text(" 0 R")],
                        _limits, _governor, token);
                case PdfArray array:
                    long arrayLength = MeasureAscii("[", token);
                    for (int index = 0; index < array.Count; index++)
                    {
                        if (index > 0) { arrayLength += MeasureAscii(" ", token); }
                        arrayLength += MeasureValue(array[index], depth + 1, newlineBeforeEndstream, token);
                    }
                    return arrayLength + MeasureAscii("]", token);
                case PdfDictionary dictionary:
                    long dictionaryLength = MeasureAscii("<<", token);
                    dictionaryLength += MeasureEntries(dictionary, depth, false, newlineBeforeEndstream, token);
                    return dictionaryLength + MeasureAscii(">>", token);
                case PdfStream stream:
                    long streamLength = MeasureAscii("<<", token);
                    streamLength += MeasureEntries(stream.Dictionary, depth, true, newlineBeforeEndstream, token);
                    streamLength += WriterFormatting.MeasureParts(
                        [WriterPart.Text("/Length "), WriterPart.Number(stream.RawMemory.Length), WriterPart.Text(" ")],
                        _limits, _governor, token);
                    streamLength += MeasureAscii(">>\nstream\n", token);
                    _governor.CheckCancellation();
                    streamLength += stream.RawMemory.Length;
                    return streamLength + MeasureAscii(newlineBeforeEndstream ? "\nendstream" : "endstream", token);
                default: throw new QpdfException($"Unsupported value of type {value.GetType().Name} during serialization.");
            }
        }
    }

    private long MeasureEntries(PdfDictionary dictionary, int depth, bool skipLength,
        bool newlineBeforeEndstream, CancellationToken token)
    {
        long length = 0L;
        using PdfDictionary.EntryEnumerator entries = dictionary.GetEntryEnumerator();
        while (entries.MoveNext(_governor, token))
        {
            (PdfName key, QpdfObject entry) = entries.Current;
            if (skipLength && key.RawSpan.SequenceEqual("Length"u8)) { continue; }
            if (entry is null) { continue; }
            checked
            {
                length += WriterEncoding.MeasureName(key, _limits, _governor, token);
                length += MeasureAscii(" ", token);
                length += MeasureValue(entry, depth + 1, newlineBeforeEndstream, token);
                length += MeasureAscii(" ", token);
            }
        }
        return length;
    }

    private long MeasureAscii(string text, CancellationToken token)
        => WriterEncoding.MeasureAscii(text, _limits, _governor, token);

    private ValueTask AppendContainerAsync(QpdfObject value, IByteSink sink, int depth, bool newlineBeforeEndstream, CancellationToken cancellationToken)
        => value switch
        {
            PdfArray array => AppendArrayAsync(array, sink, depth, newlineBeforeEndstream, cancellationToken),
            PdfDictionary dictionary => AppendDictionaryAsync(dictionary, sink, depth, newlineBeforeEndstream, cancellationToken),
            PdfStream stream => AppendStreamAsync(stream, sink, depth, newlineBeforeEndstream, cancellationToken),
            _ => ValueTask.FromException(new QpdfException($"Unsupported value of type {value.GetType().Name} during serialization.")),
        };

    private async ValueTask AppendArrayAsync(PdfArray array, IByteSink sink, int depth, bool newlineBeforeEndstream, CancellationToken cancellationToken)
    {
        await AppendAsciiAsync("[", sink, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < array.Count; i++)
        {
            if (i > 0)
            {
                await AppendAsciiAsync(" ", sink, cancellationToken).ConfigureAwait(false);
            }

            await AppendValueAsync(array[i], sink, depth + 1, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
        }

        await AppendAsciiAsync("]", sink, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendDictionaryAsync(PdfDictionary dictionary, IByteSink sink, int depth, bool newlineBeforeEndstream, CancellationToken cancellationToken)
    {
        await AppendAsciiAsync("<<", sink, cancellationToken).ConfigureAwait(false);
        using PdfDictionary.EntryEnumerator entries = dictionary.GetEntryEnumerator();
        while (entries.MoveNext(_governor, cancellationToken))
        {
            (PdfName key, QpdfObject entry) = entries.Current;
            if (entry is null)
            {
                continue;
            }

            await AppendNameAsync(key, sink, cancellationToken).ConfigureAwait(false);
            await AppendAsciiAsync(" ", sink, cancellationToken).ConfigureAwait(false);
            await AppendValueAsync(entry, sink, depth + 1, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
            await AppendAsciiAsync(" ", sink, cancellationToken).ConfigureAwait(false);
        }

        await AppendAsciiAsync(">>", sink, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendStreamAsync(PdfStream stream, IByteSink sink, int depth, bool newlineBeforeEndstream, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> data = stream.RawMemory;
        await AppendAsciiAsync("<<", sink, cancellationToken).ConfigureAwait(false);
        using PdfDictionary.EntryEnumerator entries = stream.Dictionary.GetEntryEnumerator();
        while (entries.MoveNext(_governor, cancellationToken))
        {
            (PdfName key, QpdfObject entry) = entries.Current;
            if (key.RawSpan.SequenceEqual("Length"u8))
            {
                continue;
            }

            if (entry is null)
            {
                continue;
            }

            await AppendNameAsync(key, sink, cancellationToken).ConfigureAwait(false);
            await AppendAsciiAsync(" ", sink, cancellationToken).ConfigureAwait(false);
            await AppendValueAsync(entry, sink, depth + 1, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
            await AppendAsciiAsync(" ", sink, cancellationToken).ConfigureAwait(false);
        }

        await AppendFormattedAsync([WriterPart.Text("/Length "), WriterPart.Number(data.Length), WriterPart.Text(" ")], sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync(">>\nstream\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendStreamPayloadAsync(stream, sink, cancellationToken).ConfigureAwait(false);
        if (newlineBeforeEndstream)
        {
            await AppendAsciiAsync("\nendstream", sink, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await AppendAsciiAsync("endstream", sink, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask AppendStreamPayloadAsync(PdfStream stream, IByteSink sink, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> data = stream.RawMemory;
        if (!NeedsPayloadCopy(stream, sink))
        { await AppendBytesAsync(data, sink, cancellationToken).ConfigureAwait(false); return; }
        using ParserMemoryScope payload = ParserMemoryScope.Create(_governor);
        for (int offset = 0; offset < data.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested(); _governor.CheckCancellation();
            int count = Math.Min(WriteSplitBytes, data.Length - offset);
            long bytes = count + GovernedBufferStream.ArrayOverheadBytes;
            payload.Reserve(bytes);
            byte[]? packet = null;
            try
            {
                // Each retaining write owns its bounded immutable packet. A
                // large source stream does not require a second full-size copy.
                packet = GC.AllocateUninitializedArray<byte>(count);
                GovernedBufferStream.CopyBytes(data.Span.Slice(offset, count), packet, _governor,
                    cancellationToken, _governor.CancellationToken);
                await sink.AppendAsync(packet, cancellationToken).ConfigureAwait(false);
            }
            finally { packet = null; payload.Release(bytes); }
            offset += count;
        }
    }

    private static bool NeedsPayloadCopy(PdfStream stream, IByteSink sink)
    {
        if (stream.RawMemory.Length == ((byte[])stream.AllocationCarrier).Length) { return false; }
        // A retained small slice must not pin a private document's complete input.
        // Counting, synchronous consumption and prepared copying do not retain it.
        return sink switch
        {
            CountingSink => false,
            ISynchronousByteSink synchronous => !synchronous.CanAppendSynchronously,
            BatchingSink batch => !batch.CanBorrowPayload,
            _ => true,
        };
    }

    private ValueTask AppendFormattedAsync(ReadOnlySpan<WriterPart> parts, IByteSink sink, CancellationToken cancellationToken)
    {
        if (sink is CountingSink counter)
        {
            counter.AppendLength(WriterFormatting.MeasureParts(parts, _limits, _governor, cancellationToken), cancellationToken);
            return ValueTask.CompletedTask;
        }
        if (sink is BatchingSink batch && batch.TryAppendFormatting(parts, _limits, cancellationToken))
        { return ValueTask.CompletedTask; }
        return AppendFormattingAsync(WriterFormatting.Create(parts, _limits, _governor, cancellationToken), sink, cancellationToken);
    }

    private async ValueTask AppendFormattingAsync(WriterFormatting formatted, IByteSink sink, CancellationToken cancellationToken)
    {
        using (formatted)
        {
            await AppendBytesAsync(formatted.Data, sink, cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask AppendAsciiAsync(string text, IByteSink sink, CancellationToken cancellationToken)
    {
        if (sink is CountingSink counter)
        {
            counter.AppendLength(WriterEncoding.MeasureAscii(text, _limits, _governor, cancellationToken), cancellationToken);
            return ValueTask.CompletedTask;
        }
        if (WriterEncoding.TryGetSharedAscii(text, _limits, _governor, cancellationToken, out ReadOnlyMemory<byte> literal))
        {
            return AppendBytesAsync(literal, sink, cancellationToken);
        }
        if (sink is BatchingSink batch && batch.TryAppendAscii(text, _limits, cancellationToken))
        { return ValueTask.CompletedTask; }
        return AppendEncodingAsync(WriterEncoding.Ascii(text, _limits, _governor, cancellationToken), sink, cancellationToken);
    }

    private ValueTask AppendBytesAsync(ReadOnlyMemory<byte> chunk, IByteSink sink, CancellationToken cancellationToken)
    {
        if (sink is CountingSink counter)
        {
            _governor.CheckCancellation();
            counter.AppendLength(chunk.Length, cancellationToken);
            return ValueTask.CompletedTask;
        }
        if (chunk.IsEmpty) { return ValueTask.CompletedTask; }
        if (chunk.Length <= WriteSplitBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sink is BatchingSink batch && batch.TryAppendBytes(chunk.Span, cancellationToken))
            { return ValueTask.CompletedTask; }
            return sink.AppendAsync(chunk, cancellationToken);
        }
        return AppendSplitBytesAsync(chunk, sink, cancellationToken);
    }

    private async ValueTask AppendSplitBytesAsync(ReadOnlyMemory<byte> chunk, IByteSink sink, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < chunk.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int take = Math.Min(WriteSplitBytes, chunk.Length - offset);
            await sink.AppendAsync(chunk.Slice(offset, take), cancellationToken).ConfigureAwait(false);
            offset += take;
        }
    }

    private ValueTask AppendNameAsync(PdfName name, IByteSink sink, CancellationToken cancellationToken)
    {
        if (sink is CountingSink counter)
        {
            counter.AppendLength(WriterEncoding.MeasureName(name, _limits, _governor, cancellationToken), cancellationToken);
            return ValueTask.CompletedTask;
        }
        if (WriterEncoding.TryGetSharedName(name, _limits, _governor, cancellationToken, out ReadOnlyMemory<byte> literal))
        {
            return AppendBytesAsync(literal, sink, cancellationToken);
        }
        return AppendUnsharedNameAsync(name, sink, cancellationToken);
    }

    private async ValueTask AppendUnsharedNameAsync(PdfName name, IByteSink sink, CancellationToken cancellationToken)
    {
        if (WriterEncoding.TryGetUnescapedName(name, _limits, _governor, cancellationToken, out ReadOnlyMemory<byte> raw))
        {
            await AppendAsciiAsync("/", sink, cancellationToken).ConfigureAwait(false);
            await AppendBytesAsync(raw, sink, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (sink is BatchingSink batch && batch.TryAppendName(name, _limits, cancellationToken)) { return; }
        using WriterEncoding encoded = WriterEncoding.Name(name, _limits, _governor, cancellationToken);
        await AppendBytesAsync(encoded.Data, sink, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask AppendStringAsync(PdfByteString text, IByteSink sink, CancellationToken cancellationToken)
    {
        if (sink is CountingSink counter)
        {
            counter.AppendLength(WriterEncoding.MeasureString(text, _limits, _governor, cancellationToken), cancellationToken);
            return ValueTask.CompletedTask;
        }
        if (sink is BatchingSink batch && batch.TryAppendString(text, _limits, cancellationToken))
        { return ValueTask.CompletedTask; }
        return AppendEncodingAsync(WriterEncoding.String(text, _limits, _governor, cancellationToken), sink, cancellationToken);
    }

    private async ValueTask AppendEncodingAsync(WriterEncoding encoded, IByteSink sink, CancellationToken cancellationToken)
    {
        using (encoded)
        {
            await AppendBytesAsync(encoded.Data, sink, cancellationToken).ConfigureAwait(false);
        }
    }

}
