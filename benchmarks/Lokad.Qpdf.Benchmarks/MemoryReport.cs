using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.ImageSharp;
#if !QPDF_MEMORY_BASELINE
using Lokad.Qpdf.TestSupport;
#endif

namespace Lokad.Qpdf.Benchmarks;

// Run in a dedicated worker process. Input generation, warmup, sample records
// and JSON serialization stay outside capture. No forced GC in command loops.
internal static class MemoryReport
{
    private sealed record Scenario(string Name, byte[][] Inputs, string[] Arguments);
    private sealed record Sample(int Index, double Milliseconds, long AllocatedBytes,
        int Gen0, int Gen1, int Gen2, long JitMethods, double JitMilliseconds,
        long LocalPeakBytes, long LocalResultBytes, long? RootPeakBytes,
        long? RetainedBytes, long? AfterHostBytes, long? AfterOwnerBytes,
        int? ReserveCalls, int? ResizeCalls, int? PeakReservations);
    private readonly record struct Job(long LocalPeak, long LocalResult, long? RootPeak,
        long? Retained, long? AfterHost, long? AfterOwner, int? Reserves, int? Resizes, int? Handles);

    internal static int Run(string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[1], out int warmups) || warmups is < 1 or > 1024
            || !int.TryParse(args[2], out int samples) || samples is < 1 or > 1024
            || !int.TryParse(args[3], out int batch) || batch is < 1 or > 1024)
        { throw new ArgumentException("memory-report needs mode, warmups, samples, batch-size and optional scenario names; counts are 1..1024."); }
        string mode = args[0];
#if QPDF_MEMORY_BASELINE
        if (mode != "baseline") { throw new ArgumentException("This preserved build requires baseline mode."); }
#else
        if (mode is not ("disabled" or "enabled")) { throw new ArgumentException("Mode must be disabled or enabled."); }
