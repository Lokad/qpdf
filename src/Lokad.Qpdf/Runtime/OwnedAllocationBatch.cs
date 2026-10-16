using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Runtime;

/// <summary>Transfers only fresh allocations, atomically, without visiting borrowed graphs.</summary>
internal sealed class OwnedAllocationBatch : IDisposable
{
    internal readonly record struct Allocation(object? Carrier, long Bytes);
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private Allocation[]? _items;
    private CancellationToken _token;
    private Action? _observer;
    private long _bytes;
    private int _count;
    private bool _retaining, _accepted;
    private bool _resultLifetime;

    private OwnedAllocationBatch(ParserMemoryScope memory, ResourceGovernor governor, Allocation[] items, CancellationToken token)
    { _memory = memory; _governor = governor; _items = items; _token = token; }

    internal static OwnedAllocationBatch Create(ResourceGovernor governor, int capacity, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(governor); ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (capacity > Array.MaxLength) { throw new ArgumentOutOfRangeException(nameof(capacity)); }
        governor.ChargeWork(checked(1L + capacity));
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(80L + (capacity == 0 ? 0L : checked(32L + 16L * capacity)));
            return new(memory, governor, capacity == 0 ? Array.Empty<Allocation>() : new Allocation[capacity], token);
        }
        catch { memory.Dispose(); throw; }
    }

    internal ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(OwnedAllocationBatch));
    internal ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(OwnedAllocationBatch));
    internal CancellationToken Token => _token;
    internal int Count => _count;
    internal long Bytes => _bytes;
    internal bool ResultLifetime => _resultLifetime;

    private void Step()
    { _token.ThrowIfCancellationRequested(); Governor.CheckCancellation(); Governor.ChargeWork(1L); }

    internal int Add(object carrier, long bytes)
    {
        ArgumentNullException.ThrowIfNull(carrier); ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        CheckMutable(); Step();
        Allocation[] items = _items ?? throw new ObjectDisposedException(nameof(OwnedAllocationBatch));
        if (_count == items.Length) { throw new InvalidOperationException("Allocation batch capacity exceeded."); }
        long next = checked(_bytes + bytes);
        if (next > Memory.ReservedBytes) { throw new InvalidOperationException("Allocation batch exceeds its scope's ownership."); }
        int index = _count++; items[index] = new(carrier, bytes); _bytes = next; return index;
    }

    internal void UpdateBytes(int index, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes); CheckMutable(); Step();
        Allocation[] items = _items ?? throw new ObjectDisposedException(nameof(OwnedAllocationBatch));
        if ((uint)index >= (uint)_count) { throw new ArgumentOutOfRangeException(nameof(index)); }
        long next = checked(_bytes - items[index].Bytes + bytes);
        if (next > Memory.ReservedBytes) { throw new InvalidOperationException("Allocation batch exceeds its scope's ownership."); }
        items[index] = new(items[index].Carrier, bytes); _bytes = next;
    }

    private void CheckMutable()
    {
        _ = Memory;
        if (_retaining || _accepted) { throw new InvalidOperationException("An accepted or transferring allocation batch cannot change."); }
    }

    internal Allocation Get(int index)
    {
        Step();
        if ((uint)index >= (uint)_count) { throw new ArgumentOutOfRangeException(nameof(index)); }
        return (_items ?? throw new ObjectDisposedException(nameof(OwnedAllocationBatch)))[index];
    }

    internal void ValidateRetention()
    {
        _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
        if (_observer is not null)
        {
            for (int index = 0; index < _count; index++)
            { Step(); _observer(); }
            _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
        }
    }

    internal void CheckOwner(ParserMemoryScope memory, ResourceGovernor governor)
    {
        if (!ReferenceEquals(Memory, memory) || !ReferenceEquals(Governor, governor))
        { throw new InvalidOperationException("Allocation batch belongs to its construction scope."); }
    }

    internal void Retain() => RetainCore(null);

    internal void RetainResult()
    { CheckMutable(); _resultLifetime = true; RetainCore(null); }

    internal void RetainParsed(QpdfObject parsed, ParserMemoryScope source)
    {
        ArgumentNullException.ThrowIfNull(parsed); ArgumentNullException.ThrowIfNull(source);
        CheckMutable(); Step(); source.CheckOwner(Governor);
        long bytes = source.ReservedBytes;
        source.TransferTo(Memory); _retaining = true;
        try { Memory.RetainAllocations(this, parsed, bytes); _accepted = true; }
        catch { Memory.TransferTo(source, bytes); throw; }
        finally { _retaining = false; }
    }

    internal void RetainObserved(Action observer)
    { ArgumentNullException.ThrowIfNull(observer); RetainCore(observer); }

    private void RetainCore(Action? observer)
    {
        CheckMutable(); Step(); _retaining = true; _observer = observer;
        try { if (_count != 0) { Memory.RetainAllocations(this); } _accepted = true; }
        finally { _observer = null; _retaining = false; }
    }

    public void Dispose()
    {
        if (_retaining) { throw new InvalidOperationException("A transferring allocation batch cannot close."); }
        ParserMemoryScope? memory = _memory; Allocation[]? items = _items;
        _memory = null; _governor = null; _items = null; _observer = null;
        _token = CancellationToken.None; _count = 0; _bytes = 0L;
        if (items is not null) { Array.Clear(items); }
        memory?.Dispose();
    }
}
