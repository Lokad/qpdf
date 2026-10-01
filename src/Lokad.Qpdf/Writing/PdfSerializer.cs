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
