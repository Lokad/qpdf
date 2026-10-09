namespace Lokad.Qpdf.Runtime;

/// <summary>
/// Per-job governed access to host leases.
/// Every input open and staged-output creation charges one host operation
/// and one open handle before touching the host, so invocation budgets bound
/// concurrent leases even when the host defaults are permissive. Reads through
/// a governed input charge one host operation per underlying host call, so
/// short reads cannot bypass the budget. Disposal releases the governor handle
/// exactly once alongside the host lease.
/// </summary>
internal static class GovernedHost
{
    internal static async ValueTask<IQpdfInput> OpenInputAsync(
        IQpdfHost host,
        QpdfPath path,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        governor.NoteHostOperation();
        governor.AcquireHandle();
        IQpdfInput inner;
        try
        {
            inner = await host.OpenInputAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            governor.ReleaseHandle();
            throw;
        }

        return new GovernedInput(inner, governor);
    }

    internal static async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(
        IQpdfHost host,
        QpdfPath path,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        governor.NoteHostOperation();
        governor.AcquireHandle();
        IQpdfOutputTransaction inner;
        try
        {
            inner = await host.CreateOutputAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            governor.ReleaseHandle();
            throw;
        }

        return new GovernedTransaction(inner, governor);
    }

    private sealed class GovernedInput : IQpdfInput
    {
        private readonly IQpdfInput _inner;
        private readonly ResourceGovernor _governor;
        private bool _disposed;

        internal GovernedInput(IQpdfInput inner, ResourceGovernor governor)
        {
            _inner = inner;
            _governor = governor;
        }

        public QpdfPath Path => _inner.Path;

        public string Identity => _inner.Identity;

        public long Length => _inner.Length;

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellationToken)
        {
            _governor.NoteHostOperation();
            return await _inner.ReadAsync(offset, count, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _governor.ReleaseHandle();
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class GovernedTransaction : IQpdfOutputTransaction
    {
        private readonly IQpdfOutputTransaction _inner;
        private readonly ResourceGovernor _governor;
        private bool _disposed;

        internal GovernedTransaction(IQpdfOutputTransaction inner, ResourceGovernor governor)
        {
            _inner = inner;
            _governor = governor;
        }

        public QpdfPath Path => _inner.Path;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            return _inner.WriteAsync(data, cancellationToken);
        }

        public ValueTask CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _governor.PrepareExecutionPresentation();
            return _inner.CommitAsync(cancellationToken);
        }

        public ValueTask DiscardAsync(CancellationToken cancellationToken)
        {
            return _inner.DiscardAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _governor.ReleaseHandle();
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
