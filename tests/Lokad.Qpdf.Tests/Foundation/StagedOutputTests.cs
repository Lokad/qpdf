using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class StagedOutputTests
{
    [Fact]
    public async Task RejectedCountedStagingPreservesPriorOutputAndClosesTheTransaction()
    {
        InMemoryQpdfHost host = new(QpdfLimits.Default with { MaxOutputBytes = 32L });
        QpdfPath input = QpdfPath.From("input.pdf"), target = QpdfPath.From("output.pdf");
        host.AddInput(input, await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "minimal-2page.pdf")), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(target, CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["input.pdf", "--static-id", "output.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(target, out byte[] bytes));
        Assert.Equal("prior"u8.ToArray(), bytes);
    }

    [Fact]
    public async Task CommitPublishesStagedBytes()
    {
        InMemoryQpdfHost host = new();
        QpdfPath target = QpdfPath.From("outputs/doc.pdf");
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(target, CancellationToken.None);
        await transaction.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        await transaction.WriteAsync(new byte[] { 4, 5 }, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        bool found = host.TryGetOutput(target, out byte[] content);
        Assert.True(found);
        Assert.Equal([1, 2, 3, 4, 5], content);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8192)]
    public async Task OutputsAreCopiesAndSurviveSettledTransactionCleanup(int size)
    {
        InMemoryQpdfHost host = new();
        QpdfPath target = QpdfPath.From("outputs/doc.pdf");
        await using (IQpdfOutputTransaction transaction = await host.CreateOutputAsync(target, CancellationToken.None))
        {
            byte[] chunk = new byte[size];
            Array.Fill(chunk, (byte)9);
            await transaction.WriteAsync(chunk, CancellationToken.None);
            chunk[0] = 1;
            await transaction.CommitAsync(CancellationToken.None);
            await transaction.DiscardAsync(CancellationToken.None);
            await transaction.DisposeAsync();
        }

        bool found = host.TryGetOutput(target, out byte[] content);
        Assert.True(found);
        Assert.Equal(size, content.Length);
        Assert.All(content, value => Assert.Equal(9, value));
        content[0] = 1;
        bool again = host.TryGetOutput(target, out byte[] second);
        Assert.True(again);
        Assert.Equal(size, second.Length);
        Assert.All(second, value => Assert.Equal(9, value));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task FailedCommitLeavesExistingTargetUntouched()
    {
        InMemoryQpdfHost host = new();
        QpdfPath target = QpdfPath.From("outputs/doc.pdf");
        host.AddInput(target, [1, 2, 3], QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction seeded = await host.CreateOutputAsync(target, CancellationToken.None))
        {
            await seeded.WriteAsync(new byte[] { 7, 7 }, CancellationToken.None);
            await seeded.CommitAsync(CancellationToken.None);
        }

        await using IQpdfInput guard = await host.OpenInputAsync(target, CancellationToken.None);
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(target, CancellationToken.None);
        await transaction.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        QpdfHostException ex = await Assert.ThrowsAsync<QpdfHostException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        Assert.Equal(QpdfHostFailureReason.Conflict, ex.Reason);
        bool found = host.TryGetOutput(target, out byte[] content);
        Assert.True(found);
        Assert.Equal([7, 7], content);
    }

    [Fact]
    public async Task FaultyCommitFailsExplicitly()
    {
        FailingCommitHost host = new();
        QpdfPath target = QpdfPath.From("outputs/doc.pdf");
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(target, CancellationToken.None);
        await transaction.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        QpdfHostException ex = await Assert.ThrowsAsync<QpdfHostException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        Assert.Equal(QpdfHostFailureReason.CommitFailed, ex.Reason);
    }

    [Fact]
    public async Task WriteFailurePropagatesExplicitly()
    {
        FailingWriteHost host = new();
        QpdfPath target = QpdfPath.From("outputs/doc.pdf");
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(target, CancellationToken.None);
        QpdfHostException ex = await Assert.ThrowsAsync<QpdfHostException>(
            () => transaction.WriteAsync(new byte[] { 1 }, CancellationToken.None).AsTask());
        Assert.Equal(QpdfHostFailureReason.WriteFailed, ex.Reason);
    }

    [Fact]
    public async Task OutputAliasingAnOpenInputIsRejected()
    {
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("same/doc.pdf");
        host.AddInput(path, [1, 2, 3], QpdfInputOwnership.Copy);
        await using IQpdfInput input = await host.OpenInputAsync(path, CancellationToken.None);
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(path, CancellationToken.None);
        await transaction.WriteAsync(new byte[] { 9 }, CancellationToken.None);
        QpdfHostException ex = await Assert.ThrowsAsync<QpdfHostException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
        Assert.Equal(QpdfHostFailureReason.Conflict, ex.Reason);
        bool found = host.TryGetOutput(path, out byte[] _);
        Assert.False(found);
    }

    [Fact]
    public async Task OverBudgetWritesAreRejectedBeforeGrowing()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 4L };
        InMemoryQpdfHost host = new(limits);
        QpdfPath target = QpdfPath.From("outputs/doc.pdf");
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(target, CancellationToken.None);
        await transaction.WriteAsync(new byte[] { 1, 2 }, CancellationToken.None);
        QpdfLimitException ex = await Assert.ThrowsAsync<QpdfLimitException>(
            () => transaction.WriteAsync(new byte[] { 3, 4, 5 }, CancellationToken.None).AsTask());
        Assert.Equal(nameof(QpdfLimits.MaxOutputBytes), ex.LimitName);
        await transaction.DiscardAsync(CancellationToken.None);
        bool found = host.TryGetOutput(target, out byte[] _);
        Assert.False(found);
    }

    [Fact]
    public async Task DisposeWithoutCommitDiscardsStaging()
    {
        InMemoryQpdfHost host = new();
        QpdfPath target = QpdfPath.From("outputs/doc.pdf");
        await using (IQpdfOutputTransaction transaction = await host.CreateOutputAsync(target, CancellationToken.None))
        {
            await transaction.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        }

        bool found = host.TryGetOutput(target, out byte[] _);
        Assert.False(found);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task DoubleCommitIsRejected()
    {
        InMemoryQpdfHost host = new();
        QpdfPath target = QpdfPath.From("outputs/doc.pdf");
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(target, CancellationToken.None);
        await transaction.WriteAsync(new byte[] { 1 }, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.CommitAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task CancellationDuringStagingAbandonsOutput()
    {
        InMemoryQpdfHost host = new();
        QpdfPath target = QpdfPath.From("outputs/doc.pdf");
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(target, CancellationToken.None);
        using CancellationTokenSource source = new();
        source.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => transaction.WriteAsync(new byte[] { 1 }, source.Token).AsTask());
        await transaction.DiscardAsync(CancellationToken.None);
        bool found = host.TryGetOutput(target, out byte[] _);
        Assert.False(found);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task UnsupportedOutputsFailExplicitly()
    {
        FakeHost host = new();
        QpdfHostException ex = await Assert.ThrowsAsync<QpdfHostException>(
            () => host.CreateOutputAsync(QpdfPath.From("outputs/doc.pdf"), CancellationToken.None).AsTask());
        Assert.Equal(QpdfHostFailureReason.Unsupported, ex.Reason);
    }

    [Fact]
    public async Task LeasesCloseOnEveryPath()
    {
        InMemoryQpdfHost host = new();
        QpdfPath input = QpdfPath.From("inputs/a.pdf");
        QpdfPath output = QpdfPath.From("outputs/b.pdf");
        host.AddInput(input, [1], QpdfInputOwnership.Copy);
        await using (IQpdfInput opened = await host.OpenInputAsync(input, CancellationToken.None))
        {
        }

        await using (IQpdfOutputTransaction transaction = await host.CreateOutputAsync(output, CancellationToken.None))
        {
            await transaction.WriteAsync(new byte[] { 2 }, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.Equal(0L, host.OpenLeaseCount);
    }
}

