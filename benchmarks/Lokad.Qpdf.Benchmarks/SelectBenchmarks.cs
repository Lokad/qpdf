using BenchmarkDotNet.Attributes;
using Lokad.Qpdf;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Benchmarks;

/// <summary>Timed micro-benchmarks over synthetic inputs. Timing is local-only; checked counters live in --counter-report.</summary>
[MemoryDiagnoser]
public class SelectBenchmarks
{
    private byte[] _fiftyPages = [];
    private byte[] _chain = [];

    [GlobalSetup]
    public void Setup()
    {
        _fiftyPages = PdfScenarioBuilder.BuildTextDocument(50);
        _chain = PdfScenarioBuilder.BuildFilterChain(30);
    }

    [Benchmark(Description = "Select 3 of 50 pages")]
    public async Task SelectThreeOfFifty()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), _fiftyPages, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1-3", "--", "outputs/a.pdf"]), CancellationToken.None);
        if (result.Status != QpdfExecutionStatus.Completed)
        {
            throw new InvalidOperationException("Select benchmark did not complete: " + result.Status.ToString());
        }
    }

    [Benchmark(Description = "Decode 30-filter chain")]
    public async Task DecodeChain()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), _chain, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--stream-data=uncompress", "--no-default-limits", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        if (result.Status != QpdfExecutionStatus.Completed)
        {
            throw new InvalidOperationException("Decode benchmark did not complete: " + result.Status.ToString());
        }
    }

    [Benchmark(Description = "Show page count of 50 pages")]
    public async Task ShowNpages()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), _fiftyPages, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-npages"]), CancellationToken.None);
        if (result.Status != QpdfExecutionStatus.Completed)
        {
            throw new InvalidOperationException("ShowNpages benchmark did not complete: " + result.Status.ToString());
        }
    }
}
