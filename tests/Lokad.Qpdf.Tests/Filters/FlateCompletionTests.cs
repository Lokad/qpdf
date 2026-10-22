using System.Diagnostics;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class FlateCompletionTests
{
    public static IEnumerable<object[]> IncompleteFrames()
    {
        // A complete non-final stored block and an empty fixed block missing
        // its end marker. Both retain the checksum of the produced bytes.
        foreach (string hex in new[] { "789C000300FCFF71205101E700E3", "789C0300000001" })
        foreach (int padding in new[] { 0, 6, 20000 })
        { yield return [hex, padding]; }
    }

    [Theory]
    [MemberData(nameof(IncompleteFrames))]
    public void MatchingChecksumAndWhitespaceCannotCompleteAnUnfinishedFrame(string hex, int padding)
    {
        byte[] input = [.. Convert.FromHexString(hex), .. Enumerable.Repeat((byte)' ', padding)];
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        long before = governor.Snapshot().LiveMemoryBytes;
        QpdfFilterException error = Assert.Throws<QpdfFilterException>(() => FlateFilter.Decode(input, null, QpdfLimits.Default, governor));
        Assert.Equal(QpdfFilterFailureReason.TruncatedInput, error.Reason);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().TotalDecodedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8192)]
    [InlineData(16384)]
    public void CompleteFramesFinishAtEmptyAndExactOutputBufferBoundaries(int length)
    {
        byte[] raw = new byte[length];
        for (int index = 0; index < raw.Length; index++) { raw[index] = (byte)(index % 29); }
        byte[] framed = FlateFilter.Encode(raw, null, QpdfLimits.Default, new ResourceGovernor(QpdfLimits.Default, CancellationToken.None));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(raw, FlateFilter.Decode(framed, null, QpdfLimits.Default, governor));
        Assert.Equal(raw.Length, governor.Snapshot().TotalDecodedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringInflationRetiresOnlyTheAttempt(bool governorToken)
    {
        byte[] raw = new byte[4 * 1024 * 1024]; new Random(117).NextBytes(raw);
        byte[] input = FlateFilter.Encode(raw, null, QpdfLimits.Default, new ResourceGovernor(QpdfLimits.Default, CancellationToken.None));
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using FilterPlan stages = FilterPlan.Create(governor, CancellationToken.None);
        stages.Allocate(1); stages.Set(0, new("FlateDecode", null, null));
        long prepared = governor.Snapshot().LiveMemoryBytes;
        Task worker = Task.Factory.StartNew(() =>
        {
            while (true)
            {
                using ParserMemoryScope attempt = ParserMemoryScope.Create(governor);
                _ = FilterChain.Decode(input, stages, QpdfLimits.Default, governor, attempt,
                    governorToken ? CancellationToken.None : cancellation.Token);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            long started = Stopwatch.GetTimestamp();
            while (governor.Snapshot().PeakMemoryBytes < FlateFilter.InflateWorkingSetBytes + 65536L && !worker.IsCompleted
                && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10)) { Thread.SpinWait(16); }
            Assert.True(governor.Snapshot().PeakMemoryBytes >= FlateFilter.InflateWorkingSetBytes + 65536L);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(prepared, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
        }
        finally
        {
            cancellation.Cancel();
            try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        }
    }
}
