using System.IO.Compression;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Filters;

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
        governor.CheckCancellation();
        byte[] inflated = Inflate(input, governor, limits);
        if (parms is null || parms.Predictor == 1)
        {
            return inflated;
        }

        return PredictorStages.Apply(inflated, parms, FilterName, governor);
    }

    private static byte[] Inflate(byte[] input, ResourceGovernor governor, QpdfLimits limits)
    {
        governor.CheckCancellation();
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
                governor.CheckCancellation();
                governor.ChargeWork(read);
                governor.ChargeDecodedBytes(read);
                total += read;
                if (total > limits.MaxDecodedBytes)
                {
                    throw new QpdfLimitException(nameof(QpdfLimits.MaxDecodedBytes), total, limits.MaxDecodedBytes);
                }

                governor.ReserveMemory(read);
                output.Write(buffer, 0, read);
            }

            governor.CheckCancellation();
        }
        catch (InvalidDataException ex)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.TruncatedInput, FilterName, "Deflate body is corrupt or truncated.", ex);
        }

        uint expected = ((uint)input[input.Length - 4] << 24) | ((uint)input[input.Length - 3] << 16)
            | ((uint)input[input.Length - 2] << 8) | input[input.Length - 1];
        governor.CheckCancellation();
        governor.ChargeWork(output.Length + 1L);
        governor.ReserveMemory(output.Length);
        byte[] result = output.ToArray();
        governor.ReleaseMemory(output.Length);
        governor.ReserveMemory(result.Length);
        // Retain one copy for the returned buffer; release the working buffer reservation.
        governor.ReleaseMemory(output.Length);
        if (Adler32(result, governor) != expected)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.TruncatedInput, FilterName, "Adler-32 mismatch.");
        }

        return result;
    }

    /// <summary>
    /// Flate-encodes raw bytes with a zlib wrapper for PDF FlateDecode
    /// streams. Levels follow the documented buckets: 1 is fastest, 9 is
    /// smallest size, anything else (including the default) is optimal.
    /// </summary>
    internal static byte[] Encode(byte[] input, int? level, QpdfLimits limits, ResourceGovernor governor)
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
        governor.CheckCancellation();
        if (input.Length == 0)
        {
            // BCL emits no framing for empty input; return the valid empty zlib stream
            // (header 78 9C, empty fixed block 03 00, Adler-32 of empty 00 00 00 01).
            byte[] empty = new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 };
            governor.ChargeWork(1L);
            governor.ReserveMemory(empty.Length);
            return empty;
        }

        System.IO.Compression.CompressionLevel mapped = level switch
        {
            1 => System.IO.Compression.CompressionLevel.Fastest,
            9 => System.IO.Compression.CompressionLevel.SmallestSize,
            null => System.IO.Compression.CompressionLevel.Optimal,
            >= 2 and <= 8 => System.IO.Compression.CompressionLevel.Optimal,
            _ => throw new ArgumentOutOfRangeException(nameof(level), "Flate level must be 1..9."),
        };
        governor.ChargeWork(input.Length + 1L);
        try
        {
            using System.IO.MemoryStream output = new();
            using (System.IO.Compression.ZLibStream compressor = new(output, mapped, leaveOpen: true))
            {
                compressor.Write(input, 0, input.Length);
            }

            byte[] result = output.ToArray();
            if (result.Length > limits.MaxOutputBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), result.Length, limits.MaxOutputBytes);
            }

            governor.ReserveMemory(result.Length);
            return result;
        }
        catch (QpdfException)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.IO.IOException || ex is ObjectDisposedException || ex is NotSupportedException || ex is ArgumentException)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Flate encoding failed.", ex);
        }
    }

    private static uint Adler32(byte[] data, ResourceGovernor governor)
    {
        const uint Mod = 65521U;
        uint a = 1U;
        uint b = 0U;
        int i = 0;
        while (i < data.Length)
        {
            governor.CheckCancellation();
            governor.ChargeWork(1L);
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

}
