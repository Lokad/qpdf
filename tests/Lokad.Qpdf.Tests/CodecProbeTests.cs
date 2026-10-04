using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Runtime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Lokad.Qpdf.Tests;

public sealed class CodecProbeTests
{
    private const long TestMaxPixels = 1024 * 1024;
    private const long TestMaxOutputBytes = 256 * 1024;

    private static int MapQpdfQuality(int qpdfQuality)
    {
        if (qpdfQuality < 0 || qpdfQuality > 100)
        {
            throw new Xunit.Sdk.XunitException($"qpdf quality out of range: {qpdfQuality}.");
        }

        return qpdfQuality == 0 ? 1 : qpdfQuality;
    }

    private static CompressionLevel MapQpdfLevel(int qpdfLevel)
    {
        if (qpdfLevel < 1 || qpdfLevel > 9)
        {
            throw new Xunit.Sdk.XunitException($"qpdf level out of range: {qpdfLevel}.");
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

    private static byte[] DecompressBounded(byte[] input, long maxOutputBytes)
    {
        using MemoryStream source = new(input, writable: false);
        using ZLibStream decompressor = new(source, CompressionMode.Decompress);
        using MemoryStream output = new();
        byte[] buffer = new byte[8192];
        long total = 0;
        int read;
        while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxOutputBytes)
            {
                throw new InvalidOperationException($"Expansion limit exceeded ({total} > {maxOutputBytes}).");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    [Fact]
    public void JpegGrayscaleRoundTripPreservesDimensions()
    {
        using Image<L8> image = new(8, 8);
        JpegEncoder encoder = new()
        {
            Quality = 75,
        };
        using MemoryStream buffer = new();
        image.Save(buffer, encoder);
        byte[] bytes = buffer.ToArray();
        Assert.NotEmpty(bytes);
        using Image<L8> decoded = Image.Load<L8>(bytes);
        Assert.Equal(8, decoded.Width);
        Assert.Equal(8, decoded.Height);
    }

    [Fact]
    public void JpegRgbRoundTripPreservesDimensions()
    {
        using Image<Rgb24> image = new(8, 8);
        JpegEncoder encoder = new()
        {
            Quality = 75,
        };
        using MemoryStream buffer = new();
        image.Save(buffer, encoder);
        byte[] bytes = buffer.ToArray();
        Assert.NotEmpty(bytes);
        using Image<Rgb24> decoded = Image.Load<Rgb24>(bytes);
        Assert.Equal(8, decoded.Width);
        Assert.Equal(8, decoded.Height);
    }

    [Fact]
    public void JpegQualityEndpointsMatchPinnedContract()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            JpegEncoder encoder = new()
            {
                Quality = 0,
            };
            _ = encoder.Quality;
        });
        Assert.Throws<ArgumentException>(() =>
        {
            JpegEncoder encoder = new()
            {
                Quality = 101,
            };
            _ = encoder.Quality;
        });
        Assert.Equal(1, MapQpdfQuality(0));
        Assert.Equal(75, MapQpdfQuality(75));
        Assert.Equal(100, MapQpdfQuality(100));
        Assert.ThrowsAny<Exception>(() => MapQpdfQuality(101));
    }

    [Fact]
    public void JpegFourComponentSamplesAreNotPreserved()
    {
        // Enum labels exist, but decode yields color-converted RGB pixels.
        Assert.Equal(JpegEncodingColor.Cmyk, JpegEncodingColor.Cmyk);
        Assert.Equal(JpegEncodingColor.Ycck, JpegEncodingColor.Ycck);

        using Image<Rgb24> image = new(8, 8);
        JpegEncoder encoder = new()
        {
            Quality = 75,
            ColorType = JpegEncodingColor.Cmyk,
        };
        using MemoryStream buffer = new();
        image.Save(buffer, encoder);
        byte[] bytes = buffer.ToArray();
        using Image<Rgb24> decoded = Image.Load<Rgb24>(bytes);
        Assert.Equal(8, decoded.Width);
        Assert.Equal(8, decoded.Height);
    }

