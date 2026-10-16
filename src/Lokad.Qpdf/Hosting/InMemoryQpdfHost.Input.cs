using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Hosting;

public sealed partial class InMemoryQpdfHost
{
    private sealed class StoredBytes(byte[] bytes, IMemoryReservation? admission)
    {
        private byte[]? _bytes = bytes;
        private IMemoryReservation? _admission = admission;
        private int _leases;
        private bool _retired;
        internal byte[] Bytes => _bytes ?? throw new ObjectDisposedException(nameof(StoredBytes));
        internal bool MemoryAdmitted => _admission is not null;
        internal void Acquire() { _leases++; }
        internal void ReleaseLease() { _leases--; ReleaseIfRetired(); }
        internal void Retire() { _retired = true; ReleaseIfRetired(); }

        private void ReleaseIfRetired()
        {
            if (!_retired || _leases != 0) { return; }
            _bytes = null;
            IMemoryReservation? reservation = _admission;
            _admission = null;
            reservation?.Dispose();
        }
    }

    private sealed class InMemoryInput : IQpdfInput, IImmutableInputSnapshot
    {
        private InMemoryQpdfHost? _host;
        private StoredBytes? _storage;
        internal InMemoryInput(InMemoryQpdfHost host, QpdfPath path, StoredBytes storage)
        { _host = host; _storage = storage; Path = path; Identity = path.Value; Length = storage.Bytes.Length; }
        public QpdfPath Path { get; }
        public string Identity { get; }
        public long Length { get; }
        private StoredBytes Storage => _storage ?? throw new ObjectDisposedException(nameof(IQpdfInput));
        bool IImmutableInputSnapshot.MemoryAdmitted => Storage.MemoryAdmitted;

        ReadOnlyMemory<byte> IImmutableInputSnapshot.ReadSnapshot(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Storage.Bytes; }

        IDisposable IImmutableInputSnapshot.RetainStorage()
        {
            StoredBytes storage = Storage;
            InMemoryQpdfHost host = _host ?? throw new ObjectDisposedException(nameof(IQpdfInput));
            SnapshotBorrow borrow = new(host, storage);
            storage.Acquire(); host._snapshotBorrows++;
            return borrow;
        }

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] snapshot = Storage.Bytes;
            if (offset < 0 || count < 0 || count > snapshot.Length - offset)
            { throw new ArgumentOutOfRangeException(nameof(count), "Read range exceeds the stable input length."); }
            if (count == 0) { return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty); }
            byte[] copy = GC.AllocateUninitializedArray<byte>(count);
            Copy(snapshot.AsSpan((int)offset, count), copy, cancellationToken);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(copy);
        }

        public ValueTask DisposeAsync()
        {
            InMemoryQpdfHost? host = _host;
            StoredBytes? storage = _storage;
            _host = null; _storage = null;
            if (host is not null) { storage?.ReleaseLease(); host.ReleaseLease(Identity); }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SnapshotBorrow(InMemoryQpdfHost host, StoredBytes storage) : IDisposable
    {
        private InMemoryQpdfHost? _host = host;
        private StoredBytes? _storage = storage;
        public void Dispose()
        {
            InMemoryQpdfHost? owner = _host;
            StoredBytes? storage = _storage;
            _host = null; _storage = null;
            if (owner is not null) { storage?.ReleaseLease(); owner._snapshotBorrows--; }
        }
    }
}
