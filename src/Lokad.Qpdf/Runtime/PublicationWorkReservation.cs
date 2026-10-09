namespace Lokad.Qpdf.Runtime;

/// <summary>Holds finite publication-loop work without charging unexecuted steps.</summary>
internal sealed class PublicationWorkReservation : IDisposable
{
    private ResourceGovernor? _governor;
    private ParserMemoryScope? _memory;
    internal long Remaining { get; private set; }
    internal bool Publishing { get; private set; }

    private PublicationWorkReservation(ResourceGovernor governor, ParserMemoryScope memory, long units)
    { _governor = governor; _memory = memory; Remaining = units; }

    internal static PublicationWorkReservation Create(ResourceGovernor governor, long units)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(units);
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(48L);
            PublicationWorkReservation reservation = new(governor, memory, units);
            governor.AttachPublicationWork(reservation);
            return reservation;
        }
        catch { memory.Dispose(); throw; }
    }

    internal void Begin()
    {
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(PublicationWorkReservation));
        governor.CheckCancellation(); Publishing = true;
    }

    internal void Consume(long units)
    {
        if (units > Remaining) { throw new InvalidOperationException("Publication work exceeds its admitted bound."); }
        Remaining -= units;
    }

    public void Dispose()
    {
        ResourceGovernor? governor = _governor;
        if (governor is null) { return; }
        governor.DetachPublicationWork(this); _governor = null;
        Remaining = 0L; Publishing = false;
        ParserMemoryScope? memory = _memory; _memory = null; memory?.Dispose();
    }
}
