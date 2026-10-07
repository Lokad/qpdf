using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

/// <summary>
/// Strict single-section document engine: on-demand object loading over a
/// unified xref table with a parse cache. Reference chains never recurse:
/// nested references stay references until explicitly resolved, and the page
/// walk carries its own visited set and depth budget. Compressed objects
/// decode their object stream once and cache every contained object whose
/// xref entry points back at that stream.
/// </summary>
internal sealed class DocumentEngine : IIndirectResolver, IDisposable
{
    // A cache receipt owns only this parsed subtree. Nested resolver loads have
    // independent receipts. Reads protect values that may outlive replacement.
    private readonly record struct CachedObject(QpdfObject Value, long ReservedBytes, bool Borrowed);

    private ReadOnlyMemory<byte> _data;
    private XRefTable? _table;
    private readonly QpdfLimits _limits;
    private readonly ResourceGovernor _governor;
    private readonly DiagnosticBag _diagnostics;
    private readonly QpdfRecoveryMode _recovery;
    private ParserMemoryScope? _metadata;
    private bool _ownsTable;
    private bool _valuesEscape = true;
    private Dictionary<int, CachedObject>? _cache;
    private HashSet<int>? _decodingStreams;
    private HashSet<int>? _loading;
    private IReadOnlyDictionary<int, QpdfObject>? _inspectionObjects;
    private long _unborrowedBytes;
    private Dictionary<int, CachedObject> Cache => _cache ?? throw new ObjectDisposedException(nameof(DocumentEngine));
    private HashSet<int> Loading => _loading ?? throw new ObjectDisposedException(nameof(DocumentEngine));
    private HashSet<int> DecodingStreams => _decodingStreams ?? throw new ObjectDisposedException(nameof(DocumentEngine));
    private XRefTable Table => _table ?? throw new ObjectDisposedException(nameof(DocumentEngine));
    private ParserMemoryScope Metadata => _metadata ?? throw new ObjectDisposedException(nameof(DocumentEngine));

    internal DocumentEngine(
        ReadOnlyMemory<byte> data,
        XRefTable table,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        QpdfRecoveryMode recovery)
    {
        _data = data;
        _table = table;
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _recovery = recovery;
        limits.Validate();
        _metadata = ParserMemoryScope.Create(governor);
        try
        {
            _cache = ParserContainers.CreateDictionary<int, CachedObject>(_metadata);
            _loading = ParserContainers.CreateSet<int>(_metadata);
            ParserContainers.EnsureSetCapacity(_metadata, _loading, sizeof(int));
            _decodingStreams = ParserContainers.CreateSet<int>(_metadata);
            ParserContainers.EnsureSetCapacity(_metadata, _decodingStreams, sizeof(int));
        }
        catch { _metadata.Dispose(); throw; }
    }

    internal static DocumentEngine CreateOwned(
        ReadOnlyMemory<byte> data,
        XRefTable table,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        QpdfRecoveryMode recovery,
        long reservedInput)
        => CreateOwned(data, table, limits, governor, diagnostics, recovery, reservedInput, true);

    internal static DocumentEngine CreateOwned(
        ReadOnlyMemory<byte> data,
        XRefTable table,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        QpdfRecoveryMode recovery,
        long reservedInput,
        bool valuesEscape)
    {
        table.CheckOwner(governor);
        if (reservedInput != checked(data.Length + GovernedBufferStream.ArrayOverheadBytes))
        {
            throw new InvalidOperationException("The document input receipt does not match its storage.");
        }
        DocumentEngine engine = new(data, table, limits, governor, diagnostics, recovery) { _valuesEscape = valuesEscape };
        try
        {
            engine.Metadata.AdoptReserved(reservedInput);
            engine._ownsTable = true;
            return engine;
        }
        catch { engine.Dispose(); throw; }
    }