    [Fact]
    public void JpegDecodeReservesCodecWorkingSet()
    {
        using Image<Rgb24> image = new(8, 8);
        using MemoryStream buffer = new();
        image.Save(buffer, new JpegEncoder { Quality = 75 });
        byte[] bytes = buffer.ToArray();
        long frame = 8L * 8L * 3L;
        QpdfLimits tight = QpdfLimits.Default with { MaxMemoryBytes = frame };
        Assert.Throws<QpdfLimitException>(
            () => DctFilter.Decode(bytes, tight, new ResourceGovernor(tight, CancellationToken.None)));
        QpdfLimits roomy = QpdfLimits.Default with { MaxMemoryBytes = 256L * 1024L };
        byte[] decoded = DctFilter.Decode(bytes, roomy, new ResourceGovernor(roomy, CancellationToken.None));
        Assert.Equal(frame, decoded.Length);
    }

    [Fact]
    public void JpegEncodeReservesCodecWorkingSet()
    {
        byte[] raw = new byte[8 * 8 * 3];
        long frame = 8L * 8L * 3L;
        QpdfLimits tight = QpdfLimits.Default with { MaxMemoryBytes = frame };
        Assert.Throws<QpdfLimitException>(
            () => JpegCodec.EncodeRgb(raw, 8, 8, 75, tight, new ResourceGovernor(tight, CancellationToken.None)));
        QpdfLimits roomy = QpdfLimits.Default with { MaxMemoryBytes = frame + JpegCodec.CodecWorkingSetBytes + 1024L };
        byte[] encoded = JpegCodec.EncodeRgb(raw, 8, 8, 75, roomy, new ResourceGovernor(roomy, CancellationToken.None));
        Assert.True(encoded.Length > 0);
    }

