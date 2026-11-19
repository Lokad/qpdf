using System.Diagnostics;
using System.Text;

namespace Lokad.Qpdf.Tests;

internal static class ProbeTestProcess
{
    internal static string AssemblyPath => Path.Combine(AppContext.BaseDirectory, "QpdfProbe.dll");

    internal static (int ExitCode, string Stdout, string Stderr) Run(string? input, params string[] args)
        => RunAsync(input, args).GetAwaiter().GetResult();

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string? input, string[] args)
    {
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add(AssemblyPath);
        foreach (string arg in args) { start.ArgumentList.Add(arg); }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start the probe from the test build.");
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        CancellationToken token = deadline.Token;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(token);
        Task exited = process.WaitForExitAsync(token);
        Task sent = input is null ? Task.CompletedTask : SendInputAsync(process, input, token);
        try
        {
            await Task.WhenAll(stdout, stderr, exited, sent).ConfigureAwait(false);
            return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("QpdfProbe timed out after 30 seconds.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
    }

    private static async Task SendInputAsync(Process process, string input, CancellationToken token)
    {
        StreamWriter writer = process.StandardInput;
        await writer.WriteAsync(input.AsMemory(), token).ConfigureAwait(false);
        await writer.FlushAsync(token).ConfigureAwait(false);
        writer.Close();
    }
}
