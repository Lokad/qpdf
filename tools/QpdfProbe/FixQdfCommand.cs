using Lokad.Qpdf.Image;
using Lokad.Qpdf;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Probe;

// Runs the managed fix-qdf companion over workdir files through the public
// API only, acquiring requested logical inputs and exporting the mapped output.
internal static class FixQdfCommand
{
    internal static int RunFixQdf(string[] args)
    {
        string workdir = GetOption(args, "--workdir");
        string input = GetOption(args, "--in");
        string mapping = GetOption(args, "--out");
        Dictionary<string, string> available = ProbeInputFiles.Index(workdir);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, LokadImageJpegCodec.Instance);
        IQpdfHost executionHost = new ProbeInputFiles.Requested(host, available);
        ProbeOutput output = ProbeOutput.ParseCompanion(mapping);
        QpdfExecutionResult result = Qpdf.FixQdfAsync(executionHost, QpdfPath.From(input), QpdfPath.From(output.Logical), CancellationToken.None).GetAwaiter().GetResult();
        return output.CompleteCompanion(host, result, "fixqdf");
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
}
