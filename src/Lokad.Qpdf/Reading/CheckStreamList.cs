using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

/// <summary>Owns temporary checker stream references without owning their payloads.</summary>
internal sealed class CheckStreamList : IDisposable
{
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private List<PdfStream>? _items;

    private CheckStreamList(ParserMemoryScope memory, ResourceGovernor governor, List<PdfStream> items)
    { _memory = memory; _governor = governor; _items = items; }

    internal static CheckStreamList Create(ResourceGovernor governor)
    {
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L);
            List<PdfStream> items = ParserContainers.CreateList<PdfStream>(memory);
            return new(memory, governor, items);
        }
        catch { memory.Dispose(); throw; }
    }

    private List<PdfStream> Items => _items ?? throw new ObjectDisposedException(nameof(CheckStreamList));
    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(CheckStreamList));
    internal int Count => Items.Count;

    internal PdfStream Get(int index, CancellationToken token)
    {
        List<PdfStream> items = Items; ResourceGovernor governor = Governor;
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        return items[index];
    }

    internal void Add(PdfStream stream, CancellationToken token)
    {
        List<PdfStream> items = Items; ResourceGovernor governor = Governor;
        ParserMemoryScope memory = _memory ?? throw new ObjectDisposedException(nameof(CheckStreamList));
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (items.Count == items.Capacity)
        {
            long capacity = Math.Min(Array.MaxLength, Math.Max(4L, items.Capacity * 2L));
            governor.NoteOperation();
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        }
        ParserContainers.AddItem(memory, items, stream, IntPtr.Size);
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _items = null; _memory = null; _governor = null;
        memory?.Dispose();
    }
}
