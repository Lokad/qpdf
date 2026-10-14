using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ClassicCountingTests
{
    private sealed class OutputSink(long prefix) : IByteSink
    {
        internal MemoryStream Bytes { get; } = new();
        public long Length => prefix + Bytes.Length;
        public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Bytes.Write(chunk.Span);
            return ValueTask.CompletedTask;
        }
    }

    private static ValueTask<long> Write(IReadOnlyList<NumberedObject> model, ResourceGovernor governor,
        IByteSink sink, CancellationToken token)
        => new PdfSerializer(QpdfLimits.Default, governor).SerializeAsync(new PdfVersion(1, 7), model,
            null, new byte[16], new byte[16], false, sink, token);

    [Theory]
    [InlineData("dense")]
    [InlineData("sparse")]
    [InlineData("duplicate")]
    [InlineData("reordered")]
    public async Task ClassicCountMatchesEmissionAcrossNumberingShapes(string shape)
    {
        List<NumberedObject> model = WriterIndexOwnershipTests.Model(41);
        if (shape == "sparse") { model.RemoveAt(20); }
        if (shape == "duplicate") { model.Add(new NumberedObject(4, PdfNull.Instance)); }
        if (shape == "reordered") { (model[0], model[1]) = (model[1], model[0]); }
        ResourceGovernor counting = new(QpdfLimits.Default, CancellationToken.None);
        CountingSink counter = new();
        long expected = await Write(model, counting, counter, CancellationToken.None);
        ResourceGovernor emitting = new(QpdfLimits.Default, CancellationToken.None);
        OutputSink output = new(0L);
        Assert.Equal(expected, await Write(model, emitting, output, CancellationToken.None));
        Assert.Equal(expected, output.Bytes.Length);
        Assert.Equal(0L, counting.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, emitting.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, 10000000000L)]
    [InlineData(true, 10000000000L)]
    [InlineData(false, 9999999950L)]
    [InlineData(true, 9999999950L)]
    public async Task OversizedOffsetsKeepTheirRejectionAndReleaseOwners(bool sparse, long prefix)
    {
        List<NumberedObject> model = WriterIndexOwnershipTests.Model(5);
        if (sparse) { model.RemoveAt(3); }
        CountingSink counter = new(); counter.AppendLength(prefix, CancellationToken.None);
        ResourceGovernor counting = new(QpdfLimits.Default, CancellationToken.None);
        QpdfException countError = await Assert.ThrowsAsync<QpdfException>(
            () => Write(model, counting, counter, CancellationToken.None).AsTask());
        ResourceGovernor emitting = new(QpdfLimits.Default, CancellationToken.None);
        QpdfException emittedError = await Assert.ThrowsAsync<QpdfException>(
            () => Write(model, emitting, new OutputSink(prefix), CancellationToken.None).AsTask());
        Assert.Equal(emittedError.Message, countError.Message);
        Assert.Contains("classic xref field width", countError.Message, StringComparison.Ordinal);
        Assert.Equal(0L, counting.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, emitting.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task ObjectFailureStillPrecedesOversizedXrefRejection()
    {
        QpdfObject nested = PdfNull.Instance;
        for (int depth = 0; depth < 258; depth++) { nested = new PdfArray([nested]); }
        NumberedObject[] model = [new(1, PdfNull.Instance), new(2, nested)];
        CountingSink counter = new(); counter.AppendLength(10_000_000_000L, CancellationToken.None);
        ResourceGovernor counting = new(QpdfLimits.Default, CancellationToken.None);
        QpdfParseException countError = await Assert.ThrowsAsync<QpdfParseException>(
            () => Write(model, counting, counter, CancellationToken.None).AsTask());
        ResourceGovernor emitting = new(QpdfLimits.Default, CancellationToken.None);
        QpdfParseException emittedError = await Assert.ThrowsAsync<QpdfParseException>(
            () => Write(model, emitting, new OutputSink(10_000_000_000L), CancellationToken.None).AsTask());
        Assert.Contains("Serialization depth", countError.Message, StringComparison.Ordinal);
        Assert.Equal(emittedError.Message, countError.Message);
        Assert.Equal(0L, counting.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, emitting.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TightEmissionRejectsLargeIndexesAndReleasesStorage(bool reordered)
    {
        List<NumberedObject> model = WriterIndexOwnershipTests.Model(513);
        if (reordered) { (model[0], model[1]) = (model[1], model[0]); }
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        OutputSink sink = new(0L);
        QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() =>
            new PdfSerializer(limits, governor).SerializeAsync(new PdfVersion(1, 7), model,
                null, new byte[16], new byte[16], false, sink, CancellationToken.None).AsTask());
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 0L, limits.MaxMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(513, model.Count);
    }
}
