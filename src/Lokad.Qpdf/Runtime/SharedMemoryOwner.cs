namespace Lokad.Qpdf.Runtime;

/// <summary>Aggregates existing allocation estimates into one caller-owned reservation.</summary>
internal sealed class SharedMemoryOwner(IMemoryBudget budget, long localCeiling)
{
    internal const long BlockBytes = 16L * 1024L;
    private IMemoryBudget? _budget = budget;
    private IMemoryReservation? _reservation;
    private long _reserved;
    private bool _refused;
    private bool _shrinkDeclined;

    internal long ReservedBytes => _reserved;

    internal void Ensure(long liveBytes)
    {
        if (_budget is null) { return; }
        if (_refused) { throw new QpdfMemoryAdmissionException(liveBytes); }
        if (liveBytes <= _reserved) { return; }
        long target = Capacity(liveBytes);
        if (_reservation is null)
        {
            if (!_budget.TryReserve(target, out IMemoryReservation? reservation))
            { _refused = true; throw new QpdfMemoryAdmissionException(target); }
            _reservation = reservation;
        }
        else if (!_reservation.TryResize(target))
        { _refused = true; throw new QpdfMemoryAdmissionException(target); }
        _reserved = target;
    }

    internal void ReleaseTo(long liveBytes)
    {
        if (_budget is null || _reservation is null) { return; }
        if (liveBytes == 0L)
        {
            _reservation.Dispose();
            _reservation = null;
            _reserved = 0L;
        }
        else if (!_shrinkDeclined && _reserved - liveBytes >= BlockBytes)
        {
            long target = Capacity(liveBytes);
            if (_reservation.TryResize(target)) { _reserved = target; }
            else { _shrinkDeclined = true; }
        }
    }

    internal void TrimTo(long liveBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(liveBytes);
        if (_budget is null || _reservation is null || liveBytes >= _reserved) { return; }
        if (liveBytes == 0L)
        {
            _reservation.Dispose(); _reservation = null; _reserved = 0L;
        }
        else if (!_shrinkDeclined)
        {
            if (_reservation.TryResize(liveBytes)) { _reserved = liveBytes; }
            else { _shrinkDeclined = true; }
        }
        // A host may decline this optional shrink. The existing larger
        // reservation still funds every caller alias; do not retry or fail
        // after successful publication merely to recover block slack.
    }

    // Completion relinquishes bookkeeping, not the caller's retained charge.
    // Trim unused growth capacity once, while the caller's owner is still live.
    internal void Complete(long liveBytes)
    {
        try { TrimTo(liveBytes); }
        finally { _budget = null; _reservation = null; }
    }

    private long Capacity(long liveBytes)
    {
        long rounded = liveBytes <= 1024L ? 1024L
            : liveBytes > long.MaxValue - (BlockBytes - 1L) ? long.MaxValue
            : (liveBytes + BlockBytes - 1L) / BlockBytes * BlockBytes;
        return Math.Min(localCeiling, rounded);
    }
}
