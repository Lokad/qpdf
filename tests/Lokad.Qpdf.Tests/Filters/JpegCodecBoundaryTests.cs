using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Image;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Content;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class JpegCodecBoundaryTests
{
    private sealed class BorrowCheckingCodec(byte[] expected) : IQpdfJpegCodec
    {
        public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
        {
            Assert.False(encoded.CanWrite);
            MemoryStream source = Assert.IsType<MemoryStream>(encoded);
            Assert.True(source.TryGetBuffer(out ArraySegment<byte> segment));
            Assert.Same(expected, segment.Array);
            Assert.Equal(0, segment.Offset); Assert.Equal(expected.Length, segment.Count);
            Assert.Throws<NotSupportedException>(() => source.WriteByte(0));
            LokadImageJpegCodec.Instance.Decode(encoded, frame, samples, context);
        }
        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
            => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void CoreJpegInputBorrowsAdmittedBackingWithinTightAllowance(int components)
    {
        byte[] original = JpegFixtures.Encode(17, 11, components, 75, (_, _, _) => 128);
        // Valid, ignored APP15 segments make encoded backing dominate this fixture.
        byte[] jpeg = new byte[original.Length + 16 * 65537];
        original.AsSpan(0, 2).CopyTo(jpeg);
        int at = 2;
        for (int i = 0; i < 16; i++)
        {
            jpeg[at] = 255; jpeg[at + 1] = 239; jpeg[at + 2] = 255; jpeg[at + 3] = 255;
            at += 65537;
        }
        original.AsSpan(2).CopyTo(jpeg.AsSpan(at));
        byte[] before = (byte[])jpeg.Clone();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1536L * 1024L };
        using InMemoryQpdfHost host = new(limits, new BorrowCheckingCodec(jpeg));
        ResourceGovernor governor = new(limits, CancellationToken.None, host);
        using ParserMemoryScope inputOwner = ParserMemoryScope.Create(governor);
        inputOwner.Reserve(jpeg.LongLength + 32L);
        byte[] decoded = DctFilter.Decode(jpeg, limits, governor);
        Assert.Equal(17 * 11 * components, decoded.Length);
        Assert.All(decoded, sample => Assert.Equal((byte)128, sample));
        Assert.Equal(before, jpeg);
        governor.ReleaseMemory(decoded.LongLength + JpegCodec.SampleArrayOverheadBytes);
        inputOwner.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

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
        Assert.NotNull(codec.Context); Assert.Throws<ObjectDisposedException>(() => codec.Context.MemoryBudget.TryReserve(1L, out _));
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
        Assert.Throws<ObjectDisposedException>(() => codec.Context.MemoryBudget.TryReserve(1L, out _));
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

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void NestedCodecFailuresPreserveLocalAndSharedRefusal(bool decode, bool shared, bool growth)
    {
        byte[] jpeg = DctDecodeTests.EncodeGrayJpeg(8, 8);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = shared ? 100000L : 16000L };
        RecordingMemoryBudget root = new(shared ? 16000L : 100000L);
        using InMemoryQpdfHost host = new(limits, new RefusingCodec(growth), root);
        ResourceGovernor governor = new(limits, CancellationToken.None, host);
        Exception? error = Record.Exception(() => decode ? DctFilter.Decode(jpeg, limits, governor)
            : JpegCodec.EncodeGray(new byte[64], 8, 8, 75, limits, governor));
        if (shared) { Assert.IsType<QpdfMemoryAdmissionException>(error); }
        else { Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), Assert.IsType<QpdfLimitException>(error).LimitName); }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task ConcurrentCodecCallsKeepIndependentOwnersUntilTheirWorkFinishes()
    {
        byte[] jpeg = DctDecodeTests.EncodeGrayJpeg(8, 8);
        RecordingMemoryBudget root = new(1000000L);
        RendezvousCodec codec = new();
        async Task DecodeAsync()
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, codec, root);
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            byte[] decoded = await Task.Run(() => DctFilter.Decode(jpeg, QpdfLimits.Default, governor));
            Assert.All(decoded, value => Assert.Equal((byte)123, value));
            Assert.Equal(decoded.Length + JpegCodec.SampleArrayOverheadBytes, governor.Snapshot().LiveMemoryBytes);
            governor.ReleaseMemory(decoded.Length + JpegCodec.SampleArrayOverheadBytes);
        }
        Task a = DecodeAsync(), b = DecodeAsync();
        await codec.BothEntered.Task;
        Assert.InRange(root.ReservedBytes, 2L * (64000L + 96L), 1000000L);
        codec.Resume.SetResult(); await Task.WhenAll(a, b);
        Assert.Equal(0L, root.ReservedBytes);
    }

    private sealed class RefusingCodec(bool growth) : IQpdfJpegCodec
    {
        public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context) => Fail(context);
        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context) => Fail(context);
        private void Fail(QpdfJpegCodecContext context)
        {
            using IMemoryScope owner = context.MemoryBudget.CreateScope();
            JpegMemoryBudget bridge = new(owner);
            using Lokad.Image.IMemoryScope nested = bridge.CreateScope();
            if (growth)
            {
                Assert.True(nested.TryReserve(1000L, out Lokad.Image.IMemoryReservation? receipt));
                Assert.False(receipt.TryResize(32000L));
            }
            else { Assert.False(nested.TryReserve(32000L, out _)); }
            // Simulate a nested codec translating false into its own failure type.
            throw new InvalidDataException("Nested codec allocation refused.");
        }
    }

    private sealed class RendezvousCodec : IQpdfJpegCodec
    {
        private int _entered;
        internal TaskCompletionSource BothEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
        {
            using IMemoryScope owner = context.MemoryBudget.CreateScope();
            Assert.True(owner.TryReserve(64000L, out _));
            if (Interlocked.Increment(ref _entered) == 2) { BothEntered.SetResult(); }
            Resume.Task.GetAwaiter().GetResult();
            samples.Span.Fill(123);
        }
        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
            => throw new NotSupportedException();
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
            Context = context;
            Assert.True(context.MemoryBudget.TryReserve(65536L, out IMemoryReservation? reservation));
            Reservation = reservation;
            if (failure == "exception") { throw new InvalidOperationException("private codec detail"); }
            if (failure == "oom") { throw new OutOfMemoryException("injected allocation failure"); }
            if (failure == "cancel") { cancellation?.Cancel(); context.CheckCancellation(); }
            // Deliberately leave the reservation outstanding to exercise scope cleanup.
        }
    }
}
