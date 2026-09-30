using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

/// <summary>What an xref entry points at.</summary>
internal enum XrefEntryKind
{
    /// <summary>Deleted or never-allocated slot.</summary>
    Free = 0,

    /// <summary>Direct byte offset with a generation.</summary>
    Uncompressed = 1,

    /// <summary>Index inside an object stream.</summary>
    Compressed = 2,
}

/// <summary>
/// One xref entry. Field meanings follow the kind: free carries the next
/// free number and generation; uncompressed carries offset and generation;
/// compressed carries the object-stream number and index.
/// </summary>
internal readonly struct XrefEntry
{
    private XrefEntry(XrefEntryKind kind, long first, int second)
    {
        Kind = kind;
        First = first;
        Second = second;
    }

    internal XrefEntryKind Kind { get; }

    internal long First { get; }

    internal int Second { get; }

    internal static XrefEntry Free(long next, int generation)
    {
        return new XrefEntry(XrefEntryKind.Free, next, generation);
    }

    internal static XrefEntry Uncompressed(long offset, int generation)
    {
        return new XrefEntry(XrefEntryKind.Uncompressed, offset, generation);
    }

    internal static XrefEntry Compressed(int streamNumber, int index)
    {
        return new XrefEntry(XrefEntryKind.Compressed, streamNumber, index);
    }
}

/// <summary>
/// Strict single-section cross-reference with its trailer, from a classic
/// table or a stream. Parses the tail `startxref` (which must match the
/// actual table position), validates entries before reading, and requires
/// exact `[0, /Size)` coverage. Incremental chains, hybrid structures, and
/// recovery arrive in later milestones and fail explicitly here.
/// </summary>
internal sealed class XRefTable
{
    private const int TailScanBytes = 65536;

    private XRefTable(long xrefOffset, Dictionary<int, XrefEntry> entries, PdfDictionary trailer)
    {
        XrefOffset = xrefOffset;
        Entries = entries;
        Trailer = trailer;
    }

    internal long XrefOffset { get; }

    internal IReadOnlyDictionary<int, XrefEntry> Entries { get; }

    internal PdfDictionary Trailer { get; }

    internal static XRefTable Parse(
        ReadOnlyMemory<byte> data,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics)
    {
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        limits.Validate();
        if (data.Length == 0)
        {
            throw new QpdfParseException("Input is empty.", 0L, null);
        }

        long startxrefPosition = FindStartXRef(data);
        PdfTokenizer tail = new(data);
        tail.SetPosition(startxrefPosition);
        if (!tail.TryReadToken(out PdfToken keyword) || keyword.Kind != PdfTokenKind.StartXRef)
        {
            throw new QpdfParseException("Expected 'startxref'.", startxrefPosition, null);
        }

        if (!tail.TryReadToken(out PdfToken offsetToken) || offsetToken.Kind != PdfTokenKind.Integer)
        {
            throw new QpdfParseException("Expected a startxref offset.", keyword.Offset, null);
        }

        long xrefOffset = ParseOffset(offsetToken);
        ExpectEndOfFile(data, offsetToken);
        if (xrefOffset < 0L || xrefOffset >= data.Length)
        {
            throw new QpdfParseException($"startxref offset {xrefOffset} is outside the input.", offsetToken.Offset, null);
        }

        PdfTokenizer tokenizer = new(data);
        tokenizer.SetPosition(xrefOffset);
        if (!tokenizer.TryReadToken(out PdfToken head))
        {
            throw new QpdfParseException("startxref points at end of input.", xrefOffset, null);
        }

        if (head.Kind == PdfTokenKind.XRef)
        {
            return ParseClassic(data, limits, governor, diagnostics, tokenizer, xrefOffset);
        }

        if (head.Kind == PdfTokenKind.Integer)
        {
            return ParseStream(data, limits, governor, diagnostics, tokenizer, head, xrefOffset);
        }

        throw new QpdfParseException("startxref does not point at an xref table or stream.", xrefOffset, null);
    }

