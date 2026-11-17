using Lokad.Qpdf.Image;
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

        Dictionary<string, string> available = ProbeInputFiles.Index(workdir);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, LokadImageJpegCodec.Instance);
        IQpdfHost executionHost = new ProbeInputFiles.Requested(host, available);
        ProbeOutput output = ProbeOutput.ParseCompanion(mapping);
        QpdfZlibFlateOptions options = new(mode, level);
        QpdfExecutionResult result = Qpdf.RunZlibFlateAsync(executionHost, QpdfPath.From(input), QpdfPath.From(output.Logical), options, CancellationToken.None).GetAwaiter().GetResult();
        return output.CompleteCompanion(host, result, "zlibflate");
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
