using System.Buffers.Text;
using System.Globalization;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Syntax;

/// <summary>Resolves indirect references during parsing (wired to xref in 05b).</summary>
internal interface IIndirectResolver
{
    /// <summary>Tries to resolve a reference to its object.</summary>
    bool TryResolve(PdfReference reference, out QpdfObject? value);
}

/// <summary>
/// PDF object parser with explicit strict and repair policies. Containers recurse with an explicit
/// depth budget; every value charges work and every copy reserves memory
/// before allocating. Duplicate dictionary keys resolve last-wins with a
/// recorded warning, matching the upstream reference behavior.
/// </summary>
internal sealed class PdfParser
{
    private readonly QpdfLimits _limits;
    private readonly ResourceGovernor _governor;
    private readonly DiagnosticBag _diagnostics;
    private readonly IIndirectResolver? _resolver;

    internal PdfParser(QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, IIndirectResolver? resolver)
    {
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        _limits = limits;
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _resolver = resolver;
    }

    /// <summary>Allows object syntax repair without relaxing host resource ceilings.</summary>
    internal bool TolerateObjectErrors { get; set; }

    /// <summary>Budgeted errors encountered in the current object.</summary>
    internal long ToleratedErrors { get; private set; }

    internal bool ObjectAborted { get; private set; }

    internal bool SanityChecks { get; set; }

    internal bool SuppressRepairDiagnostics { get; set; }

    private int _nearbyErrors;
    private int _goodTokens;

    internal void BeginTolerantObject()
    {
        ObjectAborted = false;
        ToleratedErrors = 0L;
        _nearbyErrors = 0;
        _goodTokens = 0;
    }

    private sealed class RepairAbort : Exception;

