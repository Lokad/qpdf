using Lokad.Qpdf;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Probe;

// Runs the managed zlib-flate companion over workdir files through the
// public API only: every file under <workdir>/inputs becomes a logical
// input named by its relative path, one logical input is compressed or
// uncompressed, and the mapped logical output is written back to disk.
internal static class ZlibFlateCommand
{
    internal static int RunZlibFlate(string[] args)
    {
        string workdir = GetOption(args, "--workdir");
        string input = GetOption(args, "--in");
        string mapping = GetOption(args, "--out");
        string modeText = GetOption(args, "--mode");
        QpdfZlibFlateMode mode;
        if (string.Equals(modeText, "compress", StringComparison.Ordinal))
        {
            mode = QpdfZlibFlateMode.Compress;
        }
        else if (string.Equals(modeText, "uncompress", StringComparison.Ordinal))
        {
            mode = QpdfZlibFlateMode.Uncompress;
        }
        else
        {
            throw new ProbeException("Invalid --mode (" + modeText + "). Expected compress or uncompress.", 1);
        }

        int? level = null;
        foreach (string candidate in GetAllOptions(args, "--level"))
        {
            if (!int.TryParse(candidate, out int parsed))
            {
                throw new ProbeException("Invalid --level (" + candidate + "). Expected 1..9.", 1);
            }

            level = parsed;
        }

        if (!Directory.Exists(workdir))
        {
            throw new ProbeException("Workdir not found: " + workdir + ".", 1);
        }

        string inputsDir = Path.Combine(workdir, "inputs");
        if (!Directory.Exists(inputsDir))
        {
            throw new ProbeException("Inputs directory not found: " + inputsDir + ".", 1);
        }

        InMemoryQpdfHost host = new();
        foreach (string file in Directory.EnumerateFiles(inputsDir, "*", SearchOption.AllDirectories))
        {
            string logical = "inputs/" + Path.GetRelativePath(inputsDir, file).Replace(Path.DirectorySeparatorChar, '/');
            host.AddInput(QpdfPath.From(logical), File.ReadAllBytes(file), QpdfInputOwnership.Copy);
        }

        int equals = mapping.IndexOf('=');
        if (equals <= 0 || equals + 1 >= mapping.Length)
        {
            throw new ProbeException("Invalid --out mapping (" + mapping + "). Expected <logical>=<physical>.", 1);
        }

        string logicalOut = mapping.Substring(0, equals);
        string physical = mapping.Substring(equals + 1);
        QpdfZlibFlateOptions options = new(mode, level);
        QpdfExecutionResult result = Qpdf.RunZlibFlateAsync(host, QpdfPath.From(input), QpdfPath.From(logicalOut), options, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine("zlibflate: status=" + result.Status + " exit=" + (int)result.ExitCode);
        foreach (QpdfDiagnostic diagnostic in result.Diagnostics)
        {
            Console.WriteLine("zlibflate: diagnostic " + diagnostic.Code + " " + diagnostic.Severity + " " + diagnostic.Message);
        }

        if (!host.TryGetOutput(QpdfPath.From(logicalOut), out byte[] content))
        {
            throw new ProbeException("Library produced no output (" + logicalOut + "). status=" + result.Status + " exit=" + (int)result.ExitCode + ".", 2);
        }

        string directory = Path.GetDirectoryName(physical) ?? string.Empty;
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(physical, content);
        Console.WriteLine("zlibflate: output " + logicalOut + " " + content.Length + " bytes -> " + physical);
        Console.WriteLine("zlibflate: counters work=" + result.Counters.TotalWork + " decoded=" + result.Counters.TotalDecodedBytes + " peakmem=" + result.Counters.PeakMemoryBytes);
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
                    throw new ProbeException("Missing value for " + name + ".", 1);
                }

                return args[i + 1];
            }
        }

        throw new ProbeException("Missing required option " + name + ".", 1);
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
