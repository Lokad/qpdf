using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class FlateDecodedBudgetTests
{
    private static byte[] Compress(byte[] bytes)
    {
        return FlateFilter.Encode(bytes, null, QpdfLimits.Default,
            new ResourceGovernor(QpdfLimits.Default, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectAndChainChargeTheResultOnce(bool chain)
    {
        byte[] raw = new byte[1024];
        new Random(41).NextBytes(raw);
        byte[] input = Compress(raw);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = raw.Length };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        byte[] result = chain
            ? FilterChain.Decode(input, [new FilterStage(FlateFilter.FilterName, null, null)], limits, governor)
            : FlateFilter.Decode(input, null, limits, governor);
        Assert.Equal(raw, result);
        Assert.Equal(raw.Length, governor.Snapshot().TotalDecodedBytes);
    }

    [Fact]
    public void AggregateFailurePrecedesAnotherLargeOutputAllocation()
    {
        byte[] raw = new byte[60000];
        new Random(43).NextBytes(raw);
        byte[] input = Compress(raw);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        byte[] first = FlateFilter.Decode(input, null, limits, governor);
        long before = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => FlateFilter.Decode(input, null, limits, governor));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(nameof(QpdfLimits.MaxDecodedBytes), error.LimitName);
        Assert.True(allocated < 100000L, $"Rejected repeated inflate allocated {allocated} bytes.");
        Assert.Equal(first.Length, governor.Snapshot().TotalDecodedBytes);
        Assert.Equal(first.Length + GovernedBufferStream.ArrayOverheadBytes, governor.Snapshot().LiveMemoryBytes);
        long maximumWorking = FlateFilter.InflateWorkingSetBytes + 8192L + 65536L + 128L;
        Assert.True(governor.Snapshot().PeakMemoryBytes < first.Length + maximumWorking);
        GC.KeepAlive(first);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public void PngSelectorsDoNotConsumeTheSampleBudget(bool chain, int priorBytes)
    {
        byte[] predicted = [0, 1, 2, 3, 2, 3, 3, 3];
        byte[] input = Compress(predicted);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = 6L + priorBytes };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        governor.ChargeDecodedBytes(priorBytes);
        PdfDictionary dictionary = new(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Predictor")] = new PdfInteger("12", 12L),
            [PdfName.FromAscii("Columns")] = new PdfInteger("3", 3L),
        });
        byte[] result = chain
            ? FilterChain.Decode(input, [new FilterStage(FlateFilter.FilterName, dictionary, null)], limits, governor)
            : FlateFilter.Decode(input, new PredictorParameters(12, 3, 1, 8), limits, governor);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, result);
        Assert.Equal(6L + priorBytes, governor.Snapshot().TotalDecodedBytes);
        Assert.Equal(result.Length + GovernedBufferStream.ArrayOverheadBytes, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void EachStageOfAChainCountsItsOwnDecodedBytes()
    {
        byte[] raw = [1, 2, 3];
        byte[] inner = Compress(raw);
        byte[] outer = Compress(inner);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = inner.Length + raw.Length };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        byte[] result = FilterChain.Decode(outer,
            [new FilterStage(FlateFilter.FilterName, null, null), new FilterStage(FlateFilter.FilterName, null, null)],
            limits, governor);
        Assert.Equal(raw, result);
        Assert.Equal(inner.Length + raw.Length, governor.Snapshot().TotalDecodedBytes);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(12)]
    public void SharedPredictorAdmissionUsesTheRemainingBudget(int predictor)
    {
        byte[] input = new byte[100000 + (predictor == 2 ? 0 : 1)];
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        governor.ChargeDecodedBytes(limits.MaxDecodedBytes);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => PredictorStages.Apply(input,
            new PredictorParameters(predictor, 100000, 1, 8), FlateFilter.FilterName, governor));
        Assert.Equal(nameof(QpdfLimits.MaxDecodedBytes), error.LimitName);
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
        Assert.Equal(limits.MaxDecodedBytes, governor.Snapshot().TotalDecodedBytes);
    }
}
