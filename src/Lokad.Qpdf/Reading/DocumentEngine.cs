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
    private readonly PdfParser _parser;
    private readonly Dictionary<int, QpdfObject> _cache = new();
    private readonly HashSet<int> _decodingStreams = new();
    private readonly HashSet<int> _loading = new();

    internal DocumentEngine(
        ReadOnlyMemory<byte> data,
        XRefTable table,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics)
    {
        _data = data;
        _table = table;
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _parser = new PdfParser(limits, governor, diagnostics, this);
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

        if (_cache.TryGetValue(reference.ObjectNumber, out QpdfObject? cached))
        {
            value = cached;
            return true;
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
            PdfTokenizer tokenizer = new(_data);
            tokenizer.SetPosition(entry.First);
            (int foundNumber, int foundGeneration, QpdfObject value) = _parser.ParseIndirectObject(tokenizer, 0);
            if (foundNumber != number || foundGeneration != entry.Second)
            {
                throw new QpdfParseException($"Object header contradicts its xref entry for object {number}.", entry.First, number);
            }

            _cache[number] = value;
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

            QpdfObject raw = Load(streamNumber, entry);
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

            PdfName extendsKey = PdfName.FromAscii("Extends");
            if (dictionary.TryGet(extendsKey, out QpdfObject extends) && extends is not PdfNull)
            {
                throw new QpdfParseException("Chained /Extends object streams arrive in work item 10.", null, streamNumber);
            }

            int count = ReadObjStmInt(dictionary, "N", 1, streamNumber);
            int first = ReadObjStmInt(dictionary, "First", 0, streamNumber);
            byte[] decoded = DecodeStreamData(stream, streamNumber);
            if (first >= decoded.Length && count > 0)
            {
                throw new QpdfParseException($"Object stream {streamNumber} has invalid /First entry.", null, streamNumber);
            }

            List<(int Number, int Offset)> pairs = ReadObjStmPairs(decoded, count, streamNumber);
            long lastOffset = -1L;
            for (int i = 0; i < pairs.Count; i++)
            {
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
                PdfTokenizer tokenizer = new(decoded.AsMemory(start, end - start));
                QpdfObject value = _parser.ParseValue(tokenizer, 0);
                tokenizer.SkipWhitespaceAndComments();
                if (tokenizer.TryReadToken(out PdfToken _))
                {
                    throw new QpdfParseException($"Object stream {streamNumber} entry {pairs[i].Number} has trailing bytes.", null, pairs[i].Number);
                }

                if (_table.Entries.TryGetValue(pairs[i].Number, out XrefEntry target)
                    && target.Kind == XrefEntryKind.Compressed
                    && target.First == streamNumber)
                {
                    _cache[pairs[i].Number] = value;
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

    private byte[] DecodeStreamData(PdfStream stream, int streamNumber)
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

        return FilterChain.Decode(stream.Data, stages, _limits, _governor);
    }

    private List<(int Number, int Offset)> ReadObjStmPairs(byte[] decoded, int count, int streamNumber)
    {
        PdfTokenizer tokenizer = new(decoded);
        List<(int Number, int Offset)> pairs = new(count);
        for (int i = 0; i < count; i++)
        {
            _governor.ChargeWork(1L);
            if (!tokenizer.TryReadToken(out PdfToken numberToken) || numberToken.Kind != PdfTokenKind.Integer
                || !tokenizer.TryReadToken(out PdfToken offsetToken) || offsetToken.Kind != PdfTokenKind.Integer)
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

    private static int ParseHeaderInt(PdfToken token, int streamNumber)
    {
        long value = 0L;
        try
        {
            checked
            {
                foreach (byte b in token.Data.Span)
                {
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