#endif
        string[] names = args.Length == 4
            ? ["tiny", "select-sparse", "rewrite-objects", "merge", "split", "json", "flate", "photo", "linearize",
                "config-text", "config-dense", "config-argv", "import-text", "import-dense", "import-unicode", "import-escaped"]
            : args[4..];
        foreach (string name in names)
        {
            Scenario scenario = Create(name);
            object? budget = null;
#if !QPDF_MEMORY_BASELINE
            if (mode == "enabled") { budget = new RecordingMemoryBudget(4294967296L, false); }
#endif
            long warmStart = Stopwatch.GetTimestamp(), quietStart = warmStart;
            long warmJit = System.Runtime.JitInfo.GetCompiledMethodCount(false);
            double warmJitMs = System.Runtime.JitInfo.GetCompilationTime(false).TotalMilliseconds;
            int warmJobs = 0;
            bool ready;
            do
            {
                _ = Execute(scenario, budget).GetAwaiter().GetResult(); warmJobs++;
                long methods = System.Runtime.JitInfo.GetCompiledMethodCount(false);
                double compilation = System.Runtime.JitInfo.GetCompilationTime(false).TotalMilliseconds;
                if (methods != warmJit || compilation != warmJitMs)
                { quietStart = Stopwatch.GetTimestamp(); warmJit = methods; warmJitMs = compilation; }
                ready = warmJobs >= warmups && Stopwatch.GetElapsedTime(warmStart).TotalSeconds >= 2
                    && Stopwatch.GetElapsedTime(quietStart).TotalSeconds >= 1;
            }
            while (!ready && warmJobs < 1000000 && Stopwatch.GetElapsedTime(warmStart).TotalSeconds < 15);
            double warmMilliseconds = Stopwatch.GetElapsedTime(warmStart).TotalMilliseconds;
            List<Sample> results = new(samples);
            for (int sample = 0; sample < samples; sample++)
            {
                int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
                long jitMethods = System.Runtime.JitInfo.GetCompiledMethodCount(false);
                double jitMs = System.Runtime.JitInfo.GetCompilationTime(false).TotalMilliseconds;
                long allocated = GC.GetTotalAllocatedBytes(true);
                long start = Stopwatch.GetTimestamp();
                Job totals = default;
                for (int index = 0; index < batch; index++)
                {
                    Job job = Execute(scenario, budget).GetAwaiter().GetResult();
                    totals = new(Math.Max(totals.LocalPeak, job.LocalPeak), Math.Max(totals.LocalResult, job.LocalResult),
                        Max(totals.RootPeak, job.RootPeak), Max(totals.Retained, job.Retained),
                        Max(totals.AfterHost, job.AfterHost), Max(totals.AfterOwner, job.AfterOwner),
                        (totals.Reserves ?? 0) + job.Reserves, (totals.Resizes ?? 0) + job.Resizes,
                        Max(totals.Handles, job.Handles));
                }
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                long bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                results.Add(new(sample, elapsed, bytes,
                    GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2,
                    System.Runtime.JitInfo.GetCompiledMethodCount(false) - jitMethods,
                    System.Runtime.JitInfo.GetCompilationTime(false).TotalMilliseconds - jitMs,
                    totals.LocalPeak, totals.LocalResult, totals.RootPeak, totals.Retained, totals.AfterHost,
                    totals.AfterOwner, totals.Reserves, totals.Resizes, totals.Handles));
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                scenario = name, mode, warmups, warmJobs, warmMilliseconds, warmupReady = ready, batchSize = batch,
                inputBytes = scenario.Inputs.Sum(input => input.LongLength),
                inputOwnership = "copy", allocation = "isolated worker total; cumulative bytes per batch",
                timing = "provisional; compare warmed interleaved A/A samples before interpreting differences",
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                runtimeAssembly = typeof(Qpdf).Assembly.GetName().Version?.ToString(),
                engineSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Qpdf).Assembly.Location))),
                inputSha256 = scenario.Inputs.Select(input => Convert.ToHexString(SHA256.HashData(input))).ToArray(),
                samples = results,
            }));
        }
        return 0;
    }

    private static long? Max(long? left, long? right) => right is null ? left : Math.Max(left ?? 0L, right.Value);
    private static int? Max(int? left, int? right) => right is null ? left : Math.Max(left ?? 0, right.Value);

    private static async Task<Job> Execute(Scenario scenario, object? budget)
    {
#if !QPDF_MEMORY_BASELINE
        RecordingMemoryBudget? root = (RecordingMemoryBudget?)budget;
        root?.ResetMetrics();
        using IMemoryScope? owner = root?.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, ImageSharpJpegCodec.Instance, owner);
#else
        InMemoryQpdfHost host = new(QpdfLimits.Default, ImageSharpJpegCodec.Instance);
#endif
        for (int index = 0; index < scenario.Inputs.Length; index++)
        { host.AddInput(QpdfPath.From("input" + index), scenario.Inputs[index], QpdfInputOwnership.Copy); }
#if !QPDF_MEMORY_BASELINE
        using IMemoryScope? invocation = owner?.CreateScope();
        IQpdfHost view = invocation is null ? host : new MemoryHost(host, invocation);
#else
        IQpdfHost view = host;
#endif
        QpdfExecutionResult? result = await Qpdf.ExecuteAsync(view,
            new QpdfCommandInvocation(scenario.Arguments), CancellationToken.None).ConfigureAwait(false);
        if (result.Status != QpdfExecutionStatus.Completed || result.ExitCode != QpdfExitCode.Success)
        { throw new InvalidOperationException(scenario.Name + " failed: " + string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))); }
        long localPeak = result.Counters.PeakMemoryBytes, localResult = result.Counters.LiveMemoryBytes;
