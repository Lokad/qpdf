using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

/// <summary>Owns decoded/materialized chunks and list storage; complete raw arrays stay borrowed.</summary>
internal sealed class AnalysisChunks : IDisposable
{
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private List<byte[]>? _parts;
    private CancellationToken _token;
    private bool _ownsSinglePart;

    private AnalysisChunks(ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    { _memory = memory; _governor = governor; _token = token; _parts = ParserContainers.CreateList<byte[]>(memory); }

    internal static AnalysisChunks Create(ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(56L); return new(memory, governor, token); }
        catch { memory.Dispose(); throw; }
    }
    // Borrowed only while this owner is alive; joining uses indexed reads.
    internal IReadOnlyList<byte[]> Parts => _parts ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
    internal bool TryAdd(PdfStream stream, IIndirectResolver resolver, QpdfLimits limits)
    {
        ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        long before = memory.ReservedBytes; // A raw slice may also materialize under this owner.
        if (!TryDecode(stream, resolver, limits, governor, memory, _token, out byte[] bytes, out bool decoded))
        { return false; }
        Append(bytes, decoded || memory.ReservedBytes > before); return true;
    }

    // Joining consumes this owner only on success. Move a sole owned payload;
    // borrowed arrays still need an independent copy before their source closes.
    internal JoinedContent Join()
    {
        ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        List<byte[]> parts = _parts ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        _token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        JoinedContent joined = _ownsSinglePart
            ? JoinedContent.TakeOwned(parts[0], memory, governor, _token)
            : JoinedContent.Create(parts, governor, _token);
        try { Dispose(); return joined; }
        catch { joined.Dispose(); throw; }
    }

    internal static bool TryDecode(PdfStream stream, IIndirectResolver resolver, QpdfLimits limits,
        ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token,
        out byte[] bytes, out bool decoded)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        bytes = Array.Empty<byte>(); decoded = false;
        FilterPlan stages;
        try { stages = FilterChain.ResolveScoped(stream.Dictionary, resolver, governor, token); }
        catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException)) { return false; }
        using FilterPlan stageOwner = stages;
        if (!FilterChain.AllStagesInLevel(stages, "specialized")) { return false; }
        // Only unfiltered or successfully decoded bytes may be interpreted as content.
        byte[] raw = stream.ArrayData(memory, governor, token);
        try { bytes = stages.Count == 0 ? raw : FilterChain.Decode(raw, stages, limits, governor, memory, token); }
        catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException)) { return false; }
        decoded = stages.Count != 0; return true;
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
                governor.NoteOperation();
                _token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            }
            ParserContainers.AddItem(memory, parts, decoded, IntPtr.Size);
            _ownsSinglePart = parts.Count == 1 && owned && decoded.Length != 0;
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
        _parts = null; _memory = null; _governor = null; _token = default; _ownsSinglePart = false;
        memory?.Dispose();
    }
}
