using System.Diagnostics;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Reading;

namespace Lokad.Qpdf.Tests;

public sealed class ProbeCliTests
{
    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
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

        throw new Xunit.Sdk.XunitException(
            "QpdfProbe.dll not found. Build the solution first (dotnet build Lokad.Qpdf.slnx -c Release); the probe is required by integration tests and CI builds it explicitly.");
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

        // First argument to dotnet must be the probe dll.
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

    private static string WriteFakeQpdf(string directory, string versionOutput, string npagesOutput, int checkExit)
    {
        if (OperatingSystem.IsWindows())
        {
            string path = Path.Combine(directory, "fake-qpdf.cmd");
            string script = "@echo off\r\n"
                + "if \"%1\"==\"--version\" echo " + versionOutput + "\r\n"
                + "if \"%1\"==\"--version\" exit /b 0\r\n"
                + "if \"%1\"==\"--show-npages\" echo " + npagesOutput + "\r\n"
                + "if \"%1\"==\"--show-npages\" exit /b 0\r\n"
                + "if \"%1\"==\"--check\" exit /b " + checkExit + "\r\n"
                + "echo fake-qpdf: unknown args %*\r\n"
                + "exit /b 1\r\n";
            File.WriteAllText(path, script, Encoding.ASCII);
            return path;
        }
        else
        {
            string path = Path.Combine(directory, "fake-qpdf.sh");
            string script = "#!/bin/sh\n"
                + "if [ \"$1\" = \"--version\" ]; then echo \"" + versionOutput + "\"; exit 0; fi\n"
                + "if [ \"$1\" = \"--show-npages\" ]; then echo \"" + npagesOutput + "\"; exit 0; fi\n"
                + "if [ \"$1\" = \"--check\" ]; then exit " + checkExit + "; fi\n"
                + "echo \"fake-qpdf: unknown args $*\" 1>&2\n"
                + "exit 1\n";
            File.WriteAllText(path, script, Encoding.ASCII);
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch
            {
            }

            return path;
        }
    }

