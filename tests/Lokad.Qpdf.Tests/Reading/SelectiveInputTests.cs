using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
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
        byte[] text = mode == "catalog-required" ? large : "<metadata>kept</metadata>"u8.ToArray();
        objects.Add((metadata, $"<< /Type /Metadata /Subtype /XML /Length {text.Length} >>", text));
        var catalog = objects[0];
        objects[0] = (catalog.Number, catalog.Body.Replace("/Pages 2 0 R",
            $"/Pages 2 0 R /Metadata {metadata} 0 R /Lang (en-US) /Outlines {metadata + 1} 0 R", StringComparison.Ordinal), catalog.Stream);
        objects.Add((metadata + 1, $"<< /Type /Outlines /First {metadata + 2} 0 R /Last {metadata + 2} 0 R /Count 1 >>", null));
        objects.Add((metadata + 2, $"<< /Title (Kept) /Parent {metadata + 1} 0 R /Dest [3 0 R /Fit] >>", null));
        for (int number = metadata + 3; number < metadata + 19; number++) { objects.Add((number, "null", null)); }
        if (mode == "warning")
        {
            var page = objects[2];
            objects[2] = (page.Number, page.Body.Replace("/Type /Page", "/Type /Page /Type /Page", StringComparison.Ordinal), page.Stream);
        }
        byte[] result = CheckTests.Assemble(objects, 1);
        if (mode == "broken-xref")
        {
            int start = result.AsSpan().LastIndexOf("startxref\n"u8) + 10;
            while (result[start] != (byte)'\n') { result[start++] = (byte)'9'; }
        }
        return result;
    }

    private sealed class Host(byte[] bytes, int chunk) : IQpdfHost
    {
        private byte[] _nextInput = bytes;
        internal int Chunk { get; } = chunk;
        internal InMemoryQpdfHost Outputs { get; } = new();
        internal int Opened, Leases;
        internal int OutputAttempts;
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
        {
            OutputAttempts++;
            if (Failure == "write") { throw new QpdfHostException(path, QpdfHostFailureReason.WriteFailed); }
            return Outputs.CreateOutputAsync(path, token);
        }

        private sealed class Lease(Host host, byte[] snapshot, QpdfPath path) : IQpdfInput
        {
            private bool _disposed;
            public QpdfPath Path => path;
            public string Identity => "selective-input";
            public long Length => snapshot.Length;
            public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                host.Reads.Add((offset, destination.Length));
                if (host.ReplaceFutureInput) { host._nextInput = "invalid future input"u8.ToArray(); }
                if (host.Failure == "empty") { return new(0); }
                if (host.Reads.Count == 3) { host.Cancel?.Cancel(); }
                token.ThrowIfCancellationRequested();
                if (offset < 0 || offset > Length) { throw new ArgumentOutOfRangeException(nameof(offset)); }
                int count = Math.Min(Math.Min(destination.Length, snapshot.Length - (int)offset), host.Chunk);
                snapshot.AsMemory((int)offset, count).CopyTo(destination);
                host.ReadBytes += count;
                return new(count);
            }
            public ValueTask DisposeAsync()
            {
                if (!_disposed)
                {
                    _disposed = true; host.Leases--;
                    if (host.Failure == "close") { throw new QpdfHostException(path, QpdfHostFailureReason.ReadFailed); }
                }
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

    private static ValueTask<QpdfExecutionResult> Execute(IQpdfHost host, bool job, string range,
        bool strict, QpdfLimits limits, CancellationToken token)
    {
        QpdfExecutionOptions options = new(limits);
        if (job)
        {
            Dictionary<string, object> configuration = new()
            {
                ["inputFile"] = Input.Value,
                ["outputFile"] = Output.Value,
                ["pages"] = new[] { new { file = ".", range } },
            };
            if (strict) { configuration.Add("suppressRecovery", true); }
            string json = System.Text.Json.JsonSerializer.Serialize(configuration);
            return Qpdf.ExecuteJobAsync(host, json, options, token);
        }
        List<string> argv = [Input.Value];
        if (strict) { argv.Add("--suppress-recovery"); }
        argv.AddRange(["--pages", ".", range, "--", Output.Value]);
        return Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), options, token);
    }

    [Theory]
    [InlineData(false, "1", int.MaxValue)]
    [InlineData(true, "1", 37)]
    [InlineData(false, "1-1", 37)]
    [InlineData(true, "1-1", int.MaxValue)]
    public async Task PublicSelectionKeepsCatalogWithBoundedReads(bool job, string range, int chunk)
    {
        Host host = new(Sparse, chunk);
        QpdfExecutionResult result = await Execute(host, job, range, false,
            QpdfLimits.Default with { MaxMemoryBytes = 2097152L }, CancellationToken.None);
        Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("; ", result.Diagnostics));
        Assert.True(host.Outputs.TryGetOutput(Output, out byte[] output));
        InMemoryQpdfHost baseline = new(); baseline.AddInput(Input, Sparse, QpdfInputOwnership.Copy);
        QpdfExecutionResult expected = await Execute(baseline, job, range, false, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(expected.ExitCode, result.ExitCode);
        Assert.True(baseline.TryGetOutput(Output, out byte[] expectedOutput)); Assert.Equal(expectedOutput, output);
        Assert.InRange(host.ReadBytes, 1L, 262144L);
        Assert.Equal(1, host.Opened); Assert.Equal(0, host.Leases); Assert.Equal(1, host.OutputAttempts);
        InMemoryQpdfHost reader = new(); reader.AddInput(Output, output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(reader, Output, CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.True(catalog.TryGet(PdfName.FromAscii("Lang"), out QpdfObject language)); Assert.IsType<PdfByteString>(language);
        Assert.True(catalog.TryGet(PdfName.FromAscii("Metadata"), out _));
        Assert.True(catalog.TryGet(PdfName.FromAscii("Outlines"), out _));
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(1, independent.NumberOfPages);
    }

    [Theory]
    [InlineData(false, "1", "required")]
    [InlineData(true, "1-1", "catalog-required")]
    [InlineData(false, "1-1", "warning")]
    [InlineData(true, "1", "broken-xref")]
    public async Task PublicFallbackUsesStableLeaseAndRequestedRecovery(bool job, string range, string mode)
    {
        byte[] input = Build(mode);
        foreach (bool strict in new[] { false, true })
        {
            Host host = new(input, int.MaxValue) { ReplaceFutureInput = true };
            InMemoryQpdfHost baseline = new(); baseline.AddInput(Input, input, QpdfInputOwnership.Copy);
            QpdfExecutionResult expected = await Execute(baseline, job, range, strict, QpdfLimits.Default, CancellationToken.None);
            QpdfExecutionResult result = await Execute(host, job, range, strict, QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(expected.Status, result.Status); Assert.Equal(expected.ExitCode, result.ExitCode);
            Assert.Equal(expected.Diagnostics, result.Diagnostics);
            Assert.Contains(host.Reads, read => read.Offset == 0 && read.Count == input.Length);
            Assert.Equal(1, host.Opened); Assert.Equal(0, host.Leases);
            bool published = baseline.TryGetOutput(Output, out byte[] expectedOutput);
            Assert.Equal(published, host.Outputs.TryGetOutput(Output, out byte[] output));
            if (published) { Assert.Equal(expectedOutput, output); Assert.Equal(1, host.OutputAttempts); }
            else { Assert.Equal(0, host.OutputAttempts); }
            if (mode == "broken-xref")
            {
                Assert.True(result.ExitCode == (strict ? QpdfExitCode.Error : QpdfExitCode.Warning), string.Join("; ", result.Diagnostics));
            }
        }
    }

    [Theory]
    [InlineData(false, "1", "input")]
    [InlineData(true, "1-1", "empty")]
    [InlineData(false, "1-1", "cancel")]
    [InlineData(true, "1", "write")]
    [InlineData(false, "1-1", "write")]
    public async Task PublicFailureClosesLeaseWithoutRetryingPublication(bool job, string range, string mode)
    {
        using CancellationTokenSource cancel = new();
        Host host = new(Sparse, int.MaxValue) { Failure = mode, Cancel = mode == "cancel" ? cancel : null };
        await using (IQpdfOutputTransaction prior = await host.Outputs.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfLimits limits = mode == "input" ? QpdfLimits.Default with { MaxInputBytes = Sparse.Length - 1L } : QpdfLimits.Default;
        QpdfExecutionResult result = await Execute(host, job, range, false, limits, cancel.Token);
        Assert.Equal(mode == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(1, host.Opened); Assert.Equal(0, host.Leases);
        Assert.Equal(mode == "write" ? 1 : 0, host.OutputAttempts);
        if (mode == "input") { Assert.Empty(host.Reads); }
        Assert.True(host.Outputs.TryGetOutput(Output, out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RangeSelectionClosesInputBeforeOpeningOutput(bool job, bool failClose)
    {
        Host host = new(Sparse, int.MaxValue) { Failure = failClose ? "close" : "" };
        await using (IQpdfOutputTransaction prior = await host.Outputs.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Execute(host, job, "1-1", false,
            QpdfLimits.Default with { MaxOpenHandles = failClose ? 2L : 1L }, CancellationToken.None);
        Assert.Equal(failClose ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(1, host.Opened); Assert.Equal(0, host.Leases);
        Assert.Equal(failClose ? 0 : 1, host.OutputAttempts);
        Assert.True(host.Outputs.TryGetOutput(Output, out byte[] output));
        if (failClose) { Assert.Equal("prior"u8.ToArray(), output); }
        else
        {
            using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(output);
            Assert.Equal(1, independent.NumberOfPages);
        }
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
