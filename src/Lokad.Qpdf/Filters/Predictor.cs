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
    internal static byte[] Apply(byte[] inflated, PredictorParameters parms, string filterName, ResourceGovernor governor)
    {
        if (parms is null)
        {
            throw new ArgumentNullException(nameof(parms));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        byte[] output = parms.Predictor == 2
            ? ApplyTiff(inflated, parms, filterName)
            : ApplyPng(inflated, parms, filterName);
        governor.ReserveMemory(output.Length);
        return output;
    }

    private static int RowLength(PredictorParameters parms, string filterName)
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

        if (rowLength < 1L || rowLength > int.MaxValue)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, filterName, "Predictor row length is out of range.");
        }

        return (int)rowLength;
    }
    private static byte[] ApplyTiff(byte[] inflated, PredictorParameters parms, string filterName)
    {
        int rowLength = RowLength(parms, filterName);
        if (inflated.Length % rowLength != 0)
        {
            throw new QpdfFilterException(
                QpdfFilterFailureReason.TruncatedInput, filterName,
                $"Inflated length {inflated.Length} is not a multiple of the TIFF row length {rowLength}.");
        }

        byte[] output = new byte[inflated.Length];
        if (parms.BitsPerComponent == 8)
        {
            int colors = parms.Colors;
            int[] previous = new int[colors];
            int rows = inflated.Length / rowLength;
            for (int row = 0; row < rows; row++)
            {
                Array.Clear(previous, 0, colors);
                int baseOffset = row * rowLength;
                for (int i = 0; i < rowLength; i++)
                {
                    int component = i % colors;
                    int decoded = (inflated[baseOffset + i] + previous[component]) & 0xFF;
                    previous[component] = decoded;
                    output[baseOffset + i] = (byte)decoded;
                }
            }

            return output;
        }

        TiffBitReader reader = new(inflated);
        TiffBitWriter writer = new(output);
        int mask = (1 << parms.BitsPerComponent) - 1;
        long samplesPerRow = (long)parms.Columns * parms.Colors;
        int rowCount = inflated.Length / rowLength;
        for (int row = 0; row < rowCount; row++)
        {
            int[] previous = new int[parms.Colors];
            for (long s = 0; s < samplesPerRow; s++)
            {
                int component = (int)(s % parms.Colors);
                int sample = reader.ReadBits(parms.BitsPerComponent);
                int decoded = (sample + previous[component]) & mask;
                previous[component] = decoded;
                writer.WriteBits(decoded, parms.BitsPerComponent);
            }

            reader.SkipRowPadding(row * rowLength + rowLength);
        }

        writer.Flush();
        return output;
    }

    private static byte[] ApplyPng(byte[] inflated, PredictorParameters parms, string filterName)
    {
        int rowLength = RowLength(parms, filterName);
        long stride = (long)rowLength + 1L;
        if (inflated.Length % stride != 0L)
        {
            throw new QpdfFilterException(
                QpdfFilterFailureReason.TruncatedInput, filterName,
                $"Inflated length {inflated.Length} is not a multiple of the PNG stride {stride}.");
        }

        int rows = (int)(inflated.Length / stride);
        long outputLength = (long)rows * rowLength;
        if (outputLength > int.MaxValue)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, filterName, "Predictor output is oversized.");
        }

        byte[] output = new byte[(int)outputLength];
        int bytesPerPixel = (parms.BitsPerComponent * parms.Colors + 7) / 8;
        if (bytesPerPixel > rowLength)
        {
            bytesPerPixel = rowLength;
        }

        byte[] previous = new byte[rowLength];
        for (int row = 0; row < rows; row++)
        {
            int inputBase = row * (rowLength + 1);
            int filter = inflated[inputBase];
            if (filter < 0 || filter > 4)
            {
                throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, filterName, $"Unknown PNG row filter {filter}.");
            }

            int outputBase = row * rowLength;
            for (int i = 0; i < rowLength; i++)
            {
                int raw = inflated[inputBase + 1 + i];
                int left = i >= bytesPerPixel ? output[outputBase + i - bytesPerPixel] : 0;
                int up = previous[i];
                int upperLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
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

            Buffer.BlockCopy(output, outputBase, previous, 0, rowLength);
        }

        return output;
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

    private sealed class TiffBitReader
    {
        private readonly byte[] _data;
        private int _position;
        private int _buffer;
        private int _available;

        internal TiffBitReader(byte[] data)
        {
            _data = data;
            _position = 0;
        }

        internal int ReadBits(int count)
        {
            while (_available < count)
            {
                _buffer = (_buffer << 8) | _data[_position];
                _position += 1;
                _available += 8;
            }

            _available -= count;
            return (_buffer >> _available) & ((1 << count) - 1);
        }

        internal void SkipRowPadding(int rowEnd)
        {
            if (_position < rowEnd)
            {
                _position = rowEnd;
            }

            _buffer = 0;
            _available = 0;
        }
    }

    private sealed class TiffBitWriter
    {
        private readonly byte[] _data;
        private int _position;
        private int _buffer;
        private int _available;

        internal TiffBitWriter(byte[] data)
        {
            _data = data;
            _position = 0;
        }

        internal void WriteBits(int value, int count)
        {
            _buffer = (_buffer << count) | (value & ((1 << count) - 1));
            _available += count;
            while (_available >= 8)
            {
                _available -= 8;
                _data[_position] = (byte)((_buffer >> _available) & 0xFF);
                _position += 1;
            }
        }

        internal void Flush()
        {
            if (_available > 0)
            {
                _data[_position] = (byte)((_buffer << (8 - _available)) & 0xFF);
            }
        }
    }
}
