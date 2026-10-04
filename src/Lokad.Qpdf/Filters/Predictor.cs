using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Filters;

/// <summary>Predictor parameters resolved from a DecodeParms dictionary.</summary>
internal sealed record PredictorParameters(int Predictor, int Columns, int Colors, int BitsPerComponent)
{
    internal static PredictorParameters? FromDictionary(PdfDictionary? parms, string filterName, IIndirectResolver? resolver)
    {
        if (parms is null)
        {
            return null;
        }

        int predictor = 1;
        int columns = 1;
        int colors = 1;
        int bitsPerComponent = 8;
        foreach (PdfName key in parms.Keys)
        {
            string name = key.DisplayText;
            if (name != "Predictor" && name != "Columns" && name != "Colors" && name != "BitsPerComponent")
            {
                continue;
            }

            if (!parms.TryGet(key, out QpdfObject raw))
            {
                throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, filterName, $"DecodeParms /{name} must be an integer.");
            }

            raw = DerefValue(raw, resolver, filterName, name);
            if (raw is not PdfInteger integer)
            {
                throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, filterName, $"DecodeParms /{name} must be an integer.");
            }

            if (name == "Predictor")
            {
                predictor = CheckRange(integer.Value, filterName, "Predictor");
            }
            else if (name == "Columns")
            {
                columns = CheckRange(integer.Value, filterName, "Columns");
            }
            else if (name == "Colors")
            {
                colors = CheckRange(integer.Value, filterName, "Colors");
            }
            else
            {
                bitsPerComponent = CheckRange(integer.Value, filterName, "BitsPerComponent");
            }
        }

        if (predictor != 1 && predictor != 2 && (predictor < 10 || predictor > 15))
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, filterName, $"Unsupported /Predictor value {predictor}.");
        }

        if (columns < 1)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, filterName, $"Invalid /Columns value {columns}.");
        }

        if (colors < 1)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, filterName, $"Invalid /Colors value {colors}.");
        }

        if (bitsPerComponent != 1 && bitsPerComponent != 2 && bitsPerComponent != 4
            && bitsPerComponent != 8 && bitsPerComponent != 16)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, filterName, $"Invalid /BitsPerComponent value {bitsPerComponent}.");
        }

        return new PredictorParameters(predictor, columns, colors, bitsPerComponent);
    }

    private static QpdfObject DerefValue(QpdfObject raw, IIndirectResolver? resolver, string filterName, string role)
    {
        if (raw is not PdfReference reference)
        {
            return raw;
        }

        if (resolver is null)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, filterName, $"Indirect DecodeParms /{role} needs an object resolver.");
        }

        if (!resolver.TryResolve(reference, out QpdfObject? resolved) || resolved is null)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, filterName, $"Unresolvable indirect DecodeParms /{role}.");
        }

        return resolved;
    }

    private static int CheckRange(long value, string filterName, string role)
    {
        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, filterName, $"DecodeParms /{role} is out of range.");
        }

        return (int)value;
    }
}

/// <summary>Predictor transforms shared by Flate and LZW stages.</summary>
internal static class PredictorStages
{
    private const int CheckInterval = 1024;

    internal static byte[] Apply(byte[] inflated, PredictorParameters parms, string filterName, ResourceGovernor governor)
    {
        if (inflated is null)
        {
            throw new ArgumentNullException(nameof(inflated));
        }

        if (parms is null)
        {
            throw new ArgumentNullException(nameof(parms));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        governor.CheckCancellation();
        int rowLength = RowLength(parms, filterName);
        bool tiff = parms.Predictor == 2;
        long stride = tiff ? rowLength : rowLength + 1L;
        if (inflated.Length % stride != 0L)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.TruncatedInput, filterName,
                $"Inflated length {inflated.Length} is not a multiple of the predictor stride {stride}.");
        }

        int rows = (int)(inflated.Length / stride);
        int outputLength = checked(rows * rowLength);
        if (outputLength == 0)
        {
            return Array.Empty<byte>();
        }

