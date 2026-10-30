using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;

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
            byte[] chunk = new byte[3];
            Assert.Equal(chunk.Length, await input.ReadAsync(2L, chunk, CancellationToken.None));
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
        byte[] chunk = new byte[1];
        Assert.Equal(chunk.Length, await input.ReadAsync(0L, chunk, CancellationToken.None));
        Assert.Equal([1], chunk.ToArray());
    }

    [Fact]
    public async Task ReadsFillCallerStorageWithoutExposingSnapshot()
    {
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("inputs/doc.pdf");
        byte[] original = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf"));
        host.AddInput(path, original, QpdfInputOwnership.Copy);
        await using (IQpdfInput input = await host.OpenInputAsync(path, CancellationToken.None))
        {
            byte[] first = new byte[original.Length];
            Assert.Equal(first.Length, await input.ReadAsync(0L, first, CancellationToken.None));
            first.AsSpan().Fill(99);
            byte[] second = new byte[original.Length];
            await QpdfInputReader.ReadExactlyAsync(input, 0L, second, CancellationToken.None);
            Assert.Equal(original, second);
        }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["inputs/doc.pdf", "--static-id", "outputs/doc.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/doc.pdf"), out byte[] output));
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(2, independent.NumberOfPages);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task EmptyReadAtEndOfInput()
    {
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("inputs/doc.pdf");
        host.AddInput(path, SampleBytes(), QpdfInputOwnership.Copy);
        await using IQpdfInput input = await host.OpenInputAsync(path, CancellationToken.None);
        byte[] chunk = new byte[0];
        Assert.Equal(chunk.Length, await input.ReadAsync(8L, chunk, CancellationToken.None));
        Assert.Empty(chunk);
    }

    [Fact]
    public async Task OutOfRangeReadsAreRejected()
    {
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("inputs/doc.pdf");
        host.AddInput(path, SampleBytes(), QpdfInputOwnership.Copy);
        await using IQpdfInput input = await host.OpenInputAsync(path, CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => input.ReadAsync(9L, new byte[1], CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => input.ReadAsync(-1L, new byte[1], CancellationToken.None).AsTask());
        Assert.Equal(1, await input.ReadAsync(7L, new byte[4], CancellationToken.None));
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
        byte[] all = new byte[8];
        await QpdfInputReader.ReadExactlyAsync(input, 0L, all, CancellationToken.None);
        Assert.Equal(SampleBytes(), all);
    }

    [Theory]
    [InlineData(0L, 8)]
    [InlineData(2L, 3)]
    public async Task ExactReadResultsRemainIsolatedAndUsableAfterLeaseClosure(long offset, int count)
    {
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("inputs/doc.pdf");
        byte[] caller = SampleBytes();
        byte[] expected = caller.AsSpan((int)offset, count).ToArray();
        host.AddInput(path, caller, QpdfInputOwnership.Copy);
        byte[] surviving;
        await using (IQpdfInput input = await host.OpenInputAsync(path, CancellationToken.None))
        {
            byte[] first = new byte[count];
            await QpdfInputReader.ReadExactlyAsync(input, offset, first, CancellationToken.None);
            Assert.Equal(expected, first);
            Array.Fill(caller, (byte)99);
            Array.Fill(first, (byte)99);
            surviving = new byte[count];
            await QpdfInputReader.ReadExactlyAsync(input, offset, surviving, CancellationToken.None);
        }
        Assert.Equal(expected, surviving);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ReadExactlyRejectsNonProgressingHosts()
    {
        byte[] snapshot = SampleBytes();
        NonProgressingHost host = new(QpdfPath.From("inputs/doc.pdf"), snapshot);
        await using IQpdfInput input = await host.OpenInputAsync(QpdfPath.From("inputs/doc.pdf"), CancellationToken.None);
        QpdfHostException ex = await Assert.ThrowsAsync<QpdfHostException>(
            () => QpdfInputReader.ReadExactlyAsync(input, 0L, new byte[8], CancellationToken.None).AsTask());
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
            () => QpdfInputReader.ReadExactlyAsync(input, 0L, new byte[9], CancellationToken.None).AsTask());
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
            () => input.ReadAsync(0L, new byte[1], source.Token).AsTask());
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

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(6, true)]
    [InlineData(6, false)]
    public async Task StagedSlicesSurviveCallerMutationAndRejectedWrites(int capacityHint, bool appendTail)
    {
        InMemoryQpdfHost host = new(QpdfLimits.Default with { MaxOutputBytes = 6 });
        QpdfPath path = QpdfPath.From("outputs/doc.pdf");
        await using (IQpdfOutputTransaction transaction = await host.CreateOutputAsync(path, CancellationToken.None))
        {
            if (capacityHint != 0)
            { Assert.IsAssignableFrom<IOutputCapacityHint>(transaction).EnsureCapacity(capacityHint, CancellationToken.None); }
            byte[] caller = [99, 1, 2, 3, 4, 99];
            await transaction.WriteAsync(caller.AsMemory(1, 4), CancellationToken.None);
            Array.Fill(caller, (byte)42);
            using CancellationTokenSource canceled = new();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.WriteAsync(caller.AsMemory(0, 1), canceled.Token).AsTask());
            await Assert.ThrowsAsync<QpdfLimitException>(() => transaction.WriteAsync(caller.AsMemory(0, 3), CancellationToken.None).AsTask());
            if (appendTail) { await transaction.WriteAsync(new byte[] { 5, 6 }, CancellationToken.None); }
            await transaction.CommitAsync(CancellationToken.None);
        }
        Assert.Equal(0, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(path, out byte[] output));
        Assert.Equal(appendTail ? new byte[] { 1, 2, 3, 4, 5, 6 } : new byte[] { 1, 2, 3, 4 }, output);
    }
}



