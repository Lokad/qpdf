using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Hosting;

public sealed partial class InMemoryQpdfHost
{
    private sealed class InMemoryTransaction(InMemoryQpdfHost host, QpdfPath path)
        : IQpdfOutputTransaction, IOutputCapacityHint, ISynchronousOutputTransaction
    {
        private MemoryStream? _staging = new();
        private IMemoryReservation? _admission;
        private bool _settled, _disposed;
        public QpdfPath Path { get; } = path;
        private MemoryStream Staging => _staging ?? throw new InvalidOperationException("Staging is already released.");

        void IOutputCapacityHint.EnsureCapacity(long length, CancellationToken cancellationToken)
        {
            CheckUnsettled(cancellationToken);
            CheckLength(length);
            EnsureCapacity(checked((int)length), cancellationToken);
        }

        private void CheckLength(long length)
        {
            long maximum = Math.Min(host._limits.MaxOutputBytes, Array.MaxLength);
            if (length > maximum) { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), length, maximum); }
        }

        private void EnsureCapacity(int capacity, CancellationToken token)
        {
            if (capacity <= Staging.Capacity) { return; }
            IMemoryReservation? admission = host.ReserveStorage(capacity, Path);
            try
            {
                token.ThrowIfCancellationRequested();
                byte[] bytes = GC.AllocateUninitializedArray<byte>(capacity);
                int length = checked((int)Staging.Length);
                Copy(Staging.GetBuffer().AsSpan(0, length), bytes, token);
                MemoryStream next = new(bytes, 0, bytes.Length, true, true);
                next.SetLength(length);
                next.Position = length;
                MemoryStream? previous = _staging;
                IMemoryReservation? previousAdmission = _admission;
                _staging = next; _admission = admission; admission = null;
                previous?.Dispose(); previous = null;
                previousAdmission?.Dispose();
            }
            finally { admission?.Dispose(); }
        }

        private void CheckUnsettled(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_settled) { throw new InvalidOperationException("The transaction is already committed or discarded."); }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        { Write(data.Span, cancellationToken); return ValueTask.CompletedTask; }

        public void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken)
        {
            CheckUnsettled(cancellationToken);
            long next = checked(Staging.Length + data.Length);
            CheckLength(next);
            if (next > Staging.Capacity)
            {
                long growth = Math.Max(next, Math.Max(256L, 2L * Staging.Capacity));
                int capacity = (int)Math.Min(Math.Min(host._limits.MaxOutputBytes, Array.MaxLength), growth);
                EnsureCapacity(capacity, cancellationToken);
            }
            // Consume the borrow in bounded writes before returning.
            for (int offset = 0; offset < data.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(8192, data.Length - offset);
                Staging.Write(data.Slice(offset, count)); offset += count;
            }
        }

        public ValueTask CommitAsync(CancellationToken cancellationToken)
        {
            CheckUnsettled(cancellationToken);
            if (host._openIdentities.ContainsKey(Path.Value))
            { throw new QpdfHostException(Path, QpdfHostFailureReason.Conflict); }
            int length = checked((int)Staging.Length);
            bool reuse = Staging.Capacity == length;
            bool borrowedAdmission = reuse && _admission is not null;
            IMemoryReservation? admission = borrowedAdmission ? _admission : host.ReserveStorage(length, Path);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] bytes;
                if (reuse) { bytes = Staging.GetBuffer(); }
                else
                {
                    bytes = GC.AllocateUninitializedArray<byte>(length);
                    Copy(Staging.GetBuffer().AsSpan(0, length), bytes, cancellationToken);
                }
                StoredBytes publication = new(bytes, admission);
                host._outputs.TryGetValue(Path.Value, out StoredBytes? previous);
                string key = previous is null ? new string(Path.Value.AsSpan()) : Path.Value;
                host._outputs[key] = publication;
                if (reuse) { _admission = null; }
                admission = null;
                previous?.Retire();
                ReleaseStaging();
                _settled = true;
                host.ReleaseLease(null);
                return ValueTask.CompletedTask;
            }
            finally { if (!borrowedAdmission) { admission?.Dispose(); } }
        }

        public ValueTask DiscardAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_settled) { _settled = true; host.ReleaseLease(null); }
            ReleaseStaging();
            return ValueTask.CompletedTask;
        }

        private void ReleaseStaging()
        {
            _staging?.Dispose(); _staging = null;
            IMemoryReservation? admission = _admission; _admission = null;
            admission?.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                if (!_settled) { _settled = true; host.ReleaseLease(null); }
                ReleaseStaging();
            }
            return ValueTask.CompletedTask;
        }
    }
}
