using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Destination for serialized bytes.</summary>
internal interface IByteSink
{
    /// <summary>Gets the bytes emitted so far.</summary>
    long Length { get; }

    /// <summary>Appends a chunk.</summary>
    ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken);
}

/// <summary>Counts bytes without retaining them (the M1 replay counting pass).</summary>
internal sealed class CountingSink : IByteSink
{
    private long _length;

    public long Length => _length;

    public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        checked
        {
            _length += chunk.Length;
        }

        return ValueTask.CompletedTask;
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
internal sealed class PdfSerializer
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

        await AppendAsciiAsync($"%PDF-{version}\n", sink, cancellationToken).ConfigureAwait(false);
        List<long> offsets = new(objects.Count);
        for (int i = 0; i < objects.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            offsets.Add(sink.Length);
            await AppendAsciiAsync($"{objects[i].Number} 0 obj\n", sink, cancellationToken).ConfigureAwait(false);
            await AppendValueAsync(objects[i].Value, sink, 0, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
            await AppendAsciiAsync("\nendobj\n", sink, cancellationToken).ConfigureAwait(false);
        }

        long xrefOffset = sink.Length;
        await AppendAsciiAsync($"xref\n0 {objects.Count + 1}\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("0000000000 65535 f \n", sink, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < offsets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offsets[i] > 9_999_999_999L)
            {
                throw new QpdfException($"Object offset {offsets[i]} exceeds the classic xref field width.");
            }

            await AppendAsciiAsync($"{offsets[i]:D10} 00000 n \n", sink, cancellationToken).ConfigureAwait(false);
        }

        string idHex1 = ToHex(id1);
        string idHex2 = ToHex(id2);
        _governor.ChargeWork(idHex1.Length + idHex2.Length);
        await AppendAsciiAsync($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /ID [<" + idHex1 + "><" + idHex2 + ">] >>\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync($"startxref\n{xrefOffset}\n%%EOF\n", sink, cancellationToken).ConfigureAwait(false);
        return sink.Length;
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

        Dictionary<int, QpdfObject> lookup = new(objects.Count);
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

        Dictionary<int, (int Container, int Index)> compressed = new();
        foreach (IReadOnlyList<int> group in groups)
        {
            if (group is null || group.Count == 0 || group.Count > 100)
            {
                throw new QpdfException("Object-stream groups must hold 1..100 members.");
            }
        }

        await AppendAsciiAsync($"%PDF-{version}\n", sink, cancellationToken).ConfigureAwait(false);
        Dictionary<int, long> offsets = new(maxNumber + groups.Count + 2);
        List<int> regular = new(objects.Count);
        HashSet<int> hidden = new();
        foreach (IReadOnlyList<int> group in groups)
        {
            foreach (int number in group)
            {
                hidden.Add(number);
            }
        }

        foreach (NumberedObject numbered in objects)
        {
            if (!hidden.Contains(numbered.Number))
            {
                regular.Add(numbered.Number);
            }
        }

        foreach (int number in regular)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            offsets[number] = sink.Length;
            await AppendAsciiAsync($"{number} 0 obj\n", sink, cancellationToken).ConfigureAwait(false);
            await AppendValueAsync(lookup[number], sink, 0, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
            await AppendAsciiAsync("\nendobj\n", sink, cancellationToken).ConfigureAwait(false);
        }

        List<int> containers = new(groups.Count);
        int nextId = maxNumber + 1;
        foreach (IReadOnlyList<int> group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int containerId = nextId;
            checked
            {
                nextId += 1;
            }

            containers.Add(containerId);
            List<byte[]> members = new(group.Count);
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

                members.Add(await SerializeMemberAsync(value, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false));
            }

            StringBuilder header = new();
            long running = 0L;
            for (int m = 0; m < group.Count; m++)
            {
                if (m > 0)
                {
                    header.Append((char)32);
                }

                header.Append(group[m].ToString(System.Globalization.CultureInfo.InvariantCulture));
                header.Append((char)32);
                header.Append(running.ToString(System.Globalization.CultureInfo.InvariantCulture));
                checked
                {
                    running += members[m].Length;
                }
            }

            header.Append((char)10);
            byte[] headerBytes = System.Text.Encoding.ASCII.GetBytes(header.ToString());
            checked
            {
                running += headerBytes.Length;
            }

            _governor.ReserveMemory(running);
            List<byte> payload = new(checked((int)Math.Min(running, int.MaxValue)));
            payload.AddRange(headerBytes);
            int first = headerBytes.Length;
            for (int m = 0; m < group.Count; m++)
            {
                compressed[group[m]] = (containerId, m);
                payload.AddRange(members[m]);
            }

            byte[] payloadBytes = [.. payload];
            byte[] storedPayload = payloadBytes;
            string containerFilter = string.Empty;
            if (compressStreams)
            {
                storedPayload = FlateFilter.Encode(payloadBytes, compressionLevel, _limits, _governor);
                containerFilter = " /Filter /FlateDecode";
            }

            offsets[containerId] = sink.Length;
            await AppendAsciiAsync($"{containerId} 0 obj\n<< /Type /ObjStm /Length {storedPayload.Length}" + containerFilter + $" /N {group.Count} /First {first} >>\nstream\n", sink, cancellationToken).ConfigureAwait(false);
            await AppendBytesAsync(storedPayload, sink, cancellationToken).ConfigureAwait(false);
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

        int xrefId = nextId;
        int size = xrefId + 1;
        offsets[xrefId] = sink.Length;
        long maxOffset = 0L;
        foreach (long offset in offsets.Values)
        {
            if (offset > maxOffset)
            {
                maxOffset = offset;
            }
        }

        int maxIndex = 0;
        foreach ((int _, int index) in compressed.Values)
        {
            if (index > maxIndex)
            {
                maxIndex = index;
            }
        }

        int f1 = Math.Max(BytesNeeded(maxOffset), BytesNeeded(size));
        int f2 = BytesNeeded(maxIndex);
        List<byte> xrefData = new(checked((size) * (1 + f1 + f2)));
        for (int number = 0; number < size; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (number == 0)
            {
                AppendUInt(xrefData, 0L, 1);
                AppendUInt(xrefData, 0L, f1);
                AppendUInt(xrefData, 0L, f2);
            }
            else if (compressed.TryGetValue(number, out (int Container, int Index) packed))
            {
                AppendUInt(xrefData, 2L, 1);
                AppendUInt(xrefData, packed.Container, f1);
                AppendUInt(xrefData, packed.Index, f2);
            }
            else if (offsets.TryGetValue(number, out long offset))
            {
                AppendUInt(xrefData, 1L, 1);
                AppendUInt(xrefData, offset, f1);
                AppendUInt(xrefData, 0L, f2);
            }
            else
            {
                throw new QpdfException($"Object {number} is missing from the streamed model.");
            }
        }

        byte[] xrefBytes = [.. xrefData];
        byte[] storedXref = xrefBytes;
        string xrefFilter = string.Empty;
        if (compressStreams)
        {
            storedXref = FlateFilter.Encode(xrefBytes, compressionLevel, _limits, _governor);
            xrefFilter = " /Filter /FlateDecode";
        }

        _governor.ReserveMemory(storedXref.Length);
        long xrefOffset = offsets[xrefId];
        string idHex1 = ToHex(id1);
        string idHex2 = ToHex(id2);
        _governor.ChargeWork(idHex1.Length + idHex2.Length);
        await AppendAsciiAsync($"{xrefId} 0 obj\n<< /Type /XRef /Length {storedXref.Length}" + xrefFilter + $" /Size {size} /Root 1 0 R /ID [<" + idHex1 + "><" + idHex2 + ">] /W [1 " + f1.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + f2.ToString(System.Globalization.CultureInfo.InvariantCulture) + "] >>\nstream\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendBytesAsync(storedXref, sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("\nendstream\nendobj\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync($"startxref\n{xrefOffset}\n%%EOF\n", sink, cancellationToken).ConfigureAwait(false);
        return sink.Length;
    }

    private async ValueTask<byte[]> SerializeMemberAsync(QpdfObject value, bool newlineBeforeEndstream, CancellationToken cancellationToken)
    {
        BufferSink buffer = new(_limits, _governor);
        await AppendValueAsync(value, buffer, 0, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private sealed class BufferSink : IByteSink
    {
        private readonly List<byte> _bytes = new();
        private readonly QpdfLimits _limits;
        private readonly ResourceGovernor _governor;

        internal BufferSink(QpdfLimits limits, ResourceGovernor governor)
        {
            _limits = limits ?? throw new ArgumentNullException(nameof(limits));
            _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        }

        public long Length => _bytes.Count;

        public byte[] ToArray()
        {
            return [.. _bytes];
        }

        public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            if ((long)_bytes.Count + chunk.Length > _limits.MaxOutputBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), (long)_bytes.Count + chunk.Length, _limits.MaxOutputBytes);
            }

            ReadOnlySpan<byte> span = chunk.Span;
            for (int i = 0; i < span.Length; i++)
            {
                _bytes.Add(span[i]);
            }

            return ValueTask.CompletedTask;
        }
    }

    private static int BytesNeeded(long value)
    {
        int bytes = 0;
        while (value != 0)
        {
            bytes += 1;
            value >>= 8;
        }

        return bytes;
    }

    private static void AppendUInt(List<byte> output, long value, int width)
    {
        for (int i = width - 1; i >= 0; i--)
        {
            output.Add((byte)((value >> (8 * i)) & 0xFFL));
        }
    }


    private async ValueTask AppendValueAsync(QpdfObject value, IByteSink sink, int depth, bool newlineBeforeEndstream, CancellationToken cancellationToken)
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
            await AppendBytesAsync(EncodeName(name), sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (value is PdfByteString text)
        {
            await AppendBytesAsync(EncodeString(text), sink, cancellationToken).ConfigureAwait(false);
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
            foreach (PdfName key in dictionary.Keys)
            {
                if (!dictionary.TryGet(key, out QpdfObject entry))
                {
                    continue;
                }

                await AppendBytesAsync(EncodeName(key), sink, cancellationToken).ConfigureAwait(false);
                await AppendAsciiAsync(" ", sink, cancellationToken).ConfigureAwait(false);
                await AppendValueAsync(entry, sink, depth + 1, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
                await AppendAsciiAsync(" ", sink, cancellationToken).ConfigureAwait(false);
            }

            await AppendAsciiAsync(">>", sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (value is PdfReference reference)
        {
            await AppendAsciiAsync($"{reference.ObjectNumber} 0 R", sink, cancellationToken).ConfigureAwait(false);
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
        byte[] data = stream.Data;
        await AppendAsciiAsync("<<", sink, cancellationToken).ConfigureAwait(false);
        foreach (PdfName key in stream.Dictionary.Keys)
        {
            if (key.DisplayText == "Length")
            {
                continue;
            }

            if (!stream.Dictionary.TryGet(key, out QpdfObject entry))
            {
                continue;
            }

            await AppendBytesAsync(EncodeName(key), sink, cancellationToken).ConfigureAwait(false);
            await AppendAsciiAsync(" ", sink, cancellationToken).ConfigureAwait(false);
            await AppendValueAsync(entry, sink, depth + 1, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
            await AppendAsciiAsync(" ", sink, cancellationToken).ConfigureAwait(false);
        }

        await AppendAsciiAsync($"/Length {data.Length} ", sink, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask AppendAsciiAsync(string text, IByteSink sink, CancellationToken cancellationToken)
    {
        await AppendBytesAsync(System.Text.Encoding.ASCII.GetBytes(text), sink, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendBytesAsync(byte[] chunk, IByteSink sink, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < chunk.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int take = Math.Min(WriteSplitBytes, chunk.Length - offset);
            await sink.AppendAsync(chunk.AsMemory(offset, take), cancellationToken).ConfigureAwait(false);
            offset += take;
        }
    }

    private static byte[] EncodeName(PdfName name)
    {
        byte[] raw = name.RawBytes;
        List<byte> output = new(raw.Length + 1) { (byte)'/' };
        foreach (byte b in raw)
        {
            if (IsRegular(b))
            {
                output.Add(b);
                continue;
            }

            output.Add((byte)'#');
            output.Add(HexDigit(b >> 4));
            output.Add(HexDigit(b & 0x0F));
        }

        return [.. output];
    }

    private static bool IsRegular(byte b)
    {
        if (b < 0x21 || b > 0x7E)
        {
            return false;
        }

        return b != (byte)'#' && b != (byte)'(' && b != (byte)')' && b != (byte)'<'
            && b != (byte)'>' && b != (byte)'[' && b != (byte)']' && b != (byte)'{'
            && b != (byte)'}' && b != (byte)'/' && b != (byte)'%';
    }

    private static byte[] EncodeString(PdfByteString text)
    {
        byte[] raw = text.Value;
        if (text.Hex)
        {
            byte[] hex = new byte[(raw.Length * 2) + 2];
            hex[0] = (byte)'<';
            for (int i = 0; i < raw.Length; i++)
            {
                hex[1 + (i * 2)] = HexDigit(raw[i] >> 4);
                hex[1 + (i * 2) + 1] = HexDigit(raw[i] & 0x0F);
            }

            hex[hex.Length - 1] = (byte)'>';
            return hex;
        }

        List<byte> output = new(raw.Length + 2) { (byte)'(' };
        foreach (byte b in raw)
        {
            switch (b)
            {
                case (byte)'\n':
                    output.Add((byte)'\\');
                    output.Add((byte)'n');
                    break;
                case (byte)'\r':
                    output.Add((byte)'\\');
                    output.Add((byte)'r');
                    break;
                case (byte)'\t':
                    output.Add((byte)'\\');
                    output.Add((byte)'t');
                    break;
                case (byte)'\b':
                    output.Add((byte)'\\');
                    output.Add((byte)'b');
                    break;
                case (byte)'\f':
                    output.Add((byte)'\\');
                    output.Add((byte)'f');
                    break;
                case (byte)'(':
                case (byte)')':
                case (byte)'\\':
                    output.Add((byte)'\\');
                    output.Add(b);
                    break;
                default:
                    if (b < 0x20 || b > 0x7E)
                    {
                        output.Add((byte)'\\');
                        output.Add((byte)('0' + ((b >> 6) & 0x07)));
                        output.Add((byte)('0' + ((b >> 3) & 0x07)));
                        output.Add((byte)('0' + (b & 0x07)));
                    }
                    else
                    {
                        output.Add(b);
                    }

                    break;
            }
        }

        output.Add((byte)')');
        return [.. output];
    }

    private static string ToHex(byte[] data)
    {
        char[] chars = new char[checked(data.Length * 2)];
        const string digits = "0123456789ABCDEF";
        for (int i = 0; i < data.Length; i++)
        {
            chars[i * 2] = digits[data[i] >> 4];
            chars[(i * 2) + 1] = digits[data[i] & 0x0F];
        }

        return new string(chars);
    }

    private static byte HexDigit(int value)
    {
        return (byte)(value < 10 ? '0' + value : 'A' + value - 10);
    }
}
