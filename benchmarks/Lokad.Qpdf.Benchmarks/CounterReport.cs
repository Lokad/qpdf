using Lokad.Qpdf.Image;
using Lokad.Qpdf;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Benchmarks;

/// <summary>Runs representative operations and prints governor counters and memory estimates.</summary>
internal static class CounterReport
{
    internal static int Run(string[] args)
    {
        return RunAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(string[] args)
    {
        int failures = 0;
        int scenarios = 0;
        HashSet<string>? requested = args.Length == 0 ? null : new(args, StringComparer.Ordinal);
        Console.WriteLine("Memory values are job estimates; allocation totals are cumulative, not retained memory.");
        async Task Execute(string name, Func<Task> scenario)
        {
            if (requested is not null && !requested.Remove(name)) { return; }
            scenarios += 1;
            long allocatedBefore = GC.GetTotalAllocatedBytes(true);
            try
            {
                await scenario().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures += 1;
                Console.Error.WriteLine("qpdfbench: scenario '" + name + "' failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Console.WriteLine("[allocation] " + name + " cumulative-bytes="
                    + (GC.GetTotalAllocatedBytes(true) - allocatedBefore).ToString());
            }
        }

        await Execute("show-npages-50", ShowNpages).ConfigureAwait(false);
        await Execute("select-few-50", SelectFew).ConfigureAwait(false);
        await Execute("merge-shared-8", MergeShared).ConfigureAwait(false);
        await Execute("merge-many-20", MergeMany).ConfigureAwait(false);
        await Execute("decode-chain-30", DecodeChain).ConfigureAwait(false);
        await Execute("json-4", JsonInspect).ConfigureAwait(false);
        await Execute("rewrite-compress-4", RewriteCompress).ConfigureAwait(false);
        await Execute("optimize-photo", OptimizePhoto).ConfigureAwait(false);
        await Execute("linearize-10", Linearize).ConfigureAwait(false);
        await Execute("cancel-select-50", CancelSelect).ConfigureAwait(false);
        if (requested is not null)
        {
            foreach (string name in requested)
            {
                Console.Error.WriteLine("qpdfbench: unknown counter scenario '" + name + "'.");
                failures += 1;
            }
        }
        Console.WriteLine("counter-report: " + scenarios.ToString() + " scenarios, " + failures.ToString() + " failures");
        return failures == 0 ? 0 : 2;
    }

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new(QpdfLimits.Default, LokadImageJpegCodec.Instance);
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static void Report(string name, QpdfExecutionResult result, long inputBytes, long outputBytes)
    {
        QpdfExecutionCounters counters = result.Counters;
        Console.WriteLine("[counter] " + name
            + " status=" + result.Status.ToString()
            + " exit=" + ((int)result.ExitCode).ToString()
            + " in=" + inputBytes.ToString()
            + " out=" + outputBytes.ToString()
            + " decoded=" + counters.TotalDecodedBytes.ToString()
            + " peakmem=" + counters.PeakMemoryBytes.ToString()
            + " livemem=" + counters.LiveMemoryBytes.ToString()
            + " hostops=" + counters.HostOperations.ToString()
            + " diags=" + counters.DiagnosticCount.ToString());
        if (result.Status != QpdfExecutionStatus.Completed)
        {
            throw new InvalidOperationException(name + " did not complete: " + result.Status.ToString());
        }
    }

    private static long PublishedBytes(InMemoryQpdfHost host, string output)
    {
        if (host.TryGetOutput(QpdfPath.From(output), out byte[] bytes))
        {
            return bytes.Length;
        }

        return -1L;
    }

    private static async Task ShowNpages()
    {
        byte[] input = PdfScenarioBuilder.BuildTextDocument(50);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-npages"]), CancellationToken.None).ConfigureAwait(false);
        Report("show-npages-50", result, input.Length, PublishedBytes(host, "outputs/a.pdf"));
    }

    private static async Task SelectFew()
    {
        byte[] input = PdfScenarioBuilder.BuildTextDocument(50);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1-3", "--", "outputs/a.pdf"]), CancellationToken.None).ConfigureAwait(false);
        Report("select-few-50", result, input.Length, PublishedBytes(host, "outputs/a.pdf"));
    }

    private static async Task MergeShared()
    {
        byte[] input = PdfScenarioBuilder.BuildTextDocument(1);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), input, QpdfInputOwnership.Copy);
        List<string> argv = new() { "--empty", "--pages" };
        for (int i = 0; i < 4; i++)
        {
            argv.Add("inputs/a.pdf");
            argv.Add("1");
            argv.Add("inputs/b.pdf");
            argv.Add("1");
        }

