using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Runtime;

/// <summary>Owns reservations made by one parser attempt, independently of resolver loads.</summary>
internal sealed class ParserMemoryScope : IDisposable
{
    private const long OwnerBytes = 64L;
    private readonly ResourceGovernor _governor;
    private long _reservedBytes;
    private bool _disposed;

    internal long ReservedBytes => _reservedBytes;

    private ParserMemoryScope(ResourceGovernor governor) { _governor = governor; }

    internal static ParserMemoryScope Create(ResourceGovernor governor)
    {
        governor.CheckCancellation();
        governor.ReserveMemory(OwnerBytes);
        try { return new ParserMemoryScope(governor); }
        catch { governor.ReleaseMemory(OwnerBytes); throw; }
    }

    internal void Reserve(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long next = checked(_reservedBytes + bytes);
        _governor.ReserveMemory(bytes);
        _reservedBytes = next;
    }

    internal void Release(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes < 0L || bytes > _reservedBytes)
        {
            throw new InvalidOperationException("Parser release exceeds its attempt's ownership.");
        }
        _reservedBytes -= bytes;
        _governor.ReleaseMemory(bytes);
    }

    // Explicit container/cache owners adopt this receipt themselves. Parsed
    // values instead transfer to their independently escaping storage below.
    internal void Retain()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _reservedBytes = 0L;
    }

    internal void Retain(QpdfObject value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long retained = _governor.RetainValue(value, _reservedBytes);
        _governor.ReleaseMemory(_reservedBytes - retained);
        _reservedBytes = 0L;
    }

    internal void Retain(object wrapper, long wrapperBytes, IReadOnlyList<QpdfObject> roots)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long retained = _governor.RetainValues(wrapper, wrapperBytes, roots, _reservedBytes);
        _governor.ReleaseMemory(_reservedBytes - retained);
        _reservedBytes = 0L;
    }

    // Transfer one independently escaping allocation while preserving this
    // scope's remaining storage. The retained ledger admits its own metadata.
    internal void RetainAllocation(object carrier, long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(carrier);
        if (bytes < 0L || bytes > _reservedBytes)
        { throw new InvalidOperationException("Retained allocation exceeds its scope's ownership."); }
        long retained = _governor.RetainValues(carrier, bytes, Array.Empty<QpdfObject>(), bytes);
        _reservedBytes -= bytes;
        _governor.ReleaseMemory(bytes - retained);
    }

    // An exclusive prepared owner needs the receipt to abandon an unexposed
    // candidate immediately, without waiting for weak collection.
    internal RetainedValueOwners.Receipt RetainAllocationReceipt(object carrier, long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(carrier);
        if (bytes < 0L || bytes > _reservedBytes)
        { throw new InvalidOperationException("Retained allocation exceeds its scope's ownership."); }
        RetainedValueOwners.Receipt receipt = _governor.RetainAllocation(carrier, bytes);
        _reservedBytes -= bytes;
        return receipt;
    }

    internal void CheckOwner(ResourceGovernor governor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(_governor, governor))
        {
            throw new InvalidOperationException("Parser ownership belongs to a different governor.");
        }
    }

    internal void RetainAllocations(OwnedAllocationBatch allocations)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        allocations.CheckOwner(this, _governor);
        long bytes = allocations.Bytes;
        if (bytes > _reservedBytes) { throw new InvalidOperationException("Allocation batch exceeds its scope's ownership."); }
        long retained = _governor.RetainAllocations(allocations, bytes);
        // Acceptance has already preflighted every receipt; the accounting
        // handoff cannot fail and leaves scratch funded until this scope closes.
        _reservedBytes -= retained;
    }

    internal void RetainAllocations(OwnedAllocationBatch allocations, QpdfObject parsed, long parsedBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        allocations.CheckOwner(this, _governor);
        long bytes = checked(allocations.Bytes + parsedBytes);
        if (bytes > _reservedBytes) { throw new InvalidOperationException("Parsed allocation batch exceeds its scope's ownership."); }
        long retained = _governor.RetainAllocations(allocations, parsed, bytes);
        _reservedBytes -= retained;
    }

    internal void Discard() { Release(_reservedBytes); }

    internal void TransferTo(ParserMemoryScope target) => TransferTo(target, _reservedBytes);

    internal void TransferTo(ParserMemoryScope target, long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        target.CheckOwner(_governor);
        if (ReferenceEquals(this, target)) { throw new InvalidOperationException("An owner cannot transfer to itself."); }
        if (bytes < 0L || bytes > _reservedBytes) { throw new InvalidOperationException("Transfer exceeds its scope's ownership."); }
        target.AdoptReserved(bytes); _reservedBytes -= bytes;
    }

    // A decoder has already admitted this allocation through the same governor.
    internal void AdoptReserved(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        _reservedBytes = checked(_reservedBytes + bytes);
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _governor.ReleaseMemory(checked(_reservedBytes + OwnerBytes));
        _reservedBytes = 0L;
    }
}
