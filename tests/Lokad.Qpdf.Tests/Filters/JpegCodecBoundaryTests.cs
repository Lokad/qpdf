using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Image;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Content;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class JpegCodecBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImageAdapterRejectsTiffSignaturesBeforeDecoding(bool bigEndian)
    {
        byte[] signature = bigEndian ? "MM\0*\0\0\0\x08"u8.ToArray() : "II*\0\x08\0\0\0"u8.ToArray();
        using MemoryStream encoded = new(signature);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfJpegCodecContext context = new(governor);
        Assert.Throws<Lokad.Image.UnsupportedImageException>(() => LokadImageJpegCodec.Instance.Decode(
            encoded, new QpdfJpegFrame(1, 1, 1), new byte[1], context));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task CodecFreeSelectionPreservesJpegAndCheckingReportsSkippedDecode()
    {
        byte[] jpeg = DctDecodeTests.EncodeGrayJpeg(8, 8);
        InMemoryQpdfHost host = new();
        Assert.Null(host.JpegCodec);
        QpdfPath source = QpdfPath.From("input.pdf"), target = QpdfPath.From("output.pdf");
        host.AddInput(source, DctDecodeTests.BuildDctPage(jpeg), QpdfInputOwnership.Copy);
        PdfPageSelectionResult copied = await Qpdf.SelectPageAsync(host, source, target, new PdfPageSelectionOptions(1), CancellationToken.None);
        Assert.Equal(1, copied.PageCount);
        Assert.True(host.TryGetOutput(target, out byte[] output));
        Assert.True(output.AsSpan().IndexOf(jpeg) >= 0);
        QpdfCheckResult check = await Qpdf.CheckAsync(host, source, CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Skipped, Assert.Single(check.Checks, c => c.Name == "streams").Status);
        Assert.Contains(check.Diagnostics, d => d.Message.Contains("DCTDecode unavailable", StringComparison.Ordinal));
        Assert.Contains(check.Diagnostics, d => d.Message.Contains("requires a host codec", StringComparison.Ordinal));
        Assert.DoesNotContain(check.Diagnostics, d => d.Message.Contains("milestone", StringComparison.Ordinal));
        Assert.Equal(0L, host.OpenLeaseCount);
        using InMemoryQpdfHost supplied = new(QpdfLimits.Default, LokadImageJpegCodec.Instance);
        supplied.AddInput(source, DctDecodeTests.BuildDctPage(jpeg), QpdfInputOwnership.Copy);
        QpdfCheckResult decoded = await Qpdf.CheckAsync(supplied, source, CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(decoded.Checks, c => c.Name == "streams").Status);
        Assert.False(decoded.HasSkipped); Assert.Equal(0L, supplied.OpenLeaseCount);
    }

    [Fact]
    public async Task RequestedOptimizationWithoutCodecFailsBeforePublication()
    {
        InMemoryQpdfHost host = new();
        QpdfPath input = QpdfPath.From("input.pdf"), target = QpdfPath.From("output.pdf");
        host.AddInput(input, ImageOptimizeTests.BuildImagePage(200, 200, "DeviceGray", 8, "", new byte[40000]), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(target, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["input.pdf", "--optimize-images", "output.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("codec", StringComparison.OrdinalIgnoreCase));
        Assert.True(host.TryGetOutput(target, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCodecIsSelectedPerHostAcrossArgvAndJobJson(bool job)
    {
        byte[] jpeg = DctDecodeTests.EncodeGrayJpeg(8, 8);
        foreach (byte sample in new byte[] { 17, 93 })
        {
            RecordingCodec codec = new(jpeg, sample, "success", null);
            InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
            host.AddInput(QpdfPath.From("input.pdf"), DctDecodeTests.BuildDctPage(jpeg), QpdfInputOwnership.Copy);
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input.pdf","showObject":"4","filteredStreamData":""}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input.pdf", "--show-object=4", "--filtered-stream-data"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal(Enumerable.Repeat(sample, 64), result.OutputBytes.GetValueOrDefault().ToArray());
            Assert.True(codec.DecodeCalled); Assert.Equal(0L, host.OpenLeaseCount);
            Assert.NotNull(codec.Context);
            Assert.Throws<ObjectDisposedException>(() => codec.Context.CheckCancellation());
        }
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("cancel")]
    [InlineData("output")]
    public void FailedCodecCallsRetireReservationsAndCannotReuseTheContext(string failure)
    {
        byte[] jpeg = DctDecodeTests.EncodeGrayJpeg(8, 8);
        using CancellationTokenSource cancellation = new();
        RecordingCodec codec = new(jpeg, 0, failure, cancellation);
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = failure == "output" ? 10L : 10000L, CancellationCheckInterval = 1L };
        ResourceGovernor governor = new(limits, cancellation.Token) { JpegCodec = codec };
        Exception? error = Record.Exception(() => JpegCodec.EncodeGray(new byte[64], 8, 8, 0, limits, governor));
        if (failure == "exception") { Assert.IsType<QpdfFilterException>(error); Assert.DoesNotContain("private codec detail", error.Message); }
        else if (failure == "cancel") { Assert.IsAssignableFrom<OperationCanceledException>(error); }
        else { Assert.IsType<QpdfLimitException>(error); }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.NotNull(codec.Context); Assert.Throws<ObjectDisposedException>(() => codec.Context.ReserveMemory(1L));
        governor.ReserveMemory(4096L); codec.Reservation?.Dispose();
        Assert.Equal(4096L, governor.Snapshot().LiveMemoryBytes); governor.ReleaseMemory(4096L);
    }

    [Fact]
    public void EncodingUsesThePublicQualityScaleAndIndependentOutputStorage()
    {
        byte[] jpeg = DctDecodeTests.EncodeGrayJpeg(8, 8);
        RecordingCodec codec = new(jpeg, 0, "success", null);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None) { JpegCodec = codec };
        byte[] first = JpegCodec.EncodeGray(new byte[64], 8, 8, 0, QpdfLimits.Default, governor);
        Assert.Equal(0, codec.Quality); Assert.Equal(jpeg, first);
        byte[] second = JpegCodec.EncodeGray(new byte[64], 8, 8, 100, QpdfLimits.Default, governor);
        Assert.Equal(100, codec.Quality); Assert.NotSame(first, second);
        second[0] = 0; Assert.Equal(jpeg, first);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllocationFailureEscapesCodecTranslationAndReleasesWorkspace(bool decode)
    {
        byte[] jpeg = DctDecodeTests.EncodeGrayJpeg(8, 8);
        RecordingCodec codec = new(jpeg, 0, "oom", null);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None) { JpegCodec = codec };
        // Inject the exception without exhausting the test process.
        Assert.Throws<OutOfMemoryException>(() => decode
            ? DctFilter.Decode(jpeg, QpdfLimits.Default, governor)
            : JpegCodec.EncodeGray(new byte[64], 8, 8, 75, QpdfLimits.Default, governor));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.NotNull(codec.Context);
        Assert.Throws<ObjectDisposedException>(() => codec.Context.ReserveMemory(1L));
        governor.ReserveMemory(4096L); codec.Reservation?.Dispose();
        Assert.Equal(4096L, governor.Snapshot().LiveMemoryBytes);
        governor.ReleaseMemory(4096L);
    }

    [Fact]
    public void MismatchedEncoderFrameIsRejectedWithoutRetainingItsOutput()
    {
        byte[] jpeg = DctDecodeTests.EncodeGrayJpeg(16, 16);
        RecordingCodec codec = new(jpeg, 0, "success", null);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None) { JpegCodec = codec };
        Assert.Throws<QpdfFilterException>(() => JpegCodec.EncodeGray(new byte[64], 8, 8, 75, QpdfLimits.Default, governor));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void DecodedAmplificationIsRejectedBeforeCallingTheProvider()
    {
        byte[] jpeg = DctDecodeTests.PatchFrameDimensions(DctDecodeTests.EncodeGrayJpeg(8, 8), 4096, 4096);
        RecordingCodec codec = new(jpeg, 0, "success", null);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = 1048576L };
        ResourceGovernor governor = new(limits, CancellationToken.None) { JpegCodec = codec };
        Assert.Throws<QpdfLimitException>(() => DctFilter.Decode(jpeg, limits, governor));
        Assert.False(codec.DecodeCalled); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class RecordingCodec(byte[] jpeg, byte sample, string failure, CancellationTokenSource? cancellation) : IQpdfJpegCodec
    {
        internal QpdfJpegCodecContext? Context { get; private set; }
        internal IDisposable? Reservation { get; private set; }
        internal bool DecodeCalled { get; private set; }
        internal int Quality { get; private set; }

        public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
        {
            DecodeCalled = true; Begin(context); samples.Span.Fill(sample);
        }

        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
        {
            Quality = quality; Begin(context); encoded.Write(jpeg);
        }

        private void Begin(QpdfJpegCodecContext context)
        {
            Context = context; Reservation = context.ReserveMemory(65536L);
            if (failure == "exception") { throw new InvalidOperationException("private codec detail"); }
            if (failure == "oom") { throw new OutOfMemoryException("injected allocation failure"); }
            if (failure == "cancel") { cancellation?.Cancel(); context.CheckCancellation(); }
            // Deliberately leave the reservation outstanding to exercise scope cleanup.
        }
    }
}
