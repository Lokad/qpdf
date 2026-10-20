using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

/// <summary>Whether a file carries usable linearization hint tables.</summary>
internal enum LinearizationStatus
{
    NotLinearized = 0,
    Valid = 1,
    Invalid = 2,
}

/// <summary>Tri-state outcome of linearization inspection.</summary>
internal readonly record struct LinearizationInspection(LinearizationStatus Status, int SharedGroups, string ErrorMessage);

/// <summary>Retained linearization parameter and hint-table values.</summary>
internal sealed record LinearizationData(
    long FileSize,
    long FirstPageObject,
    long FirstPageEnd,
    int PageCount,
    long XrefZeroOffset,
    int LindictObject,
    int HintObject,
    long FirstPage,
    long HintOffset,
    long HintLength,
    PageOffsetTable PageTable,
    SharedObjectTable SharedTable,
    OutlineTable? Outlines);

internal sealed record PageOffsetTable(
    long MinObjects,
    long FirstPageOffset,
    int DeltaObjectsBits,
    long MinPageLength,
    int DeltaLengthBits,
    long MinContentOffset,
    int DeltaContentOffsetBits,
    long MinContentLength,
    int DeltaContentLengthBits,
    int SharedCountBits,
    int IdentifierBits,
    int NumeratorBits,
    int SharedDenominator,
    IReadOnlyList<PageOffsetEntry> Entries);

internal sealed record PageOffsetEntry(
    long ObjectCount,
    long PageLength,
    long ContentOffset,
    long ContentLength,
    IReadOnlyList<int> SharedIdentifiers,
    IReadOnlyList<int> SharedNumerators);

internal sealed record SharedObjectTable(
    int FirstSharedObject,
    long FirstSharedOffset,
    int FirstPageCount,
    int TotalCount,
    int ObjectCountBits,
    long MinGroupLength,
    int DeltaGroupBits,
    IReadOnlyList<SharedObjectEntry> Entries);

internal sealed record SharedObjectEntry(
    long GroupLength,
    bool SignaturePresent,
    long MemberCount);

internal sealed record OutlineTable(
    int FirstObject,
    long FirstOffset,
    int ObjectCount,
    long GroupLength);

/// <summary>
/// Structural validation of linearized files: detection, linearization
/// dictionary checks, hint-stream resolution and decoding, and governed
/// bit-level parsing of the page-offset, shared-object, and outline tables.
/// Physical claims are independently checked with the shared reader and graph classifier.
/// </summary>
internal static class LinearizationCheck
{
    private static readonly PdfName FilterKey = PdfName.FromAscii("Filter");
    private static readonly PdfName DecodeParmsKey = PdfName.FromAscii("DecodeParms");
    private const int ScanBytes = 1024;
    private const int ScanTokens = 200;

    internal static QpdfCheckOutcome Check(
        DocumentEngine engine,
        int pageCount,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        LinearizationInspection inspection = Inspect(engine, pageCount, limits, governor, cancellationToken);
        if (inspection.Status == LinearizationStatus.NotLinearized)
        {
            return CheckOutcome.Literal(governor, "linearization", QpdfCheckStatus.Passed, "File is not linearized.");
        }
        if (inspection.Status == LinearizationStatus.Invalid)
        {
            return CheckOutcome.Create(governor, "linearization", QpdfCheckStatus.Failed, [inspection.ErrorMessage]);
        }

        return CheckOutcome.Create(governor, "linearization", QpdfCheckStatus.Passed,
            ["Linearized file with ", pageCount, " pages; ", inspection.SharedGroups, " shared groups; hint tables valid."]);
    }

    internal static LinearizationInspection Inspect(
        DocumentEngine engine,
        int pageCount,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        try
        {
            using LinearizationDataOwner? owner = TryReadOwnedData(engine, pageCount, limits, governor, cancellationToken);
            if (owner is null)
            {
                return new LinearizationInspection(LinearizationStatus.NotLinearized, 0, string.Empty);
            }

            LinearizationData data = owner.Data;
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            using Writing.ClassifiedGraph graph = LinearizationGraph.Classify(engine, data.LindictObject,
                data.HintObject, memory, governor, cancellationToken);
            LinearizationPhysical.Validate(engine, data, graph, limits, governor, cancellationToken);
            int shared = data.SharedTable.TotalCount;
            return new LinearizationInspection(
                LinearizationStatus.Valid,
                shared,
                string.Empty);
        }
        catch (QpdfException ex) when (ex is not (QpdfLimitException or QpdfMemoryAdmissionException))
        {
            return new LinearizationInspection(LinearizationStatus.Invalid, 0, ex.Message);
        }
    }

