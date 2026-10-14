using Lokad.Qpdf;
using Lokad.Qpdf.Hosting;
using Microsoft.Win32.SafeHandles;

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
        Dictionary<string, string> available = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(inputsDir, "*", SearchOption.AllDirectories))
        {
            string logical = "inputs/" + Path.GetRelativePath(inputsDir, file).Replace(Path.DirectorySeparatorChar, '/');
            available.Add(logical, file);
        }

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
            { host.AddInput(QpdfPath.From(logical), ReadInputBytes(file), QpdfInputOwnership.Transfer); }
            executionHost = host;
        }
        else { executionHost = new InputAliases(host, available); }
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

        return result.Status == QpdfExecutionStatus.Completed && (!requireSuccess || result.ExitCode == QpdfExitCode.Success) ? 0 : 2;
    }

    // Development host policy: ./ names share the canonical input lease identity.
    // The library still receives the original filename key in its operation plan.
    private sealed class InputAliases(InMemoryQpdfHost host, Dictionary<string, string> available) : IQpdfHost
    {
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = path.Value;
            while (name.StartsWith("./", StringComparison.Ordinal)) { name = name[2..]; }
            if (available.Remove(name, out string? file) && file is not null)
            {
                // This fresh file array has no caller alias. The host owns it;
                // cloning it again provides no additional isolation.
                host.AddInput(QpdfPath.From(name), ReadInputBytes(file), QpdfInputOwnership.Transfer);
            }
            return host.OpenInputAsync(QpdfPath.From(name), cancellationToken);
        }

        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
            => host.CreateOutputAsync(path, cancellationToken);
    }

    internal static byte[] ReadInputBytes(string path)
    {
        FileOptions options = OperatingSystem.IsWindows() ? FileOptions.SequentialScan : FileOptions.None;
        using SafeFileHandle input = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, options);
        long length;
        try { length = RandomAccess.GetLength(input); }
        catch (NotSupportedException) { return ReadUnknownInputBytes(input); }
        if (length == 0L) { return ReadUnknownInputBytes(input); }
        if (length > Array.MaxLength) { throw new IOException("Input exceeds the supported buffer size."); }
        // Fill every byte before ownership transfers; premature EOF fails here.
        // The complete fresh file read remains inside each measured public job.
        byte[] bytes = GC.AllocateUninitializedArray<byte>((int)length);
        int filled = 0;
        while (filled < bytes.Length)
        {
            int read = RandomAccess.Read(input, bytes.AsSpan(filled), filled);
            if (read == 0) { throw new EndOfStreamException("Input ended before its declared length."); }
            filled += read;
        }
        return bytes;
    }

    private static byte[] ReadUnknownInputBytes(SafeFileHandle input)
    {
        // An unknown length, including procfs, need not mean an empty file.
        using FileStream stream = new(input, FileAccess.Read);
        using MemoryStream storage = new();
        stream.CopyTo(storage);
        return storage.ToArray();
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
