using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

/// <summary>Owns decoded chunks and list storage; raw stream payloads remain borrowed.</summary>
internal sealed class AnalysisChunks : IDisposable
{
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private List<byte[]>? _parts;
    private CancellationToken _token;

    private AnalysisChunks(ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    { _memory = memory; _governor = governor; _token = token; _parts = ParserContainers.CreateList<byte[]>(memory); }

    internal static AnalysisChunks Create(ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(48L); return new(memory, governor, token); }
        catch { memory.Dispose(); throw; }
    }
    // Borrowed only while this owner is alive; joining uses indexed reads.
    internal IReadOnlyList<byte[]> Parts => _parts ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
    internal bool TryAdd(PdfStream stream, IIndirectResolver resolver, QpdfLimits limits)
    {
        ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        _token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        FilterPlan stages;
        try { stages = FilterChain.Resolve(stream.Dictionary, resolver, governor, _token); }
        catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException)) { return false; }
        using FilterPlan stageOwner = stages;
        if (!FilterChain.AllStagesInLevel(stages, "specialized")) { return false; }
        // PdfStream's immutable storage is already funded by its graph owner.
        byte[] raw = stream.ArrayData(memory, governor, _token);
        byte[] decoded;
        try { decoded = stages.Count == 0 ? raw : FilterChain.Decode(raw, stages, limits, governor, memory, _token); }
        catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException)) { return false; }
        Append(decoded, stages.Count != 0); return true;
    }

    internal void AddForContent(PdfStream stream, PoolResolver resolver, QpdfLimits limits)
    {
        ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        (byte[] bytes, bool decoded) = DecodeForContent(stream, resolver, limits, governor, memory, _token);
        Append(bytes, decoded);
    }

    internal bool TryAddForInlineImages(PdfStream stream, PoolResolver resolver, QpdfLimits limits)
    {
        ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        if (!TryDecodeForContent(stream, resolver, limits, governor, memory, _token, true, out byte[] bytes, out bool decoded))
        { return false; }
        Append(bytes, decoded); return true;
    }

    internal static (byte[] Data, bool Decoded) DecodeForContent(PdfStream stream, IIndirectResolver resolver,
        QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        TryDecodeForContent(stream, resolver, limits, governor, memory, token, false, out byte[] bytes, out bool decoded);
        return (bytes, decoded);
    }

    private static bool TryDecodeForContent(PdfStream stream, IIndirectResolver resolver,
        QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token,
        bool rejectDecodeFailure, out byte[] bytes, out bool decoded)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        byte[] raw = stream.ArrayData(memory, governor, token);
        bytes = raw; decoded = false;
        FilterPlan stages;
        try
        {
            stages = rejectDecodeFailure
                ? FilterChain.ResolveScoped(stream.Dictionary, resolver, governor, token)
                : FilterChain.Resolve(stream.Dictionary, resolver, governor, token);
        }
        catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException)) { return true; }
        using FilterPlan stageOwner = stages;
        if (stages.Count == 0 || !FilterChain.AllStagesInLevel(stages, "specialized")) { return true; }
        try { bytes = FilterChain.Decode(raw, stages, limits, governor, memory, token); decoded = true; return true; }
        catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException) && rejectDecodeFailure) { return false; }
        catch (QpdfFilterException exception) when (exception.Reason == QpdfFilterFailureReason.UnsupportedFilter) { return true; }
    }

    private void Append(byte[] decoded, bool owned)
    {
        ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        List<byte[]> parts = _parts ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        try
        {
            _token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (parts.Count == parts.Capacity)
            {
                long capacity = Math.Min(Array.MaxLength, Math.Max(4L, parts.Capacity * 2L));
                governor.NoteOperation();
                _token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            }
            ParserContainers.AddItem(memory, parts, decoded, IntPtr.Size);
        }
        catch
        {
            if (owned && decoded.Length != 0)
            {
                long bytes = decoded.Length + GovernedBufferStream.ArrayOverheadBytes;
                decoded = Array.Empty<byte>(); memory.Release(bytes);
            }
            throw;
        }
    }
    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _parts = null; _memory = null; _governor = null; _token = default;
        memory?.Dispose();
    }
}
