using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Filters;

/// <summary>A resolved filter stage: canonical name, its parms, and the resolver for indirect specs.</summary>
internal readonly record struct FilterStage(string Name, PdfDictionary? Parms, IIndirectResolver? Resolver);

/// <summary>
/// Resolves `/Filter` and `/DecodeParms` into an ordered stage list and
/// decodes governed Flate, LZW, ASCIIHex, ASCII85, RunLength, and DCT chains.
/// Abbreviated names (`Fl`, `LZW`, `A85`,
/// `AHx`, `RL`, `CCF`, `DCT`) resolve canonically; anything else fails explicitly as unsupported.
/// </summary>
internal static class FilterChain
{
    internal static FilterPlan Resolve(PdfDictionary streamDict, IIndirectResolver? resolver,
        ResourceGovernor governor, CancellationToken token) => ResolveCore(streamDict, resolver, governor, token, null);

    internal static FilterPlan ResolveObserved(PdfDictionary streamDict, IIndirectResolver? resolver,
        ResourceGovernor governor, CancellationToken token, Action observe)
    {
        ArgumentNullException.ThrowIfNull(observe);
        return ResolveCore(streamDict, resolver, governor, token, observe);
    }

    private static FilterPlan ResolveCore(PdfDictionary streamDict, IIndirectResolver? resolver,
        ResourceGovernor governor, CancellationToken token, Action? observe)
    {
        ArgumentNullException.ThrowIfNull(streamDict);
        FilterPlan plan = FilterPlan.Create(governor, token);
        try
        {
            ReadFilters(streamDict, resolver, plan, token, observe);
            ReadParms(streamDict, resolver, plan, token, observe);
            for (int index = 0; index < plan.Count; index++)
            {
                string name = plan[index].Name;
                if (!IsBorrowedName(name)) { plan.RetainName(name, GovernedText.StringBytes(name.Length)); }
            }
            plan.Visit();
            return plan;
        }
        catch { plan.Dispose(); throw; }
    }

