using System.Reflection;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class PublicationAdmissionTests
{
    private const string Flags = "verbose progress report-memory-usage";
    private static readonly string[] Targets = ["output.pdf", "output-1.pdf", "output-2.pdf", "side-4", "pass1.pdf"];

    private static async Task<object> Prepare(string operation, bool compressed, bool warning)
    {
        MethodInfo method = typeof(PresentationByteTests).GetMethod("Prepare", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing canonical fixture driver.");
        Task task = (Task)(method.Invoke(null, [operation, compressed, warning]) ?? throw new InvalidOperationException("Missing fixture task."));
        await task;
        return task.GetType().GetProperty("Result")?.GetValue(task) ?? throw new InvalidOperationException("Missing prepared host.");
    }

    private static async Task<QpdfExecutionResult> Run(object host, string route, string operation, string flags, QpdfLimits limits)
    {
        MethodInfo method = typeof(PresentationByteTests).GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing canonical public driver.");
        return await (Task<QpdfExecutionResult>)(method.Invoke(null, [host, route, operation, flags, limits, CancellationToken.None])
            ?? throw new InvalidOperationException("Missing execution task."));
    }

    private static InMemoryQpdfHost Inner(object host)
        => (InMemoryQpdfHost)(host.GetType().GetProperty("Inner", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(host)
            ?? throw new InvalidOperationException("Missing underlying host."));

    private static int Commits(object host)
        => (int)(host.GetType().GetProperty("Commits", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(host)
            ?? throw new InvalidOperationException("Missing commit counter."));

    [Theory]
    [InlineData("argv", "split", 5L)]
    [InlineData("job", "split", 5L)]
    [InlineData("mixed", "split", 7L)]
    [InlineData("response", "split", 7L)]
    [InlineData("argv", "json-sidecar", 3L)]
    [InlineData("job", "json-sidecar", 3L)]
    [InlineData("mixed", "json-sidecar", 5L)]
    [InlineData("response", "json-sidecar", 5L)]
    public async Task OriginalHostCeilingsPreserveEveryTargetBeforePublication(string route, string operation, long ceiling)
    {
        object host = await Prepare(operation, false, false);
        byte[][] aliases = Targets.Select(path => { Assert.True(Inner(host).TryGetOutput(QpdfPath.From(path), out byte[] bytes)); return bytes; }).ToArray();
        QpdfExecutionResult result = await Run(host, route, operation, Flags, QpdfLimits.Default with { MaxHostOperations = ceiling });
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(0, Commits(host));
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded && item.Message.Contains(nameof(QpdfLimits.MaxHostOperations), StringComparison.Ordinal));
        Assert.InRange(result.Counters.HostOperations, 1L, ceiling);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code is QpdfDiagnosticCode.VerboseSummary or QpdfDiagnosticCode.ProgressReport or QpdfDiagnosticCode.MemoryUsage);
        foreach (string path in Targets) { Assert.True(Inner(host).TryGetOutput(QpdfPath.From(path), out byte[] bytes)); Assert.Equal(new byte[] { 17, 23, 41 }, bytes); }
        Assert.All(aliases, bytes => Assert.Equal(new byte[] { 17, 23, 41 }, bytes));
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, Inner(host).OpenLeaseCount);
    }

    private static async Task<ObservedHost> Observed(string operation, bool compressed, bool warning)
        => new(Inner(await Prepare(operation, compressed, warning)));

    private static async Task<QpdfExecutionResult> RunObserved(ObservedHost host, string route, string operation, string flags,
        QpdfLimits limits, CancellationToken token)
    {
        List<string> argv = [operation == "empty" ? "--empty" : operation == "json-import" ? "input.json" : "input.pdf"];
        Dictionary<string, object> job = new(StringComparer.Ordinal);
        if (operation == "empty") { job.Add("empty", ""); }
        else { job.Add("inputFile", operation == "json-import" ? "input.json" : "input.pdf"); }
        foreach (string flag in flags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            argv.Add("--" + flag);
            job.Add(flag switch { "report-memory-usage" => "reportMemoryUsage", "no-warn" => "noWarn",
                "warning-exit-0" => "warningExit0", _ => flag }, "");
        }
        switch (operation)
        {
            case "empty":
                argv.AddRange(["--pages", "input.pdf", "1", "--"]);
                job.Add("pages", new[] { new { file = "input.pdf", range = "1" } }); break;
            case "linearized": argv.Add("--linearize"); job.Add("linearize", ""); break;
            case "qdf": argv.Add("--qdf"); job.Add("qdf", ""); break;
            case "pages":
                argv.AddRange(["--pages", ".", "1", "--"]);
                job.Add("pages", new[] { new { file = ".", range = "1" } }); break;
            case "merge":
                argv.AddRange(["--pages", ".", "1", "second.pdf", "1", "--"]);
                job.Add("pages", new[] { new { file = ".", range = "1" }, new { file = "second.pdf", range = "1" } }); break;
            case "split":
            case "split-linearized":
            case "split-qdf":
            case "split-streamed":
            case "split-pass1":
                if (operation is "split-linearized" or "split-pass1") { argv.Add("--linearize"); job.Add("linearize", ""); }
                if (operation == "split-qdf") { argv.Add("--qdf"); job.Add("qdf", ""); }
                if (operation == "split-streamed") { argv.Add("--object-streams=generate"); job.Add("objectStreams", "generate"); }
                if (operation == "split-pass1") { argv.Add("--linearize-pass1=pass1.pdf"); job.Add("linearizePass1", "pass1.pdf"); }
                argv.AddRange(["--split-pages=1", "--pages", ".", "1", ".", "1", "--"]);
                job.Add("splitPages", "1");
                job.Add("pages", new[] { new { file = ".", range = "1" }, new { file = ".", range = "1" } }); break;
            case "json-file":
            case "json-inline":
            case "json-sidecar":
                argv.AddRange(["--json=2", "--json-stream-data=" + (operation == "json-sidecar" ? "file" : "inline")]);
                job.Add("json", "2"); job.Add("jsonStreamData", operation == "json-sidecar" ? "file" : "inline");
                if (operation == "json-sidecar") { argv.Add("--json-stream-prefix=side"); job.Add("jsonStreamPrefix", "side"); }
                if (operation != "json-inline") { argv.Add("--"); } break;
            case "json-import": argv.AddRange(["--json-input", "--"]); job.Add("jsonInput", ""); break;
            case "json-update": argv.AddRange(["--update-from-json=input.json", "--"]); job.Add("updateFromJson", "input.json"); break;
            case "check": argv.Add("--check"); job.Add("check", ""); break;
            case "show-count": argv.Add("--show-npages"); job.Add("showNpages", ""); break;
            case "filtered":
                argv.AddRange(["--show-object=4", "--filtered-stream-data"]);
                job.Add("showObject", "4"); job.Add("filteredStreamData", ""); break;
        }
        if (operation is not ("check" or "show-count" or "filtered" or "json-inline"))
        { argv.Add("output.pdf"); job.Add("outputFile", "output.pdf"); }
        QpdfExecutionOptions options = new(limits);
        if (route == "job") { return await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), options, token); }
        if (route == "mixed") { argv.Insert(1, "--job-json-file=settings.json"); }
        if (route == "response")
        {
            host.Inner.AddInput(QpdfPath.From("args.txt"), Encoding.UTF8.GetBytes(string.Join('\n', argv) + "\n"), QpdfInputOwnership.Copy);
            argv = ["@args.txt"];
        }
        return await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), options, token);
    }


    public static IEnumerable<object[]> ExactJobs()
    {
        foreach (string route in new[] { "argv", "job", "mixed", "response" })
        foreach (string operation in new[] { "whole", "linearized", "qdf", "pages", "merge", "split", "split-linearized",
            "split-qdf", "split-streamed", "split-pass1", "json-file", "json-inline", "json-sidecar", "json-import", "json-update", "empty", "filtered" })
        foreach (bool compressed in new[] { false, true })
        foreach (bool presentation in new[] { false, true }) { yield return [route, operation, compressed, presentation]; }
    }

    [Theory]
    [MemberData(nameof(ExactJobs))]
    public async Task ExactHostBudgetsCountExecutedCallsAndPreserveOutputBytes(string route, string operation, bool compressed, bool presentation)
    {
        string flags = presentation ? Flags : "";
        ObservedHost baselineHost = await Observed(operation, compressed, true);
        QpdfExecutionResult baseline = await RunObserved(baselineHost, route, operation, flags, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, baseline.Status);
        Assert.Equal(baselineHost.Operations, baseline.Counters.HostOperations);
        ObservedHost host = await Observed(operation, compressed, true);
        QpdfExecutionResult result = await RunObserved(host, route, operation, flags,
            QpdfLimits.Default with { MaxHostOperations = baselineHost.Operations }, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(baseline.ExitCode, result.ExitCode);
        Assert.Equal(host.Operations, result.Counters.HostOperations); Assert.Equal(baselineHost.Operations, host.Operations);
        Assert.Equal(baselineHost.Committed, host.Committed);
        foreach (string path in Targets)
        {
            Assert.True(baselineHost.Inner.TryGetOutput(QpdfPath.From(path), out byte[] expected));
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From(path), out byte[] actual)); Assert.Equal(expected, actual);
        }
        Assert.Equal(baseline.OutputText, result.OutputText);
        Assert.Equal(baseline.OutputBytes?.ToArray(), result.OutputBytes?.ToArray());
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    public static IEnumerable<object[]> PublicationJobs()
    {
        foreach (string route in new[] { "argv", "job", "mixed", "response" })
        foreach (string operation in new[] { "split", "split-linearized", "split-qdf", "split-streamed", "split-pass1", "json-file", "json-sidecar" })
        foreach (bool compressed in new[] { false, true })
        foreach (bool presentation in new[] { false, true }) { yield return [route, operation, compressed, presentation]; }
    }

    [Theory]
    [MemberData(nameof(PublicationJobs))]
    public async Task OneShortHostBudgetRejectsTheCompleteBatchWithAcceptedWarnings(string route, string operation, bool compressed, bool presentation)
    {
        string flags = presentation ? Flags : "";
        ObservedHost baselineHost = await Observed(operation, compressed, true);
        QpdfExecutionResult baseline = await RunObserved(baselineHost, route, operation, flags, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, baseline.Status);
        ObservedHost host = await Observed(operation, compressed, true);
        byte[][] aliases = Targets.Select(path => { Assert.True(host.Inner.TryGetOutput(QpdfPath.From(path), out byte[] bytes)); return bytes; }).ToArray();
        QpdfExecutionResult result = await RunObserved(host, route, operation, flags,
            QpdfLimits.Default with { MaxHostOperations = baselineHost.Operations - 1L }, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Empty(host.Committed); Assert.Equal(0, host.Creates);
        Assert.Equal(host.Operations, result.Counters.HostOperations);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded && item.Message.Contains(nameof(QpdfLimits.MaxHostOperations), StringComparison.Ordinal));
        Assert.Equal(baseline.Diagnostics.Where(item => item.Severity == QpdfDiagnosticSeverity.Warning).Select(item => item.Message),
            result.Diagnostics.Where(item => item.Severity == QpdfDiagnosticSeverity.Warning).Select(item => item.Message));
        foreach (string path in Targets) { Assert.True(host.Inner.TryGetOutput(QpdfPath.From(path), out byte[] bytes)); Assert.Equal(new byte[] { 17, 23, 41 }, bytes); }
        Assert.All(aliases, bytes => Assert.Equal(new byte[] { 17, 23, 41 }, bytes));
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount); GC.KeepAlive(aliases);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    [InlineData("mixed")]
    [InlineData("response")]
    public async Task JsonSidecarsReuseOneHandleAtAnExactHostBudget(string route)
    {
        ObservedHost baselineHost = await Observed("json-sidecar", true, true);
        QpdfExecutionResult baseline = await RunObserved(baselineHost, route, "json-sidecar", Flags, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, baseline.Status);
        ObservedHost host = await Observed("json-sidecar", true, true);
        QpdfExecutionResult result = await RunObserved(host, route, "json-sidecar", Flags,
            QpdfLimits.Default with { MaxOpenHandles = 1L, MaxHostOperations = baselineHost.Operations }, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(2, host.Committed.Count);
        Assert.Equal(1L, host.PeakHandles); Assert.Equal(host.Operations, result.Counters.HostOperations);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    public static IEnumerable<object[]> Faults()
    {
        foreach (string route in new[] { "argv", "job", "mixed", "response" })
        foreach (string operation in new[] { "whole", "split-pass1", "json-sidecar" })
        foreach (string boundary in new[] { "open", "read", "input-close", "create", "write", "commit", "output-close", "second-create", "second-write", "second-commit", "second-output-close" })
        foreach (bool cancel in new[] { false, true })
        { if (operation != "whole" || !boundary.StartsWith("second-", StringComparison.Ordinal)) { yield return [route, operation, boundary, cancel]; } }
    }

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task HostFailuresAndCancellationKeepExecutedCountersAndPublishedTargets(string route, string operation, string boundary, bool cancel)
    {
        using CancellationTokenSource cancellation = new();
        ObservedHost host = await Observed(operation, true, true);
        host.Boundary = boundary; host.Cancellation = cancel ? cancellation : null;
        byte[][] aliases = Targets.Select(path => { Assert.True(host.Inner.TryGetOutput(QpdfPath.From(path), out byte[] bytes)); return bytes; }).ToArray();
        QpdfExecutionResult result = await RunObserved(host, route, operation, Flags, QpdfLimits.Default, cancellation.Token);
        Assert.True(host.Triggered); Assert.Equal(cancel ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(host.Operations, result.Counters.HostOperations);
        long warnings = result.Diagnostics.Count(item => item.Severity == QpdfDiagnosticSeverity.Warning);
        Assert.InRange(result.Counters.DiagnosticCount, warnings, warnings + 6L);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code is QpdfDiagnosticCode.VerboseSummary or QpdfDiagnosticCode.ProgressReport or QpdfDiagnosticCode.MemoryUsage);
        int expectedCommits = boundary == "output-close" ? 1 : boundary == "second-output-close" ? 2 : boundary.StartsWith("second-", StringComparison.Ordinal) ? 1 : 0;
        Assert.Equal(expectedCommits, host.Committed.Count);
        foreach (string path in Targets)
        {
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From(path), out byte[] bytes));
            if (host.Committed.Contains(path)) { Assert.NotEqual(new byte[] { 17, 23, 41 }, bytes); }
            else { Assert.Equal(new byte[] { 17, 23, 41 }, bytes); }
        }
        Assert.All(aliases, bytes => Assert.Equal(new byte[] { 17, 23, 41 }, bytes));
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount); GC.KeepAlive(aliases);
    }

    [Theory]
    [InlineData("argv", "split")]
    [InlineData("job", "json-sidecar")]
    public async Task GroupAndSidecarBoundariesPreserveValidPublishedBytes(string route, string operation)
    {
        QpdfLimits limits = operation == "json-sidecar" ? QpdfLimits.Default with { MaxOpenHandles = 1L } : QpdfLimits.Default;
        ObservedHost baselineHost = await Observed(operation, true, true);
        QpdfExecutionResult baseline = await RunObserved(baselineHost, route, operation, Flags, limits, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, baseline.Status);
        Assert.Equal(2, baselineHost.Committed.Count);
        Assert.Equal(0L, baselineHost.Inner.OpenLeaseCount);
        Dictionary<string, byte[]> expected = new(StringComparer.Ordinal);
        foreach (string path in baselineHost.Committed)
        {
            Assert.True(baselineHost.Inner.TryGetOutput(QpdfPath.From(path), out byte[] bytes));
            expected.Add(path, bytes);
            if (operation == "split")
            {
                using UglyToad.PdfPig.PdfDocument pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
                Assert.Equal(1, pdf.NumberOfPages);
            }
        }
        if (operation == "json-sidecar")
        {
            using JsonDocument json = JsonDocument.Parse(expected["output.pdf"]);
            Assert.Equal("side-4", json.RootElement.GetProperty("qpdf")[1].GetProperty("obj:4 0 R")
                .GetProperty("stream").GetProperty("datafile").GetString());
            Assert.Equal("q Q\n"u8.ToArray(), expected["side-4"]);
        }

        // Each distinct boundary once per publication mechanism; the two late
        // cancellations also exercise a surviving already-committed output.
        foreach ((string boundary, string outcome) in new[]
        {
            ("read", "fault"), ("input-close", "fault"), ("create", "fault"),
            ("write", "fault"), ("commit", "fault"), ("output-close", "fault"),
            ("second-write", "caller-cancel"), ("second-output-close", "host-cancel"),
        })
        {
            using CancellationTokenSource cancellation = new();
            ObservedHost host = await Observed(operation, true, true);
            host.Boundary = boundary;
            host.Cancellation = outcome == "caller-cancel" ? cancellation : null;
            host.HostCancellation = outcome == "host-cancel";
            byte[][] aliases = Targets.Select(path => { Assert.True(host.Inner.TryGetOutput(QpdfPath.From(path), out byte[] bytes)); return bytes; }).ToArray();
            QpdfExecutionResult result = await RunObserved(host, route, operation, Flags, limits, cancellation.Token);
            Assert.True(host.Triggered);
            Assert.Equal(outcome == "fault" ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Canceled, result.Status);
            Assert.Equal(outcome == "caller-cancel", cancellation.IsCancellationRequested);
            Assert.Equal(boundary == "output-close" ? 1 : boundary == "second-output-close" ? 2
                : boundary.StartsWith("second-", StringComparison.Ordinal) ? 1 : 0, host.Committed.Count);
            Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
            foreach (string path in Targets)
            {
                Assert.True(host.Inner.TryGetOutput(QpdfPath.From(path), out byte[] bytes));
                Assert.Equal(host.Committed.Contains(path) ? expected[path] : new byte[] { 17, 23, 41 }, bytes);
            }
            Assert.All(aliases, bytes => Assert.Equal(new byte[] { 17, 23, 41 }, bytes));
        }
    }

    [Theory]
    [InlineData("split", false)]
    [InlineData("split", true)]
    [InlineData("json", false)]
    [InlineData("json", true)]
    public async Task TypedBatchesAdmitAllHostCallsAndKeepTheirThrowingContract(string operation, bool compressed)
    {
        ObservedHost baseline = await Observed("split", compressed, true);
        QpdfExecutionCounters counters = await Typed(baseline, operation, QpdfLimits.Default);
        Assert.Equal(baseline.Operations, counters.HostOperations);
        ObservedHost exact = await Observed("split", compressed, true);
        QpdfExecutionCounters result = await Typed(exact, operation, QpdfLimits.Default with { MaxHostOperations = baseline.Operations });
        Assert.Equal(exact.Operations, result.HostOperations); Assert.Equal(baseline.Committed, exact.Committed);
        ObservedHost shortHost = await Observed("split", compressed, true);
        QpdfLimitException exception = await Assert.ThrowsAsync<QpdfLimitException>(() => Typed(shortHost, operation,
            QpdfLimits.Default with { MaxHostOperations = baseline.Operations - 1L }));
        Assert.Equal(nameof(QpdfLimits.MaxHostOperations), exception.LimitName);
        Assert.Empty(shortHost.Committed); Assert.Equal(0, shortHost.Creates); Assert.Equal(0L, shortHost.Inner.OpenLeaseCount);
        foreach (string path in Targets) { Assert.True(shortHost.Inner.TryGetOutput(QpdfPath.From(path), out byte[] bytes)); Assert.Equal(new byte[] { 17, 23, 41 }, bytes); }
        Assert.Equal(0L, result.OpenHandles); Assert.Equal(0L, exact.Inner.OpenLeaseCount);
    }

    private static async Task<QpdfExecutionCounters> Typed(ObservedHost host, string operation, QpdfLimits limits)
    {
        if (operation == "json")
        {
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input.pdf"), QpdfPath.From("output.pdf"),
                new QpdfJsonOptions(2, [], "file", "side", [], false), limits, CancellationToken.None);
            return result.Counters;
        }
        PdfMultiPageSelectionResult pages = await Qpdf.SelectPagesAsync(host,
            [new PdfPageSource(QpdfPath.From("input.pdf"), PdfPageSelectionOptions.FromPages([1, 1]))],
            QpdfPath.From("output.pdf"), new PdfPageMergeOptions { SplitPages = 1 }, limits, CancellationToken.None);
        return pages.Counters;
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    [InlineData("mixed")]
    [InlineData("response")]
    public async Task NoSummaryMemoryFailuresAfterStagingPreserveEverySplitTarget(string route)
    {
        byte[] random = new byte[35000]; new Random(1729).NextBytes(random);
        byte[] body = Encoding.ASCII.GetBytes("%" + Convert.ToHexString(random) + "\n");
        List<(int Number, string Dict, byte[]? StreamData)> objects = CheckTests.SinglePage("<< /Length " + body.Length + " >>", body);
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /Duplicate 1 /Duplicate 2 >>", null);
        byte[] pdf = CheckTests.Assemble(objects, 1);
        int lateFailures = 0;
        // Fixed ceilings span the observed overlap of successive prepared outputs.
        // Weak retirement can move an individual boundary. Every outcome must
        // obey the publication contract, and this route must actually fail late.
        for (long ceiling = 2000000L; ceiling <= 2600000L; ceiling += 50000L)
        {
            object host = await Prepare("split-pass1", false, false);
            Inner(host).AddInput(QpdfPath.From("input.pdf"), pdf, QpdfInputOwnership.Copy);
            string[] targets = [.. Enumerable.Range(1, 8).Select(index => $"output-{index}.pdf"), "pass1.pdf"];
            foreach (string target in targets)
            {
                await using IQpdfOutputTransaction seed = await Inner(host).CreateOutputAsync(QpdfPath.From(target), CancellationToken.None);
                await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
            }
            byte[][] aliases = targets.Select(target =>
            { Assert.True(Inner(host).TryGetOutput(QpdfPath.From(target), out byte[] bytes)); return bytes; }).ToArray();
            List<string> argv = ["input.pdf", "--linearize", "--linearize-pass1=pass1.pdf", "--split-pages=1", "--pages"];
            for (int index = 0; index < 8; index++) { argv.AddRange([".", "1"]); }
            argv.AddRange(["--", "output.pdf"]);
            QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxMemoryBytes = ceiling });
            QpdfExecutionResult result;
            if (route == "job")
            {
                result = await Qpdf.ExecuteJobAsync((IQpdfHost)host, JsonSerializer.Serialize(new
                { inputFile = "input.pdf", outputFile = "output.pdf", linearize = "", linearizePass1 = "pass1.pdf", splitPages = "1", pages = Enumerable.Range(0, 8).Select(_ => new { file = ".", range = "1" }).ToArray() }), options, CancellationToken.None);
            }
            else
            {
                if (route == "mixed") { argv.Insert(1, "--job-json-file=settings.json"); }
                if (route == "response")
                { Inner(host).AddInput(QpdfPath.From("args.txt"), Encoding.UTF8.GetBytes(string.Join('\n', argv) + "\n"), QpdfInputOwnership.Copy); argv = ["@args.txt"]; }
                result = await Qpdf.ExecuteAsync((IQpdfHost)host, new QpdfCommandInvocation(argv), options, CancellationToken.None);
            }
            Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, Inner(host).OpenLeaseCount);
            Assert.InRange(result.Counters.PeakMemoryBytes, 1L, ceiling);
            Assert.All(aliases, alias => Assert.Equal(new byte[] { 17, 23, 41 }, alias));
            if (result.Status == QpdfExecutionStatus.Completed)
            { Assert.Equal(16, Commits(host)); Assert.Equal(QpdfExitCode.Warning, result.ExitCode); }
            else
            {
                Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(0, Commits(host));
                Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded && item.Message.Contains(nameof(QpdfLimits.MaxMemoryBytes), StringComparison.Ordinal));
                Assert.DoesNotContain(result.Diagnostics, item => item.Code is QpdfDiagnosticCode.VerboseSummary or QpdfDiagnosticCode.ProgressReport or QpdfDiagnosticCode.MemoryUsage);
                Assert.Equal(result.Counters.DiagnosticCount, result.Diagnostics.Count(item => item.Code == QpdfDiagnosticCode.DuplicateKey));
                foreach (string target in targets)
                { Assert.True(Inner(host).TryGetOutput(QpdfPath.From(target), out byte[] bytes)); Assert.Equal(new byte[] { 17, 23, 41 }, bytes); }
                if (result.Counters.TotalOutputBytes > 0L) { lateFailures++; }
            }
            GC.KeepAlive(result); GC.KeepAlive(aliases);
        }
        Assert.True(lateFailures > 0, "The fixed public memory controls must include a failure after serialized output is staged.");
    }

    [Theory]
    [InlineData("typed", true)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    [InlineData("typed", false)]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    public async Task JsonSidecarCannotOverwriteMainOutput(string route, bool collision)
    {
        ObservedHost host = await Observed("json-sidecar", false, false);
        QpdfPath output = QpdfPath.From(collision ? "side-4" : "output.pdf");
        if (route == "typed")
        {
            QpdfJsonOptions options = new(2, ["qpdf"], "file", "side", ["4"], false);
            if (collision)
            {
                QpdfParseException error = await Assert.ThrowsAsync<QpdfParseException>(() =>
                    Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input.pdf"), output, options, CancellationToken.None).AsTask());
                Assert.Contains("differ", error.Message, StringComparison.Ordinal);
            }
            else
            {
                QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input.pdf"), output, options, CancellationToken.None);
                Assert.Equal(0L, result.Counters.OpenHandles);
                using JsonDocument returned = JsonDocument.Parse(result.Json);
                Assert.True(returned.RootElement.TryGetProperty("qpdf", out _));
            }
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input.pdf", "--json=2", "--json-key=qpdf",
                    "--json-stream-data=file", "--json-stream-prefix=side", "--json-object=4", "--", output.Value]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
                {
                    inputFile = "input.pdf", outputFile = output.Value, json = "2", jsonKey = new[] { "qpdf" },
                    jsonStreamData = "file", jsonStreamPrefix = "side", jsonObject = new[] { "4" },
                }), CancellationToken.None);
            Assert.Equal(collision ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal(collision ? QpdfExitCode.Error : QpdfExitCode.Success, result.ExitCode);
            Assert.Equal(0L, result.Counters.OpenHandles);
            if (collision) { Assert.Contains(result.Diagnostics, item => item.Message.Contains("differ", StringComparison.Ordinal)); }
        }

        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        if (collision)
        {
            Assert.Equal(0, host.Creates); Assert.Empty(host.Committed);
            foreach (string target in Targets)
            {
                Assert.True(host.Inner.TryGetOutput(QpdfPath.From(target), out byte[] prior));
                Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            }
        }
        else
        {
            Assert.Equal(new[] { "output.pdf", "side-4" }, host.Committed);
            Assert.True(host.Inner.TryGetOutput(output, out byte[] report));
            using JsonDocument published = JsonDocument.Parse(report);
            Assert.Equal("side-4", published.RootElement.GetProperty("qpdf")[1].GetProperty("obj:4 0 R")
                .GetProperty("stream").GetProperty("datafile").GetString());
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From("side-4"), out byte[] payload));
            Assert.Equal("q Q\n"u8.ToArray(), payload);
        }
    }

    public static IEnumerable<object[]> FirstPassTargets()
    {
        foreach (string route in new[] { "argv", "job" })
        foreach (bool streamed in new[] { false, true })
        foreach (string shape in new[] { "whole", "split-first", "split-later", "whole-safe", "split-safe" })
        { yield return [route, streamed, shape]; }
    }

    [Theory]
    [MemberData(nameof(FirstPassTargets))]
    public async Task ActiveFirstPassCannotOverwriteAnyMainOutput(string route, bool streamed, string shape)
    {
        ObservedHost host = await Observed("split-pass1", false, false);
        bool split = shape.StartsWith("split-", StringComparison.Ordinal);
        bool collision = !shape.EndsWith("-safe", StringComparison.Ordinal);
        string pass1 = shape switch { "whole" => "output.pdf", "split-first" => "output-1.pdf",
            "split-later" => "output-2.pdf", _ => "pass1.pdf" };
        List<string> argv = ["input.pdf", "--linearize", "--linearize-pass1=" + pass1];
        Dictionary<string, object> job = new(StringComparer.Ordinal)
        { ["inputFile"] = "input.pdf", ["outputFile"] = "output.pdf", ["linearize"] = "", ["linearizePass1"] = pass1 };
        if (split)
        {
            argv.AddRange(["--split-pages=1", "--pages", ".", "1", ".", "1", "--"]);
            job.Add("splitPages", "1"); job.Add("pages", new[] { new { file = ".", range = "1" }, new { file = ".", range = "1" } });
        }
        if (streamed) { argv.Add("--object-streams=generate"); job.Add("objectStreams", "generate"); }
        argv.Add("output.pdf");
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
        Assert.Equal(collision ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(collision ? QpdfExitCode.Error : QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
        if (collision)
        {
            Assert.Contains(result.Diagnostics, item => item.Message.Contains("differ", StringComparison.Ordinal));
            Assert.Equal(0, host.Creates); Assert.Empty(host.Committed);
            foreach (string target in Targets)
            {
                Assert.True(host.Inner.TryGetOutput(QpdfPath.From(target), out byte[] prior));
                Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            }
        }
        else
        {
            Assert.Equal(split ? new[] { "output-1.pdf", pass1, "output-2.pdf", pass1 } : new[] { "output.pdf", pass1 }, host.Committed);
            foreach (string target in split ? new[] { "output-1.pdf", "output-2.pdf" } : new[] { "output.pdf" })
            {
                Assert.True(host.Inner.TryGetOutput(QpdfPath.From(target), out byte[] bytes));
                host.Inner.AddInput(QpdfPath.From("readback.pdf"), bytes, QpdfInputOwnership.Copy);
                await using (QpdfDocument document = await QpdfDocument.OpenAsync(host.Inner, QpdfPath.From("readback.pdf"), CancellationToken.None))
                { Assert.Equal(1, document.PageCount); }
                QpdfCheckResult check = await Qpdf.CheckAsync(host.Inner, QpdfPath.From("readback.pdf"), CancellationToken.None);
                Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(check.Checks, item => item.Name == "linearization").Status);
            }
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From(pass1), out byte[] capture));
            Assert.True(capture.Length > 3);
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
        }
    }

    [Theory]
    [InlineData("argv", false, false)]
    [InlineData("job", false, false)]
    [InlineData("argv", true, false)]
    [InlineData("job", true, false)]
    [InlineData("argv", false, true)]
    [InlineData("job", false, true)]
    [InlineData("argv", true, true)]
    [InlineData("job", true, true)]
    public async Task InactiveFirstPassDoesNotCreateOrOverwriteCapture(string route, bool split, bool aliasesMain)
    {
        string capture = aliasesMain ? split ? "output-2.pdf" : "output.pdf" : "pass1.pdf";
        async Task<QpdfExecutionResult> RunCapture(ObservedHost host, bool includeCapture, QpdfLimits limits)
        {
            List<string> argv = ["input.pdf"];
            Dictionary<string, object> job = new(StringComparer.Ordinal)
            { ["inputFile"] = "input.pdf", ["outputFile"] = "output.pdf" };
            if (includeCapture) { argv.Add("--linearize-pass1=" + capture); job.Add("linearizePass1", capture); }
            if (split)
            {
                argv.AddRange(["--split-pages=1", "--pages", ".", "1", ".", "1", "--"]);
                job.Add("splitPages", "1"); job.Add("pages", new[] { new { file = ".", range = "1" }, new { file = ".", range = "1" } });
            }
            argv.Add("output.pdf");
            return route == "argv"
                ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), new QpdfExecutionOptions(limits), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), new QpdfExecutionOptions(limits), CancellationToken.None);
        }

        ObservedHost baseline = await Observed("split", false, false);
        QpdfExecutionResult control = await RunCapture(baseline, false, QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Success, control.ExitCode);
        ObservedHost host = await Observed("split", false, false);
        QpdfExecutionResult result = await RunCapture(host, true, QpdfLimits.Default with { MaxHostOperations = baseline.Operations });
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(baseline.Creates, host.Creates); Assert.Equal(baseline.Committed, host.Committed);
        Assert.Equal(baseline.Operations, host.Operations); Assert.Equal(control.Counters.TotalOutputBytes, result.Counters.TotalOutputBytes);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
        foreach (string path in host.Committed)
        {
            Assert.True(baseline.Inner.TryGetOutput(QpdfPath.From(path), out byte[] expected));
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From(path), out byte[] actual));
            Assert.Equal(expected, actual);
            host.Inner.AddInput(QpdfPath.From("readback.pdf"), actual, QpdfInputOwnership.Copy);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host.Inner, QpdfPath.From("readback.pdf"), CancellationToken.None))
            { Assert.Equal(1, document.PageCount); }
        }
        if (!aliasesMain)
        {
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From(capture), out byte[] prior));
            Assert.Equal(new byte[] { 17, 23, 41 }, prior);
        }
        Assert.Equal(0L, baseline.Inner.OpenLeaseCount); Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    private sealed class ObservedHost(InMemoryQpdfHost inner) : IQpdfHost
    {
        internal InMemoryQpdfHost Inner => inner;
        internal long Operations;
        internal long PeakHandles;
        internal int Creates;
        internal List<string> Committed { get; } = new();
        internal string Boundary = "";
        internal CancellationTokenSource? Cancellation;
        internal bool HostCancellation;
        internal bool Triggered;

        private void Check(string phase, QpdfPath path, int output)
        {
            string expected = Boundary.StartsWith("second-", StringComparison.Ordinal) ? Boundary[7..] : Boundary;
            int index = Boundary.StartsWith("second-", StringComparison.Ordinal) ? 2 : 1;
            if (Triggered || phase != expected || output > 0 && output != index) { return; }
            Triggered = true;
            if (HostCancellation) { throw new OperationCanceledException(new CancellationToken(true)); }
            if (Cancellation is not null) { Cancellation.Cancel(); throw new OperationCanceledException(Cancellation.Token); }
            throw new QpdfHostException(path, QpdfHostFailureReason.ReadFailed);
        }

        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            Operations++; Check("open", path, 0);
            IQpdfInput input = await inner.OpenInputAsync(path, token);
            PeakHandles = Math.Max(PeakHandles, inner.OpenLeaseCount);
            return new Input(this, input);
        }

        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            Operations++; Creates++; Check("create", path, Creates);
            IQpdfOutputTransaction transaction = await inner.CreateOutputAsync(path, token);
            PeakHandles = Math.Max(PeakHandles, inner.OpenLeaseCount);
            return new Output(this, transaction, Creates);
        }

        private sealed class Input(ObservedHost host, IQpdfInput input) : IQpdfInput
        {
            public QpdfPath Path => input.Path;
            public string Identity => input.Identity;
            public long Length => input.Length;
            public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token)
            { host.Operations++; host.Check("read", Path, 0); return input.ReadAsync(offset, count, token); }
            public async ValueTask DisposeAsync()
            { QpdfPath path = Path; await input.DisposeAsync(); host.Check("input-close", path, 0); }
        }

        private sealed class Output(ObservedHost host, IQpdfOutputTransaction transaction, int index) : IQpdfOutputTransaction
        {
            public QpdfPath Path => transaction.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
            { host.Operations++; host.Check("write", Path, index); return transaction.WriteAsync(data, token); }
            public async ValueTask CommitAsync(CancellationToken token)
            { host.Check("commit", Path, index); await transaction.CommitAsync(token); host.Committed.Add(Path.Value); }
            public ValueTask DiscardAsync(CancellationToken token) => transaction.DiscardAsync(token);
            public async ValueTask DisposeAsync()
            { QpdfPath path = Path; await transaction.DisposeAsync(); host.Check("output-close", path, index); }
        }
    }
}