    private static (PdfDictionary? Dictionary, int Number) FindDictionary(
        ReadOnlyMemory<byte> data,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken,
        ParserMemoryScope memory)
    {
        int length = Math.Min(ScanBytes, data.Length);
        PdfTokenizer tokenizer = new(data.Slice(0, length));
        limits.Validate();
        PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null);
        for (int i = 0; i < ScanTokens; i++)
        {
            using ParserMemoryScope trial = ParserMemoryScope.Create(governor);
            cancellationToken.ThrowIfCancellationRequested();
            long position = 0;
            try
            {
                if (!tokenizer.TryReadToken(out PdfToken token))
                {
                    return (null, 0);
                }

                if (token.Kind != PdfTokenKind.Integer)
                {
                    continue;
                }

                position = token.Offset;
                tokenizer.SetPosition(position);
                (int number, int _, QpdfObject value) = parser.ParseIndirectObject(tokenizer, 0, trial);
                if (value is PdfDictionary dictionary
                    && dictionary.TryGet(PdfName.FromAscii("Linearized"), out QpdfObject raw)
                    && IsOne(raw))
                {
                    if (dictionary.TryGet(PdfName.FromAscii("L"), out QpdfObject size)
                        && size is PdfInteger total
                        && total.Value == data.Length)
                    {
                        trial.TransferTo(memory);
                        return (dictionary, number);
                    }

                    return (null, 0);
                }
            }
            catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException))
            {
                try
                {
                    tokenizer.SetPosition(position + 1);
                }
                catch (QpdfParseException)
                {
                    return (null, 0);
                }
            }
        }

        return (null, 0);
    }

    private static bool IsOne(QpdfObject raw)
    {
        return raw is PdfInteger integer && integer.Value == 1L
            || raw is PdfReal real && real.Value == 1.0;
    }

    private static long RequireInteger(PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject raw) || raw is not PdfInteger integer)
        {
            throw new QpdfParseException($"Linearization dictionary key /{key} must be an integer.", null, null);
        }

        return integer.Value;
    }

    private sealed record LindictData(
        long[] Spans,
        long FirstPageObject,
        long FirstPageEnd,
        long DeclaredPages,
        long XrefZero,
        long FirstPage);

    private static LindictData CheckDictionary(PdfDictionary lindict, int fileLength, int pageCount, DocumentEngine engine, ParserMemoryScope memory)
    {
        if (!lindict.TryGet(PdfName.FromAscii("H"), out QpdfObject hints)
            || hints is not PdfArray table
            || (table.Count != 2 && table.Count != 4))
        {
            throw new QpdfParseException("Linearization dictionary key /H must hold 2 or 4 integers.", null, null);
        }

        memory.Reserve(80L + 32L + sizeof(long) * (long)table.Count);
        long[] spans = new long[table.Count];
        for (int i = 0; i < table.Count; i++)
        {
            if (table[i] is not PdfInteger integer || integer.Value < 0L)
            {
                throw new QpdfParseException("Linearization dictionary key /H must hold non-negative integers.", null, null);
            }

            spans[i] = integer.Value;
        }

        if (spans[0] > fileLength || spans[1] > fileLength - spans[0])
        {
            throw new QpdfParseException("Primary hint table exceeds the file.", null, null);
        }

        if (spans.Length == 4 && (spans[2] > fileLength || spans[3] > fileLength - spans[2]))
        {
            throw new QpdfParseException("Overflow hint table exceeds the file.", null, null);
        }

        long firstPageObject = RequireInteger(lindict, "O");
        long firstPageEnd = RequireInteger(lindict, "E");
        long declaredPages = RequireInteger(lindict, "N");
        long xrefZero = RequireInteger(lindict, "T");
        if (firstPageObject < 1L || firstPageObject > 2147483647L || !engine.XRefEntries.ContainsKey((int)firstPageObject))
        {
            throw new QpdfParseException("Linearization dictionary key /O names no known object.", null, null);
        }

        if (firstPageEnd < 0L || firstPageEnd > fileLength)
        {
            throw new QpdfParseException("Linearization dictionary key /E is out of range.", null, null);
        }

        if (declaredPages != pageCount)
        {
            throw new QpdfParseException($"Linearization dictionary key /N holds {declaredPages} for {pageCount} pages.", null, null);
        }

        if (xrefZero < 0L || xrefZero > fileLength)
        {
            throw new QpdfParseException("Linearization dictionary key /T is out of range.", null, null);
        }

        long firstPage = 0L;
        if (lindict.TryGet(PdfName.FromAscii("P"), out QpdfObject first))
        {
            if (first is not PdfInteger firstNumber)
            {
                throw new QpdfParseException("Linearization dictionary key /P must be an integer.", null, null);
            }

            firstPage = firstNumber.Value;
        }

        return new LindictData(spans, firstPageObject, firstPageEnd, declaredPages, xrefZero, firstPage);
    }

    internal static LinearizationDataOwner? TryReadOwnedData(
        DocumentEngine engine,
        int pageCount,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
        => TryReadOwnedDataCore(engine, pageCount, limits, governor, cancellationToken, null);

    // Observes only borrowed bytes during the callback; ordinary callers keep
    // no observer. This supplies independent backing/cancellation measurements.
    internal static LinearizationDataOwner? TryReadOwnedDataObserved(DocumentEngine engine, int pageCount,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken,
        Action<ReadOnlyMemory<byte>> observe)
    {
        ArgumentNullException.ThrowIfNull(observe);
        return TryReadOwnedDataCore(engine, pageCount, limits, governor, cancellationToken, observe);
    }

    private static LinearizationDataOwner? TryReadOwnedDataCore(DocumentEngine engine, int pageCount,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken,
        Action<ReadOnlyMemory<byte>>? observe)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        (PdfDictionary? lindict, int lindictNumber) = FindDictionary(engine.Data, limits, governor, cancellationToken, memory);
        if (lindict is null)
        {
            return null;
        }

        LindictData fields = CheckDictionary(lindict, engine.Data.Length, pageCount, engine, memory);
        return ReadData(engine, fields, lindictNumber, pageCount, limits, governor, cancellationToken, observe);
    }

    private static LinearizationDataOwner ReadData(
        DocumentEngine engine,
        LindictData fields,
        int lindictNumber,
        int pageCount,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken,
        Action<ReadOnlyMemory<byte>>? observe)
    {
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(192L); // Three bounded bit-reader cursors.
        long[] spans = fields.Spans;
        PdfStream primary = ResolveHintObject(engine, spans[0], governor, out int hintNumber);
        byte[] first = DecodeHintStream(primary, limits, governor, scratch, cancellationToken);
        observe?.Invoke(first);
        byte[] combined;
        if (spans.Length == 2)
        {
            combined = first;
        }
        else
        {
            PdfStream overflow = ResolveHintObject(engine, spans[2], governor, out int _);
            byte[] rest = DecodeHintStream(overflow, limits, governor, scratch, cancellationToken);
            observe?.Invoke(rest);
            combined = AllocateHintArray<byte>(scratch, checked(first.Length + rest.Length), sizeof(byte), governor, cancellationToken);
            GovernedBufferStream.CopyBytes(first, combined, governor, cancellationToken, CancellationToken.None);
            GovernedBufferStream.CopyBytes(rest, combined.AsSpan(first.Length), governor, cancellationToken, CancellationToken.None);
            observe?.Invoke(combined);
        }

        long sharedAt = RequireInteger(primary.Dictionary, "S");
        if (sharedAt < 0L || sharedAt >= combined.Length)
        {
            throw new QpdfParseException("Hint table shared-object offset is out of bounds.", null, null);
        }

        long outlineAt = -1L;
        if (primary.Dictionary.TryGet(PdfName.FromAscii("O"), out QpdfObject outline)
            && outline is not PdfNull)
        {
            if (outline is not PdfInteger outlineOffset || outlineOffset.Value < 0L || outlineOffset.Value >= combined.Length)
            {
                throw new QpdfParseException("Hint table outline offset is out of bounds.", null, null);
            }

            outlineAt = outlineOffset.Value;
        }

        HintBitReader shared = new(combined, checked(sharedAt * 8L), governor, cancellationToken);
        int firstSharedObject = (int)shared.ReadBits(32);
        long firstSharedOffset = shared.ReadBits(32);
        long firstPageShared = shared.ReadBits(32);
        long totalShared = shared.ReadBits(32);
        int objectWidth = (int)shared.ReadBits(16);
        long minGroupLength = shared.ReadBits(32);
        int groupWidth = (int)shared.ReadBits(16);
        if (totalShared < 0L || totalShared > engine.XRefEntries.Count)
        {
            throw new QpdfParseException("Shared-object hint table names too many groups.", null, null);
        }

        if (firstPageShared < 0L || firstPageShared > totalShared)
        {
            throw new QpdfParseException("Shared-object hint table first-page count exceeds its total.", null, null);
        }

        int groups = (int)totalShared;
        OwnedAllocationBatch allocations = OwnedAllocationBatch.Create(governor, cancellationToken);
        try
        {
            long[] groupDeltas = AllocateHintArray<long>(scratch, groups, sizeof(long), governor, cancellationToken);
            for (int group = 0; group < groups; group++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                groupDeltas[group] = shared.ReadBits(groupWidth);
            }
            shared.AlignToByte();

            bool[] signatures = AllocateHintArray<bool>(scratch, groups, sizeof(bool), governor, cancellationToken);
            for (int group = 0; group < groups; group++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                signatures[group] = shared.ReadBits(1) == 1L;
            }
            shared.AlignToByte();

            foreach (bool signature in signatures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.CheckCancellation();
                if (signature)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        shared.ReadBits(32);
                    }
                }
            }

            long[] memberDeltas = AllocateHintArray<long>(scratch, groups, sizeof(long), governor, cancellationToken);
            for (int group = 0; group < groups; group++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                memberDeltas[group] = shared.ReadBits(objectWidth);
            }
            shared.AlignToByte();

            SharedObjectEntry[] sharedEntries = AllocateModelArray<SharedObjectEntry>(allocations, groups, IntPtr.Size);
            for (int group = 0; group < groups; group++)
            {
                allocations.Memory.Reserve(48L);
                SharedObjectEntry entry = new(minGroupLength + groupDeltas[group], signatures[group], memberDeltas[group] + 1L);
                allocations.Add(48L);
                sharedEntries[group] = entry;
            }

            allocations.Memory.Reserve(80L);
            SharedObjectTable sharedTable = new(
                firstSharedObject, firstSharedOffset, (int)firstPageShared, groups,
                objectWidth, minGroupLength, groupWidth, sharedEntries);
            allocations.Add(80L);

            HintBitReader pages = new(combined, governor, cancellationToken);
            long minObjects = pages.ReadBits(32);
            long firstPageOffset = pages.ReadBits(32);
            int deltaObjectsBits = (int)pages.ReadBits(16);
            long minPageLength = pages.ReadBits(32);
            int deltaLengthBits = (int)pages.ReadBits(16);
            long minContentOffset = pages.ReadBits(32);
            int deltaContentOffsetBits = (int)pages.ReadBits(16);
            long minContentLength = pages.ReadBits(32);
            int deltaContentLengthBits = (int)pages.ReadBits(16);
            int sharedCountBits = (int)pages.ReadBits(16);
            int identifierBits = (int)pages.ReadBits(16);
            int numeratorBits = (int)pages.ReadBits(16);
            int sharedDenominator = (int)pages.ReadBits(16);

            long[] deltaObjects = AllocateHintArray<long>(scratch, pageCount, sizeof(long), governor, cancellationToken);
            for (int page = 0; page < pageCount; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                deltaObjects[page] = pages.ReadBits(deltaObjectsBits);
            }
            pages.AlignToByte();

            long[] deltaLengths = AllocateHintArray<long>(scratch, pageCount, sizeof(long), governor, cancellationToken);
            for (int page = 0; page < pageCount; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                deltaLengths[page] = pages.ReadBits(deltaLengthBits);
            }
            pages.AlignToByte();

            long[] sharedCounts = AllocateHintArray<long>(scratch, pageCount, sizeof(long), governor, cancellationToken);
            for (int page = 0; page < pageCount; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                long sharedCount = pages.ReadBits(sharedCountBits);
                if (sharedCount < 0L || sharedCount > totalShared)
                {
                    throw new QpdfParseException("Page hint table names more shared objects than exist.", null, null);
                }

                sharedCounts[page] = sharedCount;
            }
            pages.AlignToByte();

            int[][] identifiers = AllocateHintArray<int[]>(scratch, pageCount, IntPtr.Size, governor, cancellationToken);
            int[][] numerators = AllocateHintArray<int[]>(scratch, pageCount, IntPtr.Size, governor, cancellationToken);
            for (int page = 0; page < pageCount; page++)
            {
                int count = (int)sharedCounts[page];
                int[] ids = AllocateModelArray<int>(allocations, count, sizeof(int));
                for (int k = 0; k < count; k++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    governor.NoteOperation();
                    long id = pages.ReadBits(identifierBits);
                    if (id < 0L || id >= totalShared)
                    {
                        throw new QpdfParseException("Page hint table names an unknown shared object.", null, null);
                    }

                    ids[k] = (int)id;
                }

                identifiers[page] = ids;
                numerators[page] = AllocateModelArray<int>(allocations, count, sizeof(int));
            }
            pages.AlignToByte();

            for (int page = 0; page < pageCount; page++)
            {
                int count = identifiers[page].Length;
                for (int k = 0; k < count; k++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    governor.NoteOperation();
                    numerators[page][k] = (int)pages.ReadBits(numeratorBits);
                }
            }
            pages.AlignToByte();

            long[] contentOffsets = AllocateHintArray<long>(scratch, pageCount, sizeof(long), governor, cancellationToken);
            for (int page = 0; page < pageCount; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                contentOffsets[page] = pages.ReadBits(deltaContentOffsetBits);
            }
            pages.AlignToByte();

            long[] contentLengths = AllocateHintArray<long>(scratch, pageCount, sizeof(long), governor, cancellationToken);
            for (int page = 0; page < pageCount; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                contentLengths[page] = pages.ReadBits(deltaContentLengthBits);
            }
            pages.AlignToByte();

            PageOffsetEntry[] pageEntries = AllocateModelArray<PageOffsetEntry>(allocations, pageCount, IntPtr.Size);
            for (int page = 0; page < pageCount; page++)
            {
                allocations.Memory.Reserve(64L);
                PageOffsetEntry entry = new(minObjects + deltaObjects[page], minPageLength + deltaLengths[page],
                    minContentOffset + contentOffsets[page], minContentLength + contentLengths[page], identifiers[page], numerators[page]);
                allocations.Add(64L);
                pageEntries[page] = entry;
            }

            allocations.Memory.Reserve(144L);
            PageOffsetTable pageTable = new(
                minObjects, firstPageOffset, deltaObjectsBits, minPageLength, deltaLengthBits,
                minContentOffset, deltaContentOffsetBits, minContentLength, deltaContentLengthBits,
                sharedCountBits, identifierBits, numeratorBits, sharedDenominator, pageEntries);
            allocations.Add(144L);

            OutlineTable? outlines = null;
            if (outlineAt >= 0L)
            {
                HintBitReader outlineReader = new(combined, checked(outlineAt * 8L), governor, cancellationToken);
                int outlineFirst = (int)outlineReader.ReadBits(32);
                long outlineOffset = outlineReader.ReadBits(32);
                int outlineCount = (int)outlineReader.ReadBits(32);
                long outlineLength = outlineReader.ReadBits(32);
                allocations.Memory.Reserve(48L);
                outlines = new OutlineTable(outlineFirst, outlineOffset, outlineCount, outlineLength);
                allocations.Add(48L);
            }

            allocations.Memory.Reserve(128L + 64L);
            LinearizationData data = new(engine.Data.Length, fields.FirstPageObject, fields.FirstPageEnd, pageCount,
                fields.XrefZero, lindictNumber, hintNumber, fields.FirstPage, spans[0], spans[1], pageTable, sharedTable, outlines);
            allocations.Add(128L);
            return new LinearizationDataOwner(allocations, data);
        }
        catch { allocations.Dispose(); throw; }
    }

    private static T[] AllocateHintArray<T>(ParserMemoryScope memory, int count, long itemBytes,
        ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (count == 0) { return Array.Empty<T>(); }
        if (count < 0 || count > Array.MaxLength) { throw new QpdfParseException("Hint table exceeds the supported capacity.", null, null); }
        memory.Reserve(checked(32L + count * itemBytes));
        governor.NoteOperation();
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        return new T[count];
    }

    private static T[] AllocateModelArray<T>(OwnedAllocationBatch allocations, int count, long itemBytes)
    {
        T[] array = AllocateHintArray<T>(allocations.Memory, count, itemBytes, allocations.Governor, allocations.Token);
        if (count > 0) { allocations.Add(checked(32L + count * itemBytes)); }
        return array;
    }

    private static PdfStream ResolveHintObject(DocumentEngine engine, long offset, ResourceGovernor governor, out int number)
    {
        governor.NoteOperation();
        foreach (System.Collections.Generic.KeyValuePair<int, XrefEntry> entry in engine.XRefEntries)
        {
            if (entry.Value.Kind == XrefEntryKind.Uncompressed && entry.Value.First == offset)
            {
                number = entry.Key;
                QpdfObject value = engine.Load(entry.Key);
                if (value is PdfStream stream)
                {
                    return stream;
                }

                throw new QpdfParseException($"No hint stream starts at offset {offset}.", offset, entry.Key);
            }
        }

        throw new QpdfParseException($"No object starts at hint offset {offset}.", offset, null);
    }

    private static byte[] DecodeHintStream(PdfStream stream, QpdfLimits limits, ResourceGovernor governor,
        ParserMemoryScope memory, CancellationToken token)
    {
        byte[] raw = stream.ArrayData(memory, governor, token);
        if (!stream.Dictionary.TryGet(FilterKey, out QpdfObject filter) || filter is PdfNull)
        {
            return raw;
        }

        if (filter is PdfName name && name.RawSpan.SequenceEqual("FlateDecode"u8))
        {
            if (stream.Dictionary.TryGet(DecodeParmsKey, out QpdfObject parms) && parms is not PdfNull)
            {
                throw new QpdfParseException("Hint streams with decode parameters are unsupported.", null, null);
            }

            byte[] decoded = FlateFilter.Decode(raw, null, limits, governor, token);
            if (decoded.Length > 0) { memory.AdoptReserved(32L + decoded.Length); }
            return decoded;
        }

        throw new QpdfParseException("Hint streams must be raw or Flate-encoded.", null, null);
    }

    private sealed class HintBitReader
    {
        private readonly byte[] _data;
        private long _position;
        private readonly ResourceGovernor _governor;
        private readonly CancellationToken _token;

        internal HintBitReader(byte[] data, ResourceGovernor governor, CancellationToken token)
            : this(data, 0L, governor, token)
        {
        }

        internal HintBitReader(byte[] data, long startBits, ResourceGovernor governor, CancellationToken token)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _governor = governor;
            _token = token;
            _position = startBits >= 0L ? startBits : throw new ArgumentOutOfRangeException(nameof(startBits), "Bit position must not be negative.");
        }

        internal long ReadBits(int count)
        {
            _token.ThrowIfCancellationRequested();
            _governor.CheckCancellation();
            if (count < 0 || count > 32)
            {
                throw new QpdfParseException("Hint table requests an invalid bit width.", _position, null);
            }

            if (count == 0)
            {
                return 0L;
            }

            if (checked(_position + count) > checked((long)_data.Length * 8L))
            {
                throw new QpdfParseException("Hint table overruns its stream.", _position, null);
            }

            long value = 0L;
            for (int i = 0; i < count; i++)
            {
                long at = checked(_position + i);
                int slot = (int)(at >> 3);
                int shift = 7 - (int)(at & 7L);
                value = checked((value << 1) | (((uint)_data[slot] >> shift) & 1u));
            }

            _position = checked(_position + count);
            return value;
        }

        internal void AlignToByte()
        {
            long rest = _position & 7L;
            if (rest != 0L)
            {
                _position = checked(_position + (8L - rest));
                if (_position > checked((long)_data.Length * 8L))
                {
                    throw new QpdfParseException("Hint table overruns its stream.", _position >> 3, null);
                }
            }
        }
    }
}
