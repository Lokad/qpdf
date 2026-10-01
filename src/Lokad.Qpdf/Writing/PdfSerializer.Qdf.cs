using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>
/// QDF editable-view serialization for <see cref="PdfSerializer"/>.
/// Layout mirrors upstream QDF: a binary comment plus `%QDF-1.0` marker,
/// one object per block with original-ID and page comments, readable
/// indented values, uncompressed streams with indirect lengths stored in
/// the object right after each stream, and a classic cross-reference table.
/// </summary>
internal sealed partial class PdfSerializer
{
    private static readonly byte[] QdfBinaryLine = [0x25, 0xBF, 0xF7, 0xA2, 0xFE, 0x0A];

    internal async ValueTask<long> SerializeQdfAsync(
        PdfVersion version,
        List<NumberedObject> model,
        IReadOnlyList<IReadOnlyList<int>>? objectStreamGroups,
        IReadOnlyDictionary<int, int>? originalIds,
        int? infoRef,
        byte[] id1,
        byte[] id2,
        QdfOptions qdf,
        bool newlineBeforeEndstream,
        IByteSink sink,
        CancellationToken cancellationToken)
    {
        if (version is null)
        {
            throw new ArgumentNullException(nameof(version));
        }

        if (model is null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        if (qdf is null)
        {
            throw new ArgumentNullException(nameof(qdf));
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

        if (objectStreamGroups is not null && objectStreamGroups.Count > 0)
        {
            throw new QpdfException("QDF with object streams is not implemented in this milestone; use --object-streams=disable.");
        }

        List<NumberedObject> sorted = new(model.Count);
        foreach (NumberedObject numbered in model)
        {
            if (!IsDiscardedXRefStream(numbered.Value))
            {
                sorted.Add(numbered);
            }
        }

        sorted.Sort(static (left, right) => left.Number.CompareTo(right.Number));
        Dictionary<int, int> renumber = new(sorted.Count * 2);
        List<QdfEmission> emissions = new(sorted.Count * 2);
        int next = 1;
        foreach (NumberedObject numbered in sorted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            renumber[numbered.Number] = next;
            int? source = null;
            if (originalIds is not null && originalIds.TryGetValue(numbered.Number, out int found))
            {
                source = found;
            }

            emissions.Add(new QdfEmission(next, numbered.Value, source, null));
            next = checked(next + 1);
            if (numbered.Value is PdfStream)
            {
                emissions.Add(new QdfEmission(next, null, null, numbered.Number));
                next = checked(next + 1);
            }
        }

        List<QdfEmission> remapped = new(emissions.Count);
        foreach (QdfEmission emission in emissions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (emission.LengthFor is not null)
            {
                remapped.Add(emission);
                continue;
            }

            if (emission.Value is null)
            {
                throw new QpdfException($"QDF publication lost object {emission.Number}.");
            }

            if (emission.Value is PdfStream rawStream)
            {
                int lengthId = FindLengthId(emissions, emission.Number);
                Dictionary<PdfName, QpdfObject> stripped = new();
                foreach (PdfName key in rawStream.Dictionary.Keys)
                {
                    if (key.DisplayText == "Length")
                    {
                        continue;
                    }

                    if (rawStream.Dictionary.TryGet(key, out QpdfObject? kept) && kept is not null)
                    {
                        stripped[key] = kept;
                    }
                }

                QpdfObject remappedHeader = ObjectRemap.RemapIds(new PdfDictionary(stripped), renumber, _governor);
                if (remappedHeader is not PdfDictionary header)
                {
                    throw new QpdfException($"QDF publication reached a non-dictionary stream header for object {emission.Number}.");
                }

                Dictionary<PdfName, QpdfObject> entries = new();
                foreach (PdfName key in header.Keys)
                {
                    if (header.TryGet(key, out QpdfObject? kept) && kept is not null)
                    {
                        entries[key] = kept;
                    }
                }

                _governor.ReserveMemory(64L);
                entries[PdfName.FromAscii("Length")] = new PdfReference(lengthId, 0);
                remapped.Add(emission with { Value = new PdfStream(new PdfDictionary(entries), rawStream.Data) });
                continue;
            }

            QpdfObject value = ObjectRemap.RemapIds(emission.Value, renumber, _governor);
            remapped.Add(emission with { Value = value });
        }

        Dictionary<int, string> marks = QdfPageMarks(sorted, renumber);
        int? mappedInfo = null;
        if (infoRef.HasValue)
        {
            if (!renumber.TryGetValue(infoRef.Value, out int mapped))
            {
                throw new QpdfException($"QDF publication reached unmapped info object {infoRef.Value}.");
            }

            mappedInfo = mapped;
        }

        await AppendAsciiAsync($"%PDF-{version}\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendBytesAsync(QdfBinaryLine, sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("%QDF-1.0\n\n", sink, cancellationToken).ConfigureAwait(false);
        List<long> offsets = new(remapped.Count);
        foreach (QdfEmission emission in remapped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            if (emission.LengthFor is not null)
            {
                offsets.Add(sink.Length);
                await AppendAsciiAsync($"{emission.Number} 0 obj\n", sink, cancellationToken).ConfigureAwait(false);
                await AppendAsciiAsync(StreamLength(emission, remapped).ToString(System.Globalization.CultureInfo.InvariantCulture), sink, cancellationToken).ConfigureAwait(false);
                await AppendAsciiAsync("\nendobj\n\n", sink, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (marks.TryGetValue(emission.Number, out string? mark) && mark is not null)
            {
                await AppendAsciiAsync(mark + "\n", sink, cancellationToken).ConfigureAwait(false);
            }

            if (emission.OriginalSource.HasValue && !qdf.NoOriginalIds)
            {
                await AppendAsciiAsync($"%% Original object ID: {emission.OriginalSource.Value} 0\n", sink, cancellationToken).ConfigureAwait(false);
            }

            offsets.Add(sink.Length);
            await AppendAsciiAsync($"{emission.Number} 0 obj\n", sink, cancellationToken).ConfigureAwait(false);
            if (emission.Value is PdfStream stream)
            {
                bool addedNewline = await AppendQdfStreamAsync(stream, sink, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
                await AppendAsciiAsync("\nendobj\n\n", sink, cancellationToken).ConfigureAwait(false);
                if (addedNewline)
                {
                    await AppendAsciiAsync("%QDF: ignore_newline\n", sink, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (emission.Value is QpdfObject plain)
            {
                await AppendQdfValueAsync(plain, sink, 0, cancellationToken).ConfigureAwait(false);
            }
            await AppendAsciiAsync("\nendobj\n\n", sink, cancellationToken).ConfigureAwait(false);
        }

        long xrefOffset = sink.Length;
        await AppendAsciiAsync($"xref\n0 {remapped.Count + 1}\n", sink, cancellationToken).ConfigureAwait(false);
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
        await AppendAsciiAsync("trailer <<\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("  /Root 1 0 R\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync($"  /Size {remapped.Count + 1}\n", sink, cancellationToken).ConfigureAwait(false);
        if (mappedInfo.HasValue)
        {
            await AppendAsciiAsync($"  /Info {mappedInfo.Value} 0 R\n", sink, cancellationToken).ConfigureAwait(false);
        }

        await AppendAsciiAsync("  /ID [<" + idHex1 + "><" + idHex2 + ">]\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync(">>\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync($"startxref\n{xrefOffset}\n%%EOF\n", sink, cancellationToken).ConfigureAwait(false);
        return sink.Length;
    }

    private sealed record QdfEmission(int Number, QpdfObject? Value, int? OriginalSource, int? LengthFor);

    private static bool IsDiscardedXRefStream(QpdfObject value)
    {
        return value is PdfStream stream
            && stream.Dictionary.TryGet(PdfName.FromAscii("Type"), out QpdfObject? type)
            && type is PdfName name
            && name.DisplayText == "XRef";
    }

    private static int FindLengthId(List<QdfEmission> emissions, int streamNumber)
    {
        foreach (QdfEmission emission in emissions)
        {
            if (emission.LengthFor == streamNumber)
            {
                return emission.Number;
            }
        }

        throw new QpdfException($"QDF publication lost the length object for stream {streamNumber}.");
    }

    private static long StreamLength(QdfEmission lengthEmission, List<QdfEmission> emissions)
    {
        foreach (QdfEmission emission in emissions)
        {
            if (emission.Number == lengthEmission.LengthFor && emission.Value is PdfStream stream)
            {
                return stream.Data.Length;
            }
        }

        throw new QpdfException("QDF publication lost a length object stream.");
    }

    private static Dictionary<int, string> QdfPageMarks(List<NumberedObject> sorted, Dictionary<int, int> renumber)
    {
        Dictionary<int, string> marks = new();
        PdfDictionary? pages = null;
        foreach (NumberedObject numbered in sorted)
        {
            if (numbered.Number == 2 && numbered.Value is PdfDictionary candidate)
            {
                pages = candidate;
                break;
            }
        }

        if (pages is null)
        {
            return marks;
        }

        if (!pages.TryGet(PdfName.FromAscii("Kids"), out QpdfObject? kidsRaw) || kidsRaw is not PdfArray kids)
        {
            return marks;
        }

        int seq = 0;
        foreach (QpdfObject kid in kids.Items)
        {
            if (kid is not PdfReference kidReference)
            {
                continue;
            }

            if (!renumber.TryGetValue(kidReference.ObjectNumber, out int pageNumber))
            {
                continue;
            }

            seq += 1;
            marks[pageNumber] = $"%% Page {seq}";
            QpdfObject? page = null;
            foreach (NumberedObject numbered in sorted)
            {
                if (numbered.Number == kidReference.ObjectNumber && numbered.Value is PdfDictionary)
                {
                    page = numbered.Value;
                    break;
                }
            }

            if (page is not PdfDictionary pageDict)
            {
                continue;
            }

            if (!pageDict.TryGet(PdfName.FromAscii("Contents"), out QpdfObject? contents) || contents is null)
            {
                continue;
            }

            if (contents is PdfReference contentReference)
            {
                if (renumber.TryGetValue(contentReference.ObjectNumber, out int contentNumber) && !marks.ContainsKey(contentNumber))
                {
                    marks[contentNumber] = $"%% Contents for page {seq}";
                }

                continue;
            }

            if (contents is PdfArray contentArray)
            {
                foreach (QpdfObject item in contentArray.Items)
                {
                    if (item is PdfReference itemReference
                        && renumber.TryGetValue(itemReference.ObjectNumber, out int itemNumber)
                        && !marks.ContainsKey(itemNumber))
                    {
                        marks[itemNumber] = $"%% Contents for page {seq}";
                    }
                }
            }
        }

        return marks;
    }

    private async ValueTask<bool> AppendQdfStreamAsync(PdfStream stream, IByteSink sink, bool newlineBeforeEndstream, CancellationToken cancellationToken)
    {
        byte[] data = stream.Data;
        await AppendQdfDictionaryAsync(stream.Dictionary, sink, 0, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("\nstream\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendBytesAsync(data, sink, cancellationToken).ConfigureAwait(false);
        bool addedNewline = newlineBeforeEndstream || data.Length == 0 || data[data.Length - 1] != 0x0A;
        if (addedNewline)
        {
            await AppendAsciiAsync("\nendstream", sink, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await AppendAsciiAsync("endstream", sink, cancellationToken).ConfigureAwait(false);
        }

        return addedNewline && !newlineBeforeEndstream;
    }

    private async ValueTask AppendQdfValueAsync(QpdfObject value, IByteSink sink, int level, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (level > _limits.MaxNestingDepth)
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
                await AppendAsciiAsync(QdfIndent(level, true), sink, cancellationToken).ConfigureAwait(false);
                await AppendQdfValueAsync(array[i], sink, level + 1, cancellationToken).ConfigureAwait(false);
            }

            await AppendAsciiAsync(QdfIndent(level, false) + "]", sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (value is PdfDictionary dictionary)
        {
            await AppendQdfDictionaryAsync(dictionary, sink, level, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (value is PdfReference reference)
        {
            await AppendAsciiAsync($"{reference.ObjectNumber} 0 R", sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (value is PdfStream stream)
        {
            await AppendQdfDictionaryAsync(stream.Dictionary, sink, level, cancellationToken).ConfigureAwait(false);
            await AppendAsciiAsync("\nstream\n", sink, cancellationToken).ConfigureAwait(false);
            await AppendBytesAsync(stream.Data, sink, cancellationToken).ConfigureAwait(false);
            await AppendAsciiAsync("endstream", sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new QpdfException($"Unsupported value of type {value.GetType().Name} during serialization.");
    }

    private async ValueTask AppendQdfDictionaryAsync(PdfDictionary dictionary, IByteSink sink, int level, CancellationToken cancellationToken)
    {
        await AppendAsciiAsync("<<", sink, cancellationToken).ConfigureAwait(false);
        List<PdfName> ordered = new();
        foreach (PdfName key in dictionary.Keys)
        {
            ordered.Add(key);
        }

        ordered.Sort(static (left, right) => string.CompareOrdinal(left.DisplayText, right.DisplayText));
        foreach (PdfName key in ordered)
        {
            if (!dictionary.TryGet(key, out QpdfObject? entry) || entry is null || entry is PdfNull)
            {
                continue;
            }

            await AppendAsciiAsync(QdfIndent(level, true), sink, cancellationToken).ConfigureAwait(false);
            await AppendBytesAsync(EncodeName(key), sink, cancellationToken).ConfigureAwait(false);
            await AppendAsciiAsync(" ", sink, cancellationToken).ConfigureAwait(false);
            await AppendQdfValueAsync(entry, sink, level + 1, cancellationToken).ConfigureAwait(false);
        }

        await AppendAsciiAsync(QdfIndent(level, false) + ">>", sink, cancellationToken).ConfigureAwait(false);
    }

    private static string QdfIndent(int level, bool large)
    {
        if (large)
        {
            return "\n" + new string(' ', checked(2 * (level + 1)));
        }

        return "\n" + new string(' ', checked(2 * level));
    }
}