using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf;

/// <summary>Cooperative per-call resource admission for a caller-supplied JPEG codec.</summary>
/// <remarks>Valid only during the codec call, on its executing thread. It must not escape the call.</remarks>
public sealed class QpdfJpegCodecContext
{
    private ResourceGovernor? _governor;
    private long _reserved;

    internal QpdfJpegCodecContext(ResourceGovernor governor) { _governor = governor; }
    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(QpdfJpegCodecContext));

    /// <summary>Gets the operation's cancellation token.</summary>
    public CancellationToken CancellationToken => Governor.CancellationToken;

    /// <summary>Observes cancellation before continuing codec work.</summary>
    public void CheckCancellation() => Governor.CheckCancellation();

    /// <summary>Admits private storage before allocation; dispose the returned lease when the storage is released.</summary>
    /// <remarks>Outstanding charges retire when the codec call ends, including failure. The codec still owns actual storage cleanup.</remarks>
    public IDisposable ReserveMemory(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ResourceGovernor governor = Governor;
        governor.CheckCancellation();
        long next = checked(_reserved + bytes);
        governor.ReserveMemory(bytes);
        try
        {
            governor.CheckCancellation();
            Reservation result = new(this, bytes);
            _reserved = next;
            return result;
        }
        catch { governor.ReleaseMemory(bytes); throw; }
    }

    /// <summary>Changes an active reservation from this context before growing or after releasing its storage.</summary>
    /// <remarks>
    /// Growth observes cancellation and admits the increase before changing the reservation.
    /// Refusal or cancellation leaves its estimate unchanged. Shrink and no-op updates can
    /// complete after cancellation. Account for old and new buffers together during replacement.
    /// Disposing the reservation refunds its current estimate; the context must still be active.
    /// </remarks>
    /// <exception cref="ArgumentException">The reservation was not created by this context.</exception>
    /// <exception cref="ObjectDisposedException">The context or reservation is closed.</exception>
    public void ResizeMemory(IDisposable reservation, long bytes)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ResourceGovernor governor = Governor;
        if (reservation is not Reservation receipt)
        { throw new ArgumentException("Expected a reservation from this context.", nameof(reservation)); }
        ObjectDisposedException.ThrowIf(receipt.Context is null, reservation);
        if (!ReferenceEquals(receipt.Context, this))
        { throw new ArgumentException("Expected a reservation from this context.", nameof(reservation)); }
        long increase = bytes - receipt.Bytes;
        long next = checked(_reserved + increase);
        if (increase > 0L)
        {
            governor.CheckCancellation();
            governor.ReserveMemory(increase);
            try { governor.CheckCancellation(); }
            catch { governor.ReleaseMemory(increase); throw; }
        }
        else if (increase < 0L) { governor.ReleaseMemory(-increase); }
        receipt.Bytes = bytes;
        _reserved = next;
    }

    private void Release(long bytes)
    {
        ResourceGovernor? governor = _governor;
        if (governor is null) { return; }
        governor.ReleaseMemory(bytes); _reserved -= bytes;
    }

    internal void Close()
    {
        ResourceGovernor? governor = _governor;
        _governor = null;
        if (governor is not null) { governor.ReleaseMemory(_reserved); }
        _reserved = 0L;
    }

    private sealed class Reservation(QpdfJpegCodecContext context, long bytes) : IDisposable
    {
        internal QpdfJpegCodecContext? Context { get; private set; } = context;
        internal long Bytes { get; set; } = bytes;
        public void Dispose()
        {
            QpdfJpegCodecContext? owner = Context; Context = null;
            long released = Bytes; Bytes = 0L; owner?.Release(released);
        }
    }
}
