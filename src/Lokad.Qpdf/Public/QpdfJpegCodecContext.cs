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
            Reservation result = new(this, bytes);
            _reserved = next;
            return result;
        }
        catch { governor.ReleaseMemory(bytes); throw; }
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
        private QpdfJpegCodecContext? _context = context;
        public void Dispose()
        {
            QpdfJpegCodecContext? owner = _context; _context = null; owner?.Release(bytes);
        }
    }
}
