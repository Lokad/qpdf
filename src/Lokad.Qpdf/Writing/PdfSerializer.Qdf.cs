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
    private static readonly PdfName QdfTypeKey = PdfName.FromAscii("Type");
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

        using WriterOrder<NumberedObject> sorted = WriterOrder<NumberedObject>.Create(model.Count, 16L, _governor, cancellationToken);
        foreach (NumberedObject numbered in model)
        {
            if (!IsDiscardedXRefStream(numbered.Value))
            {
                sorted.Add(numbered);
            }
        }

        sorted.Sort(new WriterObjectOrder());
        using QdfEmissionPlan emissions = QdfEmissionPlan.Create(sorted, originalIds, _governor, cancellationToken);
        WriterIndex<int> renumber = emissions.Renumber;
        int lastLengthNumber = emissions.LastLengthNumber;
        int emissionCount = emissions.Count;

        using WriterIndex<QpdfObject> classicLookup = WriterIndex<QpdfObject>.Create(sorted.Count, IntPtr.Size, _governor, cancellationToken);
        foreach (NumberedObject numbered in sorted)
        {
            classicLookup[numbered.Number] = numbered.Value;
        }

        using WriterIndex<QdfMark> marks = QdfPageMarksSorted(classicLookup, renumber, cancellationToken);
        classicLookup.Dispose();
        sorted.Dispose();
        int? mappedInfo = null;
        if (infoRef.HasValue)
        {
            if (!renumber.TryGetValue(infoRef.Value, out int mapped))
            {
                throw new QpdfException($"QDF publication reached unmapped info object {infoRef.Value}.");
            }

            mappedInfo = mapped;
        }

        if (lastLengthNumber == 0) { renumber.Dispose(); }
        await AppendFormattedAsync([WriterPart.Text("%PDF-"), WriterPart.Number(version.Major), WriterPart.Text("."), WriterPart.Number(version.Minor), WriterPart.Text("\n")], sink, cancellationToken).ConfigureAwait(false);
        await AppendBytesAsync(QdfBinaryLine, sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("%QDF-1.0\n\n", sink, cancellationToken).ConfigureAwait(false);
        using WriterOrder<long> offsets = WriterOrder<long>.Create(emissionCount, sizeof(long), _governor, cancellationToken);
        foreach (QdfEmission emission in emissions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            if (emission.LengthFor is not null)
            {
                offsets.Add(sink.Length);
                await AppendFormattedAsync([WriterPart.Number(emission.Number), WriterPart.Text(" 0 obj\n")], sink, cancellationToken).ConfigureAwait(false);
                long lengthValue = emissions.StreamLength(emission);
                if (emission.Number == lastLengthNumber) { renumber.Dispose(); }
                await AppendFormattedAsync([WriterPart.Number(lengthValue)], sink, cancellationToken).ConfigureAwait(false);
                await AppendAsciiAsync("\nendobj\n\n", sink, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (marks.TryGetValue(emission.Number, out QdfMark mark))
            {
                await AppendQdfMarkAsync(mark, sink, cancellationToken).ConfigureAwait(false);
            }

            if (emission.OriginalSource.HasValue && !qdf.NoOriginalIds)
            {
                await AppendFormattedAsync([WriterPart.Text("%% Original object ID: "), WriterPart.Number(emission.OriginalSource.Value), WriterPart.Text(" 0\n")], sink, cancellationToken).ConfigureAwait(false);
            }

            offsets.Add(sink.Length);
            await AppendFormattedAsync([WriterPart.Number(emission.Number), WriterPart.Text(" 0 obj\n")], sink, cancellationToken).ConfigureAwait(false);
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

        renumber.Dispose(); marks.Dispose(); emissions.Dispose();
        long xrefOffset = sink.Length;
        await AppendFormattedAsync([WriterPart.Text("xref\n0 "), WriterPart.Number(emissionCount + 1), WriterPart.Text("\n")], sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("0000000000 65535 f \n", sink, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < offsets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offsets[i] > 9_999_999_999L)
            {
                throw new QpdfException($"Object offset {offsets[i]} exceeds the classic xref field width.");
            }

            await AppendFormattedAsync([WriterPart.ZeroPaddedNumber(offsets[i], 10), WriterPart.Text(" 00000 n \n")], sink, cancellationToken).ConfigureAwait(false);
        }

        offsets.Dispose();
        await AppendAsciiAsync("trailer <<\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("  /Root 1 0 R\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendFormattedAsync([WriterPart.Text("  /Size "), WriterPart.Number(emissionCount + 1), WriterPart.Text("\n")], sink, cancellationToken).ConfigureAwait(false);
        if (mappedInfo.HasValue)
        {
            await AppendFormattedAsync([WriterPart.Text("  /Info "), WriterPart.Number(mappedInfo.Value), WriterPart.Text(" 0 R\n")], sink, cancellationToken).ConfigureAwait(false);
        }

        await AppendFormattedAsync([WriterPart.Text("  /ID [<"), WriterPart.Hex(id1), WriterPart.Text("><"), WriterPart.Hex(id2), WriterPart.Text(">]\n")], sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync(">>\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendFormattedAsync([WriterPart.Text("startxref\n"), WriterPart.Number(xrefOffset), WriterPart.Text("\n%%EOF\n")], sink, cancellationToken).ConfigureAwait(false);
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
        using WriterIndex<QpdfObject> lookup = WriterIndex<QpdfObject>.Create(IntPtr.Size, _governor, cancellationToken);
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

        using WriterIndex<byte> members = WriterIndex<byte>.Create(sizeof(byte), _governor, cancellationToken);
        foreach (IReadOnlyList<int> group in objectStreamGroups)
        {
            foreach (int member in group)
            {
                members.TryAdd(member, 0);
            }
        }

        using WriterOrder<int> ordered = WriterOrder<int>.Create(lookup.Count, sizeof(int), _governor, cancellationToken);
        foreach (KeyValuePair<int, QpdfObject> entry in lookup) { ordered.Add(entry.Key); }
        ordered.Sort(new WriterNumberOrder());
        using WriterIndex<int> combined = WriterIndex<int>.Create(sizeof(int), _governor, cancellationToken);
        using WriterIndex<int> memberContainer = WriterIndex<int>.Create(sizeof(int), _governor, cancellationToken);
        using WriterIndex<int> memberIndex = WriterIndex<int>.Create(sizeof(int), _governor, cancellationToken);
        using WriterOrder<(int Old, int New, QpdfObject Value, int? Original)> regulars =
            WriterOrder<(int Old, int New, QpdfObject Value, int? Original)>.Create(lookup.Count, 32L, _governor, cancellationToken);
        int next = 1;
        foreach (int old in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (members.ContainsKey(old))
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

        ordered.Dispose(); members.Dispose();
        using WriterOrder<(int Container, IReadOnlyList<int> Group)> containers =
            WriterOrder<(int Container, IReadOnlyList<int> Group)>.Create(objectStreamGroups.Count, 16L, _governor, cancellationToken);
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
        using WriterIndex<QdfMark> marks = QdfPageMarksSorted(lookup, combined, cancellationToken);

        await AppendFormattedAsync([WriterPart.Text("%PDF-"), WriterPart.Number(version.Major), WriterPart.Text("."), WriterPart.Number(version.Minor), WriterPart.Text("\n")], sink, cancellationToken).ConfigureAwait(false);
        await AppendBytesAsync(QdfBinaryLine, sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("%QDF-1.0\n\n", sink, cancellationToken).ConfigureAwait(false);
        using WriterIndex<long> offsets = WriterIndex<long>.Create(sizeof(long), _governor, cancellationToken);
        foreach ((int old, int number, QpdfObject value, int? source) in regulars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            using WriterValue remapped = value is PdfStream originalStream
                ? WriterValue.StreamHeader(originalStream.Dictionary, number + 1, combined, _governor, cancellationToken)
                : WriterValue.Remap(value, combined, _governor, cancellationToken);
            if (marks.TryGetValue(number, out QdfMark mark))
            {
                await AppendQdfMarkAsync(mark, sink, cancellationToken).ConfigureAwait(false);
            }

            if (source.HasValue && !qdf.NoOriginalIds)
            {
                await AppendFormattedAsync([WriterPart.Text("%% Original object ID: "), WriterPart.Number(source.Value), WriterPart.Text(" 0\n")], sink, cancellationToken).ConfigureAwait(false);
            }

            offsets.Add(number, sink.Length);
            await AppendFormattedAsync([WriterPart.Number(number), WriterPart.Text(" 0 obj\n")], sink, cancellationToken).ConfigureAwait(false);
            if (value is PdfStream stream)
            {
                int lengthNumber = number + 1;
                await AppendQdfDictionaryAsync(AssertDictionary(remapped.Value), sink, 0, cancellationToken).ConfigureAwait(false);
                remapped.Dispose();
                ReadOnlyMemory<byte> data = stream.RawMemory;
                await AppendAsciiAsync("\nstream\n", sink, cancellationToken).ConfigureAwait(false);
                await AppendStreamPayloadAsync(stream, sink, cancellationToken).ConfigureAwait(false);
                bool addedNewline = newlineBeforeEndstream || data.Length == 0 || data.Span[data.Length - 1] != 0x0A;
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
                await AppendFormattedAsync([WriterPart.Number(lengthNumber), WriterPart.Text(" 0 obj\n")], sink, cancellationToken).ConfigureAwait(false);
                await AppendFormattedAsync([WriterPart.Number(data.Length)], sink, cancellationToken).ConfigureAwait(false);
                await AppendAsciiAsync("\nendobj\n\n", sink, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await AppendQdfValueAsync(remapped.Value, sink, 0, cancellationToken).ConfigureAwait(false);
            remapped.Dispose();
            await AppendAsciiAsync("\nendobj\n\n", sink, cancellationToken).ConfigureAwait(false);
        }

        regulars.Dispose();
        foreach ((int container, IReadOnlyList<int> group) in containers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            using ObjectStreamCapture headers = ObjectStreamCapture.Create(group.Count, _limits, _governor, cancellationToken);
            using ObjectStreamCapture bodies = ObjectStreamCapture.Create(group.Count, _limits, _governor, cancellationToken);
            foreach (int member in group)
            {
                using WriterValue memberValue = WriterValue.Remap(lookup[member], combined, _governor, cancellationToken);
                using BufferedSink head = BufferedSink.Create(_limits, _governor, cancellationToken);
                await AppendFormattedAsync([WriterPart.Text("%% Object stream: object "), WriterPart.Number(combined[member]), WriterPart.Text(", index "), WriterPart.Number(memberIndex[member])], head, cancellationToken).ConfigureAwait(false);
                if (originalIds is not null && originalIds.TryGetValue(member, out int memberSource) && !qdf.NoOriginalIds)
                {
                    await AppendFormattedAsync([WriterPart.Text("; original object ID: "), WriterPart.Number(memberSource)], head, cancellationToken).ConfigureAwait(false);
                }

                await AppendAsciiAsync("\n", head, cancellationToken).ConfigureAwait(false);
                if (marks.TryGetValue(combined[member], out QdfMark memberMark))
                {
                    await AppendQdfMarkAsync(memberMark, head, cancellationToken).ConfigureAwait(false);
                }

                headers.Append(head, cancellationToken);
                head.Dispose();
                using BufferedSink body = BufferedSink.Create(_limits, _governor, cancellationToken);
                await AppendQdfValueAsync(memberValue.Value, body, 0, cancellationToken).ConfigureAwait(false);
                memberValue.Dispose();
                await AppendAsciiAsync("\n", body, cancellationToken).ConfigureAwait(false);
                bodies.Append(body, cancellationToken);
            }

            // Upstream QDF keeps each member comment ahead of its value in the payload,
            // counts the first comment in /First, and records member offsets at value
            // starts so readers land on objects rather than comments.
            using ObjectStreamPayload payload = ObjectStreamPayload.CreateQdfIndexed(group, combined, headers, bodies,
                _limits, _governor, cancellationToken);
            offsets.Add(container, sink.Length);
            using (ObjectStreamHeader header = ObjectStreamHeader.Create(container, group.Count, payload.Data.Length,
                payload.First, false, true, _limits, _governor, cancellationToken))
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

            await AppendAsciiAsync("\nendobj\n\n", sink, cancellationToken).ConfigureAwait(false);
        }

        containers.Dispose(); lookup.Dispose(); marks.Dispose();
        long xrefOffset = sink.Length;
        offsets[xrefNumber] = xrefOffset;
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
        foreach (KeyValuePair<int, int> entry in memberIndex)
        {
            int index = entry.Value;
            if (index > maxIndex)
            {
                maxIndex = index;
            }
        }

        int f1 = System.Math.Max(BytesNeeded(maxOffset), BytesNeeded(size));
        int f2 = BytesNeeded(maxIndex);
        using WriterXref xref = WriterXref.Create(size, f1, f2, _limits, _governor, cancellationToken);
        for (int number = 0; number < size; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (number == 0)
            {
                xref.Append(0, 0L, 0L);
            }
            else if (offsets.TryGetValue(number, out long offset))
            {
                xref.Append(1, offset, 0L);
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

                xref.Append(2, foundContainer, foundIndex);
            }
        }

        offsets.Dispose(); memberContainer.Dispose(); memberIndex.Dispose();
        xref.Finish(false, null, _limits);
        int qdfRoot = 1;
        if (combined.TryGetValue(1, out int catalogNumber))
        {
            qdfRoot = catalogNumber;
        }
        if (!infoRef.HasValue) { combined.Dispose(); }
        await AppendFormattedAsync([WriterPart.Number(xrefNumber), WriterPart.Text(" 0 obj\n<<\n  /Type /XRef\n  /Length "), WriterPart.Number(xref.Data.Length), WriterPart.Text("\n  /W [ 1 "), WriterPart.Number(f1), WriterPart.Text(" "), WriterPart.Number(f2), WriterPart.Text(" ]\n  /Root "), WriterPart.Number(qdfRoot), WriterPart.Text(" 0 R\n  /Size "), WriterPart.Number(size), WriterPart.Text("\n")], sink, cancellationToken).ConfigureAwait(false);
        if (infoRef.HasValue)
        {
            if (!combined.TryGetValue(infoRef.Value, out int mappedInfo))
            {
                throw new QpdfException($"QDF publication reached unmapped info object {infoRef.Value}.");
            }

            combined.Dispose();
            await AppendFormattedAsync([WriterPart.Text("  /Info "), WriterPart.Number(mappedInfo), WriterPart.Text(" 0 R\n")], sink, cancellationToken).ConfigureAwait(false);
        }

        combined.Dispose();
        await AppendFormattedAsync([WriterPart.Text("  /ID [<"), WriterPart.Hex(id1), WriterPart.Text("><"), WriterPart.Hex(id2), WriterPart.Text(">]\n")], sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync(">>\nstream\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendBytesAsync(xref.Data, sink, cancellationToken).ConfigureAwait(false);
        xref.Dispose();
        await AppendAsciiAsync("\nendstream\nendobj\n\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendFormattedAsync([WriterPart.Text("startxref\n"), WriterPart.Number(xrefOffset), WriterPart.Text("\n%%EOF\n")], sink, cancellationToken).ConfigureAwait(false);
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

    private WriterIndex<QdfMark> QdfPageMarksSorted(
        WriterIndex<QpdfObject> lookup,
        WriterIndex<int> combined, CancellationToken cancellationToken)
    {
        WriterIndex<QdfMark> marks = WriterIndex<QdfMark>.Create(2L * sizeof(int), _governor, cancellationToken);
        try
        {
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
                cancellationToken.ThrowIfCancellationRequested(); _governor.ChargeWork(1L);
                if (kid is not PdfReference kidReference)
                {
                    continue;
                }

                if (!combined.TryGetValue(kidReference.ObjectNumber, out int pageNumber))
                {
                    continue;
                }

                seq += 1;
                marks[pageNumber] = new QdfMark(seq, false);
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
                        marks[contentNumber] = new QdfMark(seq, true);
                    }

                    continue;
                }

                if (contents is PdfArray contentArray)
                {
                    foreach (QpdfObject item in contentArray.Items)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); _governor.ChargeWork(1L);
                        if (item is PdfReference itemReference
                            && combined.TryGetValue(itemReference.ObjectNumber, out int itemNumber)
                            && !marks.ContainsKey(itemNumber))
                        {
                            marks[itemNumber] = new QdfMark(seq, true);
                        }
                    }
                }
            }

            return marks;
        }
        catch { marks.Dispose(); throw; }
    }

    private static bool IsDiscardedXRefStream(QpdfObject value)
    {
        return value is PdfStream stream
            && stream.Dictionary.TryGet(QdfTypeKey, out QpdfObject? type)
            && type is PdfName name
            && name.RawSpan.SequenceEqual("XRef"u8);
    }

    private async ValueTask<bool> AppendQdfStreamAsync(PdfStream stream, IByteSink sink, bool newlineBeforeEndstream, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> data = stream.RawMemory;
        await AppendQdfDictionaryAsync(stream.Dictionary, sink, 0, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("\nstream\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendStreamPayloadAsync(stream, sink, cancellationToken).ConfigureAwait(false);
        bool addedNewline = newlineBeforeEndstream || data.Length == 0 || data.Span[data.Length - 1] != 0x0A;
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
                await AppendQdfIndentAsync(level, true, "", sink, cancellationToken).ConfigureAwait(false);
                await AppendQdfValueAsync(array[i], sink, level + 1, cancellationToken).ConfigureAwait(false);
            }

            await AppendQdfIndentAsync(level, false, "]", sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (value is PdfDictionary dictionary)
        {
            await AppendQdfDictionaryAsync(dictionary, sink, level, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (value is PdfReference reference)
        {
            await AppendFormattedAsync([WriterPart.Number(reference.ObjectNumber), WriterPart.Text(" 0 R")], sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (value is PdfStream stream)
        {
            await AppendQdfDictionaryAsync(stream.Dictionary, sink, level, cancellationToken).ConfigureAwait(false);
            await AppendAsciiAsync("\nstream\n", sink, cancellationToken).ConfigureAwait(false);
            await AppendStreamPayloadAsync(stream, sink, cancellationToken).ConfigureAwait(false);
            await AppendAsciiAsync("endstream", sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new QpdfException($"Unsupported value of type {value.GetType().Name} during serialization.");
    }

    private async ValueTask AppendQdfDictionaryAsync(PdfDictionary dictionary, IByteSink sink, int level, CancellationToken cancellationToken)
    {
        await AppendAsciiAsync("<<", sink, cancellationToken).ConfigureAwait(false);
        if (dictionary.Count <= 1)
        {
            using PdfDictionary.EntryEnumerator single = dictionary.GetEntryEnumerator();
            if (single.MoveNext())
            {
                await AppendQdfEntryAsync(single.Current.Key, single.Current.Value, sink, level, cancellationToken).ConfigureAwait(false);
            }
            await AppendQdfIndentAsync(level, false, ">>", sink, cancellationToken).ConfigureAwait(false);
            return;
        }
        using WriterOrder<PdfName> ordered = WriterOrder<PdfName>.Create(dictionary.Count, IntPtr.Size, _governor, cancellationToken);
        using (PdfDictionary.EntryEnumerator entries = dictionary.GetEntryEnumerator())
        {
            while (entries.MoveNext()) { ordered.Add(entries.Current.Key); }
        }
        ordered.Sort(new WriterNameOrder(_governor, cancellationToken));
        foreach (PdfName key in ordered)
        {
            if (!dictionary.TryGet(key, out QpdfObject? entry) || entry is null)
            {
                continue;
            }

            await AppendQdfEntryAsync(key, entry, sink, level, cancellationToken).ConfigureAwait(false);
        }

        ordered.Dispose();
        await AppendQdfIndentAsync(level, false, ">>", sink, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendQdfEntryAsync(PdfName key, QpdfObject entry, IByteSink sink, int level, CancellationToken cancellationToken)
    {
        if (entry is PdfNull) { return; }
        await AppendQdfIndentAsync(level, true, "", sink, cancellationToken).ConfigureAwait(false);
        await AppendNameAsync(key, sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync(" ", sink, cancellationToken).ConfigureAwait(false);
        await AppendQdfValueAsync(entry, sink, level + 1, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask AppendQdfIndentAsync(int level, bool large, string suffix, IByteSink sink, CancellationToken cancellationToken)
        => AppendFormattedAsync([WriterPart.Text("\n"), WriterPart.Spaces(checked(2 * (large ? level + 1 : level))), WriterPart.Text(suffix)], sink, cancellationToken);

    private ValueTask AppendQdfMarkAsync(QdfMark mark, IByteSink sink, CancellationToken cancellationToken)
        => AppendFormattedAsync([WriterPart.Text(mark.Contents ? "%% Contents for page " : "%% Page "), WriterPart.Number(mark.Page), WriterPart.Text("\n")], sink, cancellationToken);

    private readonly record struct QdfMark(int Page, bool Contents);
}
