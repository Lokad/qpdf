using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Syntax;

/// <summary>Resolves indirect references through the document's xref table during parsing.</summary>
internal interface IIndirectResolver
{
    /// <summary>Tries to resolve a reference to its object.</summary>
    bool TryResolve(PdfReference reference, out QpdfObject? value);
}

/// <summary>Bounds damaged stream recovery to the current physical object.</summary>
internal interface IStreamRecoveryResolver : IIndirectResolver
{
    long GetStreamRecoveryEnd(long streamOffset);
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
    private readonly CancellationToken _token;
    private ParserMemoryScope? _memory;
    private int _dictionaryReceiptDepth = -1;
    private IReadOnlyDictionary<PdfName, ParsedValue>? _dictionaryReceipts;
    private ParserMemoryScope Memory => _memory ?? throw new InvalidOperationException("Parser allocation has no attempt owner.");

    internal PdfParser(QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, IIndirectResolver? resolver)
        : this(limits, governor, diagnostics, resolver, governor?.CancellationToken ?? CancellationToken.None) { }

    internal PdfParser(QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, IIndirectResolver? resolver,
        CancellationToken token)
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
        _token = token;
    }

    /// <summary>Allows object syntax repair without relaxing host resource ceilings.</summary>
    internal bool TolerateObjectErrors { get; set; }

    internal bool RejectIndirectReferences { get; set; }

    /// <summary>Object-stream members contain values, not following stream data.</summary>
    internal bool AllowStreams { get; set; } = true;

    internal bool BorrowImmutableStreamData { get; set; }
    internal bool BorrowedStreamData { get; private set; }

    /// <summary>Budgeted errors encountered in the current object.</summary>
    internal long ToleratedErrors { get; private set; }

    internal bool ObjectAborted { get; private set; }

    internal bool SanityChecks { get; set; }

    internal bool SuppressRepairDiagnostics { get; set; }

    private int _nearbyErrors;
    private int _goodTokens;
    // Negative outside a dictionary. Nested containers save and restore this
    // count so errors inspect only the container currently being parsed.
    private long _dictionaryOrphans = -1L;
    private long _lastCountedRepairToken = -1L;
    private long _integerFollowingToken = -1L;
    private long _integerSecondFollowingToken = -1L;

    internal void BeginTolerantObject()
    {
        ObjectAborted = false;
        BorrowedStreamData = false;
        ToleratedErrors = 0L;
        _nearbyErrors = 0;
        _goodTokens = 0;
        _dictionaryOrphans = -1L;
        _lastCountedRepairToken = -1L;
        _integerFollowingToken = -1L;
        _integerSecondFollowingToken = -1L;
    }

    private sealed class RepairAbort(string detail, long offset, string? precedingWarning) : Exception
    {
        internal string Detail { get; } = detail;
        internal long Offset { get; } = offset;
        internal string? PrecedingWarning { get; } = precedingWarning;
    }

    private void FinishRepairAbort(RepairAbort abort)
    {
        // All exclusive container frames have unwound. Diagnostic growth must
        // not overlap values that are already known to be discarded as null.
        ObjectAborted = true;
        Memory.Discard();
        if (abort.PrecedingWarning is not null) { WarnRepair(abort.PrecedingWarning, abort.Offset); }
        WarnRepair(abort.Detail, abort.Offset);
    }

    private void WarnRepair(string detail, long offset)
    {
        if (SuppressRepairDiagnostics)
        {
            // Trial damage is summarized by RecoveryEngaged. Loaded objects
            // report their own repairs, avoiding repeated scan diagnostics.
            return;
        }

        _diagnostics.AddParts(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning,
            ["Object syntax repair at offset ", DiagnosticPart.Invariant(offset), ": ", detail]);
    }

    private void CountToleratedError(long offset)
    {
        ToleratedErrors = checked(ToleratedErrors + 1L);
        if (_limits.MaxParserErrors != 0L && ToleratedErrors >= _limits.MaxParserErrors)
        {
            throw new RepairAbort("limits error(parser-max-errors): too many errors; treating object as null.", offset, null);
        }

        // The incoming token was counted optimistically. Four intervening good
        // tokens break the proximity cluster; they do not reset the total budget.
        bool separated = _goodTokens > 4;
        _goodTokens = 0;
        if (separated)
        {
            _nearbyErrors = 1;
            return;
        }

        _nearbyErrors++;
        // Zero disables the finite threshold, but remains zero for this
        // additional orphan guard. The incoming value has not been added yet.
        long remaining = _limits.MaxParserErrors == 0L ? 0L : _limits.MaxParserErrors - ToleratedErrors;
        if (_nearbyErrors > 5 || _dictionaryOrphans > remaining)
        {
            throw new RepairAbort("Too many nearby errors or orphan dictionary values; treating object as null.", offset, null);
        }
    }

    private bool TryReadValueToken(PdfTokenizer tokenizer, out PdfToken token)
    {
        ChargeWork(1L);
        bool found = tokenizer.TryReadToken(out token, TolerateObjectErrors, _governor);
        if (found)
        {
            CountRepairToken(token.Offset);
        }

        return found;
    }

    private void CountRepairToken(long offset)
    {
        if (TolerateObjectErrors && offset > _lastCountedRepairToken)
        {
            _lastCountedRepairToken = offset;
            _goodTokens = Math.Min(5, _goodTokens + 1);
        }
    }

    private void UnterminatedContainer(string kind, long offset)
    {
        if (!TolerateObjectErrors)
        {
            throw new QpdfParseException($"Unterminated {kind}.", offset, null);
        }

        throw new RepairAbort("Unterminated container; treating object as null.", offset, null);
    }

    /// <summary>Parses one object-stream member with independent error state.</summary>
    internal QpdfObject ParseObjectValue(PdfTokenizer tokenizer, int depth)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(_governor);
        QpdfObject value = ParseObjectValue(tokenizer, depth, memory);
        memory.Retain(value);
        return value;
    }

    internal QpdfObject ParseObjectValue(PdfTokenizer tokenizer, int depth, ParserMemoryScope memory)
    {
        memory.CheckOwner(_governor);
        ParserMemoryScope? parent = _memory;
        _memory = memory;
        BeginTolerantObject();
        try
        {
            try
            {
                return ParseValueCore(tokenizer, depth);
            }
            catch (RepairAbort abort) when (TolerateObjectErrors)
            {
                FinishRepairAbort(abort);
                return PdfNull.Instance;
            }
        }
        finally { _memory = parent; }
    }

    internal QpdfObject ParseValue(PdfTokenizer tokenizer, int depth)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(_governor);
        QpdfObject value = ParseValue(tokenizer, depth, memory);
        memory.Retain(value);
        return value;
    }

    internal QpdfObject ParseValue(PdfTokenizer tokenizer, int depth, ParserMemoryScope memory)
    {
        memory.CheckOwner(_governor);
        ParserMemoryScope? parent = _memory;
        _memory = memory;
        try
        {
            return ParseValueCore(tokenizer, depth);
        }
        finally { _memory = parent; }
    }

    internal ParsedDictionaryValue ParseDictionaryValue(PdfTokenizer tokenizer, int depth, ParserMemoryScope memory)
    {
        int parentDepth = _dictionaryReceiptDepth;
        IReadOnlyDictionary<PdfName, ParsedValue>? parentReceipts = _dictionaryReceipts;
        _dictionaryReceiptDepth = depth;
        _dictionaryReceipts = null;
        try
        {
            QpdfObject value = ParseValue(tokenizer, depth, memory);
            return new ParsedDictionaryValue(value, _dictionaryReceipts);
        }
        finally
        {
            _dictionaryReceiptDepth = parentDepth;
            _dictionaryReceipts = parentReceipts;
        }
    }

    private void ChargeWork(long bytes)
    { _token.ThrowIfCancellationRequested(); _governor.ChargeWork(bytes); }

    private void ReserveMemory(long bytes)
    {
        if (_memory is null) { throw new InvalidOperationException("Parser allocation has no attempt owner."); }
        _token.ThrowIfCancellationRequested();
        _memory.Reserve(bytes);
    }

    private void ReleaseMemory(long bytes)
    {
        if (_memory is null) { throw new InvalidOperationException("Parser release has no attempt owner."); }
        _memory.Release(bytes);
    }

    private QpdfObject ParseValueCore(PdfTokenizer tokenizer, int depth)
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

        // The root token opens the object; only following tokens participate
        // in the good-token gap used by container repair.
        _goodTokens = 0;
        return ParseToken(tokenizer, in token, depth);
    }

    private QpdfObject ParseToken(PdfTokenizer tokenizer, in PdfToken token, int depth)
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
                    return ParseIntegerOrReference(tokenizer, in token);
                }
                catch (QpdfParseException) when (TolerateObjectErrors)
                {
                    // Numeric conversion failure invalidates the whole object in
                    // the reference, independently of the semantic error budget.
                    throw new RepairAbort("Numeric conversion failed; treating object as null.", token.Offset, null);
                }
            case PdfTokenKind.Real:
                return ParseReal(in token);
            case PdfTokenKind.Name:
                return ParseName(in token);
            case PdfTokenKind.LiteralString:
                return ParseByteString(in token, false);
            case PdfTokenKind.HexString:
                return ParseByteString(in token, true);
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
                    throw new RepairAbort("Unexpected terminator during xref trial parsing; treating object as null.", token.Offset, null);
                }

                bool word = (depth == 0 || !SanityChecks)
                    && token.Kind is not (PdfTokenKind.ArrayClose or PdfTokenKind.DictionaryClose or PdfTokenKind.BadToken);
                string warning = word ? "Unknown token treated as string." : "Unexpected token treated as null.";
                if (depth > 0 && _limits.MaxParserErrors != 0L && ToleratedErrors >= _limits.MaxParserErrors - 1L)
                {
                    ToleratedErrors = checked(ToleratedErrors + 1L);
                    throw new RepairAbort("limits error(parser-max-errors): too many errors; treating object as null.", token.Offset, warning);
                }
                WarnRepair(warning, token.Offset);
                if (depth > 0)
                {
                    CountToleratedError(token.Offset);
                }

                if (!word)
                {
                    return PdfNull.Instance;
                }

                ReserveMemory(checked((token.Data.Length * 2L) + 96L));
                PdfByteString repaired = new(token.Data.ToArray(), hex: false);
                ReleaseMemory(checked(token.Data.Length + 32L));
                return repaired;
        }
    }

    private ParsedValue ParseOwnedToken(PdfTokenizer tokenizer, in PdfToken token, int depth)
    {
        // Measure only this attempt's contribution, never the global governor.
        // A nested resolver/cache load retains a separate scope's reservations.
        long before = Memory.ReservedBytes;
        QpdfObject value = ParseToken(tokenizer, in token, depth);
        return new ParsedValue(value, checked(Memory.ReservedBytes - before));
    }

    internal (int Number, int Generation, QpdfObject Value) ParseIndirectObject(PdfTokenizer tokenizer, int depth)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(_governor);
        (int number, int generation, QpdfObject value) = ParseIndirectObject(tokenizer, depth, memory);
        memory.Retain(value);
        return (number, generation, value);
    }

    internal (int Number, int Generation, QpdfObject Value) ParseIndirectObject(
        PdfTokenizer tokenizer, int depth, ParserMemoryScope memory)
    {
        memory.CheckOwner(_governor);
        ParserMemoryScope? parent = _memory;
        _memory = memory;
        try
        {
            (int number, int generation, QpdfObject value) = ParseIndirectObjectCore(tokenizer, depth);
            if (ObjectAborted) { memory.Discard(); }
            return (number, generation, value);
        }
        finally { _memory = parent; }
    }

    private (int Number, int Generation, QpdfObject Value) ParseIndirectObjectCore(PdfTokenizer tokenizer, int depth)
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

        int number = ParseObjectNumber(in numberToken);
        int generation = ParseGeneration(in generationToken);
        QpdfObject value;
        try
        {
            value = ParseValueCore(tokenizer, depth);
        }
        catch (RepairAbort abort) when (TolerateObjectErrors)
        {
            FinishRepairAbort(abort);
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

    private QpdfObject ParseIntegerOrReference(PdfTokenizer tokenizer, in PdfToken first)
    {
        long saved = tokenizer.Position;
        bool reference = false;
        PdfToken second = default;
        PdfToken third = default;
        _integerFollowingToken = -1L;
        _integerSecondFollowingToken = -1L;
        try
        {
            bool hasGeneration = tokenizer.TryReadReferencePart(true, out second, out long nextOffset, TolerateObjectErrors, _governor);
            _integerFollowingToken = nextOffset < tokenizer.Data.Length ? nextOffset : -1L;
            if (hasGeneration && second.Kind == PdfTokenKind.Integer)
            {
                reference = tokenizer.TryReadReferencePart(false, out third, out nextOffset, TolerateObjectErrors, _governor)
                    && third.Kind == PdfTokenKind.Reference;
                _integerSecondFollowingToken = nextOffset < tokenizer.Data.Length ? nextOffset : -1L;
            }
        }
        catch (QpdfParseException)
        {
            // Lookahead must not turn a valid integer into the following token's
            // error. Leave that token for the owning container to read once.
        }

        if (reference)
        {
            if (RejectIndirectReferences)
            { throw new QpdfParseException("Inline image dictionaries cannot contain indirect references.", first.Offset, null); }
            if (TolerateObjectErrors)
            {
                CountRepairToken(second.Offset);
                CountRepairToken(third.Offset);
                long number = ParseIntegerText(in first);
                long generation = ParseIntegerText(in second);
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

                ReserveMemory(32L);
                return new PdfReference((int)number, (int)generation);
            }

            int objectNumber = ParseObjectNumber(in first);
            int objectGeneration = ParseGeneration(in second);
            ReserveMemory(32L);
            return new PdfReference(objectNumber, objectGeneration);
        }

        tokenizer.SetPosition(saved);
        return ParseInteger(in first);
    }

    private int ParseObjectNumber(in PdfToken token)
    {
        long value = ParseIntegerText(in token);
        if (value < 0 || value > int.MaxValue)
        {
            throw new QpdfParseException($"Object number {value} is out of range.", token.Offset, null);
        }

        return (int)value;
    }

    private int ParseGeneration(in PdfToken token)
    {
        long value = ParseIntegerText(in token);
        if (value < 0 || value > 65535)
        {
            throw new QpdfParseException($"Generation {value} is out of range.", token.Offset, null);
        }

        return (int)value;
    }

    private PdfInteger ParseInteger(in PdfToken token)
    {
        long value = ParseIntegerText(in token);
        // These bounded immutable canonical values have process-lifetime storage,
        // just like booleans/null. Keep signed and padded spellings independent.
        if ((ulong)value < PdfInteger.SharedValueCount
            && token.Data.Span[0] is >= (byte)'0' and <= (byte)'9'
            && (token.Data.Length == 1 || token.Data.Span[0] != (byte)'0'))
        { return PdfInteger.GetSharedValue((int)value); }
        // Keep the original token for lossless writing. The numeric scan is
        // governed even for arbitrarily many leading zeros; admit the UTF-16
        // string and scalar before materializing them.
        ReserveMemory(checked((token.Data.Length * 2L) + 64L));
        try
        {
            return new PdfInteger(CopyNumericText(token.Data), value);
        }
        catch
        {
            ReleaseMemory(checked((token.Data.Length * 2L) + 64L));
            throw;
        }
    }

    private long ParseIntegerText(in PdfToken token)
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
                ChargeWork(1L);
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

    private PdfReal ParseReal(in PdfToken token)
    {
        long retained = checked((token.Data.Length * 2L) + 64L);
        ReserveMemory(retained);
        try
        {
            double value = PdfRealConversion.Convert(token.Data.Span, token.Offset, _governor);
            return new PdfReal(CopyNumericText(token.Data), value);
        }
        catch
        {
            ReleaseMemory(retained);
            throw;
        }
    }

    private QpdfObject ParseName(in PdfToken token)
    {
        _governor.CheckCancellation();
        if (PdfName.FindSharedAscii(token.Data.Span) is PdfName shared)
        {
            // Keep both bounded decode-pass work checks. Constant storage has
            // process lifetime and needs no per-attempt allocation receipt.
            ChargeWork(1L); _governor.CheckCancellation();
            ChargeWork(1L); _governor.CheckCancellation();
            return shared;
        }
        long before = Memory.ReservedBytes;
        try
        {
            byte[] raw = UnescapeName(token.Data, token.Offset);
            PdfName name = PdfName.FromOwnedBytes(raw);
            return name;
        }
        catch
        {
            ReleaseMemory(checked(Memory.ReservedBytes - before));
            throw;
        }
    }

    private QpdfObject ParseByteString(in PdfToken token, bool hex)
    {
        long before = Memory.ReservedBytes;
        ReserveMemory(32L);
        try
        {
            // Each decoder admits its actual scratch/result and overlap before
            // allocation. Transfer the fresh result without an immutable copy.
            byte[] raw = hex ? DecodeHex(token.Data, token.Offset) : UnescapeLiteral(token.Data, token.Offset);
            PdfByteString value = PdfByteString.FromOwnedBytes(raw, hex);
            return value;
        }
        catch
        {
            ReleaseMemory(checked(Memory.ReservedBytes - before));
            throw;
        }
    }

    private QpdfObject ParseArray(PdfTokenizer tokenizer, int depth)
    {
        long parentOrphans = _dictionaryOrphans;
        _dictionaryOrphans = -1L;
        try
        {
            return ParseArrayContents(tokenizer, depth);
        }
        finally
        {
            _dictionaryOrphans = parentOrphans;
        }
    }

    [InlineArray(4)]
    private struct ArrayPrefix
    {
        private QpdfObject _first;
    }

    private QpdfObject ParseArrayContents(PdfTokenizer tokenizer, int depth)
    {
        // Common four-item boxes need no growable heap storage. Larger arrays
        // spill once into the existing admitted list path without rescanning.
        ArrayPrefix prefix = default;
        List<QpdfObject>? items = null;
        int count = 0;
        while (true)
        {
            if (!TryReadValueToken(tokenizer, out PdfToken token))
            {
                UnterminatedContainer("array", tokenizer.Position);
            }

            if (token.Kind == PdfTokenKind.ArrayClose)
            {
                ChargeWork(count);
                _governor.CheckCancellation();
                if (items is not null) { return ParserContainers.CopyArray(Memory, items); }
                Memory.Reserve(64L + IntPtr.Size * (long)count);
                QpdfObject[] kept = count == 0 ? Array.Empty<QpdfObject>() : new QpdfObject[count];
                for (int index = 0; index < count; index++) { kept[index] = prefix[index]; }
                return PdfArray.FromOwnedItems(kept);
            }

            if (count >= _limits.MaxContainerSize)
            {
                throw new QpdfParseException($"Array holds more than {_limits.MaxContainerSize} top-level items.", token.Offset, null);
            }

            QpdfObject value = ParseToken(tokenizer, in token, depth + 1);
            if (count < 4) { prefix[count] = value; }
            else
            {
                if (items is null)
                {
                    items = ParserContainers.CreateList(Memory);
                    for (int index = 0; index < 4; index++)
                    { ParserContainers.AddItem(Memory, items, prefix[index]); }
                }
                ParserContainers.AddItem(Memory, items, value);
            }
            count++;
        }
    }

    private QpdfObject ParseDictionaryOrStream(PdfTokenizer tokenizer, int depth, long offset)
    {
        long parentOrphans = _dictionaryOrphans;
        _dictionaryOrphans = 0L;
        try
        {
            return ParseDictionaryContentsOrStream(tokenizer, depth, offset);
        }
        finally
        {
            _dictionaryOrphans = parentOrphans;
        }
    }

    [InlineArray(4)]
    private struct DictionaryPrefix
    {
        private KeyValuePair<PdfName, ParsedValue> _first;
    }

    private PdfNameTable<ParsedValue> SpillDictionaryPrefix(ref DictionaryPrefix prefix, int count)
    {
        PdfNameTable<ParsedValue> entries = ParserContainers.CreateDictionary(Memory, _governor);
        entries.EnsureCapacity(Memory, _governor, count, _token);
        for (int index = 0; index < count; index++)
        { entries.AppendKnownUnique(Memory, _governor, prefix[index].Key, prefix[index].Value, _token); }
        prefix = default;
        Memory.Release(PdfNameTable<ParsedValue>.WrapperBytes);
        return entries;
    }

    private PdfDictionary CompleteDictionaryPrefix(ref DictionaryPrefix prefix, int count)
    {
        PdfNameTable<QpdfObject> entries = PdfNameTable<QpdfObject>.Create(Memory, _governor, IntPtr.Size, _token);
        entries.EnsureCapacity(Memory, _governor, count, _token);
        for (int index = 0; index < count; index++)
        { entries.AppendKnownUnique(Memory, _governor, prefix[index].Key, prefix[index].Value.Value, _token); }
        entries.Freeze(Memory, _governor, _token);
        Memory.Reserve(32L);
        PdfDictionary result = PdfDictionary.FromOwnedEntries(entries);
        prefix = default;
        Memory.Release(PdfNameTable<ParsedValue>.WrapperBytes);
        return result;
    }

    private QpdfObject ParseDictionaryContentsOrStream(PdfTokenizer tokenizer, int depth, long offset)
    {
        // Common small dictionaries need no growable receipt table. Larger or
        // repaired dictionaries spill once into the existing construction path.
        Memory.Reserve(PdfNameTable<ParsedValue>.WrapperBytes);
        DictionaryPrefix prefix = default;
        int prefixCount = 0;
        PdfNameTable<ParsedValue>? entries = null;
        List<ParsedValue>? missingKeys = null;
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

            if ((entries?.Count ?? prefixCount) + (long)(missingKeys?.Count ?? 0) >= _limits.MaxContainerSize)
            {
                throw new QpdfParseException($"Dictionary holds more than {_limits.MaxContainerSize} top-level entries.", token.Offset, null);
            }

            if (token.Kind != PdfTokenKind.Name)
            {
                if (!TolerateObjectErrors)
                {
                    throw new QpdfParseException($"Dictionary key must be a name, not {token.Kind}.", token.Offset, null);
                }

                entries ??= SpillDictionaryPrefix(ref prefix, prefixCount);
                if (missingKeys is null)
                {
                    missingKeys = ParserContainers.CreateOwnedList(Memory);
                }

                ParserContainers.AddOwnedItem(Memory, missingKeys, ParseOwnedToken(tokenizer, in token, depth + 1));
                _dictionaryOrphans = missingKeys.Count;
                continue;
            }

            long beforeKey = Memory.ReservedBytes;
            PdfName key = (PdfName)ParseName(in token);
            long keyBytes = checked(Memory.ReservedBytes - beforeKey);
            if (!TryReadValueToken(tokenizer, out PdfToken valueToken))
            {
                UnterminatedContainer("dictionary", tokenizer.Position);
            }

            bool missingValue = valueToken.Kind == PdfTokenKind.DictionaryClose;
            ParsedValue value;
            if (missingValue && TolerateObjectErrors)
            {
                WarnRepair("Dictionary key has no value; treating value as null.", valueToken.Offset);
                value = new ParsedValue(PdfNull.Instance, 0L);
            }
            else
            {
                value = ParseOwnedToken(tokenizer, in valueToken, depth + 1);
            }

            long discardedBytes = 0L;
            int prefixIndex = -1;
            if (entries is null)
            {
                for (int index = 0; index < prefixCount; index++)
                {
                    ChargeWork(1L); _governor.CheckCancellation();
                    if (ReferenceEquals(prefix[index].Key, key)
                        || PdfNameTable<ParsedValue>.NamesEqual(prefix[index].Key, key, _governor, _token))
                    { prefixIndex = index; break; }
                }
                if (prefixIndex < 0 && prefixCount == 4)
                { entries = SpillDictionaryPrefix(ref prefix, prefixCount); }
            }
            ParsedValue previous = default;
            bool inserted;
            if (entries is not null)
            { inserted = entries.TryAdd(Memory, _governor, key, value, _token, out previous); }
            else if (prefixIndex >= 0)
            { previous = prefix[prefixIndex].Value; inserted = false; }
            else
            { prefix[prefixCount++] = new(key, value); inserted = true; }
            // Duplicates leave the stored value intact until warning/repair
            // checks succeed, retaining the first equal key and its position.
            if (!inserted)
            {
                if (!SuppressRepairDiagnostics)
                {
                    _diagnostics.AddParts(QpdfDiagnosticCode.DuplicateKey, QpdfDiagnosticSeverity.Warning,
                        ["Duplicate dictionary key '", DiagnosticPart.Name(key), "' keeps the last value."]);
                }
                if (TolerateObjectErrors)
                {
                    if (value.Value is PdfInteger)
                    {
                        // Scalar integers are disambiguated from references
                        // using following tokens. Their duplicate is observed
                        // after that lookahead; count it now, once per offset.
                        CountRepairToken(_integerFollowingToken);
                        CountRepairToken(_integerSecondFollowingToken);
                    }
                    CountToleratedError(token.Offset);
                }
                // Dictionary assignment keeps the first equal key instance.
                // This newly parsed key and the old value cease to be retained.
                discardedBytes = checked(keyBytes + previous.ReservedBytes);
                previous = default;
                if (entries is not null) { entries.Assign(Memory, _governor, key, value, _token); }
                else { prefix[prefixIndex] = new(prefix[prefixIndex].Key, value); }
            }
            ReleaseMemory(discardedBytes);
            if (missingValue)
            {
                break;
            }
        }

        if (missingKeys is not null)
        {
            PdfNameTable<ParsedValue> repaired = entries ?? throw new InvalidOperationException("Dictionary repair requires name storage.");
            if (SanityChecks)
            {
                WarnRepair("Ignored orphan dictionary values during xref recovery.", offset);
                for (int i = 0; i < missingKeys.Count; i++)
                {
                    ChargeWork(1L);
                    long reserved = missingKeys[i].ReservedBytes;
                    missingKeys[i] = default;
                    ReleaseMemory(reserved);
                }
            }
            else
            {
                long fakeIndex = 0L;
                foreach (ParsedValue value in missingKeys)
                {
                    PdfName key = FreshFakeKey(repaired, ref fakeIndex);
                    WarnRepair("Inserted a missing dictionary key.", offset);
                    repaired.TryAdd(Memory, _governor, key, value, _governor.CancellationToken);
                }
            }

            ParserContainers.ReleaseOwnedList(Memory, missingKeys);
        }

        ChargeWork(entries?.Count ?? prefixCount);
        _governor.CheckCancellation();
        bool preserveReceipts = depth == _dictionaryReceiptDepth;
        if (preserveReceipts && entries is null) { entries = SpillDictionaryPrefix(ref prefix, prefixCount); }
        PdfDictionary dictionary = entries is null ? CompleteDictionaryPrefix(ref prefix, prefixCount)
            : ParserContainers.CopyDictionary(Memory, _governor, entries, preserveReceipts);
        if (preserveReceipts) { _dictionaryReceipts = entries; }
        if (!AllowStreams) { return dictionary; }
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

    private PdfName FreshFakeKey(PdfNameTable<ParsedValue> entries, ref long index)
    {
        Span<byte> text = stackalloc byte[32];
        "QPDFFake"u8.CopyTo(text);
        while (true)
        {
            ChargeWork(1L);
            index = checked(index + 1L);
            if (!Utf8Formatter.TryFormat(index, text[8..], out int count))
            {
                throw new QpdfParseException("Dictionary repair key exceeds its supported length.", null, null);
            }

            long memory = count + 8L + 64L;
            long scratch = count + 8L + 32L;
            ReserveMemory(checked(memory + scratch));
            PdfName candidate = new(text[..(count + 8)].ToArray());
            ReleaseMemory(scratch);
            if (!entries.TryGetValue(candidate, _governor, _governor.CancellationToken, out _))
            {
                return candidate;
            }

            ReleaseMemory(memory);
        }
    }

    private QpdfObject ParseStream(PdfTokenizer tokenizer, PdfDictionary dictionary, long offset)
    {
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

        long length;
        long afterStream;
        try
        {
            length = ResolveStreamLength(dictionary, offset);
            if (length > int.MaxValue)
            {
                throw new QpdfParseException($"Stream length {length} exceeds the supported maximum.", offset, null);
            }
            if (length > data.Length - cursor)
            {
                throw new QpdfParseException($"Stream declares {length} bytes but only {data.Length - cursor} remain.", offset, null);
            }
            tokenizer.SetPosition(cursor + length);
            if (!tokenizer.TryReadToken(out PdfToken end, false, _governor) || end.Kind != PdfTokenKind.EndStream)
            {
                throw new QpdfParseException("Expected 'endstream' after stream data.", offset, null);
            }
            afterStream = tokenizer.Position;
        }
        catch (QpdfParseException error) when (TolerateObjectErrors)
        {
            WarnRepair(error.Message, offset);
            (length, afterStream) = RecoverStreamLength(data, checked((int)cursor));
        }

        if (BorrowImmutableStreamData && length != 0L)
        {
            ReserveMemory(64L);
            // Keep the existing work ceiling and bounded cancellation even
            // though the private job no longer needs to copy these bytes.
            for (long visited = 0L; visited < length;)
            {
                int count = (int)Math.Min(32768L, length - visited);
                ChargeWork(count); _governor.CheckCancellation(); visited += count;
            }
            PdfStream borrowed = PdfStream.FromImmutableSlice(dictionary, data.Slice((int)cursor, (int)length));
            BorrowedStreamData = true;
            tokenizer.SetPosition(afterStream);
            return borrowed;
        }

        // The parser creates this buffer exclusively and relinquishes it to
        // the immutable stream; the public defensive-copy constructor is not
        // needed here. Admit the payload and wrapper before allocating.
        ReserveMemory(checked(length + 64L));
        // Every byte is filled before the stream escapes. Avoid clearing the
        // destination first, and use the writer's bounded bulk-copy size.
        byte[] bytes = GC.AllocateUninitializedArray<byte>((int)length);
        for (int start = 0; start < bytes.Length; start += Math.Min(32768, bytes.Length - start))
        {
            int count = Math.Min(32768, bytes.Length - start);
            ChargeWork(count);
            _governor.CheckCancellation();
            data.Slice((int)cursor + start, count).CopyTo(bytes.AsMemory(start, count));
        }
        tokenizer.SetPosition(afterStream);

        return PdfStream.FromOwnedData(dictionary, bytes);
    }

    private (long Length, long AfterStream) RecoverStreamLength(ReadOnlyMemory<byte> data, int start)
    {
        WarnRepair("Attempting to recover stream length.", start);
        int limit = _resolver is IStreamRecoveryResolver resolver
            ? checked((int)resolver.GetStreamRecoveryEnd(start)) : data.Length;
        ReadOnlySpan<byte> span = data.Span;
        int checkedThrough = start;
        for (int position = start; position < limit; position++)
        {
            if (position == checkedThrough)
            {
                int count = Math.Min(4096, limit - position);
                ChargeWork(count);
                _governor.CheckCancellation();
                checkedThrough = position + count;
            }
            int tokenLength = span.Slice(position, limit - position).StartsWith("endstream"u8) ? 9
                : span.Slice(position, limit - position).StartsWith("endobj"u8) ? 6 : 0;
            if (tokenLength == 0 || (position + tokenLength < limit && !PdfTokenizer.IsDelimiter(span[position + tokenLength])))
            {
                continue;
            }
            long length = position - start;
            if (length == 0L) { WarnRepair("Unable to recover stream data; treating stream as empty.", start); }
            else { WarnRecoveredStreamLength(length, start); }
            // An endobj marker substitutes for a missing endstream. Leave it
            // for the ordinary indirect-object parser to consume once.
            return (length, tokenLength == 9 ? position + tokenLength : position);
        }
        WarnRepair("Unable to recover stream data; treating stream as empty.", start);
        return (0L, limit);
    }

    private void WarnRecoveredStreamLength(long length, long offset)
    {
        if (!SuppressRepairDiagnostics)
        {
            _diagnostics.AddParts(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning,
                ["Recovered stream length at offset ", DiagnosticPart.Invariant(offset), ": ", DiagnosticPart.Invariant(length)]);
        }
    }

    private long ResolveStreamLength(PdfDictionary dictionary, long offset)
    {
        if (!PdfDictionaryLookup.TryGet(dictionary, "Length"u8, _governor, _governor.CancellationToken, out QpdfObject raw))
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
        int length = UnescapeNameCore(span, Span<byte>.Empty, offset, false);
        ReserveMemory(checked(64L + length));
        byte[] result = GC.AllocateUninitializedArray<byte>(length);
        _ = UnescapeNameCore(span, result, offset, true);
        return result;
    }

    private int UnescapeNameCore(ReadOnlySpan<byte> span, Span<byte> result, long offset, bool write)
    {
        int outCount = 0;
        long nextWork = 0L;
        bool warned = false;
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

                    // An invalid hash must remain distinguishable from #23.
                    // Zero cannot be decoded from a valid name token: #00 is
                    // rejected by the tokenizer before reaching this routine.
                    if (write && !warned)
                    {
                        WarnRepair("Invalid name escape retained with a repair marker.", offset);
                        warned = true;
                    }
                    if (write) { result[outCount] = 0; }
                    outCount++;
                    continue;
                }

                if (write) { result[outCount] = (byte)((hi << 4) | lo); }
                outCount += 1;
                i += 2;
                continue;
            }

            if (write) { result[outCount] = span[i]; }
            outCount += 1;
        }

        _governor.CheckCancellation();
        return outCount;
    }

    private byte[] UnescapeLiteral(ReadOnlyMemory<byte> raw, long offset)
    {
        ReadOnlySpan<byte> span = raw.Span;
        int length = UnescapeLiteralCore(span, Span<byte>.Empty, offset, false);
        ReserveMemory(checked(32L + length));
        byte[] result = GC.AllocateUninitializedArray<byte>(length);
        _ = UnescapeLiteralCore(span, result, offset, true);
        return result;
    }

    private int UnescapeLiteralCore(ReadOnlySpan<byte> span, Span<byte> result, long offset, bool write)
    {
        int outCount = 0;
        long nextWork = 0L;
        for (int i = 0; i < span.Length; i++)
        {
            ChargeUnescapeWork(i, ref nextWork);
            if (span[i] != (byte)'\\')
            {
                // Raw CR and CRLF are a single PDF end-of-line marker. An
                // escaped \r below is a literal carriage return instead.
                if (write) { result[outCount] = span[i] == (byte)'\r' ? (byte)'\n' : span[i]; }
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
                if (write) { result[outCount] = (byte)'\n'; }
                outCount += 1;
            }
            else if (b == (byte)'r')
            {
                if (write) { result[outCount] = (byte)'\r'; }
                outCount += 1;
            }
            else if (b == (byte)'t')
            {
                if (write) { result[outCount] = (byte)'\t'; }
                outCount += 1;
            }
            else if (b == (byte)'b')
            {
                if (write) { result[outCount] = (byte)'\b'; }
                outCount += 1;
            }
            else if (b == (byte)'f')
            {
                if (write) { result[outCount] = (byte)'\f'; }
                outCount += 1;
            }
            else if (b == (byte)'(' || b == (byte)')' || b == (byte)'\\')
            {
                if (write) { result[outCount] = b; }
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

                if (write) { result[outCount] = (byte)(value & 0xFF); }
                outCount += 1;
            }
            else
            {
                if (write) { result[outCount] = b; }
                outCount += 1;
            }
        }

        _governor.CheckCancellation();
        return outCount;
    }

    private void ChargeUnescapeWork(int position, ref long nextWork)
    {
        // Escapes advance by varying numbers of bytes; equality/modulo checks
        // can miss an interval when an escape steps across its boundary.
        if (position >= nextWork)
        {
            ChargeWork(1L);
            _governor.CheckCancellation();
            nextWork = (long)position + 4096L;
        }
    }

    private byte[] DecodeHex(ReadOnlyMemory<byte> raw, long offset)
    {
        ReadOnlySpan<byte> span = raw.Span;
        // Count/validate directly over the token, then fill the exact result.
        // Whitespace does not require a full token-sized intermediate buffer.
        int count = 0;
        long nextWork = 0L;
        for (int i = 0; i < span.Length; i++)
        {
            ChargeUnescapeWork(i, ref nextWork);
            byte b = span[i];
            if (PdfTokenizer.IsWhitespace(b))
            {
                continue;
            }

            int value = HexValue(b);
            if (value < 0)
            {
                throw new QpdfParseException($"Invalid hexadecimal digit 0x{b:X2}.", offset, null);
            }

            count += 1;
        }

        int outCount = count / 2 + count % 2;
        _governor.CheckCancellation();
        ReserveMemory(checked(32L + outCount));
        byte[] result = GC.AllocateUninitializedArray<byte>(outCount);
        nextWork = 0L;
        int high = -1, position = 0;
        for (int i = 0; i < span.Length; i++)
        {
            ChargeUnescapeWork(i, ref nextWork);
            byte b = span[i];
            if (PdfTokenizer.IsWhitespace(b)) { continue; }
            int value = HexValue(b);
            if (high < 0) { high = value; }
            else
            {
                result[position++] = (byte)((high << 4) | value);
                high = -1;
            }
        }
        if (high >= 0) { result[position] = (byte)(high << 4); }
        _governor.CheckCancellation();
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
        => CopyNumericTextCore(data, null);

    internal string CopyNumericTextObserved(ReadOnlyMemory<byte> data, Action observe)
    {
        ArgumentNullException.ThrowIfNull(observe);
        return CopyNumericTextCore(data, observe);
    }

    private string CopyNumericTextCore(ReadOnlyMemory<byte> data, Action? observe)
    {
        // Canonical single digits need no new immutable string. Keep the same
        // work/cancellation observation; signs and leading zeros retain their text.
        if (data.Length == 1 && data.Span[0] is >= (byte)'0' and <= (byte)'9')
        {
            _token.ThrowIfCancellationRequested();
            _governor.ChargeWork(1L);
            observe?.Invoke();
            _governor.CheckCancellation();
            return PdfInteger.GetSingleDigitText(data.Span[0] - (byte)'0');
        }
        // The tokenizer established ASCII numeric syntax. Copy directly into
        // the admitted string, without a second full-size character array.
        return string.Create(data.Length, (Data: data, Governor: _governor, Token: _token, Observe: observe), static (chars, state) =>
        {
            ReadOnlySpan<byte> bytes = state.Data.Span;
            for (int i = 0; i < chars.Length; i++)
            {
                if ((i & 4095) == 0)
                {
                    state.Token.ThrowIfCancellationRequested();
                    state.Governor.ChargeWork(1L);
                    state.Observe?.Invoke();
                    state.Governor.CheckCancellation();
                }

                chars[i] = (char)bytes[i];
            }
        });
    }
}
