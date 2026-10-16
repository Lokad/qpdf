namespace Lokad.Qpdf.Runtime;

/// <summary>Admits sequential publication work, host calls and reusable wrapper storage.</summary>
internal sealed class PublicationWorkReservation : IDisposable
{
    private ResourceGovernor? _governor;
    private ParserMemoryScope? _memory;
    internal long Remaining { get; private set; }
    internal bool Publishing { get; private set; }
    // 0: no outputs, 1: prepaid wrapper available, 2: wrapper on an open lease.
    private byte _transactionState;

    private PublicationWorkReservation(ResourceGovernor governor, ParserMemoryScope memory, long units)
    { _governor = governor; _memory = memory; Remaining = units; _transactionState = units > 0L ? (byte)1 : (byte)0; }

    internal static PublicationWorkReservation Create(ResourceGovernor governor, long units)
        => Create(governor, units, 0L);

    internal static PublicationWorkReservation Create(ResourceGovernor governor, long units, long stagingBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(units);
        ArgumentOutOfRangeException.ThrowIfNegative(stagingBytes);
        governor.CheckHostOperationCapacity(units);
        if (units > 0L) { governor.CheckHandleCapacity(1L); }
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(48L);
            if (units > 0L) { memory.Reserve(GovernedHost.OutputWrapperBytes); }
            // Sequential transactions reuse this admitted staging allowance.
            // Local policy includes host staging. Shared admission for those
            // physical buffers belongs to the host's durable storage owner.
            memory.ReserveHostMemory(stagingBytes);
            PublicationWorkReservation reservation = new(governor, memory, units);
            governor.AttachPublicationWork(reservation);
            return reservation;
        }
        catch { memory.Dispose(); throw; }
    }

    internal void Begin()
    {
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(PublicationWorkReservation));
        governor.CheckCancellation(); governor.PrepareExecutionResult(); Publishing = true;
    }

    internal void Consume(long units)
    {
        if (units > Remaining) { throw new InvalidOperationException("Publication work exceeds its admitted bound."); }
        Remaining -= units;
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
        governor.DetachPublicationWork(this); _governor = null;
        Remaining = 0L; Publishing = false; _transactionState = 0;
        ParserMemoryScope? memory = _memory; _memory = null; memory?.Dispose();
    }
}
