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
        IReadOnlyList<IReadOnlyList<int>> groups = [objects.Skip(2).Select(value => value.Number).ToArray()];
        return new PdfSerializer(limits, governor).SerializeStreamedAsync(new PdfVersion(1, 5), objects,
            groups, false, null, null, new byte[16], new byte[16], newline, sink, token);
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(1, true, false)]
    [InlineData(100, false, false)]
    [InlineData(100, true, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, true)]
    [InlineData(100, false, true)]
    [InlineData(100, true, true)]
    public async Task CountMatchesEmittedOffsetsAcrossMemberAndDecimalBoundaries(int count, bool newline, bool sparse)
    {
        NumberedObject[] objects = Model(count, 128);
        if (sparse)
        { for (int index = 2; index < objects.Length; index++) { objects[index] = new(objects[index].Number + 1, objects[index].Value); } }
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task InvalidNumbersAndContainerOverflowRejectAndRefund(int number)
    {
        NumberedObject[] objects = Model(1, 8); objects[2] = new(number, objects[2].Value);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        await Assert.ThrowsAsync<QpdfException>(() => Write(objects, QpdfLimits.Default, governor, new CountingSink(), false, CancellationToken.None).AsTask());
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
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
