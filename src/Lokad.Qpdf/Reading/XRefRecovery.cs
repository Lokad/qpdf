using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

/// <summary>
/// Bounded cross-reference repair, separate from strict resolution so heuristics
/// can never weaken the strict contract. Scans for indirect object headers,
/// trial-parses each candidate, keeps the last valid occurrence, aborts after
/// MaxParserErrors failed trial parses, and fills
/// unscanned gaps as free. The tail trailer wins when its root resolves;
/// otherwise the last scanned catalog wins.
/// </summary>
internal static class XRefRecovery
{
    private const int ScanWindowBytes = 4096;
    private const int MaxHeaderDigits = 10;
    private const int MaxGenerationDigits = 5;
    private const int MaxGeneration = 65535;
    private const int TailScanBytes = 65536;

    internal static XRefTable Recover(
        ReadOnlyMemory<byte> data,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        string strictReason)
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
        long maxId = data.Length / 3L;
        Dictionary<int, XrefEntry> entries = new();
        QpdfLimits damaged = limits with { MaxContainerSize = limits.MaxContainerSizeDamaged };
        int? lastCatalog = ScanObjects(data, damaged, governor, diagnostics, maxId, entries);
        if (entries.Count == 0)
        {
            throw new QpdfParseException($"Bounded repair found no objects after strict parsing failed: {strictReason}", null, null);
        }

        int scannedMax = 0;
        foreach (int number in entries.Keys)
        {
            if (number > scannedMax)
            {
                scannedMax = number;
            }
        }

        int size = scannedMax + 1;
        string rootSource = "a scanned catalog";
        QpdfObject? root = null;
        PdfDictionary? tailDictionary = null;
        if (TryReadTailTrailer(data, damaged, governor, diagnostics, out int tailSize, out QpdfObject? tailRoot, out tailDictionary) && tailSize >= 1)
        {
            if (tailSize <= scannedMax + 1)
            {
                size = tailSize;
            }

            if (tailRoot is PdfReference tailReference && entries.ContainsKey(tailReference.ObjectNumber))
            {
                root = tailRoot;
                rootSource = "the active trailer";
            }
            else if (tailRoot is PdfDictionary)
            {
                root = tailRoot;
                rootSource = "the active trailer";
            }
        }

        if (root is null)
        {
            if (!lastCatalog.HasValue)
            {
                throw new QpdfParseException($"Bounded repair found no catalog after strict parsing failed: {strictReason}", null, null);
            }

            root = new PdfReference(lastCatalog.Value, 0);
        }

        Dictionary<int, XrefEntry> repaired = new();
        foreach (KeyValuePair<int, XrefEntry> entry in entries)
        {
            if (entry.Key >= 1 && entry.Key < size)
            {
                repaired[entry.Key] = entry.Value;
            }
        }

        for (int number = 1; number < size; number++)
        {
            if (!repaired.ContainsKey(number))
            {
                repaired[number] = XrefEntry.Free(0L, 0);
                governor.ReserveMemory(32L);
            }
        }

