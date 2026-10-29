using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Hosting;

/// <summary>A host whose registered inputs, staging and published outputs live in memory.</summary>
/// <remarks>
/// Registered inputs and published outputs are separate stores, even at the same path.
/// Publication does not change what <see cref="OpenInputAsync"/> reads. To read a
/// published output as input, export it with <see cref="TryGetOutput(QpdfPath, out byte[])"/>
/// and register it with <see cref="AddInput(QpdfPath, byte[], QpdfInputOwnership)"/>.
/// Not safe for concurrent mutation. Close its leases before disposing the host.
/// </remarks>
public sealed partial class InMemoryQpdfHost : IQpdfHost, IQpdfJpegCodecHost, IQpdfMemoryHost, IDisposable
{
    // Payload owner and approximate dictionary backing capacity, amortized per file.
    internal const long StorageOverheadBytes = 256L;
    private readonly QpdfLimits _limits;
    private readonly Dictionary<string, StoredBytes> _inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredBytes> _outputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _openIdentities = new(StringComparer.Ordinal);
    private long _openLeases;
    private long _snapshotBorrows;
    private bool _disposed;

    /// <summary>Creates a host with default local limits.</summary>
    public InMemoryQpdfHost() : this(QpdfLimits.Default) { }

    /// <summary>Creates a host with explicit local limits.</summary>
    public InMemoryQpdfHost(QpdfLimits limits) : this(limits, null) { }

    /// <summary>Creates a host with local limits and an optional caller-supplied JPEG codec.</summary>
    public InMemoryQpdfHost(QpdfLimits limits, IQpdfJpegCodec? jpegCodec) : this(limits, jpegCodec, null) { }