        argv.Add("--");
        argv.Add("outputs/a.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None).ConfigureAwait(false);
        Report("merge-shared-8", result, 2L * input.Length, PublishedBytes(host, "outputs/a.pdf"));
    }

    private static async Task MergeMany()
    {
        byte[] input = PdfScenarioBuilder.BuildTextDocument(1);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        List<string> argv = new() { "--empty", "--pages", "inputs/a.pdf", "1" };
        for (int i = 1; i < 20; i++)
        {
            string name = "inputs/m" + i.ToString() + ".pdf";
            host.AddInput(QpdfPath.From(name), input, QpdfInputOwnership.Copy);
            argv.Add(name);
            argv.Add("1");
        }

        argv.Add("--");
        argv.Add("outputs/a.pdf");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None).ConfigureAwait(false);
        Report("merge-many-20", result, 20L * input.Length, PublishedBytes(host, "outputs/a.pdf"));
    }

    private static async Task DecodeChain()
    {
        byte[] input = PdfScenarioBuilder.BuildFilterChain(30);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--stream-data=uncompress", "--no-default-limits", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None).ConfigureAwait(false);
        Report("decode-chain-30", result, input.Length, PublishedBytes(host, "outputs/a.pdf"));
    }

    private static async Task JsonInspect()
    {
        byte[] input = PdfScenarioBuilder.BuildTextDocument(4);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--json", "--", "outputs/a.json"]), CancellationToken.None).ConfigureAwait(false);
        Report("json-4", result, input.Length, PublishedBytes(host, "outputs/a.json"));
    }

    private static async Task RewriteCompress()
    {
        byte[] input = PdfScenarioBuilder.BuildTextDocument(4);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--stream-data=compress", "--pages", ".", "1-4", "--", "outputs/a.pdf"]), CancellationToken.None).ConfigureAwait(false);
        Report("rewrite-compress-4", result, input.Length, PublishedBytes(host, "outputs/a.pdf"));
    }

    private static async Task OptimizePhoto()
    {
        byte[] input = PdfScenarioBuilder.BuildPhotoDocument();
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--optimize-images", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None).ConfigureAwait(false);
        Report("optimize-photo", result, input.Length, PublishedBytes(host, "outputs/a.pdf"));
    }

    private static async Task Linearize()
    {
        byte[] input = PdfScenarioBuilder.BuildTextDocument(10);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--linearize", "--pages", ".", "1-10", "--", "outputs/a.pdf"]), CancellationToken.None).ConfigureAwait(false);
        Report("linearize-10", result, input.Length, PublishedBytes(host, "outputs/a.pdf"));
    }

    private static async Task CancelSelect()
    {
        byte[] input = PdfScenarioBuilder.BuildTextDocument(50);
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", input);
        using CancellationTokenSource source = new();
        source.Cancel();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1-50", "--", "outputs/a.pdf"]), source.Token).ConfigureAwait(false);
        QpdfExecutionCounters counters = result.Counters;
        Console.WriteLine("[counter] cancel-select-50 status=" + result.Status.ToString() + " exit=" + ((int)result.ExitCode).ToString() + " in=" + input.Length.ToString() + " hostops=" + counters.HostOperations.ToString());
        if (result.Status != QpdfExecutionStatus.Canceled)
        {
            throw new InvalidOperationException("cancel-select-50 was not canceled.");
        }
    }
}
