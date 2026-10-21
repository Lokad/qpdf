using System.Buffers.Text;
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
internal sealed class DocumentEngine : IStreamRecoveryResolver, IDisposable
{
    // A cache receipt owns only this parsed subtree. Nested resolver loads have
    // independent receipts. Reads protect values that may outlive replacement.
    private readonly record struct CachedObject(QpdfObject Value, long ReservedBytes, bool Borrowed);

    private ReadOnlyMemory<byte> _data;
    private SelectiveInputData? _selectiveInput;
    private XRefTable? _table;
    private readonly QpdfLimits _limits;
    private readonly ResourceGovernor _governor;
    private readonly DiagnosticBag _diagnostics;
    private readonly QpdfRecoveryMode _recovery;
    private ParserMemoryScope? _metadata;
    private bool _ownsTable;
    private bool _valuesEscape = true;
    private long _reservedInput;
    private bool _hostInput;
    private bool _borrowedStreamData;
    private PdfParser? _idleParser;
    private PdfTokenizer? _idleTokenizer;
    private ParserMemoryScope? _idleMemory;
    internal bool BorrowImmutableStreamData { get; set; }
    private Dictionary<int, CachedObject>? _cache;
    private HashSet<int>? _cachedNulls;
    private HashSet<int>? _decodingStreams;
    private HashSet<int>? _loading;
    private IReadOnlyDictionary<int, QpdfObject>? _inspectionObjects;
    private PdfDictionary? _inspectionTrailer;
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
        => CreateOwned(data, table, limits, governor, diagnostics, recovery, reservedInput, valuesEscape, false, null);

    internal static DocumentEngine CreateOwned(
        ReadOnlyMemory<byte> data, XRefTable table, QpdfLimits limits, ResourceGovernor governor,
        DiagnosticBag diagnostics, QpdfRecoveryMode recovery, long reservedInput, bool valuesEscape,
        bool hostInput, IDisposable? snapshotBorrow)
    {
        table.CheckOwner(governor);
        if (reservedInput != checked(data.Length + GovernedBufferStream.ArrayOverheadBytes))
        {
            throw new InvalidOperationException("The document input receipt does not match its storage.");
        }
        DocumentEngine engine = new(data, table, limits, governor, diagnostics, recovery) { _valuesEscape = valuesEscape };
        try
        {
            if (snapshotBorrow is not null) { engine.Metadata.AdoptHostBorrow(snapshotBorrow); }
            if (hostInput) { engine.Metadata.AdoptHostReserved(reservedInput); }
            else { engine.Metadata.AdoptReserved(reservedInput); }
            engine._reservedInput = reservedInput;
            engine._hostInput = hostInput;
            engine._ownsTable = true;
            return engine;
        }
        catch { engine.Dispose(); throw; }
    }

    internal static DocumentEngine CreateSelectiveOwned(SelectiveInputData input, QpdfLimits limits,
        ResourceGovernor governor, DiagnosticBag diagnostics)
    {
        XRefTable table = input.DetachTable();
        try
        {
            return new DocumentEngine(ReadOnlyMemory<byte>.Empty, table, limits, governor, diagnostics, QpdfRecoveryMode.Strict)
            { _valuesEscape = false, _ownsTable = true, _selectiveInput = input };
        }
        catch { table.Dispose(); input.Dispose(); throw; }
    }

    internal bool HasSelectiveInput => _selectiveInput is not null;
    internal void AcceptSelectiveInput() => _selectiveInput?.Accept();

