namespace Lokad.Qpdf.Runtime;

/// <summary>Transfers fresh byte estimates without retaining objects or visiting borrowed graphs.</summary>
internal sealed class OwnedAllocationBatch : IDisposable
{
    private ParserMemoryScope? _memory;
    private ResourceGovernor? _governor;
    private readonly int _capacity;
    private CancellationToken _token;
    private long _bytes;
    private int _count;
    private bool _accepted, _resultLifetime;

    private OwnedAllocationBatch(ParserMemoryScope memory, ResourceGovernor governor, int capacity, CancellationToken token)
    { _memory = memory; _governor = governor; _capacity = capacity; _token = token; }

    internal static OwnedAllocationBatch Create(ResourceGovernor governor, int capacity, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(governor); ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try { memory.Reserve(64L); return new(memory, governor, capacity, token); }
        catch { memory.Dispose(); throw; }
    }

    internal ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(OwnedAllocationBatch));
    internal ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(OwnedAllocationBatch));
    internal CancellationToken Token => _token;
    internal int Count => _count;
    internal long Bytes => _bytes;
    internal bool ResultLifetime => _resultLifetime;

    private void CheckMutable()
    {
        _ = Memory;
        if (_accepted) { throw new InvalidOperationException("An accepted allocation batch cannot change."); }
        _token.ThrowIfCancellationRequested(); Governor.CheckCancellation();
    }

    internal void Add(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes); CheckMutable();
        if (_count == _capacity) { throw new InvalidOperationException("Allocation batch capacity exceeded."); }
        long next = checked(_bytes + bytes);
        if (next > Memory.ReservedBytes) { throw new InvalidOperationException("Allocation batch exceeds its scope's ownership."); }
        _count++; _bytes = next;
    }

    internal void UpdateBytes(long previous, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes); CheckMutable();
        if (previous < 0L || previous > _bytes) { throw new ArgumentOutOfRangeException(nameof(previous)); }
        long next = checked(_bytes - previous + bytes);
        if (next > Memory.ReservedBytes) { throw new InvalidOperationException("Allocation batch exceeds its scope's ownership."); }
        _bytes = next;
    }

    internal void CheckOwner(ParserMemoryScope memory, ResourceGovernor governor)
    {
        if (!ReferenceEquals(Memory, memory) || !ReferenceEquals(Governor, governor))
        { throw new InvalidOperationException("Allocation batch belongs to its construction scope."); }
    }

    internal void Retain() => RetainCore(false);
    internal void RetainResult() => RetainCore(true);

    private void RetainCore(bool resultLifetime)
    {
        CheckMutable(); _resultLifetime = resultLifetime;
        if (_count != 0) { Memory.RetainAllocations(this); }
        _accepted = true;
    }

    internal void RetainParsed(ParserMemoryScope source)
    {
        ArgumentNullException.ThrowIfNull(source); CheckMutable(); source.CheckOwner(Governor);
        long bytes = source.ReservedBytes;
        source.TransferTo(Memory);
        try { Memory.RetainAllocations(this, bytes); _accepted = true; }
        catch { Memory.TransferTo(source, bytes); throw; }
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _memory = null; _governor = null; _token = CancellationToken.None;
        _count = 0; _bytes = 0L; memory?.Dispose();
    }
}