    /// <summary>Creates a host with local limits, an optional codec and a borrowed durable memory owner.</summary>
    /// <remarks>
    /// The owner covers retained host storage and default invocation admission.
    /// It must outlive this host, all leases and exported copies. A per-invocation
    /// host view can supply a shorter execution owner sharing the same root.
    /// Null preserves local limits without shared admission.
    /// </remarks>
    public InMemoryQpdfHost(QpdfLimits limits, IQpdfJpegCodec? jpegCodec, IMemoryBudget? memoryBudget)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        _limits = limits;
        JpegCodec = jpegCodec;
        MemoryBudget = memoryBudget;
    }

    /// <summary>Gets the supplied JPEG codec, or null.</summary>
    public IQpdfJpegCodec? JpegCodec { get; }

    /// <summary>Gets the borrowed durable memory owner, or null when only local limits apply.</summary>
    public IMemoryBudget? MemoryBudget { get; }

    /// <summary>Gets the number of currently open input and staging leases.</summary>
    /// <remarks>Private snapshot borrows retain storage independently of handle leases.</remarks>
    public long OpenLeaseCount => _openLeases;

    /// <summary>Registers a copied input or takes exclusive ownership of a transferred array.</summary>
    /// <remarks>
    /// Shared admission precedes copying or accepting ownership. If the array
    /// already has a reservation, use the reservation overload to hand it off
    /// without a second charge. Public reads return fresh owned bytes; their
    /// caller admits those copies, for example with the budget-aware read helper.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The ownership policy is undefined.</exception>
    public void AddInput(QpdfPath path, byte[] content, QpdfInputOwnership ownership)
    {
        ValidateInput(content);
        if (ownership != QpdfInputOwnership.Copy && ownership != QpdfInputOwnership.Transfer)
        { throw new ArgumentOutOfRangeException(nameof(ownership)); }
        IMemoryReservation? admission = ReserveStorage(content.Length, path);
        try
        {
            byte[] bytes = ownership == QpdfInputOwnership.Copy ? (byte[])content.Clone() : content;
            StoreInput(path, new StoredBytes(bytes, admission));
            admission = null;
        }
        finally { admission?.Dispose(); }
    }

    /// <summary>Registers an exclusively transferred array and its existing durable admission.</summary>
    /// <remarks>
    /// On success the host owns the reservation's disposal. Its original scope
    /// is unchanged and must outlive host storage and leases. The estimate must
    /// cover array length plus 256 bytes of owner/container allowance, adding
    /// two bytes for every path character beyond the first 64.
    /// Failure leaves the reservation owned by the caller.
    /// </remarks>
    public void AddInput(QpdfPath path, byte[] content, IMemoryReservation reservation)
    {
        ValidateInput(content);
        ArgumentNullException.ThrowIfNull(reservation);
        if (reservation.Bytes < StorageBytes(content.Length, path))
        { throw new ArgumentException("The reservation cannot cover input storage.", nameof(reservation)); }
        StoreInput(path, new StoredBytes(content, reservation));
    }

    private void ValidateInput(byte[] content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > _limits.MaxInputBytes)
        { throw new ArgumentException($"Input exceeds MaxInputBytes ({content.Length} > {_limits.MaxInputBytes}).", nameof(content)); }
    }

    private void StoreInput(QpdfPath path, StoredBytes storage)
    {
        _inputs.TryGetValue(path.Value, out StoredBytes? previous);
        string key = previous is null ? new string(path.Value.AsSpan()) : path.Value;
        _inputs[key] = storage;
        previous?.Retire();
    }

    /// <summary>Tries to export a fresh copy, charging the supplied host owner when present.</summary>
    /// <remarks>The copy remains charged until that caller owner closes, including after host disposal.</remarks>
    public bool TryGetOutput(QpdfPath path, out byte[] content) => Export(path, MemoryBudget, out content);

    /// <summary>Tries to export a fresh copy under a separate borrowed caller owner.</summary>
    /// <remarks>The caller keeps this owner alive until it drops the returned bytes and all aliases.</remarks>
    public bool TryGetOutput(QpdfPath path, IMemoryBudget memoryBudget, out byte[] content)
    {
        ArgumentNullException.ThrowIfNull(memoryBudget);
        return Export(path, memoryBudget, out content);
    }

    private bool Export(QpdfPath path, IMemoryBudget? budget, out byte[] content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_outputs.TryGetValue(path.Value, out StoredBytes? storage)) { content = []; return false; }
        byte[] bytes = storage.Bytes;
        IMemoryReservation? admission = budget is null ? null
            : MemoryAdmission.Reserve(budget, bytes.LongLength + GovernedBufferStream.ArrayOverheadBytes);
        try
        {
            content = (byte[])bytes.Clone();
            admission = null; // The caller scope now owns the copy's charge.
            return true;
        }
        finally { admission?.Dispose(); }
    }

    /// <summary>Opens a stable input lease under the local handle limit.</summary>
    public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_inputs.TryGetValue(path.Value, out StoredBytes? storage))
        { throw new QpdfHostException(path, QpdfHostFailureReason.NotFound); }
        CheckHandle();
        InMemoryInput input = new(this, path, storage);
        _openIdentities[path.Value] = _openIdentities.TryGetValue(path.Value, out int open) ? open + 1 : 1;
        storage.Acquire();
        _openLeases++;
        return ValueTask.FromResult<IQpdfInput>(input);
    }

    /// <summary>Creates an output transaction under the local handle limit.</summary>
    public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        CheckHandle();
        IQpdfOutputTransaction transaction = new InMemoryTransaction(this, path);
        _openLeases++;
        return ValueTask.FromResult(transaction);
    }

    private void CheckHandle()
    {
        if (_openLeases >= _limits.MaxOpenHandles)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxOpenHandles), _openLeases + 1L, _limits.MaxOpenHandles); }
    }

    private void ReleaseLease(string? identity)
    {
        _openLeases--;
        if (identity is not null && _openIdentities.TryGetValue(identity, out int open))
        {
            if (open == 1) { _openIdentities.Remove(identity); }
            else { _openIdentities[identity] = open - 1; }
        }
    }

    private static long StorageBytes(int capacity, QpdfPath path)
        => capacity + StorageOverheadBytes + 2L * Math.Max(0, path.Value.Length - 64);
    private IMemoryReservation? ReserveStorage(int capacity, QpdfPath path)
        => MemoryBudget is null ? null : MemoryAdmission.Reserve(MemoryBudget, StorageBytes(capacity, path));

    private static void Copy(ReadOnlySpan<byte> source, Span<byte> target, CancellationToken token)
    {
        for (int offset = 0; offset < source.Length;)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(8192, source.Length - offset);
            source.Slice(offset, count).CopyTo(target.Slice(offset, count));
            offset += count;
        }
    }

    /// <summary>Drops retained host storage; exported copies keep their caller-owned charges.</summary>
    /// <exception cref="InvalidOperationException">Leases or private snapshot borrowers remain active.</exception>
    public void Dispose()
    {
        if (_disposed) { return; }
        if (_openLeases != 0L || _snapshotBorrows != 0L)
        { throw new InvalidOperationException("Finish host leases and snapshot borrowers before disposing their storage."); }
        _disposed = true;
        foreach (StoredBytes storage in _inputs.Values) { storage.Retire(); }
        foreach (StoredBytes storage in _outputs.Values) { storage.Retire(); }
        _inputs.Clear(); _outputs.Clear(); _openIdentities.Clear();
    }
}
