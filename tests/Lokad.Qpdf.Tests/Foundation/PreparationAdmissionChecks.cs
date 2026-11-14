using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Foundation;

internal static class PreparationAdmissionChecks
{
    private static readonly byte[] Prior = [17, 23, 41];

    internal static async Task<InMemoryQpdfHost> StorageAsync(byte[] input, QpdfPath target)
    {
        InMemoryQpdfHost storage = new();
        try
        {
            storage.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
            await using IQpdfOutputTransaction prior = await storage.CreateOutputAsync(target, CancellationToken.None);
            await prior.WriteAsync(Prior, CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
            return storage;
        }
        catch { storage.Dispose(); throw; }
    }

    internal static async Task VerifyAsync(Func<Task<InMemoryQpdfHost>> prepare,
        Func<IQpdfHost, CancellationToken, ValueTask<QpdfExecutionResult>> execute, QpdfPath target,
        int maximumGrowths, IReadOnlyList<string> phases)
    {
        int growths;
        using (InMemoryQpdfHost storage = await prepare())
        {
            RecordingMemoryBudget root = new(64L << 20);
            using (IMemoryScope owner = root.CreateScope())
            {
                QpdfExecutionResult result = await execute(new MemoryBudgetHost(storage, owner), CancellationToken.None);
                Assert.Equal(QpdfExecutionStatus.Completed, result.Status); growths = root.GrowthCalls;
                Assert.Equal(0L, storage.OpenLeaseCount);
            }
            Assert.Equal(0L, root.ReservedBytes);
        }
        Assert.InRange(growths, 1, maximumGrowths);
        for (int growth = 1; growth <= growths; growth++)
        {
            using InMemoryQpdfHost storage = await prepare();
            RecordingMemoryBudget root = new(64L << 20) { RefuseOnGrowth = growth };
            using (IMemoryScope owner = root.CreateScope())
            {
                QpdfExecutionResult result = await execute(new MemoryBudgetHost(storage, owner), CancellationToken.None);
                Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
                Assert.True(storage.TryGetOutput(target, out byte[] bytes)); Assert.Equal(Prior, bytes);
                Assert.Equal(0L, storage.OpenLeaseCount);
            }
            Assert.Equal(0L, root.ReservedBytes);
        }
        foreach (string phase in phases)
        {
            using InMemoryQpdfHost storage = await prepare();
            RecordingMemoryBudget root = new(64L << 20);
            using (IMemoryScope owner = root.CreateScope())
            using (CancellationTokenSource cancellation = new())
            {
                QpdfExecutionResult result = await execute(new FaultHost(storage, owner, phase, target, cancellation), cancellation.Token);
                Assert.Equal(phase == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
                Assert.Equal(0L, storage.OpenLeaseCount); Assert.True(storage.TryGetOutput(target, out byte[] bytes));
                if (phase == "close") { Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8)); }
                else { Assert.Equal(Prior, bytes); }
            }
            Assert.Equal(0L, root.ReservedBytes);
        }
    }

    private sealed class FaultHost(InMemoryQpdfHost storage, IMemoryBudget budget, string fault,
        QpdfPath target, CancellationTokenSource cancellation) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        private void Fail(string phase)
        {
            if (fault == "cancel" && phase == "write") { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            if (fault == phase) { throw new QpdfHostException(target, QpdfHostFailureReason.WriteFailed); }
        }
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Fail("create"); return new Transaction(this, await storage.CreateOutputAsync(path, token)); }
        private sealed class Transaction(FaultHost host, IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) { host.Fail("write"); return inner.WriteAsync(bytes, token); }
            public ValueTask CommitAsync(CancellationToken token) { host.Fail("commit"); return inner.CommitAsync(token); }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); host.Fail("close"); }
        }
    }
}
