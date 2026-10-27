namespace Lokad.Qpdf.Runtime;

/// <summary>Optional capacity hint for staging whose complete storage was already admitted.</summary>
internal interface IOutputCapacityHint
{
    void EnsureCapacity(long length, CancellationToken cancellationToken);
}

/// <summary>Copies borrowed bytes into host storage before returning.</summary>
internal interface ISynchronousOutputTransaction
{
    void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken);
}

/// <summary>An immutable host snapshot borrowed only by private documents.</summary>
internal interface IImmutableInputSnapshot
{
    bool MemoryAdmitted { get; }
    IDisposable RetainStorage();
    ReadOnlyMemory<byte> ReadSnapshot(CancellationToken cancellationToken);
}

/// <summary>
/// Admits host lease wrappers before allocation and counts each open, read and
/// write attempt. Publication batches lend one prepaid output wrapper through
/// sequential transactions. Closing a lease drops its host references and
/// releases the handle and storage once, including host close failures.
/// Wrappers exist before host acquisition; completed host calls return directly
/// without async completion metadata allocation after the raw lease is returned.
/// </summary>
internal static class GovernedHost
{
    internal const long InputWrapperBytes = 32L;
    internal const long OutputWrapperBytes = 40L;

    internal static bool SupportsSynchronousWrite(IQpdfOutputTransaction transaction)
        => transaction is GovernedTransaction governed && governed.SupportsSynchronousWrite;

    internal static void WriteSynchronously(IQpdfOutputTransaction transaction, ReadOnlySpan<byte> data,
        CancellationToken token)
    {
        if (transaction is not GovernedTransaction governed)
        { throw new InvalidOperationException("Synchronous output requires a governed transaction."); }
        governed.WriteSynchronously(data, token);
    }

    internal static bool TryReadInputSnapshot(IQpdfInput input, CancellationToken token,
        out ReadOnlyMemory<byte> data, out bool memoryAdmitted, out IDisposable? storageBorrow)
    {
        if (input is GovernedInput governed) { return governed.TryReadSnapshot(token, out data, out memoryAdmitted, out storageBorrow); }
        data = default; memoryAdmitted = false; storageBorrow = null; return false;
    }

    internal static void PrepareOutputCapacity(IQpdfOutputTransaction transaction, long length,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        cancellationToken.ThrowIfCancellationRequested();
        if (transaction is IOutputCapacityHint hint) { hint.EnsureCapacity(length, cancellationToken); }
    }

    internal static ValueTask<IQpdfInput> OpenInputAsync(
        IQpdfHost host, QpdfPath path, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(governor);
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        governor.ReserveMemory(InputWrapperBytes);
        bool acquired = false;
        try
        {
            governor.AcquireHandle(); acquired = true;
            GovernedInput input = new(governor);
            governor.NoteHostOperation();
            ValueTask<IQpdfInput> pending = host.OpenInputAsync(path, cancellationToken);
            if (!pending.IsCompletedSuccessfully) { return AwaitInputAsync(pending, input, governor); }
            input.InnerLease = pending.Result;
            return ValueTask.FromResult<IQpdfInput>(input);
        }
        catch
        {
            if (acquired) { governor.ReleaseHandle(); }
            governor.ReleaseMemory(InputWrapperBytes);
            throw;
        }
    }

    private static async ValueTask<IQpdfInput> AwaitInputAsync(ValueTask<IQpdfInput> pending,
        GovernedInput input, ResourceGovernor governor)
    {
        try { input.InnerLease = await pending.ConfigureAwait(false); return input; }
        catch { governor.ReleaseHandle(); governor.ReleaseMemory(InputWrapperBytes); throw; }
    }

    internal static ValueTask<IQpdfOutputTransaction> CreateOutputAsync(
        IQpdfHost host, QpdfPath path, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(governor);
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        governor.PrepareExecutionResult();
        PublicationReservation? publication = governor.ReserveOutputWrapper();
        bool acquired = false;
        try
        {
            governor.AcquireHandle(); acquired = true;
            GovernedTransaction transaction = new(governor, publication);
            governor.NoteHostOperation();
            ValueTask<IQpdfOutputTransaction> pending = host.CreateOutputAsync(path, cancellationToken);
            if (!pending.IsCompletedSuccessfully) { return AwaitOutputAsync(pending, transaction, governor, publication); }
            transaction.InnerLease = pending.Result;
            return ValueTask.FromResult<IQpdfOutputTransaction>(transaction);
        }
        catch
        {
            if (acquired) { governor.ReleaseHandle(); }
            if (publication is null) { governor.ReleaseMemory(OutputWrapperBytes); }
            else { publication.ReleaseTransaction(); }
            throw;
        }
    }