#if !QPDF_MEMORY_BASELINE
        long? retained = root?.ReservedBytes;
        host.Dispose();
        long? afterHost = root?.ReservedBytes;
        result = null;
        invocation?.Dispose(); owner?.Dispose();
        if (root is not null && (root.ReservedBytes != 0L || root.LiveReservations != 0))
        { throw new InvalidOperationException("Job owners failed to refund."); }
        return new(localPeak, localResult, root?.PeakBytes,
            retained, afterHost, root?.ReservedBytes, root?.ReserveCalls, root?.ResizeCalls, root?.PeakReservations);
#else
        return new(localPeak, localResult, null, null, null, null, null, null, null);
#endif
    }

#if !QPDF_MEMORY_BASELINE
    private sealed class MemoryHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost, IQpdfJpegCodecHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public IQpdfJpegCodec? JpegCodec => ImageSharpJpegCodec.Instance;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }
#endif

    private static Scenario Create(string name)
    {
        if (name == "config-argv")
        {
            return new(name, [PdfScenarioBuilder.BuildTextDocument(1), "{}"u8.ToArray()],
                ["input0", "--job-json-file=input1", "--add-attachment", "input0",
                    "--description=" + new string('x', 131072), "--", "--", "output"]);
        }
        if (name is "config-text" or "config-dense")
        {
            string config = name == "config-text"
                ? "{\"addAttachment\":[{\"file\":\"input0\",\"key\":\"extra\",\"description\":\"" + new string('x', 131072) + "\"}]}"
                : "{\"pages\":[" + string.Join(',', Enumerable.Repeat("{\"file\":\".\",\"range\":\"1\"}", 256)) + "]}";
            return new(name, [PdfScenarioBuilder.BuildTextDocument(1), Encoding.UTF8.GetBytes(config)],
                ["input0", "--job-json-file=input1", "output"]);
        }
        if (name is "import-text" or "import-dense" or "import-unicode" or "import-escaped")
        {
            string body = name switch
            {
                "import-dense" => "[" + string.Join(',', Enumerable.Range(1000, 4096)) + "]",
                "import-unicode" => "\"u:" + new string('\u00e9', 65536) + "\"",
                "import-escaped" => "\"u:" + string.Concat(Enumerable.Repeat("\\u00e9", 65536)) + "\"",
                _ => "\"u:" + new string('A', 131072) + "\"",
            };
            string json = $$$"""
                {"qpdf":[{"jsonversion":2,"pdfversion":"1.4"},{
                  "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Audit":"4 0 R"}},
                  "obj:2 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["3 0 R"]}},
                  "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,1,1]}},
                  "obj:4 0 R":{"value":{{{body}}}},"trailer":{"value":{"/Root":"1 0 R"}}
                }]}
                """;
            return new(name, [Encoding.UTF8.GetBytes(json)], ["input0", "--json-input", "--", "output"]);
        }
        byte[] input = name switch
        {
            "select-sparse" => PdfScenarioBuilder.BuildSparseComparisonDocument(),
            "rewrite-objects" => PdfScenarioBuilder.BuildObjectComparisonDocument(),
            "flate" => PdfScenarioBuilder.BuildFilterChain(10),
            "photo" => PdfScenarioBuilder.BuildPhotoDocument(),
            _ => PdfScenarioBuilder.BuildTextDocument(name is "tiny" or "merge" ? 1 : 10),
        };
        string[] arguments = name switch
        {
            "tiny" => ["input0", "--show-npages"],
            "select-sparse" => ["input0", "--pages", ".", "1-3", "--", "output"],
            "rewrite-objects" => ["input0", "--stream-data=preserve", "output"],
            "merge" => ["--empty", "--pages", "input0", "1", "input1", "1", "--", "output"],
            "split" => ["input0", "--split-pages=1", "output-%d"],
            "json" => ["input0", "--json", "--", "output"],
            "flate" => ["input0", "--stream-data=uncompress", "output"],
            "photo" => ["input0", "--optimize-images", "output"],
            "linearize" => ["input0", "--linearize", "output"],
            _ => throw new ArgumentException("Unknown memory-report scenario: " + name),
        };
        return new(name, name == "merge" ? [input, input] : [input], arguments);
    }
}
