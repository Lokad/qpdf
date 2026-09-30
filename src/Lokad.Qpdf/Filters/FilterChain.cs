using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Filters;

/// <summary>A resolved filter stage: canonical name plus its parms, if any.</summary>
internal sealed record FilterStage(string Name, PdfDictionary? Parms);

/// <summary>
/// Resolves `/Filter` and `/DecodeParms` into an ordered stage list and
/// decodes governed Flate chains. Abbreviated names (`Fl`, `LZW`, `A85`,
/// `AHx`, `RL`, `CCF`, `DCT`) resolve canonically; only FlateDecode stages
/// execute in this milestone, everything else fails explicitly as unsupported.
/// </summary>
internal static class FilterChain
{
    internal static IReadOnlyList<FilterStage> Resolve(PdfDictionary streamDict)
    {
        if (streamDict is null)
        {
            throw new ArgumentNullException(nameof(streamDict));
        }

        List<string> filters = ReadFilters(streamDict);
        List<PdfDictionary?> parms = ReadParms(streamDict, filters.Count);
        List<FilterStage> stages = new(filters.Count);
        for (int i = 0; i < filters.Count; i++)
        {
            stages.Add(new FilterStage(filters[i], parms[i]));
        }

        return stages;
    }

    internal static byte[] Decode(
        byte[] input,
        IReadOnlyList<FilterStage> stages,
        QpdfLimits limits,
        ResourceGovernor governor)
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
        if (stages.Count == 0)
        {
            governor.ReserveMemory(input.Length);
            byte[] passthrough = new byte[input.Length];
            Buffer.BlockCopy(input, 0, passthrough, 0, input.Length);
            return passthrough;
        }

        byte[] current = input;
        for (int i = 0; i < stages.Count; i++)
        {
            if (stages[i].Name != FlateFilter.FilterName)
            {
                throw new QpdfFilterException(QpdfFilterFailureReason.UnsupportedFilter, stages[i].Name, "Filter is not implemented in this milestone.");
            }

            PredictorParameters? parms = PredictorParameters.FromDictionary(stages[i].Parms);
            byte[] decoded = FlateFilter.Decode(current, parms, limits, governor);
            if (decoded.Length > limits.MaxDecodedBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), decoded.Length, limits.MaxDecodedBytes);
            }

            governor.ChargeDecodedBytes(decoded.Length);
            current = decoded;
        }

        return current;
    }

    private static List<string> ReadFilters(PdfDictionary streamDict)
    {
        PdfName filterKey = PdfName.FromAscii("Filter");
        if (!streamDict.TryGet(filterKey, out QpdfObject raw) || raw is PdfNull)
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
                if (array[i] is not PdfName name)
                {
                    throw new QpdfParseException("Stream /Filter array entries must be names.", null, null);
                }

                filters.Add(Expand(name));
            }

            return filters;
        }

        throw new QpdfParseException("Stream /Filter must be a name or an array of names.", null, null);
    }

    private static List<PdfDictionary?> ReadParms(PdfDictionary streamDict, int filterCount)
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
                if (array[i] is PdfNull)
                {
                    parms[i] = null;
                }
                else if (array[i] is PdfDictionary dictionary)
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
