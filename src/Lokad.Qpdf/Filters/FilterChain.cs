using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Filters;

/// <summary>A resolved filter stage: canonical name, its parms, and the resolver for indirect specs.</summary>
internal sealed record FilterStage(string Name, PdfDictionary? Parms, IIndirectResolver? Resolver);

/// <summary>
/// Resolves `/Filter` and `/DecodeParms` into an ordered stage list and
/// decodes governed Flate, LZW, ASCIIHex, ASCII85, RunLength, and DCT chains.
/// Abbreviated names (`Fl`, `LZW`, `A85`,
/// `AHx`, `RL`, `CCF`, `DCT`) resolve canonically; anything else fails explicitly as unsupported.
/// </summary>
internal static class FilterChain
{
    internal static IReadOnlyList<FilterStage> Resolve(PdfDictionary streamDict)
    {
        return Resolve(streamDict, null);
    }

    internal static IReadOnlyList<FilterStage> Resolve(PdfDictionary streamDict, IIndirectResolver? resolver)
    {
        if (streamDict is null)
        {
            throw new ArgumentNullException(nameof(streamDict));
        }

        List<string> filters = ReadFilters(streamDict, resolver);
        List<PdfDictionary?> parms = ReadParms(streamDict, filters.Count, resolver);
        List<FilterStage> stages = new(filters.Count);
        for (int i = 0; i < filters.Count; i++)
        {
            stages.Add(new FilterStage(filters[i], parms[i], resolver));
        }

        return stages;
    }

    internal static bool AllStagesInLevel(IReadOnlyList<FilterStage> stages, string decode)
    {
        if (stages is null)
        {
            throw new ArgumentNullException(nameof(stages));
        }

        foreach (FilterStage stage in stages)
        {
            bool allowed;
            if (decode == "generalized")
            {
                allowed = stage.Name == FlateFilter.FilterName || stage.Name == LzwFilter.FilterName
                    || stage.Name == AsciiHexFilter.FilterName || stage.Name == Ascii85Filter.FilterName;
            }
            else if (decode == "specialized")
            {
                allowed = stage.Name == FlateFilter.FilterName || stage.Name == LzwFilter.FilterName
                    || stage.Name == AsciiHexFilter.FilterName || stage.Name == Ascii85Filter.FilterName
                    || stage.Name == RunLengthFilter.FilterName;
            }
            else if (decode == "all")
            {
                allowed = stage.Name == FlateFilter.FilterName || stage.Name == LzwFilter.FilterName
                    || stage.Name == AsciiHexFilter.FilterName || stage.Name == Ascii85Filter.FilterName
                    || stage.Name == RunLengthFilter.FilterName || stage.Name == DctFilter.FilterName;
            }
            else
            {
                allowed = false;
            }

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    internal static byte[] Decode(
        byte[] input,
        IReadOnlyList<FilterStage> stages,
        QpdfLimits limits,
        ResourceGovernor governor)
    {
        return Decode(input, stages, limits, governor, null);
    }

    internal static byte[] Decode(
        byte[] input,
        IReadOnlyList<FilterStage> stages,
        QpdfLimits limits,
        ResourceGovernor governor,
        ParserMemoryScope? memory)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (stages is null)
        {
            throw new ArgumentNullException(nameof(stages));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        limits.Validate();
        memory?.CheckOwner(governor);
        if (stages.Count > limits.MaxStreamFilters)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.TooManyFilters, null, $"{stages.Count} filters exceed the maximum of {limits.MaxStreamFilters}");
        }
        if (stages.Count == 0)
        {
            if (memory is null) { governor.ReserveMemory(input.Length); }
            else { memory.Reserve(input.Length); }
            byte[] passthrough = new byte[input.Length];
            Buffer.BlockCopy(input, 0, passthrough, 0, input.Length);
            return passthrough;
        }

        byte[] current = input;
        for (int i = 0; i < stages.Count; i++)
        {
            current = DecodeStage(current, stages[i], limits, governor);
            // Flate's returned array has an explicit retained reservation.
            // Other codecs still need their own allocation/ownership audit.
            if (stages[i].Name == FlateFilter.FilterName && current.Length != 0)
            {
                memory?.AdoptReserved(checked((long)current.Length + GovernedBufferStream.ArrayOverheadBytes));
            }
        }

        return current;
    }

