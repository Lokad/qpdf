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
/// Strict chained cross-reference with its effective trailer, from classic
/// tables and streams. Parses the tail `startxref`, follows `/Prev` chains and
/// hybrid `/XRefStm` links with latest-wins precedence, validates entries
/// before reading, and requires exact union coverage of the effective `/Size`.
/// Bounded repair lives in <see cref="XRefRecovery"/> and only runs when the
/// caller selects it after strict parsing fails.
/// </summary>
internal sealed class XRefTable : IDisposable
{
    private const int TailScanBytes = 65536;
    private Dictionary<int, XrefEntry>? _entries;
    private PdfDictionary? _trailer;
    private List<PdfDictionary>? _trailers;
    private readonly ResourceGovernor _governor;
    private long _containerBytes;
    private long _ownedValuesBytes;

    private XRefTable(long xrefOffset, Dictionary<int, XrefEntry> entries, PdfDictionary trailer,
        List<PdfDictionary> trailers, ResourceGovernor governor, long containerBytes)
    {
        XrefOffset = xrefOffset;
        _entries = entries;
        _trailer = trailer;
        _trailers = trailers;
        _governor = governor;
        _containerBytes = containerBytes;
    }

    internal static XRefTable CreateOwned(long xrefOffset, Dictionary<int, XrefEntry> entries,
        PdfDictionary trailer, List<PdfDictionary> trailers, ResourceGovernor governor,
        ParserMemoryScope containers, ParserMemoryScope values, bool valuesEscape)
    {
        containers.CheckOwner(governor);
        values.CheckOwner(governor);
        // The table wrapper and parsed values may have independently retained
        // aliases. Only the exclusive map/list receipt releases on disposal.
        values.Reserve(96L);
        XRefTable table = new(xrefOffset, entries, trailer, trailers, governor, containers.ReservedBytes);
        if (valuesEscape) { values.RetainGraph(); }
        else
        {
            table._ownedValuesBytes = values.ReservedBytes;
            values.Retain();
        }
        containers.Retain();
        return table;
    }

    internal void CheckOwner(ResourceGovernor governor)
    {
        if (!ReferenceEquals(_governor, governor)) { throw new InvalidOperationException("Table storage belongs to a different owner."); }
        _ = Entries;
    }

    internal void TransferExclusiveValuesTo(ParserMemoryScope target)
    {
        target.CheckOwner(_governor);
        _ = Entries;
        target.AdoptReserved(_ownedValuesBytes);
        _ownedValuesBytes = 0L;
    }

    public void Dispose()
    {
        if (_entries is null) { return; }
        _entries = null;
        _trailer = null;
        _trailers = null;
        _governor.ReleaseMemory(_containerBytes);
        _governor.ReleaseMemory(_ownedValuesBytes);
        _containerBytes = 0L;
        _ownedValuesBytes = 0L;
    }

    internal long XrefOffset { get; }

    internal IReadOnlyDictionary<int, XrefEntry> Entries => _entries ?? throw new ObjectDisposedException(nameof(XRefTable));

    internal PdfDictionary Trailer => _trailer ?? throw new ObjectDisposedException(nameof(XRefTable));

    internal IReadOnlyList<PdfDictionary> Trailers => _trailers ?? throw new ObjectDisposedException(nameof(XRefTable));

    internal static XRefTable Parse(
        ReadOnlyMemory<byte> data,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        QpdfRecoveryMode recovery,
        bool valuesEscape)
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

