using System.Diagnostics;
using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Image;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Filters;

namespace Lokad.Qpdf.Tests;

public sealed class CodecProbeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void JpegSampleRoundTripPreservesDimensions(int components)
    {
        byte[] jpeg = JpegFixtures.Encode(8, 8, components, 75, (_, _, _) => 128);
        byte[] samples = new byte[8 * 8 * components];
        Lokad.Image.ImageInfo info = Lokad.Image.ImageCodec.DecodeJpeg(jpeg, samples,
            (Lokad.Image.JpegSampleFormat)components);
        Assert.Equal(8, info.Width); Assert.Equal(8, info.Height);
        Assert.Equal(components, info.SourceComponentCount);
        Assert.Equal(-1, samples.AsSpan().IndexOfAnyExcept((byte)128));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void PinnedEncoderRejectsOutOfRangeQuality(int quality)
    {
        using MemoryStream output = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => Lokad.Image.ImageCodec.EncodeJpeg(
            new byte[64], 8, 8, Lokad.Image.JpegSampleFormat.Grayscale, output,
            options: new Lokad.Image.JpegEncodeOptions { Quality = quality }));
    }

    [Fact]
    public void UpstreamCmykSamplesDoNotExpandThePdfPolicy()
    {
        byte[] jpeg = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cmyk-8x8.jpg"));
        byte[] samples = new byte[8 * 8 * 4];
        Lokad.Image.ImageInfo info = Lokad.Image.ImageCodec.DecodeJpeg(jpeg, samples, Lokad.Image.JpegSampleFormat.Cmyk);
        Assert.Equal(8, info.Width); Assert.Equal(8, info.Height);
        Assert.Equal(4, info.SourceComponentCount);
        QpdfFilterException failure = Assert.Throws<QpdfFilterException>(() => DctFilter.Decode(jpeg, QpdfLimits.Default,
            new ResourceGovernor(QpdfLimits.Default, CancellationToken.None) { JpegCodec = LokadImageJpegCodec.Instance }));
        Assert.Equal(QpdfFilterFailureReason.UnsupportedFilter, failure.Reason);
    }

    [Fact]
    public void JpegDecodeReservesCodecWorkingSet()
    {
        byte[] jpeg = DctDecodeTests.EncodeRgbJpeg(8, 8);
        long frame = 8L * 8L * 3L;
        QpdfLimits tight = QpdfLimits.Default with { MaxMemoryBytes = frame };
        Assert.Throws<QpdfLimitException>(() => DctFilter.Decode(jpeg, tight,
            new ResourceGovernor(tight, CancellationToken.None) { JpegCodec = LokadImageJpegCodec.Instance }));
        QpdfLimits roomy = QpdfLimits.Default with { MaxMemoryBytes = 256L * 1024L };
        byte[] decoded = DctFilter.Decode(jpeg, roomy,
            new ResourceGovernor(roomy, CancellationToken.None) { JpegCodec = LokadImageJpegCodec.Instance });
        Assert.Equal(frame, decoded.Length);
    }

    [Fact]
    public void JpegEncodeReservesCodecWorkingSet()
    {
        byte[] raw = new byte[8 * 8 * 3];
        QpdfLimits tight = QpdfLimits.Default with { MaxMemoryBytes = raw.Length };
        Assert.Throws<QpdfLimitException>(() => JpegCodec.EncodeRgb(raw, 8, 8, 75, tight,
            new ResourceGovernor(tight, CancellationToken.None) { JpegCodec = LokadImageJpegCodec.Instance }));
        QpdfLimits roomy = QpdfLimits.Default with { MaxMemoryBytes = 256L * 1024L };
        byte[] encoded = JpegCodec.EncodeRgb(raw, 8, 8, 75, roomy,
            new ResourceGovernor(roomy, CancellationToken.None) { JpegCodec = LokadImageJpegCodec.Instance });
        Assert.NotEmpty(encoded);
    }

    [Fact]
    public void CancelledEncodeFailsFast()
    {
        using CancellationTokenSource source = new(); source.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, source.Token);
        Assert.Throws<OperationCanceledException>(() => JpegCodec.EncodeRgb(new byte[8 * 8 * 3], 8, 8, 75, QpdfLimits.Default, governor));
    }

    [Fact]
    public void JpegCancelledDecodeHonorsCancellation()
    {
        byte[] jpeg = DctDecodeTests.EncodeRgbJpeg(8, 8);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Lokad.Image.ImageCodec.DecodeJpeg(
            jpeg, new byte[8 * 8 * 3], Lokad.Image.JpegSampleFormat.Rgb, cancellationToken: cancellation.Token));
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