    private static byte[] DecodeStage(byte[] input, FilterStage stage, QpdfLimits limits, ResourceGovernor governor)
    {
        if (stage.Name == FlateFilter.FilterName || stage.Name == LzwFilter.FilterName)
        { return DecodePredictorStage(input, stage, limits, governor); }

        if (stage.Name == DctFilter.FilterName)
        {
            // DctFilter owns decoded-byte accounting for its frame; DecodeChecked would charge twice.
            return DctFilter.Decode(input, limits, governor);
        }

        if (stage.Name == AsciiHexFilter.FilterName || stage.Name == Ascii85Filter.FilterName || stage.Name == RunLengthFilter.FilterName)
        {
            RequireNoParms(stage);
            byte[] decoded = stage.Name == AsciiHexFilter.FilterName
                ? AsciiHexFilter.Decode(input, limits, governor)
                : stage.Name == Ascii85Filter.FilterName
                    ? Ascii85Filter.Decode(input, limits, governor)
                    : RunLengthFilter.Decode(input, limits, governor);
            return DecodeChecked(decoded, limits, governor);
        }

        throw new QpdfFilterException(QpdfFilterFailureReason.UnsupportedFilter, stage.Name, "Filter is not implemented in this milestone.");
    }

    private static byte[] DecodePredictorStage(byte[] input, FilterStage stage, QpdfLimits limits, ResourceGovernor governor)
    {
        bool earlyChange = stage.Name != LzwFilter.FilterName || ReadEarlyChange(stage.Parms, stage.Resolver, governor);
        using ParserMemoryScope? memory = stage.Parms is null ? null : ParserMemoryScope.Create(governor);
        PredictorParameters? parms = null;
        try
        {
            if (memory is not null)
            { parms = PredictorParameters.FromDictionary(stage.Parms, stage.Name, stage.Resolver, memory, governor, governor.CancellationToken); }
            // Direct hint/companion decoding shares the same codec implementation.
            if (stage.Name == FlateFilter.FilterName) { return FlateFilter.Decode(input, parms, limits, governor); }
            byte[] inflated = LzwFilter.Decode(input, earlyChange, limits, governor);
            if (parms is not null && parms.Predictor != 1)
            { return DecodeChecked(PredictorStages.Apply(inflated, parms, LzwFilter.FilterName, governor), limits, governor); }
            return DecodeChecked(inflated, limits, governor);
        }
        finally { parms = null; }
    }

