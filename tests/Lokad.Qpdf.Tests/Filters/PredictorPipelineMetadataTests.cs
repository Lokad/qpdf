using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class PredictorPipelineMetadataTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataVisitsHonorTheCeilingBeforeCodecExecution(bool lzw)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 64L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        FilterStage stage = new(lzw ? LzwFilter.FilterName : FlateFilter.FilterName, PredictorMetadataTests.UnknownEntries(), null);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => FilterChain.Decode([], [stage], limits, governor));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidEmptyCodecOutputRetiresExclusivePredictorMetadata(bool lzw)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        FilterStage stage = new(lzw ? LzwFilter.FilterName : FlateFilter.FilterName, new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), null);
        byte[] input = lzw ? [128, 64, 64] : [120, 156, 3, 0, 0, 0, 0, 1];
        Assert.Empty(FilterChain.Decode(input, [stage], QpdfLimits.Default, governor)); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EarlyChangeValidationPrecedesLzwPredictorValidationAndIsIgnoredByFlate(bool lzw)
    {
        PdfDictionary dictionary = new(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("Predictor")] = new PdfInteger("7", 7L), [PdfName.FromAscii("EarlyChange")] = new PdfInteger("2", 2L) });
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        FilterStage stage = new(lzw ? LzwFilter.FilterName : FlateFilter.FilterName, dictionary, null);
        QpdfFilterException failure = Assert.Throws<QpdfFilterException>(() => FilterChain.Decode([], [stage], QpdfLimits.Default, governor));
        Assert.Contains(lzw ? "/EarlyChange must be 0 or 1" : "Unsupported /Predictor value 7", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void CodecFailureRetiresMetadataWithoutReleasingPriorStorage()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L); long before = governor.Snapshot().LiveMemoryBytes;
        FilterStage stage = new(FlateFilter.FilterName, new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), null);
        Assert.Throws<QpdfFilterException>(() => FilterChain.Decode([], [stage], QpdfLimits.Default, governor));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
    }
}
