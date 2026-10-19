using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ObjectStreamWriterCaptureTests
{
    internal static List<NumberedObject> Model(bool opaque)
    {
        List<NumberedObject> model = LinearizedStreamedPartsTests.OnePageModel();
        model[4] = new NumberedObject(5, new PdfDictionary(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Type")] = PdfName.FromAscii("Font"),
            [PdfName.FromAscii("BaseFont")] = PdfName.FromAscii("Helvetica"),
            [PdfName.FromAscii("AuthoredMembers")] = opaque
                ? new PdfByteString(new byte[16384], true)
                : new PdfArray(Enumerable.Repeat<QpdfObject>(PdfNull.Instance, 4096).ToArray())
        }));
        return model;
    }

    internal static Task<long> Start(string route, List<NumberedObject> model, QpdfLimits limits,
        ResourceGovernor governor, IByteSink sink, CancellationToken cancellationToken)
    {
        byte[] id = new byte[16];
        IReadOnlyList<IReadOnlyList<int>> groups = new IReadOnlyList<int>[] { new[] { 5 } };
        PdfVersion version = new(1, 5);
        if (route == "linearized" || route == "counting")
        {
            LinearizedWriter writer = new(limits, governor);
            return writer.WriteLinearizedStreamedAsync(version, model, groups, null, id, id, false, false, null,
                route == "counting" ? new CountingSink() : sink, route == "counting" ? sink : null, cancellationToken).AsTask();
        }
        PdfSerializer serializer = new(limits, governor);
        return route == "qdf"
            ? serializer.SerializeQdfAsync(version, model, groups, null, null, id, id, new QdfOptions(true, false), false, sink, cancellationToken).AsTask()
            : serializer.SerializeStreamedAsync(version, model, groups, false, null, null, id, id, false, sink, cancellationToken).AsTask();
    }

    [Theory]
    [InlineData("ordinary", 49152L)]
    [InlineData("qdf", 49152L)]
    [InlineData("counting", 85000L)]
    public async Task CaptureAdmissionRejectsBeforeAnyContainerIsWritten(string route, long ceiling)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = ceiling };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        MarkerSink sink = new();
        List<NumberedObject> model = Model(route == "counting");
        QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() =>
            Start(route, model, limits, governor, sink, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.True(sink.Length > 0L);
        Assert.False(sink.ContainerStarted);
        Assert.True(governor.Snapshot().PeakMemoryBytes <= ceiling);
        Assert.True(governor.Snapshot().LiveMemoryBytes < 4096L);
        Assert.True(Assert.IsType<PdfDictionary>(model[4].Value).TryGet(PdfName.FromAscii("AuthoredMembers"), out QpdfObject value));
        if (route == "counting") { Assert.Equal(16384, Assert.IsType<PdfByteString>(value).ValueSpan.Length); }
        else { Assert.Same(PdfNull.Instance, Assert.IsType<PdfArray>(value)[0]); }
    }

    private sealed class MarkerSink : IByteSink
    {
        public long Length { get; private set; }
        internal bool ContainerStarted { get; private set; }

        public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ContainerStarted |= chunk.Span.IndexOf("/Type /ObjStm"u8) >= 0;
            Length += chunk.Length;
            return ValueTask.CompletedTask;
        }
    }
}
