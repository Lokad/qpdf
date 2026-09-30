using System.IO.Compression;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Filters;

/// <summary>Predictor parameters resolved from a DecodeParms dictionary.</summary>
internal sealed record PredictorParameters(int Predictor, int Columns, int Colors, int BitsPerComponent)
{
    internal static PredictorParameters? FromDictionary(PdfDictionary? parms)
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

            if (!parms.TryGet(key, out QpdfObject raw) || raw is not PdfInteger integer)
            {
                throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, "FlateDecode", $"DecodeParms /{name} must be an integer.");
            }

            if (name == "Predictor")
            {
                predictor = CheckRange(integer.Value, "Predictor");
            }
            else if (name == "Columns")
            {
                columns = CheckRange(integer.Value, "Columns");
            }
            else if (name == "Colors")
            {
                colors = CheckRange(integer.Value, "Colors");
            }
            else
            {
                bitsPerComponent = CheckRange(integer.Value, "BitsPerComponent");
            }
        }

        if (predictor != 1 && predictor != 2 && (predictor < 10 || predictor > 15))
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, "FlateDecode", $"Unsupported /Predictor value {predictor}.");
        }

        if (columns < 1)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, "FlateDecode", $"Invalid /Columns value {columns}.");
        }

        if (colors < 1)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, "FlateDecode", $"Invalid /Colors value {colors}.");
        }

        if (bitsPerComponent != 1 && bitsPerComponent != 2 && bitsPerComponent != 4
            && bitsPerComponent != 8 && bitsPerComponent != 16)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, "FlateDecode", $"Invalid /BitsPerComponent value {bitsPerComponent}.");
        }

        return new PredictorParameters(predictor, columns, colors, bitsPerComponent);
    }

    private static int CheckRange(long value, string role)
    {
        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, "FlateDecode", $"DecodeParms /{role} is out of range.");
        }

        return (int)value;
    }
}

/// <summary>
/// Governed FlateDecode over BCL inflate with strict zlib framing: the header
/// is validated, preset dictionaries rejected, the Adler-32 trailer verified,
/// and predictor transforms applied under dimension budgets. This strictness
/// exceeds lenient BCL behavior, which can return short output for truncated
/// input without throwing (see the M0 probe record).
/// </summary>
internal static class FlateFilter
{
    internal const string FilterName = "FlateDecode";

    internal static byte[] Decode(byte[] input, PredictorParameters? parms, QpdfLimits limits, ResourceGovernor governor)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        limits.Validate();
        byte[] inflated = Inflate(input, governor, limits);
        if (parms is null || parms.Predictor == 1)
        {
            return inflated;
        }

        return parms.Predictor == 2
            ? ApplyTiff(inflated, parms)
            : ApplyPng(inflated, parms);
    }

    private static byte[] Inflate(byte[] input, ResourceGovernor governor, QpdfLimits limits)
    {
        if (input.Length < 7)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.TruncatedInput, FilterName, $"Zlib input holds {input.Length} bytes.");
        }

        if ((input[0] & 0x0F) != 8)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Not a deflate zlib stream.");
        }

        if ((input[0] >> 4) > 7)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Unsupported zlib window size.");
        }

        if (((input[0] << 8) | input[1]) % 31 != 0)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Bad zlib header check.");
        }

        if ((input[1] & 0x20) != 0)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Preset dictionaries are unsupported.");
        }

        int bodyLength = input.Length - 6;
        using MemoryStream source = new(input, 2, bodyLength, writable: false);
        using DeflateStream decompressor = new(source, CompressionMode.Decompress);
        using MemoryStream output = new();
        byte[] buffer = new byte[8192];
        long total = 0L;
        try
        {
            int read;
            while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > limits.MaxDecodedBytes)
                {
                    throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), total, limits.MaxDecodedBytes);
                }

                governor.ReserveMemory(read);
                output.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.TruncatedInput, FilterName, "Deflate body is corrupt or truncated.", ex);
        }

        uint expected = ((uint)input[input.Length - 4] << 24) | ((uint)input[input.Length - 3] << 16)
            | ((uint)input[input.Length - 2] << 8) | input[input.Length - 1];
        byte[] result = output.ToArray();
        if (Adler32(result) != expected)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.TruncatedInput, FilterName, "Adler-32 mismatch.");
        }

        return result;
    }

    private static uint Adler32(byte[] data)
    {
        const uint Mod = 65521U;
        uint a = 1U;
        uint b = 0U;
        int i = 0;
        while (i < data.Length)
        {
            int chunk = Math.Min(5552, data.Length - i);
            for (int j = 0; j < chunk; j++)
            {
                a += data[i + j];
                b += a;
            }

            a %= Mod;
            b %= Mod;
            i += chunk;
        }

        return (b << 16) | a;
    }

    private static int RowLength(PredictorParameters parms)
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
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Predictor dimensions overflow.", ex);
        }

        if (rowLength < 1L || rowLength > int.MaxValue)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Predictor row length is out of range.");
        }

        return (int)rowLength;
    }

    private static byte[] ApplyTiff(byte[] inflated, PredictorParameters parms)
    {
        int rowLength = RowLength(parms);
        if (inflated.Length % rowLength != 0)
        {
            throw new QpdfFilterException(
                QpdfFilterFailureReason.TruncatedInput, FilterName,
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
    private static byte[] ApplyPng(byte[] inflated, PredictorParameters parms)
    {
        int rowLength = RowLength(parms);
        long stride = (long)rowLength + 1L;
        if (inflated.Length % stride != 0L)
        {
            throw new QpdfFilterException(
                QpdfFilterFailureReason.TruncatedInput, FilterName,
                $"Inflated length {inflated.Length} is not a multiple of the PNG stride {stride}.");
        }

        int rows = (int)(inflated.Length / stride);
        long outputLength = (long)rows * rowLength;
        if (outputLength > int.MaxValue)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Predictor output is oversized.");
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
                throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, $"Unknown PNG row filter {filter}.");
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

