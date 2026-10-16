using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf;

namespace Lokad.Qpdf.TestSupport;

// Development-only shared allowance, compiled by tests and the isolated package
// consumer. This is deliberately independent of the production implementation.
internal sealed class RecordingMemoryBudget : IMemoryBudget
{
    private readonly object _gate = new();
    private readonly Scope _root;
    private long _reserved;
    private long _peak;
    private int _reserves;
    private int _resizes;
    private readonly List<long> _requests = [];

    internal RecordingMemoryBudget(long limitBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limitBytes);
        LimitBytes = limitBytes;
        _root = new(this, null);
    }

    internal long LimitBytes { get; }
    internal long ReservedBytes { get { lock (_gate) { return _reserved; } } }
    internal long PeakBytes { get { lock (_gate) { return _peak; } } }
    internal int ReserveCalls { get { lock (_gate) { return _reserves; } } }
    internal int ResizeCalls { get { lock (_gate) { return _resizes; } } }
    internal long[] Requests { get { lock (_gate) { return _requests.ToArray(); } } }

    public IMemoryScope CreateScope() => _root.CreateScope();
    public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        => _root.TryReserve(bytes, out reservation);

    private bool Admit(long bytes, long previous)
    {
        _requests.Add(bytes);
        long delta = bytes - previous;
        if (delta > LimitBytes - _reserved) { return false; }
        _reserved += delta;
        _peak = Math.Max(_peak, _reserved);
        return true;
    }

    private sealed class Scope(RecordingMemoryBudget root, Scope? parent) : IMemoryScope
    {
        private readonly List<Scope> _children = [];
        private readonly HashSet<Reservation> _reservations = [];
        private bool _disposed;

        public IMemoryScope CreateScope()
        {
            lock (root._gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                Scope child = new(root, this);
                _children.Add(child);
                return child;
            }
        }

        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            lock (root._gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                root._reserves++;
                if (!root.Admit(bytes, 0L)) { reservation = null; return false; }
                Reservation admitted = new(root, this, bytes);
                _reservations.Add(admitted);
                reservation = admitted;
                return true;
            }
        }

        internal void Remove(Reservation reservation) => _reservations.Remove(reservation);

        public void Dispose()
        {
            lock (root._gate)
            {
                Close();
                parent?._children.Remove(this);
            }
        }

        private void Close()
        {
            if (_disposed) { return; }
            _disposed = true;
            foreach (Scope child in _children) { child.Close(); }
            _children.Clear();
            foreach (Reservation reservation in _reservations) { reservation.Close(); }
            _reservations.Clear();
        }
    }

    private sealed class Reservation(RecordingMemoryBudget root, Scope owner, long bytes) : IMemoryReservation
    {
        private long _bytes = bytes;
        private bool _disposed;

        public long Bytes { get { lock (root._gate) { return _bytes; } } }

        public bool TryResize(long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            lock (root._gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                root._resizes++;
                if (!root.Admit(bytes, _bytes)) { return false; }
                _bytes = bytes;
                return true;
            }
        }

        internal void Close()
        {
            if (_disposed) { return; }
            root._reserved -= _bytes;
            _bytes = 0L;
            _disposed = true;
        }

        public void Dispose()
        {
            lock (root._gate)
            {
                Close();
                owner.Remove(this);
            }
        }
    }
}
