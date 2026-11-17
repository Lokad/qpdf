using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Filters;

internal static partial class FilterChain
{
    // Parameter support is independent of decode-level selection, frame integrity
    // and host JPEG capability. The same preparation runs before actual decoding.
    internal static bool SupportsParameters(FilterPlan stages, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stages);
        stages.Visit(); token.ThrowIfCancellationRequested();
        for (int index = 0; index < stages.Count; index++)
        {
            try
            {
                using StageParameters parameters = ReadStageParameters(stages[index], stages.Governor, token);
            }
            catch (QpdfException exception) when (exception is QpdfFilterException or QpdfParseException)
            { return false; }
        }
        return true;
    }

    private static bool SupportsDctParameters(FilterStage stage) => stage.Parms is null;

    private static StageParameters ReadStageParameters(FilterStage stage, ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (stage.Name == FlateFilter.FilterName || stage.Name == LzwFilter.FilterName)
        {
            bool earlyChange = stage.Name != LzwFilter.FilterName || ReadEarlyChange(stage.Parms, stage.Resolver, governor, token);
            ParserMemoryScope? memory = stage.Parms is null ? null : ParserMemoryScope.Create(governor);
            try
            {
                PredictorParameters? predictor = memory is null ? null
                    : PredictorParameters.FromDictionary(stage.Parms, stage.Name, stage.Resolver, memory, governor, token);
                if (predictor is not null) { _ = PredictorStages.RowLength(predictor, stage.Name); }
                return new(memory, predictor, earlyChange);
            }
            catch { memory?.Dispose(); throw; }
        }
        if (stage.Name == DctFilter.FilterName)
        {
            if (!SupportsDctParameters(stage))
            { throw new QpdfFilterException(QpdfFilterFailureReason.UnsupportedFilter, stage.Name, "DCTDecode DecodeParms are not supported."); }
        }
        else if (stage.Name == AsciiHexFilter.FilterName || stage.Name == Ascii85Filter.FilterName || stage.Name == RunLengthFilter.FilterName)
        { RequireNoParms(stage); }
        else
        { throw new QpdfFilterException(QpdfFilterFailureReason.UnsupportedFilter, stage.Name, "Filter decoding is unavailable."); }
        return new(null, null, true);
    }

    private struct StageParameters(ParserMemoryScope? memory, PredictorParameters? predictor, bool earlyChange) : IDisposable
    {
        private ParserMemoryScope? _memory = memory;
        internal PredictorParameters? Predictor { get; private set; } = predictor;
        internal bool EarlyChange { get; } = earlyChange;

        public void Dispose()
        {
            ParserMemoryScope? owner = _memory;
            Predictor = null; _memory = null;
            owner?.Dispose();
        }
    }
}
