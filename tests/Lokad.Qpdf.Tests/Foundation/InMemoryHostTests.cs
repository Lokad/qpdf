using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class InMemoryHostTests
{
    private static byte[] SampleBytes()
    {
        return [1, 2, 3, 4, 5, 6, 7, 8];
    }

    [Fact]
    public async Task OpenAndReadReturnsRegisteredBytes()
    {
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("inputs/doc.pdf");
        host.AddInput(path, SampleBytes(), QpdfInputOwnership.Copy);
        await using (IQpdfInput input = await host.OpenInputAsync(path, CancellationToken.None))
        {
            Assert.Equal(8L, input.Length);
            Assert.Equal(path, input.Path);
            ReadOnlyMemory<byte> chunk = await input.ReadAsync(2L, 3, CancellationToken.None);
            Assert.Equal([3, 4, 5], chunk.ToArray());
        }

        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CopiedInputsAreIsolatedFromCallerMutations()
    {
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("inputs/doc.pdf");
        byte[] caller = SampleBytes();
        host.AddInput(path, caller, QpdfInputOwnership.Copy);
        caller[0] = 99;
        await using IQpdfInput input = await host.OpenInputAsync(path, CancellationToken.None);
        ReadOnlyMemory<byte> chunk = await input.ReadAsync(0L, 1, CancellationToken.None);
        Assert.Equal([1], chunk.ToArray());
    }

    [Fact]
    public async Task ReadsReturnFreshCopies()
    {
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("inputs/doc.pdf");
        host.AddInput(path, SampleBytes(), QpdfInputOwnership.Copy);
        await using IQpdfInput input = await host.OpenInputAsync(path, CancellationToken.None);
        byte[] first = (await input.ReadAsync(0L, 8, CancellationToken.None)).ToArray();
        first[0] = 99;
        byte[] second = (await input.ReadAsync(0L, 8, CancellationToken.None)).ToArray();
        Assert.Equal(1, second[0]);
    }

    [Fact]
    public async Task EmptyReadAtEndOfInput()
    {
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("inputs/doc.pdf");
        host.AddInput(path, SampleBytes(), QpdfInputOwnership.Copy);
        await using IQpdfInput input = await host.OpenInputAsync(path, CancellationToken.None);
        ReadOnlyMemory<byte> chunk = await input.ReadAsync(8L, 0, CancellationToken.None);
        Assert.Equal(0, chunk.Length);
    }

    [Fact]
    public async Task OutOfRangeReadsAreRejected()
    {
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("inputs/doc.pdf");
        host.AddInput(path, SampleBytes(), QpdfInputOwnership.Copy);
        await using IQpdfInput input = await host.OpenInputAsync(path, CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => input.ReadAsync(9L, 1, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => input.ReadAsync(-1L, 1, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => input.ReadAsync(0L, -1, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task MissingInputFailsExplicitly()
    {
        InMemoryQpdfHost host = new();
        QpdfHostException ex = await Assert.ThrowsAsync<QpdfHostException>(
            () => host.OpenInputAsync(QpdfPath.From("inputs/missing.pdf"), CancellationToken.None).AsTask());
        Assert.Equal(QpdfHostFailureReason.NotFound, ex.Reason);
    }

    [Fact]
    public async Task ReadExactlyLoopsShortReads()
    {
        FakeHost host = new();
        QpdfPath path = QpdfPath.From("inputs/doc.pdf");
        host.Add(path, SampleBytes());
        await using IQpdfInput input = await host.OpenInputAsync(path, CancellationToken.None);
        byte[] all = await QpdfInputReader.ReadExactlyAsync(input, 0L, 8, CancellationToken.None);
        Assert.Equal(SampleBytes(), all);
    }

    [Fact]
    public async Task ReadExactlyRejectsNonProgressingHosts()
    {
        byte[] snapshot = SampleBytes();
        NonProgressingHost host = new(QpdfPath.From("inputs/doc.pdf"), snapshot);
        await using IQpdfInput input = await host.OpenInputAsync(QpdfPath.From("inputs/doc.pdf"), CancellationToken.None);
        QpdfHostException ex = await Assert.ThrowsAsync<QpdfHostException>(
            () => QpdfInputReader.ReadExactlyAsync(input, 0L, 8, CancellationToken.None).AsTask());
        Assert.Equal(QpdfHostFailureReason.NonProgressingRead, ex.Reason);
    }

    [Fact]
    public async Task ReadExactlyRejectsOverlongRanges()
    {
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("inputs/doc.pdf");
        host.AddInput(path, SampleBytes(), QpdfInputOwnership.Copy);
        await using IQpdfInput input = await host.OpenInputAsync(path, CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => QpdfInputReader.ReadExactlyAsync(input, 0L, 9, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task CancelledOperationsObserveCancellation()
    {
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("inputs/doc.pdf");
        host.AddInput(path, SampleBytes(), QpdfInputOwnership.Copy);
        using CancellationTokenSource source = new();
        source.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => host.OpenInputAsync(path, source.Token).AsTask());
        await using IQpdfInput input = await host.OpenInputAsync(path, CancellationToken.None);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => input.ReadAsync(0L, 1, source.Token).AsTask());
    }

    [Fact]
    public void OversizedInputsAreRejectedUpfront()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxInputBytes = 4L };
        InMemoryQpdfHost host = new(limits);
        Assert.Throws<ArgumentException>(
            () => host.AddInput(QpdfPath.From("inputs/big.pdf"), SampleBytes(), QpdfInputOwnership.Copy));
    }

    [Fact]
    public async Task HandleBudgetIsEnforced()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxOpenHandles = 1L };
        InMemoryQpdfHost host = new(limits);
        QpdfPath path = QpdfPath.From("inputs/doc.pdf");
        host.AddInput(path, SampleBytes(), QpdfInputOwnership.Copy);
        await using IQpdfInput input = await host.OpenInputAsync(path, CancellationToken.None);
        await Assert.ThrowsAsync<QpdfLimitException>(
            () => host.OpenInputAsync(path, CancellationToken.None).AsTask());
    }

    [Fact]
    public void InvalidPathsAreRejected()
    {
        #pragma warning disable CS8625 // Intentional null literal for a negative test.
        Assert.Throws<ArgumentNullException>(() => QpdfPath.From(null));
#pragma warning restore CS8625
        Assert.Throws<ArgumentException>(() => QpdfPath.From(string.Empty));
        Assert.Throws<ArgumentException>(() => QpdfPath.From("a\0b"));
    }
}



