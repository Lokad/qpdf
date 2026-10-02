using System.Text;
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
        if (objectStreamGroups is not null && objectStreamGroups.Count > 0)
        {
            return await SerializeQdfStreamedAsync(version, model, objectStreamGroups, originalIds, infoRef, id1, id2, qdf, newlineBeforeEndstream, sink, cancellationToken).ConfigureAwait(false);
        }

        return await SerializeQdfClassicAsync(version, model, originalIds, infoRef, id1, id2, qdf, newlineBeforeEndstream, sink, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<long> SerializeQdfClassicAsync(
        PdfVersion version,
        List<NumberedObject> model,
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
                int lengthId = FindLengthId(emissions, renumber, emission.Number);
                PdfDictionary header = AssertDictionary(StreamHeaderWithLength(rawStream, lengthId, renumber));
                remapped.Add(emission with { Value = new PdfStream(header, rawStream.Data) });
                continue;
            }

            QpdfObject value = ObjectRemap.RemapIds(emission.Value, renumber, _governor);
            remapped.Add(emission with { Value = value });
        }

        Dictionary<int, QpdfObject> classicLookup = new(sorted.Count);
        foreach (NumberedObject numbered in sorted)
        {
            classicLookup[numbered.Number] = numbered.Value;
        }

        Dictionary<int, string> marks = QdfPageMarksSorted(classicLookup, renumber);
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
                await AppendAsciiAsync(StreamLength(emission, remapped, renumber).ToString(System.Globalization.CultureInfo.InvariantCulture), sink, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<long> SerializeQdfStreamedAsync(
        PdfVersion version,
        List<NumberedObject> model,
        IReadOnlyList<IReadOnlyList<int>> objectStreamGroups,
        IReadOnlyDictionary<int, int>? originalIds,
        int? infoRef,
        byte[] id1,
        byte[] id2,
        QdfOptions qdf,
        bool newlineBeforeEndstream,
        IByteSink sink,
        CancellationToken cancellationToken)
    {
        Dictionary<int, QpdfObject> lookup = new(model.Count);
        foreach (NumberedObject numbered in model)
        {
            if (!IsDiscardedXRefStream(numbered.Value) && !lookup.ContainsKey(numbered.Number))
            {
                lookup[numbered.Number] = numbered.Value;
            }
        }

        foreach (IReadOnlyList<int> group in objectStreamGroups)
        {
            if (group is null || group.Count == 0 || group.Count > 100)
            {
                throw new QpdfException("Object-stream groups must hold 1..100 members.");
            }
        }

        HashSet<int> members = new();
        foreach (IReadOnlyList<int> group in objectStreamGroups)
        {
            foreach (int member in group)
            {
                members.Add(member);
            }
        }

        List<int> ordered = new(lookup.Keys);
        ordered.Sort();
        Dictionary<int, int> combined = new(ordered.Count * 2);
        Dictionary<int, int> memberContainer = new();
        Dictionary<int, int> memberIndex = new();
        List<(int Old, int New, QpdfObject Value, int? Original)> regulars = new();
        int next = 1;
        foreach (int old in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (members.Contains(old))
            {
                continue;
            }

            combined[old] = next;
            int? source = null;
            if (originalIds is not null && originalIds.TryGetValue(old, out int found))
            {
                source = found;
            }

            regulars.Add((old, next, lookup[old], source));
            next = checked(next + 1);
            if (lookup[old] is PdfStream)
            {
                next = checked(next + 1);
            }
        }

        List<(int Container, IReadOnlyList<int> Group)> containers = new(objectStreamGroups.Count);
        foreach (IReadOnlyList<int> group in objectStreamGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int container = next;
            next = checked(next + 1);
            for (int m = 0; m < group.Count; m++)
            {
                if (!lookup.ContainsKey(group[m]))
                {
                    throw new QpdfException($"Object-stream member {group[m]} is missing from the model.");
                }

                if (group[m] == 1 || group[m] == 2)
                {
                    throw new QpdfException($"Object {group[m]} cannot be compressed into an object stream.");
                }

                if (lookup[group[m]] is PdfStream)
                {
                    throw new QpdfException($"Object {group[m]} cannot be compressed into an object stream.");
                }

                combined[group[m]] = next;
                memberContainer[group[m]] = container;
                memberIndex[group[m]] = m;
                next = checked(next + 1);
            }

            containers.Add((container, group));
        }

        int xrefNumber = next;
        next = checked(next + 1);
        int size = xrefNumber + 1;
        Dictionary<int, string> marks = QdfPageMarksSorted(lookup, combined);

        await AppendAsciiAsync($"%PDF-{version}\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendBytesAsync(QdfBinaryLine, sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("%QDF-1.0\n\n", sink, cancellationToken).ConfigureAwait(false);
        Dictionary<int, long> offsets = new();
        foreach ((int old, int number, QpdfObject value, int? source) in regulars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            QpdfObject remapped = ObjectRemap.RemapIds(value, combined, _governor);
            if (marks.TryGetValue(number, out string? mark) && mark is not null)
            {
                await AppendAsciiAsync(mark + "\n", sink, cancellationToken).ConfigureAwait(false);
            }

            if (source.HasValue && !qdf.NoOriginalIds)
            {
                await AppendAsciiAsync($"%% Original object ID: {source.Value} 0\n", sink, cancellationToken).ConfigureAwait(false);
            }

            offsets.Add(number, sink.Length);
            await AppendAsciiAsync($"{number} 0 obj\n", sink, cancellationToken).ConfigureAwait(false);
            if (remapped is PdfStream stream)
            {
                int lengthNumber = number + 1;
                QpdfObject header = StreamHeaderWithLength(stream, lengthNumber, combined);
                await AppendQdfDictionaryAsync(AssertDictionary(header), sink, 0, cancellationToken).ConfigureAwait(false);
                await AppendAsciiAsync("\nstream\n", sink, cancellationToken).ConfigureAwait(false);
                await AppendBytesAsync(stream.Data, sink, cancellationToken).ConfigureAwait(false);
                bool addedNewline = newlineBeforeEndstream || stream.Data.Length == 0 || stream.Data[stream.Data.Length - 1] != 0x0A;
                if (addedNewline)
                {
                    await AppendAsciiAsync("\nendstream", sink, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await AppendAsciiAsync("endstream", sink, cancellationToken).ConfigureAwait(false);
                }

                await AppendAsciiAsync("\nendobj\n\n", sink, cancellationToken).ConfigureAwait(false);
                if (addedNewline && !newlineBeforeEndstream)
                {
                    await AppendAsciiAsync("%QDF: ignore_newline\n", sink, cancellationToken).ConfigureAwait(false);
                }

                offsets.Add(lengthNumber, sink.Length);
                await AppendAsciiAsync($"{lengthNumber} 0 obj\n", sink, cancellationToken).ConfigureAwait(false);
                await AppendAsciiAsync(stream.Data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), sink, cancellationToken).ConfigureAwait(false);
                await AppendAsciiAsync("\nendobj\n\n", sink, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await AppendQdfValueAsync(remapped, sink, 0, cancellationToken).ConfigureAwait(false);
            await AppendAsciiAsync("\nendobj\n\n", sink, cancellationToken).ConfigureAwait(false);
        }

        foreach ((int container, IReadOnlyList<int> group) in containers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            List<byte[]> headers = new(group.Count);
            List<byte[]> bodies = new(group.Count);
            List<int> blockNumbers = new(group.Count);
            foreach (int member in group)
            {
                QpdfObject memberValue = ObjectRemap.RemapIds(lookup[member], combined, _governor);
                BufferSink head = new(_limits, _governor);
                await AppendAsciiAsync($"%% Object stream: object {combined[member]}, index {memberIndex[member]}", head, cancellationToken).ConfigureAwait(false);
                if (originalIds is not null && originalIds.TryGetValue(member, out int memberSource) && !qdf.NoOriginalIds)
                {
                    await AppendAsciiAsync($"; original object ID: {memberSource}", head, cancellationToken).ConfigureAwait(false);
                }

                await AppendAsciiAsync("\n", head, cancellationToken).ConfigureAwait(false);
                if (marks.TryGetValue(combined[member], out string? memberMark) && memberMark is not null)
                {
                    await AppendAsciiAsync(memberMark + "\n", head, cancellationToken).ConfigureAwait(false);
                }

                BufferSink body = new(_limits, _governor);
                await AppendQdfValueAsync(memberValue, body, 0, cancellationToken).ConfigureAwait(false);
                await AppendAsciiAsync("\n", body, cancellationToken).ConfigureAwait(false);
                headers.Add(head.ToArray());
                bodies.Add(body.ToArray());
                blockNumbers.Add(combined[member]);
            }

            // Upstream QDF keeps each member comment ahead of its value in the payload,
            // counts the first comment in /First, and records member offsets at value
            // starts so readers land on objects rather than comments.
            StringBuilder table = new();
            long prefix = 0L;
            for (int m = 0; m < headers.Count; m++)
            {
                long atValue = checked(prefix + headers[m].Length - headers[0].Length);
                table.Append(blockNumbers[m].ToString(System.Globalization.CultureInfo.InvariantCulture));
                table.Append((char)32);
                table.Append(atValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
                table.Append((char)10);
                checked
                {
                    prefix += headers[m].Length + bodies[m].Length;
                }
            }

            byte[] tableBytes = System.Text.Encoding.ASCII.GetBytes(table.ToString());
            long first = checked(tableBytes.Length + headers[0].Length);
            _governor.ReserveMemory(checked(tableBytes.Length + prefix));
            List<byte> payload = new(checked((int)System.Math.Min(checked(tableBytes.Length + prefix), int.MaxValue)));
            payload.AddRange(tableBytes);
            for (int m = 0; m < headers.Count; m++)
            {
                payload.AddRange(headers[m]);
                payload.AddRange(bodies[m]);
            }

            byte[] payloadBytes = [.. payload];
            offsets.Add(container, sink.Length);
            await AppendAsciiAsync($"{container} 0 obj\n<<\n  /Type /ObjStm\n  /Length {payloadBytes.Length}\n  /N {group.Count}\n  /First {first}\n>>\nstream\n", sink, cancellationToken).ConfigureAwait(false);
            await AppendBytesAsync(payloadBytes, sink, cancellationToken).ConfigureAwait(false);
            if (newlineBeforeEndstream)
            {
                await AppendAsciiAsync("\nendstream", sink, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await AppendAsciiAsync("endstream", sink, cancellationToken).ConfigureAwait(false);
            }

            await AppendAsciiAsync("\nendobj\n\n", sink, cancellationToken).ConfigureAwait(false);
        }

        offsets[xrefNumber] = sink.Length;
        long maxOffset = 0L;
        foreach (long offset in offsets.Values)
        {
            if (offset > maxOffset)
            {
                maxOffset = offset;
            }
        }

        int maxIndex = 0;
        foreach (int index in memberIndex.Values)
        {
            if (index > maxIndex)
            {
                maxIndex = index;
            }
        }

        int f1 = System.Math.Max(BytesNeeded(maxOffset), BytesNeeded(size));
        int f2 = BytesNeeded(maxIndex);
        List<byte> xrefData = new(checked(size * (1 + f1 + f2)));
        for (int number = 0; number < size; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (number == 0)
            {
                AppendUInt(xrefData, 0L, 1);
                AppendUInt(xrefData, 0L, f1);
                AppendUInt(xrefData, 0L, f2);
            }
            else if (offsets.TryGetValue(number, out long offset))
            {
                AppendUInt(xrefData, 1L, 1);
                AppendUInt(xrefData, offset, f1);
                AppendUInt(xrefData, 0L, f2);
            }
            else
            {
                int foundContainer = -1;
                int foundIndex = -1;
                foreach (KeyValuePair<int, int> entry in memberContainer)
                {
                    if (combined.TryGetValue(entry.Key, out int memberNumber) && memberNumber == number)
                    {
                        foundContainer = entry.Value;
                        foundIndex = memberIndex[entry.Key];
                        break;
                    }
                }

                if (foundContainer < 0)
                {
                    throw new QpdfException($"Object {number} is missing from the QDF streamed model.");
                }

                AppendUInt(xrefData, 2L, 1);
                AppendUInt(xrefData, foundContainer, f1);
                AppendUInt(xrefData, foundIndex, f2);
            }
        }

        byte[] xrefBytes = [.. xrefData];
        _governor.ReserveMemory(xrefBytes.Length);
        int qdfRoot = 1;
        if (combined.TryGetValue(1, out int catalogNumber))
        {
            qdfRoot = catalogNumber;
        }
        string idHex1 = ToHex(id1);
        string idHex2 = ToHex(id2);
        _governor.ChargeWork(idHex1.Length + idHex2.Length);
        await AppendAsciiAsync($"{xrefNumber} 0 obj\n<<\n  /Type /XRef\n  /Length {xrefBytes.Length}\n  /W [ 1 {f1.ToString(System.Globalization.CultureInfo.InvariantCulture)} {f2.ToString(System.Globalization.CultureInfo.InvariantCulture)} ]\n  /Root {qdfRoot} 0 R\n  /Size {size}\n", sink, cancellationToken).ConfigureAwait(false);
        if (infoRef.HasValue)
        {
            if (!combined.TryGetValue(infoRef.Value, out int mappedInfo))
            {
                throw new QpdfException($"QDF publication reached unmapped info object {infoRef.Value}.");
            }

            await AppendAsciiAsync($"  /Info {mappedInfo} 0 R\n", sink, cancellationToken).ConfigureAwait(false);
        }

        await AppendAsciiAsync("  /ID [<" + idHex1 + "><" + idHex2 + ">]\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync(">>\nstream\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendBytesAsync(xrefBytes, sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("\nendstream\nendobj\n\n", sink, cancellationToken).ConfigureAwait(false);
        long xrefOffset = offsets[xrefNumber];
        await AppendAsciiAsync($"startxref\n{xrefOffset}\n%%EOF\n", sink, cancellationToken).ConfigureAwait(false);
        return sink.Length;
    }

    private static PdfDictionary AssertDictionary(QpdfObject value)
    {
        if (value is PdfDictionary dictionary)
        {
            return dictionary;
        }

        throw new QpdfException("QDF publication reached a non-dictionary stream header.");
    }

    private QpdfObject StreamHeaderWithLength(PdfStream stream, int lengthNumber, Dictionary<int, int> renumber)
    {
        Dictionary<PdfName, QpdfObject> stripped = new();
        foreach (PdfName key in stream.Dictionary.Keys)
        {
            if (key.DisplayText == "Length")
            {
                continue;
            }

            if (stream.Dictionary.TryGet(key, out QpdfObject? kept) && kept is not null)
            {
                stripped[key] = kept;
            }
        }

        QpdfObject remapped = ObjectRemap.RemapIds(new PdfDictionary(stripped), renumber, _governor);
        if (remapped is not PdfDictionary header)
        {
            throw new QpdfException("QDF publication reached a non-dictionary stream header.");
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
        entries[PdfName.FromAscii("Length")] = new PdfReference(lengthNumber, 0);
        return new PdfDictionary(entries);
    }

    private static Dictionary<int, string> QdfPageMarksSorted(
        Dictionary<int, QpdfObject> lookup,
        Dictionary<int, int> combined)
    {
        Dictionary<int, string> marks = new();
        if (!lookup.TryGetValue(2, out QpdfObject? pagesRaw) || pagesRaw is not PdfDictionary pages)
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

            if (!combined.TryGetValue(kidReference.ObjectNumber, out int pageNumber))
            {
                continue;
            }

            seq += 1;
            marks[pageNumber] = $"%% Page {seq}";
            if (!lookup.TryGetValue(kidReference.ObjectNumber, out QpdfObject? pageRaw) || pageRaw is not PdfDictionary pageDict)
            {
                continue;
            }

            if (!pageDict.TryGet(PdfName.FromAscii("Contents"), out QpdfObject? contents) || contents is null)
            {
                continue;
            }

            if (contents is PdfReference contentReference)
            {
                if (combined.TryGetValue(contentReference.ObjectNumber, out int contentNumber) && !marks.ContainsKey(contentNumber))
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
                        && combined.TryGetValue(itemReference.ObjectNumber, out int itemNumber)
                        && !marks.ContainsKey(itemNumber))
                    {
                        marks[itemNumber] = $"%% Contents for page {seq}";
                    }
                }
            }
        }

        return marks;
    }    private sealed record QdfEmission(int Number, QpdfObject? Value, int? OriginalSource, int? LengthFor);

    private static bool IsDiscardedXRefStream(QpdfObject value)
    {
        return value is PdfStream stream
            && stream.Dictionary.TryGet(PdfName.FromAscii("Type"), out QpdfObject? type)
            && type is PdfName name
            && name.DisplayText == "XRef";
    }

    private static int FindLengthId(List<QdfEmission> emissions, Dictionary<int, int> renumber, int streamNumber)
    {
        foreach (QdfEmission emission in emissions)
        {
            if (emission.LengthFor is not null && renumber.TryGetValue(emission.LengthFor.Value, out int mapped) && mapped == streamNumber)
            {
                return emission.Number;
            }
        }

        throw new QpdfException($"QDF publication lost the length object for stream {streamNumber}.");
    }

    private static long StreamLength(QdfEmission lengthEmission, List<QdfEmission> emissions, Dictionary<int, int> renumber)
    {
        int target = lengthEmission.LengthFor is null ? -1 : (renumber.TryGetValue(lengthEmission.LengthFor.Value, out int mapped) ? mapped : -1);
        foreach (QdfEmission emission in emissions)
        {
            if (emission.Number == target && emission.Value is PdfStream stream)
            {
                return stream.Data.Length;
            }
        }

        throw new QpdfException("QDF publication lost a length object stream.");
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