using BenchmarkDotNet.Attributes;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Benchmarks;

/// <summary>Warmed public jobs with memory transport; no native process ratio.</summary>
[MemoryDiagnoser]
public class ComparisonBenchmarks
{
    private ComparisonWorkload _workload = new(string.Empty, [], [], [], []);
    private readonly Dictionary<string, byte[]> _inputs = new(StringComparer.Ordinal);
    private QpdfInputOwnership _ownership = QpdfInputOwnership.Copy;

    [Params("select-S10", "select-M500", "merge-250x2", "split-M500", "rewrite-M500",
        "rewrite-X100", "object-streams-X100", "linearize-M500", "check-X100",
        "rewrite-ID-M500", "rewrite-objects", "select-sparse", "split-single")]
    public string Workload { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        string directory = Environment.GetEnvironmentVariable("QPDF_COMPARISON_ROOT")
            ?? throw new InvalidOperationException("Set QPDF_COMPARISON_ROOT to the prepared comparison directory.");
        _ownership = Environment.GetEnvironmentVariable("QPDF_COMPARISON_INPUT_OWNERSHIP") switch
        {
            null or "copy" => QpdfInputOwnership.Copy,
            "transfer" => QpdfInputOwnership.Transfer,
            _ => throw new InvalidOperationException("QPDF_COMPARISON_INPUT_OWNERSHIP must be copy or transfer."),
        };
        Console.WriteLine("Comparison input ownership: " + _ownership);
        _workload = ComparisonWorkloads.Create().Single(item => item.Name == Workload);
        foreach (string name in _workload.Inputs)
        {
            _inputs.Add(name, File.ReadAllBytes(Path.Combine(directory, "inputs", name)));
        }
    }

    [Benchmark]
    public async Task ExecutePublicJob()
    {
        InMemoryQpdfHost host = new();
        foreach ((string name, byte[] content) in _inputs)
        {
            // Prepared inputs remain immutable for the entire benchmark, so
            // transfer can isolate processing from an optional host clone.
            host.AddInput(QpdfPath.From("inputs/" + name), content, _ownership);
        }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(_workload.Arguments), CancellationToken.None);
        if (result.Status != QpdfExecutionStatus.Completed || result.ExitCode != QpdfExitCode.Success)
        {
            throw new InvalidOperationException("Comparison workload failed: " + Workload + " " + result.Status);
        }
    }
}
