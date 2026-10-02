using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

/// <summary>
/// Structural validation of linearized files: detection, linearization
/// dictionary checks, hint-stream resolution and decoding, and governed
/// bit-level parsing of the page-offset, shared-object, and outline tables.
/// Object-identity cross-checks against computed part assignments arrive
/// with the linearization writer.
/// </summary>
internal static class LinearizationCheck
{
    private const int ScanBytes = 1024;
    private const int ScanTokens = 200;

    internal static QpdfCheckOutcome Check(
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

        PdfDictionary? lindict = FindDictionary(engine.Data, limits, governor, cancellationToken);
        if (lindict is null)
        {
            return new QpdfCheckOutcome("linearization", QpdfCheckStatus.Passed, "File is not linearized.");
        }

        try
        {
            long[] spans = CheckDictionary(lindict, engine.Data.Length, pageCount, engine);
            int shared = CheckHints(engine, lindict, spans, pageCount, limits, governor, cancellationToken);
            return new QpdfCheckOutcome(
                "linearization",
                QpdfCheckStatus.Passed,
                $"Linearized file with {pageCount} pages; {shared} shared groups; hint tables valid.");
        }
        catch (QpdfException ex)
        {
            return new QpdfCheckOutcome("linearization", QpdfCheckStatus.Failed, ex.Message);
        }
    }

    private static PdfDictionary? FindDictionary(
        ReadOnlyMemory<byte> data,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        int length = Math.Min(ScanBytes, data.Length);
        PdfTokenizer tokenizer = new(data.Slice(0, length));
        limits.Validate();
        PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null);
        for (int i = 0; i < ScanTokens; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long position = 0;
            try
            {
                if (!tokenizer.TryReadToken(out PdfToken token))
                {
                    return null;
                }

                if (token.Kind != PdfTokenKind.Integer)
                {
                    continue;
                }

                position = token.Offset;
                tokenizer.SetPosition(position);
                (int _, int _, QpdfObject value) = parser.ParseIndirectObject(tokenizer, 0);
                if (value is PdfDictionary dictionary
                    && dictionary.TryGet(PdfName.FromAscii("Linearized"), out QpdfObject raw)
                    && IsOne(raw))
                {
                    if (dictionary.TryGet(PdfName.FromAscii("L"), out QpdfObject size)
                        && size is PdfInteger total
                        && total.Value == data.Length)
                    {
                        return dictionary;
                    }

                    return null;
                }
            }
            catch (QpdfException)
            {
                try
                {
                    tokenizer.SetPosition(position + 1);
                }
                catch (QpdfParseException)
                {
                    return null;
                }
            }
        }

        return null;
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

    private static long[] CheckDictionary(PdfDictionary lindict, int fileLength, int pageCount, DocumentEngine engine)
    {
        if (!lindict.TryGet(PdfName.FromAscii("H"), out QpdfObject hints)
            || hints is not PdfArray table
            || (table.Count != 2 && table.Count != 4))
        {
            throw new QpdfParseException("Linearization dictionary key /H must hold 2 or 4 integers.", null, null);
        }

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

        if (lindict.TryGet(PdfName.FromAscii("P"), out QpdfObject first) && first is not PdfInteger)
        {
            throw new QpdfParseException("Linearization dictionary key /P must be an integer.", null, null);
        }

        return spans;
    }

