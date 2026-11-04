using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

/// <summary>Owns decoded chunks and list storage; immutable raw payloads stay borrowed until joining.</summary>
internal sealed class AnalysisChunks : IDisposable
{
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private List<ReadOnlyMemory<byte>>? _parts;
    private CancellationToken _token;
    private bool _ownsSinglePart;

    private AnalysisChunks(ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    { _memory = memory; _governor = governor; _token = token; _parts = ParserContainers.CreateList<ReadOnlyMemory<byte>>(memory); }

    internal static AnalysisChunks Create(ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(56L); return new(memory, governor, token); }
        catch { memory.Dispose(); throw; }
    }
    // Borrowed only while this owner is alive; joining uses indexed reads.
    internal IReadOnlyList<ReadOnlyMemory<byte>> Parts => _parts ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
    internal bool TryAdd(PdfStream stream, IIndirectResolver resolver, QpdfLimits limits)
    {
        ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        if (!TryDecode(stream, resolver, limits, governor, memory, _token, out ReadOnlyMemory<byte> bytes, out bool decoded))
        { return false; }
        Append(bytes, decoded); return true;
    }

    // The decoder's temporary scope owns a complete decoded array, or nothing
    // when it borrowed immutable raw memory. Transfer only that payload;
    // the decoder can retire any other scratch when its scope closes.
    internal void Add(ReadOnlyMemory<byte> bytes, ParserMemoryScope source)
    {
        ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        source.CheckOwner(governor); _token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        bool owned = bytes.Length != 0 && source.ReservedBytes != 0L;
        long allocation = owned ? checked((long)bytes.Length + GovernedBufferStream.ArrayOverheadBytes) : 0L;
        if (owned) { source.TransferTo(memory, allocation); }
        try
        {
            Append(bytes, false);
            _ownsSinglePart = Parts.Count == 1 && owned;
        }
        catch { if (owned) { memory.TransferTo(source, allocation); } throw; }
    }

    // Joining consumes this owner only on success. Move a sole owned payload;
    // borrowed memory still needs an independent copy before its source closes.
    internal JoinedContent Join()
    {
        ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        List<ReadOnlyMemory<byte>> parts = _parts ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        _token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        JoinedContent joined = _ownsSinglePart
            ? JoinedContent.TakeOwned(parts[0], memory, governor, _token)
            : JoinedContent.Create(parts, governor, _token);
        try { Dispose(); return joined; }
        catch { joined.Dispose(); throw; }
    }

    internal static bool TryDecode(PdfStream stream, IIndirectResolver resolver, QpdfLimits limits,
        ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token,
        out ReadOnlyMemory<byte> bytes, out bool decoded)
    {
        memory.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        bytes = ReadOnlyMemory<byte>.Empty; decoded = false;
        if (FilterChain.HasExternalData(stream, resolver, governor, token)) { return false; }
        FilterPlan stages;
        try { stages = FilterChain.ResolveScoped(stream.Dictionary, resolver, governor, token); }
        catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException)) { return false; }
        using FilterPlan stageOwner = stages;
        if (!FilterChain.AllStagesInLevel(stages, "specialized")) { return false; }
        // Only unfiltered or successfully decoded bytes may be interpreted as content.
        if (stages.Count == 0) { bytes = stream.RawMemory; return true; }
        try { bytes = FilterChain.Decode(stream.ArrayData(memory, governor, token), stages, limits, governor, memory, token); }
        catch (QpdfException exception) when (exception is not (QpdfLimitException or QpdfMemoryAdmissionException)) { return false; }
        decoded = true; return true;
    }

    private void Append(ReadOnlyMemory<byte> decoded, bool owned)
    {
        ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        List<ReadOnlyMemory<byte>> parts = _parts ?? throw new ObjectDisposedException(nameof(AnalysisChunks));
        try
        {
            _token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (parts.Count == parts.Capacity)
            {
                governor.NoteOperation();
                _token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            }
            ParserContainers.AddItem(memory, parts, decoded, 16L);
            _ownsSinglePart = parts.Count == 1 && owned && decoded.Length != 0;
        }
        catch
        {
            if (owned && decoded.Length != 0)
            {
                long bytes = decoded.Length + GovernedBufferStream.ArrayOverheadBytes;
                decoded = ReadOnlyMemory<byte>.Empty; memory.Release(bytes);
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
