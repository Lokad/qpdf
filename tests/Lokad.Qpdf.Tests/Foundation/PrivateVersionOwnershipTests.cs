using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class PrivateVersionOwnershipTests
{
    private sealed class FailureHost(InMemoryQpdfHost inner, IMemoryBudget budget, bool cancel, CancellationTokenSource cancellation)
        : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            return ValueTask.FromException<IQpdfOutputTransaction>(new QpdfHostException(path, QpdfHostFailureReason.WriteFailed));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedSelectionRefundsPrivateSourceVersions(bool secondSource, bool cancel)
    {
        RecordingMemoryBudget root = new(16777216);
        using (IMemoryScope owner = root.CreateScope())
        {
            using InMemoryQpdfHost inner = new(QpdfLimits.Default, null, owner);
            byte[] source = CheckTests.Assemble(CheckTests.SinglePage("<< /Length 3 >>", "q Q"u8.ToArray()), 1);
            inner.AddInput(QpdfPath.From("first"), source, QpdfInputOwnership.Copy);
            byte[] other = (byte[])source.Clone(); other[7] = (byte)'7';
            inner.AddInput(QpdfPath.From("second"), other, QpdfInputOwnership.Transfer);
            await using (IQpdfOutputTransaction prior = await inner.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
            { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
            long retained = root.ReservedBytes;
            using CancellationTokenSource cancellation = new();
            FailureHost host = new(inner, owner, cancel, cancellation);
            PdfPageSource[] sources = secondSource
                ? [new(QpdfPath.From("first"), new(1)), new(QpdfPath.From("second"), new(1))]
                : [new(QpdfPath.From("first"), new(1))];
            async Task Run() => await Qpdf.SelectPagesAsync(host, sources, QpdfPath.From("output"), cancellation.Token);
            if (cancel) { await Assert.ThrowsAnyAsync<OperationCanceledException>(Run); }
            else { await Assert.ThrowsAsync<QpdfHostException>(Run); }
            Assert.Equal(retained, root.ReservedBytes); Assert.Equal(0L, inner.OpenLeaseCount);
            Assert.True(inner.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task TypedCheckVersionRemainsFundedAndUsableAfterHostDisposal()
    {
        RecordingMemoryBudget root = new(16777216);
        using (IMemoryScope owner = root.CreateScope())
        {
            QpdfCheckResult result;
            using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
            {
                host.AddInput(QpdfPath.From("input"), CheckTests.Assemble(CheckTests.SinglePage("<< /Length 3 >>", "q Q"u8.ToArray()), 1), QpdfInputOwnership.Transfer);
                result = await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None);
                Assert.True(result.Passed); Assert.Equal(0L, host.OpenLeaseCount);
            }
            Assert.True(root.ReservedBytes >= 32L); Assert.Equal("1.4", result.Version.ToString());
            Assert.Equal(result.Version, result.Version with { });
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}
