using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>
/// Two-pass linearized publication. A counting pass records part alignment
/// with placeholder dictionaries and zeroed cross-references; hint tables
/// are generated from those hint-less coordinates; an emitting pass fills
/// real values and hint content. Padding keeps both passes byte-identical
/// in length, verified by a replay check. An optional pass-one sink captures
/// the counting pass bytes for debugging; the captured file is not a valid
/// PDF file.
/// </summary>
internal sealed class LinearizedWriter
{
    private const int LindictPad = 200;
    private const int PrevFieldWidth = 21;
    private const long MaxOffsetField = 9999999999L;

    private readonly QpdfLimits _limits;
    private readonly ResourceGovernor _governor;
    private readonly PdfSerializer _serializer;

    internal LinearizedWriter(QpdfLimits limits, ResourceGovernor governor)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        _serializer = new PdfSerializer(limits, governor);
    }

    internal async ValueTask<long> WriteLinearizedAsync(
        PdfVersion version,
        List<NumberedObject> model,
        int? infoRef,
        byte[] id1,
        byte[] id2,
        bool newlineBeforeEndstream,
        bool compressHints,
        int? compressionLevel,
        IByteSink sink,
        IByteSink? pass1Sink,
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

        LinearizedParts parts = LinearizedParts.Classify(model, _governor, cancellationToken);
        Dictionary<int, QpdfObject> emitted = RemapModel(parts, cancellationToken);
        int? infoNew = null;
        if (infoRef.HasValue)
        {
            if (!parts.Renumber.TryGetValue(infoRef.Value, out int mapped))
            {
                throw new QpdfException($"Linearized publication reached unmapped info object {infoRef.Value}.");
            }

            infoNew = mapped;
        }

        CountingSink counter = new();
        IByteSink countingSink = counter;
        if (pass1Sink is not null)
        {
            countingSink = new TeeSink(counter, pass1Sink);
        }

        LinearLayout counted = await EmitPassAsync(
            version, parts, emitted, infoNew, id1, id2, newlineBeforeEndstream, false, null, null, countingSink, cancellationToken).ConfigureAwait(false);
        byte[] hintObject = BuildHintObject(parts, counted, compressHints, compressionLevel, cancellationToken);
        long hintLength = hintObject.LongLength;

        LinearLayout finalLayout = await EmitPassAsync(
            version, parts, emitted, infoNew, id1, id2, newlineBeforeEndstream, true, hintObject, counted, sink, cancellationToken).ConfigureAwait(false);
        long written = finalLayout.FileSize;
        if (written != checked(counted.FileSize + hintLength))
        {
            throw new QpdfException("Linearized replay diverged between counting and emitting passes.");
        }

        return written;
    }

    private Dictionary<int, QpdfObject> RemapModel(LinearizedParts parts, CancellationToken cancellationToken)
    {
        Dictionary<int, QpdfObject> emitted = new(parts.Renumber.Count);
        foreach (KeyValuePair<int, int> entry in parts.Renumber)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!parts.Lookup.TryGetValue(entry.Key, out QpdfObject? value) || value is null)
            {
                throw new QpdfException($"Linearized publication lost object {entry.Key}.");
            }

            emitted[entry.Value] = ObjectRemap.RemapIds(value, parts.Renumber, _governor);
        }

        return emitted;
    }

    private sealed class TeeSink : IByteSink
    {
        private readonly IByteSink _first;
        private readonly IByteSink _second;

        internal TeeSink(IByteSink first, IByteSink second)
        {
            _first = first;
            _second = second;
        }

        public long Length => _first.Length;

        public async ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            await _first.AppendAsync(chunk, cancellationToken).ConfigureAwait(false);
            await _second.AppendAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class LinearLayout
    {
        internal long FirstXref;
        internal long HintPosition;
        internal long Part6End;
        internal long MainXref;
        internal long MainXrefTable;
        internal long FileSize;
        internal readonly Dictionary<int, long> Starts = new();
        internal readonly Dictionary<int, long> Lengths = new();
    }

    private async ValueTask<LinearLayout> EmitPassAsync(
        PdfVersion version,
        LinearizedParts parts,
        Dictionary<int, QpdfObject> emitted,
        int? infoNew,
        byte[] id1,
        byte[] id2,
        bool newlineBeforeEndstream,
        bool final,
        byte[]? hintObject,
        LinearLayout? counted,
        IByteSink sink,
        CancellationToken cancellationToken)
    {
        LinearLayout layout = new();
        string idHex1 = ToHex(id1);
        string idHex2 = ToHex(id2);
        _governor.ChargeWork(idHex1.Length + idHex2.Length);
        int catalogNew = parts.Renumber[1];

        await AppendAsciiAsync($"%PDF-{version}\n", sink, cancellationToken).ConfigureAwait(false);

        string lindictInner = LindictInner(parts.Renumber, parts.Pages, parts.LindictNumber, counted, hintObject, final);
        layout.Starts[parts.LindictNumber] = sink.Length;
        await AppendAsciiAsync($"{parts.LindictNumber} 0 obj\n<<", sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync(lindictInner, sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync(">>\nendobj\n", sink, cancellationToken).ConfigureAwait(false);
        layout.Lengths[parts.LindictNumber] = checked(sink.Length - layout.Starts[parts.LindictNumber]);

        layout.FirstXref = sink.Length;
        List<int> firstHalf = FirstHalfNumbers(parts);
        await AppendAsciiAsync($"xref\n{firstHalf[0]} {firstHalf.Count}\n", sink, cancellationToken).ConfigureAwait(false);
        foreach (int number in firstHalf)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long offset = XrefOffset(parts.HintNumber, counted, hintObject, final, number);
            if (offset > MaxOffsetField)
            {
                throw new QpdfException($"Object offset {offset} exceeds the classic xref field width.");
            }
            await AppendAsciiAsync($"{offset:D10} 00000 n \n", sink, cancellationToken).ConfigureAwait(false);
        }

        await AppendAsciiAsync($"trailer << /Root {catalogNew} 0 R", sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync($" /Size {firstHalf[^1] + 1}", sink, cancellationToken).ConfigureAwait(false);
        long prevValue = final && counted is not null ? checked(counted.MainXrefTable + HintLength(hintObject)) : 0L;
        string prevDigits = prevValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (prevDigits.Length > PrevFieldWidth)
        {
            throw new QpdfException("Linearized cross-reference exceeds its reserved space.");
        }
        await AppendAsciiAsync($" /Prev {prevValue}{new string(' ', PrevFieldWidth - prevDigits.Length)}", sink, cancellationToken).ConfigureAwait(false);
        if (infoNew.HasValue)
        {
            await AppendAsciiAsync($" /Info {infoNew.Value} 0 R", sink, cancellationToken).ConfigureAwait(false);
        }

        await AppendAsciiAsync($" /ID [<{idHex1}><{idHex2}>] >>\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("startxref\n0\n%%EOF\n", sink, cancellationToken).ConfigureAwait(false);

        await EmitObjectsAsync(parts, parts.Part4, emitted, newlineBeforeEndstream, layout, sink, cancellationToken).ConfigureAwait(false);
        if (hintObject is not null)
        {
            layout.HintPosition = sink.Length;
            layout.Starts[parts.HintNumber] = sink.Length;
            await AppendBytesAsync(hintObject, sink, cancellationToken).ConfigureAwait(false);
            layout.Lengths[parts.HintNumber] = checked(sink.Length - layout.Starts[parts.HintNumber]);
        }
        else
        {
            layout.HintPosition = sink.Length;
            layout.Starts[parts.HintNumber] = sink.Length;
            layout.Lengths[parts.HintNumber] = 0L;
        }

        await EmitObjectsAsync(parts, parts.Part6, emitted, newlineBeforeEndstream, layout, sink, cancellationToken).ConfigureAwait(false);
        layout.Part6End = sink.Length;
        foreach (IReadOnlyList<int> run in parts.Part7)
        {
            await EmitObjectsAsync(parts, run, emitted, newlineBeforeEndstream, layout, sink, cancellationToken).ConfigureAwait(false);
        }

        await EmitObjectsAsync(parts, parts.Part8, emitted, newlineBeforeEndstream, layout, sink, cancellationToken).ConfigureAwait(false);
        await EmitObjectsAsync(parts, parts.Part9, emitted, newlineBeforeEndstream, layout, sink, cancellationToken).ConfigureAwait(false);

        layout.MainXrefTable = sink.Length;
        List<int> secondHalf = SecondHalfNumbers(parts);
        await AppendAsciiAsync($"xref\n0 {secondHalf.Count + 1}", sink, cancellationToken).ConfigureAwait(false);
        layout.MainXref = sink.Length;
        await AppendAsciiAsync("\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("0000000000 65535 f \n", sink, cancellationToken).ConfigureAwait(false);
        foreach (int number in secondHalf)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long position = XrefOffset(parts.HintNumber, counted, hintObject, final, number);
            if (position > MaxOffsetField)
            {
                throw new QpdfException($"Object offset {position} exceeds the classic xref field width.");
            }

            await AppendAsciiAsync($"{position:D10} 00000 n \n", sink, cancellationToken).ConfigureAwait(false);
        }

        await AppendAsciiAsync($"trailer << /Size {secondHalf[^1] + 1} /ID [<{idHex1}><{idHex2}>] >>\n", sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync($"startxref\n{layout.FirstXref}\n%%EOF\n", sink, cancellationToken).ConfigureAwait(false);
        layout.FileSize = sink.Length;
        return layout;
    }

    private static long XrefOffset(int hintNumber, LinearLayout? counted, byte[]? hintObject, bool final, int number)
    {
        if (!final || counted is null || !counted.Starts.TryGetValue(number, out long recorded))
        {
            return 0L;
        }

        if (number != hintNumber && recorded >= counted.HintPosition)
        {
            return checked(recorded + HintLength(hintObject));
        }

        return recorded;
    }

    private static List<int> FirstHalfNumbers(LinearizedParts parts)
    {
        List<int> numbers = new();
        numbers.Add(parts.LindictNumber);
        foreach (int old in parts.Part4)
        {
            numbers.Add(parts.Renumber[old]);
        }

        numbers.Add(parts.HintNumber);
        foreach (int old in parts.Part6)
        {
            numbers.Add(parts.Renumber[old]);
        }
        numbers.Sort();
        return numbers;
    }

    private static List<int> SecondHalfNumbers(LinearizedParts parts)
    {
        List<int> numbers = new();
        foreach (IReadOnlyList<int> run in parts.Part7)
        {
            foreach (int old in run)
            {
                numbers.Add(parts.Renumber[old]);
            }
        }

        foreach (int old in parts.Part8)
        {
            numbers.Add(parts.Renumber[old]);
        }

        foreach (int old in parts.Part9)
        {
            numbers.Add(parts.Renumber[old]);
        }
        numbers.Sort();
        return numbers;
    }

    private static string LindictInner(Dictionary<int, int> renumber, IReadOnlyList<int> pages, int lindictNumber, LinearLayout? counted, byte[]? hintObject, bool final)
    {
        if (!final || counted is null || hintObject is null)
        {
            return new string(' ', LindictPad);
        }

        long hintLength = hintObject.LongLength;
        long fileSize = checked(counted.FileSize + hintLength);
        long part6End = checked(counted.Part6End + hintLength);
        long mainXref = checked(counted.MainXref + hintLength);
        string inner = $" /Linearized 1 /L {fileSize} /H [ {counted.HintPosition} {hintLength} ]"
            + $" /O {renumber[pages[0]]} /E {part6End} /N {pages.Count} /T {mainXref}";
        if (inner.Length > LindictPad)
        {
            throw new QpdfParseException("Linearization dictionary exceeds its reserved space.", null, null);
        }

        return inner + new string(' ', LindictPad - inner.Length);
    }

    private async ValueTask EmitObjectsAsync(
        LinearizedParts parts,
        IReadOnlyList<int> olds,
        Dictionary<int, QpdfObject> emitted,
        bool newlineBeforeEndstream,
        LinearLayout layout,
        IByteSink sink,
        CancellationToken cancellationToken)
    {
        foreach (int old in olds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            if (!parts.Renumber.TryGetValue(old, out int number))
            {
                throw new QpdfException($"Linearized publication lost object {old}.");
            }

            if (!emitted.TryGetValue(number, out QpdfObject? value) || value is null)
            {
                throw new QpdfException($"Linearized publication lost object {number}.");
            }

            layout.Starts[number] = sink.Length;
            await _serializer.AppendNumberedObjectAsync(number, value, sink, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
            layout.Lengths[number] = checked(sink.Length - layout.Starts[number]);
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
            int take = System.Math.Min(8192, chunk.Length - offset);
            await sink.AppendAsync(new ReadOnlyMemory<byte>(chunk, offset, take), cancellationToken).ConfigureAwait(false);
            offset += take;
        }
    }

    private static string ToHex(byte[] bytes)
    {
        System.Text.StringBuilder builder = new(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            builder.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static long HintLength(byte[]? hintObject)
    {
        return hintObject is null ? 0L : hintObject.LongLength;
    }

    private byte[] BuildHintObject(
        LinearizedParts parts,
        LinearLayout counted,
        bool compressHints,
        int? compressionLevel,
        CancellationToken cancellationToken)
    {
        return BuildHintObjectCore(parts.Part6, parts.Part8, parts.Part7, parts.Pages, parts.PageUsers, parts.Outlines, parts.Renumber, parts.HintNumber, true, counted, compressHints, compressionLevel, cancellationToken);
    }

    private byte[] BuildHintObject(
        StreamedParts parts,
        LinearLayout counted,
        bool compressHints,
        int? compressionLevel,
        CancellationToken cancellationToken)
    {
        return BuildHintObjectCore(parts.Part6, parts.Part8, parts.Part7, parts.Pages, parts.PageUsers, parts.Outlines, parts.Renumber, parts.HintNumber, parts.OutlinesDirect, counted, compressHints, compressionLevel, cancellationToken);
    }

    private byte[] BuildHintObjectCore(
        IReadOnlyList<int> part6,
        IReadOnlyList<int> part8,
        IReadOnlyList<IReadOnlyList<int>> part7,
        IReadOnlyList<int> pages,
        IReadOnlyList<HashSet<int>> pageUsers,
        IReadOnlyList<int> outlines,
        Dictionary<int, int> renumber,
        int hintNumber,
        bool outlinesDirect,
        LinearLayout counted,
        bool compressHints,
        int? compressionLevel,
        CancellationToken cancellationToken)
    {
        List<int> groups = new(part6.Count + part8.Count);
        foreach (int old in part6)
        {
            groups.Add(renumber[old]);
        }

        foreach (int old in part8)
        {
            groups.Add(renumber[old]);
        }

        Dictionary<int, int> groupIndex = new(groups.Count);
        for (int i = 0; i < groups.Count; i++)
        {
            groupIndex[groups[i]] = i;
        }

        List<long> groupLengths = new(groups.Count);
        foreach (int number in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            groupLengths.Add(ObjectLength(counted, number));
        }

        List<int> pageCounts = new(pages.Count);
        List<long> pageLengths = new(pages.Count);
        List<List<int>> pageShared = new(pages.Count);
        pageCounts.Add(part6.Count);
        List<int> part6New = new();
        foreach (int old in part6)
        {
            part6New.Add(renumber[old]);
        }

        pageLengths.Add(RunLength(counted, part6New));
        pageShared.Add(new List<int>());
        for (int i = 1; i < pages.Count; i++)
        {
            List<int> run = new();
            foreach (int old in part7[i - 1])
            {
                run.Add(renumber[old]);
            }
            pageCounts.Add(run.Count);
            pageLengths.Add(RunLength(counted, run));
            pageShared.Add(SortedShared(pageUsers, renumber, i, groupIndex));
        }

        List<int> outlineNew = new(outlines.Count);
        foreach (int old in outlines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outlineNew.Add(renumber[old]);
        }

        long firstPageOffset = counted.Starts[renumber[pages[0]]];
        long firstSharedOffset = groups.Count > part6.Count
            ? counted.Starts[groups[part6.Count]]
            : 0L;
        int firstSharedObj = groups.Count > part6.Count ? groups[part6.Count] : 0;
        int outlineFirst = 0;
        long outlineOffset = 0L;
        long outlineLength = 0L;
        if (outlineNew.Count > 0 && outlinesDirect)
        {
            outlineFirst = outlineNew[0];
            outlineOffset = counted.Starts[outlineFirst];
            outlineLength = RunLength(counted, outlineNew);
        }

        byte[] tables = EncodeHintTables(part6.Count, pageCounts, pageLengths, pageShared, groupLengths, firstPageOffset, firstSharedObj, firstSharedOffset, groups.Count, outlineFirst, outlineOffset, outlineNew.Count, outlineLength, out long sharedAt, out long outlineAt, cancellationToken);
        byte[] data = tables;
        bool flate = compressHints;
        if (flate)
        {
            data = FlateFilter.Encode(tables, compressionLevel, _limits, _governor);
        }

        System.Text.StringBuilder head = new();
        head.Append(hintNumber);
        head.Append(" 0 obj\n<<");
        if (flate)
        {
            head.Append(" /Filter /FlateDecode");
        }

        head.Append(" /S ");
        head.Append(sharedAt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (outlineNew.Count > 0 && outlinesDirect)
        {
            head.Append(" /O ");
            head.Append(outlineAt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        head.Append(" /Length ");
        head.Append(data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        head.Append(" >>\nstream\n");
        byte[] prefix = System.Text.Encoding.ASCII.GetBytes(head.ToString());
        byte[] tail = data.Length == 0 || data[data.Length - 1] != 10
            ? System.Text.Encoding.ASCII.GetBytes("\nendstream\nendobj\n")
            : System.Text.Encoding.ASCII.GetBytes("endstream\nendobj\n");
        _governor.ReserveMemory(checked((long)prefix.Length + data.Length + tail.Length));
        byte[] hintObject = new byte[checked(prefix.Length + data.Length + tail.Length)];
        Buffer.BlockCopy(prefix, 0, hintObject, 0, prefix.Length);
        Buffer.BlockCopy(data, 0, hintObject, prefix.Length, data.Length);
        Buffer.BlockCopy(tail, 0, hintObject, prefix.Length + data.Length, tail.Length);
        return hintObject;
    }

    private static long ObjectLength(LinearLayout counted, int number)
    {
        if (!counted.Lengths.TryGetValue(number, out long length))
        {
            throw new QpdfException($"Linearized publication lost the length of object {number}.");
        }

        return length;
    }

    private static long RunLength(LinearLayout counted, IReadOnlyList<int> run)
    {
        if (run.Count == 0)
        {
            throw new QpdfParseException("Linearized publication reached an empty page run.", null, null);
        }

        long start = counted.Starts[run[0]];
        int last = run[run.Count - 1];
        return checked(counted.Starts[last] + ObjectLength(counted, last) - start);
    }

    private static List<int> SortedShared(IReadOnlyList<HashSet<int>> pageUsers, Dictionary<int, int> renumber, int page, Dictionary<int, int> groupIndex)
    {
        List<int> olds = new(pageUsers[page]);
        olds.Sort();
        List<int> identifiers = new(olds.Count);
        foreach (int old in olds)
        {
            if (renumber.TryGetValue(old, out int number) && groupIndex.TryGetValue(number, out int index))
            {
                identifiers.Add(index);
            }
        }

        return identifiers;
    }

    private byte[] EncodeHintTables(
        int part6Count,
        List<int> pageCounts,
        List<long> pageLengths,
        List<List<int>> pageShared,
        List<long> groupLengths,
        long firstPageOffset,
        int firstSharedObj,
        long firstSharedOffset,
        int totalShared,
        int outlineFirst,
        long outlineOffset,
        int outlineCount,
        long outlineLength,
        out long sharedAt,
        out long outlineAt,
        CancellationToken cancellationToken)
    {
        _ = firstSharedObj;
        _ = firstSharedOffset;
        HintBitWriter pages = new(_governor);
        long minObjects = pageCounts[0];
        long maxObjects = pageCounts[0];
        long minLength = pageLengths[0];
        long maxLength = pageLengths[0];
        long maxShared = 0L;
        for (int i = 0; i < pageCounts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            minObjects = System.Math.Min(minObjects, pageCounts[i]);
            maxObjects = System.Math.Max(maxObjects, pageCounts[i]);
            minLength = System.Math.Min(minLength, pageLengths[i]);
            maxLength = System.Math.Max(maxLength, pageLengths[i]);
            maxShared = System.Math.Max(maxShared, pageShared[i].Count);
        }

        pages.WriteBits(minObjects, 32);
        pages.WriteBits(firstPageOffset, 32);
        pages.WriteBits(BitLength(maxObjects - minObjects), 16);
        pages.WriteBits(minLength, 32);
        pages.WriteBits(BitLength(maxLength - minLength), 16);
        pages.WriteBits(0L, 32);
        pages.WriteBits(0, 16);
        pages.WriteBits(minLength, 32);
        pages.WriteBits(BitLength(maxLength - minLength), 16);
        pages.WriteBits(BitLength(maxShared), 16);
        pages.WriteBits(BitLength(totalShared), 16);
        // Every numerator is zero, so its native encoding has no payload bits.
        pages.WriteBits(0, 16);
        pages.WriteBits(4L, 16);
        for (int i = 0; i < pageCounts.Count; i++)
        {
            pages.WriteBits(pageCounts[i] - minObjects, BitLength(maxObjects - minObjects));
        }

        pages.Flush();
        for (int i = 0; i < pageCounts.Count; i++)
        {
            pages.WriteBits(pageLengths[i] - minLength, BitLength(maxLength - minLength));
        }

        pages.Flush();
        for (int i = 0; i < pageCounts.Count; i++)
        {
            pages.WriteBits(pageShared[i].Count, BitLength(maxShared));
        }

        pages.Flush();
        for (int i = 0; i < pageCounts.Count; i++)
        {
            foreach (int id in pageShared[i])
            {
                pages.WriteBits(id, BitLength(totalShared));
            }
        }

        pages.Flush();
        for (int i = 0; i < pageCounts.Count; i++)
        {
            for (int k = 0; k < pageShared[i].Count; k++)
            {
                pages.WriteBits(0L, 0);
            }
        }

        pages.Flush();
        for (int i = 0; i < pageCounts.Count; i++)
        {
            pages.WriteBits(0L, 0);
        }

        pages.Flush();
        for (int i = 0; i < pageCounts.Count; i++)
        {
            pages.WriteBits(pageLengths[i] - minLength, BitLength(maxLength - minLength));
        }

        pages.Flush();
        sharedAt = pages.Length;
        long minGroup = groupLengths[0];
        long maxGroup = groupLengths[0];
        for (int i = 0; i < groupLengths.Count; i++)
        {
            minGroup = System.Math.Min(minGroup, groupLengths[i]);
            maxGroup = System.Math.Max(maxGroup, groupLengths[i]);
        }

        HintBitWriter shared = new(_governor);
        shared.WriteBits(firstSharedObj, 32);
        shared.WriteBits(firstSharedOffset, 32);
        shared.WriteBits(part6Count, 32);
        shared.WriteBits(totalShared, 32);
        shared.WriteBits(0, 16);
        shared.WriteBits(minGroup, 32);
        shared.WriteBits(BitLength(maxGroup - minGroup), 16);
        for (int i = 0; i < groupLengths.Count; i++)
        {
            shared.WriteBits(groupLengths[i] - minGroup, BitLength(maxGroup - minGroup));
        }

        shared.Flush();
        for (int i = 0; i < groupLengths.Count; i++)
        {
            shared.WriteBits(0L, 1);
        }

        shared.Flush();
        for (int i = 0; i < groupLengths.Count; i++)
        {
            shared.WriteBits(0L, 0);
        }

        shared.Flush();
        byte[] head = pages.ToArray();
        byte[] tail = shared.ToArray();
        outlineAt = checked((long)head.Length + tail.Length);
        byte[] outlines = EncodeOutlineTable(outlineFirst, outlineOffset, outlineCount, outlineLength);
        _governor.ReserveMemory(checked((long)head.Length + tail.Length + outlines.Length));
        byte[] tables = new byte[checked(head.Length + tail.Length + outlines.Length)];
        Buffer.BlockCopy(head, 0, tables, 0, head.Length);
        Buffer.BlockCopy(tail, 0, tables, head.Length, tail.Length);
        Buffer.BlockCopy(outlines, 0, tables, head.Length + tail.Length, outlines.Length);
        return tables;
    }

    private byte[] EncodeOutlineTable(
        int outlineFirst,
        long outlineOffset,
        int outlineCount,
        long outlineLength)
    {
        if (outlineCount <= 0)
        {
            return [];
        }

        HintBitWriter outlines = new(_governor);
        outlines.WriteBits(outlineFirst, 32);
        outlines.WriteBits(outlineOffset, 32);
        outlines.WriteBits(outlineCount, 32);
        outlines.WriteBits(outlineLength, 32);
        return outlines.ToArray();
    }

    private static int BitLength(long value)
    {
        if (value < 0L)
        {
            throw new QpdfParseException("Linearized hint value is negative.", null, null);
        }

        int bits = 0;
        while (value != 0L)
        {
            bits = checked(bits + 1);
            value >>= 1;
        }

        return bits;
    }

    private sealed class HintBitWriter
    {
        private readonly ResourceGovernor _governor;
        private readonly List<byte> _bytes = new();
        private int _current;
        private int _filled;

        internal HintBitWriter(ResourceGovernor governor)
        {
            _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        }

        internal long Length => _bytes.Count;

        internal void WriteBits(long value, int count)
        {
            if (count < 0 || count > 32)
            {
                throw new QpdfParseException("Linearized hint value exceeds its bit width.", null, null);
            }

            if (value < 0L || (count < 63 && value >= (1L << count)))
            {
                throw new QpdfParseException("Linearized hint value exceeds its bit width.", null, null);
            }

            _governor.ChargeWork(1L);
            for (int i = count - 1; i >= 0; i--)
            {
                int bit = (int)((value >> i) & 1L);
                _current = checked((_current << 1) | bit);
                _filled = checked(_filled + 1);
                if (_filled == 8)
                {
                    _governor.ReserveMemory(1L);
                    _bytes.Add((byte)_current);
                    _current = 0;
                    _filled = 0;
                }
            }
        }

        internal void Flush()
        {
            if (_filled > 0)
            {
                _governor.ReserveMemory(1L);
                _bytes.Add((byte)(_current << (8 - _filled)));
                _current = 0;
                _filled = 0;
            }
        }

        internal byte[] ToArray()
        {
            Flush();
            return [.. _bytes];
        }
    }

    internal async ValueTask<long> WriteLinearizedStreamedAsync(
        PdfVersion version,
        List<NumberedObject> model,
        IReadOnlyList<IReadOnlyList<int>> objectStreamGroups,
        int? infoRef,
        byte[] id1,
        byte[] id2,
        bool newlineBeforeEndstream,
        bool compressStreams,
        int? compressionLevel,
        IByteSink sink,
        IByteSink? pass1Sink,
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

        if (objectStreamGroups is null)
        {
            throw new ArgumentNullException(nameof(objectStreamGroups));
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

        StreamedParts parts = LinearizedParts.ClassifyStreamed(model, objectStreamGroups, _governor, cancellationToken);
        if (parts.Containers.Count == 0)
        {
            return await WriteLinearizedAsync(version, model, infoRef, id1, id2, newlineBeforeEndstream, compressStreams, compressionLevel, sink, pass1Sink, cancellationToken).ConfigureAwait(false);
        }

        Dictionary<int, QpdfObject> emitted = new(parts.Renumber.Count);
        foreach (KeyValuePair<int, QpdfObject> entry in parts.Lookup)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!parts.Renumber.TryGetValue(entry.Key, out int number))
            {
                throw new QpdfException($"Linearized publication lost object {entry.Key}.");
            }

            emitted[number] = ObjectRemap.RemapIds(entry.Value, parts.Renumber, _governor);
        }

        if (emitted.Count != parts.Lookup.Count)
        {
            throw new QpdfException("Linearized publication lost objects while remapping.");
        }

        int? infoNew = null;
        if (infoRef.HasValue)
        {
            if (!parts.Renumber.TryGetValue(infoRef.Value, out int mapped))
            {
                throw new QpdfException($"Linearized publication reached unmapped info object {infoRef.Value}.");
            }

            infoNew = mapped;
        }

        int field1 = PdfSerializer.BytesNeeded(_limits.MaxOutputBytes);
        int byNumber = PdfSerializer.BytesNeeded(parts.MaxNumber);
        if (byNumber > field1)
        {
            field1 = byNumber;
        }

        if (field1 < 1)
        {
            field1 = 1;
        }

        int field2 = PdfSerializer.BytesNeeded(parts.MaxMemberIndex);
        if (field2 < 1)
        {
            field2 = 1;
        }

        CountingSink counter = new();
        IByteSink countingSink = counter;
        if (pass1Sink is not null)
        {
            countingSink = new TeeSink(counter, pass1Sink);
        }

        LinearLayout counted = await EmitPassStreamedAsync(
            version, parts, emitted, infoNew, id1, id2, newlineBeforeEndstream, compressStreams, compressionLevel, field1, field2, false, null, null, countingSink, cancellationToken).ConfigureAwait(false);
        byte[] hintObject = BuildHintObject(parts, counted, compressStreams, compressionLevel, cancellationToken);
        long hintLength = hintObject.LongLength;

        LinearLayout finalLayout = await EmitPassStreamedAsync(
            version, parts, emitted, infoNew, id1, id2, newlineBeforeEndstream, compressStreams, compressionLevel, field1, field2, true, hintObject, counted, sink, cancellationToken).ConfigureAwait(false);
        long written = finalLayout.FileSize;
        if (written != checked(counted.FileSize + hintLength))
        {
            throw new QpdfException("Linearized replay diverged between counting and emitting passes.");
        }

        return written;
    }

    private async ValueTask<LinearLayout> EmitPassStreamedAsync(
        PdfVersion version,
        StreamedParts parts,
        Dictionary<int, QpdfObject> emitted,
        int? infoNew,
        byte[] id1,
        byte[] id2,
        bool newlineBeforeEndstream,
        bool compressStreams,
        int? compressionLevel,
        int field1,
        int field2,
        bool final,
        byte[]? hintObject,
        LinearLayout? counted,
        IByteSink sink,
        CancellationToken cancellationToken)
    {
        LinearLayout layout = new();
        string idHex1 = ToHex(id1);
        string idHex2 = ToHex(id2);
        _governor.ChargeWork(idHex1.Length + idHex2.Length);
        int catalogNew = parts.Renumber[1];
        Dictionary<int, (int Container, int Index)> memberEntries = new(parts.Members.Count);
        foreach (KeyValuePair<int, (int Container, int Index)> entry in parts.Members)
        {
            memberEntries[parts.Renumber[entry.Key]] = entry.Value;
        }

        await AppendAsciiAsync($"%PDF-{version}\n", sink, cancellationToken).ConfigureAwait(false);

        string lindictInner = LindictInner(parts.Renumber, parts.Pages, parts.LindictNumber, counted, hintObject, final);
        layout.Starts[parts.LindictNumber] = sink.Length;
        await AppendAsciiAsync($"{parts.LindictNumber} 0 obj\n<<", sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync(lindictInner, sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync(">>\nendobj\n", sink, cancellationToken).ConfigureAwait(false);
        layout.Lengths[parts.LindictNumber] = checked(sink.Length - layout.Starts[parts.LindictNumber]);

        long? firstPrev = final && counted is not null ? checked(counted.MainXrefTable + HintLength(hintObject)) : 0L;
        await AppendXrefStreamAsync(parts, memberEntries, counted, hintObject, final, parts.XrefFirstNumber, parts.LindictNumber, parts.FirstSize - parts.LindictNumber, parts.FirstSize, catalogNew, infoNew, firstPrev, idHex1, idHex2, field1, field2, layout, sink, cancellationToken).ConfigureAwait(false);

        await EmitUnitsAsync(parts, parts.Part4, emitted, newlineBeforeEndstream, compressStreams, compressionLevel, layout, sink, cancellationToken).ConfigureAwait(false);
        if (hintObject is not null)
        {
            layout.HintPosition = sink.Length;
            layout.Starts[parts.HintNumber] = sink.Length;
            await AppendBytesAsync(hintObject, sink, cancellationToken).ConfigureAwait(false);
            layout.Lengths[parts.HintNumber] = checked(sink.Length - layout.Starts[parts.HintNumber]);
        }
        else
        {
            layout.HintPosition = sink.Length;
            layout.Starts[parts.HintNumber] = sink.Length;
            layout.Lengths[parts.HintNumber] = 0L;
        }

        await EmitUnitsAsync(parts, parts.Part6, emitted, newlineBeforeEndstream, compressStreams, compressionLevel, layout, sink, cancellationToken).ConfigureAwait(false);
        layout.Part6End = sink.Length;
        foreach (IReadOnlyList<int> run in parts.Part7)
        {
            await EmitUnitsAsync(parts, run, emitted, newlineBeforeEndstream, compressStreams, compressionLevel, layout, sink, cancellationToken).ConfigureAwait(false);
        }

        await EmitUnitsAsync(parts, parts.Part8, emitted, newlineBeforeEndstream, compressStreams, compressionLevel, layout, sink, cancellationToken).ConfigureAwait(false);
        await EmitUnitsAsync(parts, parts.Part9, emitted, newlineBeforeEndstream, compressStreams, compressionLevel, layout, sink, cancellationToken).ConfigureAwait(false);

        await AppendXrefStreamAsync(parts, memberEntries, counted, hintObject, final, parts.XrefMainNumber, 0, parts.MainSize, parts.MainSize, null, null, null, idHex1, idHex2, field1, field2, layout, sink, cancellationToken).ConfigureAwait(false);
        layout.MainXrefTable = layout.Starts[parts.XrefMainNumber];
        layout.MainXref = checked(layout.Starts[parts.XrefMainNumber] - 1L);

        await AppendAsciiAsync($"startxref\n{layout.Starts[parts.XrefFirstNumber]}\n%%EOF\n", sink, cancellationToken).ConfigureAwait(false);
        layout.FileSize = sink.Length;
        return layout;
    }

    private async ValueTask EmitUnitsAsync(
        StreamedParts parts,
        IReadOnlyList<int> olds,
        Dictionary<int, QpdfObject> emitted,
        bool newlineBeforeEndstream,
        bool compressStreams,
        int? compressionLevel,
        LinearLayout layout,
        IByteSink sink,
        CancellationToken cancellationToken)
    {
        foreach (int old in olds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            if (!parts.Renumber.TryGetValue(old, out int number))
            {
                throw new QpdfException($"Linearized publication lost object {old}.");
            }

            layout.Starts[number] = sink.Length;
            if (parts.Containers.Contains(old))
            {
                await AppendContainerAsync(parts, old, number, emitted, newlineBeforeEndstream, compressStreams, compressionLevel, sink, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!emitted.TryGetValue(number, out QpdfObject? value) || value is null)
                {
                    throw new QpdfException($"Linearized publication lost object {number}.");
                }

                await _serializer.AppendNumberedObjectAsync(number, value, sink, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
            }

            layout.Lengths[number] = checked(sink.Length - layout.Starts[number]);
        }
    }

    private async ValueTask AppendContainerAsync(
        StreamedParts parts,
        int containerOld,
        int containerNew,
        Dictionary<int, QpdfObject> emitted,
        bool newlineBeforeEndstream,
        bool compressStreams,
        int? compressionLevel,
        IByteSink sink,
        CancellationToken cancellationToken)
    {
        if (!parts.ContainerMembers.TryGetValue(containerOld, out IReadOnlyList<int>? memberOlds) || memberOlds is null)
        {
            throw new QpdfException($"Linearized publication lost an object-stream container.");
        }

        List<byte[]> members = new(memberOlds.Count);
        foreach (int memberOld in memberOlds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!parts.Renumber.TryGetValue(memberOld, out int memberNew)
                || !emitted.TryGetValue(memberNew, out QpdfObject? value)
                || value is null)
            {
                throw new QpdfException($"Linearized publication lost object-stream member {memberOld}.");
            }

            CaptureSink capture = new(_limits, _governor);
            await _serializer.AppendValueAsync(value, capture, 0, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
            members.Add(capture.ToArray());
        }

        System.Text.StringBuilder header = new();
        long running = 0L;
        for (int m = 0; m < memberOlds.Count; m++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (m > 0)
            {
                header.Append((char)32);
            }

            header.Append(parts.Renumber[memberOlds[m]].ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        List<byte> payload = new(checked((int)System.Math.Min(running, int.MaxValue)));
        payload.AddRange(headerBytes);
        int first = headerBytes.Length;
        foreach (byte[] member in members)
        {
            payload.AddRange(member);
        }

        byte[] payloadBytes = [.. payload];
        byte[] storedPayload = payloadBytes;
        string containerFilter = string.Empty;
        if (compressStreams)
        {
            storedPayload = FlateFilter.Encode(payloadBytes, compressionLevel, _limits, _governor);
            containerFilter = " /Filter /FlateDecode";
        }

        await AppendAsciiAsync($"{containerNew} 0 obj\n<< /Type /ObjStm /Length {storedPayload.Length}" + containerFilter + $" /N {memberOlds.Count} /First {first} >>\nstream\n", sink, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask AppendXrefStreamAsync(
        StreamedParts parts,
        Dictionary<int, (int Container, int Index)> memberEntries,
        LinearLayout? counted,
        byte[]? hintObject,
        bool final,
        int number,
        int first,
        int count,
        int size,
        int? rootNew,
        int? infoNew,
        long? prevValue,
        string idHex1,
        string idHex2,
        int field1,
        int field2,
        LinearLayout layout,
        IByteSink sink,
        CancellationToken cancellationToken)
    {
        if (memberEntries is null)
        {
            throw new ArgumentNullException(nameof(memberEntries));
        }

        List<byte> data = new(checked(count * (1 + field1 + field2)));
        long capacity = field1 >= 8 ? long.MaxValue : 1L << (8 * field1);
        for (int n = first; n < first + count; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            if (n == 0)
            {
                data.Add(0);
                PdfSerializer.AppendUInt(data, 0L, field1);
                PdfSerializer.AppendUInt(data, 0L, field2);
            }
            else if (memberEntries.TryGetValue(n, out (int Container, int Index) packed))
            {
                if (packed.Container < 0 || (field1 < 8 && packed.Container >= (1L << (8 * field1))))
                {
                    throw new QpdfException("Linearized cross-reference exceeds its reserved space.");
                }

                data.Add(2);
                PdfSerializer.AppendUInt(data, packed.Container, field1);
                PdfSerializer.AppendUInt(data, packed.Index, field2);
            }
            else
            {
                long offset = XrefOffset(parts.HintNumber, counted, hintObject, final, n);
                if (offset < 0L || offset >= capacity)
                {
                    throw new QpdfException("Linearized cross-reference exceeds its reserved space.");
                }

                data.Add(1);
                PdfSerializer.AppendUInt(data, offset, field1);
                PdfSerializer.AppendUInt(data, 0L, field2);
            }
        }

        byte[] raw = [.. data];
        _governor.ReserveMemory(raw.LongLength);
        System.Text.StringBuilder head = new();
        head.Append(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        head.Append(" 0 obj\n<< /Type /XRef /Length ");
        head.Append(raw.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (first != 0 || first + count != size)
        {
            head.Append(" /Index [ ");
            head.Append(first.ToString(System.Globalization.CultureInfo.InvariantCulture));
            head.Append((char)32);
            head.Append(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            head.Append(" ]");
        }

        if (rootNew.HasValue)
        {
            head.Append(" /Root ");
            head.Append(rootNew.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            head.Append(" 0 R");
        }

        if (infoNew.HasValue)
        {
            head.Append(" /Info ");
            head.Append(infoNew.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            head.Append(" 0 R");
        }

        head.Append(" /Size ");
        head.Append(size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (prevValue.HasValue)
        {
            string prevDigits = prevValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (prevDigits.Length > PrevFieldWidth)
            {
                throw new QpdfException("Linearized cross-reference exceeds its reserved space.");
            }

            head.Append(" /Prev ");
            head.Append(prevDigits);
            head.Append(new string((char)32, PrevFieldWidth - prevDigits.Length));
        }

        head.Append(" /ID [<");
        head.Append(idHex1);
        head.Append("><");
        head.Append(idHex2);
        head.Append(">] /W [1 ");
        head.Append(field1.ToString(System.Globalization.CultureInfo.InvariantCulture));
        head.Append((char)32);
        head.Append(field2.ToString(System.Globalization.CultureInfo.InvariantCulture));
        head.Append("] >>\nstream\n");
        byte[] prefix = System.Text.Encoding.ASCII.GetBytes(head.ToString());
        layout.Starts[number] = sink.Length;
        await AppendBytesAsync(prefix, sink, cancellationToken).ConfigureAwait(false);
        await AppendBytesAsync(raw, sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("\nendstream\nendobj\n", sink, cancellationToken).ConfigureAwait(false);
        layout.Lengths[number] = checked(sink.Length - layout.Starts[number]);
    }

    private sealed class CaptureSink : IByteSink
    {
        private readonly List<byte> _bytes = new();
        private readonly QpdfLimits _limits;
        private readonly ResourceGovernor _governor;

        internal CaptureSink(QpdfLimits limits, ResourceGovernor governor)
        {
            _limits = limits ?? throw new ArgumentNullException(nameof(limits));
            _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        }

        public long Length => _bytes.Count;

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

        internal byte[] ToArray()
        {
            return [.. _bytes];
        }
    }
}
