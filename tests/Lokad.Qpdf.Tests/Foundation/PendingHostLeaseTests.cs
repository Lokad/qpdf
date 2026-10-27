using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class PendingHostLeaseTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach ((bool output, bool prepaid) in new[] { (false, false), (true, false), (true, true) })
        foreach (string outcome in new[] { "success", "fault", "cancel", "oom" })
        { yield return [output, prepaid, outcome]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PendingCreationRetainsOwnershipUntilCompletionAndRefundsExactlyOnce(bool output, bool prepaid, string outcome)
    {
        RecordingMemoryBudget root = new(4096L);
        using IMemoryScope owner = root.CreateScope();
        Assert.True(root.TryReserve(256L, out IMemoryReservation? sibling));
        using (sibling)
        using (InMemoryQpdfHost storage = new())
        {
            QpdfPath path = QpdfPath.From("lease");
            storage.AddInput(path, [1, 2, 3], QpdfInputOwnership.Copy);
            PendingHost host = new(owner);
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            using PublicationReservation? publication = prepaid ? PublicationReservation.Create(governor, 1L) : null;
            publication?.Begin();
            long retained = governor.Snapshot().LiveMemoryBytes;
            async Task<IAsyncDisposable> Open()
                => output ? await GovernedHost.CreateOutputAsync(host, path, governor, CancellationToken.None)
                    : await GovernedHost.OpenInputAsync(host, path, governor, CancellationToken.None);
            Task<IAsyncDisposable> opening = Open();
            Assert.False(opening.IsCompleted);
            Assert.Equal(1L, governor.Snapshot().OpenHandles);
            Assert.Equal(1L, governor.Snapshot().HostOperations);
            Assert.Equal(0L, storage.OpenLeaseCount);

            Exception failure = outcome == "oom" ? new OutOfMemoryException("host creation")
                : new QpdfHostException(path, QpdfHostFailureReason.Unsupported);
            if (outcome == "success")
            {
                if (output) { host.Output.SetResult(await storage.CreateOutputAsync(path, CancellationToken.None)); }
                else { host.Input.SetResult(await storage.OpenInputAsync(path, CancellationToken.None)); }
                IAsyncDisposable lease = await opening.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(1L, storage.OpenLeaseCount);
                await lease.DisposeAsync();
                await lease.DisposeAsync();
            }
            else
            {
                if (outcome == "cancel")
                {
                    if (output) { host.Output.SetCanceled(); } else { host.Input.SetCanceled(); }
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
                }
                else
                {
                    if (output) { host.Output.SetException(failure); } else { host.Input.SetException(failure); }
                    Exception observed = await Assert.ThrowsAnyAsync<Exception>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
                    Assert.Same(failure, observed);
                }
            }
            Assert.Equal(0L, storage.OpenLeaseCount);
            Assert.Equal(0L, governor.Snapshot().OpenHandles);
            Assert.Equal(retained, governor.Snapshot().LiveMemoryBytes);
            publication?.Dispose();
            governor.CompleteStorage();
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(256L, root.ReservedBytes);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    private sealed class PendingHost(IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        internal readonly TaskCompletionSource<IQpdfInput> Input = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<IQpdfOutputTransaction> Output = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => new(Input.Task);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => new(Output.Task);
    }
}