    private static XRefTable ParseClassic(
        ReadOnlyMemory<byte> data,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        PdfTokenizer tokenizer,
        long xrefOffset)
    {
        Dictionary<int, XrefEntry> entries = new();
        while (true)
        {
            if (!tokenizer.TryReadToken(out PdfToken token))
            {
                throw new QpdfParseException("Unterminated xref table.", tokenizer.Position, null);
            }

            if (token.Kind == PdfTokenKind.Trailer)
            {
                break;
            }

            if (token.Kind != PdfTokenKind.Integer)
            {
                throw new QpdfParseException($"Unexpected token {token.Kind} in xref table.", token.Offset, null);
            }

            if (!tokenizer.TryReadToken(out PdfToken countToken) || countToken.Kind != PdfTokenKind.Integer)
            {
                throw new QpdfParseException("Expected an xref subsection count.", token.Offset, null);
            }

            ReadSubsection(data, tokenizer, token, countToken, entries, governor);
        }

        PdfParser parser = new(limits, governor, diagnostics, null);
        QpdfObject trailerObject = parser.ParseValue(tokenizer, 0);
        if (trailerObject is not PdfDictionary trailer)
        {
            throw new QpdfParseException("Trailer must be a dictionary.", tokenizer.Position, null);
        }

        RejectChainedTrailer(trailer);
        int size = ReadSize(trailer);
        ValidateCoverage(data, entries, size);
        return new XRefTable(xrefOffset, entries, trailer);
    }

    private static void RejectChainedTrailer(PdfDictionary trailer)
    {
        PdfName prevKey = PdfName.FromAscii("Prev");
        if (trailer.TryGet(prevKey, out QpdfObject prev) && prev is not PdfNull)
        {
            throw new QpdfParseException("Incremental /Prev chains arrive in work item 10.", null, null);
        }

        PdfName xstmKey = PdfName.FromAscii("XRefStm");
        if (trailer.TryGet(xstmKey, out QpdfObject xstm) && xstm is not PdfNull)
        {
            throw new QpdfParseException("Hybrid /XRefStm structures arrive in work item 10.", null, null);
        }
    }
    private static XRefTable ParseStream(
        ReadOnlyMemory<byte> data,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        PdfTokenizer tokenizer,
        PdfToken numberToken,
        long xrefOffset)
    {
        tokenizer.SetPosition(numberToken.Offset);
        PdfParser parser = new(limits, governor, diagnostics, null);
        (int streamNumber, int streamGeneration, QpdfObject raw) = parser.ParseIndirectObject(tokenizer, 0);
        if (raw is not PdfStream stream)
        {
            throw new QpdfParseException("startxref does not point at an xref stream.", xrefOffset, streamNumber);
        }

        PdfDictionary dictionary = stream.Dictionary;
        PdfName typeKey = PdfName.FromAscii("Type");
        if (!dictionary.TryGet(typeKey, out QpdfObject typeRaw) || typeRaw is not PdfName typeName || typeName.DisplayText != "XRef")
        {
            throw new QpdfParseException("Cross-reference stream must declare /Type /XRef.", xrefOffset, streamNumber);
        }

        PdfName prevKey = PdfName.FromAscii("Prev");
        if (dictionary.TryGet(prevKey, out QpdfObject prev) && prev is not PdfNull)
        {
            throw new QpdfParseException("Chained /Prev xref streams arrive in work item 10.", xrefOffset, streamNumber);
        }

        int size = ReadSize(dictionary);
        int[] widths = ReadWidths(dictionary);
        List<(int First, int Count)> index = ReadIndex(dictionary, size);
        byte[] decoded = DecodeXRefData(stream, governor, limits, xrefOffset, streamNumber);
        Dictionary<int, XrefEntry> entries = ReadStreamEntries(decoded, widths, index, size, data.Length, governor, xrefOffset);
        ValidateStreamCoverage(entries, size);
        return new XRefTable(xrefOffset, entries, dictionary);
    }

