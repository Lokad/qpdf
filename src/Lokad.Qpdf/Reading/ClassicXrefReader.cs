using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

/// <summary>A classic xref entry: byte offset or free-list link.</summary>
internal readonly struct XrefEntry
{
    internal XrefEntry(long offset, int generation, bool inUse)
    {
        Offset = offset;
        Generation = generation;
        InUse = inUse;
    }

    internal long Offset { get; }

    internal int Generation { get; }

    internal bool InUse { get; }
}

/// <summary>
/// Strict single-section classic xref table with its trailer. Parses the tail
/// `startxref` (which must match the actual table position), fixed-width
/// entries, and the trailer dictionary; validates coverage against `/Size`.
/// Incremental chains, hybrid structures, and xref streams arrive in M2.
/// </summary>
internal sealed class ClassicXrefTable
{
    private const int TailScanBytes = 65536;

    private ClassicXrefTable(long xrefOffset, Dictionary<int, XrefEntry> entries, PdfDictionary trailer)
    {
        XrefOffset = xrefOffset;
        Entries = entries;
        Trailer = trailer;
    }

    internal long XrefOffset { get; }

    internal IReadOnlyDictionary<int, XrefEntry> Entries { get; }

    internal PdfDictionary Trailer { get; }

    internal static ClassicXrefTable Parse(
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
        if (!tokenizer.TryReadToken(out PdfToken table) || table.Kind != PdfTokenKind.XRef)
        {
            throw new QpdfParseException("startxref does not point at an xref table.", xrefOffset, null);
        }

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

        int size = ReadSize(trailer);
        ValidateCoverage(data, entries, size);
        return new ClassicXrefTable(xrefOffset, entries, trailer);
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

        return new XrefEntry(target, generation, kind == (byte)'n');
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

            if (entry.InUse && (entry.Offset < 0L || entry.Offset >= data.Length))
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

        if (!entries.TryGetValue(0, out XrefEntry head) || head.InUse)
        {
            throw new QpdfParseException("Object 0 must be a free xref entry.", null, 0);
        }
    }
}
