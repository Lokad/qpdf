using System.Reflection;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class PublicationLeaseOwnershipTests
{
    [Fact]
    public void PlainCancellationDoesNotExportOrRetainAnEmptyFailureView()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        ExecutionPresentation? presentation = ExecutionPresentation.Create(governor, bag, false, false, false, false, false, CancellationToken.None);
        Assert.NotNull(presentation);
        ExecutionOutcome canceled = ExecutionOutcome.Canceled([]);
        Assert.Equal(canceled, presentation.Failure(canceled));
        presentation.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(canceled);
    }

    [Theory]
    [InlineData(false, 31L)]
    [InlineData(true, 39L)]
    public async Task WrapperMemoryExhaustionPrecedesTheHostCall(bool output, long ceiling)
    {
        LeaseHost host = new(false);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = ceiling }, CancellationToken.None);
        QpdfLimitException exception = output
            ? await Assert.ThrowsAsync<QpdfLimitException>(() => GovernedHost.CreateOutputAsync(host, QpdfPath.From("out"), governor, CancellationToken.None).AsTask())
            : await Assert.ThrowsAsync<QpdfLimitException>(() => GovernedHost.OpenInputAsync(host, QpdfPath.From("input"), governor, CancellationToken.None).AsTask());
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), exception.LimitName);
        Assert.Equal(ceiling + 1L, exception.Requested); Assert.Equal(0, host.Calls);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, governor.Snapshot().HostOperations);
        Assert.Equal(0L, governor.Snapshot().OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExactWrapperStorageStaysThroughCloseAndRetiresOnce(bool output, bool closeFault)
    {
        long bytes = output ? 40L : 32L;
        LeaseHost host = new(closeFault);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = bytes }, CancellationToken.None);
        IAsyncDisposable lease;
        if (output)
        {
            IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, QpdfPath.From("out"), governor, CancellationToken.None);
            await transaction.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None); lease = transaction;
        }
        else
        {
            IQpdfInput input = await GovernedHost.OpenInputAsync(host, QpdfPath.From("input"), governor, CancellationToken.None);
            Assert.Equal(new byte[] { 1, 2, 3 }, (await input.ReadAsync(0L, 3, CancellationToken.None)).ToArray()); lease = input;
        }
        Assert.Equal(bytes, governor.Snapshot().LiveMemoryBytes); Assert.Equal(2L, governor.Snapshot().HostOperations);
        host.BeforeClose = () => { Assert.Equal(bytes, governor.Snapshot().LiveMemoryBytes); Assert.Equal(1L, governor.Snapshot().OpenHandles); };
        if (closeFault) { await Assert.ThrowsAsync<QpdfHostException>(() => lease.DisposeAsync().AsTask()); }
        else { await lease.DisposeAsync(); }
        await lease.DisposeAsync();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(bytes, governor.Snapshot().PeakMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
        GC.KeepAlive(lease);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("host")]
    [InlineData("handles")]
    public void FiniteBatchAdmissionRejectsBeforeBeginning(string exhausted)
    {
        QpdfLimits limits = exhausted switch
        {
            "memory" => QpdfLimits.Default with { MaxMemoryBytes = 151L },
            "host" => QpdfLimits.Default with { MaxHostOperations = 3L },
            _ => QpdfLimits.Default with { MaxOpenHandles = 1L },
        };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        if (exhausted == "handles") { governor.AcquireHandle(); }
        QpdfLimitException exception = Assert.Throws<QpdfLimitException>(() => PublicationReservation.Create(governor, 4L));
        Assert.Equal(exhausted == "memory" ? nameof(QpdfLimits.MaxMemoryBytes) : exhausted == "host" ? nameof(QpdfLimits.MaxHostOperations) : nameof(QpdfLimits.MaxOpenHandles), exception.LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, governor.Snapshot().HostOperations);
        if (exhausted == "handles") { governor.ReleaseHandle(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APrepaidWrapperServesMultipleTransactionsAtAnExactMemoryBudget(bool closeFault)
    {
        LeaseHost host = new(closeFault);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 152L, MaxHostOperations = 6L, MaxOpenHandles = 1L }, CancellationToken.None);
        using (PublicationReservation publication = PublicationReservation.Create(governor, 6L))
        {
            Assert.Equal(152L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, governor.Snapshot().HostOperations);
            Assert.Throws<QpdfLimitException>(() => governor.NoteHostOperation());
            publication.Begin();
            for (int index = 0; index < 3; index++)
            {
                IQpdfOutputTransaction transaction = await GovernedHost.CreateOutputAsync(host, QpdfPath.From("out" + index), governor, CancellationToken.None);
                Assert.Throws<InvalidOperationException>(() => publication.Dispose());
await transaction.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
                await transaction.CommitAsync(CancellationToken.None);
                if (closeFault) { await Assert.ThrowsAsync<QpdfHostException>(() => transaction.DisposeAsync().AsTask()); }
                else { await transaction.DisposeAsync(); }
                Assert.Equal(152L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, governor.Snapshot().OpenHandles);
            }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(152L, governor.Snapshot().PeakMemoryBytes);
        Assert.Equal(6L, governor.Snapshot().HostOperations);         Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledHostCreationRetiresTheUnexposedWrapper(bool cancel)
    {
        LeaseHost host = new(false) { FailCreation = !cancel, CancelCreation = cancel };
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 40L }, CancellationToken.None);
        if (cancel) { await Assert.ThrowsAsync<OperationCanceledException>(() => GovernedHost.CreateOutputAsync(host, QpdfPath.From("out"), governor, CancellationToken.None).AsTask()); }
        else { await Assert.ThrowsAsync<QpdfHostException>(() => GovernedHost.CreateOutputAsync(host, QpdfPath.From("out"), governor, CancellationToken.None).AsTask()); }
        Assert.Equal(1L, governor.Snapshot().HostOperations); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    private sealed class LeaseHost(bool closeFault) : IQpdfHost
    {
        internal InMemoryQpdfHost Inner { get; } = Create();
        internal int Calls;
        internal Action? BeforeClose;
        internal bool FailCreation, CancelCreation;
        private static InMemoryQpdfHost Create()
        { InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), new byte[] { 1, 2, 3 }, QpdfInputOwnership.Copy); return host; }
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        { Calls++; return new Input(this, await Inner.OpenInputAsync(path, token)); }
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            Calls++;
            if (CancelCreation) { throw new OperationCanceledException(); }
            if (FailCreation) { throw new QpdfHostException(path, QpdfHostFailureReason.ReadFailed); }
            return new Output(this, await Inner.CreateOutputAsync(path, token));
        }
        private void Close(QpdfPath path)
        { if (closeFault) { throw new QpdfHostException(path, QpdfHostFailureReason.ReadFailed); } }
        private sealed class Input(LeaseHost host, IQpdfInput inner) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token) => inner.ReadAsync(offset, count, token);
            public async ValueTask DisposeAsync()
            { QpdfPath path = Path; host.BeforeClose?.Invoke(); await inner.DisposeAsync(); host.Close(path); }
        }
        private sealed class Output(LeaseHost host, IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) => inner.WriteAsync(bytes, token);
            public ValueTask CommitAsync(CancellationToken token) => inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public async ValueTask DisposeAsync()
            { QpdfPath path = Path; host.BeforeClose?.Invoke(); await inner.DisposeAsync(); host.Close(path); }
        }
    }
}