    private static int[] ReadWidths(PdfDictionary dictionary)
    {
        PdfName key = PdfName.FromAscii("W");
        if (!dictionary.TryGet(key, out QpdfObject raw) || raw is not PdfArray array || array.Count != 3)
        {
            throw new QpdfParseException("Cross-reference stream must declare exactly three /W widths.", null, null);
        }

        int[] widths = new int[3];
        int total = 0;
        for (int i = 0; i < 3; i++)
        {
            if (array[i] is not PdfInteger integer || integer.Value < 0L || integer.Value > 8L)
            {
                throw new QpdfParseException("Cross-reference stream /W widths must be integers 0..8.", null, null);
            }

            widths[i] = (int)integer.Value;
            total += widths[i];
        }

        if (total < 1)
        {
            throw new QpdfParseException("Cross-reference stream /W entry size is zero.", null, null);
        }

        return widths;
    }

    private static List<(int First, int Count)> ReadIndex(PdfDictionary dictionary, int size)
    {
        PdfName key = PdfName.FromAscii("Index");
        if (!dictionary.TryGet(key, out QpdfObject raw) || raw is PdfNull)
        {
            return new List<(int First, int Count)> { (0, size) };
        }

        if (raw is not PdfArray array || array.Count < 2 || array.Count % 2 != 0)
        {
            throw new QpdfParseException("Cross-reference stream /Index must hold even pairs.", null, null);
        }

        List<(int First, int Count)> index = new(array.Count / 2);
        for (int i = 0; i < array.Count; i += 2)
        {
            if (array[i] is not PdfInteger first || array[i + 1] is not PdfInteger count)
            {
                throw new QpdfParseException("Cross-reference stream /Index entries must be integers.", null, null);
            }

            if (first.Value < 0L || first.Value >= size || count.Value <= 0L || first.Value + count.Value > size)
            {
                throw new QpdfParseException($"Cross-reference stream /Index range [{first.Value}, {first.Value + count.Value}) is outside /Size {size}.", null, null);
            }

            index.Add(((int)first.Value, (int)count.Value));
        }

        return index;
    }

    private static byte[] DecodeXRefData(
        PdfStream stream,
        ResourceGovernor governor,
        QpdfLimits limits,
        long xrefOffset,
        int streamNumber)
    {
        IReadOnlyList<FilterStage> stages;
        try
        {
            stages = FilterChain.Resolve(stream.Dictionary);
        }
        catch (QpdfParseException ex)
        {
            throw new QpdfParseException($"Cross-reference stream declares invalid filters: {ex.Message}", xrefOffset, streamNumber);
        }

        return FilterChain.Decode(stream.Data, stages, limits, governor);
    }

    private static Dictionary<int, XrefEntry> ReadStreamEntries(
        byte[] decoded,
        int[] widths,
        List<(int First, int Count)> index,
        int size,
        int dataLength,
        ResourceGovernor governor,
        long xrefOffset)
    {
        int entrySize = widths[0] + widths[1] + widths[2];
        long total = 0L;
        foreach ((int first, int count) in index)
        {
            checked
            {
                total += count;
            }
        }

        checked
        {
            if (total * entrySize != decoded.Length)
            {
                throw new QpdfParseException($"Cross-reference stream data holds {decoded.Length} bytes for {total} entries of {entrySize}.", xrefOffset, null);
            }
        }

        governor.ReserveMemory(checked(total * 32L));
        Dictionary<int, XrefEntry> entries = new();
        int position = 0;
        foreach ((int first, int count) in index)
        {
            for (int k = 0; k < count; k++)
            {
                int number = first + k;
                long[] fields = new long[3];
                for (int field = 0; field < 3; field++)
                {
                    long value = 0L;
                    for (int b = 0; b < widths[field]; b++)
                    {
                        value = (value << 8) | decoded[position];
                        position += 1;
                    }

                    fields[field] = value;
                }

                if (widths[0] == 0)
                {
                    fields[0] = 1L;
                }

                if (number == 0)
                {
                    continue;
                }

                XrefEntry entry = ReadStreamEntry(fields, number, dataLength, xrefOffset);
                if (entries.ContainsKey(number))
                {
                    throw new QpdfParseException($"Duplicate xref entry for object {number}.", xrefOffset, number);
                }

                governor.ChargeWork(1L);
                entries[number] = entry;
            }
        }

        return entries;
    }

