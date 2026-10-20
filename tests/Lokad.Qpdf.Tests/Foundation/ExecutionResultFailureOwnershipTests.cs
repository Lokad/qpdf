using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class ExecutionResultFailureOwnershipTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] Routes = ["argv", "job", "mixed", "response", "qdf", "zlib-compress", "zlib-uncompress"];
    private static readonly QpdfPath Input = QpdfPath.From("input.bin");
    private static readonly QpdfPath Output = QpdfPath.From("output.bin");

    private readonly record struct Values(long Live, long Peak, long Decoded, long OutputBytes,
        long HostCalls, long Handles, long Diagnostics);

    private static Values Snapshot(QpdfExecutionCounters c) => new(c.LiveMemoryBytes, c.PeakMemoryBytes,         c.TotalDecodedBytes, c.TotalOutputBytes, c.HostOperations, c.OpenHandles, c.DiagnosticCount);

    public static IEnumerable<object[]> BudgetAliases()
    {
        foreach (string kind in new[] { "result", "counters" })
        foreach (bool compressed in new[] { false, true })
        {
            foreach (long memory in new[] { 2048L, 4096L, 8192L }) { yield return [kind, compressed, memory]; }
        }
    }

    public static IEnumerable<object[]> CopyCases()
    {
        foreach (string copy in new[] { "result", "counter-clone", "counter-constructor" })
        foreach (bool compressed in new[] { false, true })
        { yield return [copy, compressed]; }
    }

    public static IEnumerable<object[]> TinyJobs()
    {
        foreach (string route in Routes)
        foreach (bool canceled in new[] { false, true })
        {
            foreach (long memory in new[] { 1L, 80L, 152L, ExecutionResultAdmission.MemoryBytes - 1L }) { yield return [route, memory, canceled]; }
        }
    }

    public static IEnumerable<object[]> AdmissionEdges()
    {
        foreach (string route in Routes)
        { yield return [route]; }
    }

    public static IEnumerable<object[]> HealthyJobs()
    {
        foreach (string route in Routes) { yield return [route]; }
    }

    public static IEnumerable<object[]> CompanionValidation()
    {
        foreach (string invalid in new[] { "mode", "level-low", "level-high", "uncompress-level" })
        foreach (string budget in new[] { "memory", "default", "edge-memory" })
        foreach (bool canceled in new[] { false, true }) { yield return [invalid, budget, canceled]; }
    }

    private static object Field(object target, string name) => target.GetType().GetField(name, Members)?.GetValue(target)
        ?? throw new InvalidOperationException("Missing canonical fixture field: " + name);

    private static void SetValue(object alias, object? value)
        => (alias.GetType().GetField("Value", Members)
            ?? throw new InvalidOperationException("Missing canonical alias field.")).SetValue(alias, value);

    private static async Task<object> Complete(string kind, bool compressed, long ceiling)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = ceiling };
        MethodInfo method = typeof(ExecutionResultOwnershipTests).GetMethod("CompleteFileJson", Members)
            ?? throw new InvalidOperationException("Missing unchanged complete file-JSON fixture.");
        Task running = (Task)(method.Invoke(null, [kind, "argv", compressed, limits, true])
            ?? throw new InvalidOperationException("Missing public fixture task."));
        await running;
        return running.GetType().GetProperty("Result", Members)?.GetValue(running)
            ?? throw new InvalidOperationException("Missing canonical public alias.");
    }

    [Theory]
    [MemberData(nameof(BudgetAliases))]
    [Trait("Layer", "Contract")]
    public async Task OriginalBudgetFailuresFundIndependentResultAndCounterAliases(string kind, bool compressed, long ceiling)
    {
        object alias = await Complete(kind, compressed, ceiling);
        await Task.Yield();
        ResourceGovernor governor = Assert.IsType<ResourceGovernor>(Field(alias, "Governor"));
        Assert.True(governor.Snapshot().LiveMemoryBytes >= (kind == "result" ? 168L : 88L));
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 0L, ceiling);
        SetValue(alias, null);
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(alias);
    }

    private sealed record Detached(object Value, WeakReference[] Metadata, WeakReference Host);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Detached> Detach(string kind, bool compressed, long ceiling)
    {
        object alias = await Complete(kind, compressed, ceiling);
        ResourceGovernor governor = Assert.IsType<ResourceGovernor>(Field(alias, "Governor"));
        List<WeakReference> metadata = [new(governor)];
        object? owners = typeof(ResourceGovernor).GetField("_valueOwners", Members)?.GetValue(governor);
        if (owners is not null)
        {
            metadata.Add(new(owners));
        }
        return new(Field(alias, "Value"), metadata.ToArray(), Assert.IsType<WeakReference>(Field(alias, "Host")));
    }

    [Theory]
    [MemberData(nameof(BudgetAliases))]
    [Trait("Layer", "Contract")]
    public async Task FailureAliasesDoNotRootCompletedJobMetadata(string kind, bool compressed, long ceiling)
    {
        Detached alias = await Detach(kind, compressed, ceiling);
        await Task.Yield();
        Collect();
        Assert.False(alias.Host.IsAlive);
        Assert.All(alias.Metadata, reference => Assert.False(reference.IsAlive));
        QpdfExecutionCounters counters = alias.Value is QpdfExecutionResult result ? result.Counters : Assert.IsType<QpdfExecutionCounters>(alias.Value);
        Assert.True(counters.LiveMemoryBytes >= 168L);
        GC.KeepAlive(alias);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<object> Copy(string kind, bool compressed)
    {
        object alias = await Complete("result", compressed, 4096L);
        QpdfExecutionResult original = Assert.IsType<QpdfExecutionResult>(Field(alias, "Value"));
        object copy;
        if (kind == "result")
        {
            QpdfExecutionResult clone = original with { };
            Assert.Equal(original, clone); Assert.Equal(original.GetHashCode(), clone.GetHashCode());
            Assert.Same(original.Diagnostics, clone.Diagnostics); Assert.Same(original.Counters, clone.Counters);
            copy = clone;
        }
        else
        {
            QpdfExecutionCounters c = original.Counters;
            QpdfExecutionCounters clone = kind == "counter-clone" ? c with { }
                : new(c.LiveMemoryBytes, c.PeakMemoryBytes, c.TotalDecodedBytes, c.TotalOutputBytes,
                    c.HostOperations, c.OpenHandles, c.DiagnosticCount);
            Assert.Equal(c, clone); Assert.Equal(c.GetHashCode(), clone.GetHashCode());
            Assert.True(c == clone); Assert.False(c.Equals(null));
            copy = clone;
        }
        SetValue(alias, copy);
        return alias;
    }

    [Theory]
    [MemberData(nameof(CopyCases))]
    [Trait("Layer", "Contract")]
    public async Task CallerRecordCopiesPreserveValuesUnderTheCallerOwner(string kind, bool compressed)
    {
        object alias = await Copy(kind, compressed);
        await Task.Yield();
        ResourceGovernor governor = Assert.IsType<ResourceGovernor>(Field(alias, "Governor"));
        if (kind == "result") { Assert.True(governor.Snapshot().LiveMemoryBytes >= 88L); }
        else { Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes); }
        SetValue(alias, null);
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(alias);
    }

    public static IEnumerable<object[]> PreparedWarningJobs()
    {
        foreach (string route in new[] { "argv", "job", "mixed", "response" })
        foreach (string kind in new[] { "result", "counters" })
        foreach (bool compressed in new[] { false, true })
        foreach (bool noWarn in new[] { false, true }) { yield return [route, kind, compressed, noWarn]; }
    }

    private sealed class WarningAlias(ResourceGovernor governor, object value)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal object? Value = value;
    }

    private static object Property(object target, string name) => target.GetType().GetProperty(name, Members)?.GetValue(target)
        ?? throw new InvalidOperationException("Missing canonical fixture property: " + name);

    private static Task<QpdfExecutionResult> RunWarnings(object host, string route, bool noWarn, QpdfLimits limits)
        => (Task<QpdfExecutionResult>)(typeof(DiagnosticFailureExportTests).GetMethod("RunMode", Members)?.Invoke(null,
            [host, route, false, noWarn, "json-file", limits, CancellationToken.None])
            ?? throw new InvalidOperationException("Missing canonical warning driver."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WarningAlias CaptureWarning(ResourceGovernor governor, QpdfExecutionResult result, string kind)
    {
        Assert.Equal(governor.Snapshot(), result.Counters);
        return new(governor, kind == "result" ? result : result.Counters);
    }

    private static async Task<WarningAlias> CompleteWarnings(string route, string kind, bool compressed, bool noWarn)
    {
        byte[] input = (byte[])(typeof(DiagnosticFailureExportTests).GetMethod("StreamWarningPdf", Members)?.Invoke(null, [compressed])
            ?? throw new InvalidOperationException("Missing unchanged fifty-warning fixture."));
        Task baselinePreparation = (Task)(typeof(DiagnosticFailureExportTests).GetMethod("Prepare", Members)?.Invoke(null, [])
            ?? throw new InvalidOperationException("Missing canonical warning host."));
        await baselinePreparation;
        object baselineHost = Property(baselinePreparation, "Result");
        Assert.IsType<InMemoryQpdfHost>(Property(baselineHost, "Inner")).AddInput(QpdfPath.From("input.pdf"), input, QpdfInputOwnership.Copy);
        QpdfExecutionResult baseline = await RunWarnings(baselineHost, route, false, QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Completed, baseline.Status);
        string[] accepted = baseline.Diagnostics.Where(d => d.Code == QpdfDiagnosticCode.DuplicateKey).Select(d => d.Message).ToArray();
        Assert.Equal(50, accepted.Length);
        Task preparation = (Task)(typeof(ExecutionCounterTests).GetMethod("Prepare", Members)?.Invoke(null,
            ["argv-json", compressed, "input-close", "success"]) ?? throw new InvalidOperationException("Missing canonical paused host."));
        await preparation;
        object host = Property(preparation, "Result");
        (host.GetType().GetField("PausePath", Members)
            ?? throw new InvalidOperationException("Missing canonical input pause selector.")).SetValue(host, QpdfPath.From("input.pdf"));
        InMemoryQpdfHost inner = Assert.IsType<InMemoryQpdfHost>(Property(host, "Inner"));
        inner.AddInput(QpdfPath.From("input.pdf"), input, QpdfInputOwnership.Copy);
        Assert.True(inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] prior));
        Task<QpdfExecutionResult> running = RunWarnings(host, route, noWarn, QpdfLimits.Default);
        ResourceGovernor? pressuredGovernor = null;
        long pressure = 0L;
        try
        {
            await Assert.IsAssignableFrom<Task>(Property(host, "Entered")).WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = Assert.IsType<ResourceGovernor>(typeof(ExecutionCounterTests).GetMethod("FindGovernor", Members)?.Invoke(null,
                [running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)]));
            Assert.True(Assert.IsType<long>(Field(host, "ReadBytes")) > 0L);
            long acceptedCount = governor.Snapshot().DiagnosticCount;
            Assert.Equal(noWarn ? 1L : 51L, acceptedCount);
            long warnings = noWarn ? 0L : 50L;
            pressure = QpdfLimits.Default.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes;
            governor.ReserveMemory(pressure); pressuredGovernor = governor;
            host.GetType().GetMethod("Resume", Members)?.Invoke(host, []);
            QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
            Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
            Assert.Equal(accepted.Take(noWarn ? 0 : (int)warnings), result.Diagnostics
                .Where(d => d.Code == QpdfDiagnosticCode.DuplicateKey).Select(d => d.Message));
            Assert.Equal(acceptedCount, result.Counters.DiagnosticCount);             Assert.Null(result.OutputText); Assert.Null(result.OutputBytes);
            Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, inner.OpenLeaseCount);
            Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            Assert.True(inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] kept)); Assert.NotEqual(prior, kept);
            return CaptureWarning(governor, result, kind);
        }
        finally
        {
            host.GetType().GetMethod("Resume", Members)?.Invoke(host, []); await running;
            pressuredGovernor?.ReleaseMemory(pressure);
        }
    }

    [Theory]
    [MemberData(nameof(PreparedWarningJobs))]
    [Trait("Layer", "Contract")]
    public async Task PreparedResultsKeepWarningsAndPublishUnderLaterMemoryPressure(string route, string kind, bool compressed, bool noWarn)
    {
        WarningAlias alias = await CompleteWarnings(route, kind, compressed, noWarn);
        await Task.Yield();
        Assert.True(alias.Governor.Snapshot().LiveMemoryBytes >= (kind == "result" ? 168L : 88L));
        Assert.InRange(alias.Governor.Snapshot().PeakMemoryBytes, 0L, QpdfLimits.Default.MaxMemoryBytes);
        alias.Value = null;
        Assert.Equal(alias.Governor.CallerMetadataBytes + alias.Governor.CallerStorageBytes, alias.Governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(alias);
    }

    private sealed class CountingHost : IQpdfHost
    {
        internal readonly InMemoryQpdfHost Inner = new();
        internal int Calls;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        { Calls++; return Inner.OpenInputAsync(path, token); }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Calls++; return Inner.CreateOutputAsync(path, token); }
    }

    private static async Task<CountingHost> Host(string route)
    {
        CountingHost host = new();
        byte[] input = "q Q\n"u8.ToArray();
        if (route == "qdf")
        {
            InMemoryQpdfHost setup = new();
            QpdfExecutionResult created = await Qpdf.ExecuteAsync(setup,
                new QpdfCommandInvocation(["--empty", "--qdf", "--object-streams=disable", "--", "source.pdf"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, created.Status);
            Assert.True(setup.TryGetOutput(QpdfPath.From("source.pdf"), out input));
        }
        if (route == "zlib-uncompress")
        {
            using MemoryStream bytes = new();
            using (ZLibStream encoder = new(bytes, CompressionLevel.Optimal, true)) { encoder.Write(input); }
            input = bytes.ToArray();
        }
        host.Inner.AddInput(Input, input, QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("settings.json"), "{\"showNpages\":\"\"}"u8.ToArray(), QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("args.txt"), "--empty\n--show-npages\n"u8.ToArray(), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await host.Inner.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        return host;
    }

    private static Func<ValueTask<QpdfExecutionResult>> Request(CountingHost host, string route, QpdfLimits limits, CancellationToken token)
    {
        QpdfExecutionOptions options = new(limits);
        if (route == "job") { return () => Qpdf.ExecuteJobAsync(host, "{\"empty\":\"\",\"showNpages\":\"\"}", options, token); }
        if (route == "qdf") { return () => Qpdf.FixQdfAsync(host, Input, Output, limits, token); }
        if (route is "zlib-compress" or "zlib-uncompress")
        {
            QpdfZlibFlateOptions codec = new(route == "zlib-compress" ? QpdfZlibFlateMode.Compress : QpdfZlibFlateMode.Uncompress, null);
            return () => Qpdf.RunZlibFlateAsync(host, Input, Output, codec, limits, token);
        }
        QpdfCommandInvocation argv = new(route == "mixed" ? ["--empty", "--job-json-file=settings.json"]
            : route == "response" ? ["@args.txt"] : ["--empty", "--show-npages"]);
        return () => Qpdf.ExecuteAsync(host, argv, options, token);
    }

    private static void Prior(CountingHost host)
    {
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior));
        Assert.Equal(new byte[] { 17, 23, 41 }, prior);
    }

    [Theory]
    [MemberData(nameof(HealthyJobs))]
    [Trait("Layer", "Contract")]
    public async Task AdmissionFixturesCompleteUnderDefaultLimits(string route)
    {
        CountingHost host = await Host(route);
        QpdfExecutionResult result = await Request(host, route, QpdfLimits.Default, CancellationToken.None)();
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.Inner.OpenLeaseCount);
        if (route is "argv" or "job" or "mixed" or "response")
        {
            QpdfDiagnostic count = Assert.Single(result.Diagnostics);
            Assert.Equal(QpdfDiagnosticCode.PageCount, count.Code); Assert.Equal(QpdfDiagnosticSeverity.Info, count.Severity);
            Assert.Contains(": 0", count.Message, StringComparison.Ordinal); Assert.Null(result.OutputText);
            Prior(host);
        }
        else
        { Assert.Empty(result.Diagnostics); Assert.True(host.Calls > 0); Assert.True(host.Inner.TryGetOutput(Output, out byte[] bytes)); Assert.NotEqual(new byte[] { 17, 23, 41 }, bytes); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (bool Same, long Allocated) MeasureSynchronousRejection(Func<ValueTask<QpdfExecutionResult>> invoke, QpdfExecutionResult first)
    {
        for (int warm = 0; warm < 8; warm++) { Completed(invoke()); }
        bool same = true;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int repeat = 0; repeat < 32; repeat++) { same &= ReferenceEquals(first, Completed(invoke())); }
        return (same, GC.GetAllocatedBytesForCurrentThread() - before);

        static QpdfExecutionResult Completed(ValueTask<QpdfExecutionResult> pending)
        {
            if (!pending.IsCompletedSuccessfully) { throw new InvalidOperationException("Pre-admission rejection must complete synchronously."); }
            return pending.Result;
        }
    }

    [Theory]
    [MemberData(nameof(TinyJobs))]
    [Trait("Layer", "Contract")]
    public async Task UnadmittedJobsBorrowImmutableOutcomesWithoutHostCallsOrWarmAllocation(string route, long ceiling, bool canceled)
    {
        CountingHost host = await Host(route);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = ceiling };
        using CancellationTokenSource cancellation = new();
        if (canceled) { cancellation.Cancel(); }
        Func<ValueTask<QpdfExecutionResult>> invoke = Request(host, route, limits, cancellation.Token);
        QpdfExecutionResult first = await invoke();
        (bool same, long allocated) = MeasureSynchronousRejection(invoke, first);
        Assert.True(same); Assert.Equal(0L, allocated); Assert.Equal(0, host.Calls);
        Assert.Equal(canceled ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, first.Status);
        Assert.Equal(QpdfExitCode.Error, first.ExitCode);
        Assert.Equal(default, Snapshot(first.Counters)); Assert.Null(first.OutputText); Assert.Null(first.OutputBytes);
        if (canceled) { Assert.Empty(first.Diagnostics); }
        else
        {
            QpdfDiagnostic error = Assert.Single(first.Diagnostics);
            Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, error.Code);
            Assert.Contains("MaxMemoryBytes", error.Message, StringComparison.Ordinal);
            IList<QpdfDiagnostic> diagnostics = Assert.IsAssignableFrom<IList<QpdfDiagnostic>>(first.Diagnostics);
            Assert.Throws<NotSupportedException>(() => diagnostics[0] = error);
        }
        Prior(host);
    }

    [Theory]
    [MemberData(nameof(AdmissionEdges))]
    [Trait("Layer", "Contract")]
    public async Task ExactlyAdmittedFailuresKeepTheFixedCarrierReserve(string route)
    {
        CountingHost host = await Host(route);
        QpdfLimits limits = QpdfLimits.Default with
        { MaxMemoryBytes = ExecutionResultAdmission.MemoryBytes};
        QpdfExecutionResult result = await Request(host, route, limits, CancellationToken.None)();
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(result.Counters.LiveMemoryBytes >= 168L);
        Assert.True(result.Counters.PeakMemoryBytes <= limits.MaxMemoryBytes);
Assert.Equal(0L, result.Counters.OpenHandles);
        Prior(host);
    }

    [Theory]
    [MemberData(nameof(CompanionValidation))]
    [Trait("Layer", "Contract")]
    public async Task InvalidCompanionOptionsKeepTheirExistingPrecedence(string invalid, string budget, bool canceled)
    {
        CountingHost host = await Host("zlib-compress");
        QpdfLimits limits = budget switch
        {
            "memory" => QpdfLimits.Default with { MaxMemoryBytes = 1L },
            "edge-memory" => QpdfLimits.Default with { MaxMemoryBytes = ExecutionResultAdmission.MemoryBytes },
            _ => QpdfLimits.Default,
        };
        QpdfZlibFlateOptions options = invalid switch
        {
            "mode" => new((QpdfZlibFlateMode)99, null),
            "level-low" => new(QpdfZlibFlateMode.Compress, 0),
            "level-high" => new(QpdfZlibFlateMode.Compress, 10),
            _ => new(QpdfZlibFlateMode.Uncompress, 3),
        };
        using CancellationTokenSource cancellation = new(); if (canceled) { cancellation.Cancel(); }
        QpdfExecutionResult result = await Qpdf.RunZlibFlateAsync(host, Input, Output, options, limits, cancellation.Token);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, error.Code);
        Assert.Equal(invalid == "mode" ? "The zlib-flate mode is not supported."
            : "The zlib-flate compression level must be 1..9 while compressing.", error.Message);
        Assert.Equal(0, host.Calls); Prior(host);
    }
    private static void Collect()
    { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
}
