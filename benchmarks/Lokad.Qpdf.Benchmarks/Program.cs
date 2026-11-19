using BenchmarkDotNet.Configs;
using BenchmarkDotNet.ConsoleArguments;
using BenchmarkDotNet.ConsoleArguments.ListBenchmarks;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Lokad.Qpdf.Benchmarks;

/// <summary>Developer-only resource-counter and benchmark harness entry point.</summary>
internal static class Program
{
    internal static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        try
        {
            if (args.Length == 2 && args[0] == "--security-inputs")
            {
                return SecurityWorkloads.GenerateAsync(args[1]).GetAwaiter().GetResult();
            }
            if (args.Length == 2 && args[0] == "--comparison-inputs")
            {
                return ComparisonWorkloads.Generate(args[1]);
            }
            if (args[0] == "--counter-report")
            {
                return CounterReport.Run(args[1..]);
            }
            if (args[0] == "--memory-report")
            {
                return MemoryReport.Run(args[1..]);
            }

            return RunBenchmarks(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("qpdfbench: unexpected failure: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static int RunBenchmarks(string[] args)
    {
        IConfig config = DefaultConfig.Instance;
        Summary[] summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config).ToArray();
        if (summaries.Length == 0)
        {
            if (args is ["--help"] or ["--version"]) { return 0; }
            // BenchmarkDotNet also returns no summaries for informational
            // invocations. Use its parser to distinguish those from refusals.
            var (valid, _, options) = ConfigParser.Parse(args, NullLogger.Instance, config);
            if (valid && (options.PrintInformation || options.ListBenchmarkCaseMode != ListBenchmarkCaseMode.Disabled))
            { return 0; }
            Console.Error.WriteLine("qpdfbench: no benchmark results were produced.");
            return 1;
        }

        if (summaries.All(summary => !summary.HasCriticalValidationErrors && !summary.Reports.IsEmpty
            && summary.Reports.All(report => report.Success && report.ResultStatistics is not null))) { return 0; }
        Console.Error.WriteLine("qpdfbench: one or more benchmarks did not produce a successful measurement.");
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Lokad.Qpdf.Benchmarks: developer-only resource-counter and benchmark harness.");
        Console.WriteLine("Not part of the shipped Lokad.qpdf package. All inputs are synthetic; no external tools required.");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  Lokad.Qpdf.Benchmarks --help");
        Console.WriteLine("  Lokad.Qpdf.Benchmarks --counter-report [scenario names...]");
        Console.WriteLine("  Lokad.Qpdf.Benchmarks --memory-report <disabled|enabled> <warmups> <samples> <batch-size> [scenario names...]");
        Console.WriteLine("  Lokad.Qpdf.Benchmarks --comparison-inputs <fresh-directory>");
        Console.WriteLine("  Lokad.Qpdf.Benchmarks --security-inputs <fresh-directory>");
        Console.WriteLine("  Lokad.Qpdf.Benchmarks --filter <BenchmarkDotNet filter>");
    }
}