    private bool TryGetCached(int number, out QpdfObject value)
    {
        if (Cache.TryGetValue(number, out CachedObject cached))
        {
            if (!cached.Borrowed && _valuesEscape)
            {
                long retained = _governor.RetainValue(cached.Value, cached.ReservedBytes);
                _governor.ReleaseMemory(cached.ReservedBytes - retained);
                Cache[number] = cached with { Borrowed = true };
                _unborrowedBytes = checked(_unborrowedBytes - cached.ReservedBytes);
            }
            value = cached.Value;
            return true;
        }
        value = PdfNull.Instance;
        return false;
    }

    private void StoreCached(int number, QpdfObject value, ParserMemoryScope parsed, bool borrowed)
    {
        borrowed = borrowed && _valuesEscape;
        _governor.CheckCancellation();
        bool replacing = Cache.TryGetValue(number, out CachedObject previous);
        if (!replacing) { ParserContainers.EnsureEntryCapacity(Metadata, Cache, 24L); }
        _governor.CheckCancellation();
        long discarded = replacing && !previous.Borrowed ? previous.ReservedBytes : 0L;
        long nextUnborrowed = checked(_unborrowedBytes - discarded + (borrowed ? 0L : parsed.ReservedBytes));
        long reservedBytes = parsed.ReservedBytes;
        if (borrowed) { parsed.Retain(value); }
        Cache[number] = new CachedObject(value, reservedBytes, borrowed);
        if (!borrowed) { parsed.Retain(); }
        _unborrowedBytes = nextUnborrowed;
        _governor.ReleaseMemory(discarded);
    }

    public void Dispose()
    {
        if (_cache is null) { return; }
        // Drop backing storage before releasing metadata. Borrowed graphs have
        // escaped this owner; their admission and bytes remain independent.
        _cache = null;
        _loading = null;
        _decodingStreams = null;
        _inspectionObjects = null;
        _data = default;
        XRefTable? table = _table;
        _table = null;
        ParserMemoryScope? metadata = _metadata;
        _metadata = null;
        if (_ownsTable) { table?.Dispose(); }
        _governor.ReleaseMemory(_unborrowedBytes);
        _unborrowedBytes = 0L;
        metadata?.Dispose();
    }

    internal PdfDictionary Trailer => Table.Trailer;

    internal IReadOnlyList<PdfDictionary> Trailers => Table.Trailers;

    internal DiagnosticBag Diagnostics => _diagnostics;

    internal IReadOnlyDictionary<int, XrefEntry> XRefEntries => Table.Entries;

    internal ReadOnlyMemory<byte> Data => _data;

    // Inspection edits share this resolver but never change physical xref offsets.
    // Their scoped owner detaches the graph before releasing its storage.
    internal void SetInspectionObjects(IReadOnlyDictionary<int, QpdfObject>? objects)
    {
        _ = Cache;
        _inspectionObjects = objects;
    }

    internal IEnumerable<int> InspectionObjectNumbers => _inspectionObjects?.Keys ?? [];

    bool IIndirectResolver.TryResolve(PdfReference reference, out QpdfObject? value)
    {
        _ = Cache;
        if (reference is null)
        {
            throw new ArgumentNullException(nameof(reference));
        }

        if (_inspectionObjects is not null && _inspectionObjects.TryGetValue(reference.ObjectNumber, out QpdfObject? edited))
        {
            int generation = Table.Entries.TryGetValue(reference.ObjectNumber, out XrefEntry original)
                && original.Kind == XrefEntryKind.Uncompressed ? original.Second : 0;
            value = reference.Generation == generation ? edited : PdfNull.Instance;
            return true;
        }

        if (reference.ObjectNumber == 0 && !Table.Entries.ContainsKey(0))
        {
            value = PdfNull.Instance;
            return true;
        }

        if (!Table.Entries.TryGetValue(reference.ObjectNumber, out XrefEntry entry))
        {
            value = null;
            return false;
        }

        if (entry.Kind == XrefEntryKind.Free)
        {
            value = PdfNull.Instance;
            return true;
        }

        // Object-generation pairs resolve exactly like the reference: a stale
        // generation (freed or superseded) is a dead pair and resolves to null
        // instead of the newest object with that number. Compressed members are
        // always generation 0.
        if (entry.Kind == XrefEntryKind.Compressed ? reference.Generation != 0 : entry.Second != reference.Generation)
        {
            value = PdfNull.Instance;
            return true;
        }

        if (TryGetCached(reference.ObjectNumber, out QpdfObject cached))
        {
            value = cached;
            return true;
        }

        value = Load(reference.ObjectNumber, entry);
        return true;
    }

