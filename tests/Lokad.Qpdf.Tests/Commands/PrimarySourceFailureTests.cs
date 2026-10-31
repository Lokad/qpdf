using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class PrimarySourceFailureTests
{
    private sealed class Host(IMemoryBudget? budget, string fault, CancellationTokenSource? cancel)
        : IQpdfHost, IQpdfMemoryHost, IDisposable
    {
        internal readonly InMemoryQpdfHost Inner = new();
        internal int PrimaryReads;
        public IMemoryBudget? MemoryBudget => budget;
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            IQpdfInput input = await Inner.OpenInputAsync(path, token);
            return path.Value == "primary" ? new Input(input, this, fault, cancel) : input;
        }
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Output(await Inner.CreateOutputAsync(path, token), fault);
        public void Dispose() => Inner.Dispose();

        private sealed class Input(IQpdfInput inner, Host host, string fault, CancellationTokenSource? cancel) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            {
                int count = await inner.ReadAsync(offset, destination, token);
                host.PrimaryReads++;
                if (fault == "cancel") { cancel?.Cancel(); }
                if (fault == "read") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); }
                return count;
            }
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                if (fault == "close") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); }
            }
        }

        private sealed class Output(IQpdfOutputTransaction inner, string fault) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
                => fault == "write" ? throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed) : inner.WriteAsync(bytes, token);
            public ValueTask CommitAsync(CancellationToken token)
                => fault == "commit" ? throw new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed) : inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private static void AssertUnpublished(Host host, QpdfExecutionResult result)
    {
        Assert.Equal(0, host.Inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        foreach (string path in new[] { "primary", "secondary", "output" })
        {
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From(path), out byte[] bytes));
            Assert.Equal(Encoding.ASCII.GetBytes("prior-" + path), bytes);
        }
    }

    public static IEnumerable<object[]> Failures()
    {
        foreach (string route in new[] { "argv", "job" })
        foreach (bool replace in new[] { false, true })
        foreach (string fault in new[] { "cancel", "read", "close", "write", "commit" })
        { yield return [route, replace, fault]; }
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task ExtraPrimaryAndPublicationFailuresPreserveEveryTarget(string route, bool replace, string fault)
    {
        RecordingMemoryBudget budget = new(16777216L);
        using (IMemoryScope caller = budget.CreateScope())
        using (CancellationTokenSource cancel = new())
        using (Host host = new(caller, fault, cancel))
        {
            PrimarySourceTests.Register(host.Inner, "valid"); await PrimarySourceTests.SeedOutputs(host.Inner);
            QpdfExecutionResult result = await PrimarySourceTests.Run(host, host.Inner, route, "1", replace, false, "output", cancel.Token);
            Assert.Equal(fault == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            Assert.True(host.PrimaryReads > 0); AssertUnpublished(host, result);
            if (fault != "cancel") { Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.HostOperationFailed); }
        }
        Assert.Equal(0L, budget.ReservedBytes);
    }

    [Theory]
    [InlineData("argv", false)]
    [InlineData("argv", true)]
    [InlineData("job", false)]
    [InlineData("job", true)]
    public async Task EverySharedRefusalReleasesTheUnselectedPrimaryGraph(string route, bool replace)
    {
        int growths;
        RecordingMemoryBudget baseline = new(16777216L);
        using (IMemoryScope caller = baseline.CreateScope())
        using (Host host = new(caller, string.Empty, null))
        {
            PrimarySourceTests.Register(host.Inner, "valid"); await PrimarySourceTests.SeedOutputs(host.Inner);
            QpdfExecutionResult result = await PrimarySourceTests.Run(host, host.Inner, route, "1", replace, false, "output", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.True(host.PrimaryReads > 0);
            Assert.Equal(0, host.Inner.OpenLeaseCount); growths = baseline.GrowthCalls;
        }
        Assert.Equal(0L, baseline.ReservedBytes); Assert.True(growths > 0);
        for (int refused = 1; refused <= growths; refused++)
        {
            RecordingMemoryBudget budget = new(16777216L) { RefuseOnGrowth = refused };
            using (IMemoryScope caller = budget.CreateScope())
            using (Host host = new(caller, string.Empty, null))
            {
                PrimarySourceTests.Register(host.Inner, "valid"); await PrimarySourceTests.SeedOutputs(host.Inner);
                QpdfExecutionResult result = await PrimarySourceTests.Run(host, host.Inner, route, "1", replace, false, "output", CancellationToken.None);
                Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
                Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded);
                Assert.Equal(refused, budget.GrowthCalls); AssertUnpublished(host, result);
            }
            Assert.Equal(0L, budget.ReservedBytes);
        }
    }
}
