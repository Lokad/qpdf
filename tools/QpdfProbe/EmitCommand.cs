using Lokad.Qpdf;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Probe;

// Runs the managed library over workdir files through the public API only:
// every file under <workdir>/inputs becomes a logical input named by its
// relative path, argv (or a job file) executes, and mapped logical outputs
// are written back to disk. Lets reference scripts compare managed output
// against the pinned oracle byte-for-byte at the validation level.
internal static class EmitCommand
{
    internal static int RunEmit(string[] args)
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

        return Execute(workdir, outs, null, argv);
    }

    internal static int RunEmitJob(string[] args)
    {
        string workdir = GetOption(args, "--workdir");
        string jobFile = GetOption(args, "--job");
        List<string> outs = GetAllOptions(args, "--out");
        string jobJson = File.ReadAllText(jobFile);
        return Execute(workdir, outs, jobJson, null);
    }

    private static int Execute(string workdir, List<string> outs, string? jobJson, List<string>? argv)
    {
        if (!Directory.Exists(workdir))
        {
            throw new ProbeException($"Workdir not found: {workdir}.", 1);
        }

        string inputsDir = Path.Combine(workdir, "inputs");
        if (!Directory.Exists(inputsDir))
        {
            throw new ProbeException($"Inputs directory not found: {inputsDir}.", 1);
        }

        InMemoryQpdfHost host = new();
        foreach (string file in Directory.EnumerateFiles(inputsDir, "*", SearchOption.AllDirectories))
        {
            string logical = "inputs/" + Path.GetRelativePath(inputsDir, file).Replace(Path.DirectorySeparatorChar, '/');
            host.AddInput(QpdfPath.From(logical), File.ReadAllBytes(file), QpdfInputOwnership.Copy);
        }

        QpdfExecutionResult result;
        if (jobJson is not null)
        {
            result = Qpdf.ExecuteJobAsync(host, jobJson, CancellationToken.None).GetAwaiter().GetResult();
        }
        else if (argv is not null)
        {
            result = Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None).GetAwaiter().GetResult();
        }
        else
        {
            throw new ProbeException("Internal error: neither job nor argv supplied.", 1);
        }

        Console.WriteLine($"emit: status={result.Status} exit={(int)result.ExitCode}");
        foreach (QpdfDiagnostic diagnostic in result.Diagnostics)
        {
            Console.WriteLine($"emit: diagnostic {diagnostic.Code} {diagnostic.Severity} {diagnostic.Message}");
        }

        foreach (string mapping in outs)
        {
            int equals = mapping.IndexOf('=');
            if (equals <= 0 || equals + 1 >= mapping.Length)
            {
                throw new ProbeException($"Invalid --out mapping '{mapping}'. Expected <logical>=<physical>.", 1);
            }

            string logical = mapping[..equals];
            string physical = mapping[(equals + 1)..];
            if (!host.TryGetOutput(QpdfPath.From(logical), out byte[] content))
            {
                throw new ProbeException($"Library produced no output '{logical}'. status={result.Status} exit={(int)result.ExitCode}.", 2);
            }

            string? directory = Path.GetDirectoryName(physical);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(physical, content);
            Console.WriteLine($"emit: output {logical} {content.Length} bytes -> {physical}");
        }

        Console.WriteLine($"emit: counters work={result.Counters.TotalWork} decoded={result.Counters.TotalDecodedBytes} peakmem={result.Counters.PeakMemoryBytes}");
        if (result.Status == QpdfExecutionStatus.Canceled)
        {
            return 4;
        }

        return result.Status == QpdfExecutionStatus.Completed ? 0 : 2;
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