    internal QpdfObject Load(int number)
    {
        _ = Cache;
        if (_inspectionObjects is not null && _inspectionObjects.TryGetValue(number, out QpdfObject? edited)) { return edited; }
        if (TryGetCached(number, out QpdfObject cached))
        {
            return cached;
        }

        if (number == 0 && !Table.Entries.ContainsKey(0))
        {
            return PdfNull.Instance;
        }

        if (!Table.Entries.TryGetValue(number, out XrefEntry entry))
        {
            throw new QpdfParseException($"Object {number} has no xref entry.", null, number);
        }

        if (entry.Kind == XrefEntryKind.Free)
        {
            return PdfNull.Instance;
        }

        return Load(number, entry);
    }

    private QpdfObject Load(int number, XrefEntry entry)
    {
        if (Loading.Count >= _limits.MaxNestingDepth)
        {
            throw new QpdfParseException($"Object load depth exceeds {_limits.MaxNestingDepth}.", null, number);
        }

        if (!ParserContainers.AddSetItem(Metadata, Loading, number, sizeof(int)))
        {
            throw new QpdfParseException($"Object {number} is referenced cyclically during load.", null, number);
        }

        try
        {
            if (entry.Kind == XrefEntryKind.Compressed)
            {
                return LoadCompressed(number, (int)entry.First, entry.Second);
            }

            _governor.ChargeWork(1L);
            using ParserMemoryScope memory = ParserMemoryScope.Create(_governor);
            PdfTokenizer tokenizer = new(_data);
            tokenizer.SetPosition(entry.First);
            _governor.ReserveMemory(128L);
            int foundNumber;
            int foundGeneration;
            QpdfObject value;
            try
            {
                bool reconstructed = Table.XrefOffset < 0L;
                QpdfLimits parserLimits = reconstructed
                    ? _limits with { MaxContainerSize = Math.Min(_limits.MaxContainerSize, _limits.MaxContainerSizeDamaged) }
                    : _limits;
                PdfParser parser = new(parserLimits, _governor, _diagnostics, this)
                {
                    TolerateObjectErrors = _recovery != QpdfRecoveryMode.Strict,
                    SanityChecks = reconstructed,
                };
                (foundNumber, foundGeneration, value) = parser.ParseIndirectObject(tokenizer, 0, memory);
            }
            finally
            {
                _governor.ReleaseMemory(128L);
            }
            if (foundNumber != number || foundGeneration != entry.Second)
            {
                throw new QpdfParseException($"Object header contradicts its xref entry for object {number}.", entry.First, number);
            }

            StoreCached(number, value, memory, true);
            return value;
        }
        finally
        {
            Loading.Remove(number);
        }
    }

    private QpdfObject LoadCompressed(int number, int streamNumber, int index)
    {
        if (TryGetCached(number, out QpdfObject cached))
        {
            return cached;
        }

        DecodeObjStm(streamNumber);
        if (TryGetCached(number, out QpdfObject resolved))
        {
            return resolved;
        }

        throw new QpdfParseException($"Compressed object {number} is missing from object stream {streamNumber}.", null, number);
    }

