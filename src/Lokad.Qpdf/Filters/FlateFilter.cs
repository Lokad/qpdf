using System.IO.Compression;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Filters;

/// <summary>
/// Governed FlateDecode over BCL ZLibStream. Header checks reject unsupported
/// framing and preset dictionaries; bounded input rejects exhaustion before the
/// codec completes its frame and validates Adler-32. Later container bytes are
/// ignored. Predictor transforms retain their dimension and allocation bounds.
/// </summary>
internal static partial class FlateFilter
{
    internal const string FilterName = "FlateDecode";

    // Conservative allowances for .NET 10 codec state and its pooled 8 KiB
    // buffer. Explicit scratch and growing output arrays are charged separately.
    // See the runtime/zlib source evidence in docs/COMPATIBILITY.md.
    internal const long InflateWorkingSetBytes = 128L * 1024L;
    internal const long DeflateWorkingSetBytes = 512L * 1024L;
    private const int ChunkBytes = GovernedReadStream.MaximumReadBytes;

    internal static byte[] Decode(byte[] input, PredictorParameters? parms, QpdfLimits limits, ResourceGovernor governor)
        => Decode(input, parms, limits, governor, CancellationToken.None);

    internal static byte[] Decode(byte[] input, PredictorParameters? parms, QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
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
        token.ThrowIfCancellationRequested();
        governor.CheckCancellation();
        long selectorStride = parms is not null && parms.Predictor >= 10
            ? PredictorStages.RowLength(parms, FilterName) + 1L
            : 0L;
        byte[] inflated = Inflate(input, selectorStride, governor, limits, token);
        byte[] result;
        if (parms is null || parms.Predictor == 1)
        {
            result = inflated;
        }
        else
        {
            try
            {
                result = PredictorStages.Apply(inflated, parms, FilterName, governor, token);
            }
            finally
            {
                long bytes = inflated.Length == 0 ? 0L : inflated.Length + GovernedBufferStream.ArrayOverheadBytes;
                inflated = Array.Empty<byte>();
                governor.ReleaseMemory(bytes);
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

    private static byte[] Inflate(byte[] input, long selectorStride, ResourceGovernor governor, QpdfLimits limits, CancellationToken token)
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
            // Decode the complete zlib frame so checksum bytes or inline-image
            // padding cannot masquerade as a missing DEFLATE end marker.
            using GovernedReadStream source = new(input, governor, token);
            using ZLibStream decompressor = new(source, CompressionMode.Decompress);
            long maximumRaw = Math.Min(limits.MaxDecodedBytes, Array.MaxLength);
            if (selectorStride > 0L)
            {
                // Raw PNG rows also hold selectors. Bound their expansion while
                // admitting only sample bytes against the decoded-byte budget.
                maximumRaw = Math.Min(Array.MaxLength, maximumRaw + maximumRaw / (selectorStride - 1L) + 1L);
            }

            using GovernedBufferStream output = new(governor, maximumRaw, nameof(QpdfLimits.MaxDecodedBytes), token);
            byte[] buffer = new byte[ChunkBytes];
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    governor.CheckCancellation();
                    // Check cancellation between bounded codec reads; input
                    // refills also check it when no output is produced.
                    governor.NoteOperation();
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

            governor.CheckCancellation();
            token.ThrowIfCancellationRequested();
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
        => EncodeCore(input, level, limits, governor, null, CancellationToken.None);

    internal static byte[] Encode(byte[] input, int? level, QpdfLimits limits, ResourceGovernor governor,
        ParserMemoryScope memory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(governor);
        ArgumentNullException.ThrowIfNull(memory);
        memory.CheckOwner(governor);
        return EncodeCore(input, level, limits, governor, memory, cancellationToken);
    }

    private static byte[] EncodeCore(byte[] input, int? level, QpdfLimits limits, ResourceGovernor governor,
        ParserMemoryScope? memory, CancellationToken cancellationToken)
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
        cancellationToken.ThrowIfCancellationRequested();
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
            governor.NoteOperation();
            long reserved = 8L + GovernedBufferStream.ArrayOverheadBytes;
            if (memory is null) { governor.ReserveMemory(reserved); }
            else { memory.Reserve(reserved); }
            try
            {
                return new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 };
            }
            catch
            {
                if (memory is null) { governor.ReleaseMemory(reserved); }
                else { memory.Release(reserved); }
                throw;
            }
        }

        governor.NoteOperation();
        long workingSet = DeflateWorkingSetBytes + (memory is null ? 0L : 256L);
        governor.ReserveMemory(workingSet);
        try
        {
            using GovernedBufferStream output = new(governor, limits.MaxOutputBytes, nameof(QpdfLimits.MaxOutputBytes), cancellationToken);
            using (ZLibStream compressor = new(output, mapped, leaveOpen: true))
            {
                for (int position = 0; position < input.Length;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    governor.CheckCancellation();
                    int count = Math.Min(ChunkBytes, input.Length - position);
                    if (memory is not null) { governor.NoteOperation(); }
                    compressor.Write(input, position, count);
                    position += count;
                }
            }

            return memory is null ? output.ToArray() : output.ToArray(memory, cancellationToken);
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
            governor.ReleaseMemory(workingSet);
        }
    }
}
