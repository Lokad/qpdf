using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Content;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class IndirectRotationFailureTests
{
    private sealed class Host(string fault, IMemoryBudget? budget, CancellationTokenSource? cancel)
        : IQpdfHost, IQpdfMemoryHost, IDisposable
    {
        internal readonly InMemoryQpdfHost Inner = new();
        internal int Created;
        public IMemoryBudget? MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => Inner.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            Created++;
            return new Output(await Inner.CreateOutputAsync(path, token), fault, cancel);
        }
        public void Dispose() => Inner.Dispose();

        private sealed class Output(IQpdfOutputTransaction inner, string fault, CancellationTokenSource? cancel)
            : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            {
                if (fault == "write") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
                if (fault == "cancel") { cancel?.Cancel(); token.ThrowIfCancellationRequested(); }
                return inner.WriteAsync(bytes, token);
            }
            public ValueTask CommitAsync(CancellationToken token)
                => fault == "commit" ? throw new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed) : inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private static string[] Arguments(string operation)
        => operation == "add"
            ? ["input", "--pages", ".", "1", "--", "--rotate=+90", "output"]
            : ["input", operation == "rotation" ? "--flatten-rotation" : "--flatten-annotations=all", "output"];

    private static async Task Seed(Host host)
    {
        host.Inner.AddInput(IndirectRotationTests.Input, IndirectRotationTests.Fixture("5 0 R", "90", true), QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction prior = await host.Inner.CreateOutputAsync(IndirectRotationTests.Output, CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
        await prior.CommitAsync(CancellationToken.None);
    }

    private static void AssertUnpublished(Host host, QpdfExecutionResult result)
    {
        Assert.Equal(0, host.Inner.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.Inner.TryGetOutput(IndirectRotationTests.Output, out byte[] output));
        Assert.Equal("prior"u8.ToArray(), output);
    }

    public static IEnumerable<object[]> OutputFailures()
    {
        foreach (string operation in new[] { "add", "rotation", "annotations" })
        foreach (string failure in new[] { "write", "commit", "cancel" })
        { yield return [operation, failure]; }
    }

    [Theory]
    [MemberData(nameof(OutputFailures))]
    public async Task FailuresAfterIndirectTransformationPreservePriorOutput(string operation, string failure)
    {
        using CancellationTokenSource cancel = new();
        using Host host = new(failure, null, cancel);
        await Seed(host);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(Arguments(operation)), cancel.Token);
        Assert.Equal(1, host.Created);
        AssertUnpublished(host, result);
        if (failure == "cancel") { Assert.Equal(QpdfExecutionStatus.Canceled, result.Status); }
        else
        {
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.HostOperationFailed);
        }
    }

    [Theory]
    [InlineData("add")]
    [InlineData("rotation")]
    [InlineData("annotations")]
    public async Task EverySharedAdmissionRefusalReleasesPrivateStorage(string operation)
    {
        int growths;
        RecordingMemoryBudget baseline = new(16777216L);
        using (IMemoryScope caller = baseline.CreateScope())
        using (Host host = new(string.Empty, caller, null))
        {
            await Seed(host);
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(Arguments(operation)), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            growths = baseline.GrowthCalls;
            Assert.True(growths > 0);
            Assert.Equal(0, host.Inner.OpenLeaseCount);
        }
        Assert.Equal(0L, baseline.ReservedBytes);
        for (int refused = 1; refused <= growths; refused++)
        {
            RecordingMemoryBudget budget = new(16777216L) { RefuseOnGrowth = refused };
            using (IMemoryScope caller = budget.CreateScope())
            using (Host host = new(string.Empty, caller, null))
            {
                await Seed(host);
                QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(Arguments(operation)), CancellationToken.None);
                Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
                Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded);
                Assert.Equal(refused, budget.GrowthCalls);
                AssertUnpublished(host, result);
            }
            Assert.Equal(0L, budget.ReservedBytes);
        }
    }
}
