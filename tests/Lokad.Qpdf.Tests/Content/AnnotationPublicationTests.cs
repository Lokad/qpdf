using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AnnotationPublicationTests
{
    private static readonly QpdfPath Output = QpdfPath.From("output");

    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget, string fault, CancellationTokenSource cancellation) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        private void Fail(string phase)
        {
            if (fault == "cancel" && phase == "write") { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            if (fault == phase) { throw new QpdfHostException(Output, QpdfHostFailureReason.WriteFailed); }
        }
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Fail("create"); return new Transaction(this, await storage.CreateOutputAsync(path, token)); }
        private sealed class Transaction(Host host, IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            { host.Fail("write"); return inner.WriteAsync(bytes, token); }
            public ValueTask CommitAsync(CancellationToken token)
            { host.Fail("commit"); return inner.CommitAsync(token); }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); host.Fail("close"); }
        }
    }

    private static async Task<InMemoryQpdfHost> Storage()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), FlattenResourceStorageTests.Fixture(64, false), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("overlay"), FlattenResourceStorageTests.Fixture(4, false), QpdfInputOwnership.Transfer);
        await using IQpdfOutputTransaction initial = await host.CreateOutputAsync(Output, CancellationToken.None);
        await initial.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await initial.CommitAsync(CancellationToken.None);
        return host;
    }

    private static ValueTask<QpdfExecutionResult> Run(Host host, bool overlay, CancellationToken token)
        => Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(overlay
            ? ["input", "--overlay", "overlay", "--", "output"]
            : ["input", "--flatten-annotations=all", "--", "output"]), token);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EverySharedGrowthCanRefuseWithoutPublishing(bool overlay)
    {
        int growths;
        using (InMemoryQpdfHost storage = await Storage())
        {
            RecordingMemoryBudget root = new(64L * 1024 * 1024);
            using (IMemoryScope owner = root.CreateScope())
            using (CancellationTokenSource cancellation = new())
            {
                QpdfExecutionResult result = await Run(new(storage, owner, "none", cancellation), overlay, cancellation.Token);
                Assert.Equal(QpdfExecutionStatus.Completed, result.Status); growths = root.GrowthCalls;
                Assert.Equal(0L, storage.OpenLeaseCount);
            }
            Assert.Equal(0L, root.ReservedBytes);
        }
        Assert.InRange(growths, 1, 64);
        for (int growth = 1; growth <= growths; growth++)
        {
            using InMemoryQpdfHost storage = await Storage();
            RecordingMemoryBudget root = new(64L * 1024 * 1024) { RefuseOnGrowth = growth };
            QpdfExecutionResult result;
            using (IMemoryScope owner = root.CreateScope())
            using (CancellationTokenSource cancellation = new())
            {
                result = await Run(new(storage, owner, "none", cancellation), overlay, cancellation.Token);
                Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
                Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
                Assert.True(storage.TryGetOutput(Output, out byte[] bytes)); Assert.Equal(new byte[] { 17, 23, 41 }, bytes);
                Assert.Equal(0L, storage.OpenLeaseCount);
            }
            Assert.Equal(0L, root.ReservedBytes); Assert.NotEmpty(result.Diagnostics);
        }
    }

    [Theory]
    [InlineData("create", false)]
    [InlineData("write", false)]
    [InlineData("commit", false)]
    [InlineData("cancel", false)]
    [InlineData("close", false)]
    [InlineData("write", true)]
    [InlineData("commit", true)]
    [InlineData("cancel", true)]
    [InlineData("close", true)]
    public async Task OutputFaultsRefundOwnersAndRespectCommit(string phase, bool overlay)
    {
        using InMemoryQpdfHost storage = await Storage();
        RecordingMemoryBudget root = new(64L * 1024 * 1024);
        using (IMemoryScope owner = root.CreateScope())
        using (CancellationTokenSource cancellation = new())
        {
            QpdfExecutionResult result = await Run(new(storage, owner, phase, cancellation), overlay, cancellation.Token);
            Assert.Equal(phase == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.True(storage.TryGetOutput(Output, out byte[] bytes));
            if (phase == "close") { Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8)); }
            else { Assert.Equal(new byte[] { 17, 23, 41 }, bytes); }
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}