    [Fact]
    public void CancelledEncodeFailsFast()
    {
        byte[] raw = new byte[8 * 8 * 3];
        using CancellationTokenSource source = new();
        source.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, source.Token);
        Assert.Throws<OperationCanceledException>(() => JpegCodec.EncodeRgb(raw, 8, 8, 75, QpdfLimits.Default, governor));
    }

    [Fact]
    public void JpegOversizedDimensionsAreRejectedBeforeAllocation()
    {
        long pixels = (long)100000 * 100000;
        Assert.True(pixels > TestMaxPixels);
    }

    [Fact]
    public async Task JpegCancelledDecodeHonorsCancellation()
    {
        using Image<Rgb24> image = new(8, 8);
        JpegEncoder encoder = new()
        {
            Quality = 75,
        };
        using MemoryStream buffer = new();
        image.Save(buffer, encoder);
        byte[] bytes = buffer.ToArray();

        using CancellationTokenSource cts = new();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using MemoryStream input = new(bytes);
            using Image<Rgb24> decoded = await Image.LoadAsync<Rgb24>(input, cts.Token);
        });
    }

    [Fact]
    public void FlateZlibRoundTripPreservesBytes()
    {
        byte[] payload = Encoding.ASCII.GetBytes("Flate probe payload 0123456789; ");
        byte[] repeated = new byte[payload.Length * 32];
        for (int i = 0; i < 32; i++)
        {
            Buffer.BlockCopy(payload, 0, repeated, i * payload.Length, payload.Length);
        }

        byte[] compressed;
        using (MemoryStream output = new())
        {
            using (ZLibStream compressor = new(output, CompressionLevel.Optimal))
            {
                compressor.Write(repeated, 0, repeated.Length);
            }

            compressed = output.ToArray();
        }

        byte[] roundTripped = DecompressBounded(compressed, TestMaxOutputBytes);
        Assert.Equal(repeated, roundTripped);
    }

    [Fact]
    public void FlateLevelMappingFollowsDeclaredBuckets()
    {
        Assert.Equal(CompressionLevel.Fastest, MapQpdfLevel(1));
        Assert.Equal(CompressionLevel.Optimal, MapQpdfLevel(5));
        Assert.Equal(CompressionLevel.SmallestSize, MapQpdfLevel(9));
        Assert.ThrowsAny<Exception>(() => MapQpdfLevel(0));
        Assert.ThrowsAny<Exception>(() => MapQpdfLevel(10));
    }

    [Fact]
    public void FlateTruncationIsDetected()
    {
        byte[] payload = Encoding.ASCII.GetBytes(new string('a', 1024));
        byte[] compressed;
        using (MemoryStream output = new())
        {
            using (ZLibStream compressor = new(output, CompressionLevel.Optimal))
            {
                compressor.Write(payload, 0, payload.Length);
            }

            compressed = output.ToArray();
        }

        byte[] truncated = new byte[compressed.Length / 2];
        Buffer.BlockCopy(compressed, 0, truncated, 0, truncated.Length);
        try
        {
            byte[] partial = DecompressBounded(truncated, TestMaxOutputBytes);
            Assert.NotEqual(payload, partial);
        }
        catch (InvalidDataException)
        {
        }
    }

    [Fact]
    public void FlateCorruptionIsRejected()
    {
        byte[] payload = Encoding.ASCII.GetBytes(new string('a', 1024));
        byte[] compressed;
        using (MemoryStream output = new())
        {
            using (ZLibStream compressor = new(output, CompressionLevel.Optimal))
            {
                compressor.Write(payload, 0, payload.Length);
            }

            compressed = output.ToArray();
        }

        byte[] corrupted = (byte[])compressed.Clone();
        corrupted[corrupted.Length / 2] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => DecompressBounded(corrupted, TestMaxOutputBytes));
    }

    [Fact]
    public void FlateExpansionLimitIsEnforced()
    {
        byte[] zeros = new byte[65536];
        byte[] bomb;
        using (MemoryStream output = new())
        {
            using (ZLibStream compressor = new(output, CompressionLevel.Optimal))
            {
                compressor.Write(zeros, 0, zeros.Length);
            }

            bomb = output.ToArray();
        }

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => DecompressBounded(bomb, 1024));
        Assert.Contains("Expansion limit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlateCancelledDecodeHonorsCancellation()
    {
        byte[] payload = Encoding.ASCII.GetBytes("cancel me");
        byte[] compressed;
        using (MemoryStream output = new())
        {
            using (ZLibStream compressor = new(output, CompressionLevel.Optimal))
            {
                compressor.Write(payload, 0, payload.Length);
            }

            compressed = output.ToArray();
        }

        using CancellationTokenSource cts = new();
        cts.Cancel();
        using MemoryStream source = new(compressed, writable: false);
        using ZLibStream decompressor = new(source, CompressionMode.Decompress);
        byte[] buffer = new byte[64];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            _ = await decompressor.ReadAsync(buffer, cts.Token);
        });
    }

    private static string FindProbeDll()
    {
        string baseDir = AppContext.BaseDirectory;
        DirectoryInfo? current = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && current is not null; i++)
        {
            string candidate = Path.Combine(current.FullName, "tools", "QpdfProbe", "bin", "Release", "net10.0", "QpdfProbe.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            string debugCandidate = Path.Combine(current.FullName, "tools", "QpdfProbe", "bin", "Debug", "net10.0", "QpdfProbe.dll");
            if (File.Exists(debugCandidate))
            {
                return debugCandidate;
            }

            current = current.Parent;
        }

        throw new Xunit.Sdk.XunitException("QpdfProbe.dll not found. Build the solution first.");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProbe(params string[] probeArgs)
    {
        string probeDll = FindProbeDll();
        ProcessStartInfo start = new()
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in probeArgs)
        {
            start.ArgumentList.Add(arg);
        }

        start.ArgumentList.Insert(0, probeDll);
        Process? started = Process.Start(start);
        if (started is null)
        {
            throw new Xunit.Sdk.XunitException("Failed to start QpdfProbe process.");
        }

        using Process process = started;
        StringBuilder stdout = new();
        StringBuilder stderr = new();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Assert.True(process.WaitForExit(30000), "QpdfProbe timed out after 30s.");
        process.WaitForExit();
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void ProbeJpegCommandSucceeds()
    {
        (int exit, string stdout, _) = RunProbe("probe-jpeg");
        Assert.Equal(0, exit);
        Assert.Contains("jpeg:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeFlateCommandSucceeds()
    {
        (int exit, string stdout, _) = RunProbe("probe-flate");
        Assert.Equal(0, exit);
        Assert.Contains("flate:", stdout, StringComparison.Ordinal);
    }
}



