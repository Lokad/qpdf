using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ObjectStreamCountingTests
{
    private static NumberedObject[] Model(int count, int bytes)
    {
        List<NumberedObject> objects =
        [
            new(1, new PdfDictionary(new Dictionary<PdfName, QpdfObject>
            {
                [PdfName.FromAscii("Type")] = PdfName.FromAscii("Catalog"),
                [PdfName.FromAscii("Pages")] = new PdfReference(2, 0),
            })),
            new(2, new PdfDictionary(new Dictionary<PdfName, QpdfObject>
            {
                [PdfName.FromAscii("Type")] = PdfName.FromAscii("Pages"),
                [PdfName.FromAscii("Count")] = new PdfInteger("0", 0),
                [PdfName.FromAscii("Kids")] = new PdfArray([]),
            })),
        ];
        for (int i = 0; i < count; i++)
        {
            objects.Add(new(3 + i, new PdfArray([new PdfInteger((i * 10000).ToString(), i * 10000),
                new PdfName([65, 35, 32, 255]), new PdfByteString(new byte[bytes], i % 2 == 0)])));
        }
        return objects.ToArray();
    }

    private static ValueTask<long> Write(NumberedObject[] objects, QpdfLimits limits, ResourceGovernor governor,
        IByteSink sink, bool newline, CancellationToken token)
    {
        IReadOnlyList<IReadOnlyList<int>> groups = [Enumerable.Range(3, objects.Length - 2).ToArray()];
        return new PdfSerializer(limits, governor).SerializeStreamedAsync(new PdfVersion(1, 5), objects,
            groups, false, null, null, new byte[16], new byte[16], newline, sink, token);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(100, false)]
    [InlineData(100, true)]
    public async Task CountMatchesEmittedOffsetsAcrossMemberAndDecimalBoundaries(int count, bool newline)
    {
        NumberedObject[] objects = Model(count, 128);
        ResourceGovernor countGovernor = new(QpdfLimits.Default, CancellationToken.None);
        CountingSink counter = new();
        long counted = await Write(objects, QpdfLimits.Default, countGovernor, counter, newline, CancellationToken.None);
        ResourceGovernor emitGovernor = new(QpdfLimits.Default, CancellationToken.None);
        using BufferedSink output = BufferedSink.Create(QpdfLimits.Default, emitGovernor, CancellationToken.None);
        long emitted = await Write(objects, QpdfLimits.Default, emitGovernor, output, newline, CancellationToken.None);
        Assert.Equal(emitted, counted);
        Assert.Equal(output.Length, counter.Length);
        Assert.Equal(0L, countGovernor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task CountingLargeMemberNeedsNoMemberOrPayloadSizedBuffer()
    {
        NumberedObject[] objects = Model(1, 80 * 1024);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 16 * 1024 };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        CountingSink counter = new();
        long length = await Write(objects, limits, governor, counter, false, CancellationToken.None);
        Assert.True(length > 160 * 1024);
        Assert.True(governor.Snapshot().PeakMemoryBytes < limits.MaxMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