        private static int CheckHints(
        DocumentEngine engine,
        PdfDictionary lindict,
        long[] spans,
        int pageCount,
        QpdfLimits limits,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        PdfStream primary = ResolveHintObject(engine, spans[0], governor);
        byte[] first = DecodeHintStream(primary, limits, governor);
        byte[] combined;
        if (spans.Length == 2)
        {
            combined = first;
        }
        else
        {
            PdfStream overflow = ResolveHintObject(engine, spans[2], governor);
            byte[] rest = DecodeHintStream(overflow, limits, governor);
            governor.ReserveMemory(checked((long)first.Length + rest.Length));
            combined = new byte[checked(first.Length + rest.Length)];
            Buffer.BlockCopy(first, 0, combined, 0, first.Length);
            Buffer.BlockCopy(rest, 0, combined, first.Length, rest.Length);
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

        HintBitReader shared = new(combined, checked(sharedAt * 8L));
        shared.ReadBits(32);
        shared.ReadBits(32);
        long firstPageShared = shared.ReadBits(32);
        long totalShared = shared.ReadBits(32);
        int objectWidth = (int)shared.ReadBits(16);
        shared.ReadBits(32);
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
        for (int group = 0; group < groups; group++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            shared.ReadBits(groupWidth);
        }
        shared.AlignToByte();

        List<long> signatures = new(groups);
        for (int group = 0; group < groups; group++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            signatures.Add(shared.ReadBits(1));
        }
        shared.AlignToByte();

        foreach (long signature in signatures)
        {
            if (signature == 1L)
            {
                for (int k = 0; k < 4; k++)
                {
                    shared.ReadBits(32);
                }
            }
        }

        for (int group = 0; group < groups; group++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            shared.ReadBits(objectWidth);
        }
        shared.AlignToByte();
HintBitReader pages = new(combined);
        long[] header = new long[13];
        int[] widths = [32, 32, 16, 32, 16, 32, 16, 32, 16, 16, 16, 16, 16];
        for (int i = 0; i < header.Length; i++)
        {
            header[i] = pages.ReadBits(widths[i]);
        }

                for (int page = 0; page < pageCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            pages.ReadBits((int)header[2]);
        }
        pages.AlignToByte();

        for (int page = 0; page < pageCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            pages.ReadBits((int)header[4]);
        }
        pages.AlignToByte();

        List<long> pageShared = new(pageCount);
        for (int page = 0; page < pageCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            long sharedCount = pages.ReadBits((int)header[9]);
            if (sharedCount < 0L || sharedCount > totalShared)
            {
                throw new QpdfParseException("Page hint table names more shared objects than exist.", null, null);
            }

            pageShared.Add(sharedCount);
        }
        pages.AlignToByte();

        List<long[]> identifiers = new(pageCount);
        for (int page = 0; page < pageCount; page++)
        {
            int count = (int)pageShared[page];
            long[] ids = new long[count];
            for (int k = 0; k < count; k++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                long id = pages.ReadBits((int)header[10]);
                if (id < 0L || id >= totalShared)
                {
                    throw new QpdfParseException("Page hint table names an unknown shared object.", null, null);
                }

                ids[k] = id;
            }

            identifiers.Add(ids);
        }
        pages.AlignToByte();

        for (int page = 0; page < pageCount; page++)
        {
            int count = identifiers[page].Length;
            for (int k = 0; k < count; k++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                pages.ReadBits((int)header[11]);
            }
        }
        pages.AlignToByte();

        for (int page = 0; page < pageCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            pages.ReadBits((int)header[6]);
        }
        pages.AlignToByte();

        for (int page = 0; page < pageCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            pages.ReadBits((int)header[8]);
        }
        pages.AlignToByte();

if (outlineAt >= 0L)
        {
            HintBitReader outlines = new(combined, checked(outlineAt * 8L));
            outlines.ReadBits(32);
            outlines.ReadBits(32);
            outlines.ReadBits(32);
            outlines.ReadBits(32);
        }

        return groups;
    }

private static PdfStream ResolveHintObject(DocumentEngine engine, long offset, ResourceGovernor governor)
    {
        governor.ChargeWork(engine.XRefEntries.Count);
        foreach (System.Collections.Generic.KeyValuePair<int, XrefEntry> entry in engine.XRefEntries)
        {
            if (entry.Value.Kind == XrefEntryKind.Uncompressed && entry.Value.First == offset)
            {
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

    private static byte[] DecodeHintStream(PdfStream stream, QpdfLimits limits, ResourceGovernor governor)
    {
        byte[] raw = stream.Data;
        if (!stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filter) || filter is PdfNull)
        {
            return raw;
        }

        if (filter is PdfName name && name.DisplayText == "FlateDecode")
        {
            if (stream.Dictionary.TryGet(PdfName.FromAscii("DecodeParms"), out QpdfObject parms) && parms is not PdfNull)
            {
                throw new QpdfParseException("Hint streams with decode parameters are unsupported.", null, null);
            }

            return FlateFilter.Decode(raw, null, limits, governor);
        }

        throw new QpdfParseException("Hint streams must be raw or Flate-encoded.", null, null);
    }

    private sealed class HintBitReader
    {
        private readonly byte[] _data;
        private long _position;

        internal HintBitReader(byte[] data)
            : this(data, 0L)
        {
        }

        internal HintBitReader(byte[] data, long startBits)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _position = startBits >= 0L ? startBits : throw new ArgumentOutOfRangeException(nameof(startBits), "Bit position must not be negative.");
        }

        internal long ReadBits(int count)
        {
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
