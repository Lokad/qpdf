using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

/// <summary>Owns inline content-entry metadata while borrowing streams and their payloads.</summary>
internal sealed class ContentEntries : IDisposable
{
    internal readonly record struct Entry(int? ObjectId, int ArrayIndex, PdfStream Stream);
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private List<Entry>? _items;
    private CancellationToken _token;

    private ContentEntries(ParserMemoryScope memory, ResourceGovernor governor, List<Entry> items, CancellationToken token)
    { _memory = memory; _governor = governor; _items = items; _token = token; }

    internal static ContentEntries Create(ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(64L); return new(memory, governor, ParserContainers.CreateList<Entry>(memory), token); }
        catch { memory.Dispose(); throw; }
    }

    private List<Entry> Items => _items ?? throw new ObjectDisposedException(nameof(ContentEntries));
    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(ContentEntries));
    internal int Count => Items.Count;

    private void Visit()
    { _token.ThrowIfCancellationRequested(); Governor.CheckCancellation(); }

    internal Entry Get(int index)
    { List<Entry> items = Items; Visit(); return items[index]; }

    internal void Add(Entry entry)
    {
        List<Entry> items = Items; Visit();
        ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(ContentEntries));
        if (items.Count == items.Capacity)
        {
            long capacity = Math.Min(Array.MaxLength, Math.Max(4L, items.Capacity * 2L));
            Governor.NoteOperation();
            _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
        }
        ParserContainers.AddItem(memory, items, entry, 24L);
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _items = null; _memory = null; _governor = null; _token = CancellationToken.None;
        memory?.Dispose();
    }
}