        try
        {
            return ParseStrict(data, limits, governor, diagnostics, valuesEscape);
        }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
        {
            if (recovery == QpdfRecoveryMode.Strict)
            {
                throw;
            }

            return XRefRecovery.Recover(data, limits, governor, diagnostics, ex.Message, valuesEscape);
        }
    }

    private static XRefTable ParseStrict(
        ReadOnlyMemory<byte> data,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        bool valuesEscape)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using ParserMemoryScope values = ParserMemoryScope.Create(governor);
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

        Dictionary<int, XrefEntry> merged = ParserContainers.CreateDictionary<int, XrefEntry>(memory);
        List<PdfDictionary> trailers = ParserContainers.CreateList<PdfDictionary>(memory);
        using ParserMemoryScope revisions = ParserMemoryScope.Create(governor);
        HashSet<long> visited = ParserContainers.CreateSet<long>(revisions);
        int? effectiveSize = null;
        long? current = xrefOffset;
        long sections = 0L;
        while (current.HasValue)
        {
            if (!ParserContainers.AddSetItem(revisions, visited, current.Value, sizeof(long)))
            {
                throw new QpdfParseException($"Cross-reference chain cycles via /Prev at offset {current.Value}.", current.Value, null);
            }

            sections += 1L;
            if (sections > limits.MaxRevisions)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxRevisions), sections, limits.MaxRevisions);
            }

            governor.NoteOperation();
            current = ProcessSection(data, limits, governor, diagnostics, merged, trailers, ref effectiveSize, current.Value, memory, values);
        }

        if (effectiveSize is null || trailers.Count == 0)
        {
            throw new QpdfParseException("No cross-reference sections were parsed.", xrefOffset, null);
        }

        ValidateUnionCoverage(merged, effectiveSize.Value, governor);
        return CreateOwned(xrefOffset, merged, trailers[0], trailers, governor, memory, values, valuesEscape);
    }

    internal static long ReadTailOffset(ReadOnlyMemory<byte> data)
    {
        long marker = FindStartXRef(data);
        PdfTokenizer tokenizer = new(data);
        tokenizer.SetPosition(marker);
        if (!tokenizer.TryReadToken(out PdfToken keyword) || keyword.Kind != PdfTokenKind.StartXRef
            || !tokenizer.TryReadToken(out PdfToken offset) || offset.Kind != PdfTokenKind.Integer)
        { throw new CompleteInputRequiredException(); }
        long position = ParseOffset(offset);
        ExpectEndOfFile(data, offset);
        return position;
    }

    // Only a complete standalone classic section can use a compact input.
    // Parsing/coverage use the ordinary helpers. Local error offsets never
    // escape: the owner retries the complete reader on any speculative error.
    internal static XRefTable ReadClassicWindow(ReadOnlyMemory<byte> window, long physicalOffset,
        int inputLength, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using ParserMemoryScope values = ParserMemoryScope.Create(governor);
        using ParserMemoryScope section = ParserMemoryScope.Create(governor);
        PdfTokenizer tokenizer = new(window);
        if (!tokenizer.TryReadToken(out PdfToken keyword) || keyword.Kind != PdfTokenKind.XRef)
        { throw new CompleteInputRequiredException(); }
        (Dictionary<int, XrefEntry> entries, PdfDictionary trailer) = ParseClassicSection(
            window, limits, governor, diagnostics, tokenizer, values, section);
        if ((PdfDictionaryLookup.TryGet(trailer, "Prev"u8, governor, governor.CancellationToken, out QpdfObject previous) && previous is not PdfNull)
            || (PdfDictionaryLookup.TryGet(trailer, "XRefStm"u8, governor, governor.CancellationToken, out QpdfObject hybrid) && hybrid is not PdfNull))
        { throw new CompleteInputRequiredException(); }
        int size = ReadSize(trailer, governor);
        Dictionary<int, XrefEntry> merged = ParserContainers.CreateDictionary<int, XrefEntry>(memory);
        MergeSection(merged, entries, size, inputLength, physicalOffset, governor, memory);
        ValidateUnionCoverage(merged, size, governor);
        List<PdfDictionary> trailers = ParserContainers.CreateList<PdfDictionary>(memory);
        ParserContainers.AddItem(memory, trailers, trailer, IntPtr.Size);
        return CreateOwned(physicalOffset, merged, trailer, trailers, governor, memory, values, false);
    }

    private static long? ProcessSection(
        ReadOnlyMemory<byte> data,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        Dictionary<int, XrefEntry> merged,
        List<PdfDictionary> trailers,
        ref int? effectiveSize,
        long current,
        ParserMemoryScope memory,
        ParserMemoryScope values)
    {
        using ParserMemoryScope section = ParserMemoryScope.Create(governor);
        PdfTokenizer tokenizer = new(data);
        tokenizer.SetPosition(current);
        if (!tokenizer.TryReadToken(out PdfToken head))
        {
            throw new QpdfParseException("Cross-reference section points at end of input.", current, null);
        }

        // Classic sections continue after the consumed keyword; streams rewind to re-read their header.
        if (head.Kind == PdfTokenKind.XRef)
        {
            (Dictionary<int, XrefEntry> entries, PdfDictionary trailer) = ParseClassicSection(data, limits, governor, diagnostics, tokenizer, values, section);
            if (effectiveSize is null)
            {
                effectiveSize = ReadSize(trailer, governor);
            }

            MergeSection(merged, entries, effectiveSize.Value, data.Length, current, governor, memory);
            section.Discard();
            ParserContainers.AddItem(memory, trailers, trailer, IntPtr.Size);
            long? hybrid = limits.IgnoreXrefStreams ? null : ReadLinkOffset(trailer, "XRefStm"u8, "XRefStm", data, current, governor);
            if (hybrid.HasValue)
            {
                (Dictionary<int, XrefEntry> extra, PdfDictionary _) = ParseStreamSection(data, limits, governor, diagnostics, hybrid.Value, values, section, false);
                MergeSection(merged, extra, effectiveSize.Value, data.Length, hybrid.Value, governor, memory);
            }

            return ReadLinkOffset(trailer, "Prev"u8, "Prev", data, current, governor);
        }

        if (head.Kind == PdfTokenKind.Integer)
        {
            tokenizer.SetPosition(head.Offset);
        }

        if (head.Kind == PdfTokenKind.Integer && limits.IgnoreXrefStreams)
        {
            return null;
        }

        if (head.Kind == PdfTokenKind.Integer)
        {
            (Dictionary<int, XrefEntry> entries, PdfDictionary trailer) = ParseStreamSection(data, limits, governor, diagnostics, current, values, section, true);
            if (effectiveSize is null)
            {
                effectiveSize = ReadSize(trailer, governor);
            }

            MergeSection(merged, entries, effectiveSize.Value, data.Length, current, governor, memory);
            section.Discard();
            ParserContainers.AddItem(memory, trailers, trailer, IntPtr.Size);
            return ReadLinkOffset(trailer, "Prev"u8, "Prev", data, current, governor);
        }

        throw new QpdfParseException("Cross-reference section is neither an xref table nor a stream.", current, null);
    }

    private static void MergeSection(
        Dictionary<int, XrefEntry> merged, Dictionary<int, XrefEntry> entries, int effectiveSize, int dataLength, long offset,
        ResourceGovernor governor, ParserMemoryScope memory)
    {
        // Every section key is already merged or will be added. Its parsed
        // count bounds the union below even after a newer section populated it.
        int capacity = merged.EnsureCapacity(0);
        int target = entries.Count;
        if (capacity > 0 && target > capacity)
        {
            // Avoid a small resize followed immediately by geometric growth
            // when the sections are largely disjoint. Their combined count
            // bounds useful capacity without trusting the declared /Size.
            long upper = (long)merged.Count + entries.Count;
            target = (int)Math.Min(Array.MaxLength, Math.Min(upper, Math.Max(target, capacity * 2L)));
        }
        // The shared hint keeps tight jobs on ordinary admitted growth.
        ParserContainers.HintEntryCapacity(memory, merged, target, 24L, governor, true);
        int maximumCount = (int)Math.Min(Array.MaxLength, (long)merged.Count + entries.Count);
        foreach (KeyValuePair<int, XrefEntry> entry in entries)
        {
            governor.NoteOperation();
            if (entry.Key < 0 || entry.Key >= effectiveSize)
            {
                throw new QpdfParseException($"Xref entry for object {entry.Key} is outside /Size {effectiveSize}.", offset, entry.Key);
            }

            if (entry.Value.Kind == XrefEntryKind.Uncompressed
                && (entry.Value.First < 0L || entry.Value.First >= dataLength))
            {
                throw new QpdfParseException($"Xref entry for object {entry.Key} points outside the input.", offset, entry.Key);
            }

            if (!merged.ContainsKey(entry.Key))
            {
                ParserContainers.EnsureEntryCapacity(memory, merged, 24L, maximumCount);
                merged[entry.Key] = entry.Value;
            }
        }
    }

    private static long? ReadLinkOffset(PdfDictionary trailer, ReadOnlySpan<byte> key, string name,
        ReadOnlyMemory<byte> data, long offset, ResourceGovernor governor)
    {
        if (!PdfDictionaryLookup.TryGet(trailer, key, governor, governor.CancellationToken, out QpdfObject raw) || raw is PdfNull)
        {
            return null;
        }

        if (raw is not PdfInteger link || link.Value < 0L || link.Value >= data.Length)
        {
            throw new QpdfParseException($"Trailer /{name} must be an offset inside the input.", offset, null);
        }

        return link.Value;
    }

    private static void ValidateUnionCoverage(Dictionary<int, XrefEntry> entries, int size, ResourceGovernor governor)
    {
        for (int number = 1; number < size; number++)
        {
            governor.NoteOperation();
            if (!entries.ContainsKey(number))
            {
                throw new QpdfParseException($"Object {number} has no xref entry.", null, number);
            }
        }

        if (entries.TryGetValue(0, out XrefEntry head) && head.Kind != XrefEntryKind.Free)
        {
            throw new QpdfParseException("Object 0 must be absent or free.", null, 0);
        }
    }

    private static (Dictionary<int, XrefEntry> Entries, PdfDictionary Trailer) ParseClassicSection(
        ReadOnlyMemory<byte> data,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        PdfTokenizer tokenizer,
        ParserMemoryScope memory,
        ParserMemoryScope entriesMemory)
    {
        Dictionary<int, XrefEntry> entries = ParserContainers.CreateDictionary<int, XrefEntry>(entriesMemory);
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

            ReadSubsection(data, tokenizer, token, countToken, entries, governor, entriesMemory);
        }

        PdfParser parser = new(limits, governor, diagnostics, null);
        QpdfObject trailerObject = parser.ParseValue(tokenizer, 0, memory);
        if (trailerObject is not PdfDictionary trailer)
        {
            throw new QpdfParseException("Trailer must be a dictionary.", tokenizer.Position, null);
        }

        return (entries, trailer);
    }

    private static (Dictionary<int, XrefEntry> Entries, PdfDictionary Trailer) ParseStreamSection(
        ReadOnlyMemory<byte> data,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        long xrefOffset,
        ParserMemoryScope memory,
        ParserMemoryScope entriesMemory,
        bool retainDictionary)
    {
        PdfTokenizer tokenizer = new(data);
        tokenizer.SetPosition(xrefOffset);
        PdfParser parser = new(limits, governor, diagnostics, null);
        using ParserMemoryScope parsed = ParserMemoryScope.Create(governor);
        (int streamNumber, int streamGeneration, QpdfObject raw) = parser.ParseIndirectObject(tokenizer, 0, parsed);
        if (raw is not PdfStream stream)
        {
            throw new QpdfParseException("Cross-reference offset does not point at an xref stream.", xrefOffset, streamNumber);
        }

        PdfDictionary dictionary = stream.Dictionary;
        if (!PdfDictionaryLookup.TryGet(dictionary, "Type"u8, governor, governor.CancellationToken, out QpdfObject typeRaw)
            || typeRaw is not PdfName typeName || !PdfDictionaryLookup.NameEquals(typeName, "XRef"u8, governor, governor.CancellationToken))
        {
            throw new QpdfParseException("Cross-reference stream must declare /Type /XRef.", xrefOffset, streamNumber);
        }

        int size = ReadSize(dictionary, governor);
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(48L);
        int[] widths = ReadWidths(dictionary, governor);
        List<(int First, int Count)> index = ReadIndex(dictionary, size, governor, scratch);
        ReadOnlyMemory<byte> decoded = DecodeXRefData(stream, governor, limits, xrefOffset, streamNumber, scratch);
        Dictionary<int, XrefEntry> entries = ReadStreamEntries(decoded, widths, index, data.Length, governor, xrefOffset, entriesMemory);
        if (retainDictionary)
        {
            // ParseStream retains length + 64 for its wrapper/raw array. The
            // xref table retains the dictionary, with no raw stream object.
            parsed.Release(checked((long)stream.RawMemory.Length + 64L));
            parsed.TransferTo(memory);
        }
        return (entries, dictionary);
    }

    private static int[] ReadWidths(PdfDictionary dictionary, ResourceGovernor governor)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, "W"u8, governor, governor.CancellationToken, out QpdfObject raw) || raw is not PdfArray array || array.Count != 3)
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

    private static List<(int First, int Count)> ReadIndex(
        PdfDictionary dictionary, int size, ResourceGovernor governor, ParserMemoryScope scratch)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, "Index"u8, governor, governor.CancellationToken, out QpdfObject raw) || raw is PdfNull)
        {
            scratch.Reserve(72L);
            return new List<(int First, int Count)>(1) { (0, size) };
        }

        if (raw is not PdfArray array || array.Count < 2 || array.Count % 2 != 0)
        {
            throw new QpdfParseException("Cross-reference stream /Index must hold even pairs.", null, null);
        }

        scratch.Reserve(checked(64L + (array.Count / 2L) * 8L));
        List<(int First, int Count)> index = new(array.Count / 2);
        for (int i = 0; i < array.Count; i += 2)
        {
            governor.NoteOperation();
            if (array[i] is not PdfInteger first || array[i + 1] is not PdfInteger count)
            {
                throw new QpdfParseException("Cross-reference stream /Index entries must be integers.", null, null);
            }

            if (first.Value < 0L || first.Value >= size || count.Value <= 0L || count.Value > size - first.Value)
            {
                throw new QpdfParseException($"Cross-reference stream /Index starting at {first.Value} with count {count.Value} is outside /Size {size}.", null, null);
            }

            index.Add(((int)first.Value, (int)count.Value));
        }

        return index;
    }

    private static ReadOnlyMemory<byte> DecodeXRefData(
        PdfStream stream,
        ResourceGovernor governor,
        QpdfLimits limits,
        long xrefOffset,
        int streamNumber,
        ParserMemoryScope scratch)
    {
        FilterPlan stages;
        try
        {
            stages = FilterChain.Resolve(stream.Dictionary, null, governor, governor.CancellationToken);
        }
        catch (QpdfParseException ex)
        {
            throw new QpdfParseException($"Cross-reference stream declares invalid filters: {ex.Message}", xrefOffset, streamNumber);
        }
        using FilterPlan stageOwner = stages;

        if (stages.Count == 0) { return stream.RawMemory; }
        byte[] encoded = stream.ArrayData(scratch, governor, governor.CancellationToken);
        return FilterChain.Decode(encoded, stages, limits, governor, scratch);
    }

    private static Dictionary<int, XrefEntry> ReadStreamEntries(
        ReadOnlyMemory<byte> decoded,
        int[] widths,
        List<(int First, int Count)> index,
        int dataLength,
        ResourceGovernor governor,
        long xrefOffset,
        ParserMemoryScope memory)
    {
        int entrySize = widths[0] + widths[1] + widths[2];
        long total = 0L;
        long previousEnd = 0L;
        bool orderedDisjoint = true;
        bool includesZero = false;
        foreach ((int first, int count) in index)
        {
            checked
            {
                total += count;
            }
            if (count > 0)
            {
                if (first < previousEnd) { orderedDisjoint = false; }
                previousEnd = (long)first + count;
                includesZero |= first == 0;
            }
        }

        checked
        {
            if (total * entrySize != decoded.Length)
            {
                throw new QpdfParseException($"Cross-reference stream data holds {decoded.Length} bytes for {total} entries of {entrySize}.", xrefOffset, null);
            }
        }

        Dictionary<int, XrefEntry> entries = ParserContainers.CreateDictionary<int, XrefEntry>(memory);
        // Shape validation establishes the row count. Only ordered, disjoint
        // ranges establish the dictionary count; object zero is never stored.
        // Other shapes and tight budgets retain ordinary admitted growth.
        if (entrySize > 0 && orderedDisjoint && total <= int.MaxValue)
        {
            int storedCount = checked((int)total - (includesZero ? 1 : 0));
            ParserContainers.HintEntryCapacity(memory, entries, storedCount, 24L, governor);
        }
        int position = 0;
        Span<long> fields = stackalloc long[3];
        foreach ((int first, int count) in index)
        {
            for (int k = 0; k < count; k++)
            {
                int number = first + k;
                governor.NoteOperation();
                for (int field = 0; field < 3; field++)
                {
                    long value = 0L;
                    for (int b = 0; b < widths[field]; b++)
                    {
                        value = (value << 8) | decoded.Span[position];
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

                ParserContainers.EnsureEntryCapacity(memory, entries, 24L);
                entries[number] = entry;
            }
        }

        return entries;
    }

    private static XrefEntry ReadStreamEntry(ReadOnlySpan<long> fields, int number, int dataLength, long xrefOffset)
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

        // Future entry types denote null. Keep the slot in the union so an
        // older revision cannot restore a superseded object. Other fields have
        // no defined meaning for these types, including unsigned-wide values.
        return XrefEntry.Free(0L, 0);
    }


    private static long FindStartXRef(ReadOnlyMemory<byte> data)
    {
        int window = Math.Min(data.Length, TailScanBytes);
        int baseOffset = data.Length - window;
        ReadOnlySpan<byte> tail = data.Span.Slice(baseOffset, window);
        ReadOnlySpan<byte> marker = "startxref"u8;
        int searchEnd = tail.Length;
        while (searchEnd >= marker.Length)
        {
            int i = tail[..searchEnd].LastIndexOf(marker);
            if (i < 0) { break; }
            if ((i == 0 || IsDelimiterOrWhitespace(tail[i - 1]))
                && (i + marker.Length >= tail.Length || IsDelimiterOrWhitespace(tail[i + marker.Length])))
            {
                return baseOffset + i;
            }
            // Check boundaries against the original tail, including bytes past
            // this candidate, before searching for an earlier occurrence.
            searchEnd = i + marker.Length - 1;
        }
        throw new QpdfParseException("No startxref marker in the file tail.", data.Length, null);
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
        ResourceGovernor governor,
        ParserMemoryScope memory)
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

        long cursor = SkipSubsectionBreak(data, tokenizer, firstToken);
        ReadOnlySpan<byte> span = data.Span;
        // This validated subsection bounds the entries added by this loop.
        // Choose capped growth before admission rather than retrying on refusal.
        int maximumCount = (int)Math.Min(Array.MaxLength, entries.Count + count);
        for (long number = first; number < first + count; number++)
        {
            XrefEntry entry = ReadEntry(span, ref cursor, firstToken.Offset, (int)number);
            if (entries.ContainsKey((int)number))
            {
                throw new QpdfParseException($"Duplicate xref entry for object {number}.", firstToken.Offset, (int)number);
            }

            governor.NoteOperation();
            ParserContainers.EnsureEntryCapacity(memory, entries, 24L, maximumCount);
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

    private static int ReadSize(PdfDictionary trailer, ResourceGovernor governor)
    {
        if (!PdfDictionaryLookup.TryGet(trailer, "Size"u8, governor, governor.CancellationToken, out QpdfObject raw) || raw is not PdfInteger size)
        {
            throw new QpdfParseException("Trailer must declare an integer /Size.", null, null);
        }

        if (size.Value < 1L || size.Value > int.MaxValue)
        {
            throw new QpdfParseException($"Trailer /Size {size.Value} is out of range.", null, null);
        }

        return (int)size.Value;
    }

}
