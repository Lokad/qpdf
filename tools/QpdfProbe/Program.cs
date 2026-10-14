using System.Security.Cryptography;

namespace Lokad.Qpdf.Probe;

internal static class Program
{
    private const string ExpectedQpdfVersion = "12.4.2";
    private static bool _consoleEncodingInitialized;

    internal static int Main(string[] args)
    {
        if (!_consoleEncodingInitialized)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            _consoleEncodingInitialized = true;
        }
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "probe-oracle" => ProbeOracle(args[1..]),
                "validate" => Validate(args[1..]),
                "page-text" => PageText(args[1..]),
                "compare" => Compare(args[1..]),
                "compare-raster" => RasterComparison.Run(args[1..]),
                "emit" => EmitCommand.RunEmit(args[1..]),
                "emit-job" => EmitCommand.RunEmitJob(args[1..]),
                "batch" => BatchCommand.Run(args[1..]),
                "warm" => WarmCommand.Run(args[1..]),
                "select-file" => FileSelectionCommand.Run(args[1..]),
                "fix-qdf" => FixQdfCommand.RunFixQdf(args[1..]),
                "zlib-flate" => ZlibFlateCommand.RunZlibFlate(args[1..]),
                "probe-jpeg" => ProbeJpeg(),
                "probe-flate" => ProbeFlate(),
                _ => Fail($"Unknown command '{args[0]}'. Use --help for usage."),
            };
        }
        catch (ProbeException ex)
        {
            Console.Error.WriteLine($"qpdfprobe: {ex.Message}");
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"qpdfprobe: unexpected failure: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static bool IsHelp(string value)
    {
        return value == "--help" || value == "-h" || value == "help";
    }

    private static void PrintHelp()
    {
        Console.WriteLine("QpdfProbe 0.1.0: developer-only qpdf oracle and independent validation runner.");
        Console.WriteLine("Not part of the shipped Lokad.qpdf package. Requires an explicit qpdf 12.4.2 executable for oracle commands.");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  QpdfProbe --help");
        Console.WriteLine("  QpdfProbe probe-oracle --qpdf-exe <path> --input <pdf>");
        Console.WriteLine("  QpdfProbe validate --input <pdf>");
        Console.WriteLine("  QpdfProbe page-text --input <pdf> (independent PdfPig page text, JSON array)");
        Console.WriteLine("  QpdfProbe compare --qpdf-exe <path> --input <pdf> --expected-npages <N>");
        Console.WriteLine("  QpdfProbe compare-raster --left <ppm> --right <ppm> --expected-width <1..4096> --expected-height <1..4096> --max-mean-error <0..1> --max-changed-fraction <0..1> --channel-threshold <0..255>");
        Console.WriteLine("  QpdfProbe emit --workdir <dir> --out <logical>=<physical> [--out ...] -- <argv...>");
        Console.WriteLine("  QpdfProbe emit-job --workdir <dir> --job <job.json> --out <logical>=<physical> [--out ...]");
        Console.WriteLine("  QpdfProbe batch (bounded sequential JSON requests on stdin; developer harness only)");
        Console.WriteLine("  QpdfProbe warm (bounded sequential emit requests; internal job timing, no process startup)");
        Console.WriteLine("  QpdfProbe select-file <input.pdf> <output.pdf> (additional typed first-page selection through a file host)");
        Console.WriteLine("    read-input <file> requests measure fresh full-input loading only; length/hash validation is outside timing.");
        Console.WriteLine("  QpdfProbe fix-qdf --workdir <dir> --in <logical-input> --out <logical>=<physical>");
        Console.WriteLine("  QpdfProbe zlib-flate --workdir <dir> --in <logical-input> --out <logical>=<physical> --mode <compress|uncompress> [--level 1..9]");
        Console.WriteLine("  QpdfProbe probe-jpeg");
        Console.WriteLine("  QpdfProbe probe-flate");
        Console.WriteLine();
        Console.WriteLine("probe-oracle runs: qpdf --version (must contain 12.4.2), qpdf --show-npages, qpdf --check.");
        Console.WriteLine("validate opens the PDF with the pinned independent reader (PdfPig 0.1.16) and reports page count, header version and SHA-256.");
        Console.WriteLine("compare requires oracle npages, independent npages and --expected-npages to agree; mismatch exits 2.");
        Console.WriteLine("emit runs the managed library over <dir>/inputs through the public API and writes mapped outputs to disk.");
        Console.WriteLine("validate --allow-empty explicitly accepts an independently observed zero-page document.");
    Console.WriteLine("probe-jpeg exercises the pinned ImageSharp 3.1.12 JPEG codec (grayscale/RGB round-trips, quality endpoints, CMYK/YCCK gap, bounds, cancellation).");
        Console.WriteLine("probe-flate exercises the governed BCL Flate pipeline (zlib round-trip, level mapping, truncation, expansion limit, cancellation).");
        Console.WriteLine("Missing executables or inputs are explicit failures, never silent skips.");
    }

    private static int ProbeJpeg()
    {
        JpegProbe.Run();
        return 0;
    }

    private static int ProbeFlate()
    {
        FlateProbe.RunAsync().GetAwaiter().GetResult();
        return 0;
    }

    private static int ProbeOracle(string[] args)
    {
        string qpdfExe = GetOption(args, "--qpdf-exe");
        string input = GetOption(args, "--input");
        OracleResult result = OracleRunner.Run(qpdfExe, input, ExpectedQpdfVersion);
        Console.WriteLine($"oracle: exe={result.ExePath}");
        Console.WriteLine($"oracle: version-ok={ExpectedQpdfVersion}");
        Console.WriteLine($"oracle: version-output={SingleLine(result.VersionOutput)}");
        Console.WriteLine($"oracle: input-sha256={result.InputSha256}");
        Console.WriteLine($"oracle: npages={result.Npages}");
        Console.WriteLine($"oracle: check-exit={result.CheckExitCode}");
        return 0;
    }

    private static int Validate(string[] args)
    {
        string input = GetOption(args, "--input");
        IndependentResult result = IndependentReader.Read(input, Array.IndexOf(args, "--allow-empty") >= 0);
        Console.WriteLine($"independent: input-sha256={result.InputSha256}");
        Console.WriteLine($"independent: header-version={result.HeaderVersion}");
        Console.WriteLine($"independent: npages={result.Npages}");
        Console.WriteLine("independent: reader=PdfPig 0.1.16");
        return 0;
    }

    private static int PageText(string[] args)
    {
        IndependentReader.WritePageText(GetOption(args, "--input"));
        return 0;
    }

    private static int Compare(string[] args)
    {
        string qpdfExe = GetOption(args, "--qpdf-exe");
        string input = GetOption(args, "--input");
        string expectedText = GetOption(args, "--expected-npages");
        if (!int.TryParse(expectedText, out int expected) || expected <= 0)
        {
            throw new ProbeException($"Invalid --expected-npages '{expectedText}'. Expected a positive integer.", 1);
        }

        OracleResult oracle = OracleRunner.Run(qpdfExe, input, ExpectedQpdfVersion);
        IndependentResult independent = IndependentReader.Read(input, false);
        string sha = ComputeSha256(input);
        Console.WriteLine($"compare: input-sha256={sha}");
        Console.WriteLine($"compare: expected-npages={expected}");
        Console.WriteLine($"compare: oracle-npages={oracle.Npages}");
        Console.WriteLine($"compare: independent-npages={independent.Npages}");

        if (oracle.Npages != independent.Npages || oracle.Npages != expected)
        {
            Console.Error.WriteLine($"qpdfprobe: page-count mismatch (expected={expected}, oracle={oracle.Npages}, independent={independent.Npages}).");
            return 2;
        }

        if (!string.Equals(oracle.InputSha256, independent.InputSha256, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("qpdfprobe: input checksum changed between oracle and independent reads.");
            return 2;
        }

        Console.WriteLine("compare: match");
        return 0;
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

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"qpdfprobe: {message}");
        return 1;
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

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
