using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Lokad.Qpdf.Probe;

internal sealed record OracleResult(
    string ExePath,
    string VersionOutput,
    int Npages,
    int CheckExitCode,
    string InputSha256);

internal static class OracleRunner
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    internal static OracleResult Run(string qpdfExe, string inputPdf, string expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(qpdfExe))
        {
            throw new ProbeException("Missing qpdf executable path. Supply --qpdf-exe <path> to the pinned qpdf 12.4.2 executable.", 1);
        }

        if (!File.Exists(qpdfExe))
        {
            throw new ProbeException($"qpdf executable not found: {qpdfExe}. Bootstrap the pinned 12.4.2 build (see tools/bootstrap-qpdf.ps1); this is an explicit failure, not a skipped check.", 1);
        }

        if (!File.Exists(inputPdf))
        {
            throw new ProbeException($"Input PDF not found: {inputPdf}.", 1);
        }

        string inputSha = ComputeSha256(inputPdf);
        string versionOutput = Invoke(qpdfExe, "--version", "version");
        string versionLine = versionOutput.Split(['\r', '\n'], 2)[0].Trim();
        if (!string.Equals(versionLine, $"qpdf version {expectedVersion}", StringComparison.Ordinal))
        {
            throw new ProbeException($"qpdf version mismatch. Expected 'qpdf version {expectedVersion}', got: {SingleLine(versionOutput)}.", 1);
        }

        string npagesOutput = Invoke(qpdfExe, $"--show-npages \"{inputPdf}\"", "show-npages");
        int npages = ParseNpages(npagesOutput);

        int checkExit = InvokeCheck(qpdfExe, inputPdf);

        return new OracleResult(qpdfExe, versionOutput, npages, checkExit, inputSha);
    }

    private static int ParseNpages(string output)
    {
        string trimmed = output.Trim();
        if (!int.TryParse(trimmed, out int npages) || npages <= 0)
        {
            throw new ProbeException($"Unable to parse `qpdf --show-npages` output as a positive page count: '{SingleLine(output)}'.", 1);
        }

        return npages;
    }

    private static int InvokeCheck(string qpdfExe, string inputPdf)
    {
        (int exitCode, string stdout, string stderr) = RunProcess(qpdfExe, $"--check \"{inputPdf}\"");
        if (exitCode != 0)
        {
            throw new ProbeException($"`qpdf --check` failed with exit {exitCode}. stdout: {SingleLine(stdout)} stderr: {SingleLine(stderr)}", 1);
        }

        return exitCode;
    }

    private static string Invoke(string qpdfExe, string arguments, string operation)
    {
        (int exitCode, string stdout, string stderr) = RunProcess(qpdfExe, arguments);
        if (exitCode != 0)
        {
            throw new ProbeException($"`qpdf {operation}` failed with exit {exitCode}. stdout: {SingleLine(stdout)} stderr: {SingleLine(stderr)}", 1);
        }

        return stdout;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(string exe, string arguments)
    {
        ProcessStartInfo start = new()
        {
            FileName = exe,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using Process process = new()
        {
            StartInfo = start,
        };

        StringBuilder stdout = new();
        StringBuilder stderr = new();

        try
        {
            if (!process.Start())
            {
                throw new ProbeException($"Unable to start qpdf process: {exe} {arguments}.", 1);
            }
        }
        catch (Exception ex) when (ex is not ProbeException)
        {
            throw new ProbeException($"Unable to start qpdf executable '{exe}': {ex.Message}.", 1);
        }

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

        bool exited = process.WaitForExit(CommandTimeout);
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new ProbeException($"qpdf process timed out after {(int)CommandTimeout.TotalSeconds}s: {exe} {arguments}.", 1);
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SingleLine(string value)
    {
        string trimmed = value.Trim();
        int newline = trimmed.IndexOf('\n');
        if (newline >= 0)
        {
            trimmed = trimmed[..newline];
        }

        newline = trimmed.IndexOf('\r');
        if (newline >= 0)
        {
            trimmed = trimmed[..newline];
        }

        return trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }
}
