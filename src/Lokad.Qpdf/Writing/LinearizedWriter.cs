using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using System.Runtime.InteropServices;

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

        using ParserMemoryScope classificationMemory = ParserMemoryScope.Create(_governor);
        using LinearizedParts parts = LinearizedParts.Classify(model, classificationMemory, _governor, cancellationToken);
        using RemappedModel emitted = RemappedModel.Create(parts.Lookup, parts.Renumber, _governor, cancellationToken);
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

        using LinearizedLayout counted = await EmitPassAsync(
            version, parts, emitted, infoNew, id1, id2, newlineBeforeEndstream, false, null, null, countingSink, cancellationToken).ConfigureAwait(false);
        using HintPayload hintObject = BuildHintObject(parts, counted, compressHints, compressionLevel, cancellationToken);
        long hintLength = hintObject.Data.Length;
        if (sink is IOutputCapacityHint staging)
        { staging.EnsureCapacity(checked(counted.FileSize + hintLength), cancellationToken); }

        using LinearizedLayout finalLayout = await EmitPassAsync(
            version, parts, emitted, infoNew, id1, id2, newlineBeforeEndstream, true, hintObject, counted, sink, cancellationToken).ConfigureAwait(false);
        long written = finalLayout.FileSize;
        if (written != checked(counted.FileSize + hintLength))
        {
            throw new QpdfException("Linearized replay diverged between counting and emitting passes.");
        }

        return written;
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

    private async ValueTask<LinearizedLayout> EmitPassAsync(
        PdfVersion version,
        LinearizedParts parts,
        RemappedModel emitted,
        int? infoNew,
        byte[] id1,
        byte[] id2,
        bool newlineBeforeEndstream,
        bool final,
        HintPayload? hintObject,
        LinearizedLayout? counted,
        IByteSink sink,
        CancellationToken cancellationToken)
    {
        LinearizedLayout layout = LinearizedLayout.Create(_governor, false, cancellationToken);
        try
        {

            int catalogNew = parts.Renumber[1];

            await AppendFormattedAsync([WriterPart.Text("%PDF-"), WriterPart.Number(version.Major), WriterPart.Text("."), WriterPart.Number(version.Minor), WriterPart.Text("\n")], sink, cancellationToken).ConfigureAwait(false);

            using (WriterFormatting lindictInner = LindictInner(parts.Renumber, parts.Pages, counted, hintObject, final, cancellationToken))
            {
                layout.RecordStart(parts.LindictNumber, sink.Length);
                await AppendFormattedAsync([WriterPart.Number(parts.LindictNumber), WriterPart.Text(" 0 obj\n<<")], sink, cancellationToken).ConfigureAwait(false);
                await AppendBytesAsync(lindictInner.Data, sink, cancellationToken).ConfigureAwait(false);
            }
            await AppendAsciiAsync(">>\nendobj\n", sink, cancellationToken).ConfigureAwait(false);
            layout.RecordLength(parts.LindictNumber, checked(sink.Length - layout.Starts[parts.LindictNumber]));

            layout.FirstXref = sink.Length;
            using WriterOrder<int> firstHalf = FirstHalfNumbers(parts, cancellationToken);
            await AppendFormattedAsync([WriterPart.Text("xref\n"), WriterPart.Number(firstHalf[0]), WriterPart.Text(" "), WriterPart.Number(firstHalf.Count), WriterPart.Text("\n")], sink, cancellationToken).ConfigureAwait(false);
            foreach (int number in firstHalf)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long offset = XrefOffset(parts.HintNumber, counted, hintObject, final, number);
                if (offset > MaxOffsetField)
                {
                    throw new QpdfException($"Object offset {offset} exceeds the classic xref field width.");
                }
                await AppendFormattedAsync([WriterPart.ZeroPaddedNumber(offset, 10), WriterPart.Text(" 00000 n \n")], sink, cancellationToken).ConfigureAwait(false);
            }

            await AppendFormattedAsync([WriterPart.Text("trailer << /Root "), WriterPart.Number(catalogNew), WriterPart.Text(" 0 R")], sink, cancellationToken).ConfigureAwait(false);
            await AppendFormattedAsync([WriterPart.Text(" /Size "), WriterPart.Number(firstHalf[^1] + 1)], sink, cancellationToken).ConfigureAwait(false);
            firstHalf.Dispose();
            long prevValue = final && counted is not null ? checked(counted.MainXrefTable + HintLength(hintObject)) : 0L;
            await AppendFormattedAsync([WriterPart.Text(" /Prev "), WriterPart.RightPaddedNumber(prevValue, PrevFieldWidth)], sink, cancellationToken).ConfigureAwait(false);
            if (infoNew.HasValue)
            {
                await AppendFormattedAsync([WriterPart.Text(" /Info "), WriterPart.Number(infoNew.Value), WriterPart.Text(" 0 R")], sink, cancellationToken).ConfigureAwait(false);
            }

            await AppendFormattedAsync([WriterPart.Text(" /ID [<"), WriterPart.Hex(id1), WriterPart.Text("><"), WriterPart.Hex(id2), WriterPart.Text(">] >>\n")], sink, cancellationToken).ConfigureAwait(false);
            await AppendAsciiAsync("startxref\n0\n%%EOF\n", sink, cancellationToken).ConfigureAwait(false);

            await EmitObjectsAsync(parts, parts.Part4, emitted, newlineBeforeEndstream, layout, sink, cancellationToken).ConfigureAwait(false);
            if (hintObject is not null)
            {
                layout.HintPosition = sink.Length;
                layout.RecordStart(parts.HintNumber, sink.Length);
                await AppendBytesAsync(hintObject.Data, sink, cancellationToken).ConfigureAwait(false);
                layout.RecordLength(parts.HintNumber, checked(sink.Length - layout.Starts[parts.HintNumber]));
            }
            else
            {
                layout.HintPosition = sink.Length;
                layout.RecordStart(parts.HintNumber, sink.Length);
                layout.RecordLength(parts.HintNumber, 0L);
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
            using WriterOrder<int> secondHalf = SecondHalfNumbers(parts, cancellationToken);
            await AppendFormattedAsync([WriterPart.Text("xref\n0 "), WriterPart.Number(secondHalf.Count + 1)], sink, cancellationToken).ConfigureAwait(false);
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

                await AppendFormattedAsync([WriterPart.ZeroPaddedNumber(position, 10), WriterPart.Text(" 00000 n \n")], sink, cancellationToken).ConfigureAwait(false);
            }

            await AppendFormattedAsync([WriterPart.Text("trailer << /Size "), WriterPart.Number(secondHalf[^1] + 1), WriterPart.Text(" /ID [<"), WriterPart.Hex(id1), WriterPart.Text("><"), WriterPart.Hex(id2), WriterPart.Text(">] >>\n")], sink, cancellationToken).ConfigureAwait(false);
            secondHalf.Dispose();
            await AppendFormattedAsync([WriterPart.Text("startxref\n"), WriterPart.Number(layout.FirstXref), WriterPart.Text("\n%%EOF\n")], sink, cancellationToken).ConfigureAwait(false);
            layout.FileSize = sink.Length;
            return layout;
        }
        catch { layout.Dispose(); throw; }
    }

    private static long XrefOffset(int hintNumber, LinearizedLayout? counted, HintPayload? hintObject, bool final, int number)
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

    private WriterOrder<int> FirstHalfNumbers(LinearizedParts parts, CancellationToken cancellationToken)
    {
        WriterOrder<int> numbers = WriterOrder<int>.Create(checked(2 + parts.Part4.Count + parts.Part6.Count), sizeof(int), _governor, cancellationToken);
        try
        {
            numbers.Add(parts.LindictNumber);
            foreach (int old in parts.Part4) { numbers.Add(parts.Renumber[old]); }
            numbers.Add(parts.HintNumber);
            foreach (int old in parts.Part6) { numbers.Add(parts.Renumber[old]); }
            numbers.Sort(new WriterNumberOrder());
            return numbers;
        }
        catch {numbers.Dispose(); throw;}
    }

    private WriterOrder<int> SecondHalfNumbers(LinearizedParts parts, CancellationToken cancellationToken)
    {
        int capacity = checked(parts.Part8.Count + parts.Part9.Count);
        foreach (IReadOnlyList<int> run in parts.Part7)
        {
            cancellationToken.ThrowIfCancellationRequested(); _governor.ChargeWork(1L);
            capacity = checked(capacity + run.Count);
        }
        WriterOrder<int> numbers = WriterOrder<int>.Create(capacity, sizeof(int), _governor, cancellationToken);
        try
        {
            foreach (IReadOnlyList<int> run in parts.Part7)
            {
                cancellationToken.ThrowIfCancellationRequested(); _governor.ChargeWork(1L);
                foreach (int old in run) { numbers.Add(parts.Renumber[old]); }
            }
            foreach (int old in parts.Part8) { numbers.Add(parts.Renumber[old]); }
            foreach (int old in parts.Part9) { numbers.Add(parts.Renumber[old]); }
            numbers.Sort(new WriterNumberOrder());
            return numbers;
        }
        catch {numbers.Dispose(); throw;}
    }

    private WriterFormatting LindictInner(GovernedIntDictionary<int> renumber, IReadOnlyList<int> pages,
        LinearizedLayout? counted, HintPayload? hintObject, bool final, CancellationToken cancellationToken)
    {
        if (!final || counted is null || hintObject is null)
        {
            return WriterFormatting.Create([WriterPart.Spaces(LindictPad)], _limits, _governor, cancellationToken);
        }
        long hintLength = hintObject.Data.Length;
        long fileSize = checked(counted.FileSize + hintLength);
        long part6End = checked(counted.Part6End + hintLength);
        long mainXref = checked(counted.MainXref + hintLength);
        return WriterFormatting.CreatePadded([WriterPart.Text(" /Linearized 1 /L "), WriterPart.Number(fileSize),
            WriterPart.Text(" /H [ "), WriterPart.Number(counted.HintPosition), WriterPart.Text(" "), WriterPart.Number(hintLength),
            WriterPart.Text(" ] /O "), WriterPart.Number(renumber[pages[0]]), WriterPart.Text(" /E "), WriterPart.Number(part6End),
            WriterPart.Text(" /N "), WriterPart.Number(pages.Count), WriterPart.Text(" /T "), WriterPart.Number(mainXref)],
            LindictPad, "Linearization dictionary exceeds its reserved space.", _limits, _governor, cancellationToken);
    }

    private async ValueTask EmitObjectsAsync(
        LinearizedParts parts,
        IReadOnlyList<int> olds,
        RemappedModel emitted,
        bool newlineBeforeEndstream,
        LinearizedLayout layout,
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

            layout.RecordStart(number, sink.Length);
            await _serializer.AppendNumberedObjectAsync(number, value, sink, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
            layout.RecordLength(number, checked(sink.Length - layout.Starts[number]));
        }
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
        if (sink is BatchingSink batch && batch.TryAppendAscii(text, _limits, cancellationToken)) { return; }
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
            int take = System.Math.Min(8192, chunk.Length - offset);
            await sink.AppendAsync(chunk.Slice(offset, take), cancellationToken).ConfigureAwait(false);
            offset += take;
        }
    }


    private static long HintLength(HintPayload? hintObject)
    {
        return hintObject is null ? 0L : hintObject.Data.Length;
    }

    private HintPayload BuildHintObject(
        LinearizedParts parts,
        LinearizedLayout counted,
        bool compressHints,
        int? compressionLevel,
        CancellationToken cancellationToken)
    {
        return BuildHintObjectCore(parts.Part6, parts.Part8, parts.Part7, parts.Pages, parts.PageUsers, parts.Outlines, parts.Renumber, parts.HintNumber, true, counted, compressHints, compressionLevel, cancellationToken);
    }

    private HintPayload BuildHintObject(
        StreamedParts parts,
        LinearizedLayout counted,
        bool compressHints,
        int? compressionLevel,
        CancellationToken cancellationToken)
    {
        return BuildHintObjectCore(parts.Part6, parts.Part8, parts.Part7, parts.Pages, parts.PageUsers, parts.Outlines, parts.Renumber, parts.HintNumber, parts.OutlinesDirect, counted, compressHints, compressionLevel, cancellationToken);
    }

    private HintPayload BuildHintObjectCore(
        IReadOnlyList<int> part6,
        IReadOnlyList<int> part8,
        IReadOnlyList<IReadOnlyList<int>> part7,
        IReadOnlyList<int> pages,
        IReadOnlyList<IReadOnlyCollection<int>> pageUsers,
        IReadOnlyList<int> outlines,
        GovernedIntDictionary<int> renumber,
        int hintNumber,
        bool outlinesDirect,
        LinearizedLayout counted,
        bool compressHints,
        int? compressionLevel,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope scratch = ParserMemoryScope.Create(_governor);
        List<int> groups = ParserContainers.CreateList<int>(scratch);
        foreach (int old in part6)
        {
            ParserContainers.AddItem(scratch, groups, renumber[old], sizeof(int));
        }

        foreach (int old in part8)
        {
            ParserContainers.AddItem(scratch, groups, renumber[old], sizeof(int));
        }

        GovernedIntDictionary<int> groupIndex = GovernedIntDictionary<int>.Create(scratch, _governor, sizeof(int), cancellationToken);
        for (int i = 0; i < groups.Count; i++)
        {
            groupIndex.Assign(scratch, groups[i], i);
        }

        List<long> groupLengths = ParserContainers.CreateList<long>(scratch);
        foreach (int number in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ParserContainers.AddItem(scratch, groupLengths, ObjectLength(counted, number), sizeof(long));
        }

        List<int> pageCounts = ParserContainers.CreateList<int>(scratch);
        List<long> pageLengths = ParserContainers.CreateList<long>(scratch);
        List<List<int>> pageShared = ParserContainers.CreateList<List<int>>(scratch);
        ParserContainers.AddItem(scratch, pageCounts, part6.Count, sizeof(int));
        List<int> part6New = ParserContainers.CreateList<int>(scratch);
        foreach (int old in part6)
        {
            ParserContainers.AddItem(scratch, part6New, renumber[old], sizeof(int));
        }

        ParserContainers.AddItem(scratch, pageLengths, RunLength(counted, part6New), sizeof(long));
        ParserContainers.AddItem(scratch, pageShared, ParserContainers.CreateList<int>(scratch), IntPtr.Size);
        for (int i = 1; i < pages.Count; i++)
        {
            List<int> run = ParserContainers.CreateList<int>(scratch);
            foreach (int old in part7[i - 1])
            {
                ParserContainers.AddItem(scratch, run, renumber[old], sizeof(int));
            }
            ParserContainers.AddItem(scratch, pageCounts, run.Count, sizeof(int));
            ParserContainers.AddItem(scratch, pageLengths, RunLength(counted, run), sizeof(long));
            ParserContainers.AddItem(scratch, pageShared, SortedShared(pageUsers, renumber, i, groupIndex, scratch, cancellationToken), IntPtr.Size);
        }

        List<int> outlineNew = ParserContainers.CreateList<int>(scratch);
        foreach (int old in outlines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ParserContainers.AddItem(scratch, outlineNew, renumber[old], sizeof(int));
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

        using HintPayload tables = EncodeHintTablesOwned(part6.Count, pageCounts, pageLengths, pageShared, groupLengths, firstPageOffset, firstSharedObj, firstSharedOffset, groups.Count, outlineFirst, outlineOffset, outlineNew.Count, outlineLength, out long sharedAt, out long outlineAt, cancellationToken);
        byte[] data = tables.Bytes;
        bool flate = compressHints;
        if (flate)
        {
            data = FlateFilter.Encode(tables.Bytes, compressionLevel, _limits, _governor, scratch, cancellationToken);
        }

        using WriterFormatting head = WriterFormatting.Create([WriterPart.Number(hintNumber), WriterPart.Text(" 0 obj\n<<"),
            WriterPart.Text(flate ? " /Filter /FlateDecode" : ""), WriterPart.Text(" /S "), WriterPart.Number(sharedAt),
            WriterPart.Text(outlineNew.Count > 0 && outlinesDirect ? " /O " : ""),
            outlineNew.Count > 0 && outlinesDirect ? WriterPart.Number(outlineAt) : default,
            WriterPart.Text(" /Length "), WriterPart.Number(data.Length), WriterPart.Text(" >>\nstream\n")],
            _limits, _governor, cancellationToken);
        ReadOnlyMemory<byte> prefix = head.Data;
        ReadOnlySpan<byte> tail = data.Length == 0 || data[data.Length - 1] != 10
            ? "\nendstream\nendobj\n"u8 : "endstream\nendobj\n"u8;
        HintPayload hintObject = HintPayload.Create(checked(prefix.Length + data.Length + tail.Length), _limits, _governor, cancellationToken);
        try
        {
            byte[] target = hintObject.Bytes;
            GovernedBufferStream.CopyBytes(prefix.Span, target, _governor, cancellationToken, CancellationToken.None);
            GovernedBufferStream.CopyBytes(data, target.AsSpan(prefix.Length), _governor, cancellationToken, CancellationToken.None);
            GovernedBufferStream.CopyBytes(tail, target.AsSpan(prefix.Length + data.Length), _governor, cancellationToken, CancellationToken.None);
            return hintObject;
        }
        catch { hintObject.Dispose(); throw; }
    }

    private static long ObjectLength(LinearizedLayout counted, int number)
    {
        if (!counted.Lengths.TryGetValue(number, out long length))
        {
            throw new QpdfException($"Linearized publication lost the length of object {number}.");
        }

        return length;
    }

    private static long RunLength(LinearizedLayout counted, IReadOnlyList<int> run)
    {
        if (run.Count == 0)
        {
            throw new QpdfParseException("Linearized publication reached an empty page run.", null, null);
        }

        long start = counted.Starts[run[0]];
        int last = run[run.Count - 1];
        return checked(counted.Starts[last] + ObjectLength(counted, last) - start);
    }

    private readonly struct HintNumberComparer : IComparer<int>
    {
        public int Compare(int left, int right) => left.CompareTo(right);
    }

    private List<int> SortedShared(IReadOnlyList<IReadOnlyCollection<int>> pageUsers, GovernedIntDictionary<int> renumber,
        int page, GovernedIntDictionary<int> groupIndex, ParserMemoryScope memory, CancellationToken cancellationToken)
    {
        List<int> olds = ParserContainers.CreateList<int>(memory);
        foreach (int old in pageUsers[page])
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            ParserContainers.AddItem(memory, olds, old, sizeof(int));
        }
        GovernedSort.Sort(CollectionsMarshal.AsSpan(olds), new HintNumberComparer(), _governor, cancellationToken);
        List<int> identifiers = ParserContainers.CreateList<int>(memory);
        foreach (int old in olds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (renumber.TryGetValue(old, out int number) && groupIndex.TryGetValue(number, out int index))
            {
                ParserContainers.AddItem(memory, identifiers, index, sizeof(int));
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
        using HintPayload tables = EncodeHintTablesOwned(part6Count, pageCounts, pageLengths, pageShared, groupLengths,
            firstPageOffset, firstSharedObj, firstSharedOffset, totalShared, outlineFirst, outlineOffset, outlineCount,
            outlineLength, out sharedAt, out outlineAt, cancellationToken);
        return tables.Escape();
    }

    private HintPayload EncodeHintTablesOwned(
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
        using HintBitWriter pages = new(_limits, _governor, cancellationToken);
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

        using HintBitWriter shared = new(_limits, _governor, cancellationToken);
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
        using HintBitWriter outlines = new(_limits, _governor, cancellationToken);
        if (outlineCount > 0)
        {
            outlines.WriteBits(outlineFirst, 32);
            outlines.WriteBits(outlineOffset, 32);
            outlines.WriteBits(outlineCount, 32);
            outlines.WriteBits(outlineLength, 32);
        }
        outlines.Flush();
        outlineAt = checked(pages.Length + shared.Length);
        HintPayload tables = HintPayload.Create(checked((int)(outlineAt + outlines.Length)), _limits, _governor, cancellationToken);
        try
        {
            Span<byte> target = tables.Bytes;
            GovernedBufferStream.CopyBytes(pages.WrittenSpan, target, _governor, cancellationToken, CancellationToken.None);
            GovernedBufferStream.CopyBytes(shared.WrittenSpan, target.Slice((int)pages.Length), _governor, cancellationToken, CancellationToken.None);
            GovernedBufferStream.CopyBytes(outlines.WrittenSpan, target.Slice((int)outlineAt), _governor, cancellationToken, CancellationToken.None);
            return tables;
        }
        catch { tables.Dispose(); throw; }
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

    private sealed class HintBitWriter : IDisposable
    {
        private readonly ResourceGovernor _governor;
        private readonly CancellationToken _token;
        private ParserMemoryScope? _memory;
        private GovernedBufferStream? _bytes;
        private int _current;
        private int _filled;

        internal HintBitWriter(QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
        {
            _governor = governor;
            _token = token;
            ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            try
            {
                // Covers the bit cursor and its governed Stream wrapper; capacity is separate.
                memory.Reserve(192L);
                _bytes = new GovernedBufferStream(governor, limits.MaxOutputBytes, nameof(QpdfLimits.MaxOutputBytes), token, 32);
                _memory = memory;
            }
            catch { memory.Dispose(); throw; }
        }

        private GovernedBufferStream Bytes => _bytes ?? throw new ObjectDisposedException(nameof(HintBitWriter));
        internal long Length => Bytes.Length;
        internal ReadOnlySpan<byte> WrittenSpan => Bytes.WrittenSpan;

        internal void WriteBits(long value, int count)
        {
            _ = Bytes;
            _token.ThrowIfCancellationRequested();
            _governor.CheckCancellation();
            if (count < 0 || count > 32 || value < 0L || value >= (1L << count))
            { throw new QpdfParseException("Linearized hint value exceeds its bit width.", null, null); }
            _governor.ChargeWork(1L);
            for (int i = count - 1; i >= 0; i--)
            {
                _current = checked((_current << 1) | (int)((value >> i) & 1L));
                _filled++;
                if (_filled == 8)
                {
                    Bytes.WriteByte((byte)_current);
                    _current = 0;
                    _filled = 0;
                }
            }
        }

        internal void Flush()
        {
            if (_filled > 0)
            {
                Bytes.WriteByte((byte)(_current << (8 - _filled)));
                _current = 0;
                _filled = 0;
            }
        }

        public void Dispose()
        {
            GovernedBufferStream? bytes = _bytes;
            ParserMemoryScope? memory = _memory;
            _bytes = null;
            _memory = null;
            bytes?.Dispose();
            memory?.Dispose();
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

        using ParserMemoryScope classificationMemory = ParserMemoryScope.Create(_governor);
        using StreamedParts parts = LinearizedParts.ClassifyStreamed(model, objectStreamGroups, classificationMemory, _governor, cancellationToken);
        if (parts.Containers.Count == 0)
        {
            parts.Dispose();
            classificationMemory.Dispose();
            return await WriteLinearizedAsync(version, model, infoRef, id1, id2, newlineBeforeEndstream, compressStreams, compressionLevel, sink, pass1Sink, cancellationToken).ConfigureAwait(false);
        }

        using RemappedModel emitted = RemappedModel.Create(parts.Lookup, parts.Renumber, _governor, cancellationToken);

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

        using LinearizedLayout counted = await EmitPassStreamedAsync(
            version, parts, emitted, infoNew, id1, id2, newlineBeforeEndstream, compressStreams, compressionLevel, field1, field2, false, null, null, countingSink, cancellationToken).ConfigureAwait(false);
        using HintPayload hintObject = BuildHintObject(parts, counted, compressStreams, compressionLevel, cancellationToken);
        long hintLength = hintObject.Data.Length;
        if (sink is IOutputCapacityHint staging)
        { staging.EnsureCapacity(checked(counted.FileSize + hintLength), cancellationToken); }

        using LinearizedLayout finalLayout = await EmitPassStreamedAsync(
            version, parts, emitted, infoNew, id1, id2, newlineBeforeEndstream, compressStreams, compressionLevel, field1, field2, true, hintObject, counted, sink, cancellationToken).ConfigureAwait(false);
        long written = finalLayout.FileSize;
        if (written != checked(counted.FileSize + hintLength))
        {
            throw new QpdfException("Linearized replay diverged between counting and emitting passes.");
        }

        return written;
    }

    private async ValueTask<LinearizedLayout> EmitPassStreamedAsync(
        PdfVersion version,
        StreamedParts parts,
        RemappedModel emitted,
        int? infoNew,
        byte[] id1,
        byte[] id2,
        bool newlineBeforeEndstream,
        bool compressStreams,
        int? compressionLevel,
        int field1,
        int field2,
        bool final,
        HintPayload? hintObject,
        LinearizedLayout? counted,
        IByteSink sink,
        CancellationToken cancellationToken)
    {
        LinearizedLayout layout = LinearizedLayout.Create(_governor, true, cancellationToken);
        try
        {

            int catalogNew = parts.Renumber[1];
            foreach (KeyValuePair<int, (int Container, int Index)> entry in parts.Members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                layout.RecordMember(parts.Renumber[entry.Key], entry.Value);
            }

            await AppendFormattedAsync([WriterPart.Text("%PDF-"), WriterPart.Number(version.Major), WriterPart.Text("."), WriterPart.Number(version.Minor), WriterPart.Text("\n")], sink, cancellationToken).ConfigureAwait(false);

            using (WriterFormatting lindictInner = LindictInner(parts.Renumber, parts.Pages, counted, hintObject, final, cancellationToken))
            {
                layout.RecordStart(parts.LindictNumber, sink.Length);
                await AppendFormattedAsync([WriterPart.Number(parts.LindictNumber), WriterPart.Text(" 0 obj\n<<")], sink, cancellationToken).ConfigureAwait(false);
                await AppendBytesAsync(lindictInner.Data, sink, cancellationToken).ConfigureAwait(false);
            }
            await AppendAsciiAsync(">>\nendobj\n", sink, cancellationToken).ConfigureAwait(false);
            layout.RecordLength(parts.LindictNumber, checked(sink.Length - layout.Starts[parts.LindictNumber]));

            long? firstPrev = final && counted is not null ? checked(counted.MainXrefTable + HintLength(hintObject)) : 0L;
            await AppendXrefStreamAsync(parts, counted, hintObject, final, parts.XrefFirstNumber, parts.LindictNumber, parts.FirstSize - parts.LindictNumber, parts.FirstSize, catalogNew, infoNew, firstPrev, id1, id2, field1, field2, layout, sink, cancellationToken).ConfigureAwait(false);

            await EmitUnitsAsync(parts, parts.Part4, emitted, newlineBeforeEndstream, compressStreams, compressionLevel, layout, sink, cancellationToken).ConfigureAwait(false);
            if (hintObject is not null)
            {
                layout.HintPosition = sink.Length;
                layout.RecordStart(parts.HintNumber, sink.Length);
                await AppendBytesAsync(hintObject.Data, sink, cancellationToken).ConfigureAwait(false);
                layout.RecordLength(parts.HintNumber, checked(sink.Length - layout.Starts[parts.HintNumber]));
            }
            else
            {
                layout.HintPosition = sink.Length;
                layout.RecordStart(parts.HintNumber, sink.Length);
                layout.RecordLength(parts.HintNumber, 0L);
            }

            await EmitUnitsAsync(parts, parts.Part6, emitted, newlineBeforeEndstream, compressStreams, compressionLevel, layout, sink, cancellationToken).ConfigureAwait(false);
            layout.Part6End = sink.Length;
            foreach (IReadOnlyList<int> run in parts.Part7)
            {
                await EmitUnitsAsync(parts, run, emitted, newlineBeforeEndstream, compressStreams, compressionLevel, layout, sink, cancellationToken).ConfigureAwait(false);
            }

            await EmitUnitsAsync(parts, parts.Part8, emitted, newlineBeforeEndstream, compressStreams, compressionLevel, layout, sink, cancellationToken).ConfigureAwait(false);
            await EmitUnitsAsync(parts, parts.Part9, emitted, newlineBeforeEndstream, compressStreams, compressionLevel, layout, sink, cancellationToken).ConfigureAwait(false);

            await AppendXrefStreamAsync(parts, counted, hintObject, final, parts.XrefMainNumber, 0, parts.MainSize, parts.MainSize, null, null, null, id1, id2, field1, field2, layout, sink, cancellationToken).ConfigureAwait(false);
            layout.MainXrefTable = layout.Starts[parts.XrefMainNumber];
            layout.MainXref = checked(layout.Starts[parts.XrefMainNumber] - 1L);

            await AppendFormattedAsync([WriterPart.Text("startxref\n"), WriterPart.Number(layout.Starts[parts.XrefFirstNumber]), WriterPart.Text("\n%%EOF\n")], sink, cancellationToken).ConfigureAwait(false);
            layout.FileSize = sink.Length;
            return layout;
        }
        catch { layout.Dispose(); throw; }
    }

    private async ValueTask EmitUnitsAsync(
        StreamedParts parts,
        IReadOnlyList<int> olds,
        RemappedModel emitted,
        bool newlineBeforeEndstream,
        bool compressStreams,
        int? compressionLevel,
        LinearizedLayout layout,
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

            layout.RecordStart(number, sink.Length);
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

            layout.RecordLength(number, checked(sink.Length - layout.Starts[number]));
        }
    }

    private async ValueTask AppendContainerAsync(
        StreamedParts parts,
        int containerOld,
        int containerNew,
        RemappedModel emitted,
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

        using ObjectStreamCapture members = ObjectStreamCapture.Create(memberOlds.Count, _limits, _governor, cancellationToken);
        foreach (int memberOld in memberOlds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!parts.Renumber.TryGetValue(memberOld, out int memberNew)
                || !emitted.TryGetValue(memberNew, out QpdfObject? value)
                || value is null)
            {
                throw new QpdfException($"Linearized publication lost object-stream member {memberOld}.");
            }

            await members.AppendAsync(_serializer, value, newlineBeforeEndstream, cancellationToken).ConfigureAwait(false);
        }

        using ObjectStreamPayload payload = ObjectStreamPayload.CreateLinearized(memberOlds, parts.Renumber, members,
            compressStreams, compressionLevel, _limits, _governor, cancellationToken);
        using (ObjectStreamHeader header = ObjectStreamHeader.Create(containerNew, memberOlds.Count, payload.Data.Length,
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

    private async ValueTask AppendXrefStreamAsync(
        StreamedParts parts,
        LinearizedLayout? counted,
        HintPayload? hintObject,
        bool final,
        int number,
        int first,
        int count,
        int size,
        int? rootNew,
        int? infoNew,
        long? prevValue,
        byte[] id1,
        byte[] id2,
        int field1,
        int field2,
        LinearizedLayout layout,
        IByteSink sink,
        CancellationToken cancellationToken)
    {
        using ParserMemoryScope dataMemory = ParserMemoryScope.Create(_governor);
        int entryWidth = checked(1 + field1 + field2);
        int dataLength = checked(count * entryWidth);
        if (dataLength > _limits.MaxOutputBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), dataLength, _limits.MaxOutputBytes);
        }
        dataMemory.Reserve(checked(32L + dataLength));
        _governor.ChargeWork(dataLength);
        byte[] raw = new byte[dataLength];
        long capacity = field1 >= 8 ? long.MaxValue : 1L << (8 * field1);
        for (int n = first; n < first + count; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            int offsetInData = checked((n - first) * entryWidth);
            if (n == 0)
            {
                raw[offsetInData] = 0;
                PdfSerializer.WriteUInt(raw.AsSpan(offsetInData + 1, field1), 0L);
                PdfSerializer.WriteUInt(raw.AsSpan(offsetInData + 1 + field1, field2), 0L);
            }
            else if (layout.TryGetMember(n, out (int Container, int Index) packed))
            {
                if (packed.Container < 0 || (field1 < 8 && packed.Container >= (1L << (8 * field1))))
                {
                    throw new QpdfException("Linearized cross-reference exceeds its reserved space.");
                }

                raw[offsetInData] = 2;
                PdfSerializer.WriteUInt(raw.AsSpan(offsetInData + 1, field1), packed.Container);
                PdfSerializer.WriteUInt(raw.AsSpan(offsetInData + 1 + field1, field2), packed.Index);
            }
            else
            {
                long offset = XrefOffset(parts.HintNumber, counted, hintObject, final, n);
                if (offset < 0L || offset >= capacity)
                {
                    throw new QpdfException("Linearized cross-reference exceeds its reserved space.");
                }

                raw[offsetInData] = 1;
                PdfSerializer.WriteUInt(raw.AsSpan(offsetInData + 1, field1), offset);
                PdfSerializer.WriteUInt(raw.AsSpan(offsetInData + 1 + field1, field2), 0L);
            }
            _governor.ChargeWork(entryWidth);
        }
        bool index = first != 0 || first + count != size;
        layout.RecordStart(number, sink.Length);
        await AppendFormattedAsync([WriterPart.Number(number), WriterPart.Text(" 0 obj\n<< /Type /XRef /Length "), WriterPart.Number(raw.Length),
            WriterPart.Text(index ? " /Index [ " : ""), index ? WriterPart.Number(first) : default, WriterPart.Text(index ? " " : ""),
            index ? WriterPart.Number(count) : default, WriterPart.Text(index ? " ]" : ""),
            WriterPart.Text(rootNew.HasValue ? " /Root " : ""), rootNew.HasValue ? WriterPart.Number(rootNew.Value) : default,
            WriterPart.Text(rootNew.HasValue ? " 0 R" : ""), WriterPart.Text(infoNew.HasValue ? " /Info " : ""),
            infoNew.HasValue ? WriterPart.Number(infoNew.Value) : default, WriterPart.Text(infoNew.HasValue ? " 0 R" : ""),
            WriterPart.Text(" /Size "), WriterPart.Number(size), WriterPart.Text(prevValue.HasValue ? " /Prev " : ""),
            prevValue.HasValue ? WriterPart.RightPaddedNumber(prevValue.Value, PrevFieldWidth) : default,
            WriterPart.Text(" /ID [<"), WriterPart.Hex(id1), WriterPart.Text("><"), WriterPart.Hex(id2),
            WriterPart.Text(">] /W [1 "), WriterPart.Number(field1), WriterPart.Text(" "), WriterPart.Number(field2),
            WriterPart.Text("] >>\nstream\n")], sink, cancellationToken).ConfigureAwait(false);
        await AppendBytesAsync(raw, sink, cancellationToken).ConfigureAwait(false);
        await AppendAsciiAsync("\nendstream\nendobj\n", sink, cancellationToken).ConfigureAwait(false);
        layout.RecordLength(number, checked(sink.Length - layout.Starts[number]));
    }

}
