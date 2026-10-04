using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class PredictorMemoryTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(12)]
    public void TightMemoryRejectsBeforeAllocatingPredictorOutput(int predictor)
    {
        const int Columns = 256 * 1024;
        byte[] input = new byte[Columns + (predictor == 2 ? 0 : 1)];
        PredictorParameters parameters = new(predictor, Columns, 1, 8);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => PredictorStages.Apply(input, parameters, FlateFilter.FilterName, governor));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 65536L, $"Rejected predictor allocated {allocated} bytes.");
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
    }

    [Fact]
    public void EmptyPngWithHugeRowsNeedsNoRowBuffer()
    {
        PredictorParameters parameters = new(12, 2 * 1024 * 1024, 1, 8);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] output = PredictorStages.Apply([], parameters, FlateFilter.FilterName, governor);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Empty(output);
        Assert.True(allocated < 65536L, $"Empty predictor allocated {allocated} bytes.");
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(12)]
    public void WorkAdmissionPrecedesOutputAllocation(int predictor)
    {
        byte[] input = new byte[100000 + (predictor == 2 ? 0 : 1)];
        PredictorParameters parameters = new(predictor, 100000, 1, 8);
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(
            () => PredictorStages.Apply(input, parameters, FlateFilter.FilterName, governor));
        Assert.Equal(nameof(QpdfLimits.MaxWork), error.LimitName);
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(12)]
    public void CanceledPredictorDoesNotAllocate(int predictor)
    {
        byte[] input = new byte[100000 + (predictor == 2 ? 0 : 1)];
        PredictorParameters parameters = new(predictor, 100000, 1, 8);
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PredictorStages.Apply(input, parameters, FlateFilter.FilterName, governor));
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(12)]
    public void FlatePredictorReleasesItsInflatedIntermediate(int predictor)
    {
        byte[] predicted = predictor == 2 ? [7, 2, 3] : [0, 7, 9, 12];
        byte[] compressed = FlateFilter.Encode(predicted, null, QpdfLimits.Default,
            new ResourceGovernor(QpdfLimits.Default, CancellationToken.None));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] result = FlateFilter.Decode(compressed, new PredictorParameters(predictor, 3, 1, 8), QpdfLimits.Default, governor);
        Assert.Equal(new byte[] { 7, 9, 12 }, result);
        Assert.Equal(result.Length + GovernedBufferStream.ArrayOverheadBytes, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(result);
    }

    [Fact]
    public void InvalidPngRowReleasesOutputAndInflatedIntermediate()
    {
        byte[] compressed = FlateFilter.Encode([7, 1, 2, 3], null, QpdfLimits.Default,
            new ResourceGovernor(QpdfLimits.Default, CancellationToken.None));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Throws<QpdfFilterException>(() => FlateFilter.Decode(compressed,
            new PredictorParameters(12, 3, 1, 8), QpdfLimits.Default, governor));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void PackedTiffSamplesKeepEachRowsBytePadding()
    {
        byte[] predicted = [0xA1, 0x11, 0x10, 0x51, 0x11, 0x10];
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] result = PredictorStages.Apply(predicted, new PredictorParameters(2, 5, 1, 4), FlateFilter.FilterName, governor);
        Assert.Equal(new byte[] { 0xAB, 0xCD, 0xE0, 0x56, 0x78, 0x90 }, result);
    }

    [Fact]
    public void SixteenBitTiffSamplesResetAtEachRow()
    {
        byte[] predicted = [0x12, 0x00, 0x11, 0x00, 0x45, 0x00, 0x22, 0x00];
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] result = PredictorStages.Apply(predicted, new PredictorParameters(2, 2, 1, 16), FlateFilter.FilterName, governor);
        Assert.Equal(new byte[] { 0x12, 0x00, 0x23, 0x00, 0x45, 0x00, 0x67, 0x00 }, result);
    }
}
