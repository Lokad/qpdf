using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using ImageScope = Lokad.Image.IMemoryScope;
using ImageReservation = Lokad.Image.IMemoryReservation;

namespace Lokad.Qpdf.Image;

// One codec call forwards every child to the active qpdf context. It adds
// ownership and interface translation, never another allowance or retry policy.
internal sealed class JpegMemoryBudget : ImageScope
{
    private readonly object _gate;
    private readonly JpegMemoryBudget _root;
    private readonly JpegMemoryBudget? _parent;
    private readonly HashSet<JpegMemoryBudget> _children = [];
    private readonly HashSet<Receipt> _receipts = [];
    private QpdfJpegCodecContext? _context;
    private ExceptionDispatchInfo? _refusal;

    internal JpegMemoryBudget(QpdfJpegCodecContext context)
    { _context = context; _gate = new(); _root = this; }

    private JpegMemoryBudget(JpegMemoryBudget parent)
    { _context = parent.Context; _gate = parent._gate; _root = parent._root; _parent = parent; }

    private QpdfJpegCodecContext Context => _context ?? throw new ObjectDisposedException(nameof(JpegMemoryBudget));

    internal void RethrowRefusal() => _root._refusal?.Throw();

    public ImageScope CreateScope()
    {
        lock (_gate)
        {
            _ = Context;
            JpegMemoryBudget child = new(this);
            _children.Add(child);
            return child;
        }
    }

    public bool TryReserve(long bytes, [NotNullWhen(true)] out ImageReservation? reservation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            QpdfJpegCodecContext context = Context;
            IDisposable storage;
            try { storage = context.ReserveMemory(bytes); }
            catch (Exception failure) when (failure is QpdfLimitException or QpdfMemoryAdmissionException)
            { _root._refusal ??= ExceptionDispatchInfo.Capture(failure); reservation = null; return false; }
            try
            {
                Receipt receipt = new(this, storage, bytes);
                _receipts.Add(receipt);
                reservation = receipt;
                return true;
            }
            catch { storage.Dispose(); throw; }
        }
    }

    public void Dispose()
    {
        lock (_gate) { Close(); _parent?._children.Remove(this); }
    }

    private void Close()
    {
        if (_context is null) { return; }
        foreach (JpegMemoryBudget child in _children) { child.Close(); }
        _children.Clear();
        foreach (Receipt receipt in _receipts) { receipt.Close(); }
        _receipts.Clear();
        _context = null;
    }

    private sealed class Receipt(JpegMemoryBudget owner, IDisposable storage, long bytes) : ImageReservation
    {
        private readonly object _gate = owner._gate;
        private JpegMemoryBudget? _owner = owner;
        private IDisposable? _storage = storage;
        private long _bytes = bytes;

        public long Bytes { get { lock (_gate) { return _bytes; } } }

        public bool TryResize(long bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            lock (_gate)
            {
                JpegMemoryBudget scope = _owner ?? throw new ObjectDisposedException(nameof(Receipt));
                IDisposable reservation = _storage ?? throw new ObjectDisposedException(nameof(Receipt));
                try { scope.Context.ResizeMemory(reservation, bytes); }
                catch (Exception failure) when (failure is QpdfLimitException or QpdfMemoryAdmissionException)
                { scope._root._refusal ??= ExceptionDispatchInfo.Capture(failure); return false; }
                _bytes = bytes;
                return true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                JpegMemoryBudget? scope = _owner;
                Close();
                scope?._receipts.Remove(this);
            }
        }

        internal void Close()
        {
            _owner = null; _bytes = 0L;
            IDisposable? storage = _storage; _storage = null; storage?.Dispose();
        }
    }
}
