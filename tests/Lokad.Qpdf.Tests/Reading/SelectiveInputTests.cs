using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class SelectiveInputTests
{
    private static readonly QpdfPath Input = QpdfPath.From("in.pdf"), Output = QpdfPath.From("out.pdf");
    private static readonly byte[] Sparse = Build("ordinary");

    private static byte[] Build(string mode)
    {
        byte[] large = new byte[9 * 1048576];
        byte[] payload = mode == "required" ? large : "q Q\n"u8.ToArray();
        List<(int Number, string Body, byte[]? Stream)> objects = CheckTests.SinglePage(
            mode == "malformed" ? "<< /Length 1 >>" : $"<< /Length {payload.Length} >>", payload);
        if (mode != "required") { objects.Add((6, $"<< /Length {large.Length} >>", large)); }
        // Retained metadata after the bulk object exercises both prefetched
        // runs. Small trailing objects also separate the EOF tail from payload.
        int metadata = mode == "required" ? 6 : 7;
        byte[] text = "<metadata>kept</metadata>"u8.ToArray();
        objects.Add((metadata, $"<< /Type /Metadata /Subtype /XML /Length {text.Length} >>", text));
        var catalog = objects[0];
        objects[0] = (catalog.Number, catalog.Body.Replace("/Pages 2 0 R",
            $"/Pages 2 0 R /Metadata {metadata} 0 R", StringComparison.Ordinal), catalog.Stream);
        for (int number = metadata + 1; number < metadata + 17; number++) { objects.Add((number, "null", null)); }
        if (mode == "warning")
        {
            var page = objects[2];
            objects[2] = (page.Number, page.Body.Replace("/Type /Page", "/Type /Page /Type /Page", StringComparison.Ordinal), page.Stream);
        }
        return CheckTests.Assemble(objects, 1);
    }

    private sealed class Host(byte[] bytes, int chunk) : IQpdfHost
    {
        private byte[] _nextInput = bytes;
        internal int Chunk { get; } = chunk;
        internal InMemoryQpdfHost Outputs { get; } = new();
        internal int Opened, Leases;
        internal long ReadBytes;
        internal List<(long Offset, int Count)> Reads { get; } = [];
        internal string Failure = "";
        internal CancellationTokenSource? Cancel;
        internal bool ReplaceFutureInput;

        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Opened++; Leases++;
            return new(new Lease(this, _nextInput, path));
        }

        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => Outputs.CreateOutputAsync(path, token);

        private sealed class Lease(Host host, byte[] snapshot, QpdfPath path) : IQpdfInput
        {
            private bool _disposed;
            public QpdfPath Path => path;
            public string Identity => "selective-input";
            public long Length => snapshot.Length;
            public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                host.Reads.Add((offset, count));
                if (host.ReplaceFutureInput) { host._nextInput = "invalid future input"u8.ToArray(); }
                if (host.Failure == "empty") { return new(ReadOnlyMemory<byte>.Empty); }
                if (host.Reads.Count == 3) { host.Cancel?.Cancel(); }
                token.ThrowIfCancellationRequested();
                int take = Math.Min(count, host.Chunk);
                host.ReadBytes += take;
                return new(snapshot.AsSpan(checked((int)offset), take).ToArray());
            }
            public ValueTask DisposeAsync()
            {
                if (!_disposed) { _disposed = true; host.Leases--; }
                return ValueTask.CompletedTask;
            }
        }
    }

    private static ValueTask<PdfPageSelectionResult> Select(IQpdfHost host, QpdfLimits limits, CancellationToken token)
        => Qpdf.SelectPageAsync(host, Input, Output, PdfPageSelectionOptions.FromPages([1]), limits, token);

    private static async Task<byte[]> Baseline(byte[] input)
    {
        InMemoryQpdfHost host = new(); host.AddInput(Input, input, QpdfInputOwnership.Copy);
        await Select(host, QpdfLimits.Default, CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        return bytes;
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(37)]
    public async Task SmallSelectionUsesBoundedRangesAndPreservesOutput(int chunk)
    {
        Host host = new(Sparse, chunk);
        PdfPageSelectionResult result = await Select(host,
            QpdfLimits.Default with { MaxMemoryBytes = 524288L }, CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        Assert.True(host.Outputs.TryGetOutput(Output, out byte[] output));
        Assert.Equal(await Baseline(Sparse), output);
        Assert.InRange(host.ReadBytes, 1L, 262144L);
        int bulk = Sparse.AsSpan().IndexOf("6 0 obj"u8);
        Assert.True(bulk >= 0);
        int payload = bulk + Sparse.AsSpan(bulk).IndexOf("stream\n"u8) + 7;
        Assert.DoesNotContain(host.Reads, read => read.Offset < payload + 9L * 1048576L && read.Offset + read.Count > payload);
        Assert.True(result.Counters.PeakMemoryBytes <= 524288L);
        Assert.Equal(1, host.Opened); Assert.Equal(0, host.Leases);
        if (chunk == 37) { Assert.True(host.Reads.Count > 3); }
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(1, independent.NumberOfPages);
    }

    [Theory]
    [InlineData("required")]
    [InlineData("warning")]
    public async Task FallbackKeepsOneStableInputAndOriginalOutput(string mode)
    {
        byte[] input = Build(mode);
        Host host = new(input, int.MaxValue) { ReplaceFutureInput = true };
        PdfPageSelectionResult result = await Select(host, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(1, result.PageCount);
        Assert.Contains(host.Reads, read => read.Offset == 0 && read.Count == input.Length);
        Assert.True(host.Outputs.TryGetOutput(Output, out byte[] output));
        Assert.Equal(await Baseline(input), output);
        Assert.Equal(1, host.Opened); Assert.Equal(0, host.Leases);
        if (mode == "warning")
        { Assert.Single(result.Warnings, item => item.Code == QpdfDiagnosticCode.DuplicateKey); }
    }

    [Fact]
    public async Task MalformedSelectedObjectReportsOriginalPhysicalOffset()
    {
        byte[] input = Build("malformed");
        InMemoryQpdfHost baseline = new(); baseline.AddInput(Input, input, QpdfInputOwnership.Copy);
        QpdfParseException expected = await Assert.ThrowsAsync<QpdfParseException>(
            () => Select(baseline, QpdfLimits.Default, CancellationToken.None).AsTask());
        Host host = new(input, int.MaxValue);
        QpdfParseException actual = await Assert.ThrowsAsync<QpdfParseException>(
            () => Select(host, QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.Equal(expected.Message, actual.Message); Assert.Equal(expected.ByteOffset, actual.ByteOffset);
        Assert.Equal(1, host.Opened); Assert.Equal(0, host.Leases);
        Assert.False(host.Outputs.TryGetOutput(Output, out _));
    }

    [Theory]
    [InlineData("input")]
    [InlineData("empty")]
    [InlineData("cancel")]
    public async Task ReadFailuresPreservePriorOutputAndCloseTheLease(string mode)
    {
        using CancellationTokenSource cancel = new();
        Host host = new(Sparse, int.MaxValue) { Failure = mode, Cancel = mode == "cancel" ? cancel : null };
        await using (IQpdfOutputTransaction prior = await host.Outputs.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfLimits limits = mode == "input" ? QpdfLimits.Default with { MaxInputBytes = Sparse.Length - 1L } : QpdfLimits.Default;
        if (mode == "input")
        {
            QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() => Select(host, limits, cancel.Token).AsTask());
            Assert.Equal(nameof(QpdfLimits.MaxInputBytes), failure.LimitName); Assert.Empty(host.Reads);
        }
        else if (mode == "empty")
        { await Assert.ThrowsAsync<QpdfHostException>(() => Select(host, limits, cancel.Token).AsTask()); }
        else { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Select(host, limits, cancel.Token).AsTask()); }
        Assert.True(host.Outputs.TryGetOutput(Output, out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        Assert.Equal(1, host.Opened); Assert.Equal(0, host.Leases);
    }
}
