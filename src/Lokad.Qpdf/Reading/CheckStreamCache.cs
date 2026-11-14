using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

/// <summary>Operation-local decoded bytes; current-page chunks pin their borrowed payloads.</summary>
internal sealed class CheckStreamCache : IDisposable
{
    private const int MaximumEntries = 32;
    private const long MaximumPayloadBytes = 1024L * 1024L;
    private const long MaximumEntryBytes = 256L * 1024L;
    private ResourceGovernor? _governor;
    private CancellationToken _token;
    private readonly long _maximumBytes;
    private ParserMemoryScope? _memory;
    private List<Entry>? _entries;
    private long _payloadBytes;
    private bool _pageActive;

    private readonly record struct Entry(PdfStream Stream, ReadOnlyMemory<byte> Bytes, bool Pinned)
    {
        internal long Charge => Bytes.IsEmpty ? 0L : Bytes.Length + GovernedBufferStream.ArrayOverheadBytes;
    }

    private CheckStreamCache(ParserMemoryScope memory, ResourceGovernor governor, QpdfLimits limits, CancellationToken token)
    { _memory = memory; _governor = governor; _token = token; _maximumBytes = Math.Min(MaximumPayloadBytes, limits.MaxMemoryBytes / 16L); }

    internal static CheckStreamCache Create(ResourceGovernor governor, QpdfLimits limits, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(80L); return new(memory, governor, limits, token); }
        catch { memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(CheckStreamCache));

    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(CheckStreamCache));

    internal void BeginPage()
    {
        Memory.CheckOwner(Governor); _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
        if (_entries is not null)
        {
            for (int index = 0; index < _entries.Count; index++)
            { Governor.NoteOperation(); _entries[index] = _entries[index] with { Pinned = false }; }
        }
        _pageActive = true;
    }

    internal void EndPage() => _pageActive = false;

    internal bool TryBorrow(PdfStream stream, out ReadOnlyMemory<byte> bytes)
    {
        Memory.CheckOwner(Governor); _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
        if (_entries is not null)
        {
            for (int index = 0; index < _entries.Count; index++)
            {
                Governor.NoteOperation();
                Entry entry = _entries[index];
                if (!ReferenceEquals(entry.Stream, stream)) { continue; }
                _entries.RemoveAt(index);
                _entries.Add(entry with { Pinned = entry.Pinned || _pageActive });
                bytes = entry.Bytes; return true;
            }
        }
        bytes = ReadOnlyMemory<byte>.Empty; return false;
    }

    internal bool Remember(PdfStream stream, ReadOnlyMemory<byte> bytes, ParserMemoryScope source)
    {
        ParserMemoryScope memory = Memory;
        source.CheckOwner(Governor); _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
        Entry entry = new(stream, bytes, _pageActive);
        long charge = entry.Charge;
        if (charge > Math.Min(MaximumEntryBytes, _maximumBytes)) { return false; }
        _entries ??= ParserContainers.CreateList<Entry>(memory, MaximumEntries, 40L);
        while (_entries.Count == MaximumEntries || _payloadBytes + charge > _maximumBytes)
        {
            int index = 0;
            while (index < _entries.Count && _entries[index].Pinned) { Governor.NoteOperation(); index++; }
            // Chunks on this page still borrow these arrays. Leave the new
            // payload with its caller instead of refunding a live borrow.
            if (index == _entries.Count) { return false; }
            long retired = _entries[index].Charge;
            _entries.RemoveAt(index);
            _payloadBytes -= retired; memory.Release(retired);
        }
        source.TransferTo(memory, charge);
        try { _entries.Add(entry); _payloadBytes += charge; }
        catch { memory.TransferTo(source, charge); throw; }
        return true;
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _entries = null; _memory = null; _governor = null; _token = default; _payloadBytes = 0L; _pageActive = false;
        memory?.Dispose();
    }
}
