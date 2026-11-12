using System.Text.Json;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Image;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Content;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class CmykJpegTests
{
    private sealed class ExistingCodec : IQpdfJpegCodec
    {
        public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
            => throw new InvalidOperationException("An existing codec must never receive CMYK samples.");
        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
            => throw new InvalidOperationException();
    }

    [Fact]
    public void ExistingCodecsDeclineBeforeSampleAllocation()
    {
        IQpdfJpegCodec codec = new ExistingCodec();
        Assert.False(codec.SupportsCmykDecoding);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1 };
        ResourceGovernor governor = new(limits, CancellationToken.None) { JpegCodec = codec };
        byte[] jpeg = CmykJpegBuilder.Build([10, -20, 30, -40], null);
        QpdfFilterException failure = Assert.Throws<QpdfFilterException>(() => DctFilter.Decode(jpeg, limits, governor));
        Assert.Equal(QpdfFilterFailureReason.UnsupportedFilter, failure.Reason);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task DecodingPreservesPdfColorSpaceAndDecodeArray(int transform, bool job)
    {
        byte[] jpeg = CmykJpegBuilder.Build([10, -20, 30, -40], transform < 0 ? null : transform);
        byte[] input = ImageOptimizeTests.BuildImagePage(8, 8, "DeviceCMYK", 8,
            " /Filter /DCTDecode /Decode [1 0 1 0 1 0 1 0]", jpeg);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, LokadImageJpegCodec.Instance);
        host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", decodeLevel = "all" }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "output", "--decode-level=all"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        PdfStream stream = await ImageOptimizeTests.OutputImageAsync(host, output);
        Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("ColorSpace"), out QpdfObject space));
        Assert.Equal("DeviceCMYK", Assert.IsType<PdfName>(space).DisplayText);
        Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("Decode"), out QpdfObject decode));
        Assert.Equal(new[] { 1L, 0L, 1L, 0L, 1L, 0L, 1L, 0L }, Assert.IsType<PdfArray>(decode).Items.Select(value => Assert.IsType<PdfInteger>(value).Value));
        byte[] samples = Writing.StreamObservation.Decode(stream);
        byte[] pixel = transform == 2 ? [75, 132, 152, 88] : [138, 108, 158, 88];
        Assert.Equal(256, samples.Length);
        for (int offset = 0; offset < samples.Length; offset += 4) { Assert.Equal(pixel, samples.AsSpan(offset, 4).ToArray()); }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("decoded")]
    [InlineData("memory")]
    public void FourComponentDecodeHonorsLimitsAndCancellation(string failure)
    {
        using CancellationTokenSource cancellation = new();
        if (failure == "cancel") { cancellation.Cancel(); }
        QpdfLimits limits = QpdfLimits.Default with
        { MaxDecodedBytes = failure == "decoded" ? 255 : 10000, MaxMemoryBytes = failure == "memory" ? 255 : 1048576 };
        ResourceGovernor governor = new(limits, cancellation.Token) { JpegCodec = LokadImageJpegCodec.Instance };
        byte[] jpeg = CmykJpegBuilder.Build([10, -20, 30, -40], 2);
        Exception? error = Record.Exception(() => DctFilter.Decode(jpeg, limits, governor));
        if (failure == "cancel") { Assert.IsAssignableFrom<OperationCanceledException>(error); }
        else { Assert.Equal(failure == "decoded" ? nameof(QpdfLimits.MaxDecodedBytes) : nameof(QpdfLimits.MaxMemoryBytes), Assert.IsType<QpdfLimitException>(error).LimitName); }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
