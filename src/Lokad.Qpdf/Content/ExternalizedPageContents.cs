using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

/// <summary>Bounded identity index borrowing completed externalizations from the output graph.</summary>
internal sealed class ExternalizedPageContents : IDisposable
{
    private const int MaximumEntries = 32;
    internal readonly record struct Result(PdfReference Contents, PdfDictionary Resources);
    private readonly record struct Entry(QpdfObject Contents, PdfDictionary Resources, Result Replacement);
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private CancellationToken _token;
    private List<Entry>? _entries;

    private ExternalizedPageContents(ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    { _memory = memory; _governor = governor; _token = token; }

    internal static ExternalizedPageContents Create(ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(64L); return new(memory, governor, token); }
        catch { memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(ExternalizedPageContents));
    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(ExternalizedPageContents));

    internal bool TryGet(QpdfObject contents, PdfDictionary resources, out Result result)
    {
        Memory.CheckOwner(Governor); _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
        if (_entries is not null)
        {
            for (int index = 0; index < _entries.Count; index++)
            {
                Governor.NoteOperation();
                Entry entry = _entries[index];
                if (!ReferenceEquals(contents, entry.Contents) || !ReferenceEquals(resources, entry.Resources)) { continue; }
                _entries.RemoveAt(index); _entries.Add(entry);
                result = entry.Replacement; return true;
            }
        }
        result = default; return false;
    }

    internal void Remember(QpdfObject contents, PdfDictionary resources, Result replacement)
    {
        Memory.CheckOwner(Governor); _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
        _entries ??= ParserContainers.CreateList<Entry>(Memory, MaximumEntries, 32L);
        if (_entries.Count == MaximumEntries) { _entries.RemoveAt(0); }
        _entries.Add(new(contents, resources, replacement));
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _entries = null; _memory = null; _governor = null; _token = default;
        memory?.Dispose();
    }
}