    internal static bool AllStagesInLevel(IReadOnlyList<FilterStage> stages, string decode)
    {
        if (stages is null)
        {
            throw new ArgumentNullException(nameof(stages));
        }

        for (int index = 0; index < stages.Count; index++)
        {
            if (!NameInLevel(stages[index].Name, decode))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool NameInLevel(string name, string decode)
    {
        bool generalized = name == FlateFilter.FilterName || name == LzwFilter.FilterName
            || name == AsciiHexFilter.FilterName || name == Ascii85Filter.FilterName;
        return decode switch
        {
            "generalized" => generalized,
            "specialized" => generalized || name == RunLengthFilter.FilterName,
            "all" => generalized || name == RunLengthFilter.FilterName || name == DctFilter.FilterName,
            _ => false,
        };
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
        if (stages is FilterPlan plan && !ReferenceEquals(plan.Governor, governor))
        { throw new InvalidOperationException("Filter stages belong to a different governor."); }
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

    private static bool Lookup(PdfDictionary dictionary, ReadOnlySpan<byte> key,
        FilterPlan plan, CancellationToken token, Action? observe, out QpdfObject value)
        => observe is null ? PdfDictionaryLookup.TryGet(dictionary, key, plan.Governor, token, out value)
            : PdfDictionaryLookup.TryGetObserved(dictionary, key, plan.Governor, token, observe, out value);

    private static QpdfObject ResolveValue(QpdfObject raw, IIndirectResolver? resolver,
        string role, FilterPlan plan, CancellationToken token)
    {
        plan.Visit(); token.ThrowIfCancellationRequested();
        QpdfObject result = Deref(raw, resolver, role);
        token.ThrowIfCancellationRequested(); plan.Visit();
        return result;
    }

    private static void ReadFilters(PdfDictionary dictionary, IIndirectResolver? resolver,
        FilterPlan plan, CancellationToken token, Action? observe)
    {
        if (!Lookup(dictionary, "Filter"u8, plan, token, observe, out QpdfObject raw) || raw is PdfNull) { return; }
        raw = ResolveValue(raw, resolver, "/Filter", plan, token);
        if (raw is PdfNull) { return; }
        if (raw is PdfName single)
        {
            plan.Allocate(1);
            plan.Set(0, new FilterStage(Expand(single, plan, token, observe), null, resolver));
            return;
        }
        if (raw is not PdfArray array)
        { throw new QpdfParseException("Stream /Filter must be a name or an array of names.", null, null); }
        plan.Allocate(array.Count);
        for (int index = 0; index < array.Count; index++)
        {
            QpdfObject entry = ResolveValue(array[index], resolver, "/Filter", plan, token);
            if (entry is not PdfName name)
            { throw new QpdfParseException("Stream /Filter array entries must be names.", null, null); }
            plan.Set(index, new FilterStage(Expand(name, plan, token, observe), null, resolver));
            if ((index + 1) % 256 == 0) { observe?.Invoke(); }
        }
    }

    private static void ReadParms(PdfDictionary dictionary, IIndirectResolver? resolver,
        FilterPlan plan, CancellationToken token, Action? observe)
    {
        if (!Lookup(dictionary, "DecodeParms"u8, plan, token, observe, out QpdfObject raw) || raw is PdfNull) { return; }
        raw = ResolveValue(raw, resolver, "/DecodeParms", plan, token);
        if (raw is PdfNull) { return; }
        if (raw is PdfDictionary single)
        {
            for (int index = 0; index < plan.Count; index++)
            { plan.Set(index, plan[index] with { Parms = single }); }
            return;
        }
        if (raw is not PdfArray array)
        { throw new QpdfParseException("Stream /DecodeParms must be a dictionary, an array, or null.", null, null); }
        if (array.Count != plan.Count)
        { throw new QpdfParseException($"Stream /DecodeParms holds {array.Count} entries for {plan.Count} filters.", null, null); }
        for (int index = 0; index < array.Count; index++)
        {
            QpdfObject entry = ResolveValue(array[index], resolver, "/DecodeParms", plan, token);
            PdfDictionary? parms = entry switch
            {
                PdfNull => null,
                PdfDictionary value => value,
                _ => throw new QpdfParseException("Stream /DecodeParms entries must be dictionaries or null.", null, null),
            };
            plan.Set(index, plan[index] with { Parms = parms });
            if ((index + 1) % 256 == 0) { observe?.Invoke(); }
        }
    }

    private static string? CanonicalName(PdfName name, ResourceGovernor governor, CancellationToken token)
    {
        ReadOnlySpan<byte> raw = name.RawSpan;
        switch (raw.Length)
        {
            case 2:
                if (PdfDictionaryLookup.NameEquals(name, "Fl"u8, governor, token)) { return FlateFilter.FilterName; }
                if (PdfDictionaryLookup.NameEquals(name, "RL"u8, governor, token)) { return RunLengthFilter.FilterName; }
                break;
            case 3:
                if (PdfDictionaryLookup.NameEquals(name, "LZW"u8, governor, token)) { return LzwFilter.FilterName; }
                if (PdfDictionaryLookup.NameEquals(name, "A85"u8, governor, token)) { return Ascii85Filter.FilterName; }
                if (PdfDictionaryLookup.NameEquals(name, "AHx"u8, governor, token)) { return AsciiHexFilter.FilterName; }
                if (PdfDictionaryLookup.NameEquals(name, "CCF"u8, governor, token)) { return "CCITTFaxDecode"; }
                if (PdfDictionaryLookup.NameEquals(name, "DCT"u8, governor, token)) { return DctFilter.FilterName; }
                break;
            case 9:
                if (PdfDictionaryLookup.NameEquals(name, "LZWDecode"u8, governor, token)) { return LzwFilter.FilterName; }
                if (PdfDictionaryLookup.NameEquals(name, "DCTDecode"u8, governor, token)) { return DctFilter.FilterName; }
                if (PdfDictionaryLookup.NameEquals(name, "JPXDecode"u8, governor, token)) { return "JPXDecode"; }
                break;
            case 11:
                if (PdfDictionaryLookup.NameEquals(name, "FlateDecode"u8, governor, token)) { return FlateFilter.FilterName; }
                if (PdfDictionaryLookup.NameEquals(name, "JBIG2Decode"u8, governor, token)) { return "JBIG2Decode"; }
                break;
            case 13:
                if (PdfDictionaryLookup.NameEquals(name, "ASCII85Decode"u8, governor, token)) { return Ascii85Filter.FilterName; }
                break;
            case 14:
                if (PdfDictionaryLookup.NameEquals(name, "ASCIIHexDecode"u8, governor, token)) { return AsciiHexFilter.FilterName; }
                if (PdfDictionaryLookup.NameEquals(name, "CCITTFaxDecode"u8, governor, token)) { return "CCITTFaxDecode"; }
                break;
            case 15:
                if (PdfDictionaryLookup.NameEquals(name, "RunLengthDecode"u8, governor, token)) { return RunLengthFilter.FilterName; }
                break;
        }
        return null;
    }

    private static bool IsBorrowedName(string name) => name.Length == 0
        || ReferenceEquals(name, FlateFilter.FilterName) || ReferenceEquals(name, LzwFilter.FilterName)
        || ReferenceEquals(name, AsciiHexFilter.FilterName) || ReferenceEquals(name, Ascii85Filter.FilterName)
        || ReferenceEquals(name, RunLengthFilter.FilterName) || ReferenceEquals(name, DctFilter.FilterName)
        || ReferenceEquals(name, "CCITTFaxDecode") || ReferenceEquals(name, "JPXDecode") || ReferenceEquals(name, "JBIG2Decode");

    private static string Expand(PdfName name, FilterPlan plan, CancellationToken token, Action? observe)
    {
        string? canonical = CanonicalName(name, plan.Governor, token);
        if (canonical is not null) { return canonical; }
        int length = 0;
        Span<char> scratch = stackalloc char[3];
        for (int index = 0; index < name.RawSpan.Length; index++)
        {
            plan.Visit();
            length = checked(length + PdfNameDisplay.WriteByte(name.RawSpan[index], scratch));
            if (length > Array.MaxLength / sizeof(char)) { throw new QpdfException("Filter name exceeds supported capacity."); }
            if ((index + 1) % 256 == 0) { observe?.Invoke(); }
        }
        plan.Visit();
        long bytes = GovernedText.StringBytes(length);
        plan.ReserveName(bytes);
        try
        {
            return string.Create(length, (name, plan, observe), static (target, state) =>
            {
                int offset = 0;
                for (int index = 0; index < state.name.RawSpan.Length; index++)
                {
                    state.plan.Visit();
                    offset += PdfNameDisplay.WriteByte(state.name.RawSpan[index], target.Slice(offset));
                    if ((index + 1) % 256 == 0) { state.observe?.Invoke(); }
                }
                state.plan.Visit();
            });
        }
        catch { plan.ReleaseName(bytes); throw; }
    }
}