    private static async ValueTask<IQpdfOutputTransaction> AwaitOutputAsync(ValueTask<IQpdfOutputTransaction> pending,
        GovernedTransaction transaction, ResourceGovernor governor, PublicationReservation? publication)
    {
        try { transaction.InnerLease = await pending.ConfigureAwait(false); return transaction; }
        catch
        {
            governor.ReleaseHandle();
            if (publication is null) { governor.ReleaseMemory(OutputWrapperBytes); }
            else { publication.ReleaseTransaction(); }
            throw;
        }
    }

    private sealed class GovernedInput : IQpdfInput
    {
        internal IQpdfInput? InnerLease;
        private ResourceGovernor? _governor;

        internal GovernedInput(ResourceGovernor governor) { _governor = governor; }

        private IQpdfInput Inner => InnerLease ?? throw new ObjectDisposedException(nameof(GovernedInput));
        public QpdfPath Path => Inner.Path;

        public string Identity => Inner.Identity;
        public long Length => Inner.Length;

        internal bool TryReadSnapshot(CancellationToken token, out ReadOnlyMemory<byte> data,
            out bool memoryAdmitted, out IDisposable? storageBorrow)
        {
            IQpdfInput inner = Inner;
            if (inner is not IImmutableInputSnapshot snapshot)
            { data = default; memoryAdmitted = false; storageBorrow = null; return false; }
            ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(GovernedInput));
            token.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.NoteHostOperation();
            data = snapshot.ReadSnapshot(token);
            memoryAdmitted = snapshot.MemoryAdmitted;
            if (data.Length != inner.Length)
            { throw new QpdfHostException(inner.Path, QpdfHostFailureReason.NonProgressingRead); }
            storageBorrow = memoryAdmitted ? snapshot.RetainStorage() : null;
            return true;
        }

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellationToken)
        {
            IQpdfInput inner = Inner;
            ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(GovernedInput));
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.NoteHostOperation();
            return inner.ReadAsync(offset, count, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            IQpdfInput? inner = InnerLease;
            ResourceGovernor? governor = _governor;
            if (inner is null || governor is null) { return; }
            InnerLease = null; _governor = null;
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            finally { governor.ReleaseHandle(); governor.ReleaseMemory(InputWrapperBytes); }
        }
    }

    private sealed class GovernedTransaction : IQpdfOutputTransaction, IOutputCapacityHint
    {
        internal IQpdfOutputTransaction? InnerLease;
        private ResourceGovernor? _governor;
        private PublicationReservation? _publication;

        internal GovernedTransaction(ResourceGovernor governor, PublicationReservation? publication)
        { _governor = governor; _publication = publication; }

        private IQpdfOutputTransaction Inner => InnerLease ?? throw new ObjectDisposedException(nameof(GovernedTransaction));
        private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(GovernedTransaction));
        public QpdfPath Path => Inner.Path;

        internal bool SupportsSynchronousWrite => InnerLease is ISynchronousOutputTransaction;

        internal void WriteSynchronously(ReadOnlySpan<byte> data, CancellationToken cancellationToken)
        {
            IQpdfOutputTransaction inner = PrepareWrite(cancellationToken);
            if (inner is not ISynchronousOutputTransaction synchronous)
            { throw new InvalidOperationException("The output transaction does not consume spans synchronously."); }
            synchronous.Write(data, cancellationToken);
        }

        void IOutputCapacityHint.EnsureCapacity(long length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Governor.CheckCancellation();
            if (Inner is IOutputCapacityHint hint) { hint.EnsureCapacity(length, cancellationToken); }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            IQpdfOutputTransaction inner = PrepareWrite(cancellationToken);
            return inner.WriteAsync(data, cancellationToken);
        }

        private IQpdfOutputTransaction PrepareWrite(CancellationToken cancellationToken)
        {
            IQpdfOutputTransaction inner = Inner;
            ResourceGovernor governor = Governor;
            cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.NoteHostOperation();
            return inner;
        }

        public ValueTask CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Governor.PrepareExecutionPresentation();
            return Inner.CommitAsync(cancellationToken);
        }

        public ValueTask DiscardAsync(CancellationToken cancellationToken) => Inner.DiscardAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            IQpdfOutputTransaction? inner = InnerLease;
            ResourceGovernor? governor = _governor;
            PublicationReservation? publication = _publication;
            if (inner is null || governor is null) { return; }
            InnerLease = null; _governor = null; _publication = null;
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                governor.ReleaseHandle();
                if (publication is null) { governor.ReleaseMemory(OutputWrapperBytes); }
                else { publication.ReleaseTransaction(); }
            }
        }
    }
}
