using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class WriterIdentifierTests
{
    [Theory]
    [InlineData(-1, 1, "id1", false)]
    [InlineData(-1, -1, "id1", false)]
    [InlineData(0, -1, "id2", false)]
    [InlineData(0, 1, "id1", true)]
    [InlineData(1, 0, "id2", true)]
    [InlineData(257, 1, "id1", true)]
    [InlineData(1, 257, "id2", true)]
    [InlineData(257, 0, "id1", true)]
    public void InvalidPairsPreserveExceptionTypeParameterAndPrecedence(int first, int second, string parameter, bool range)
    {
        byte[]? id1 = first < 0 ? null : new byte[first], id2 = second < 0 ? null : new byte[second];
        ArgumentException error = range
            ? Assert.Throws<ArgumentOutOfRangeException>(() => WriterIdentifiers.Validate(id1, id2))
            : Assert.Throws<ArgumentNullException>(() => WriterIdentifiers.Validate(id1, id2));
        Assert.Equal(parameter, error.ParamName);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 256)]
    [InlineData(256, 1)]
    [InlineData(256, 256)]
    public void BothInclusiveLengthBoundariesAreAccepted(int first, int second)
        => WriterIdentifiers.Validate(new byte[first], new byte[second]);

    [Theory]
    [InlineData("classic")]
    [InlineData("streamed")]
    [InlineData("qdf")]
    [InlineData("linearized")]
    [InlineData("linearized-streamed")]
    public async Task EveryWriterRejectsThePairBeforeWriting(string mode)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfSerializer serializer = new(QpdfLimits.Default, governor);
        LinearizedWriter linearized = new(QpdfLimits.Default, governor);
        CountingSink sink = new();
        List<NumberedObject> objects = [];
        IReadOnlyList<IReadOnlyList<int>> groups = [];
        byte[] first = [1], second = new byte[257];
        PdfVersion version = new(1, 7);
        long live = governor.Snapshot().LiveMemoryBytes;
        ArgumentOutOfRangeException error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            _ = mode switch
            {
                "classic" => await serializer.SerializeAsync(version, objects, null, first, second, false, sink, CancellationToken.None),
                "streamed" => await serializer.SerializeStreamedAsync(version, objects, groups, false, null, null, first, second, false, sink, CancellationToken.None),
                "qdf" => await serializer.SerializeQdfAsync(version, objects, null, null, null, first, second, new(true, false), false, sink, CancellationToken.None),
                "linearized" => await linearized.WriteLinearizedAsync(version, objects, null, first, second, false, false, null, sink, null, CancellationToken.None),
                _ => await linearized.WriteLinearizedStreamedAsync(version, objects, groups, null, first, second, false, false, null, sink, null, CancellationToken.None),
            };
        });
        Assert.Equal("id2", error.ParamName);
        Assert.Equal(0L, sink.Length);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
    }
}