        governor.CheckDecodedBytes(outputLength);
        governor.ChargeWork(outputLength);
        long reserved = outputLength + GovernedBufferStream.ArrayOverheadBytes;
        governor.ReserveMemory(reserved);
        try
        {
            byte[] output = new byte[outputLength];
            if (tiff)
            {
                ApplyTiff(inflated, output, rows, rowLength, parms, governor);
            }
            else
            {
                ApplyPng(inflated, output, rows, rowLength, parms, filterName, governor);
            }

            return output;
        }
        catch
        {
            governor.ReleaseMemory(reserved);
            throw;
        }
    }

    internal static int RowLength(PredictorParameters parms, string filterName)
    {
        long rowLength;
        try
        {
            checked
            {
                rowLength = ((long)parms.Columns * parms.Colors * parms.BitsPerComponent + 7L) / 8L;
            }
        }
        catch (OverflowException ex)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, filterName, "Predictor dimensions overflow.", ex);
        }

        if (rowLength < 1L || rowLength > Array.MaxLength)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, filterName, "Predictor row length is out of range.");
        }

        return (int)rowLength;
    }

    private static void ApplyTiff(byte[] inflated, byte[] output, int rows, int rowLength,
        PredictorParameters parms, ResourceGovernor governor)
    {
        int bits = parms.BitsPerComponent;
        long samplesPerRow = (long)parms.Columns * parms.Colors;
        int mask = (1 << bits) - 1;
        for (int row = 0; row < rows; row++)
        {
            governor.CheckCancellation();
            int start = row * rowLength;
            if (bits == 8)
            {
                governor.ChargeWork(rowLength);
                for (int i = 0; i < rowLength; i++)
                {
                    if (i % CheckInterval == 0)
                    {
                        governor.CheckCancellation();
                    }

                    int previous = i >= parms.Colors ? output[start + i - parms.Colors] : 0;
                    output[start + i] = (byte)((inflated[start + i] + previous) & 0xFF);
                }
            }
            else
            {
                governor.ChargeWork(samplesPerRow * bits * 3L);
                for (long sample = 0; sample < samplesPerRow; sample++)
                {
                    if (sample % CheckInterval == 0)
                    {
                        governor.CheckCancellation();
                    }

                    long bitPosition = sample * bits;
                    int previous = sample >= parms.Colors
                        ? ReadSample(output, start, (sample - parms.Colors) * bits, bits)
                        : 0;
                    int value = (ReadSample(inflated, start, bitPosition, bits) + previous) & mask;
                    WriteSample(output, start, bitPosition, bits, value);
                }
            }
        }
    }

    private static void ApplyPng(byte[] inflated, byte[] output, int rows, int rowLength,
        PredictorParameters parms, string filterName, ResourceGovernor governor)
    {
        int bytesPerPixel = (int)Math.Min(rowLength, ((long)parms.BitsPerComponent * parms.Colors + 7L) / 8L);
        for (int row = 0; row < rows; row++)
        {
            governor.CheckCancellation();
            governor.ChargeWork(rowLength);
            int inputBase = row * (rowLength + 1);
            int filter = inflated[inputBase];
            if (filter > 4)
            {
                throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, filterName, $"Unknown PNG row filter {filter}.");
            }

            int outputBase = row * rowLength;
            int previousBase = outputBase - rowLength;
            for (int i = 0; i < rowLength; i++)
            {
                if (i % CheckInterval == 0)
                {
                    governor.CheckCancellation();
                }

                int raw = inflated[inputBase + 1 + i];
                int left = i >= bytesPerPixel ? output[outputBase + i - bytesPerPixel] : 0;
                int up = row > 0 ? output[previousBase + i] : 0;
                int upperLeft = row > 0 && i >= bytesPerPixel ? output[previousBase + i - bytesPerPixel] : 0;
                int decoded = filter switch
                {
                    0 => raw,
                    1 => raw + left,
                    2 => raw + up,
                    3 => raw + ((left + up) / 2),
                    _ => raw + Paeth(left, up, upperLeft),
                };
                output[outputBase + i] = (byte)(decoded & 0xFF);
            }
        }
    }

    private static int ReadSample(byte[] data, int rowStart, long bitPosition, int bits)
    {
        int value = 0;
        for (int bit = 0; bit < bits; bit++)
        {
            long position = bitPosition + bit;
            value = (value << 1) | ((data[rowStart + (int)(position / 8L)] >> (7 - (int)(position % 8L))) & 1);
        }

        return value;
    }

    private static void WriteSample(byte[] data, int rowStart, long bitPosition, int bits, int value)
    {
        for (int bit = 0; bit < bits; bit++)
        {
            long position = bitPosition + bit;
            int index = rowStart + (int)(position / 8L);
            data[index] |= (byte)(((value >> (bits - bit - 1)) & 1) << (7 - (int)(position % 8L)));
        }
    }

    private static int Paeth(int left, int up, int upperLeft)
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
}
