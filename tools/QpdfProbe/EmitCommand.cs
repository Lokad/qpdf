using Lokad.Qpdf.Image;
using Lokad.Qpdf;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Probe;

// Runs the managed library over workdir files through the public API only:
// every file under <workdir>/inputs is available by its relative logical name.
// Requested inputs are registered lazily in a fresh in-memory host; argv (or
// a job file) executes, and mapped logical outputs
// are written back to disk. Lets reference scripts compare managed output
// against the pinned oracle byte-for-byte at the validation level.
internal static class EmitCommand
{
    internal static int RunEmit(string[] args)
        => RunEmit(args, false);

    internal static int RunWarmEmit(string[] args)
        => RunEmit(args, true);

    private static int RunEmit(string[] args, bool requireSuccess)
    {
        string workdir = GetOption(args, "--workdir");
        List<string> outs = GetAllOptions(args, "--out");
        int sep = Array.IndexOf(args, "--");
        if (sep < 0 || sep + 1 >= args.Length)
        {
            throw new ProbeException("emit requires '-- <argv...>' with at least one argument.", 1);
        }

        List<string> argv = new();
        for (int i = sep + 1; i < args.Length; i++)
        {
            argv.Add(args[i]);
        }

        return Execute(workdir, outs, null, argv, requireSuccess, args.AsSpan(0, sep).Contains("--file-input"));
    }

    internal static int RunEmitJob(string[] args)
        => RunEmitJob(args, false);

    internal static int RunWarmEmitJob(string[] args)
        => RunEmitJob(args, true);

    private static int RunEmitJob(string[] args, bool requireSuccess)
    {
        string workdir = GetOption(args, "--workdir");
        string jobFile = GetOption(args, "--job");
        List<string> outs = GetAllOptions(args, "--out");
        string jobJson = File.ReadAllText(jobFile);
        return Execute(workdir, outs, jobJson, null, requireSuccess, args.Contains("--file-input", StringComparer.Ordinal));
    }

    private static int Execute(string workdir, List<string> outs, string? jobJson, List<string>? argv, bool requireSuccess, bool fileInput)
    {
        Dictionary<string, string> available = ProbeInputFiles.Index(workdir);
        InMemoryQpdfHost host = new(QpdfLimits.Default, LokadImageJpegCodec.Instance);
        // Reference calls own storage here; warmed calls keep their
        // established acquisition and measurement lifetime.
        using InMemoryQpdfHost? referenceStorage = requireSuccess ? null : host;

        QpdfExecutionResult result;
        IQpdfHost executionHost;
        if (fileInput) { executionHost = new FileSelectionCommand.FileHost(host, available); }
        else if (requireSuccess)
        {
            // Warmed workloads contain exactly their required inputs under
            // canonical logical names. Keep file reads and owned-buffer transfer
            // inside the timed emit call, using the consuming memory host itself.
            // The reference adapter below still supports lazy filename aliases.
            foreach ((string logical, string file) in available)
            { host.AddInput(QpdfPath.From(logical), ProbeInputFiles.ReadInputBytes(file), QpdfInputOwnership.Transfer); }
            executionHost = host;
        }
        else { executionHost = new ProbeInputFiles.Requested(host, available); }
        if (jobJson is not null)
        {
            result = Qpdf.ExecuteJobAsync(executionHost, jobJson, CancellationToken.None).GetAwaiter().GetResult();
        }
        else if (argv is not null)
        {
            result = Qpdf.ExecuteAsync(executionHost, new QpdfCommandInvocation(argv), CancellationToken.None).GetAwaiter().GetResult();
        }
        else
        {
            throw new ProbeException("Internal error: neither job nor argv supplied.", 1);
        }

        Console.WriteLine($"emit: status={result.Status} exit={(int)result.ExitCode}");
        if (result.OutputText is not null)
        {
            Console.WriteLine("emit: text " + System.Text.Json.JsonSerializer.Serialize(result.OutputText));
        }
        if (result.OutputBytes is ReadOnlyMemory<byte> bytes)
        {
            Console.WriteLine("emit: bytes " + Convert.ToHexString(bytes.Span));
        }
        foreach (QpdfDiagnostic diagnostic in result.Diagnostics)
        {
            Console.WriteLine($"emit: diagnostic {diagnostic.Code} {diagnostic.Severity} {diagnostic.Message}");
        }

        foreach (string mapping in outs)
        {
            if (!ProbeOutput.TryParse(mapping, out ProbeOutput output))
            { throw new ProbeException($"Invalid --out mapping '{mapping}'. Expected <logical>=<physical>.", 1); }
            string logical = output.Logical;
            if (!host.TryGetOutput(QpdfPath.From(logical), out byte[] content))
            {
                throw new ProbeException($"Library produced no output '{logical}'. status={result.Status} exit={(int)result.ExitCode}.", 2);
            }

            output.Export(content, "emit");
        }

        Console.WriteLine($"emit: counters decoded={result.Counters.TotalDecodedBytes} peakmem={result.Counters.PeakMemoryBytes}");
        if (result.Status == QpdfExecutionStatus.Canceled)
        {
            return 4;
        }

        return result.Status == QpdfExecutionStatus.Completed && (!requireSuccess || result.ExitCode == QpdfExitCode.Success) ? 0 : 2;
    }

    private static string GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ProbeException($"Missing value for {name}.", 1);
                }

                return args[i + 1];
            }
        }

        throw new ProbeException($"Missing required option {name}.", 1);
    }

    private static List<string> GetAllOptions(string[] args, string name)
    {
        List<string> values = new();
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                values.Add(args[i + 1]);
            }
        }

        return values;
    }
}