    private void WarnRepair(string detail, long offset)
    {
        if (SuppressRepairDiagnostics)
        {
            // Trial damage is summarized by RecoveryEngaged. Loaded objects
            // report their own repairs, avoiding repeated scan diagnostics.
            return;
        }

        _diagnostics.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning,
            $"Object syntax repair at offset {offset.ToString(CultureInfo.InvariantCulture)}: {detail}");
    }

    private void CountToleratedError(long offset)
    {
        ToleratedErrors = checked(ToleratedErrors + 1L);
        if (_limits.MaxParserErrors != 0L && ToleratedErrors >= _limits.MaxParserErrors)
        {
            WarnRepair("limits error(parser-max-errors): too many errors; treating object as null.", offset);
            throw new RepairAbort();
        }

        // The incoming token was counted optimistically. Four intervening good
        // tokens break the proximity cluster; they do not reset the total budget.
        _nearbyErrors = _goodTokens > 4 ? 1 : _nearbyErrors + 1;
        _goodTokens = 0;
        if (_nearbyErrors > 5)
        {
            WarnRepair("Too many errors in close proximity; treating object as null.", offset);
            throw new RepairAbort();
        }
    }

    private bool TryReadValueToken(PdfTokenizer tokenizer, out PdfToken token)
    {
        _governor.ChargeWork(1L);
        bool found = tokenizer.TryReadToken(out token, TolerateObjectErrors, _governor);
        if (found && TolerateObjectErrors)
        {
            _goodTokens = Math.Min(5, _goodTokens + 1);
        }

        return found;
    }

    private void UnterminatedContainer(string kind, long offset)
    {
        if (!TolerateObjectErrors)
        {
            throw new QpdfParseException($"Unterminated {kind}.", offset, null);
        }

        WarnRepair("Unterminated container; treating object as null.", offset);
        throw new RepairAbort();
    }

    /// <summary>Parses one object-stream member with independent error state.</summary>
    internal QpdfObject ParseObjectValue(PdfTokenizer tokenizer, int depth)
    {
        BeginTolerantObject();
        try
        {
            return ParseValue(tokenizer, depth);
        }
        catch (RepairAbort) when (TolerateObjectErrors)
        {
            ObjectAborted = true;
            return PdfNull.Instance;
        }
    }

    internal QpdfObject ParseValue(PdfTokenizer tokenizer, int depth)
    {
        if (tokenizer is null)
        {
            throw new ArgumentNullException(nameof(tokenizer));
        }

        if (depth > _limits.MaxNestingDepth)
        {
            throw new QpdfParseException($"Nesting depth exceeds {_limits.MaxNestingDepth}.", tokenizer.Position, null);
        }

        if (!TryReadValueToken(tokenizer, out PdfToken token))
        {
            throw new QpdfParseException("Unexpected end of input while parsing a value.", tokenizer.Position, null);
        }

        return ParseToken(tokenizer, token, depth);
    }

    private QpdfObject ParseToken(PdfTokenizer tokenizer, PdfToken token, int depth)
    {
        if (depth > _limits.MaxNestingDepth)
        {
            throw new QpdfParseException($"Nesting depth exceeds {_limits.MaxNestingDepth}.", token.Offset, null);
        }

        switch (token.Kind)
        {
            case PdfTokenKind.Null:
                return PdfNull.Instance;
            case PdfTokenKind.True:
                return PdfBoolean.True;
            case PdfTokenKind.False:
                return PdfBoolean.False;
            case PdfTokenKind.Integer:
                try
                {
                    return ParseIntegerOrReference(tokenizer, token);
                }
                catch (QpdfParseException) when (TolerateObjectErrors)
                {
                    // Numeric conversion failure invalidates the whole object in
                    // the reference, independently of the semantic error budget.
                    WarnRepair("Numeric conversion failed; treating object as null.", token.Offset);
                    throw new RepairAbort();
                }
            case PdfTokenKind.Real:
                return ParseReal(token);
            case PdfTokenKind.Name:
                return ParseName(token);
            case PdfTokenKind.LiteralString:
                return ParseByteString(token, false);
            case PdfTokenKind.HexString:
                return ParseByteString(token, true);
            case PdfTokenKind.ArrayOpen:
                return ParseArray(tokenizer, depth);
            case PdfTokenKind.DictionaryOpen:
                return ParseDictionaryOrStream(tokenizer, depth, token.Offset);
            case PdfTokenKind.EndObject when TolerateObjectErrors && depth == 0:
                tokenizer.SetPosition(token.Offset);
                WarnRepair("Empty object treated as null.", token.Offset);
                return PdfNull.Instance;
            default:
                if (!TolerateObjectErrors)
                {
                    throw new QpdfParseException($"Unexpected token {token.Kind} while parsing a value.", token.Offset, null);
                }

                if (SanityChecks && token.Kind is PdfTokenKind.EndObject or PdfTokenKind.EndStream
                    or PdfTokenKind.ArrayClose or PdfTokenKind.DictionaryClose)
                {
                    WarnRepair("Unexpected terminator during xref trial parsing; treating object as null.", token.Offset);
                    throw new RepairAbort();
                }

                bool word = !SanityChecks && token.Kind is not (PdfTokenKind.ArrayClose or PdfTokenKind.DictionaryClose or PdfTokenKind.BadToken);
                WarnRepair(word ? "Unknown token treated as string." : "Unexpected token treated as null.", token.Offset);
                if (depth > 0)
                {
                    CountToleratedError(token.Offset);
                }

                if (!word)
                {
                    return PdfNull.Instance;
                }

                _governor.ReserveMemory(checked((token.Data.Length * 2L) + 96L));
                PdfByteString repaired = new(token.Data.ToArray(), hex: false);
                _governor.ReleaseMemory(checked(token.Data.Length + 32L));
                return repaired;
        }
    }

    internal (int Number, int Generation, QpdfObject Value) ParseIndirectObject(PdfTokenizer tokenizer, int depth)
    {
        if (tokenizer is null)
        {
            throw new ArgumentNullException(nameof(tokenizer));
        }

        BeginTolerantObject();
        if (!tokenizer.TryReadToken(out PdfToken numberToken, false, _governor) || numberToken.Kind != PdfTokenKind.Integer)
        {
            throw new QpdfParseException("Expected an object number.", tokenizer.Position, null);
        }

        if (!tokenizer.TryReadToken(out PdfToken generationToken, false, _governor) || generationToken.Kind != PdfTokenKind.Integer)
        {
            throw new QpdfParseException("Expected an object generation.", numberToken.Offset, null);
        }

        if (!tokenizer.TryReadToken(out PdfToken objToken, false, _governor) || objToken.Kind != PdfTokenKind.Object)
        {
            throw new QpdfParseException("Expected 'obj'.", numberToken.Offset, null);
        }

        int number = ParseObjectNumber(numberToken);
        int generation = ParseGeneration(generationToken);
        QpdfObject value;
        try
        {
            value = ParseValue(tokenizer, depth);
        }
        catch (RepairAbort) when (TolerateObjectErrors)
        {
            ObjectAborted = true;
            return (number, generation, PdfNull.Instance);
        }

        long beforeEnd = tokenizer.Position;
        if (!tokenizer.TryReadToken(out PdfToken endToken, TolerateObjectErrors, _governor)
            || endToken.Kind != PdfTokenKind.EndObject)
        {
            if (!TolerateObjectErrors)
            {
                throw new QpdfParseException("Expected 'endobj'.", numberToken.Offset, number);
            }

            tokenizer.SetPosition(beforeEnd);
            WarnRepair("Missing endobj after the object value.", beforeEnd);
        }

        return (number, generation, value);
    }

    private QpdfObject ParseIntegerOrReference(PdfTokenizer tokenizer, PdfToken first)
    {
        long saved = tokenizer.Position;
        bool reference = false;
        PdfToken second = default;
        try
        {
            reference = tokenizer.TryReadToken(out second, TolerateObjectErrors, _governor)
                && second.Kind == PdfTokenKind.Integer
                && tokenizer.TryReadToken(out PdfToken third, TolerateObjectErrors, _governor)
                && third.Kind == PdfTokenKind.Reference;
        }
        catch (QpdfParseException)
        {
            // Lookahead must not turn a valid integer into the following token's
            // error. Leave that token for the owning container to read once.
        }

        if (reference)
        {
            if (TolerateObjectErrors)
            {
                _goodTokens = Math.Min(5, _goodTokens + 2);
                long number = ParseIntegerText(first);
                long generation = ParseIntegerText(second);
                if (number < int.MinValue || number > int.MaxValue
                    || generation < int.MinValue || generation > int.MaxValue)
                {
                    throw new QpdfParseException("Indirect reference exceeds the integer range.", first.Offset, null);
                }

                if (number < 1L || generation < 0L || generation >= 65535L)
                {
                    WarnRepair("Invalid indirect reference treated as null.", first.Offset);
                    CountToleratedError(first.Offset);
                    return PdfNull.Instance;
                }

                return new PdfReference((int)number, (int)generation);
            }

            return new PdfReference(ParseObjectNumber(first), ParseGeneration(second));
        }

        tokenizer.SetPosition(saved);
        return ParseInteger(first);
    }

    private int ParseObjectNumber(PdfToken token)
    {
        long value = ParseIntegerText(token);
        if (value < 0 || value > int.MaxValue)
        {
            throw new QpdfParseException($"Object number {value} is out of range.", token.Offset, null);
        }

        return (int)value;
    }

    private int ParseGeneration(PdfToken token)
    {
        long value = ParseIntegerText(token);
        if (value < 0 || value > 65535)
        {
            throw new QpdfParseException($"Generation {value} is out of range.", token.Offset, null);
        }

        return (int)value;
    }

    private PdfInteger ParseInteger(PdfToken token)
    {
        long value = ParseIntegerText(token);
        // Keep the original token for lossless writing. The numeric scan is
        // governed even for arbitrarily many leading zeros; admit the UTF-16
        // string and scalar before materializing them.
        _governor.ReserveMemory(checked((token.Data.Length * 2L) + 64L));
        try
        {
            return new PdfInteger(CopyNumericText(token.Data), value);
        }
        catch
        {
            _governor.ReleaseMemory(checked((token.Data.Length * 2L) + 64L));
            throw;
        }
    }

    private long ParseIntegerText(PdfToken token)
    {
        ReadOnlySpan<byte> span = token.Data.Span;
        int i = 0;
        bool negative = false;
        if (span[0] == (byte)'+' || span[0] == (byte)'-')
        {
            negative = span[0] == (byte)'-';
            i = 1;
        }

        // Accumulate magnitude against the sign-specific bound so the extra
        // unit in the negative range (long.MinValue) remains representable.
        ulong limit = negative ? 1UL << 63 : (ulong)long.MaxValue;
        ulong magnitude = 0UL;
        for (; i < span.Length; i++)
        {
            if ((i & 4095) == 0)
            {
                _governor.ChargeWork(1L);
                _governor.CheckCancellation();
            }

            uint digit = (uint)(span[i] - (byte)'0');
            if (digit > 9U)
            {
                throw new QpdfParseException("Malformed integer token.", token.Offset, null);
            }

            if (magnitude > (limit - digit) / 10UL)
            {
                throw new QpdfParseException("Integer token overflows 64 bits.", token.Offset, null);
            }

            magnitude = (magnitude * 10UL) + digit;
        }

        return negative
            ? magnitude == 1UL << 63 ? long.MinValue : -(long)magnitude
            : (long)magnitude;
    }

    private PdfReal ParseReal(PdfToken token)
    {
        long retained = checked((token.Data.Length * 2L) + 64L);
        _governor.ReserveMemory(retained);
        try
        {
            double value = PdfRealConversion.Convert(token.Data.Span, token.Offset, _governor);
            return new PdfReal(CopyNumericText(token.Data), value);
        }
        catch
        {
            _governor.ReleaseMemory(retained);
            throw;
        }
    }

    private QpdfObject ParseName(PdfToken token)
    {
        // Unescaping may overlap a full-size array, trimmed result and immutable
        // object's private copy. Admit the conservative overlap before any copy.
        long admitted = checked((token.Data.Length * 3L) + 128L);
        _governor.ReserveMemory(admitted);
        try
        {
            byte[] raw = UnescapeName(token.Data, token.Offset);
            PdfName name = new(raw);
            _governor.ReleaseMemory(admitted - checked(raw.Length + 64L));
            return name;
        }
        catch
        {
            _governor.ReleaseMemory(admitted);
            throw;
        }
    }

    private QpdfObject ParseByteString(PdfToken token, bool hex)
    {
        long admitted = checked((token.Data.Length * 3L) + 128L);
        _governor.ReserveMemory(admitted);
        try
        {
            byte[] raw = hex ? DecodeHex(token.Data, token.Offset) : UnescapeLiteral(token.Data, token.Offset);
            PdfByteString value = new(raw, hex);
            _governor.ReleaseMemory(admitted - checked(raw.Length + 64L));
            return value;
        }
        catch
        {
            _governor.ReleaseMemory(admitted);
            throw;
        }
    }

    private QpdfObject ParseArray(PdfTokenizer tokenizer, int depth)
    {
        _governor.ReserveMemory(32L);
        List<QpdfObject> items = new();
        while (true)
        {
            if (!TryReadValueToken(tokenizer, out PdfToken token))
            {
                UnterminatedContainer("array", tokenizer.Position);
            }

            if (token.Kind == PdfTokenKind.ArrayClose)
            {
                return new PdfArray(items);
            }

            if (items.Count >= _limits.MaxContainerSize)
            {
                throw new QpdfParseException($"Array holds more than {_limits.MaxContainerSize} top-level items.", token.Offset, null);
            }

            _governor.ReserveMemory(16L);
            items.Add(ParseToken(tokenizer, token, depth + 1));
        }
    }

    private QpdfObject ParseDictionaryOrStream(PdfTokenizer tokenizer, int depth, long offset)
    {
        _governor.ReserveMemory(64L);
        Dictionary<PdfName, QpdfObject> entries = new();
        List<QpdfObject>? missingKeys = null;
        while (true)
        {
            if (!TryReadValueToken(tokenizer, out PdfToken token))
            {
                UnterminatedContainer("dictionary", tokenizer.Position);
            }

            if (token.Kind == PdfTokenKind.DictionaryClose)
            {
                break;
            }

            if (entries.Count + (long)(missingKeys?.Count ?? 0) >= _limits.MaxContainerSize)
            {
                throw new QpdfParseException($"Dictionary holds more than {_limits.MaxContainerSize} top-level entries.", token.Offset, null);
            }

            if (token.Kind != PdfTokenKind.Name)
            {
                if (!TolerateObjectErrors)
                {
                    throw new QpdfParseException($"Dictionary key must be a name, not {token.Kind}.", token.Offset, null);
                }

                if (missingKeys is null)
                {
                    _governor.ReserveMemory(32L);
                    missingKeys = new List<QpdfObject>();
                }

                _governor.ReserveMemory(16L);
                missingKeys.Add(ParseToken(tokenizer, token, depth + 1));
                continue;
            }

            PdfName key = (PdfName)ParseName(token);
            if (!TryReadValueToken(tokenizer, out PdfToken valueToken))
            {
                UnterminatedContainer("dictionary", tokenizer.Position);
            }

            bool missingValue = valueToken.Kind == PdfTokenKind.DictionaryClose;
            QpdfObject value;
            if (missingValue && TolerateObjectErrors)
            {
                WarnRepair("Dictionary key has no value; treating value as null.", valueToken.Offset);
                value = PdfNull.Instance;
            }
            else
            {
                value = ParseToken(tokenizer, valueToken, depth + 1);
            }

            if (entries.ContainsKey(key))
            {
                if (!SuppressRepairDiagnostics)
                {
                    _diagnostics.Add(QpdfDiagnosticCode.DuplicateKey, QpdfDiagnosticSeverity.Warning,
                        $"Duplicate dictionary key '{key.DisplayText}' keeps the last value.");
                }
                if (TolerateObjectErrors)
                {
                    CountToleratedError(token.Offset);
                }
            }
            else
            {
                _governor.ReserveMemory(32L);
            }

            entries[key] = value;
            if (missingValue)
            {
                break;
            }
        }

        if (missingKeys is not null)
        {
            if (SanityChecks)
            {
                WarnRepair("Ignored orphan dictionary values during xref recovery.", offset);
            }
            else
            {
                long fakeIndex = 0L;
                foreach (QpdfObject value in missingKeys)
                {
                    PdfName key = FreshFakeKey(entries, ref fakeIndex);
                    WarnRepair("Inserted a missing dictionary key.", offset);
                    _governor.ReserveMemory(32L);
                    entries.Add(key, value);
                }
            }

            _governor.ReleaseMemory(checked(32L + (missingKeys.Count * 16L)));
        }

        PdfDictionary dictionary = new(entries);
        long beforeNext = tokenizer.Position;
        bool nextIsStream = false;
        try
        {
            nextIsStream = tokenizer.TryReadToken(out PdfToken next, TolerateObjectErrors, _governor)
                && next.Kind == PdfTokenKind.Stream;
            if (nextIsStream)
            {
                return ParseStream(tokenizer, dictionary, next.Offset);
            }
        }
        catch (QpdfParseException) when (!nextIsStream)
        {
            // Following lexical damage belongs to the outer container.
        }

        tokenizer.SetPosition(beforeNext);
        return dictionary;
    }

    private PdfName FreshFakeKey(Dictionary<PdfName, QpdfObject> entries, ref long index)
    {
        Span<byte> text = stackalloc byte[32];
        "QPDFFake"u8.CopyTo(text);
        while (true)
        {
            _governor.ChargeWork(1L);
            index = checked(index + 1L);
            if (!Utf8Formatter.TryFormat(index, text[8..], out int count))
            {
                throw new QpdfParseException("Dictionary repair key exceeds its supported length.", null, null);
            }

            long memory = count + 8L + 64L;
            long scratch = count + 8L + 32L;
            _governor.ReserveMemory(checked(memory + scratch));
            PdfName candidate = new(text[..(count + 8)].ToArray());
            _governor.ReleaseMemory(scratch);
            if (!entries.ContainsKey(candidate))
            {
                return candidate;
            }

            _governor.ReleaseMemory(memory);
        }
    }

    private QpdfObject ParseStream(PdfTokenizer tokenizer, PdfDictionary dictionary, long offset)
    {
        long length = ResolveStreamLength(dictionary, offset);
        if (length > int.MaxValue)
        {
            throw new QpdfParseException($"Stream length {length} exceeds the supported maximum.", offset, null);
        }

        ReadOnlyMemory<byte> data = tokenizer.Data;
        long cursor = tokenizer.Position;
        if (cursor < data.Length && data.Span[(int)cursor] == (byte)'\r')
        {
            cursor += 1;
            if (cursor < data.Length && data.Span[(int)cursor] == (byte)'\n')
            {
                cursor += 1;
            }
        }
        else if (cursor < data.Length && data.Span[(int)cursor] == (byte)'\n')
        {
            cursor += 1;
        }
        else
        {
            throw new QpdfParseException("The 'stream' keyword must be followed by an end-of-line marker.", offset, null);
        }

        checked
        {
            if (length > data.Length - cursor)
            {
                throw new QpdfParseException($"Stream declares {length} bytes but only {data.Length - cursor} remain.", offset, null);
            }
        }

        _governor.ReserveMemory(length);
        byte[] bytes = new byte[(int)length];
        data.Slice((int)cursor, (int)length).CopyTo(bytes);
        tokenizer.SetPosition(cursor + length);
        tokenizer.SkipWhitespaceAndComments();
        if (!tokenizer.TryReadToken(out PdfToken end) || end.Kind != PdfTokenKind.EndStream)
        {
            throw new QpdfParseException("Expected 'endstream' after stream data.", offset, null);
        }

        return new PdfStream(dictionary, bytes);
    }

    private long ResolveStreamLength(PdfDictionary dictionary, long offset)
    {
        PdfName lengthKey = new([(byte)'L', (byte)'e', (byte)'n', (byte)'g', (byte)'t', (byte)'h']);
        if (!dictionary.TryGet(lengthKey, out QpdfObject raw))
        {
            throw new QpdfParseException("Stream dictionary has no /Length.", offset, null);
        }

        if (raw is PdfInteger direct)
        {
            if (direct.Value < 0L)
            {
                throw new QpdfParseException($"Stream length {direct.Value} is negative.", offset, null);
            }

            return direct.Value;
        }

        if (raw is PdfReference reference)
        {
            if (_resolver is null)
            {
                throw new QpdfParseException("Indirect stream length needs an object resolver.", offset, null);
            }

            if (!_resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is not PdfInteger indirect)
            {
                throw new QpdfParseException("Unresolvable indirect stream length.", offset, reference.ObjectNumber);
            }

            if (indirect.Value < 0L)
            {
                throw new QpdfParseException($"Stream length {indirect.Value} is negative.", offset, reference.ObjectNumber);
            }

            return indirect.Value;
        }

        throw new QpdfParseException("Stream /Length must be an integer or an indirect reference.", offset, null);
    }

    private byte[] UnescapeName(ReadOnlyMemory<byte> raw, long offset)
    {
        ReadOnlySpan<byte> span = raw.Span;
        byte[] result = new byte[span.Length];
        int outCount = 0;
        long nextWork = 0L;
        for (int i = 0; i < span.Length; i++)
        {
            ChargeUnescapeWork(i, ref nextWork);
            if (span[i] == (byte)'#')
            {
                int hi = span.Length - i >= 2 ? HexValue(span[i + 1]) : -1;
                int lo = span.Length - i >= 3 ? HexValue(span[i + 2]) : -1;
                if (hi < 0 || lo < 0)
                {
                    if (!TolerateObjectErrors)
                    {
                        throw new QpdfParseException("Invalid or truncated '#' escape in name.", offset, null);
                    }

                    WarnRepair("Invalid name escape retained literally.", offset);
                    result[outCount++] = span[i];
                    continue;
                }

                result[outCount] = (byte)((hi << 4) | lo);
                outCount += 1;
                i += 2;
                continue;
            }

            result[outCount] = span[i];
            outCount += 1;
        }

        _governor.CheckCancellation();
        if (outCount == result.Length)
        {
            return result;
        }

        byte[] trimmed = new byte[outCount];
        Buffer.BlockCopy(result, 0, trimmed, 0, outCount);
        return trimmed;
    }

    private byte[] UnescapeLiteral(ReadOnlyMemory<byte> raw, long offset)
    {
        ReadOnlySpan<byte> span = raw.Span;
        byte[] result = new byte[span.Length];
        int outCount = 0;
        long nextWork = 0L;
        for (int i = 0; i < span.Length; i++)
        {
            ChargeUnescapeWork(i, ref nextWork);
            if (span[i] != (byte)'\\')
            {
                // Raw CR and CRLF are a single PDF end-of-line marker. An
                // escaped \r below is a literal carriage return instead.
                result[outCount] = span[i] == (byte)'\r' ? (byte)'\n' : span[i];
                outCount += 1;
                if (span[i] == (byte)'\r' && i + 1 < span.Length && span[i + 1] == (byte)'\n')
                {
                    i += 1;
                }
                continue;
            }

            i += 1;
            if (i >= span.Length)
            {
                throw new QpdfParseException("Trailing backslash in literal string.", offset, null);
            }

            byte b = span[i];
            if (b == (byte)'n')
            {
                result[outCount] = (byte)'\n';
                outCount += 1;
            }
            else if (b == (byte)'r')
            {
                result[outCount] = (byte)'\r';
                outCount += 1;
            }
            else if (b == (byte)'t')
            {
                result[outCount] = (byte)'\t';
                outCount += 1;
            }
            else if (b == (byte)'b')
            {
                result[outCount] = (byte)'\b';
                outCount += 1;
            }
            else if (b == (byte)'f')
            {
                result[outCount] = (byte)'\f';
                outCount += 1;
            }
            else if (b == (byte)'(' || b == (byte)')' || b == (byte)'\\')
            {
                result[outCount] = b;
                outCount += 1;
            }
            else if (b == (byte)'\r')
            {
                if (i + 1 < span.Length && span[i + 1] == (byte)'\n')
                {
                    i += 1;
                }
            }
            else if (b == (byte)'\n')
            {
            }
            else if (b >= (byte)'0' && b <= (byte)'7')
            {
                int value = b - (byte)'0';
                for (int k = 0; k < 2; k++)
                {
                    if (i + 1 < span.Length && span[i + 1] >= (byte)'0' && span[i + 1] <= (byte)'7')
                    {
                        i += 1;
                        value = (value * 8) + (span[i] - (byte)'0');
                    }
                    else
                    {
                        break;
                    }
                }

                result[outCount] = (byte)(value & 0xFF);
                outCount += 1;
            }
            else
            {
                result[outCount] = b;
                outCount += 1;
            }
        }

        _governor.CheckCancellation();
        if (outCount == result.Length)
        {
            return result;
        }

        byte[] trimmed = new byte[outCount];
        Buffer.BlockCopy(result, 0, trimmed, 0, outCount);
        return trimmed;
    }

    private void ChargeUnescapeWork(int position, ref long nextWork)
    {
        // Escapes advance by varying numbers of bytes; equality/modulo checks
        // can miss an interval when an escape steps across its boundary.
        if (position >= nextWork)
        {
            _governor.ChargeWork(1L);
            _governor.CheckCancellation();
            nextWork = (long)position + 4096L;
        }
    }

    private static byte[] DecodeHex(ReadOnlyMemory<byte> raw, long offset)
    {
        ReadOnlySpan<byte> span = raw.Span;
        byte[] digits = new byte[span.Length];
        int count = 0;
        for (int i = 0; i < span.Length; i++)
        {
            byte b = span[i];
            if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r' || b == (byte)'\f' || b == 0x00)
            {
                continue;
            }

            int value = HexValue(b);
            if (value < 0)
            {
                throw new QpdfParseException($"Invalid hexadecimal digit 0x{b:X2}.", offset, null);
            }

            digits[count] = (byte)value;
            count += 1;
        }

        int outCount = (count + 1) / 2;
        byte[] result = new byte[outCount];
        for (int i = 0; i < count; i += 2)
        {
            int hi = digits[i];
            int lo = i + 1 < count ? digits[i + 1] : 0;
            result[i / 2] = (byte)((hi << 4) | lo);
        }

        return result;
    }

    private static int HexValue(byte b)
    {
        if (b >= (byte)'0' && b <= (byte)'9')
        {
            return b - (byte)'0';
        }

        if (b >= (byte)'A' && b <= (byte)'F')
        {
            return b - (byte)'A' + 10;
        }

        if (b >= (byte)'a' && b <= (byte)'f')
        {
            return b - (byte)'a' + 10;
        }

        return -1;
    }

    private string CopyNumericText(ReadOnlyMemory<byte> data)
    {
        // The tokenizer established ASCII numeric syntax. Copy directly into
        // the admitted string, without a second full-size character array.
        return string.Create(data.Length, (Data: data, Governor: _governor), static (chars, state) =>
        {
            ReadOnlySpan<byte> bytes = state.Data.Span;
            for (int i = 0; i < chars.Length; i++)
            {
                if ((i & 4095) == 0)
                {
                    state.Governor.ChargeWork(1L);
                    state.Governor.CheckCancellation();
                }

                chars[i] = (char)bytes[i];
            }
        });
    }
}
