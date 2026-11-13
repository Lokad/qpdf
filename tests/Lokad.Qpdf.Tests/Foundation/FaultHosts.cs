using System.Runtime.InteropServices;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

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

/// <summary>Records real calls/admission and suspends one selected host callback.</summary>
internal sealed class RecordingJobHost : IQpdfHost, IQpdfMemoryHost, IDisposable
{
    private readonly string? _boundary;
    private readonly string _outcome;
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resume;
    private bool _paused;

    internal RecordingJobHost(string? boundary, QpdfPath? path, string outcome, bool asyncResume)
        : this(new InMemoryQpdfHost(), boundary, path, outcome, asyncResume) { }

    internal RecordingJobHost(InMemoryQpdfHost inner, string? boundary, QpdfPath? path, string outcome, bool asyncResume)
    {
        Inner = inner; _boundary = boundary; PausePath = path; _outcome = outcome;
        _resume = new(asyncResume ? TaskCreationOptions.RunContinuationsAsynchronously : TaskCreationOptions.None);
    }

    internal static RecordingJobHost ForOutcome(string outcome, QpdfPath input, QpdfPath output)
    {
        string? boundary = outcome switch
        {
            "budget" => null,
            "publication-fault" or "commit-fault" => "commit",
            "read-fault" => "read",
            "close-fault" => "input-close",
            "create-fault" => "create",
            "write-fault" => "write",
            "output-close-fault" => "output-close",
            _ => "open",
        };
        QpdfPath path = boundary is "create" or "write" or "commit" or "output-close" ? output : input;
        string behavior = outcome is "success" or "cancel" or "budget" ? "success"
            : outcome == "host-cancel" ? "host-cancel" : "fault";
        return new(boundary, path, behavior, true);
    }

    internal InMemoryQpdfHost Inner { get; }
    internal RecordingMemoryBudget Budget { get; } = new(64L << 20);
    public IMemoryBudget MemoryBudget => Budget;
    internal Task Entered => _entered.Task;
    internal QpdfPath? PausePath { get; set; }
    internal List<QpdfPath> OpenedPaths { get; } = [];
    internal List<QpdfPath> CreatedPaths { get; } = [];
    internal HashSet<QpdfPath> Committed { get; } = [];
    internal List<WeakReference> ReadBuffers { get; } = [];
    internal bool ObserveReadBuffers { get; init; }
    internal long ReadBytes { get; private set; }
    internal long HostCalls { get; private set; }
    internal Memory<byte> PendingRead { get; private set; }
    internal ReadOnlyMemory<byte> PendingWrite { get; private set; }
    internal void Resume() => _resume.TrySetResult();

    private async ValueTask Pause(string phase, QpdfPath path, CancellationToken token)
    {
        if (_paused || phase != _boundary || PausePath is QpdfPath selected && path != selected) { return; }
        _paused = true; _entered.TrySetResult();
        await _resume.Task.WaitAsync(token);
        if (_outcome == "fault") { throw new QpdfHostException(path, QpdfHostFailureReason.ReadFailed); }
        if (_outcome == "host-cancel") { throw new OperationCanceledException(new CancellationToken(true)); }
    }

    public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
    {
        HostCalls++; OpenedPaths.Add(path);
        await Pause("open", path, token);
        return new Input(this, await Inner.OpenInputAsync(path, token), token);
    }

    public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
    {
        HostCalls++; CreatedPaths.Add(path);
        await Pause("create", path, token);
        return new Output(this, await Inner.CreateOutputAsync(path, token), token);
    }

    private sealed class Input(RecordingJobHost host, IQpdfInput inner, CancellationToken token) : IQpdfInput
    {
        public QpdfPath Path => inner.Path;
        public string Identity => inner.Identity;
        public long Length => inner.Length;
        public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellation)
        {
            host.HostCalls++; host.PendingRead = destination;
            if (host.ObserveReadBuffers && MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)destination, out ArraySegment<byte> segment)
                && segment.Array is byte[] array) { host.ReadBuffers.Add(new(array)); }
            try
            {
                int count = await inner.ReadAsync(offset, destination, cancellation);
                host.ReadBytes += count;
                await host.Pause("read", Path, cancellation);
                return count;
            }
            finally { host.PendingRead = default; }
        }
        public async ValueTask DisposeAsync()
        { try { await host.Pause("input-close", Path, token); } finally { await inner.DisposeAsync(); } }
    }

    private sealed class Output(RecordingJobHost host, IQpdfOutputTransaction inner, CancellationToken token) : IQpdfOutputTransaction
    {
        public QpdfPath Path => inner.Path;
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellation)
        {
            host.HostCalls++; host.PendingWrite = data;
            try { await host.Pause("write", Path, cancellation); await inner.WriteAsync(data, cancellation); }
            finally { host.PendingWrite = default; }
        }
        public async ValueTask CommitAsync(CancellationToken cancellation)
        { await host.Pause("commit", Path, cancellation); await inner.CommitAsync(cancellation); host.Committed.Add(Path); }
        public ValueTask DiscardAsync(CancellationToken cancellation) => inner.DiscardAsync(cancellation);
        public async ValueTask DisposeAsync()
        { try { await host.Pause("output-close", Path, token); } finally { await inner.DisposeAsync(); } }
    }

    public void Dispose() { Inner.Dispose(); Budget.Dispose(); }
}

internal sealed class CountingPublicationHost(InMemoryQpdfHost inner) : IQpdfHost
{
    internal InMemoryQpdfHost Inner { get; } = inner;
    internal int Commits { get; private set; }
    public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => Inner.OpenInputAsync(path, token);
    public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        => new Transaction(this, await Inner.CreateOutputAsync(path, token));
    private sealed class Transaction : IQpdfOutputTransaction
    {
        private readonly CountingPublicationHost _host;
        private readonly IQpdfOutputTransaction _inner;
        internal Transaction(CountingPublicationHost host, IQpdfOutputTransaction inner) { _host = host; _inner = inner; }
        public QpdfPath Path => _inner.Path;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token) => _inner.WriteAsync(data, token);
        public async ValueTask CommitAsync(CancellationToken token) { _host.Commits++; await _inner.CommitAsync(token); }
        public ValueTask DiscardAsync(CancellationToken token) => _inner.DiscardAsync(token);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
