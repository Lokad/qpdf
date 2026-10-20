using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class DiagnosticFailureExportTests
{
    private readonly ITestOutputHelper _output;

    public DiagnosticFailureExportTests(ITestOutputHelper output) => _output = output;

    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Methods = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    // Preserve the original complete fifty-page characterization fixture.
    private static byte[] WarningPdf()
        => BuildWarningPdf(null, false);

    private static byte[] StreamWarningPdf(bool compressed)
    {
        byte[] body = "q Q\n"u8.ToArray();
        if (compressed)
        {
            using MemoryStream buffer = new();
            using (ZLibStream encoder = new(buffer, CompressionLevel.Optimal, true)) { encoder.Write(body); }
            body = buffer.ToArray();
        }
        return BuildWarningPdf(body, compressed);
    }

    private static byte[] BuildWarningPdf(byte[]? body, bool compressed)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 50 /Kids [" + string.Join(" ", Enumerable.Range(3, 50).Select(x => x + " 0 R")) + "] >>", null),
        ];
        for (int index = 0; index < 50; index++)
        {
            string key = string.Concat(Enumerable.Range(0, 80).Select(j => "#" + (128 + (index + j) % 128).ToString("X2")));
            objects.Add((3 + index, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10]"
                + (body is null ? "" : " /Contents 53 0 R")
                + " /Resources << /" + key + " 1 /" + key + " 2 >> >>", null));
        }
        if (body is not null)
        { objects.Add((53, "<< /Length " + body.Length + (compressed ? " /Filter /FlateDecode" : "") + " >>", body)); }
        return CheckTests.Assemble(objects, 1);
    }

    private static async Task<object> Prepare()
    {
        MethodInfo method = typeof(PresentationByteTests).GetMethod("Prepare", Methods)
            ?? throw new InvalidOperationException("Missing canonical public fixture driver.");
        Task task = (Task)(method.Invoke(null, ["json-sidecar", false, false])
            ?? throw new InvalidOperationException("Missing fixture task."));
        await task;
        object host = task.GetType().GetProperty("Result")?.GetValue(task)
            ?? throw new InvalidOperationException("Missing prepared host.");
        Inner(host).AddInput(QpdfPath.From("input.pdf"), WarningPdf(), QpdfInputOwnership.Copy);
        return host;
    }

    private static InMemoryQpdfHost Inner(object host)
        => (InMemoryQpdfHost)(host.GetType().GetProperty("Inner", Fields)?.GetValue(host)
            ?? throw new InvalidOperationException("Missing host storage."));

    private static async Task<QpdfExecutionResult> Run(IQpdfHost host, string route, bool summaries, bool noWarn,
        QpdfLimits limits, CancellationToken token)
        => await RunMode(host, route, summaries, noWarn, "pages", limits, token);

    private static async Task<QpdfExecutionResult> RunMode(IQpdfHost host, string route, bool summaries, bool noWarn,
        string mode, QpdfLimits limits, CancellationToken token)
    {
        if (route == "job")
        {
            Dictionary<string, object> job = new()
            {
                ["inputFile"] = "input.pdf",
                ["json"] = "2",
                ["testJsonSchema"] = "",
            };
            if (mode != "json-inline") { job.Add("outputFile", "output.pdf"); }
            if (mode == "pages") { job.Add("jsonKey", new[] { "pages" }); }
            else
            {
                job.Add("jsonStreamData", mode == "json-sidecar" ? "file" : "inline");
                if (mode == "json-sidecar") { job.Add("jsonStreamPrefix", "side"); }
            }
            if (summaries) { job.Add("verbose", ""); job.Add("progress", ""); job.Add("reportMemoryUsage", ""); }
            if (noWarn) { job.Add("noWarn", ""); }
            return await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), new QpdfExecutionOptions(limits), token);
        }
        List<string> argv = ["input.pdf", "--json=2", "--test-json-schema"];
        if (mode == "pages") { argv.Add("--json-key=pages"); }
        else
        {
            argv.Add("--json-stream-data=" + (mode == "json-sidecar" ? "file" : "inline"));
            if (mode == "json-sidecar") { argv.Add("--json-stream-prefix=side"); }
        }
        if (summaries) { argv.AddRange(["--verbose", "--progress", "--report-memory-usage"]); }
        if (noWarn) { argv.Add("--no-warn"); }
        if (mode != "json-inline") { argv.AddRange(["--", "output.pdf"]); }
        if (route == "mixed") { argv.Insert(1, "--job-json-file=settings.json"); }
        if (route == "response")
        {
            Inner(host).AddInput(QpdfPath.From("args.txt"), Encoding.UTF8.GetBytes(string.Join('\n', argv) + "\n"), QpdfInputOwnership.Copy);
            argv = ["@args.txt"];
        }
        return await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), new QpdfExecutionOptions(limits), token);
    }

    // Follow strong diagnostic storage, independently of a particular projection
    // field name. Weak ledger entries are not roots of a returned public view.
    private static QpdfDiagnostic[] ReachableDiagnostics(object root)
    {
        List<QpdfDiagnostic> found = new();
        Stack<object> pending = new();
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.TryPop(out object? current))
        {
            if (!seen.Add(current)) { continue; }
            Assert.InRange(seen.Count, 1, 10000);
            if (current is QpdfDiagnostic diagnostic) { found.Add(diagnostic); continue; }
            if (current is string or WeakReference) { continue; }
            if (current is Array array)
            { foreach (object? item in array) { if (item is not null) { pending.Push(item); } } continue; }
            Type type = current.GetType();
            if (type.IsValueType || type.IsPrimitive || type.IsEnum) { continue; }
            foreach (FieldInfo field in type.GetFields(Fields))
            {
                if (field.GetValue(current) is object value
                    && (value is IReadOnlyList<QpdfDiagnostic> || value is Array)) { pending.Push(value); }
            }
        }
        return found.ToArray();
    }

    private static void Collect()
    { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    [InlineData("mixed")]
    [InlineData("response")]
    [Trait("Layer", "Ownership")]
    public async Task OriginalMixedNoWarnFailureDoesNotRootHiddenWarnings(string route)
    {
        object host = await Prepare();
        Assert.True(Inner(host).TryGetOutput(QpdfPath.From("output.pdf"), out byte[] alias));
        QpdfExecutionResult result = await Run((IQpdfHost)host, route, true, true,
            QpdfLimits.Default with { MaxDiagnostics = 3L }, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Equal(3L, result.Counters.DiagnosticCount); Assert.Equal(2, result.Diagnostics.Count);
        Assert.Equal(QpdfDiagnosticCode.JsonSchemaCheck, result.Diagnostics[0].Code);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, result.Diagnostics[1].Code);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, Inner(host).OpenLeaseCount);
        Assert.Null(result.OutputText); Assert.Null(result.OutputBytes);
        Assert.Equal(new byte[] { 17, 23, 41 }, alias);
        Assert.True(Inner(host).TryGetOutput(QpdfPath.From("output.pdf"), out byte[] target)); Assert.Equal(alias, target);
        Collect();
        Assert.DoesNotContain(ReachableDiagnostics(result.Diagnostics), item => item.Severity == QpdfDiagnosticSeverity.Warning);
        GC.KeepAlive(result); GC.KeepAlive(alias);
    }

    public static IEnumerable<object[]> InputCloseJobs()
    {
        foreach (string route in new[] { "argv", "job", "mixed", "response" })
        foreach (bool summaries in new[] { false, true })
        foreach (bool noWarn in new[] { false, true })
        foreach (string outcome in new[] { "success", "fault", "cancel" })
        { yield return [route, summaries, noWarn, outcome]; }
    }

    [Theory]
    [MemberData(nameof(InputCloseJobs))]
    [Trait("Layer", "Contract")]
    public async Task InputCloseAfterPublicWarningAcceptancePreservesHistory(string route, bool summaries, bool noWarn, string outcome)
        => await ExerciseInputClose(route, summaries, noWarn, outcome, "pages", false);

    public static IEnumerable<object[]> OutputCloseJobs()
    {
        foreach (string route in new[] { "argv", "job", "mixed", "response" })
        foreach (string mode in new[] { "json-file", "json-inline", "json-sidecar" })
        foreach (bool compressed in new[] { false, true })
        foreach (bool summaries in new[] { false, true })
        foreach (bool noWarn in new[] { false, true })
        foreach (string outcome in new[] { "success", "fault", "cancel" })
        { yield return [route, mode, compressed, summaries, noWarn, outcome]; }
    }

    [Theory]
    [MemberData(nameof(OutputCloseJobs))]
    [Trait("Layer", "Contract")]
    public async Task OutputJobsPreserveAcceptedWarningsAndEveryPriorTarget(string route, string mode,
        bool compressed, bool summaries, bool noWarn, string outcome)
        => await ExerciseInputClose(route, summaries, noWarn, outcome, mode, compressed);

    private static async Task ExerciseInputClose(string route, bool summaries, bool noWarn, string outcome,
        string mode, bool compressed)
    {
        MethodInfo prepare = typeof(ExecutionCounterTests).GetMethod("Prepare", Methods)
            ?? throw new InvalidOperationException("Missing observed host driver.");
        Task prepared = (Task)(prepare.Invoke(null, ["argv-json", false, "input-close", outcome == "fault" ? "fault" : "success"])
            ?? throw new InvalidOperationException("Missing prepared task."));
        await prepared;
        object host = prepared.GetType().GetProperty("Result")?.GetValue(prepared)
            ?? throw new InvalidOperationException("Missing observed host.");
        host.GetType().GetField("PausePath", Fields)?.SetValue(host, QpdfPath.From("input.pdf"));
        Inner(host).AddInput(QpdfPath.From("input.pdf"), mode == "pages" ? WarningPdf() : StreamWarningPdf(compressed), QpdfInputOwnership.Copy);
        Inner(host).AddInput(QpdfPath.From("settings.json"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction seed = await Inner(host).CreateOutputAsync(QpdfPath.From("side-53"), CancellationToken.None))
        { await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        Assert.True(Inner(host).TryGetOutput(QpdfPath.From("output.pdf"), out byte[] alias));
        Assert.True(Inner(host).TryGetOutput(QpdfPath.From("side-53"), out byte[] sideAlias));
        using CancellationTokenSource cancellation = new();
        Task<QpdfExecutionResult> running = RunMode((IQpdfHost)host, route, summaries, noWarn, mode,
            QpdfLimits.Default with { MaxOpenHandles = 1L }, cancellation.Token);
        Task entered = (Task)(host.GetType().GetProperty("Entered", Fields)?.GetValue(host)
            ?? throw new InvalidOperationException("Missing host observation."));
        await entered.WaitAsync(TimeSpan.FromSeconds(10));
        MethodInfo find = typeof(ExecutionCounterTests).GetMethod("FindGovernor", Methods)
            ?? throw new InvalidOperationException("Missing governor observation driver.");
        ResourceGovernor governor = (ResourceGovernor)(find.Invoke(null, [running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)])
            ?? throw new InvalidOperationException("Expected the actual public governor."));
        Assert.Equal(noWarn ? 1L : 51L, governor.Snapshot().DiagnosticCount); Assert.False(running.IsCompleted);
        if (outcome == "cancel") { cancellation.Cancel(); }
        else { host.GetType().GetMethod("Resume", Fields)?.Invoke(host, null); }
        QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(outcome == "success" ? QpdfExecutionStatus.Completed : outcome == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(noWarn ? 0 : 50, result.Diagnostics.Count(item => item.Code == QpdfDiagnosticCode.DuplicateKey));
        Assert.Equal(new byte[] { 17, 23, 41 }, alias); Assert.Equal(alias, sideAlias);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, Inner(host).OpenLeaseCount);
        if (outcome != "success")
        {
            Assert.InRange(result.Counters.DiagnosticCount, noWarn ? 1L : 51L, (noWarn ? 1L : 51L) + (summaries ? 6L : 0L)); Assert.Null(result.OutputText); Assert.Null(result.OutputBytes);
            Assert.Equal(new byte[] { 17, 23, 41 }, alias);
            Assert.True(Inner(host).TryGetOutput(QpdfPath.From("output.pdf"), out byte[] target)); Assert.Equal(alias, target);
            Assert.True(Inner(host).TryGetOutput(QpdfPath.From("side-53"), out byte[] sideTarget)); Assert.Equal(sideAlias, sideTarget);
            Assert.DoesNotContain(result.Diagnostics, item => item.Code is QpdfDiagnosticCode.VerboseSummary or QpdfDiagnosticCode.ProgressReport or QpdfDiagnosticCode.MemoryUsage);
        }
        else
        {
            Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
            if (mode == "json-inline") { Assert.NotNull(result.OutputText); }
            if (mode == "json-sidecar")
            {
                Assert.True(Inner(host).TryGetOutput(QpdfPath.From("side-53"), out byte[] stream));
                Assert.Equal("q Q\n"u8.ToArray(), stream);
            }
        }
        GC.KeepAlive(result); GC.KeepAlive(alias); GC.KeepAlive(governor);
    }

    public static IEnumerable<object[]> MemoryJobs()
    {
        foreach (string route in new[] { "argv", "job", "mixed", "response" })
        foreach (bool summaries in new[] { false, true })
        foreach (bool noWarn in new[] { false, true }) { yield return [route, summaries, noWarn]; }
    }

    [Theory]
    [MemberData(nameof(MemoryJobs))]
    [Trait("Layer", "Contract")]
    public async Task FixedPublicMemoryFailuresExportEveryAcceptedVisibleWarning(string route, bool summaries, bool noWarn)
        => await ExerciseMemory(route, summaries, noWarn, "pages", false);

    public static IEnumerable<object[]> OutputMemoryJobs()
    {
        foreach (string route in new[] { "argv", "job", "mixed", "response" })
        foreach (string mode in new[] { "json-file", "json-inline", "json-sidecar" })
        foreach (bool compressed in new[] { false, true })
        foreach (bool summaries in new[] { false, true })
        foreach (bool noWarn in new[] { false, true }) { yield return [route, mode, compressed, summaries, noWarn]; }
    }

    [Theory]
    [MemberData(nameof(OutputMemoryJobs))]
    [Trait("Layer", "Contract")]
    public async Task OutputMemoryFailuresKeepTheAcceptedPrefixAndSurvivingTargets(string route, string mode,
        bool compressed, bool summaries, bool noWarn)
        => await ExerciseMemory(route, summaries, noWarn, mode, compressed);

    private static async Task ExerciseMemory(string route, bool summaries, bool noWarn, string mode, bool compressed)
    {
        object baselineHost = await Prepare();
        if (mode != "pages") { Inner(baselineHost).AddInput(QpdfPath.From("input.pdf"), StreamWarningPdf(compressed), QpdfInputOwnership.Copy); }
        QpdfExecutionResult baseline = await RunMode((IQpdfHost)baselineHost, route, false, false, mode,
            QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, baseline.Status);
        string[] accepted = baseline.Diagnostics.Where(item => item.Code == QpdfDiagnosticCode.DuplicateKey)
            .Select(item => item.Message).ToArray();
        Assert.Equal(50, accepted.Length);
        int witnessed = 0;
        for (long ceiling = 60000L; ceiling <= 150000L; ceiling += 10000L)
        {
            object host = await Prepare();
            if (mode != "pages") { Inner(host).AddInput(QpdfPath.From("input.pdf"), StreamWarningPdf(compressed), QpdfInputOwnership.Copy); }
            await using (IQpdfOutputTransaction seed = await Inner(host).CreateOutputAsync(QpdfPath.From("side-53"), CancellationToken.None))
            { await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
            Assert.True(Inner(host).TryGetOutput(QpdfPath.From("output.pdf"), out byte[] alias));
            Assert.True(Inner(host).TryGetOutput(QpdfPath.From("side-53"), out byte[] sideAlias));
            QpdfExecutionResult result = await RunMode((IQpdfHost)host, route, summaries, noWarn, mode,
                QpdfLimits.Default with { MaxMemoryBytes = ceiling, MaxOpenHandles = 1L }, CancellationToken.None);
            Assert.InRange(result.Counters.PeakMemoryBytes, 0L, ceiling);
            Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, Inner(host).OpenLeaseCount);
            if (result.Status == QpdfExecutionStatus.Completed) { continue; }
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded
                && item.Message.Contains(nameof(QpdfLimits.MaxMemoryBytes), StringComparison.Ordinal));
            long warnings = Math.Min(50L, result.Counters.DiagnosticCount);
            Assert.Equal(noWarn ? 0L : warnings, result.Diagnostics.LongCount(item => item.Code == QpdfDiagnosticCode.DuplicateKey));
            Assert.Equal(accepted.Take(noWarn ? 0 : (int)warnings), result.Diagnostics
                .Where(item => item.Code == QpdfDiagnosticCode.DuplicateKey).Select(item => item.Message));
            if (noWarn || warnings != 0L) { witnessed++; }
            Assert.Null(result.OutputText); Assert.Null(result.OutputBytes);
            Assert.DoesNotContain(result.Diagnostics, item => item.Code is QpdfDiagnosticCode.VerboseSummary or QpdfDiagnosticCode.ProgressReport or QpdfDiagnosticCode.MemoryUsage);
            Assert.Equal(new byte[] { 17, 23, 41 }, alias);
            Assert.True(Inner(host).TryGetOutput(QpdfPath.From("output.pdf"), out byte[] target)); Assert.Equal(alias, target);
            Assert.True(Inner(host).TryGetOutput(QpdfPath.From("side-53"), out byte[] sideTarget)); Assert.Equal(sideAlias, sideTarget);
            GC.KeepAlive(result); GC.KeepAlive(alias);
        }
        Assert.InRange(witnessed, 1, 10);
    }

    }
