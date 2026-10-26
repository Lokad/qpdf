using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class QdfBinaryTests
{
    public static IEnumerable<object[]> Payloads()
    {
        foreach (bool objectStreams in new[] { false, true })
        foreach (byte[] payload in new byte[][] { [1, 2, 3], [1, 13, 3], [1, 13, 10], [1, 2, 13], [13], Enumerable.Range(0, 256).Select(i => (byte)i).ToArray() })
        { yield return [objectStreams, payload]; }
    }

    private static async Task<byte[]> Repair(byte[] input)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.FixQdfAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal(0L, host.OpenLeaseCount);
        return output;
    }

    private static async Task CheckImage(byte[] pdf, byte[] expected)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), pdf, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None))
        {
            Assert.Equal(1, document.PageCount);
            int images = 0;
            Assert.True(document.Engine.Trailer.TryGet(PdfName.FromAscii("Size"), out QpdfObject sizeValue));
            int size = checked((int)Assert.IsType<PdfInteger>(sizeValue).Value);
            for (int number = 1; number < size; number++)
            {
                if (document.GetObject(number) is PdfStream stream && stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject subtype)
                    && subtype.Equals(PdfName.FromAscii("Image")))
                { images++; Assert.Equal(expected, stream.Data); }
            }
            Assert.Equal(1, images);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.Equal(1, independent.NumberOfPages);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task PristineBinaryQdfPreservesImageBytesAndRepairsIdempotently(bool objectStreams, byte[] payload)
    {
        byte[] qdf = await QdfRepairFixtures.Emit(QdfRepairFixtures.Image(payload), objectStreams);
        byte[] first = await Repair(qdf);
        byte[] second = await Repair(first);
        Assert.Equal(first, second); await CheckImage(second, payload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditedBinaryPayloadGetsItsNewLengthWithoutNormalizingCarriages(bool objectStreams)
    {
        byte[] qdf = await QdfRepairFixtures.Emit(QdfRepairFixtures.Image([17, 13, 23]), objectStreams);
        string edited = Encoding.Latin1.GetString(qdf).Replace("\u0011\r\u0017", "\u0011\r\u0019\u0017", StringComparison.Ordinal)
            .Replace("/Width 3", "/Width 4", StringComparison.Ordinal);
        byte[] output = await Repair(Encoding.Latin1.GetBytes(edited));
        await CheckImage(output, [17, 13, 25, 23]); Assert.Equal(output, await Repair(output));
    }

    [Fact]
    public async Task BinaryXrefCarriagesDoNotPreventASecondRepair()
    {
        byte[] first = await Repair(QdfRepairFixtures.ObjectStream(24, 0));
        Assert.Contains((byte)13, first); // Member index 13 occurs in the binary xref.
        byte[] second = await Repair(first); Assert.Equal(first, second);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), second, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
    }
}
