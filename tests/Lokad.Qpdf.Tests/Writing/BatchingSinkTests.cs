using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;
using Lokad.Qpdf.Objects;
using System.Text;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class BatchingSinkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FormattingAcrossBatchBoundaryReusesOnlySynchronousScratch(bool synchronous)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        SynchronousSink destination = new(false) { CanAppendSynchronously = synchronous };
        using (BatchingSink sink = BatchingSink.Create(destination, QpdfLimits.Default, governor, CancellationToken.None))
        {
            await sink.AppendAsync(new byte[250], CancellationToken.None);
            Assert.Equal(synchronous, sink.TryAppendFormatting(
                [WriterPart.Number(123456), WriterPart.Text(" 0 R")], QpdfLimits.Default, CancellationToken.None));
            if (!synchronous)
            {
                await new PdfSerializer(QpdfLimits.Default, governor).AppendValueAsync(
                    new PdfReference(123456, 0), sink, 0, false, CancellationToken.None);
            }
            await sink.AppendAsync("!"u8.ToArray(), CancellationToken.None);
            await sink.FlushAsync(CancellationToken.None);
            Assert.Equal(261L, sink.Length);
        }
        Assert.Equal(new byte[250].Concat("123456 0 R!"u8.ToArray()).ToArray(), destination.Bytes.ToArray());
        if (!synchronous)
        { Assert.Equal(destination.Bytes.ToArray(), destination.Retained.SelectMany(chunk => chunk.ToArray()).ToArray()); }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SmallByteBoundaryHonorsSynchronousCapabilityAndCopiesCallerBytes(bool synchronous)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        SynchronousSink destination = new(false) { CanAppendSynchronously = synchronous };
        byte[] token = "immutable"u8.ToArray();
        using (BatchingSink sink = BatchingSink.Create(destination, QpdfLimits.Default, governor, CancellationToken.None))
        {
            await sink.AppendAsync(new byte[250], CancellationToken.None);
            bool appended = sink.TryAppendBytes(token, CancellationToken.None);
            Assert.Equal(synchronous, appended);
            if (!appended)
            {
                await sink.AppendAsync(token, CancellationToken.None);
            }
            Array.Fill(token, (byte)'x');
            await sink.AppendAsync("!"u8.ToArray(), CancellationToken.None);
            await sink.FlushAsync(CancellationToken.None);
            Assert.Equal(260L, sink.Length);
        }
        Assert.Equal(new byte[250].Concat("immutable!"u8.ToArray()).ToArray(), destination.Bytes.ToArray());
        if (!synchronous)
        { Assert.Equal(destination.Bytes.ToArray(), destination.Retained.SelectMany(chunk => chunk.ToArray()).ToArray()); }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task FormattingBoundaryFlushFailureReleasesScratch()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        SynchronousSink destination = new(true);
        using (BatchingSink sink = BatchingSink.Create(destination, QpdfLimits.Default, governor, CancellationToken.None))
        {
            await sink.AppendAsync(new byte[250], CancellationToken.None);
            Assert.Throws<IOException>(() => sink.TryAppendFormatting(
                [WriterPart.Number(123456), WriterPart.Text(" 0 R")], QpdfLimits.Default, CancellationToken.None));
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task DirectTokenFormattingPreservesEscapesAndNumbers()
    {
        PdfArray value = new([new PdfName([65, 35, 32, 47, 255]),
            new PdfByteString([40, 41, 92, 10, 0, 255], false), new PdfByteString([0, 255], true),
            new PdfInteger("10000", 10000), PdfName.FromAscii("Distinct")]);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        RetainingSink destination = new(false);
        using (BatchingSink sink = BatchingSink.Create(destination, QpdfLimits.Default, governor, CancellationToken.None))
        {
            await new PdfSerializer(QpdfLimits.Default, governor).AppendValueAsync(value, sink, 0, false, CancellationToken.None);
            await sink.FlushAsync(CancellationToken.None);
        }
        const string expected = """[/A#23#20#2F#FF (\(\)\\\n\000\377) <00FF> 10000 /Distinct]""";
        Assert.Equal(expected, Encoding.ASCII.GetString(destination.Chunks.SelectMany(chunk => chunk.ToArray()).ToArray()));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class RetainingSink(bool fail) : IByteSink, IOutputCapacityHint
    {
        internal List<ReadOnlyMemory<byte>> Chunks { get; } = new();
        internal long CapacityHint { get; private set; }
        public void EnsureCapacity(long length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapacityHint = length;
        }
        public long Length { get; private set; }
        public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Chunks.Add(chunk);
            if (fail) { throw new IOException("Authored write failure."); }
            Length += chunk.Length;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SynchronousSink(bool fail) : ISynchronousByteSink
    {
        internal MemoryStream Bytes { get; } = new();
        public long Length => Bytes.Length;
        public bool CanAppendSynchronously { get; init; } = true;
        internal List<ReadOnlyMemory<byte>> Retained { get; } = [];
        public void Append(ReadOnlySpan<byte> chunk, CancellationToken token)
        {
            if (!CanAppendSynchronously) { throw new InvalidOperationException("Synchronous consumption is unsupported."); }
            token.ThrowIfCancellationRequested();
            if (fail) { throw new IOException("Authored synchronous failure."); }
            Bytes.Write(chunk);
        }
        public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (fail) { throw new IOException("Authored asynchronous failure."); }
            Retained.Add(chunk); Bytes.Write(chunk.Span);
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousCapabilityPreservesBytesAcrossFlushReuseAndDisposal(bool synchronous)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        SynchronousSink destination = new(false) { CanAppendSynchronously = synchronous };
        using (BatchingSink sink = BatchingSink.Create(destination, QpdfLimits.Default, governor, CancellationToken.None))
        {
            await sink.AppendAsync("first"u8.ToArray(), CancellationToken.None);
            await sink.FlushAsync(CancellationToken.None);
            await sink.AppendAsync("second"u8.ToArray(), CancellationToken.None);
            await sink.FlushAsync(CancellationToken.None);
            Assert.Equal(11L, sink.Length);
        }
        Assert.Equal("firstsecond"u8.ToArray(), destination.Bytes.ToArray());
        if (!synchronous)
        { Assert.Equal("firstsecond"u8.ToArray(), destination.Retained.SelectMany(chunk => chunk.ToArray()).ToArray()); }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousFailureOrCancellationReleasesPendingStorage(bool cancel)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        SynchronousSink destination = new(!cancel);
        using (BatchingSink sink = BatchingSink.Create(destination, QpdfLimits.Default, governor, cancellation.Token))
        {
            await sink.AppendAsync("pending"u8.ToArray(), cancellation.Token);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sink.FlushAsync(cancellation.Token));
            }
            else { await Assert.ThrowsAsync<IOException>(async () => await sink.FlushAsync(cancellation.Token)); }
        }
        Assert.Empty(destination.Bytes.ToArray());
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task LogicalOffsetsAndRetainedPacketsSurviveScratchReuseAndDisposal()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        RetainingSink destination = new(false);
        using (BatchingSink sink = BatchingSink.Create(destination, QpdfLimits.Default, governor, CancellationToken.None))
        {
            byte[] bytes = [1, 2, 3];
            await sink.AppendAsync(bytes, CancellationToken.None);
            Assert.Equal(3L, sink.Length);
            Assert.Equal(0L, destination.Length);
            ((IOutputCapacityHint)sink).EnsureCapacity(8L, CancellationToken.None);
            Assert.Equal(8L, destination.CapacityHint);
            Assert.Equal(3L, sink.Length);
            bytes[0] = 9;
            await sink.FlushAsync(CancellationToken.None);
            await sink.AppendAsync("later"u8.ToArray(), CancellationToken.None);
            await sink.FlushAsync(CancellationToken.None);
            Assert.Equal(8L, sink.Length);
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, destination.Chunks[0].ToArray());
        Assert.Equal("later"u8.ToArray(), destination.Chunks[1].ToArray());
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAndCancellationReleasePendingStorage(bool cancel)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        RetainingSink destination = new(!cancel);
        using (BatchingSink sink = BatchingSink.Create(destination, QpdfLimits.Default, governor, cancellation.Token))
        {
            await sink.AppendAsync("pending"u8.ToArray(), cancellation.Token);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sink.FlushAsync(cancellation.Token));
            }
            else { await Assert.ThrowsAsync<IOException>(async () => await sink.FlushAsync(cancellation.Token)); }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        if (!cancel) { Assert.Equal("pending"u8.ToArray(), Assert.Single(destination.Chunks).ToArray()); }
    }

    [Fact]
    public async Task BufferedOutputStillRejectsBeyondItsByteCeiling()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 4 };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        RetainingSink destination = new(false);
        using BatchingSink sink = BatchingSink.Create(destination, limits, governor, CancellationToken.None);
        await sink.AppendAsync("abc"u8.ToArray(), CancellationToken.None);
        QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(async () =>
            await sink.AppendAsync("de"u8.ToArray(), CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxOutputBytes), failure.LimitName);
        Assert.Empty(destination.Chunks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeOutputHintKeepsTightJobsBoundedAndRetainedBytesStable(bool tightMemory)
    {
        QpdfLimits limits = tightMemory
            ? QpdfLimits.Default with { MaxMemoryBytes = 1024L }
            : QpdfLimits.Default with { MaxWork = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        RetainingSink destination = new(false);
        using (BatchingSink sink = BatchingSink.Create(destination, limits, governor, CancellationToken.None))
        {
            ((IOutputCapacityHint)sink).EnsureCapacity(32768L, CancellationToken.None);
            byte[] data = new byte[64]; Array.Fill(data, (byte)'A');
            for (int index = 0; index < 16; index++) { await sink.AppendAsync(data, CancellationToken.None); }
            await sink.FlushAsync(CancellationToken.None);
            Array.Fill(data, (byte)'X');
        }
        byte[] written = destination.Chunks.SelectMany(chunk => chunk.ToArray()).ToArray();
        Assert.Equal(Enumerable.Repeat((byte)'A', 1024).ToArray(), written);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 0L, limits.MaxMemoryBytes);
        Assert.InRange(governor.Snapshot().TotalWork, 0L, limits.MaxWork);
    }

    [Theory]
    [InlineData(500, true, "ordinary")]
    [InlineData(32, true, "ordinary")]
    [InlineData(500, true, "linearized")]
    [InlineData(500, true, "linearized-streamed")]
    [InlineData(2, false, "ordinary")]
    public async Task RepeatedPageSelectionCompletesWithinPracticalHostOperationBudget(long hostOperations, bool succeeds, string mode)
    {
        InMemoryQpdfHost host = new();
        byte[] source = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf"));
        host.AddInput(QpdfPath.From("in.pdf"), source, QpdfInputOwnership.Copy);
        byte[] prior = "prior output"u8.ToArray();
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("out.pdf"), CancellationToken.None))
        {
            await seed.WriteAsync(prior, CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }
        string pages = string.Join(',', Enumerable.Repeat("1", 200));
        List<string> arguments = ["in.pdf", "--static-id", "--stream-data=preserve", "--decode-level=none",
            "--compress-streams=n", "--object-streams=" + (mode == "linearized-streamed" ? "generate" : "disable"),
            "--remove-unreferenced-resources=no", "--pages", ".", pages, "--", "out.pdf"];
        if (mode != "ordinary") { arguments.Insert(1, "--linearize"); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(arguments),
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxHostOperations = hostOperations }), CancellationToken.None);
        if (!succeeds)
        {
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
            Assert.True(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] unchanged));
            Assert.Equal(prior, unchanged);
            Assert.Equal(0L, host.OpenLeaseCount);
            return;
        }
        Assert.True(result.Status == QpdfExecutionStatus.Completed,
            $"{result.Status}; host operations={result.Counters.HostOperations}; "
            + string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.True(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] output));
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(200, independent.NumberOfPages);
        Assert.True(result.Counters.HostOperations <= hostOperations);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}
