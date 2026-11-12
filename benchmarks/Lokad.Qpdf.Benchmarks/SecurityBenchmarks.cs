#if !QPDF_MEMORY_BASELINE
using BenchmarkDotNet.Attributes;

namespace Lokad.Qpdf.Benchmarks;

/// <summary>Complete public operations over identical prepared plaintext and encrypted inputs.</summary>
[MemoryDiagnoser]
public class SecurityBenchmarks
{
    private SecurityWorkload? _workload;

    [Params("plain", "r4", "r6")]
    public string Protection { get; set; } = "plain";

    [Params("auth", "open", "rewrite", "select")]
    public string Operation { get; set; } = "auth";

    [GlobalSetup]
    public async Task Setup()
    {
        _workload = SecurityWorkloads.Load("security-" + Protection + "-" + Operation);
        await SecurityWorkloads.ValidateAsync(_workload);
    }

    [Benchmark]
    public Task CompletePublicOperation()
        => SecurityWorkloads.ExecuteAsync(_workload ?? throw new InvalidOperationException("Security benchmark was not prepared."));
}
#endif
