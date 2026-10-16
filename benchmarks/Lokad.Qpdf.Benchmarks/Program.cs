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

            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, BenchmarkDotNet.Configs.DefaultConfig.Instance);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("qpdfbench: unexpected failure: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
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
        Console.WriteLine("  Lokad.Qpdf.Benchmarks --filter <BenchmarkDotNet filter>");
    }
}
