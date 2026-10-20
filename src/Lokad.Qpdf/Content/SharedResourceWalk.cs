using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

/// <summary>Owns scratch indexes and borrowed pending nodes for the shared-resource decision.</summary>
internal sealed class SharedResourceWalk : IDisposable
{
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private GovernedIntSet? _nodes;
    private GovernedIntSet? _resources;
    private List<QpdfObject>? _pending;

    private SharedResourceWalk(ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        _memory = memory; _governor = governor;
        _nodes = GovernedIntSet.Create(memory, governor, token);
        _resources = GovernedIntSet.Create(memory, governor, token);
        _pending = ParserContainers.CreateList<QpdfObject>(memory);
    }

    internal static SharedResourceWalk Create(ResourceGovernor governor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(64L); return new(memory, governor, token); }
        catch { memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(SharedResourceWalk));
    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(SharedResourceWalk));
    private List<QpdfObject> Pending => _pending ?? throw new ObjectDisposedException(nameof(SharedResourceWalk));
    internal int Count => Pending.Count;

    internal bool VisitNode(int number, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return (_nodes ?? throw new ObjectDisposedException(nameof(SharedResourceWalk))).Add(Memory, number); }
    internal bool VisitResource(int number, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return (_resources ?? throw new ObjectDisposedException(nameof(SharedResourceWalk))).Add(Memory, number); }

    internal void Push(QpdfObject node, CancellationToken token)
    {
        List<QpdfObject> pending = Pending; ResourceGovernor governor = Governor;
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (pending.Count == pending.Capacity)
        {
            governor.NoteOperation();
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        }
        ParserContainers.AddItem(Memory, pending, node);
    }

    internal bool TryPop(CancellationToken token, out QpdfObject node)
    {
        List<QpdfObject> pending = Pending; ResourceGovernor governor = Governor;
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (pending.Count == 0) { node = PdfNull.Instance; return false; }
        int index = pending.Count - 1; node = pending[index]; pending.RemoveAt(index); return true;
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _pending = null; _nodes = null; _resources = null; _memory = null; _governor = null;
        memory?.Dispose();
    }
}
