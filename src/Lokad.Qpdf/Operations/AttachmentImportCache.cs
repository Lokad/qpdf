using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Operations;

// Completed attachment graphs borrow the output pool. No reader or input lease
// survives the request that prepared them. Failed or warning-producing requests
// keep the ordinary path, so reuse cannot suppress their diagnostics.
internal sealed class AttachmentImportCache : IDisposable
{
    private sealed record Source(string File, QpdfPassword? Password, EmbeddedFileEntry[] Entries);
    private const int MaximumSources = 32;
    private const int MaximumEntries = 4096;
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private CancellationToken _token;
    private List<Source>? _sources;
    private int _entryCount;

    private AttachmentImportCache(ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    { _memory = memory; _governor = governor; _token = token; }

    internal static AttachmentImportCache Create(ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(64L); return new(memory, governor, token); }
        catch { memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(AttachmentImportCache));
    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(AttachmentImportCache));

    internal IReadOnlyList<EmbeddedFileEntry>? Find(AttachmentCopy copy)
    {
        Memory.CheckOwner(Governor); _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
        if (_sources is null) { return null; }
        for (int index = 0; index < _sources.Count; index++)
        {
            Governor.NoteOperation();
            Source source = _sources[index];
            if (StringComparer.Ordinal.Equals(source.File, copy.File)
                && (ReferenceEquals(source.Password, copy.Password)
                    || source.Password is not null && copy.Password is not null && source.Password.HasSameInput(copy.Password, Governor)))
            {
                _sources.RemoveAt(index); _sources.Add(source);
                return source.Entries;
            }
        }
        return null;
    }

    internal EmbeddedFileEntry[]? CreateEntries(int count, ParserMemoryScope source)
    {
        source.CheckOwner(Governor); _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > MaximumEntries) { return null; }
        while (_sources is not null && _sources.Count > 0
            && (_sources.Count >= MaximumSources || count > MaximumEntries - _entryCount))
        {
            _token.ThrowIfCancellationRequested(); Governor.NoteOperation();
            Source retired = _sources[0];
            _sources.RemoveAt(0); _entryCount -= retired.Entries.Length;
            Memory.Release(64L + ArrayBytes(retired.Entries.Length));
        }
        if (count == 0) { return []; }
        source.Reserve(ArrayBytes(count));
        return new EmbeddedFileEntry[count];
    }

    internal void Remember(AttachmentCopy copy, EmbeddedFileEntry[] entries, ParserMemoryScope source)
    {
        ParserMemoryScope memory = Memory;
        source.CheckOwner(Governor); _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
        if ((_sources?.Count ?? 0) >= MaximumSources || entries.Length > MaximumEntries - _entryCount)
        { throw new InvalidOperationException("Attachment import cache capacity is exhausted."); }
        _sources ??= ParserContainers.CreateList<Source>(memory, MaximumSources, IntPtr.Size);
        memory.Reserve(64L);
        _sources.Add(new(copy.File, copy.Password, entries));
        source.TransferTo(memory, ArrayBytes(entries.Length));
        _entryCount += entries.Length;
    }

    private static long ArrayBytes(int count) => count == 0 ? 0L : checked(32L + count * 3L * IntPtr.Size);

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _sources = null; _memory = null; _governor = null; _token = CancellationToken.None; _entryCount = 0;
        memory?.Dispose();
    }
}
