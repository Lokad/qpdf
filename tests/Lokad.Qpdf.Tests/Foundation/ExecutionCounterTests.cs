namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class ExecutionCounterTests
{
    private static readonly QpdfPath InputPath = QpdfPath.From("input.pdf"), OutputPath = QpdfPath.From("output.pdf");

    public static IEnumerable<object[]> Jobs()
    {
        string[] routes = ["argv-whole", "argv-linearized", "argv-pages", "argv-merge", "job-whole", "mixed-whole",
            "response-whole", "argv-json", "argv-filtered", "job-check", "qdf", "zlib-compress", "zlib-uncompress"];
        for (int i = 0; i < routes.Length; i++)
        { yield return [routes[i], i % 2 == 0, routes[i] is "argv-json" or "argv-filtered" or "job-check" ? "input-close" : "write", "success"]; }
        foreach ((string route, string boundary, string outcome) in new[] {
            ("argv-whole", "read", "fault"), ("argv-whole", "write", "cancel"),
            ("argv-linearized", "commit", "fault"), ("argv-pages", "input-close", "fault"),
            ("argv-merge", "output-close", "fault"), ("mixed-whole", "input-close", "cancel"),
            ("response-whole", "write", "fault"), ("argv-json", "input-close", "cancel"),
            ("argv-filtered", "input-close", "fault"), ("job-check", "input-close", "fault"),
            ("qdf", "write", "fault"), ("zlib-compress", "commit", "cancel"), ("zlib-uncompress", "output-close", "fault") })
        { yield return [route, false, boundary, outcome]; }
    }

    [Theory]
    [MemberData(nameof(Jobs))]
    public async Task PublicJobsReturnObservedFinalCounters(string route, bool compressed, string boundary, string outcome)
    {
        using RecordingJobHost host = await ExecutionJobFixtures.PrepareAsync(route, compressed, boundary,
            outcome == "fault" ? "fault" : "success");
        if (boundary is "read" or "input-close") { host.PausePath = QpdfPath.From(route.StartsWith("zlib-", StringComparison.Ordinal) ? "bytes.bin" : "input.pdf"); }
        Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] prior));
        using CancellationTokenSource cancellation = new();
        Task<object> running = ExecutionJobFixtures.RunAsync(host, route, QpdfLimits.Default, cancellation.Token);
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.ReadBytes > 0L); Assert.True(host.Budget.ReservedBytes > 0L); Assert.False(running.IsCompleted);
            if (outcome == "cancel") { cancellation.Cancel(); } else { host.Resume(); }
            QpdfExecutionResult result = Assert.IsType<QpdfExecutionResult>(await running.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(outcome == "success" ? QpdfExecutionStatus.Completed : outcome == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            if (outcome == "success" && route == "argv-json") { Assert.NotNull(result.OutputText); }
            if (outcome == "success" && route == "argv-filtered") { Assert.Equal("q Q\n"u8.ToArray(), result.OutputBytes?.ToArray()); }
            if (outcome != "success") { Assert.Null(result.OutputText); Assert.Null(result.OutputBytes); }
            Assert.Equal(host.HostCalls, result.Counters.HostOperations);
            Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.InRange(result.Counters.PeakMemoryBytes, 1L, QpdfLimits.Default.MaxMemoryBytes);
            Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            if (host.Committed.Count == 0)
            { Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] kept)); Assert.Equal(prior, kept); }
            QpdfExecutionCounters snapshot = result.Counters with { };
            host.Dispose();
            Assert.Equal(0L, host.Budget.ReservedBytes); Assert.Equal(0, host.Budget.LiveReservations);
            Assert.Equal(snapshot, result.Counters);
        }
        finally { host.Resume(); await running; }
    }

    public static IEnumerable<object[]> Configurations()
    {
        foreach (string route in new[] { "response-help", "response-utf8", "response-parse", "response-zopfli",
            "nested-job", "multi-job", "mixed-job" })
        foreach (string outcome in new[] { "success", "fault", "cancel" })
        { yield return [route, outcome]; }
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task ConfigurationFailuresKeepObservedCounters(string route, string outcome)
    {
        using CancellationTokenSource cancellation = new();
        using RecordingJobHost host = await ExecutionJobFixtures.PrepareAsync("argv-whole", false, "input-close", outcome);
        byte[] response = route switch
        {
            "response-help" => "--help\n"u8.ToArray(),
            "response-utf8" => [0xff, 0x00],
            "response-parse" => "--not-an-option\n"u8.ToArray(),
            _ => "--zopfli\n"u8.ToArray(),
        };
        host.Inner.AddInput(QpdfPath.From("args.txt"), response, QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("invalid.json"), "{\"unknownOption\":\"\"}"u8.ToArray(), QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("outer.json"), "{\"jobJsonFile\":\"invalid.json\"}"u8.ToArray(), QpdfInputOwnership.Copy);
        host.PausePath = QpdfPath.From(route.StartsWith("response-", StringComparison.Ordinal) ? "args.txt" : "invalid.json");
        Task<QpdfExecutionResult> executing = route == "nested-job"
            ? Qpdf.ExecuteJobAsync(host, "{\"jobJsonFile\":\"outer.json\"}", cancellation.Token).AsTask()
            : Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(route switch
            {
                "multi-job" => ["--job-json-file=settings.json", "--job-json-file=invalid.json"],
                "mixed-job" => ["input.pdf", "--job-json-file=invalid.json", "output.pdf"],
                _ => ["@args.txt"],
            }), cancellation.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L);
            Assert.True(host.ReadBytes > 0L);
            if (outcome == "cancel") { cancellation.Cancel(); } else { host.Resume(); }
            QpdfExecutionResult result = await executing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(outcome == "cancel" ? QpdfExecutionStatus.Canceled : outcome == "success" && route == "response-help"
                ? QpdfExecutionStatus.Completed : QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(host.HostCalls, result.Counters.HostOperations);
            QpdfExecutionCounters snapshot = result.Counters with { };
            Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.True(host.Inner.TryGetOutput(OutputPath, out byte[] kept));
            Assert.Equal(new byte[] { 17, 23, 41 }, kept);
            Assert.Null(result.OutputText); Assert.Null(result.OutputBytes);
            if (outcome != "cancel") { Assert.NotEmpty(result.Diagnostics); }
            host.Dispose();
            Assert.Equal(0L, host.Budget.ReservedBytes); Assert.Equal(0, host.Budget.LiveReservations);
            Assert.Equal(snapshot, result.Counters);
        }
        finally { host.Resume(); await executing; }
    }

}
