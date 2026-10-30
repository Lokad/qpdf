namespace Lokad.Qpdf.Tests.Foundation;

/// <summary>Input over a fixed snapshot used by fault-injection hosts.</summary>
internal sealed class FakeInput : IQpdfInput
{
    private readonly byte[] _snapshot;
    private bool _disposed;

    internal FakeInput(QpdfPath path, byte[] snapshot)
    {
        Path = path;
        Identity = path.Value;
        _snapshot = snapshot;
    }

    public QpdfPath Path { get; }

    public string Identity { get; }

    public long Length => _snapshot.Length;

    public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (offset < 0 || offset > Length) { throw new ArgumentOutOfRangeException(nameof(offset)); }
        int count = Math.Min(destination.Length, _snapshot.Length - (int)offset);
        _snapshot.AsMemory((int)offset, count).CopyTo(destination);
        return new(count);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Input that returns empty reads before end of input.</summary>
internal sealed class NonProgressingInput : IQpdfInput
{
    private readonly byte[] _snapshot;

    internal NonProgressingInput(QpdfPath path, byte[] snapshot)
    {
        Path = path;
        Identity = path.Value;
        _snapshot = snapshot;
    }

    public QpdfPath Path { get; }

    public string Identity { get; }

    public long Length => _snapshot.Length;

    public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(0);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

/// <summary>Input-only host serving fixed snapshots.</summary>
internal sealed class FakeHost : IQpdfHost
{
    private readonly Dictionary<string, byte[]> _inputs = new(StringComparer.Ordinal);

    internal FakeHost()
    {
    }

    internal void Add(QpdfPath path, byte[] content)
    {
        _inputs[path.Value] = (byte[])content.Clone();
    }

    public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_inputs.TryGetValue(path.Value, out byte[]? stored) && stored is not null)
            return ValueTask.FromResult<IQpdfInput>(new FakeInput(path, stored));

        throw new QpdfHostException(path, QpdfHostFailureReason.NotFound);
    }

    public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new QpdfHostException(path, QpdfHostFailureReason.Unsupported);
    }
}

/// <summary>Host whose inputs never progress, for contract tests.</summary>
internal sealed class NonProgressingHost : IQpdfHost
{
    private readonly byte[] _snapshot;
    private readonly QpdfPath _path;

    internal NonProgressingHost(QpdfPath path, byte[] snapshot)
    {
        _path = path;
        _snapshot = (byte[])snapshot.Clone();
    }

    public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IQpdfInput>(new NonProgressingInput(_path, _snapshot));
    }

    public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new QpdfHostException(path, QpdfHostFailureReason.Unsupported);
    }
}

/// <summary>Transaction whose commit always fails, leaving targets untouched.</summary>
internal sealed class FailingCommitTransaction : IQpdfOutputTransaction
{
    private readonly QpdfPath _path;

    internal FailingCommitTransaction(QpdfPath path)
    {
        _path = path;
    }

    public QpdfPath Path => _path;

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new QpdfHostException(_path, QpdfHostFailureReason.CommitFailed);
    }

    public ValueTask DiscardAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

/// <summary>Host whose output commits always fail.</summary>
internal sealed class FailingCommitHost : IQpdfHost
{
    public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new QpdfHostException(path, QpdfHostFailureReason.NotFound);
    }

    public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IQpdfOutputTransaction>(new FailingCommitTransaction(path));
    }
}

/// <summary>Transaction whose writes always fail.</summary>
internal sealed class FailingWriteTransaction : IQpdfOutputTransaction
{
    private readonly QpdfPath _path;

    internal FailingWriteTransaction(QpdfPath path)
    {
        _path = path;
    }

    public QpdfPath Path => _path;

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new QpdfHostException(_path, QpdfHostFailureReason.WriteFailed);
    }

    public ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new QpdfHostException(_path, QpdfHostFailureReason.CommitFailed);
    }

    public ValueTask DiscardAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

/// <summary>Host whose output writes always fail.</summary>
internal sealed class FailingWriteHost : IQpdfHost
{
    public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new QpdfHostException(path, QpdfHostFailureReason.NotFound);
    }

    public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IQpdfOutputTransaction>(new FailingWriteTransaction(path));
    }
}

