using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf;

/// <summary>Borrowed per-call resources for a caller-supplied JPEG codec.</summary>
/// <remarks>
/// The context and its budget cannot escape the call. The codec owns only scopes
/// and reservations it creates. Budget operations are thread-safe; finish all
/// workers and buffer users before disposing their owners or returning. Streams
/// and samples retain their single-caller contract. Independent calls share no
/// context state. Samples and supplied input/output are already funded; admit
/// substantial private scratch and replacement overlap before acquisition.
/// </remarks>
public sealed class QpdfJpegCodecContext
{
    private readonly object _gate = new();
    private readonly Scope _budget;
    private ResourceGovernor? _governor;
    private ExceptionDispatchInfo? _refusal;

    internal QpdfJpegCodecContext(ResourceGovernor governor)
    { _governor = governor; _budget = new(this, null); }

    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(QpdfJpegCodecContext));

    /// <summary>Gets the borrowed allowance shared with this PDF operation.</summary>
    /// <remarks>
    /// Empty scopes reserve nothing and introduce no private ceiling. Refusal
    /// preserves existing charges. If the codec fails after a refusal, the PDF
    /// boundary reports that original budget failure instead of corrupt JPEG data.
    /// Successful fallbacks remain possible. Do not dispose the borrowed root.
    /// Outstanding charges retire at call completion, after all codec users finish.
    /// </remarks>
    public IMemoryBudget MemoryBudget { get { lock (_gate) { _ = Governor; return _budget; } } }

    /// <summary>Gets the operation's cancellation token.</summary>
    public CancellationToken CancellationToken { get { lock (_gate) { return Governor.CancellationToken; } } }

    /// <summary>Observes cancellation before continuing codec work.</summary>
    public void CheckCancellation() { lock (_gate) { Governor.CheckCancellation(); } }

    internal void RethrowMemoryRefusal() { lock (_gate) { _refusal?.Throw(); } }

    internal void Close()
    {
        lock (_gate)
        {
            if (_governor is null) { return; }
            _budget.Dispose(); _governor = null; _refusal = null;
        }
    }

    // Only parent links and aggregate totals are needed: descendants observe an
    // ancestor's closure, without child registries or per-receipt collections.
    private sealed class Scope(QpdfJpegCodecContext context, Scope? parent) : IMemoryScope
    {
        private bool _closed;
        private long _bytes;

        private bool Active
        {
            get
            {
                if (context._governor is null) { return false; }
                for (Scope? current = this; current is not null; current = current.Parent)
                { if (current._closed) { return false; } }
                return true;
            }
        }

        private Scope? Parent => parent;
        private void CheckActive() => ObjectDisposedException.ThrowIf(!Active, this);

        public IMemoryScope CreateScope()
        {
            lock (context._gate) { CheckActive(); return new Scope(context, this); }
        }

        private bool TryIncrease(long bytes)
        {
            ResourceGovernor governor = context.Governor;
            governor.CheckCancellation();
            try { governor.ReserveMemory(bytes); }
            catch (Exception failure) when (failure is QpdfLimitException or QpdfMemoryAdmissionException)
            { context._refusal ??= ExceptionDispatchInfo.Capture(failure); return false; }
            try { governor.CheckCancellation(); }
            catch { governor.ReleaseMemory(bytes); throw; }
            return true;
        }

        private void Adjust(long bytes)
        {
            for (Scope? current = this; current is not null; current = current.Parent)
            { current._bytes += bytes; }
        }

        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            lock (context._gate)
            {
                CheckActive();
                if (!TryIncrease(bytes)) { reservation = null; return false; }
                try { reservation = new Reservation(context, this, bytes); }
                catch { context.Governor.ReleaseMemory(bytes); throw; }
                Adjust(bytes); return true;
            }
        }

        public void Dispose()
        {
            lock (context._gate)
            {
                if (Active) { context.Governor.ReleaseMemory(_bytes); Adjust(-_bytes); }
                _closed = true;
            }
        }

        private sealed class Reservation(QpdfJpegCodecContext context, Scope owner, long bytes) : IMemoryReservation
        {
            private Scope? _owner = owner;
            private long _bytes = bytes;
            public long Bytes { get { lock (context._gate) { return _owner is { Active: true } ? _bytes : 0L; } } }

            public bool TryResize(long bytes)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(bytes);
                lock (context._gate)
                {
                    Scope scope = _owner ?? throw new ObjectDisposedException(nameof(Reservation));
                    scope.CheckActive();
                    long increase = bytes - _bytes;
                    if (increase > 0 && !scope.TryIncrease(increase)) { return false; }
                    if (increase < 0) { context.Governor.ReleaseMemory(-increase); }
                    scope.Adjust(increase); _bytes = bytes; return true;
                }
            }

            public void Dispose()
            {
                lock (context._gate)
                {
                    if (_owner is { Active: true } scope)
                    { context.Governor.ReleaseMemory(_bytes); scope.Adjust(-_bytes); }
                    _owner = null; _bytes = 0;
                }
            }
        }
    }
}
