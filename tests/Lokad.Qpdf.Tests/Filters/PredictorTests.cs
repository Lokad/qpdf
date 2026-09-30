using System.IO.Compression;
using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class PredictorTests
{
    private static (QpdfLimits Limits, ResourceGovernor Governor) Governed()
    {
        QpdfLimits limits = QpdfLimits.Default;
        return (limits, new ResourceGovernor(limits, CancellationToken.None));
    }

    private static byte[] ZLibCompress(byte[] input)
    {
        using MemoryStream output = new();
        using (ZLibStream compressor = new(output, CompressionLevel.Optimal))
        {
            compressor.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }

    private static PdfDictionary PredictorDict(int predictor, int columns, int colors, int bits)
    {
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Predictor")] = new PdfInteger(predictor.ToString(), predictor),
            [PdfName.FromAscii("Columns")] = new PdfInteger(columns.ToString(), columns),
            [PdfName.FromAscii("Colors")] = new PdfInteger(colors.ToString(), colors),
            [PdfName.FromAscii("BitsPerComponent")] = new PdfInteger(bits.ToString(), bits),
        };
        return new PdfDictionary(entries);
    }

    private static PdfDictionary StreamDict(PdfDictionary parms)
    {
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Filter")] = PdfName.FromAscii("FlateDecode"),
            [PdfName.FromAscii("DecodeParms")] = parms,
        };
        return new PdfDictionary(entries);
    }

    private static byte[] DecodePredicted(byte[] predicted, PdfDictionary parms)
    {
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        IReadOnlyList<FilterStage> stages = FilterChain.Resolve(StreamDict(parms));
        return FilterChain.Decode(ZLibCompress(predicted), stages, limits, governor);
    }

    private static int ReadSampleBits(byte[] raw, int bitPosition, int bits)
    {
        int value = 0;
        for (int b = 0; b < bits; b++)
        {
            value = (value << 1) | ((raw[bitPosition / 8] >> (7 - (bitPosition % 8))) & 1);
            bitPosition += 1;
        }

        return value;
    }

    private static void WriteSampleBits(byte[] output, ref int bitPosition, int value, int bits)
    {
        for (int b = bits - 1; b >= 0; b--)
        {
            if (((value >> b) & 1) != 0)
            {
                output[bitPosition / 8] |= (byte)(1 << (7 - (bitPosition % 8)));
            }

            bitPosition += 1;
        }
    }

    private static byte[] TiffForward(byte[] raw, int columns, int colors, int bits)
    {
        int mask = (1 << bits) - 1;
        int rowBits = columns * colors * bits;
        int rowBytes = (rowBits + 7) / 8;
        int rows = raw.Length * 8 / rowBits;
        byte[] output = new byte[rows * rowBytes];
        for (int row = 0; row < rows; row++)
        {
            int[] previous = new int[colors];
            int outBit = row * rowBytes * 8;
            for (int s = 0; s < columns * colors; s++)
            {
                int sample = ReadSampleBits(raw, row * rowBits + s * bits, bits);
                int encoded = (sample - previous[s % colors]) & mask;
                previous[s % colors] = sample;
                WriteSampleBits(output, ref outBit, encoded, bits);
            }
        }

        return output;
    }

    private static byte[] PngForward(byte[][] rows, int[] types, int rowLength, int bytesPerPixel)
    {
        byte[] output = new byte[rows.Length * (rowLength + 1)];
        byte[] previous = new byte[rowLength];
        for (int row = 0; row < rows.Length; row++)
        {
            output[row * (rowLength + 1)] = (byte)types[row];
            for (int i = 0; i < rowLength; i++)
            {
                int raw = rows[row][i];
                int left = i >= bytesPerPixel ? rows[row][i - bytesPerPixel] : 0;
                int up = previous[i];
                int upperLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                int encoded = types[row] switch
                {
                    0 => raw,
                    1 => raw - left,
                    2 => raw - up,
                    3 => raw - ((left + up) / 2),
                    _ => raw - PaethForward(left, up, upperLeft),
                };
                output[row * (rowLength + 1) + 1 + i] = (byte)(encoded & 0xFF);
            }

            Buffer.BlockCopy(rows[row], 0, previous, 0, rowLength);
        }

        return output;
    }

    private static int PaethForward(int left, int up, int upperLeft)
    {
        int estimate = left + up - upperLeft;
        int distanceLeft = Math.Abs(estimate - left);
        int distanceUp = Math.Abs(estimate - up);
        int distanceUpperLeft = Math.Abs(estimate - upperLeft);
        if (distanceLeft <= distanceUp && distanceLeft <= distanceUpperLeft)
        {
            return left;
        }

        return distanceUp <= distanceUpperLeft ? up : upperLeft;
    }

    [Fact]
    public void TiffRoundTripsGrayscale()
    {
        byte[] raw = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120];
        byte[] predicted = TiffForward(raw, 4, 1, 8);
        byte[] decoded = DecodePredicted(predicted, PredictorDict(2, 4, 1, 8));
        Assert.Equal(raw, decoded);
    }

    [Fact]
    public void TiffRoundTripsRgb()
    {
        byte[] raw = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120];
        byte[] predicted = TiffForward(raw, 2, 3, 8);
        byte[] decoded = DecodePredicted(predicted, PredictorDict(2, 2, 3, 8));
        Assert.Equal(raw, decoded);
    }

    [Fact]
    public void TiffRoundTripsPackedSamples()
    {
        byte[] raw = [0xAB, 0xCD, 0xE0];
        byte[] predicted = TiffForward(raw, 5, 1, 4);
        byte[] decoded = DecodePredicted(predicted, PredictorDict(2, 5, 1, 4));
        Assert.Equal(raw, decoded);
    }

    [Fact]
    public void TiffRoundTripsSingleBit()
    {
        byte[] raw = [0b10110001, 0b01101010];
        byte[] predicted = TiffForward(raw, 8, 1, 1);
        byte[] decoded = DecodePredicted(predicted, PredictorDict(2, 8, 1, 1));
        Assert.Equal(raw, decoded);
    }

    [Fact]
    public void PngRoundTripsMixedRowFilters()
    {
        byte[][] rows = [[10, 20, 30], [40, 50, 60]];
        byte[] predicted = PngForward(rows, [1, 4], 3, 1);
        byte[] decoded = DecodePredicted(predicted, PredictorDict(12, 3, 1, 8));
        Assert.Equal([10, 20, 30, 40, 50, 60], decoded);
    }

    [Fact]
    public void PngRoundTripsAllFilterTypes()
    {
        byte[][] rows = [[1, 2, 3, 4], [5, 6, 7, 8], [9, 10, 11, 12], [13, 14, 15, 16], [17, 18, 19, 20]];
        byte[] predicted = PngForward(rows, [0, 1, 2, 3, 4], 4, 1);
        byte[] decoded = DecodePredicted(predicted, PredictorDict(10, 4, 1, 8));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20], decoded);
    }

    [Fact]
    public void PngRoundTripsRgb()
    {
        byte[][] rows = [[1, 2, 3, 4, 5, 6], [7, 8, 9, 10, 11, 12]];
        byte[] predicted = PngForward(rows, [1, 4], 6, 3);
        byte[] decoded = DecodePredicted(predicted, PredictorDict(15, 2, 3, 8));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], decoded);
    }

    [Fact]
    public void PredictorOnePassesThrough()
    {
        byte[] raw = Encoding.ASCII.GetBytes("Predictor 1 means no transform.");
        byte[] decoded = DecodePredicted(raw, PredictorDict(1, 4, 3, 8));
        Assert.Equal(raw, decoded);
    }

    [Fact]
    public void PredictorDefaultsMatchUpstream()
    {
        byte[] raw = [7, 9, 4, 6];
        byte[] predicted = TiffForward(raw, 1, 1, 8);
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Predictor")] = new PdfInteger("2", 2L),
        };
        byte[] decoded = DecodePredicted(predicted, new PdfDictionary(entries));
        Assert.Equal(raw, decoded);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(0)]
    public void RejectsBadPredictorValues(int predictor)
    {
        byte[] raw = [1, 2, 3, 4];
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Predictor")] = new PdfInteger(predictor.ToString(), predictor),
            [PdfName.FromAscii("Columns")] = new PdfInteger("4", 4L),
        };
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        IReadOnlyList<FilterStage> stages = FilterChain.Resolve(StreamDict(new PdfDictionary(entries)));
        QpdfFilterException ex = Assert.Throws<QpdfFilterException>(
            () => FilterChain.Decode(ZLibCompress(raw), stages, limits, governor));
        Assert.Equal(QpdfFilterFailureReason.InvalidData, ex.Reason);
    }

    [Theory]
    [InlineData(0, 1, 8)]
    [InlineData(4, 0, 8)]
    [InlineData(4, 1, 3)]
    [InlineData(4, 1, 7)]
    public void RejectsBadPredictorDimensions(int columns, int colors, int bits)
    {
        byte[] raw = [1, 2, 3, 4];
        QpdfFilterException ex = Assert.Throws<QpdfFilterException>(
            () => DecodePredicted(raw, PredictorDict(2, columns, colors, bits)));
        Assert.Equal(QpdfFilterFailureReason.InvalidData, ex.Reason);
    }

    [Fact]
    public void RejectsRowLengthMismatches()
    {
        byte[] even = [1, 2, 3, 4, 5];
        QpdfFilterException tiff = Assert.Throws<QpdfFilterException>(
            () => DecodePredicted(even, PredictorDict(2, 2, 1, 8)));
        Assert.Equal(QpdfFilterFailureReason.TruncatedInput, tiff.Reason);

        byte[] stride = [0, 1, 2, 3, 4, 5, 6];
        QpdfFilterException png = Assert.Throws<QpdfFilterException>(
            () => DecodePredicted(stride, PredictorDict(12, 3, 1, 8)));
        Assert.Equal(QpdfFilterFailureReason.TruncatedInput, png.Reason);
    }

    [Fact]
    public void RejectsUnknownPngRowFilters()
    {
        byte[] row = [7, 1, 2, 3];
        QpdfFilterException ex = Assert.Throws<QpdfFilterException>(
            () => DecodePredicted(row, PredictorDict(12, 3, 1, 8)));
        Assert.Equal(QpdfFilterFailureReason.InvalidData, ex.Reason);
    }

    [Fact]
    public void ArrayParmsCarryPredictors()
    {
        byte[] raw = [3, 1, 4, 1, 5, 9, 2, 6];
        byte[] predicted = TiffForward(raw, 4, 1, 8);
        Dictionary<PdfName, QpdfObject> parms = new()
        {
            [PdfName.FromAscii("Predictor")] = new PdfInteger("2", 2L),
            [PdfName.FromAscii("Columns")] = new PdfInteger("4", 4L),
        };
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Filter")] = PdfName.FromAscii("FlateDecode"),
            [PdfName.FromAscii("DecodeParms")] = new PdfArray([new PdfDictionary(parms)]),
        };
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        IReadOnlyList<FilterStage> stages = FilterChain.Resolve(new PdfDictionary(entries));
        byte[] decoded = FilterChain.Decode(ZLibCompress(predicted), stages, limits, governor);
        Assert.Equal(raw, decoded);
    }

    [Fact]
    public void SingleParmsDictAppliesToEveryStage()
    {
        byte[] raw = Encoding.ASCII.GetBytes("Two flate stages share one predictor dict.");
        byte[] twice = ZLibCompress(ZLibCompress(raw));
        Dictionary<PdfName, QpdfObject> parms = new()
        {
            [PdfName.FromAscii("Predictor")] = new PdfInteger("1", 1L),
        };
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Filter")] = new PdfArray([PdfName.FromAscii("FlateDecode"), PdfName.FromAscii("Fl")]),
            [PdfName.FromAscii("DecodeParms")] = new PdfDictionary(parms),
        };
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        IReadOnlyList<FilterStage> stages = FilterChain.Resolve(new PdfDictionary(entries));
        byte[] decoded = FilterChain.Decode(twice, stages, limits, governor);
        Assert.Equal(raw, decoded);
    }
}