    private static XrefEntry ReadStreamEntry(long[] fields, int number, int dataLength, long xrefOffset)
    {
        if (fields[0] == 0L)
        {
            if (fields[1] < 0L || fields[1] > int.MaxValue || fields[2] < 0L || fields[2] > int.MaxValue)
            {
                throw new QpdfParseException($"Free xref entry for object {number} is out of range.", xrefOffset, number);
            }

            return XrefEntry.Free(fields[1], (int)fields[2]);
        }

        if (fields[0] == 1L)
        {
            if (fields[1] < 0L || fields[1] >= dataLength || fields[2] < 0L || fields[2] > 65535L)
            {
                throw new QpdfParseException($"Xref entry for object {number} points outside the input.", xrefOffset, number);
            }

            return XrefEntry.Uncompressed(fields[1], (int)fields[2]);
        }

        if (fields[0] == 2L)
        {
            if (fields[1] < 0L || fields[1] > int.MaxValue || fields[2] < 0L || fields[2] > int.MaxValue)
            {
                throw new QpdfParseException($"Compressed xref entry for object {number} is out of range.", xrefOffset, number);
            }

            return XrefEntry.Compressed((int)fields[1], (int)fields[2]);
        }

        throw new QpdfParseException($"Unknown xref entry type {fields[0]} for object {number}.", xrefOffset, number);
    }

    private static void ValidateStreamCoverage(Dictionary<int, XrefEntry> entries, int size)
    {
        // Object 0 entries in streams are ignored like the reference reader;
        // object 0 still resolves as free. Coverage therefore starts at 1.
        for (int number = 1; number < size; number++)
        {
            if (!entries.ContainsKey(number))
            {
                throw new QpdfParseException($"Object {number} has no xref entry.", null, number);
            }
        }
    }

    private static long FindStartXRef(ReadOnlyMemory<byte> data)
    {
        int window = Math.Min(data.Length, TailScanBytes);
        int baseOffset = data.Length - window;
        ReadOnlySpan<byte> tail = data.Span.Slice(baseOffset, window);
        ReadOnlySpan<byte> marker = [(byte)'s', (byte)'t', (byte)'a', (byte)'r', (byte)'t', (byte)'x', (byte)'r', (byte)'e', (byte)'f'];
        int found = -1;
        for (int i = 0; i + marker.Length <= tail.Length; i++)
        {
            if (tail.Slice(i, marker.Length).SequenceEqual(marker)
                && (i == 0 || IsDelimiterOrWhitespace(tail[i - 1]))
                && (i + marker.Length >= tail.Length || IsDelimiterOrWhitespace(tail[i + marker.Length])))
            {
                found = i;
            }
        }

        if (found < 0)
        {
            throw new QpdfParseException("No startxref marker in the file tail.", data.Length, null);
        }

        return baseOffset + found;
    }

    private static bool IsDelimiterOrWhitespace(byte b)
    {
        return b == 0x00 || b == 0x09 || b == 0x0A || b == 0x0C || b == 0x0D || b == 0x20
            || b == (byte)'(' || b == (byte)')' || b == (byte)'<' || b == (byte)'>'
            || b == (byte)'[' || b == (byte)']' || b == (byte)'/' || b == (byte)'%';
    }

