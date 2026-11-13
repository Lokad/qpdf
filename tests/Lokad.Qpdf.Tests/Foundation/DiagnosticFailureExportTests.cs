using System.Runtime.CompilerServices;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class DiagnosticFailureExportTests
{
    private static async Task<RecordingJobHost> Prepare()
    {
        RecordingJobHost host = await ExecutionJobFixtures.PrepareAsync("argv-json", false, "none", "success");
        host.Inner.AddInput(QpdfPath.From("input.pdf"), DiagnosticTestJobs.WarningPdf(), QpdfInputOwnership.Copy);
        return host;
    }

    private static Task<QpdfExecutionResult> Run(RecordingJobHost host, string route, bool summaries, bool noWarn,
        QpdfLimits limits, CancellationToken token)
        => DiagnosticTestJobs.RunAsync(host, route, summaries, noWarn, "pages", limits, token);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IReadOnlyList<QpdfDiagnostic> Projection, WeakReference Hidden) ProjectWithoutWarnings()
    {
        using RecordingJobHost host = new(null, null, "success", true);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
        QpdfDiagnostic hidden = new(QpdfDiagnosticCode.PageCountMismatch, QpdfDiagnosticSeverity.Warning, new string('W', 4096));
        QpdfDiagnostic visible = new(QpdfDiagnosticCode.JsonSchemaCheck, QpdfDiagnosticSeverity.Info, "schema");
        using DiagnosticProjection projection = DiagnosticProjection.Create(governor, [hidden, visible],
            true, false, false, false, 0L, 0L, 0L);
        Assert.True(projection.HasWarning);
        IReadOnlyList<QpdfDiagnostic> result = projection.Transfer();
        Assert.Same(visible, Assert.Single(result));
        governor.CompletePrivateStorage();
        return (result, new(hidden));
    }

    [Fact]
    [Trait("Layer", "Ownership")]
    public void FilteredProjectionDoesNotRetainHiddenWarningRecords()
    {
        (IReadOnlyList<QpdfDiagnostic> projection, WeakReference hidden) = ProjectWithoutWarnings();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(hidden.IsAlive);
        Assert.Equal("schema", Assert.Single(projection).Message);
        GC.KeepAlive(projection);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    [InlineData("mixed")]
    [InlineData("response")]
    [Trait("Layer", "Ownership")]
    public async Task NoWarnFailurePreservesVisibleDiagnosticsAndTargets(string route)
    {
        using RecordingJobHost host = await Prepare();
        Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] alias));
        QpdfExecutionResult result = await Run(host, route, true, true,
            QpdfLimits.Default with { MaxDiagnostics = 3L }, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Equal(3L, result.Counters.DiagnosticCount); Assert.Equal(2, result.Diagnostics.Count);
        Assert.Equal(QpdfDiagnosticCode.JsonSchemaCheck, result.Diagnostics[0].Code);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, result.Diagnostics[1].Code);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
        Assert.Null(result.OutputText); Assert.Null(result.OutputBytes);
        Assert.Equal(new byte[] { 17, 23, 41 }, alias);
        Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] target)); Assert.Equal(alias, target);
        Assert.DoesNotContain(result.Diagnostics, item => item.Severity == QpdfDiagnosticSeverity.Warning);
        GC.KeepAlive(result); GC.KeepAlive(alias);
    }

    public static IEnumerable<object[]> InputCloseJobs()
    {
        foreach (bool summaries in new[] { false, true })
        foreach (bool noWarn in new[] { false, true })
        foreach (string outcome in new[] { "success", "fault", "cancel" })
        { yield return ["argv", summaries, noWarn, outcome]; }
        foreach (string route in new[] { "job", "mixed", "response" })
        { yield return [route, true, true, "success"]; }
    }

    [Theory]
    [MemberData(nameof(InputCloseJobs))]
    [Trait("Layer", "Contract")]
    public async Task InputCloseAfterPublicWarningAcceptancePreservesHistory(string route, bool summaries, bool noWarn, string outcome)
        => await ExerciseInputClose(route, summaries, noWarn, outcome, "pages", false);

    public static IEnumerable<object[]> OutputCloseJobs()
    {
        foreach (string mode in new[] { "json-file", "json-inline", "json-sidecar" })
        foreach (bool noWarn in new[] { false, true })
        foreach (string outcome in new[] { "success", "fault", "cancel" })
        { yield return ["argv", mode, mode == "json-sidecar", true, noWarn, outcome]; }
        foreach (string route in new[] { "job", "mixed", "response" })
        { yield return [route, "json-sidecar", false, false, false, "success"]; }
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
        using RecordingJobHost host = await ExecutionJobFixtures.PrepareAsync("argv-json", false, "input-close",
            outcome == "fault" ? "fault" : "success");
        host.PausePath = QpdfPath.From("input.pdf");
        host.Inner.AddInput(QpdfPath.From("input.pdf"), mode == "pages" ? DiagnosticTestJobs.WarningPdf() : DiagnosticTestJobs.StreamWarningPdf(compressed), QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("settings.json"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(QpdfPath.From("side-53"), CancellationToken.None))
        { await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] alias));
        Assert.True(host.Inner.TryGetOutput(QpdfPath.From("side-53"), out byte[] sideAlias));
        using CancellationTokenSource cancellation = new();
        Task<QpdfExecutionResult> running = DiagnosticTestJobs.RunAsync(host, route, summaries, noWarn, mode,
            QpdfLimits.Default with { MaxOpenHandles = 1L }, cancellation.Token);
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L); Assert.False(running.IsCompleted);
            if (outcome == "cancel") { cancellation.Cancel(); }
            else { host.Resume(); }
            QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(outcome == "success" ? QpdfExecutionStatus.Completed : outcome == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(noWarn ? 0 : 50, result.Diagnostics.Count(item => item.Code == QpdfDiagnosticCode.DuplicateKey));
            Assert.Equal(new byte[] { 17, 23, 41 }, alias); Assert.Equal(alias, sideAlias);
            Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
            if (outcome != "success")
            {
                Assert.InRange(result.Counters.DiagnosticCount, noWarn ? 1L : 51L, (noWarn ? 1L : 51L) + (summaries ? 6L : 0L)); Assert.Null(result.OutputText); Assert.Null(result.OutputBytes);
                Assert.Equal(new byte[] { 17, 23, 41 }, alias);
                Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] target)); Assert.Equal(alias, target);
                Assert.True(host.Inner.TryGetOutput(QpdfPath.From("side-53"), out byte[] sideTarget)); Assert.Equal(sideAlias, sideTarget);
                Assert.DoesNotContain(result.Diagnostics, item => item.Code is QpdfDiagnosticCode.VerboseSummary or QpdfDiagnosticCode.ProgressReport or QpdfDiagnosticCode.MemoryUsage);
            }
            else
            {
                Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
                if (mode == "json-inline") { Assert.NotNull(result.OutputText); }
                if (mode == "json-sidecar")
                {
                    Assert.True(host.Inner.TryGetOutput(QpdfPath.From("side-53"), out byte[] stream));
                    Assert.Equal("q Q\n"u8.ToArray(), stream);
                }
            }
            Assert.Equal(host.HostCalls, result.Counters.HostOperations);
        }
        finally { host.Resume(); await running; }
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
        foreach (string mode in new[] { "json-file", "json-inline", "json-sidecar" })
        foreach (bool noWarn in new[] { false, true })
        { yield return ["argv", mode, mode == "json-inline", true, noWarn]; }
        foreach (string route in new[] { "job", "mixed", "response" })
        { yield return [route, "json-sidecar", false, false, false]; }
    }

    [Theory]
    [MemberData(nameof(OutputMemoryJobs))]
    [Trait("Layer", "Contract")]
    public async Task OutputMemoryFailuresKeepTheAcceptedPrefixAndSurvivingTargets(string route, string mode,
        bool compressed, bool summaries, bool noWarn)
        => await ExerciseMemory(route, summaries, noWarn, mode, compressed);

    private static async Task ExerciseMemory(string route, bool summaries, bool noWarn, string mode, bool compressed)
    {
        using RecordingJobHost baselineHost = await Prepare();
        if (mode != "pages") { baselineHost.Inner.AddInput(QpdfPath.From("input.pdf"), DiagnosticTestJobs.StreamWarningPdf(compressed), QpdfInputOwnership.Copy); }
        QpdfExecutionResult baseline = await DiagnosticTestJobs.RunAsync(baselineHost, route, false, false, mode,
            QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, baseline.Status);
        string[] accepted = baseline.Diagnostics.Where(item => item.Code == QpdfDiagnosticCode.DuplicateKey)
            .Select(item => item.Message).ToArray();
        Assert.Equal(50, accepted.Length);
        int witnessed = 0;
        for (long ceiling = 60000L; ceiling <= 150000L; ceiling += 10000L)
        {
            using RecordingJobHost host = await Prepare();
            if (mode != "pages") { host.Inner.AddInput(QpdfPath.From("input.pdf"), DiagnosticTestJobs.StreamWarningPdf(compressed), QpdfInputOwnership.Copy); }
            await using (IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(QpdfPath.From("side-53"), CancellationToken.None))
            { await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] alias));
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From("side-53"), out byte[] sideAlias));
            QpdfExecutionResult result = await DiagnosticTestJobs.RunAsync(host, route, summaries, noWarn, mode,
                QpdfLimits.Default with { MaxMemoryBytes = ceiling, MaxOpenHandles = 1L }, CancellationToken.None);
            Assert.InRange(result.Counters.PeakMemoryBytes, 0L, ceiling);
            Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
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
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] target)); Assert.Equal(alias, target);
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From("side-53"), out byte[] sideTarget)); Assert.Equal(sideAlias, sideTarget);
            GC.KeepAlive(result); GC.KeepAlive(alias);
        }
        Assert.InRange(witnessed, 1, 10);
    }

    }