        Dictionary<PdfName, QpdfObject> trailerEntries = new()
        {
            [PdfName.FromAscii("Size")] = new PdfInteger(size.ToString(), size),
            [PdfName.FromAscii("Root")] = root,
        };
        if (tailDictionary is not null)
        {
            PdfName encryptKey = PdfName.FromAscii("Encrypt");
            if (tailDictionary.TryGet(encryptKey, out QpdfObject? encryptValue) && encryptValue is not null && encryptValue is not PdfNull)
            {
                trailerEntries[encryptKey] = encryptValue;
            }
            PdfName infoKey = PdfName.FromAscii("Info");
            if (tailDictionary.TryGet(infoKey, out QpdfObject? infoValue) && infoValue is not null && infoValue is not PdfNull)
            {
                trailerEntries[infoKey] = infoValue;
            }
            PdfName idKey = PdfName.FromAscii("ID");
            if (tailDictionary.TryGet(idKey, out QpdfObject? idValue) && idValue is not null && idValue is not PdfNull)
            {
                trailerEntries[idKey] = idValue;
            }
        }
        PdfDictionary trailer = new(trailerEntries);
        diagnostics.Add(
            QpdfDiagnosticCode.RecoveryEngaged,
            QpdfDiagnosticSeverity.Warning,
            $"Bounded repair rebuilt {repaired.Count} cross-reference entries from {rootSource} after strict parsing failed: {strictReason}");
        return new XRefTable(-1L, repaired, trailer, new List<PdfDictionary> { trailer });
    }

    private static int? ScanObjects(
        ReadOnlyMemory<byte> data,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        long maxId,
        Dictionary<int, XrefEntry> entries)
    {
        int? lastCatalog = null;
        long errors = 0L;
        ReadOnlySpan<byte> span = data.Span;
        PdfParser parser = new(limits, governor, diagnostics, null);
        long chargedThrough = 0L;
        int position = 0;
        while (position < span.Length)
        {
            if (position - chargedThrough >= ScanWindowBytes)
            {
                governor.ChargeWork(1L);
                chargedThrough = position;
            }

            if (!IsDigit(span[position]))
            {
                position += 1;
                continue;
            }

            int numberStart = position;
            long number = 0L;
            int digits = 0;
            while (position < span.Length && IsDigit(span[position]) && digits < MaxHeaderDigits)
            {
                number = (number * 10L) + (span[position] - (byte)'0');
                position += 1;
                digits += 1;
            }

            if (position < span.Length && IsDigit(span[position]))
            {
                position = numberStart + 1;
                continue;
            }

            int afterNumber = position;
            position = SkipWhitespace(span, position);
            if (position == afterNumber || position >= span.Length || !IsDigit(span[position]))
            {
                position = numberStart + 1;
                continue;
            }

            long generation = 0L;
            int generationDigits = 0;
            while (position < span.Length && IsDigit(span[position]) && generationDigits < MaxGenerationDigits)
            {
                generation = (generation * 10L) + (span[position] - (byte)'0');
                position += 1;
                generationDigits += 1;
            }

            if ((position < span.Length && IsDigit(span[position])) || generation > MaxGeneration)
            {
                position = numberStart + 1;
                continue;
            }

            int afterGeneration = position;
            position = SkipWhitespace(span, position);
            if (position == afterGeneration || position + 3 > span.Length
                || span[position] != (byte)'o' || span[position + 1] != (byte)'b' || span[position + 2] != (byte)'j')
            {
                position = numberStart + 1;
                continue;
            }

            if (number < 1L || number > maxId || number > int.MaxValue)
            {
                position = numberStart + 1;
                continue;
            }

            governor.ChargeWork(1L);
            QpdfObject? value = TryParseObject(data, limits, governor, diagnostics, parser, numberStart, (int)number, (int)generation, out bool errored);
            if (errored)
            {
                errors += 1L;
                if (errors > limits.MaxParserErrors)
                {
                    throw new QpdfParseException($"Bounded repair exceeded the maximum of {limits.MaxParserErrors} parsable errors at object {number}.", null, null);
                }
            }

            if (value is not null)
            {
                entries[(int)number] = XrefEntry.Uncompressed(numberStart, (int)generation);
                governor.ReserveMemory(32L);
                if (value is PdfDictionary dictionary
                    && dictionary.TryGet(PdfName.FromAscii("Type"), out QpdfObject raw)
                    && raw is PdfName name && name.DisplayText == "Catalog")
                {
                    lastCatalog = (int)number;
                }
            }

            position = numberStart + 1;
        }

        return lastCatalog;
    }

    private static QpdfObject? TryParseObject(
        ReadOnlyMemory<byte> data,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        PdfParser parser,
        int offset,
        int number,
        int generation,
        out bool errored)
    {
        try
        {
            PdfTokenizer tokenizer = new(data);
            tokenizer.SetPosition(offset);
            (int foundNumber, int foundGeneration, QpdfObject value) = parser.ParseIndirectObject(tokenizer, 0);
            if (foundNumber == number && foundGeneration == generation)
            {
                errored = false;
                return value;
            }

            errored = false;
            return null;
        }
        catch (QpdfException)
        {
            errored = true;
            return null;
        }
    }

    private static bool TryReadTailTrailer(
        ReadOnlyMemory<byte> data,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        out int size,
        out QpdfObject? root,
        out PdfDictionary? tailDictionary)
    {
        size = 0;
        root = null;
        tailDictionary = null;
        ReadOnlySpan<byte> span = data.Span;
        int scanStart = span.Length > TailScanBytes ? span.Length - TailScanBytes : 0;
        int last = -1;
        for (int i = scanStart; i + 7 <= span.Length; i++)
        {
            if (span[i] == (byte)'t' && span[i + 1] == (byte)'r' && span[i + 2] == (byte)'a'
                && span[i + 3] == (byte)'i' && span[i + 4] == (byte)'l' && span[i + 5] == (byte)'e'
                && span[i + 6] == (byte)'r')
            {
                last = i;
            }
        }

        if (last < 0)
        {
            return false;
        }

        try
        {
            PdfTokenizer tokenizer = new(data);
            tokenizer.SetPosition(last);
            if (!tokenizer.TryReadToken(out PdfToken keyword) || keyword.Kind != PdfTokenKind.Trailer)
            {
                return false;
            }

            PdfParser parser = new(limits, governor, diagnostics, null);
            if (parser.ParseValue(tokenizer, 0) is not PdfDictionary trailer)
            {
                return false;
            }

            if (!trailer.TryGet(PdfName.FromAscii("Size"), out QpdfObject sizeRaw) || sizeRaw is not PdfInteger sizeInteger
                || sizeInteger.Value < 1L || sizeInteger.Value > int.MaxValue)
            {
                return false;
            }

            if (!trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject rootRaw) || rootRaw is PdfNull)
            {
                return false;
            }

            size = (int)sizeInteger.Value;
            root = rootRaw;
            tailDictionary = trailer;
            return true;
        }
        catch (QpdfException)
        {
            return false;
        }
    }

    private static int SkipWhitespace(ReadOnlySpan<byte> span, int position)
    {
        while (position < span.Length)
        {
            byte current = span[position];
            if (current != (byte)' ' && current != (byte)'\t' && current != (byte)'\n'
                && current != (byte)'\r' && current != (byte)'\f' && current != 0)
            {
                break;
            }

            position += 1;
        }

        return position;
    }

    private static bool IsDigit(byte value)
    {
        return value >= (byte)'0' && value <= (byte)'9';
    }
}