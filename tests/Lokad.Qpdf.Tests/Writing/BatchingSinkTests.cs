using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;
using Lokad.Qpdf.Objects;
using System.Text;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class BatchingSinkTests
{
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

    private sealed class RetainingSink(bool fail) : IByteSink
    {
        internal List<ReadOnlyMemory<byte>> Chunks { get; } = new();
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
    [InlineData(500, true)]
    [InlineData(2, false)]
    public async Task RepeatedPageSelectionCompletesWithinPracticalHostOperationBudget(long hostOperations, bool succeeds)
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
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["in.pdf", "--static-id", "--stream-data=preserve", "--decode-level=none",
                "--compress-streams=n", "--object-streams=disable", "--remove-unreferenced-resources=no",
                "--pages", ".", pages, "--", "out.pdf"]),
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
