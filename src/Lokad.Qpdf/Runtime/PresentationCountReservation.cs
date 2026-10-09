namespace Lokad.Qpdf.Runtime;

/// <summary>Holds summary slots while the job accepts ordinary diagnostics.</summary>
internal sealed class PresentationCountReservation : IDisposable
{
    private const long OwnerBytes = 48L;
    private ResourceGovernor? _governor;
    private long _pending;
    private readonly bool _progress;
    private bool _outputStarted;
    private bool _presented;

    private PresentationCountReservation(ResourceGovernor governor, long pending, bool progress)
    { _governor = governor; _pending = pending; _progress = progress; }

    internal static PresentationCountReservation? Create(ResourceGovernor governor, bool verbose,
        bool reportMemoryUsage, bool progress, CancellationToken cancellationToken)
    {
        if (!verbose && !reportMemoryUsage && !progress) { return null; }
        governor.CheckCancellation();
        cancellationToken.ThrowIfCancellationRequested();
        long pending = (verbose ? 1L : 0L) + (reportMemoryUsage ? 1L : 0L);
        governor.CheckDiagnosticCount(pending);
        governor.ChargeWork(1L);
        governor.ReserveMemory(OwnerBytes);
        try
        {
            PresentationCountReservation reservation = new(governor, pending, progress);
            governor.AttachPresentationCount(reservation);
            return reservation;
        }
        catch { governor.ReleaseMemory(OwnerBytes); throw; }
    }

    internal long Pending => _pending;

    internal void BeginOutput()
    {
        if (!_progress || _outputStarted) { return; }
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(PresentationCountReservation));
        long pending = checked(_pending + 4L);
        governor.CheckDiagnosticCount(pending);
        _pending = pending;
        _outputStarted = true;
    }

    internal void Present()
    {
        if (_presented) { throw new InvalidOperationException("Presentation slots were already accepted."); }
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(PresentationCountReservation));
        governor.AcceptPresentationCount(this, _pending);
        _pending = 0L;
        _presented = true;
    }

    public void Dispose()
    {
        ResourceGovernor? governor = _governor;
        if (governor is null) { return; }
        governor.DetachPresentationCount(this);
        _governor = null;
        _pending = 0L;
        governor.ReleaseMemory(OwnerBytes);
    }
}
