using System.Text;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class GovernedLeaseTests
{
    private static byte[] Fixture(string name)
    {
        return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    }

    [Fact]
    public async Task HandleBudgetEnforcedPerJob()
    {
        CountingHost host = new(Fixture("minimal-1page.pdf"));
        QpdfLimits limits = QpdfLimits.Default with { MaxOpenHandles = 1L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["a", "--overlay", "b", "--", "--", "out"]), new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.True(host.PeakLeases <= 1);
    }

    [Fact]
    public async Task ShortReadsChargePerCall()
    {
        CountingHost host = new(Fixture("minimal-1page.pdf")) { ShortReads = true };
        QpdfLimits limits = QpdfLimits.Default with { MaxHostOperations = 2L };
        await Assert.ThrowsAsync<QpdfLimitException>(() => QpdfDocument.OpenAsync(host, QpdfPath.From("a"), limits, CancellationToken.None).AsTask());
        Assert.Equal(1, host.ReadCalls);
    }

    [Fact]
    public async Task ResponseBudgetFailureReleasesLease()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("args"), Encoding.UTF8.GetBytes("--version"), QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxHostOperations = 1L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["@args", "--version"]), new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ResponseMemoryAdmissionBeforeFetch()
    {
        CountingHost host = new(Encoding.UTF8.GetBytes(new string((char)97, 16384)));
        host.AddInput("args", Encoding.UTF8.GetBytes(new string((char)97, 16384)));
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["@args", "--version"]), new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(0, host.ReadCalls);
    }

    [Fact]
    public async Task DuplicateLeasesKeepConflictUntilLastDispose()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("a"), new byte[] { 1, 2, 3 }, QpdfInputOwnership.Transfer);
        await using IQpdfInput first = await host.OpenInputAsync(QpdfPath.From("a"), CancellationToken.None);
        await using IQpdfInput second = await host.OpenInputAsync(QpdfPath.From("a"), CancellationToken.None);
        await first.DisposeAsync();
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(QpdfPath.From("a"), CancellationToken.None);
        await transaction.WriteAsync(new byte[] { 4 }, CancellationToken.None);
        await Assert.ThrowsAsync<QpdfHostException>(() => transaction.CommitAsync(CancellationToken.None).AsTask());
        await transaction.DiscardAsync(CancellationToken.None);
        await second.DisposeAsync();
        await using IQpdfOutputTransaction retry = await host.CreateOutputAsync(QpdfPath.From("a"), CancellationToken.None);
        await retry.WriteAsync(new byte[] { 4 }, CancellationToken.None);
        await retry.CommitAsync(CancellationToken.None);
    }

    private sealed class CountingHost : IQpdfHost
    {
        private readonly InMemoryQpdfHost _inner = new();
        public int ReadCalls { get; private set; }
        public long PeakLeases { get; private set; }
        public bool ShortReads { get; init; }

        internal CountingHost(byte[] bytes)
        {
            _inner.AddInput(QpdfPath.From("a"), bytes, QpdfInputOwnership.Copy);
            _inner.AddInput(QpdfPath.From("b"), bytes, QpdfInputOwnership.Copy);
        }

        internal void AddInput(string name, byte[] bytes)
        {
            _inner.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        }

        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            IQpdfInput input = await _inner.OpenInputAsync(path, cancellationToken);
            PeakLeases = Math.Max(PeakLeases, _inner.OpenLeaseCount);
            return new CountingInput(this, input);
        }

        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            IQpdfOutputTransaction transaction = await _inner.CreateOutputAsync(path, cancellationToken);
            PeakLeases = Math.Max(PeakLeases, _inner.OpenLeaseCount);
            return transaction;
        }

        private sealed class CountingInput : IQpdfInput
        {
            private readonly CountingHost _host;
            private readonly IQpdfInput _input;

            internal CountingInput(CountingHost host, IQpdfInput input)
            {
                _host = host;
                _input = input;
            }

            public QpdfPath Path => _input.Path;

            public string Identity => _input.Identity;

            public long Length => _input.Length;

            public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellationToken)
            {
                _host.ReadCalls++;
                return _input.ReadAsync(offset, _host.ShortReads ? Math.Min(1, count) : count, cancellationToken);
            }

            public ValueTask DisposeAsync()
            {
                return _input.DisposeAsync();
            }
        }
    }
}
