namespace Lokad.Qpdf.Runtime;

/// <summary>Holds summary slots and exact message quotas while the job runs.</summary>
internal sealed class PresentationCountReservation : IDisposable
{
    private const long OwnerBytes = 48L;
    private ResourceGovernor? _governor;
    private long _pending;
    private long _anticipatedOutput;
    private readonly byte _summaries;
    private bool _outputStarted;
    private bool _presented;

    private PresentationCountReservation(ResourceGovernor governor, long pending, byte summaries)
    { _governor = governor; _pending = pending; _summaries = summaries; }

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
            byte summaries = (byte)((verbose ? 1 : 0) | (reportMemoryUsage ? 2 : 0) | (progress ? 4 : 0));
            PresentationCountReservation reservation = new(governor, pending, summaries);
            governor.AttachPresentationCount(reservation);
            return reservation;
        }
        catch { governor.ReleaseMemory(OwnerBytes); throw; }
    }

    internal long Pending => _pending;
    internal long AnticipatedOutput => _anticipatedOutput;

    internal void BeginOutput(long outputBytes, long decodedBytes, long peakMemoryBytes)
    {
        if ((_summaries & 4) == 0 || _outputStarted) { return; }
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(PresentationCountReservation));
        long pending = checked(_pending + 4L);
        governor.CheckDiagnosticCount(pending);
        governor.CheckPendingDiagnosticBytes(MessageBytes(outputBytes, decodedBytes, peakMemoryBytes));
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

    internal long MessageBytes(long outputBytes, long decodedBytes, long peakMemoryBytes)
    {
        if (_presented) { return 0L; }
        outputBytes = Math.Max(outputBytes, _anticipatedOutput);
        // These summaries contain only literals and nonnegative decimal counters.
        // Default integer formatting uses ASCII digits without grouping. Relational
        // cases have fixed work and need no formatting buffer or temporary string.
        long bytes = 0L;
        if ((_summaries & 1) != 0)
        { bytes += 72L + DecimalWidth(outputBytes) + DecimalWidth(decodedBytes) + DecimalWidth(peakMemoryBytes); }
        if ((_summaries & 2) != 0) { bytes += 38L + DecimalWidth(peakMemoryBytes); }
        if ((_summaries & 4) != 0 && outputBytes > 0L) { bytes += 121L + 4L * DecimalWidth(outputBytes); }
        return bytes;
    }

    internal void AnticipateOutput(long outputBytes, long decodedBytes, long peakMemoryBytes)
    {
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(PresentationCountReservation));
        if (outputBytes < _anticipatedOutput) { return; }
        long pending = checked(_pending + (((_summaries & 4) != 0 && !_outputStarted && outputBytes > 0L) ? 4L : 0L));
        governor.CheckDiagnosticCount(pending);
        governor.CheckPendingDiagnosticBytes(MessageBytes(outputBytes, decodedBytes, peakMemoryBytes));
        _anticipatedOutput = outputBytes;
        _pending = pending;
        if ((_summaries & 4) != 0 && outputBytes > 0L) { _outputStarted = true; }
    }

    private static int DecimalWidth(long value) => value switch
    {
        < 10L => 1,
        < 100L => 2,
        < 1_000L => 3,
        < 10_000L => 4,
        < 100_000L => 5,
        < 1_000_000L => 6,
        < 10_000_000L => 7,
        < 100_000_000L => 8,
        < 1_000_000_000L => 9,
        < 10_000_000_000L => 10,
        < 100_000_000_000L => 11,
        < 1_000_000_000_000L => 12,
        < 10_000_000_000_000L => 13,
        < 100_000_000_000_000L => 14,
        < 1_000_000_000_000_000L => 15,
        < 10_000_000_000_000_000L => 16,
        < 100_000_000_000_000_000L => 17,
        < 1_000_000_000_000_000_000L => 18,
        _ => 19,
    };

    public void Dispose()
    {
        ResourceGovernor? governor = _governor;
        if (governor is null) { return; }
        governor.DetachPresentationCount(this);
        _governor = null;
        _pending = 0L;
        _anticipatedOutput = 0L;
        governor.ReleaseMemory(OwnerBytes);
    }
}
