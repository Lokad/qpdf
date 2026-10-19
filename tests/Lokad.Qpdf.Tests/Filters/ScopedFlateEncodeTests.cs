using System.Diagnostics;
using System.IO.Compression;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class ScopedFlateEncodeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(9)]
    public void ScopedOutputMatchesLegacyBytesAndIndependentDecoding(int? level)
    {
        byte[] input = new byte[65536]; new Random(107).NextBytes(input);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(97L);
        byte[] output = FlateFilter.Encode(input, level, QpdfLimits.Default, governor, memory, CancellationToken.None);
        byte[] legacy = FlateFilter.Encode(input, level, QpdfLimits.Default,
            new ResourceGovernor(QpdfLimits.Default, CancellationToken.None));
        Assert.Equal(legacy, output);
        Assert.Equal(97L + 32L + output.Length, memory.ReservedBytes);
        using MemoryStream encoded = new(output);
        using ZLibStream decoder = new(encoded, CompressionMode.Decompress);
        using MemoryStream decoded = new(); decoder.CopyTo(decoded);
        Assert.Equal(input, decoded.ToArray());
        memory.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void EmptyFrameBelongsToTheCallerScope()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        byte[] encoded = FlateFilter.Encode([], null, QpdfLimits.Default, governor, memory, CancellationToken.None);
        Assert.Equal(new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 }, encoded);
        Assert.Equal(40L, memory.ReservedBytes);
        memory.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LimitFailureReleasesCodecScratchAndPreservesPriorReceipt(bool outputLimit)
    {
        QpdfLimits limits = outputLimit ? QpdfLimits.Default with { MaxOutputBytes = 3L }
            : QpdfLimits.Default with { MaxMemoryBytes = FlateFilter.DeflateWorkingSetBytes + 416L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(97L);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
            FlateFilter.Encode(new byte[65536], null, limits, governor, memory, CancellationToken.None));
        Assert.Equal(outputLimit ? nameof(QpdfLimits.MaxOutputBytes) : nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(97L, memory.ReservedBytes);
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void AForeignScopeRejectsBeforeCodecWork()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        ResourceGovernor other = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(other);
        Assert.Throws<InvalidOperationException>(() => FlateFilter.Encode([1], null, QpdfLimits.Default, governor, memory, CancellationToken.None));
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
        Assert.Equal(0L, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterObservedCodecInputWorkStopsBothTokens(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(97L);
        byte[] source = new byte[4 * 1024 * 1024]; new Random(109).NextBytes(source);
        // Give the codec its own test thread and keep the observer runnable so
        // cancellation follows actual encoded-buffer growth.
        Task worker = Task.Factory.StartNew(() =>
        {
            while (true)
            {
                using ParserMemoryScope attempt = ParserMemoryScope.Create(governor);
                _ = FlateFilter.Encode(source, null, QpdfLimits.Default, governor, attempt,
                    governorToken ? CancellationToken.None : cancellation.Token);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            long started = Stopwatch.GetTimestamp();
            while (governor.Snapshot().PeakMemoryBytes < FlateFilter.DeflateWorkingSetBytes + 65536L && !worker.IsCompleted
                && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10)) { Thread.SpinWait(16); }
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(97L, memory.ReservedBytes);
            Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
        }
        finally
        {
            cancellation.Cancel();
            try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
    }
}
