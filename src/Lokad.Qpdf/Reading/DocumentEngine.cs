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
internal sealed class DocumentEngine : IIndirectResolver
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly XRefTable _table;
    private readonly QpdfLimits _limits;
    private readonly ResourceGovernor _governor;
    private readonly DiagnosticBag _diagnostics;
    private readonly QpdfRecoveryMode _recovery;
    private readonly Dictionary<int, QpdfObject> _cache = new();
    private readonly HashSet<int> _decodingStreams = new();
    private readonly HashSet<int> _loading = new();

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
    }

    internal PdfDictionary Trailer => _table.Trailer;

    internal IReadOnlyList<PdfDictionary> Trailers => _table.Trailers;

    internal DiagnosticBag Diagnostics => _diagnostics;

    internal IReadOnlyDictionary<int, XrefEntry> XRefEntries => _table.Entries;

    internal ReadOnlyMemory<byte> Data => _data;

    bool IIndirectResolver.TryResolve(PdfReference reference, out QpdfObject? value)
    {
        if (reference is null)
        {
            throw new ArgumentNullException(nameof(reference));
        }

        if (reference.ObjectNumber == 0 && !_table.Entries.ContainsKey(0))
        {
            value = PdfNull.Instance;
            return true;
        }

        if (!_table.Entries.TryGetValue(reference.ObjectNumber, out XrefEntry entry))
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

        if (_cache.TryGetValue(reference.ObjectNumber, out QpdfObject? cached))
        {
            value = cached;
            return true;
        }

        value = Load(reference.ObjectNumber, entry);
        return true;
    }

    internal QpdfObject Load(int number)
    {
        if (_cache.TryGetValue(number, out QpdfObject? cached) && cached is not null)
        {
            return cached;
        }

        if (number == 0 && !_table.Entries.ContainsKey(0))
        {
            return PdfNull.Instance;
        }

        if (!_table.Entries.TryGetValue(number, out XrefEntry entry))
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
        if (_loading.Count >= _limits.MaxNestingDepth)
        {
            throw new QpdfParseException($"Object load depth exceeds {_limits.MaxNestingDepth}.", null, number);
        }

        if (!_loading.Add(number))
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
                bool reconstructed = _table.XrefOffset < 0L;
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

            _cache[number] = value;
            memory.Retain();
            return value;
        }
        finally
        {
            _loading.Remove(number);
        }
    }

    private QpdfObject LoadCompressed(int number, int streamNumber, int index)
    {
        if (_cache.TryGetValue(number, out QpdfObject? cached) && cached is not null)
        {
            return cached;
        }

        DecodeObjStm(streamNumber);
        if (_cache.TryGetValue(number, out QpdfObject? resolved) && resolved is not null)
        {
            return resolved;
        }

        throw new QpdfParseException($"Compressed object {number} is missing from object stream {streamNumber}.", null, number);
    }

    private void DecodeObjStm(int streamNumber)
    {
        if (!_decodingStreams.Add(streamNumber))
        {
            throw new QpdfParseException($"Object stream cycle at object {streamNumber}.", null, streamNumber);
        }

        try
        {
            if (!_table.Entries.TryGetValue(streamNumber, out XrefEntry entry)
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
            PdfName typeKey = PdfName.FromAscii("Type");
            if (!dictionary.TryGet(typeKey, out QpdfObject typeRaw) || typeRaw is not PdfName typeName || typeName.DisplayText != "ObjStm")
            {
                throw new QpdfParseException($"Object {streamNumber} must declare /Type /ObjStm.", null, streamNumber);
            }

            // the header pairs carried by this stream, matched against the
            // cross-reference table, matching the pinned reference, whose reader
            // matching the pinned reference, whose reader carries no Extends handling
            // (even a self-reference like the objstm-extends.pdf fixture resolves).
            // The key itself stays opaque data for the copy engine to preserve.

            int count = ReadObjStmInt(dictionary, "N", 1, streamNumber);
            int first = ReadObjStmInt(dictionary, "First", 0, streamNumber);
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
                previouslyCached[i] = _cache.ContainsKey(pairs[i].Number);
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

                if (_table.Entries.TryGetValue(pairs[i].Number, out XrefEntry target)
                    && target.Kind == XrefEntryKind.Compressed
                    && target.First == streamNumber
                    && !previouslyCached[i])
                {
                    _cache[pairs[i].Number] = value;
                    memory.Retain();
                }
            }
        }
        finally
        {
            _decodingStreams.Remove(streamNumber);
        }
    }

    private static int ReadObjStmInt(PdfDictionary dictionary, string name, int minimum, int streamNumber)
    {
        PdfName key = PdfName.FromAscii(name);
        if (!dictionary.TryGet(key, out QpdfObject raw) || raw is not PdfInteger integer)
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
        IReadOnlyList<FilterStage> stages;
        try
        {
            stages = FilterChain.Resolve(stream.Dictionary, this);
        }
        catch (QpdfParseException ex)
        {
            throw new QpdfParseException($"Object stream {streamNumber} declares invalid filters: {ex.Message}", null, streamNumber);
        }

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