    private void DecodeObjStm(int streamNumber)
    {
        if (!ParserContainers.AddSetItem(Metadata, DecodingStreams, streamNumber, sizeof(int)))
        {
            throw new QpdfParseException($"Object stream cycle at object {streamNumber}.", null, streamNumber);
        }

        try
        {
            if (!Table.Entries.TryGetValue(streamNumber, out XrefEntry entry)
                || entry.Kind != XrefEntryKind.Uncompressed)
            {
                throw new QpdfParseException($"Object stream {streamNumber} is not a direct object.", null, streamNumber);
            }

            QpdfObject raw = Load(streamNumber);
            if (raw is not PdfStream stream)
            {
                throw new QpdfParseException($"Object {streamNumber} is not a stream.", null, streamNumber);
            }

            PdfDictionary dictionary = stream.Dictionary;
            if (!PdfDictionaryLookup.TryGet(dictionary, "Type"u8, _governor, _governor.CancellationToken, out QpdfObject typeRaw)
                || typeRaw is not PdfName typeName || !PdfDictionaryLookup.NameEquals(typeName, "ObjStm"u8, _governor, _governor.CancellationToken))
            {
                throw new QpdfParseException($"Object {streamNumber} must declare /Type /ObjStm.", null, streamNumber);
            }

            // the header pairs carried by this stream, matched against the
            // cross-reference table, matching the pinned reference, whose reader
            // matching the pinned reference, whose reader carries no Extends handling
            // (even a self-reference like the objstm-extends.pdf fixture resolves).
            // The key itself stays opaque data for the copy engine to preserve.

            int count = ReadObjStmInt(dictionary, "N"u8, "N", 1, streamNumber);
            int first = ReadObjStmInt(dictionary, "First"u8, "First", 0, streamNumber);
            using ParserMemoryScope scratch = ParserMemoryScope.Create(_governor);
            ReadOnlyMemory<byte> decoded = DecodeStreamData(stream, streamNumber, scratch);
            if (first >= decoded.Length && count > 0)
            {
                throw new QpdfParseException($"Object stream {streamNumber} has invalid /First entry.", null, streamNumber);
            }

            List<(int Number, int Offset)> pairs = ReadObjStmPairs(decoded, count, streamNumber, scratch);
            scratch.Reserve(checked((long)pairs.Count + 32L));
            bool[] previouslyCached = new bool[pairs.Count];
            long lastOffset = -1L;
            for (int i = 0; i < pairs.Count; i++)
            {
                _governor.ChargeWork(1L);
                previouslyCached[i] = Cache.ContainsKey(pairs[i].Number);
                if (pairs[i].Offset <= lastOffset)
                {
                    throw new QpdfParseException($"Object stream {streamNumber} offsets must ascend.", null, streamNumber);
                }

                lastOffset = pairs[i].Offset;
                if (pairs[i].Number < 1)
                {
                    throw new QpdfParseException($"Object stream {streamNumber} holds invalid object number {pairs[i].Number}.", null, streamNumber);
                }

                if (pairs[i].Number == streamNumber)
                {
                    throw new QpdfParseException($"Object stream {streamNumber} claims to contain itself.", null, streamNumber);
                }

                checked
                {
                    if ((long)first + pairs[i].Offset >= decoded.Length)
                    {
                        throw new QpdfParseException($"Object stream {streamNumber} entry {pairs[i].Number} points outside the data.", null, pairs[i].Number);
                    }
                }
            }

            for (int i = 0; i < pairs.Count; i++)
            {
                int start = first + pairs[i].Offset;
                int end = i + 1 < pairs.Count ? first + pairs[i + 1].Offset : decoded.Length;
                PdfTokenizer tokenizer = new(decoded.Slice(start, end - start));
                using ParserMemoryScope memory = ParserMemoryScope.Create(_governor);
                _governor.ReserveMemory(128L);
                QpdfObject value;
                try
                {
                    PdfParser parser = new(_limits, _governor, _diagnostics, this)
                    {
                        TolerateObjectErrors = _recovery != QpdfRecoveryMode.Strict,
                    };
                    value = parser.ParseObjectValue(tokenizer, 0, memory);
                    if (!parser.ObjectAborted && tokenizer.TryReadToken(out PdfToken _, false, _governor))
                    {
                        throw new QpdfParseException($"Object stream {streamNumber} entry {pairs[i].Number} has trailing bytes.", null, pairs[i].Number);
                    }
                }
                finally
                {
                    _governor.ReleaseMemory(128L);
                }

                if (Table.Entries.TryGetValue(pairs[i].Number, out XrefEntry target)
                    && target.Kind == XrefEntryKind.Compressed
                    && target.First == streamNumber
                    && !previouslyCached[i])
                {
                    StoreCached(pairs[i].Number, value, memory, false);
                }
            }
        }
        finally
        {
            DecodingStreams.Remove(streamNumber);
        }
    }

