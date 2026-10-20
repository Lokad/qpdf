using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Foundation;

// Lifetime controls collect process-wide. Keep them in the existing exclusive
// collection so they cannot change another job's observed peak mid-comparison.
[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class PresentationOwnershipTests
{

    // Reuse the preceding public drivers verbatim, retaining their fixtures,
    // routes and publication assertions while adding independent obligations.
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

    public static IEnumerable<object[]> JsonWarningPolicies()
    {
        foreach (string route in new[] { "argv", "job", "mixed", "response" })
        foreach (bool noWarn in new[] { false, true })
        foreach (bool warningExitZero in new[] { false, true }) { yield return [route, noWarn, warningExitZero]; }
    }

    [Theory]
    [MemberData(nameof(JsonWarningPolicies))]
    public async Task JsonSidecarsPreserveWarningExitPolicyAndSuppressOnlyPresentation(string route, bool noWarn, bool warningExitZero)
    {
        object host = await Prepare("json-sidecar", false, true);
        QpdfExecutionResult result = await Run(host, route, "json-sidecar", "verbose progress report-memory-usage"
            + (noWarn ? " no-warn" : "") + (warningExitZero ? " warning-exit-0" : ""), QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(warningExitZero ? QpdfExitCode.Success : QpdfExitCode.Warning, result.ExitCode);
        Assert.Equal(noWarn ? 0 : 1, result.Diagnostics.Count(item => item.Code == QpdfDiagnosticCode.DuplicateKey));
        Assert.Equal(noWarn ? 6L : 7L, result.Counters.DiagnosticCount); Assert.Equal(2, Commits(host));
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, Inner(host).OpenLeaseCount);
    }

    [Fact]
    public async Task FailureDiagnosticsRemainReadableAfterAnotherPublicJob()
    {
        const string flags = "verbose progress report-memory-usage";
        object baselineHost = await Prepare("whole", false, true);
        QpdfExecutionResult baseline = await Run(baselineHost, "argv", "whole", flags, QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Completed, baseline.Status);
        long ceiling = baseline.Diagnostics.Sum(item => (long)Encoding.UTF8.GetByteCount(item.Message)) - 1L;
        object host = await Prepare("whole", false, true);
        QpdfExecutionResult failed = await Run(host, "argv", "whole", flags,
            QpdfLimits.Default with { MaxDiagnosticBytes = ceiling });
        Assert.Equal(QpdfExecutionStatus.Failed, failed.Status);
        Assert.Equal(QpdfExitCode.Error, failed.ExitCode);
        IReadOnlyList<QpdfDiagnostic> diagnostics = failed.Diagnostics;
        (QpdfDiagnosticCode Code, string Message)[] expected = diagnostics.Select(item => (item.Code, item.Message)).ToArray();
        Assert.Contains(expected, item => item.Code == QpdfDiagnosticCode.DuplicateKey);
        Assert.Contains(expected, item => item.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.Equal(0L, Inner(host).OpenLeaseCount);

        QpdfExecutionResult completed = await Run(host, "argv", "whole", "", QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Completed, completed.Status);
        Assert.Equal(expected, diagnostics.Select(item => (item.Code, item.Message)).ToArray());
        Assert.Equal(expected.Length, diagnostics.Count);
        for (int index = 0; index < expected.Length; index++)
        { Assert.Equal(expected[index], (diagnostics[index].Code, diagnostics[index].Message)); }
        Assert.Equal(0L, Inner(host).OpenLeaseCount);
    }

    public static IEnumerable<object[]> WarningJobs()
    {
        foreach (string route in new[] { "argv", "job", "mixed", "response" })
        foreach (string operation in new[] { "whole", "split", "json-sidecar" })
        foreach (bool compressed in new[] { false, true })
        foreach (bool noWarn in new[] { false, true })
        { yield return [route, operation, compressed, noWarn]; }
    }

    [Theory]
    [MemberData(nameof(WarningJobs))]
    public async Task ByteFailurePreservesAcceptedWarningHistoryAndTargets(string route, string operation, bool compressed, bool noWarn)
    {
        const string flags = "verbose progress report-memory-usage";
        object baselineHost = await Prepare(operation, compressed, true);
        QpdfExecutionResult baseline = await Run(baselineHost, route, operation, flags + (noWarn ? " no-warn" : ""), QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Completed, baseline.Status);
        QpdfDiagnostic[] warnings = baseline.Diagnostics.Where(item => item.Code == QpdfDiagnosticCode.DuplicateKey).ToArray();
        if (!noWarn) { Assert.NotEmpty(warnings); }
        long ceiling = baseline.Diagnostics.Sum(item => (long)Encoding.UTF8.GetByteCount(item.Message)) - 1L;
        object host = await Prepare(operation, compressed, true);
        string[] targets = ["output.pdf", "output-1.pdf", "output-2.pdf", "side-4"];
        byte[][] aliases = targets.Select(target =>
        { Assert.True(Inner(host).TryGetOutput(QpdfPath.From(target), out byte[] old)); return old; }).ToArray();
        QpdfExecutionResult failed = await Run(host, route, operation, flags + (noWarn ? " no-warn" : ""), QpdfLimits.Default with { MaxDiagnosticBytes = ceiling });
        Assert.Equal(QpdfExecutionStatus.Failed, failed.Status); Assert.Equal(QpdfExitCode.Error, failed.ExitCode); Assert.Equal(0, Commits(host));
        Assert.InRange(failed.Counters.DiagnosticCount, warnings.Length, baseline.Counters.DiagnosticCount);
        Assert.Equal(noWarn ? [] : warnings.Select(item => item.Message).ToArray(), failed.Diagnostics.Where(item => item.Code == QpdfDiagnosticCode.DuplicateKey).Select(item => item.Message).ToArray());
        Assert.Contains(failed.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.DoesNotContain(failed.Diagnostics, item => item.Code is QpdfDiagnosticCode.VerboseSummary or QpdfDiagnosticCode.ProgressReport or QpdfDiagnosticCode.MemoryUsage);
        Assert.Null(failed.OutputText); Assert.Null(failed.OutputBytes);
        foreach (string target in targets)
        { Assert.True(Inner(host).TryGetOutput(QpdfPath.From(target), out byte[] bytes)); Assert.Equal(new byte[] { 17, 23, 41 }, bytes); }
        Assert.All(aliases, alias => Assert.Equal(new byte[] { 17, 23, 41 }, alias));
        Assert.Equal(0L, failed.Counters.OpenHandles); Assert.Equal(0L, Inner(host).OpenLeaseCount);
        GC.KeepAlive(failed); GC.KeepAlive(aliases);
    }

    public static IEnumerable<object[]> WriterJobs()
    {
        foreach (string route in new[] { "argv", "job", "mixed", "response" })
        foreach (string operation in new[] { "split", "split-linearized", "split-qdf", "split-streamed", "split-pass1" })
        foreach (bool compressed in new[] { false, true }) { yield return [route, operation, compressed]; }
    }

    [Theory]
    [MemberData(nameof(WriterJobs))]
    public async Task PreparedSplitBytesMatchTheUnmodifiedWriterPath(string route, string operation, bool compressed)
    {
        object prior = await Prepare(operation, compressed, false), current = await Prepare(operation, compressed, false);
        QpdfExecutionResult old = await Run(prior, route, operation, "", QpdfLimits.Default);
        QpdfExecutionResult result = await Run(current, route, operation, "verbose progress report-memory-usage", QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Completed, old.Status); Assert.Equal(old.Status, result.Status); Assert.Equal(old.ExitCode, result.ExitCode);
        Assert.Equal(Commits(prior), Commits(current));
        foreach (string target in new[] { "output-1.pdf", "output-2.pdf", "pass1.pdf" })
        {
            Assert.True(Inner(prior).TryGetOutput(QpdfPath.From(target), out byte[] expected));
            Assert.True(Inner(current).TryGetOutput(QpdfPath.From(target), out byte[] actual)); Assert.Equal(expected, actual);
        }
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, Inner(current).OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    [InlineData("mixed")]
    [InlineData("response")]
    public async Task PreparedSplitPreservesBytesAcrossSegmentBoundaries(string route)
    {
        byte[] body = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("q Q\n", 17501)));
        byte[] pdf = CheckTests.Assemble(CheckTests.SinglePage("<< /Length " + body.Length + " >>", body), 1);
        object prior = await Prepare("split-qdf", false, false), current = await Prepare("split-qdf", false, false);
        Inner(prior).AddInput(QpdfPath.From("input.pdf"), pdf, QpdfInputOwnership.Copy);
        Inner(current).AddInput(QpdfPath.From("input.pdf"), pdf, QpdfInputOwnership.Copy);
        QpdfExecutionResult old = await Run(prior, route, "split-qdf", "", QpdfLimits.Default);
        QpdfExecutionResult result = await Run(current, route, "split-qdf", "verbose progress report-memory-usage", QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Completed, old.Status); Assert.Equal(old.Status, result.Status);
        foreach (string target in new[] { "output-1.pdf", "output-2.pdf" })
        {
            Assert.True(Inner(prior).TryGetOutput(QpdfPath.From(target), out byte[] expected));
            Assert.True(Inner(current).TryGetOutput(QpdfPath.From(target), out byte[] actual));
            Assert.True(actual.Length > 65536); Assert.Equal(expected, actual);
        }
        Assert.Equal(2, Commits(current)); Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, Inner(current).OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    [InlineData("mixed")]
    [InlineData("response")]
    public async Task PublicMemoryFailuresAfterStagingPreserveEverySplitTarget(string route)
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
            List<string> argv = ["input.pdf", "--verbose", "--progress", "--report-memory-usage", "--linearize", "--linearize-pass1=pass1.pdf", "--split-pages=1", "--pages"];
            for (int index = 0; index < 8; index++) { argv.AddRange([".", "1"]); }
            argv.AddRange(["--", "output.pdf"]);
            QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxMemoryBytes = ceiling });
            QpdfExecutionResult result;
            if (route == "job")
            {
                result = await Qpdf.ExecuteJobAsync((IQpdfHost)host, JsonSerializer.Serialize(new
                { inputFile = "input.pdf", outputFile = "output.pdf", verbose = "", progress = "", reportMemoryUsage = "", linearize = "", linearizePass1 = "pass1.pdf", splitPages = "1", pages = Enumerable.Range(0, 8).Select(_ => new { file = ".", range = "1" }).ToArray() }), options, CancellationToken.None);
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

    [Fact]
    public async Task PreparedOutputMemoryFailureRetiresTheWholeUnpublishedAttempt()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 50000L }, CancellationToken.None);
        using (PreparedOutputSink output = PreparedOutputSink.Create(QpdfLimits.Default, governor, CancellationToken.None))
        {
            await output.AppendAsync(new byte[32768], CancellationToken.None);
            Assert.Equal(32768L, output.Length);
            QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(async () => await output.AppendAsync(new byte[32768], CancellationToken.None));
            Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.Equal(32768L, output.Length);
            Assert.InRange(governor.Snapshot().PeakMemoryBytes, 32768L, 50000L);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("governor")]
    [InlineData("construction")]
    [InlineData("append")]
    public async Task PreparedOutputHonorsIndependentTokensAfterObservedWork(string canceled)
    {
        using CancellationTokenSource governorToken = new(), constructionToken = new(), appendToken = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken.Token);
        using (PreparedOutputSink output = PreparedOutputSink.Create(QpdfLimits.Default, governor, constructionToken.Token))
        {
            await output.AppendAsync(new byte[32768], appendToken.Token);
            (canceled == "governor" ? governorToken : canceled == "construction" ? constructionToken : appendToken).Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await output.AppendAsync(new byte[1], appendToken.Token));
            Assert.Equal(32768L, output.Length);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private static void Collect()
    { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    }