    private bool TryGetCached(int number, out QpdfObject value)
    {
        if (_cachedNulls is not null && _cachedNulls.Contains(number))
        {
            value = PdfNull.Instance;
            return true;
        }
        if (Cache.TryGetValue(number, out CachedObject cached))
        {
            if (!cached.Borrowed && _valuesEscape)
            {
                long retained = _governor.RetainGraph(cached.ReservedBytes);
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
        if (value is PdfNull && !_valuesEscape)
        {
            // Null is shared and has no subtree receipt. Memoize its number
            // in an admitted set, preserving repair warnings exactly once.
            _cachedNulls ??= ParserContainers.CreateSet<int>(Metadata);
            ParserContainers.AddSetItem(Metadata, _cachedNulls, number, sizeof(int));
            parsed.Discard();
            if (replacing)
            {
                Cache.Remove(number);
                if (!previous.Borrowed)
                {
                    _unborrowedBytes = checked(_unborrowedBytes - previous.ReservedBytes);
                    _governor.ReleaseMemory(previous.ReservedBytes);
                }
            }
            return;
        }
        if (!replacing) { ParserContainers.EnsureEntryCapacity(Metadata, Cache, 24L, Table.Entries.Count); }
        _governor.CheckCancellation();
        long discarded = replacing && !previous.Borrowed ? previous.ReservedBytes : 0L;
        long nextUnborrowed = checked(_unborrowedBytes - discarded + (borrowed ? 0L : parsed.ReservedBytes));
        long reservedBytes = parsed.ReservedBytes;
        if (borrowed) { parsed.RetainGraph(); }
        Cache[number] = new CachedObject(value, reservedBytes, borrowed);
        _cachedNulls?.Remove(number);
        if (!borrowed) { parsed.Retain(); }
        _unborrowedBytes = nextUnborrowed;
        _governor.ReleaseMemory(discarded);
    }

    // A complete copy job may borrow immutable scalars after its private
    // source closes. Transfer those exclusive values to the job's lifetime;
    // Reader maps retire at document closure. Input bytes also transfer if
    // copied stream headers still borrow slices of that immutable backing.
    internal void TransferExclusiveValuesTo(ParserMemoryScope target)
    {
        target.CheckOwner(_governor);
        if (_valuesEscape || !_ownsTable)
        { throw new InvalidOperationException("Only an exclusive document can transfer its values."); }
        Table.TransferExclusiveValuesTo(target);
        if (_borrowedStreamData && _reservedInput != 0L)
        {
            if (_hostInput) { Metadata.TransferHostTo(target, _reservedInput); }
            else { Metadata.TransferTo(target, _reservedInput); }
            _reservedInput = 0L;
            _hostInput = false;
        }
        target.AdoptReserved(_unborrowedBytes);
        _unborrowedBytes = 0L;
    }

    public void Dispose()
    {
        if (_cache is null) { return; }
        // Drop backing storage before releasing metadata. Borrowed graphs have
        // escaped this owner; their admission and bytes remain independent.
        _cache = null;
        _cachedNulls = null;
        _loading = null;
        _decodingStreams = null;
        _inspectionObjects = null;
        _inspectionTrailer = null;
        _idleParser = null;
        _idleTokenizer = null;
        _idleMemory?.Dispose();
        _idleMemory = null;
        _data = default;
        _selectiveInput?.Dispose();
        _selectiveInput = null;
        XRefTable? table = _table;
        _table = null;
        ParserMemoryScope? metadata = _metadata;
        _metadata = null;
        if (_ownsTable) { table?.Dispose(); }
        _governor.ReleaseMemory(_unborrowedBytes);
        _unborrowedBytes = 0L;
        metadata?.Dispose();
    }

    internal PdfDictionary Trailer => _inspectionTrailer ?? Table.Trailer;

    internal IReadOnlyList<PdfDictionary> Trailers => Table.Trailers;

    internal DiagnosticBag Diagnostics => _diagnostics;

    internal IReadOnlyDictionary<int, XrefEntry> XRefEntries => Table.Entries;

    internal ReadOnlyMemory<byte> Data => _data;

    long IStreamRecoveryResolver.GetStreamRecoveryEnd(long streamOffset)
    {
        long end = _data.Length;
        foreach (XrefEntry entry in Table.Entries.Values)
        {
            _governor.NoteOperation();
            if (entry.Kind == XrefEntryKind.Uncompressed && entry.First > streamOffset && entry.First < end)
            {
                end = entry.First;
            }
        }
        if (Table.XrefOffset > streamOffset && Table.XrefOffset < end) { end = Table.XrefOffset; }
        return end;
    }

    // Checking borrows input bytes but owns its short-lived parsing attempt.
    // Reuse the reader/parser rather than searching binary payloads for endobj.
    // Keeping this observation scoped avoids enlarging every ordinary cache row.
    internal PdfObjectExtent ReadPhysicalExtent(int number, QpdfLimits limits,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        _ = Cache;
        cancellationToken.ThrowIfCancellationRequested();
        governor.CheckCancellation();
        if (!Table.Entries.TryGetValue(number, out XrefEntry entry)
            || entry.Kind != XrefEntryKind.Uncompressed)
        {
            throw new QpdfParseException("Linearization needs an uncompressed physical object.", null, number);
        }
        using ParserMemoryScope attempt = ParserMemoryScope.Create(governor);
        attempt.Reserve(128L);
        PdfTokenizer tokenizer = new(_data, cancellationToken);
        tokenizer.SetPosition(entry.First);
        PdfParser parser = new(limits, governor, _diagnostics, this) { SuppressRepairDiagnostics = true };
        (int found, int generation, _) = parser.ParseIndirectObject(tokenizer, 0, attempt);
        if (found != number || generation != entry.Second)
        {
            throw new QpdfParseException("Physical object header contradicts its xref entry.", entry.First, number);
        }
        long end = tokenizer.Position;
        long whitespaceEnd = end;
        ReadOnlySpan<byte> bytes = _data.Span;
        while (whitespaceEnd < bytes.Length && bytes[(int)whitespaceEnd] is 9 or 10 or 11 or 12 or 13 or 32)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.CheckCancellation();
            whitespaceEnd++;
        }
        return new(entry.First, end, whitespaceEnd);
    }

    // Inspection edits share this resolver but never change physical xref offsets.
    // Their scoped owner detaches the graph before releasing its storage.
    internal void SetInspectionObjects(IReadOnlyDictionary<int, QpdfObject>? objects)
    {
        _ = Cache;
        _inspectionObjects = objects;
        _inspectionTrailer = null;
    }

    internal void SetInspectionTrailer(PdfDictionary trailer)
    {
        _ = Cache;
        _inspectionTrailer = trailer;
    }

    internal IEnumerable<int> InspectionObjectNumbers => _inspectionObjects?.Keys ?? [];

    bool IIndirectResolver.TryResolve(PdfReference reference, out QpdfObject? value)
        => TryResolve(reference, false, out value);

    internal PdfArray? ResolveArray(PdfReference reference)
        => TryResolve(reference, true, out QpdfObject? value) ? value as PdfArray : null;

    private bool TryResolve(PdfReference reference, bool arrayOnly, out QpdfObject? value)
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

        if (arrayOnly && entry.Kind == XrefEntryKind.Uncompressed && !StartsWithArray(reference, entry.First))
        { value = null; return false; }
        value = Load(reference.ObjectNumber, entry);
        return true;
    }

