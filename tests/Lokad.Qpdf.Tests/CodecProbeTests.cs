using System.Diagnostics;
using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.ImageSharp;
using Lokad.Qpdf.Runtime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Lokad.Qpdf.Tests;

public sealed class CodecProbeTests
{
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
    public void PinnedEncoderRejectsOutOfRangeQuality()
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
    }

    [Fact]
    public void PinnedCodecCanReadCmykLabeledJpegAsRgb()
    {
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
            () => DctFilter.Decode(bytes, tight, new ResourceGovernor(tight, CancellationToken.None) { JpegCodec = ImageSharpJpegCodec.Instance }));
        QpdfLimits roomy = QpdfLimits.Default with { MaxMemoryBytes = 256L * 1024L };
        byte[] decoded = DctFilter.Decode(bytes, roomy, new ResourceGovernor(roomy, CancellationToken.None) { JpegCodec = ImageSharpJpegCodec.Instance });
        Assert.Equal(frame, decoded.Length);
    }

    [Fact]
    public void JpegEncodeReservesCodecWorkingSet()
    {
        byte[] raw = new byte[8 * 8 * 3];
        long frame = 8L * 8L * 3L;
        QpdfLimits tight = QpdfLimits.Default with { MaxMemoryBytes = frame };
        Assert.Throws<QpdfLimitException>(
            () => JpegCodec.EncodeRgb(raw, 8, 8, 75, tight, new ResourceGovernor(tight, CancellationToken.None) { JpegCodec = ImageSharpJpegCodec.Instance }));
        QpdfLimits roomy = QpdfLimits.Default with { MaxMemoryBytes = 256L * 1024L };
        byte[] encoded = JpegCodec.EncodeRgb(raw, 8, 8, 75, roomy, new ResourceGovernor(roomy, CancellationToken.None) { JpegCodec = ImageSharpJpegCodec.Instance });
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
}