    private static byte[] DecodeChecked(byte[] decoded, QpdfLimits limits, ResourceGovernor governor)
    {
        if (decoded.Length > limits.MaxDecodedBytes)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), decoded.Length, limits.MaxDecodedBytes);
        }

        governor.ChargeDecodedBytes(decoded.Length);
        return decoded;
    }

    private static bool ReadEarlyChange(PdfDictionary? parms, IIndirectResolver? resolver, ResourceGovernor governor)
    {
        if (parms is null)
        {
            return true;
        }

        if (!PdfDictionaryLookup.TryGet(parms, "EarlyChange"u8, governor, governor.CancellationToken, out QpdfObject raw) || raw is PdfNull)
        {
            return true;
        }

        governor.CheckCancellation(); governor.ChargeWork(1L);
        raw = Deref(raw, resolver, "/EarlyChange");
        governor.CheckCancellation(); governor.ChargeWork(1L);
        if (raw is PdfNull)
        {
            return true;
        }

        if (raw is PdfInteger integer && (integer.Value == 0L || integer.Value == 1L))
        {
            return integer.Value == 1L;
        }

        throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, LzwFilter.FilterName, "DecodeParms /EarlyChange must be 0 or 1.");
    }

    private static void RequireNoParms(FilterStage stage)
    {
        if (stage.Parms is not null)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, stage.Name, "DecodeParms are not supported for this filter.");
        }
    }

    private static QpdfObject Deref(QpdfObject raw, IIndirectResolver? resolver, string role)
    {
        if (raw is not PdfReference reference)
        {
            return raw;
        }

        if (resolver is null)
        {
            throw new QpdfParseException($"Indirect stream {role} needs an object resolver.", null, null);
        }

        if (!resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is null)
        {
            throw new QpdfParseException($"Unresolvable indirect stream {role}.", null, reference.ObjectNumber);
        }

        return resolved;
    }

    private static List<string> ReadFilters(PdfDictionary streamDict, IIndirectResolver? resolver)
    {
        PdfName filterKey = PdfName.FromAscii("Filter");
        if (!streamDict.TryGet(filterKey, out QpdfObject raw) || raw is PdfNull)
        {
            return new List<string>();
        }

        raw = Deref(raw, resolver, "/Filter");
        if (raw is PdfNull)
        {
            return new List<string>();
        }

        List<string> filters = new();
        if (raw is PdfName single)
        {
            filters.Add(Expand(single));
            return filters;
        }

        if (raw is PdfArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                QpdfObject entry = Deref(array[i], resolver, "/Filter");
                if (entry is not PdfName name)
                {
                    throw new QpdfParseException("Stream /Filter array entries must be names.", null, null);
                }

                filters.Add(Expand(name));
            }

            return filters;
        }

        throw new QpdfParseException("Stream /Filter must be a name or an array of names.", null, null);
    }
    private static List<PdfDictionary?> ReadParms(PdfDictionary streamDict, int filterCount, IIndirectResolver? resolver)
    {
        List<PdfDictionary?> parms = new();
        for (int i = 0; i < filterCount; i++)
        {
            parms.Add(null);
        }

        PdfName parmsKey = PdfName.FromAscii("DecodeParms");
        if (!streamDict.TryGet(parmsKey, out QpdfObject raw) || raw is PdfNull)
        {
            return parms;
        }

        raw = Deref(raw, resolver, "/DecodeParms");
        if (raw is PdfNull)
        {
            return parms;
        }

        if (raw is PdfDictionary single)
        {
            for (int i = 0; i < parms.Count; i++)
            {
                parms[i] = single;
            }

            return parms;
        }

        if (raw is PdfArray array)
        {
            if (array.Count != filterCount)
            {
                throw new QpdfParseException($"Stream /DecodeParms holds {array.Count} entries for {filterCount} filters.", null, null);
            }

            for (int i = 0; i < array.Count; i++)
            {
                QpdfObject entry = Deref(array[i], resolver, "/DecodeParms");
                if (entry is PdfNull)
                {
                    parms[i] = null;
                }
                else if (entry is PdfDictionary dictionary)
                {
                    parms[i] = dictionary;
                }
                else
                {
                    throw new QpdfParseException("Stream /DecodeParms entries must be dictionaries or null.", null, null);
                }
            }

            return parms;
        }

        throw new QpdfParseException("Stream /DecodeParms must be a dictionary, an array, or null.", null, null);
    }

    private static string Expand(PdfName name)
    {
        return name.DisplayText switch
        {
            "Fl" => "FlateDecode",
            "LZW" => "LZWDecode",
            "A85" => "ASCII85Decode",
            "AHx" => "ASCIIHexDecode",
            "RL" => "RunLengthDecode",
            "CCF" => "CCITTFaxDecode",
            "DCT" => "DCTDecode",
            _ => name.DisplayText,
        };
    }
}