    private static long ParseOffset(PdfToken token)
    {
        ReadOnlySpan<byte> span = token.Data.Span;
        long value = 0L;
        try
        {
            checked
            {
                for (int i = 0; i < span.Length; i++)
                {
                    if (span[i] < (byte)'0' || span[i] > (byte)'9')
                    {
                        throw new QpdfParseException("startxref offset must be a non-negative integer.", token.Offset, null);
                    }

                    value = (value * 10L) + (span[i] - (byte)'0');
                }
            }
        }
        catch (OverflowException ex)
        {
            throw new QpdfParseException("startxref offset overflows 64 bits.", token.Offset, null, ex);
        }

        return value;
    }

    private static void ExpectEndOfFile(ReadOnlyMemory<byte> data, PdfToken offsetToken)
    {
        int cursor = (int)(offsetToken.Offset + offsetToken.Data.Length);
        ReadOnlySpan<byte> span = data.Span;
        while (cursor < span.Length && IsDelimiterOrWhitespace(span[cursor]) && span[cursor] != (byte)'%')
        {
            cursor += 1;
        }

        ReadOnlySpan<byte> marker = [(byte)'%', (byte)'%', (byte)'E', (byte)'O', (byte)'F'];
        if (cursor + marker.Length > span.Length || !span.Slice(cursor, marker.Length).SequenceEqual(marker))
        {
            throw new QpdfParseException("Expected '%%EOF' after startxref.", offsetToken.Offset, null);
        }
    }

    private static void ReadSubsection(
        ReadOnlyMemory<byte> data,
        PdfTokenizer tokenizer,
        PdfToken firstToken,
        PdfToken countToken,
        Dictionary<int, XrefEntry> entries,
        ResourceGovernor governor)
    {
        long first = ParseSubsectionNumber(firstToken);
        long count = ParseSubsectionNumber(countToken);
        if (count <= 0L)
        {
            throw new QpdfParseException($"xref subsection count {count} must be positive.", firstToken.Offset, null);
        }

        checked
        {
            if (first < 0L || first + count > int.MaxValue)
            {
                throw new QpdfParseException($"xref subsection [{first}, {first + count}) is out of range.", firstToken.Offset, null);
            }
        }

        governor.ReserveMemory(checked(count * 32L));
        long cursor = SkipSubsectionBreak(data, tokenizer, firstToken);
        ReadOnlySpan<byte> span = data.Span;
        for (long number = first; number < first + count; number++)
        {
            XrefEntry entry = ReadEntry(span, ref cursor, firstToken.Offset, (int)number);
            if (entries.ContainsKey((int)number))
            {
                throw new QpdfParseException($"Duplicate xref entry for object {number}.", firstToken.Offset, (int)number);
            }

            governor.ChargeWork(1L);
            entries[(int)number] = entry;
        }

        tokenizer.SetPosition(cursor);
    }

    private static long SkipSubsectionBreak(ReadOnlyMemory<byte> data, PdfTokenizer tokenizer, PdfToken firstToken)
    {
        long cursor = tokenizer.Position;
        ReadOnlySpan<byte> span = data.Span;
        while (cursor < span.Length && span[(int)cursor] == (byte)' ')
        {
            cursor += 1;
        }

        if (cursor < span.Length && span[(int)cursor] == (byte)'\r')
        {
            cursor += 1;
        }

        if (cursor >= span.Length || span[(int)cursor] != (byte)'\n')
        {
            throw new QpdfParseException("xref subsection header must end with a line break.", firstToken.Offset, null);
        }

        return cursor + 1;
    }

    private static long ParseSubsectionNumber(PdfToken token)
    {
        ReadOnlySpan<byte> span = token.Data.Span;
        long value = 0L;
        try
        {
            checked
            {
                for (int i = 0; i < span.Length; i++)
                {
                    if (span[i] < (byte)'0' || span[i] > (byte)'9')
                    {
                        throw new QpdfParseException("xref subsection bounds must be non-negative integers.", token.Offset, null);
                    }

                    value = (value * 10L) + (span[i] - (byte)'0');
                }
            }
        }
        catch (OverflowException ex)
        {
            throw new QpdfParseException("xref subsection bound overflows 64 bits.", token.Offset, null, ex);
        }

        return value;
    }

