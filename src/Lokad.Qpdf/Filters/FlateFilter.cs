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

    // Conservative allowances for .NET 10 codec state and its pooled 8 KiB
    // buffer. Explicit scratch and growing output arrays are charged separately.
    // See the runtime/zlib source evidence in docs/COMPATIBILITY.md.
    internal const long InflateWorkingSetBytes = 128L * 1024L;
    internal const long DeflateWorkingSetBytes = 512L * 1024L;
    private const int ChunkBytes = GovernedReadStream.MaximumReadBytes;

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
        long selectorStride = parms is not null && parms.Predictor >= 10
            ? PredictorStages.RowLength(parms, FilterName) + 1L
            : 0L;
        byte[] inflated = Inflate(input, selectorStride, governor, limits);
        byte[] result;
        if (parms is null || parms.Predictor == 1)
        {
            result = inflated;
        }
        else
        {
            try
            {
                result = PredictorStages.Apply(inflated, parms, FilterName, governor);
            }
            finally
            {
                governor.ReleaseMemory(inflated.Length == 0 ? 0L : inflated.Length + GovernedBufferStream.ArrayOverheadBytes);
            }
        }

        try
        {
            governor.ChargeDecodedBytes(result.Length);
            return result;
        }
        catch
        {
            governor.ReleaseMemory(result.Length == 0 ? 0L : result.Length + GovernedBufferStream.ArrayOverheadBytes);
            throw;
        }
    }

    private static byte[] Inflate(byte[] input, long selectorStride, ResourceGovernor governor, QpdfLimits limits)
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

        long reserved = InflateWorkingSetBytes + ChunkBytes + GovernedBufferStream.ArrayOverheadBytes;
        governor.ReserveMemory(reserved);
        try
        {
            using GovernedReadStream source = new(input.AsMemory(2, input.Length - 6), governor);
            using DeflateStream decompressor = new(source, CompressionMode.Decompress);
            long maximumRaw = Math.Min(limits.MaxDecodedBytes, Array.MaxLength);
            if (selectorStride > 0L)
            {
                // Raw PNG rows also hold selectors. Bound their expansion while
                // admitting only sample bytes against the decoded-byte budget.
                maximumRaw = Math.Min(Array.MaxLength, maximumRaw + maximumRaw / (selectorStride - 1L) + 1L);
            }

            using GovernedBufferStream output = new(governor, maximumRaw, nameof(QpdfLimits.MaxDecodedBytes));
            byte[] buffer = new byte[ChunkBytes];
            try
            {
                while (true)
                {
                    governor.CheckCancellation();
                    // Admit each bounded codec output unit before CPU work;
                    // input refills also charge when no output is produced.
                    governor.ChargeWork(ChunkBytes);
                    int read = decompressor.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    long prospectiveLength = output.Length + read;
                    long sampleBytes = selectorStride == 0L
                        ? prospectiveLength
                        : prospectiveLength - (prospectiveLength + selectorStride - 1L) / selectorStride;
                    governor.CheckDecodedBytes(sampleBytes);
                    output.Write(buffer, 0, read);
                }
            }
            catch (InvalidDataException ex)
            {
                throw new QpdfFilterException(QpdfFilterFailureReason.TruncatedInput, FilterName, "Deflate body is corrupt or truncated.", ex);
            }

            uint expected = ((uint)input[input.Length - 4] << 24) | ((uint)input[input.Length - 3] << 16)
                | ((uint)input[input.Length - 2] << 8) | input[input.Length - 1];
            governor.CheckCancellation();
            if (Adler32(output.WrittenSpan, governor) != expected)
            {
                throw new QpdfFilterException(QpdfFilterFailureReason.TruncatedInput, FilterName, "Adler-32 mismatch.");
            }

            return output.ToArray();
        }
        finally
        {
            governor.ReleaseMemory(reserved);
        }
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
        CompressionLevel mapped = level switch
        {
            1 => CompressionLevel.Fastest,
            9 => CompressionLevel.SmallestSize,
            null => CompressionLevel.Optimal,
            >= 2 and <= 8 => CompressionLevel.Optimal,
            _ => throw new ArgumentOutOfRangeException(nameof(level), "Flate level must be 1..9."),
        };
        if (input.Length == 0)
        {
            if (limits.MaxOutputBytes < 8L)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), 8L, limits.MaxOutputBytes);
            }

            // BCL emits no framing for empty input; return the valid empty zlib stream
            // (header 78 9C, empty fixed block 03 00, Adler-32 of empty 00 00 00 01).
            governor.ChargeWork(1L);
            long reserved = 8L + GovernedBufferStream.ArrayOverheadBytes;
            governor.ReserveMemory(reserved);
            try
            {
                return new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 };
            }
            catch
            {
                governor.ReleaseMemory(reserved);
                throw;
            }
        }

        governor.ChargeWork(input.Length + 1L);
        governor.ReserveMemory(DeflateWorkingSetBytes);
        try
        {
            using GovernedBufferStream output = new(governor, limits.MaxOutputBytes, nameof(QpdfLimits.MaxOutputBytes));
            using (ZLibStream compressor = new(output, mapped, leaveOpen: true))
            {
                for (int position = 0; position < input.Length;)
                {
                    governor.CheckCancellation();
                    int count = Math.Min(ChunkBytes, input.Length - position);
                    compressor.Write(input, position, count);
                    position += count;
                }
            }

            return output.ToArray();
        }
        catch (QpdfException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is NotSupportedException || ex is ArgumentException)
        {
            throw new QpdfFilterException(QpdfFilterFailureReason.InvalidData, FilterName, "Flate encoding failed.", ex);
        }
        finally
        {
            governor.ReleaseMemory(DeflateWorkingSetBytes);
        }
    }

    private static uint Adler32(ReadOnlySpan<byte> data, ResourceGovernor governor)
    {
        const uint Mod = 65521U;
        uint a = 1U;
        uint b = 0U;
        int i = 0;
        while (i < data.Length)
        {
            governor.CheckCancellation();
            int chunk = Math.Min(5552, data.Length - i);
            governor.ChargeWork(chunk);
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
