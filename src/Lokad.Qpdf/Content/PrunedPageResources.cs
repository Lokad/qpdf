using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Content;

/// <summary>Bounded identity index borrowing resource results already owned by the output graph.</summary>
internal sealed class PrunedPageResources : IDisposable
{
    private const int MaximumEntries = 32;
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private CancellationToken _token;
    private List<Entry>? _entries;
    private readonly record struct Entry(QpdfObject Contents, PdfDictionary Resources, PdfDictionary Result, int FirstPage, PdfReference? Shared);

    private PrunedPageResources(ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    { _memory = memory; _governor = governor; _token = token; }

    internal static PrunedPageResources Create(ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(64L); return new(memory, governor, token); }
        catch { memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(PrunedPageResources));
    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(PrunedPageResources));

    internal bool TryGet(QpdfObject contents, PdfDictionary resources, GraphCopier copier, out QpdfObject result)
    {
        Memory.CheckOwner(Governor); _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
        if (_entries is not null)
        {
            for (int index = 0; index < _entries.Count; index++)
            {
                Governor.NoteOperation();
                Entry entry = _entries[index];
                if (!ReferenceEquals(contents, entry.Contents) || !ReferenceEquals(resources, entry.Resources)) { continue; }
                if (entry.Shared is null)
                {
                    // Promote only on actual reuse. A single-page result keeps
                    // its compact direct header; repeated pages share one object.
                    PdfReference shared = copier.AddIndirectObjectWithId(entry.Result, copier.NextId);
                    PdfDictionary firstPage = (PdfDictionary)copier.CopiedObjects[entry.FirstPage];
                    using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(Governor, _token);
                    PdfDictionary replacement = PdfDictionaryRebuild.ReplaceEntry(graph, firstPage, "Resources"u8, shared);
                    copier.ValidateReplacement(entry.FirstPage); graph.Retain(); copier.ReplaceCopiedObject(entry.FirstPage, replacement);
                    entry = entry with { Shared = shared };
                }
                _entries.RemoveAt(index); _entries.Add(entry);
                result = entry.Shared; return true;
            }
        }
        result = resources; return false;
    }

    internal void Remember(QpdfObject contents, PdfDictionary resources, PdfDictionary result, int page)
    {
        Memory.CheckOwner(Governor); _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
        _entries ??= ParserContainers.CreateList<Entry>(Memory, MaximumEntries, 40L);
        if (_entries.Count == MaximumEntries) { _entries.RemoveAt(0); }
        _entries.Add(new(contents, resources, result, page, null));
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _entries = null; _memory = null; _governor = null; _token = default;
        memory?.Dispose();
    }
}