    private static XrefEntry ReadEntry(ReadOnlySpan<byte> span, ref long cursor, long offset, int number)
    {
        if (cursor + 18 > span.Length)
        {
            throw new QpdfParseException($"xref entry for object {number} is truncated.", offset, number);
        }

        long entryOffset = cursor;
        for (int i = 0; i < 10; i++)
        {
            if (span[(int)cursor + i] < (byte)'0' || span[(int)cursor + i] > (byte)'9')
            {
                throw new QpdfParseException($"xref entry for object {number} has a malformed offset.", offset, number);
            }
        }

        if (span[(int)cursor + 10] != (byte)' ')
        {
            throw new QpdfParseException($"xref entry for object {number} is malformed.", offset, number);
        }

        for (int i = 11; i < 16; i++)
        {
            if (span[(int)cursor + i] < (byte)'0' || span[(int)cursor + i] > (byte)'9')
            {
                throw new QpdfParseException($"xref entry for object {number} has a malformed generation.", offset, number);
            }
        }

        if (span[(int)cursor + 16] != (byte)' ')
        {
            throw new QpdfParseException($"xref entry for object {number} is malformed.", offset, number);
        }

        byte kind = span[(int)cursor + 17];
        if (kind != (byte)'n' && kind != (byte)'f')
        {
            throw new QpdfParseException($"xref entry for object {number} must be 'n' or 'f'.", offset, number);
        }

        cursor += 18;
        if (cursor < span.Length && span[(int)cursor] == (byte)' ')
        {
            cursor += 1;
        }

        if (cursor < span.Length && span[(int)cursor] == (byte)'\r')
        {
            cursor += 1;
        }

        if (cursor >= span.Length || span[(int)cursor] != (byte)'\n')
        {
            throw new QpdfParseException($"xref entry for object {number} must end with a line break.", offset, number);
        }

        cursor += 1;
        long target = 0L;
        for (int i = 0; i < 10; i++)
        {
            target = (target * 10L) + (span[(int)entryOffset + i] - (byte)'0');
        }

        int generation = 0;
        for (int i = 11; i < 16; i++)
        {
            generation = (generation * 10) + (span[(int)entryOffset + i] - (byte)'0');
        }

        return kind == (byte)'n' ? XrefEntry.Uncompressed(target, generation) : XrefEntry.Free(target, generation);
    }

    private static int ReadSize(PdfDictionary trailer)
    {
        PdfName sizeKey = new([(byte)'S', (byte)'i', (byte)'z', (byte)'e']);
        if (!trailer.TryGet(sizeKey, out QpdfObject raw) || raw is not PdfInteger size)
        {
            throw new QpdfParseException("Trailer must declare an integer /Size.", null, null);
        }

        if (size.Value < 1L || size.Value > int.MaxValue)
        {
            throw new QpdfParseException($"Trailer /Size {size.Value} is out of range.", null, null);
        }

        return (int)size.Value;
    }

    private static void ValidateCoverage(ReadOnlyMemory<byte> data, Dictionary<int, XrefEntry> entries, int size)
    {
        for (int number = 0; number < size; number++)
        {
            if (!entries.TryGetValue(number, out XrefEntry entry))
            {
                throw new QpdfParseException($"Object {number} has no xref entry.", null, number);
            }

            if (entry.Kind == XrefEntryKind.Uncompressed && (entry.First < 0L || entry.First >= data.Length))
            {
                throw new QpdfParseException($"xref entry for object {number} points outside the input.", null, number);
            }
        }

        foreach (int number in entries.Keys)
        {
            if (number < 0 || number >= size)
            {
                throw new QpdfParseException($"xref entry for object {number} is outside /Size {size}.", null, number);
            }
        }

        if (!entries.TryGetValue(0, out XrefEntry head) || head.Kind != XrefEntryKind.Free)
        {
            throw new QpdfParseException("Object 0 must be a free xref entry.", null, 0);
        }
    }
}