    [Fact]
    public void ProbeHelpSucceedsWithoutExternalTools()
    {
        (int exit, string stdout, _) = RunProbe("--help");
        Assert.Equal(0, exit);
        Assert.Contains("probe-oracle", stdout, StringComparison.Ordinal);
        Assert.Contains("validate", stdout, StringComparison.Ordinal);
        Assert.Contains("compare", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeValidateAcceptsMinimalOnePageFixture()
    {
        string input = Path.Combine(FixtureDir(), "minimal-1page.pdf");
        (int exit, string stdout, _) = RunProbe("validate", "--input", input);
        Assert.Equal(0, exit);
        Assert.Contains("npages=1", stdout, StringComparison.Ordinal);
        Assert.Contains("header-version=1.4", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeValidateRejectsTruncatedFixture()
    {
        string input = Path.Combine(FixtureDir(), "truncated-1page.pdf");
        (int exit, _, string stderr) = RunProbe("validate", "--input", input);
        Assert.NotEqual(0, exit);
        Assert.Contains("qpdfprobe:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeOracleFailsExplicitlyWhenExecutableIsMissing()
    {
        string input = Path.Combine(FixtureDir(), "minimal-1page.pdf");
        string missing = Path.Combine(Path.GetTempPath(), "qpdf-missing-" + Guid.NewGuid().ToString("N") + (OperatingSystem.IsWindows() ? ".exe" : ""));
        (int exit, _, string stderr) = RunProbe("probe-oracle", "--qpdf-exe", missing, "--input", input);
        Assert.NotEqual(0, exit);
        Assert.Contains("not found", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProbeCompareSucceedsWhenFakeOracleAgreesWithIndependent()
    {
        string input = Path.Combine(FixtureDir(), "minimal-1page.pdf");
        string tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "qpdfprobe-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string fake = WriteFakeQpdf(tempDir, "qpdf version 12.4.2", "1", 0);
            (int exit, string stdout, _) = RunProbe("compare", "--qpdf-exe", fake, "--input", input, "--expected-npages", "1");
            Assert.Equal(0, exit);
            Assert.Contains("compare: match", stdout, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void ProbeCompareDetectsDeliberatePageCountMismatch()
    {
        string input = Path.Combine(FixtureDir(), "minimal-1page.pdf");
        string tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "qpdfprobe-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string fake = WriteFakeQpdf(tempDir, "qpdf version 12.4.2", "1", 0);
            (int exit, _, string stderr) = RunProbe("compare", "--qpdf-exe", fake, "--input", input, "--expected-npages", "2");
            Assert.Equal(2, exit);
            Assert.Contains("mismatch", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void ProbeCompareRejectsWrongOracleVersion()
    {
        string input = Path.Combine(FixtureDir(), "minimal-1page.pdf");
        string tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "qpdfprobe-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string fake = WriteFakeQpdf(tempDir, "qpdf version 0.0.0", "1", 0);
            (int exit, _, string stderr) = RunProbe("compare", "--qpdf-exe", fake, "--input", input, "--expected-npages", "1");
            Assert.NotEqual(0, exit);
            Assert.Contains("version mismatch", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void ProbeEmitFailsExplicitlyOnMissingWorkdir()
    {
        string missing = Path.Combine(Path.GetTempPath(), "lokad-qpdf-no-such-workdir");
        (int exit, _, string stderr) = RunProbe("emit", "--workdir", missing, "--", "inputs/a.pdf", "--check");
        Assert.Equal(1, exit);
        Assert.Contains("Workdir not found", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeEmitPreservesCompleteBinaryInputAndPriorOutputOnEmptyInput(bool empty)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("qpdfprobe-input-");
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        try
        {
            byte[] payload = Enumerable.Range(0, 98304).Select(i => (byte)(i % 256)).ToArray();
            byte[] input = empty ? [] : TolerantObjectLoadingTests.Pdf(
                ["<< /Length 98304 >>\nstream\n" + Encoding.Latin1.GetString(payload) + "\nendstream"], false);
            string inputs = Directory.CreateDirectory(Path.Combine(directory.FullName, "inputs")).FullName;
            File.WriteAllBytes(Path.Combine(inputs, "a.pdf"), input);
            string target = Path.Combine(directory.FullName, "out.pdf");
            File.WriteAllBytes(target, "prior"u8.ToArray());
            (int exit, _, _) = RunProbe("emit", "--workdir", directory.FullName, "--out", "out.pdf=" + target,
                "--", "inputs/a.pdf", "--stream-data=uncompress", "out.pdf");
            byte[] output = File.ReadAllBytes(target);
            if (empty)
            {
                Assert.Equal(2, exit);
                Assert.Equal("prior"u8.ToArray(), output);
            }
            else
            {
                Assert.Equal(0, exit);
                using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(output);
                Assert.Equal(1, independent.NumberOfPages);
                InMemoryQpdfHost host = new();
                host.AddInput(QpdfPath.From("out.pdf"), output, QpdfInputOwnership.Copy);
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("out.pdf"), CancellationToken.None);
                PdfDictionary page = document.PageReferences[0].Page;
                Assert.True(page.TryGet(PdfName.FromAscii("Audit"), out QpdfObject audit));
                PdfStream stream = Assert.IsType<PdfStream>(audit is PdfReference reference ? document.GetObject(reference.ObjectNumber) : audit);
                Assert.Equal(payload, stream.Data);
            }
        }
        finally
        {
            Assert.StartsWith(tempRoot, Path.GetFullPath(directory.FullName), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(directory.FullName, true);
        }
    }

    [Fact]
    public void ProbeEmitJobFailsExplicitlyOnMissingWorkdir()
    {
        string missing = Path.Combine(Path.GetTempPath(), "lokad-qpdf-no-such-workdir");
        string job = Path.Combine(Path.GetTempPath(), "lokad-qpdf-empty-job.json");
        File.WriteAllText(job, "{}");
        (int exit, _, string stderr) = RunProbe("emit-job", "--workdir", missing, "--job", job);
        Assert.Equal(1, exit);
        Assert.Contains("Workdir not found", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeFixQdfFailsExplicitlyOnMissingWorkdir()
    {
        string missing = Path.Combine(Path.GetTempPath(), "lokad-qpdf-no-such-workdir");
        string physical = Path.Combine(Path.GetTempPath(), "lokad-qpdf-no-such-output.pdf");
        (int exit, _, string stderr) = RunProbe("fix-qdf", "--workdir", missing, "--in", "inputs/a.pdf", "--out", "outputs/b.pdf=" + physical);
        Assert.Equal(1, exit);
        Assert.Contains("Workdir not found", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeZlibFlateFailsExplicitlyOnMissingWorkdir()
    {
        string missing = Path.Combine(Path.GetTempPath(), "lokad-qpdf-no-such-workdir");
        string physical = Path.Combine(Path.GetTempPath(), "lokad-qpdf-no-such-output.bin");
        (int exit, _, string stderr) = RunProbe("zlib-flate", "--workdir", missing, "--in", "inputs/a.bin", "--out", "outputs/b.bin=" + physical, "--mode", "compress");
        Assert.Equal(1, exit);
        Assert.Contains("Workdir not found", stderr, StringComparison.Ordinal);
    }}
