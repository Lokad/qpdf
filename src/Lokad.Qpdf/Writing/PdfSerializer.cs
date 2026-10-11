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

        await AppendFormattedAsync([WriterPart.Text("%PDF-"), WriterPart.Number(version.Major), WriterPart.Text("."), WriterPart.Number(version.Minor), WriterPart.Text("\n")], sink, cancellationToken).ConfigureAwait(false);
        using WriterIndex<long> offsets = WriterIndex<long>.Create(sizeof(long), _governor, cancellationToken);
        int maxNumber = 0;
        for (int i = 0; i < objects.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            if (objects[i].Number <= 0)
            {
                throw new QpdfException($"Publication reached invalid object number {objects[i].Number}.");
            }

            offsets[objects[i].Number] = sink.Length;
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
            if (!offsets.TryGetValue(number, out long offset))
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

        offsets.Dispose();

        await AppendFormattedAsync([WriterPart.Text("trailer\n<< /Size "), WriterPart.Number(maxNumber + 1), WriterPart.Text(" /Root 1 0 R"), WriterPart.Text(infoRef.HasValue ? " /Info " : ""), infoRef.HasValue ? WriterPart.Number(infoRef.Value) : default, WriterPart.Text(infoRef.HasValue ? " 0 R" : ""), WriterPart.Text(" /ID [<"), WriterPart.Hex(id1), WriterPart.Text("><"), WriterPart.Hex(id2), WriterPart.Text(">] >>\n")], sink, cancellationToken).ConfigureAwait(false);
        await AppendFormattedAsync([WriterPart.Text("startxref\n"), WriterPart.Number(xrefOffset), WriterPart.Text("\n%%EOF\n")], sink, cancellationToken).ConfigureAwait(false);
        return sink.Length;
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

        using WriterIndex<QpdfObject> lookup = WriterIndex<QpdfObject>.Create(IntPtr.Size, _governor, cancellationToken);
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
            _governor.ChargeWork(1L);
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

            using ObjectStreamCapture members = ObjectStreamCapture.Create(group.Count, _limits, _governor, cancellationToken);
            foreach (int number in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!lookup.TryGetValue(number, out QpdfObject? value) || value is null)
                {
                    throw new QpdfException($"Object-stream member {number} is missing from the model.");
                }

                if (value is PdfStream || number == 1 || number == 2)
                {
                    throw new QpdfException($"Object {number} cannot be compressed into an object stream.");
                }

                await members.AppendAsync(this, value, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
            }

            for (int index = 0; index < group.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _governor.ChargeWork(1L);
                compressed[group[index]] = (containerId, index);
            }
            using ObjectStreamPayload payload = ObjectStreamPayload.Create(group, null, members,
                compressStreams, compressionLevel, _limits, _governor, cancellationToken);
            offsets[containerId] = sink.Length;
            using (ObjectStreamHeader header = ObjectStreamHeader.Create(containerId, group.Count, payload.Data.Length,
                payload.First, payload.Compressed, false, _limits, _governor, cancellationToken))
            {
                await AppendBytesAsync(header.Data, sink, cancellationToken).ConfigureAwait(false);
            }
            await AppendBytesAsync(payload.Data, sink, cancellationToken).ConfigureAwait(false);
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


    internal async ValueTask AppendValueAsync(QpdfObject value, IByteSink sink, int depth, bool newlineBeforeEndstream, CancellationToken cancellationToken)
    {
        if (depth > _limits.MaxNestingDepth)
        {
            throw new QpdfParseException($"Serialization depth exceeds {_limits.MaxNestingDepth}.", null, null);
        }

        _governor.ChargeWork(1L);
        if (value is PdfNull)
        {
            await AppendAsciiAsync("null", sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (value is PdfBoolean flag)
        {
            await AppendAsciiAsync(flag.Value ? "true" : "false", sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (value is PdfInteger integer)
        {
            await AppendAsciiAsync(integer.Text, sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (value is PdfReal real)
        {
            await AppendAsciiAsync(real.Text, sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (value is PdfName name)
        {
            await AppendNameAsync(name, sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (value is PdfByteString text)
        {
            await AppendStringAsync(text, sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (value is PdfArray array)
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
            return;
        }

        if (value is PdfDictionary dictionary)
        {
            await AppendAsciiAsync("<<", sink, cancellationToken).ConfigureAwait(false);
            using PdfDictionary.EntryEnumerator entries = dictionary.GetEntryEnumerator();
            while (entries.MoveNext(_governor, cancellationToken))
            {
                PdfName key = entries.Current.Key;
                QpdfObject entry = entries.Current.Value;
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
            return;
        }

        if (value is PdfReference reference)
        {
            await AppendFormattedAsync([WriterPart.Number(reference.ObjectNumber), WriterPart.Text(" 0 R")], sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (value is PdfStream stream)
        {
            await AppendStreamAsync(stream, sink, depth, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new QpdfException($"Unsupported value of type {value.GetType().Name} during serialization.");
    }

    private async ValueTask AppendStreamAsync(PdfStream stream, IByteSink sink, int depth, bool newlineBeforeEndstream, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> data = stream.RawMemory;
        await AppendAsciiAsync("<<", sink, cancellationToken).ConfigureAwait(false);
        using PdfDictionary.EntryEnumerator entries = stream.Dictionary.GetEntryEnumerator();
        while (entries.MoveNext(_governor, cancellationToken))
        {
            PdfName key = entries.Current.Key;
            if (key.RawSpan.SequenceEqual("Length"u8))
            {
                continue;
            }

            QpdfObject entry = entries.Current.Value;
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
        await AppendBytesAsync(data, sink, cancellationToken).ConfigureAwait(false);
        if (newlineBeforeEndstream)
        {
            await AppendAsciiAsync("\nendstream", sink, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await AppendAsciiAsync("endstream", sink, cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask AppendFormattedAsync(ReadOnlySpan<WriterPart> parts, IByteSink sink, CancellationToken cancellationToken)
    {
        if (sink is CountingSink counter)
        {
            counter.AppendLength(WriterFormatting.MeasureParts(parts, _limits, _governor, cancellationToken), cancellationToken);
            return ValueTask.CompletedTask;
        }
        return AppendFormattingAsync(WriterFormatting.Create(parts, _limits, _governor, cancellationToken), sink, cancellationToken);
    }

    private async ValueTask AppendFormattingAsync(WriterFormatting formatted, IByteSink sink, CancellationToken cancellationToken)
    {
        using (formatted)
        {
            await AppendBytesAsync(formatted.Data, sink, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask AppendAsciiAsync(string text, IByteSink sink, CancellationToken cancellationToken)
    {
        if (sink is CountingSink counter)
        {
            counter.AppendLength(WriterEncoding.MeasureAscii(text, _limits, _governor, cancellationToken), cancellationToken);
            return;
        }
        if (WriterEncoding.TryGetSharedAscii(text, _limits, _governor, cancellationToken, out ReadOnlyMemory<byte> literal))
        {
            await AppendBytesAsync(literal, sink, cancellationToken).ConfigureAwait(false);
            return;
        }
        using WriterEncoding encoded = WriterEncoding.Ascii(text, _limits, _governor, cancellationToken);
        await AppendBytesAsync(encoded.Data, sink, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendBytesAsync(ReadOnlyMemory<byte> chunk, IByteSink sink, CancellationToken cancellationToken)
    {
        if (sink is CountingSink counter)
        {
            _governor.CheckCancellation();
            counter.AppendLength(chunk.Length, cancellationToken);
            return;
        }
        int offset = 0;
        while (offset < chunk.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int take = Math.Min(WriteSplitBytes, chunk.Length - offset);
            await sink.AppendAsync(chunk.Slice(offset, take), cancellationToken).ConfigureAwait(false);
            offset += take;
        }
    }

    private async ValueTask AppendNameAsync(PdfName name, IByteSink sink, CancellationToken cancellationToken)
    {
        if (sink is CountingSink counter)
        {
            counter.AppendLength(WriterEncoding.MeasureName(name, _limits, _governor, cancellationToken), cancellationToken);
            return;
        }
        using WriterEncoding encoded = WriterEncoding.Name(name, _limits, _governor, cancellationToken);
        await AppendBytesAsync(encoded.Data, sink, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendStringAsync(PdfByteString text, IByteSink sink, CancellationToken cancellationToken)
    {
        if (sink is CountingSink counter)
        {
            counter.AppendLength(WriterEncoding.MeasureString(text, _limits, _governor, cancellationToken), cancellationToken);
            return;
        }
        using WriterEncoding encoded = WriterEncoding.String(text, _limits, _governor, cancellationToken);
        await AppendBytesAsync(encoded.Data, sink, cancellationToken).ConfigureAwait(false);
    }


}