    // Inspect only the header and first value token. A stream dictionary stops
    // here, so JSON can report its reference without copying the stream payload.
    // Arrays and compressed members still use the ordinary parser/cache owner.
    private bool StartsWithArray(PdfReference reference, long offset)
    {
        _governor.CheckCancellation(); _governor.ReserveMemory(128L);
        PdfTokenizer? tokenizer = null;
        try
        {
            ReadOnlyMemory<byte> data = _selectiveInput is null ? _data : _selectiveInput.At(offset);
            tokenizer = _idleTokenizer ?? new(data); _idleTokenizer = null;
            if (_selectiveInput is null) { tokenizer.ResetForObject(offset); }
            else { tokenizer.ResetForMember(data); }
            // Uncertain/mismatched headers go through full parsing for its
            // existing validation and recovery, rather than being hidden.
            if (!tokenizer.TryReadToken(out PdfToken number, false, _governor) || number.Kind != PdfTokenKind.Integer
                || !Utf8Parser.TryParse(number.Data.Span, out int objectNumber, out int numberBytes)
                || numberBytes != number.Data.Length || objectNumber != reference.ObjectNumber
                || !tokenizer.TryReadToken(out PdfToken generation, false, _governor) || generation.Kind != PdfTokenKind.Integer
                || !Utf8Parser.TryParse(generation.Data.Span, out int generationNumber, out int generationBytes)
                || generationBytes != generation.Data.Length || generationNumber != reference.Generation
                || !tokenizer.TryReadToken(out PdfToken marker, false, _governor) || marker.Kind != PdfTokenKind.Object)
            { return true; }
            return !tokenizer.TryReadToken(out PdfToken first, false, _governor) || first.Kind == PdfTokenKind.ArrayOpen;
        }
        finally
        {
            if (tokenizer is not null) { _idleTokenizer = tokenizer; }
            _governor.ReleaseMemory(128L);
        }
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

            _governor.NoteOperation();
            ParserMemoryScope memory = _idleMemory ?? ParserMemoryScope.Create(_governor);
            _idleMemory = null;
            try
            {
                ReadOnlyMemory<byte> data = _selectiveInput is null ? _data : _selectiveInput.At(entry.First);
                PdfTokenizer tokenizer = _idleTokenizer ?? new(data);
                _idleTokenizer = null;
                if (_selectiveInput is null) { tokenizer.ResetForObject(entry.First); }
                else { tokenizer.ResetForMember(data); }
                _governor.ReserveMemory(128L);
                int foundNumber;
                int foundGeneration;
                QpdfObject value;
                // A nested stream-length load needs a separate active parser.
                PdfParser? parser = _idleParser;
                _idleParser = null;
                try
                {
                    if (parser is null)
                    {
                        bool reconstructed = Table.XrefOffset < 0L;
                        QpdfLimits parserLimits = reconstructed
                            ? _limits with { MaxContainerSize = Math.Min(_limits.MaxContainerSize, _limits.MaxContainerSizeDamaged) }
                            : _limits;
                        parser = new(parserLimits, _governor, _diagnostics, this)
                        {
                            TolerateObjectErrors = _recovery != QpdfRecoveryMode.Strict,
                            SanityChecks = reconstructed,
                        };
                    }
                    parser.BorrowImmutableStreamData = BorrowImmutableStreamData && !_valuesEscape && _ownsTable;
                    (foundNumber, foundGeneration, value) = parser.ParseIndirectObject(tokenizer, 0, memory);
                    _borrowedStreamData |= parser.BorrowedStreamData;
                }
                finally
                {
                    // The parser restores its attempt scope even on failure. Keep
                    // one idle instance; any nested instance can now retire.
                    _idleParser = parser;
                    _idleTokenizer = tokenizer;
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
                // Cache acceptance has transferred the parsed value's storage.
                // Discard a rejected attempt before keeping the empty owner.
                memory.Discard();
                if (_cache is not null && Loading.Count == 1)
                {
                    _idleMemory = memory;
                }
                else
                {
                    // A nested load must not leave another idle allowance live
                    // while its parent still owns active scratch.
                    memory.Dispose();
                }
            }
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
            bool[] accept = new bool[pairs.Count];
            long lastOffset = -1L;
            int eligible = 0;
            for (int i = 0; i < pairs.Count; i++)
            {
                _governor.NoteOperation();
                bool previouslyCached = Cache.ContainsKey(pairs[i].Number)
                    || (_cachedNulls is not null && _cachedNulls.Contains(pairs[i].Number));
                if (!previouslyCached && Table.Entries.TryGetValue(pairs[i].Number, out XrefEntry candidate)
                    && candidate.Kind == XrefEntryKind.Compressed && candidate.First == streamNumber)
                { accept[i] = true; eligible++; }
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

            PdfParser? memberParser = null;
            PdfTokenizer? memberTokenizer = null;
            // Accepted members transfer their storage to the cache; all other
            // storage retires before advancing. Only this decoder owns the
            // scratch scope, including when a member triggers a nested load.
            using ParserMemoryScope memory = ParserMemoryScope.Create(_governor);
            bool hinted = false;
            for (int i = 0; i < pairs.Count; i++)
            {
                int start = first + pairs[i].Offset;
                int end = i + 1 < pairs.Count ? first + pairs[i + 1].Offset : decoded.Length;
                _governor.ReserveMemory(128L);
                QpdfObject value;
                try
                {
                    PdfTokenizer tokenizer = memberTokenizer ??= new(ReadOnlyMemory<byte>.Empty);
                    tokenizer.ResetForMember(decoded.Slice(start, end - start));
                    // ParseObjectValue resets member repair state and restores
                    // its private owner, so this decoder can reuse one parser.
                    // Nested decoders keep separate instances; admission remains
                    // before each member and cache acceptance keeps its own scope.
                    PdfParser parser = memberParser ??= new(_limits, _governor, _diagnostics, this)
                    {
                        TolerateObjectErrors = _recovery != QpdfRecoveryMode.Strict,
                        AllowStreams = false,
                    };
                    value = parser.ParseObjectValue(tokenizer, 0, memory);
                    // Native consumes one value from a compressed member. The
                    // strict reader additionally rejects trailing bytes; neither
                    // policy treats a following stream keyword as file data.
                    if (_recovery == QpdfRecoveryMode.Strict && !parser.ObjectAborted
                        && tokenizer.TryReadToken(out PdfToken _, false, _governor))
                    {
                        throw new QpdfParseException($"Object stream {streamNumber} entry {pairs[i].Number} has trailing bytes.", null, pairs[i].Number);
                    }
                }
                finally
                {
                    _governor.ReleaseMemory(128L);
                }

                // The validated header already established eligibility against
                // this immutable xref table and the pre-pass cache snapshot.
                if (accept[i])
                {
                    if (!hinted && (value is not PdfNull || _valuesEscape))
                    {
                        // This decoder visits the whole validated group. Hint
                        // only after a member can be accepted, so rejected and
                        // unmatched attempts do not grow the cache speculatively.
                        int capacity = (int)Math.Min(Table.Entries.Count, (long)Cache.Count + eligible);
                        ParserContainers.HintEntryCapacity(Metadata, Cache, capacity, 24L, _governor, true);
                        hinted = true;
                    }
                    StoreCached(pairs[i].Number, value, memory, false);
                }
                memory.Discard();
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
        // Filters borrow immutable input. Full payload arrays already have an
        // owner; only slices need a materialized array under this scratch scope.
        byte[] encoded = stream.ArrayData(scratch, _governor, _governor.CancellationToken);
        return FilterChain.Decode(encoded, stages, _limits, _governor, scratch);
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
            _governor.NoteOperation();
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
                    if ((i & 255) == 0) { _governor.NoteOperation(); }
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
