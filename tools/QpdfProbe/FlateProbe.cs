using System.IO.Compression;

namespace Lokad.Qpdf.Probe;

// Bounds the BCL Flate pipeline for PDF FlateDecode use. ZLibStream handles
// the zlib-wrapped (RFC1950) form used by PDF; DeflateStream handles raw
// deflate. qpdf --compression-level 1..9 maps to BCL buckets (1 Fastest,
// 2..8 Optimal, 9 SmallestSize). Output limits are enforced by a bounded copy
// loop because the codec alone does not cap allocations. Cancellation flows
// through the async overloads.
internal static class FlateProbe
{
    internal const long MaxProbeOutputBytes = 256 * 1024;

    internal static CompressionLevel MapQpdfLevelToBcl(int qpdfLevel)
    {
        if (qpdfLevel < 1 || qpdfLevel > 9)
        {
            throw new ProbeException($"qpdf --compression-level must be in [1..9], got {qpdfLevel}.", 1);
        }

        if (qpdfLevel == 1)
        {
            return CompressionLevel.Fastest;
        }

        if (qpdfLevel == 9)
        {
            return CompressionLevel.SmallestSize;
        }

        return CompressionLevel.Optimal;
    }

    internal static byte[] CompressZlib(byte[] input, CompressionLevel level)
    {
        using MemoryStream output = new();
        using (ZLibStream compressor = new(output, level))
        {
            compressor.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }

    internal static byte[] DecompressZlibBounded(byte[] input, long maxOutputBytes)
    {
        using MemoryStream source = new(input, writable: false);
        using ZLibStream decompressor = new(source, CompressionMode.Decompress);
        return CopyBounded(decompressor, maxOutputBytes);
    }

    internal static byte[] DecompressDeflateBounded(byte[] input, long maxOutputBytes)
    {
        using MemoryStream source = new(input, writable: false);
        using DeflateStream decompressor = new(source, CompressionMode.Decompress);
        return CopyBounded(decompressor, maxOutputBytes);
    }

    internal static async Task<byte[]> CompressZlibAsync(byte[] input, CompressionLevel level, CancellationToken cancellationToken)
    {
        using MemoryStream output = new();
        using (ZLibStream compressor = new(output, level))
        {
            await compressor.WriteAsync(input, cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }

    internal static async Task<byte[]> DecompressZlibBoundedAsync(byte[] input, long maxOutputBytes, CancellationToken cancellationToken)
    {
        using MemoryStream source = new(input, writable: false);
        using ZLibStream decompressor = new(source, CompressionMode.Decompress);
        return await CopyBoundedAsync(decompressor, maxOutputBytes, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task RunAsync()
    {
        byte[] payload = System.Text.Encoding.ASCII.GetBytes("Flate probe payload: repeated bytes 0123456789; ");
        byte[] repeated = new byte[payload.Length * 64];
        for (int i = 0; i < 64; i++)
        {
            Buffer.BlockCopy(payload, 0, repeated, i * payload.Length, payload.Length);
        }

        byte[] compressed = CompressZlib(repeated, CompressionLevel.Optimal);
        byte[] roundTripped = DecompressZlibBounded(compressed, MaxProbeOutputBytes);
        if (!roundTripped.AsSpan().SequenceEqual(repeated))
        {
            throw new ProbeException("Flate zlib round-trip changed bytes.", 1);
        }

        Console.WriteLine($"flate: zlib round-trip ok input={repeated.Length} compressed={compressed.Length}");

        CheckLevelMapping();
        Console.WriteLine("flate: qpdf 1..9 maps to BCL Fastest/Optimal/SmallestSize; 0 and 10 rejected");

        CheckTruncation(compressed, repeated);

        CheckExpansionLimit();
        Console.WriteLine($"flate: expansion limit enforced at {MaxProbeOutputBytes} bytes");

        await CheckCancellationAsync(compressed).ConfigureAwait(false);
        Console.WriteLine("flate: cancellation honored");
    }

    private static void CheckLevelMapping()
    {
        if (MapQpdfLevelToBcl(1) != CompressionLevel.Fastest)
        {
            throw new ProbeException("qpdf level 1 must map to BCL Fastest.", 1);
        }

        if (MapQpdfLevelToBcl(5) != CompressionLevel.Optimal || MapQpdfLevelToBcl(8) != CompressionLevel.Optimal)
        {
            throw new ProbeException("qpdf levels 2..8 must map to BCL Optimal.", 1);
        }

        if (MapQpdfLevelToBcl(9) != CompressionLevel.SmallestSize)
        {
            throw new ProbeException("qpdf level 9 must map to BCL SmallestSize.", 1);
        }

        bool lowRejected = false;
        try
        {
            _ = MapQpdfLevelToBcl(0);
        }
        catch (ProbeException)
        {
            lowRejected = true;
        }

        bool highRejected = false;
        try
        {
            _ = MapQpdfLevelToBcl(10);
        }
        catch (ProbeException)
        {
            highRejected = true;
        }

        if (!lowRejected || !highRejected)
        {
            throw new ProbeException("qpdf compression levels outside [1..9] must be rejected.", 1);
        }
    }

    private static void CheckTruncation(byte[] compressed, byte[] original)
    {
        byte[] truncated = new byte[compressed.Length / 2];
        Buffer.BlockCopy(compressed, 0, truncated, 0, truncated.Length);
        try
        {
            byte[] partial = DecompressZlibBounded(truncated, MaxProbeOutputBytes);
            if (partial.AsSpan().SequenceEqual(original))
            {
                throw new ProbeException("Truncated flate input decoded to the full original; truncation was silently accepted.", 1);
            }

            Console.WriteLine($"flate: truncation yields short output ({partial.Length} vs {original.Length} bytes) without throwing; callers must validate expected length");
        }
        catch (InvalidDataException)
        {
            Console.WriteLine("flate: truncation rejected with InvalidDataException");
        }

        byte[] corrupted = (byte[])compressed.Clone();
        corrupted[corrupted.Length / 2] ^= 0xFF;
        bool corruptedRejected = false;
        try
        {
            _ = DecompressZlibBounded(corrupted, MaxProbeOutputBytes);
        }
        catch (InvalidDataException)
        {
            corruptedRejected = true;
        }

        if (!corruptedRejected)
        {
            throw new ProbeException("Corrupted flate input was not rejected with InvalidDataException.", 1);
        }

        Console.WriteLine("flate: corruption rejected with InvalidDataException");
    }

    private static void CheckExpansionLimit()
    {
        byte[] zeros = new byte[65536];
        byte[] bomb = CompressZlib(zeros, CompressionLevel.Optimal);
        bool limited = false;
        try
        {
            _ = DecompressZlibBounded(bomb, 1024);
        }
        catch (ProbeException ex) when (ex.Message.Contains("expansion limit", StringComparison.OrdinalIgnoreCase))
        {
            limited = true;
        }

        if (!limited)
        {
            throw new ProbeException("Flate expansion limit was not enforced.", 1);
        }
    }

    private static async Task CheckCancellationAsync(byte[] compressed)
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        bool cancelled = false;
        try
        {
            _ = await DecompressZlibBoundedAsync(compressed, MaxProbeOutputBytes, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        if (!cancelled)
        {
            throw new ProbeException("Cancelled flate decode did not honor cancellation.", 1);
        }
    }

    private static byte[] CopyBounded(Stream source, long maxOutputBytes)
    {
        using MemoryStream output = new();
        byte[] buffer = new byte[8192];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxOutputBytes)
            {
                throw new ProbeException($"Flate expansion limit exceeded ({total} > {maxOutputBytes}).", 1);
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static async Task<byte[]> CopyBoundedAsync(Stream source, long maxOutputBytes, CancellationToken cancellationToken)
    {
        using MemoryStream output = new();
        byte[] buffer = new byte[8192];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxOutputBytes)
            {
                throw new ProbeException($"Flate expansion limit exceeded ({total} > {maxOutputBytes}).", 1);
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }
}



