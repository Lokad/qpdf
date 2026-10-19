namespace Lokad.Qpdf.Runtime;

/// <summary>Admits sequential host calls and reusable publication storage.</summary>
internal sealed class PublicationReservation : IDisposable
{
    private ResourceGovernor? _governor;
    private ParserMemoryScope? _memory;
    internal long HostOperations { get; private set; }
    internal bool Publishing { get; private set; }
    // 0: no outputs, 1: prepaid wrapper available, 2: wrapper on an open lease.
    private byte _transactionState;

    private PublicationReservation(ResourceGovernor governor, ParserMemoryScope memory, long operations)
    { _governor = governor; _memory = memory; HostOperations = operations; _transactionState = operations > 0L ? (byte)1 : (byte)0; }

    internal static PublicationReservation Create(ResourceGovernor governor, long operations)
        => Create(governor, operations, 0L);

    internal static PublicationReservation Create(ResourceGovernor governor, long operations, long stagingBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(operations);
        ArgumentOutOfRangeException.ThrowIfNegative(stagingBytes);
        governor.CheckHostOperationCapacity(operations);
        if (operations > 0L) { governor.CheckHandleCapacity(1L); }
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(48L);
            if (operations > 0L) { memory.Reserve(GovernedHost.OutputWrapperBytes); }
            // Sequential transactions reuse this admitted staging allowance.
            // Local policy includes host staging. Shared admission for those
            // physical buffers belongs to the host's durable storage owner.
            memory.ReserveHostMemory(stagingBytes);
            PublicationReservation reservation = new(governor, memory, operations);
            governor.AttachPublication(reservation);
            return reservation;
        }
        catch { memory.Dispose(); throw; }
    }

    internal void Begin()
    {
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(PublicationReservation));
        governor.CheckCancellation(); governor.PrepareExecutionResult(); Publishing = true;
    }

    internal void AcquireTransaction()
    {
        if (_transactionState != 1) { throw new InvalidOperationException("Publication transactions must be sequential."); }
        _transactionState = 2;
    }

    internal void ReleaseTransaction()
    {
        if (_transactionState != 2) { throw new InvalidOperationException("Publication wrapper is not on a lease."); }
        _transactionState = 1;
    }

    public void Dispose()
    {
        ResourceGovernor? governor = _governor;
        if (governor is null) { return; }
        if (_transactionState == 2) { throw new InvalidOperationException("Close the publication lease before retiring its storage."); }
        governor.DetachPublication(this); _governor = null;
        HostOperations = 0L; Publishing = false; _transactionState = 0;
        ParserMemoryScope? memory = _memory; _memory = null; memory?.Dispose();
    }
}
