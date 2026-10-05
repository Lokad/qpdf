using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

/// <summary>
/// Bounded cross-reference repair, separate from strict resolution so heuristics
/// can never weaken the strict contract. Scans for indirect object headers,
/// trial-parses each candidate with per-object error tolerance, keeps the last valid
/// occurrence, bounds scan work through the host governor, and fills
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
        using ParserMemoryScope results = ParserMemoryScope.Create(governor);
        long maxId = data.Length / 3L;
        Dictionary<int, XrefEntry> entries = new();
        QpdfLimits damaged = limits with { MaxContainerSize = Math.Min(limits.MaxContainerSize, limits.MaxContainerSizeDamaged) };
        int? lastCatalog = ScanObjects(data, damaged, governor, diagnostics, maxId, entries, results);
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
        using ParserMemoryScope tailMemory = ParserMemoryScope.Create(governor);
        IReadOnlyDictionary<PdfName, ParsedValue>? tailMembers;
        if (TryReadTailTrailer(data, damaged, governor, diagnostics, tailMemory,
            out int tailSize, out QpdfObject? tailRoot, out tailDictionary, out tailMembers) && tailSize >= 1)
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

            results.Reserve(64L);
            root = new PdfReference(lastCatalog.Value, 0);
        }

        Dictionary<int, XrefEntry> repaired = new();
        foreach (KeyValuePair<int, XrefEntry> entry in entries)
        {
            governor.ChargeWork(1L);
            if (entry.Key >= 1 && entry.Key < size)
            {
                repaired[entry.Key] = entry.Value;
            }
            else { results.Release(32L); }
        }

        for (int number = 1; number < size; number++)
        {
            governor.ChargeWork(1L);
            if (!repaired.ContainsKey(number))
            {
                results.Reserve(32L);
                repaired[number] = XrefEntry.Free(0L, 0);
            }
        }

        Dictionary<PdfName, ParsedValue> trailerEntries = ParserContainers.CreateDictionary(results);
        results.Reserve(84L); // At most ten decimal digits and the scalar/string overhead.
        string sizeText = size.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PdfInteger sizeValue = new(sizeText, size);
        results.Release(20L - sizeText.Length * 2L);
        AddTrailerEntry(trailerEntries, results, "Size", sizeValue);
        AddTrailerEntry(trailerEntries, results, "Root", root);
        if (tailDictionary is not null)
        {
            PdfName encryptKey = PdfName.FromAscii("Encrypt");
            if (tailDictionary.TryGet(encryptKey, out QpdfObject? encryptValue) && encryptValue is not null && encryptValue is not PdfNull)
            {
                AddTrailerEntry(trailerEntries, results, "Encrypt", encryptValue);
            }
            PdfName infoKey = PdfName.FromAscii("Info");
            if (tailDictionary.TryGet(infoKey, out QpdfObject? infoValue) && infoValue is not null && infoValue is not PdfNull)
            {
                AddTrailerEntry(trailerEntries, results, "Info", infoValue);
            }
            PdfName idKey = PdfName.FromAscii("ID");
            if (tailDictionary.TryGet(idKey, out QpdfObject? idValue) && idValue is not null && idValue is not PdfNull)
            {
                AddTrailerEntry(trailerEntries, results, "ID", idValue);
            }
        }
        long retainedTailBytes = 0L;
        if (tailMembers is not null)
        {
            foreach (KeyValuePair<PdfName, ParsedValue> entry in trailerEntries)
            {
                governor.ChargeWork(1L);
                if (tailMembers.TryGetValue(entry.Key, out ParsedValue member) && ReferenceEquals(member.Value, entry.Value.Value))
                {
                    retainedTailBytes = checked(retainedTailBytes + member.ReservedBytes);
                }
            }
        }
        // Only selected values survive; all parsed keys, container storage,
        // receipt metadata and unselected values cease to belong to the result.
        tailMemory.Release(checked(tailMemory.ReservedBytes - retainedTailBytes));
        tailMemory.TransferTo(results);
        PdfDictionary trailer = ParserContainers.CopyDictionary(results, governor, trailerEntries, false);
        diagnostics.Add(
            QpdfDiagnosticCode.RecoveryEngaged,
            QpdfDiagnosticSeverity.Warning,
            $"Bounded repair rebuilt {repaired.Count} cross-reference entries from {rootSource} after strict parsing failed: {strictReason}");
        results.Reserve(144L); // Table, one-element trailer list and its backing array.
        XRefTable table = new(-1L, repaired, trailer, new List<PdfDictionary> { trailer });
        results.Retain();
        return table;
    }

    private static void AddTrailerEntry(
        Dictionary<PdfName, ParsedValue> entries, ParserMemoryScope memory, string key, QpdfObject value)
    {
        long scratch = 32L + key.Length;
        memory.Reserve(checked(64L + key.Length + scratch));
        PdfName name = PdfName.FromAscii(key);
        memory.Release(scratch);
        ParserContainers.EnsureEntryCapacity(memory, entries);
        entries.Add(name, new ParsedValue(value, 0L));
    }

    private static int? ScanObjects(
        ReadOnlyMemory<byte> data,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        long maxId,
        Dictionary<int, XrefEntry> entries,
        ParserMemoryScope results)
    {
        int? lastCatalog = null;
        ReadOnlySpan<byte> span = data.Span;
        PdfParser parser = new(limits, governor, diagnostics, null);
        parser.TolerateObjectErrors = true;
        parser.SanityChecks = true;
        parser.SuppressRepairDiagnostics = true;
        long chargedThrough = 0L;
        int position = 0;
        while (position < span.Length)
        {
            if (position - chargedThrough >= ScanWindowBytes)
            {
                governor.ChargeWork(1L);
                governor.CheckCancellation();
                chargedThrough = position;
            }

            // A candidate must start at the beginning of its number. Otherwise
            // object 51 also creates a false object 1 at its second digit.
            if (!IsDigit(span[position]) || (position > 0 && IsDigit(span[position - 1])))
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
            position = SkipWhitespace(span, position, governor);
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
            position = SkipWhitespace(span, position, governor);
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
            using ParserMemoryScope trial = ParserMemoryScope.Create(governor);
            QpdfObject? value = TryParseObject(data, parser, numberStart, (int)number, (int)generation, trial);

            if (value is not null)
            {
                if (!entries.ContainsKey((int)number)) { results.Reserve(32L); }
                entries[(int)number] = XrefEntry.Uncompressed(numberStart, (int)generation);
                if (value is PdfDictionary dictionary
                    && dictionary.TryGet(PdfName.FromAscii("Type"), out QpdfObject raw)
                    && raw is PdfName name && name.RawSpan.SequenceEqual("Catalog"u8))
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
        PdfParser parser,
        int offset,
        int number,
        int generation,
        ParserMemoryScope trial)
    {
        try
        {
            PdfTokenizer tokenizer = new(data);
            tokenizer.SetPosition(offset);
            (int foundNumber, int foundGeneration, QpdfObject value) = parser.ParseIndirectObject(tokenizer, 0, trial);
            if (foundNumber == number && foundGeneration == generation)
            {
                return value;
            }

            return null;
        }
        catch (QpdfParseException)
        {
            return null;
        }
    }

    private static bool TryReadTailTrailer(
        ReadOnlyMemory<byte> data,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        ParserMemoryScope memory,
        out int size,
        out QpdfObject? root,
        out PdfDictionary? tailDictionary,
        out IReadOnlyDictionary<PdfName, ParsedValue>? members)
    {
        size = 0;
        root = null;
        tailDictionary = null;
        members = null;
        ReadOnlySpan<byte> span = data.Span;
        int scanStart = span.Length > TailScanBytes ? span.Length - TailScanBytes : 0;
        int last = -1;
        for (int i = scanStart; i + 7 <= span.Length; i++)
        {
            if ((i - scanStart) % ScanWindowBytes == 0)
            {
                governor.ChargeWork(1L);
                governor.CheckCancellation();
            }

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
            ParsedDictionaryValue parsed = parser.ParseDictionaryValue(tokenizer, 0, memory);
            if (parsed.Value is not PdfDictionary trailer)
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
            members = parsed.Members;
            return true;
        }
        catch (QpdfParseException)
        {
            return false;
        }
    }

    private static int SkipWhitespace(ReadOnlySpan<byte> span, int position, ResourceGovernor governor)
    {
        int checkedThrough = position;
        while (position < span.Length)
        {
            if (position - checkedThrough >= ScanWindowBytes)
            {
                governor.ChargeWork(1L);
                governor.CheckCancellation();
                checkedThrough = position;
            }

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
