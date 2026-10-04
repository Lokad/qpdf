namespace Lokad.Qpdf.Hosting;

/// <summary>
/// No-filesystem host backed by caller-supplied memory. Suitable for the
/// bounded in-memory preview use case and for tests. All inputs, outputs,
/// and leases live in memory; no filesystem, process, or network access.
/// </summary>
public sealed class InMemoryQpdfHost : IQpdfHost
{
    private readonly QpdfLimits _limits;
    private readonly Dictionary<string, byte[]> _inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _outputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _openIdentities = new(StringComparer.Ordinal);
    private long _openLeases;

    /// <summary>Creates a host with default budgets.</summary>
    public InMemoryQpdfHost()
        : this(QpdfLimits.Default)
    {
    }

    /// <summary>Creates a host with explicit budgets.</summary>
    /// <exception cref="ArgumentNullException">The limits are null.</exception>
    public InMemoryQpdfHost(QpdfLimits limits)
    {
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        _limits = limits;
    }

    /// <summary>Gets the number of currently open input and staging leases.</summary>
    public long OpenLeaseCount => _openLeases;

    /// <summary>
    /// Registers an input. With <see cref="QpdfInputOwnership.Copy" /> the
    /// bytes are cloned; with <see cref="QpdfInputOwnership.Transfer" /> the
    /// array is referenced and the caller must not mutate it afterwards.
    /// Reads always return fresh copies.
    /// </summary>
    /// <exception cref="ArgumentNullException">Content is null.</exception>
    /// <exception cref="ArgumentException">Content exceeds the input budget.</exception>
    public void AddInput(QpdfPath path, byte[] content, QpdfInputOwnership ownership)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        if (content.Length > _limits.MaxInputBytes)
        {
            throw new ArgumentException($"Input exceeds MaxInputBytes ({content.Length} > {_limits.MaxInputBytes}).", nameof(content));
        }

        byte[] stored = ownership == QpdfInputOwnership.Copy ? (byte[])content.Clone() : content;
        _inputs[path.Value] = stored;
    }

    /// <summary>
    /// Tries to retrieve a copy of a committed output.
    /// </summary>
    public bool TryGetOutput(QpdfPath path, out byte[] content)
    {
        if (_outputs.TryGetValue(path.Value, out byte[]? stored) && stored is not null)
        {
            content = (byte[])stored.Clone();
            return true;
        }

        content = [];
        return false;
    }

    /// <summary>Opens the staged input, charging an open handle against the host budget.</summary>
    public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_inputs.TryGetValue(path.Value, out byte[]? stored) || stored is null)
        {
            throw new QpdfHostException(path, QpdfHostFailureReason.NotFound);
        }

        if (_openLeases >= _limits.MaxOpenHandles)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxOpenHandles), _openLeases + 1L, _limits.MaxOpenHandles);
        }

        _openLeases += 1L;
        _openIdentities[path.Value] = _openIdentities.TryGetValue(path.Value, out int open) ? open + 1 : 1;
        IQpdfInput input = new InMemoryInput(this, path, stored);
        return ValueTask.FromResult(input);
    }

    /// <summary>Creates a staged output transaction, charging an open handle against the host budget.</summary>
    public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_openLeases >= _limits.MaxOpenHandles)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxOpenHandles), _openLeases + 1L, _limits.MaxOpenHandles);
        }

        _openLeases += 1L;
        IQpdfOutputTransaction transaction = new InMemoryTransaction(this, path);
        return ValueTask.FromResult(transaction);
    }

    private void ReleaseLease(string? identity)
    {
        _openLeases = Math.Max(0L, _openLeases - 1L);
        if (identity is not null)
        {
            if (_openIdentities.TryGetValue(identity, out int open))
            {
                if (open <= 1)
                {
                    _openIdentities.Remove(identity);
                }
                else
                {
                    _openIdentities[identity] = open - 1;
                }
            }
        }
    }

    private sealed class InMemoryInput : IQpdfInput
    {
        private readonly InMemoryQpdfHost _host;
        private readonly byte[] _snapshot;
        private bool _disposed;

        internal InMemoryInput(InMemoryQpdfHost host, QpdfPath path, byte[] snapshot)
        {
            _host = host;
            Path = path;
            Identity = path.Value;
            _snapshot = snapshot;
        }

        public QpdfPath Path { get; }

        public string Identity { get; }

        public long Length => _snapshot.Length;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(IQpdfInput));
            }

            if (offset < 0 || count < 0 || count > _snapshot.Length - offset)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Read range exceeds the stable input length.");
            }

            if (count == 0)
            {
                return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
            }

            byte[] copy = new byte[count];
            Buffer.BlockCopy(_snapshot, (int)offset, copy, 0, count);
            return ValueTask.FromResult((ReadOnlyMemory<byte>)copy);
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _host.ReleaseLease(Identity);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryTransaction : IQpdfOutputTransaction
    {
        private readonly InMemoryQpdfHost _host;
        private readonly MemoryStream _staging = new();
        private bool _settled;
        private bool _disposed;

        internal InMemoryTransaction(InMemoryQpdfHost host, QpdfPath path)
        {
            _host = host;
            Path = path;
        }

        public QpdfPath Path { get; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(IQpdfOutputTransaction));
            }

            if (_settled)
            {
                throw new InvalidOperationException("The transaction is already committed or discarded.");
            }

            checked
            {
                long next = _staging.Length + data.Length;
                if (next > _host._limits.MaxOutputBytes)
                {
                    throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), next, _host._limits.MaxOutputBytes);
                }
            }

            byte[] copy = data.ToArray();
            _staging.Write(copy, 0, copy.Length);
            return ValueTask.CompletedTask;
        }

        public ValueTask CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(IQpdfOutputTransaction));
            }

            if (_settled)
            {
                throw new InvalidOperationException("The transaction is already committed or discarded.");
            }

            if (_host._openIdentities.ContainsKey(Path.Value))
            {
                throw new QpdfHostException(Path, QpdfHostFailureReason.Conflict);
            }

            _host._outputs[Path.Value] = _staging.ToArray();
            _settled = true;
            _host.ReleaseLease(null);
            return ValueTask.CompletedTask;
        }

        public ValueTask DiscardAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(IQpdfOutputTransaction));
            }

            if (!_settled)
            {
                _settled = true;
                _host.ReleaseLease(null);
            }

            _staging.SetLength(0L);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                if (!_settled)
                {
                    _settled = true;
                    _host.ReleaseLease(null);
                }

                _staging.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }
}