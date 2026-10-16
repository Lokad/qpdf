namespace Lokad.Qpdf.Runtime;

/// <summary>Aggregates existing allocation estimates into one caller-owned reservation.</summary>
internal sealed class SharedMemoryOwner(IMemoryBudget budget, long localCeiling)
{
    internal const long BlockBytes = 16L * 1024L;
    private IMemoryBudget? _budget = budget;
    private IMemoryReservation? _reservation;
    private long _reserved;
    private bool _refused;

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
        else if (_reserved - liveBytes >= BlockBytes)
        {
            long target = Capacity(liveBytes);
            MemoryAdmission.Resize(_reservation, target);
            _reserved = target;
        }
    }

    // A returned graph/payload stays charged beneath the supplied caller scope.
    // Closing local bookkeeping must not refund that caller-owned storage, and
    // later counter snapshots/GC retirement must not mutate its admission.
    internal void Complete()
    {
        _budget = null;
        _reservation = null;
    }

    private long Capacity(long liveBytes)
    {
        long rounded = liveBytes <= 1024L ? 1024L
            : liveBytes > long.MaxValue - (BlockBytes - 1L) ? long.MaxValue
            : (liveBytes + BlockBytes - 1L) / BlockBytes * BlockBytes;
        return Math.Min(localCeiling, rounded);
    }
}