    private int ReadObjStmInt(PdfDictionary dictionary, ReadOnlySpan<byte> key, string name, int minimum, int streamNumber)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, key, _governor, _governor.CancellationToken, out QpdfObject raw) || raw is not PdfInteger integer)
        {
            throw new QpdfParseException($"Object stream {streamNumber} has incorrect /{name}.", null, streamNumber);
        }

        if (integer.Value < minimum || integer.Value > int.MaxValue)
        {
            throw new QpdfParseException($"Object stream {streamNumber} has incorrect /{name}.", null, streamNumber);
        }

        return (int)integer.Value;
    }

    private ReadOnlyMemory<byte> DecodeStreamData(PdfStream stream, int streamNumber, ParserMemoryScope scratch)
    {
        FilterPlan stages;
        try
        {
            stages = FilterChain.Resolve(stream.Dictionary, this, _governor, _governor.CancellationToken);
        }
        catch (QpdfParseException ex)
        {
            throw new QpdfParseException($"Object stream {streamNumber} declares invalid filters: {ex.Message}", null, streamNumber);
        }
        using FilterPlan stageOwner = stages;

        if (stages.Count == 0) { return stream.RawMemory; }
        scratch.Reserve(checked((long)stream.RawMemory.Length + 32L));
        return FilterChain.Decode(stream.Data, stages, _limits, _governor, scratch);
    }

    private List<(int Number, int Offset)> ReadObjStmPairs(
        ReadOnlyMemory<byte> decoded, int count, int streamNumber, ParserMemoryScope scratch)
    {
        PdfTokenizer tokenizer = new(decoded);
        if ((long)count * 4L > decoded.Length)
        {
            throw new QpdfParseException($"Object stream {streamNumber} declares {count} entries but holds {decoded.Length} bytes.", null, streamNumber);
        }

        scratch.Reserve(checked((long)count * 8L + 64L));
        List<(int Number, int Offset)> pairs = new(count);
        for (int i = 0; i < count; i++)
        {
            _governor.ChargeWork(1L);
            if (!tokenizer.TryReadToken(out PdfToken numberToken, false, _governor) || numberToken.Kind != PdfTokenKind.Integer
                || !tokenizer.TryReadToken(out PdfToken offsetToken, false, _governor) || offsetToken.Kind != PdfTokenKind.Integer)
            {
                throw new QpdfParseException($"Object stream {streamNumber} header expects integers.", null, streamNumber);
            }

            int number = ParseHeaderInt(numberToken, streamNumber);
            int offset = ParseHeaderInt(offsetToken, streamNumber);
            if (offset < 0)
            {
                throw new QpdfParseException($"Object stream {streamNumber} holds a negative offset.", null, streamNumber);
            }

            pairs.Add((number, offset));
        }

        return pairs;
    }

    private int ParseHeaderInt(PdfToken token, int streamNumber)
    {
        long value = 0L;
        try
        {
            checked
            {
                ReadOnlySpan<byte> bytes = token.Data.Span;
                for (int i = 0; i < bytes.Length; i++)
                {
                    if ((i & 255) == 0) { _governor.ChargeWork(1L); }
                    byte b = bytes[i];
                    if (b < (byte)'0' || b > (byte)'9')
                    {
                        throw new QpdfParseException($"Object stream {streamNumber} header expects integers.", null, streamNumber);
                    }

                    value = (value * 10L) + (b - (byte)'0');
                }
            }
        }
        catch (OverflowException ex)
        {
            throw new QpdfParseException($"Object stream {streamNumber} header integer overflows.", null, streamNumber, ex);
        }

        if (value > int.MaxValue)
        {
            throw new QpdfParseException($"Object stream {streamNumber} header integer overflows.", null, streamNumber);
        }

        return (int)value;
    }
}
