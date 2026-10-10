using System.IO.Compression;
using System.Reflection;
using Xunit.Abstractions;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class ExecutionResultFailureOwnershipTests
{
    private readonly ITestOutputHelper _output;
    private OwnershipBatch.Observation? _collection;

    public ExecutionResultFailureOwnershipTests(ITestOutputHelper output) => _output = output;

    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] Routes = ["argv", "job", "mixed", "response", "qdf", "zlib-compress", "zlib-uncompress"];
    private static readonly QpdfPath Input = QpdfPath.From("input.bin");
    private static readonly QpdfPath Output = QpdfPath.From("output.bin");

    private readonly record struct Values(long Live, long Peak, long Work, long Decoded, long OutputBytes,
        long HostCalls, long Handles, long Diagnostics);

    private static Values Snapshot(QpdfExecutionCounters c) => new(c.LiveMemoryBytes, c.PeakMemoryBytes, c.TotalWork,
        c.TotalDecodedBytes, c.TotalOutputBytes, c.HostOperations, c.OpenHandles, c.DiagnosticCount);

    public static IEnumerable<object[]> BudgetAliases()
    {
        foreach (string kind in new[] { "result", "counters" })
        foreach (bool compressed in new[] { false, true })
        {
            foreach (long work in new[] { 64L, 128L, 256L, 512L, 1024L, 2048L }) { yield return [kind, compressed, "work", work]; }
            foreach (long memory in new[] { 2048L, 4096L, 8192L }) { yield return [kind, compressed, "memory", memory]; }
        }
    }

    public static IEnumerable<object[]> CopyCases()
    {
        foreach (string copy in new[] { "result", "counter-clone", "counter-constructor" })
        foreach (bool compressed in new[] { false, true })
        foreach (long work in new[] { 64L, 128L, 256L, 512L }) { yield return [copy, compressed, work]; }
    }

    public static IEnumerable<object[]> TinyJobs()
    {
        foreach (string route in Routes)
        foreach (bool canceled in new[] { false, true })
        {
            foreach (long memory in new[] { 1L, 80L, 152L, 255L }) { yield return [route, "memory", memory, canceled]; }
            foreach (long work in new[] { 1L, 2L, 3L }) { yield return [route, "work", work, canceled]; }
        }
    }

    public static IEnumerable<object[]> AdmissionEdges()
    {
        foreach (string route in Routes)
        foreach (string bound in new[] { "memory", "work", "both" }) { yield return [route, bound]; }
    }

    public static IEnumerable<object[]> HealthyJobs()
    {
        foreach (string route in Routes) { yield return [route]; }
    }

    public static IEnumerable<object[]> CompanionValidation()
    {
        foreach (string invalid in new[] { "mode", "level-low", "level-high", "uncompress-level" })
        foreach (string budget in new[] { "memory", "work", "default", "edge-memory", "edge-work" })
        foreach (bool canceled in new[] { false, true }) { yield return [invalid, budget, canceled]; }
    }

    private static object Field(object target, string name) => target.GetType().GetField(name, Members)?.GetValue(target)
        ?? throw new InvalidOperationException("Missing canonical fixture field: " + name);

    private static void SetValue(object alias, object? value)
        => (alias.GetType().GetField("Value", Members)
            ?? throw new InvalidOperationException("Missing canonical alias field.")).SetValue(alias, value);

    private async Task CollectAsync(ResourceGovernor governor)
    {
        if (_collection is not null) { await _collection.CollectAsync(governor, 4); }
        else
        {
            for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            for (int boundary = 0; boundary < 4; boundary++) { governor.ReserveMemory(0L); }
        }
    }

    private static async Task<object> Complete(string kind, bool compressed, string budget, long ceiling)
    {
        QpdfLimits limits = budget == "work" ? QpdfLimits.Default with { MaxWork = ceiling }
            : QpdfLimits.Default with { MaxMemoryBytes = ceiling };
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
    public async Task OriginalBudgetFailuresFundIndependentResultAndCounterAliases(string kind, bool compressed, string budget, long ceiling)
    {
        object alias = await Complete(kind, compressed, budget, ceiling);
        await Task.Yield();
        ResourceGovernor governor = Assert.IsType<ResourceGovernor>(Field(alias, "Governor"));
        if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
        await CollectAsync(governor);
        Assert.False(Assert.IsType<WeakReference>(Field(alias, "Host")).IsAlive);
        Assert.Equal(kind == "result", Assert.IsType<WeakReference>(Field(alias, "Result")).IsAlive);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= (kind == "result" ? 168L : 88L));
        await VerifyCounterValuesAsync(alias, budget, ceiling);
        SetValue(alias, null);
        await CollectAsync(governor);
        Assert.False(Assert.IsType<WeakReference>(Field(alias, "Result")).IsAlive);
        Assert.False(Assert.IsType<WeakReference>(Field(alias, "Counters")).IsAlive);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(alias);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task VerifyCounterValuesAsync(object alias, string budget, long ceiling)
    {
        object value = Field(alias, "Value");
        QpdfExecutionCounters counters = value is QpdfExecutionResult result ? result.Counters : Assert.IsType<QpdfExecutionCounters>(value);
        Values before = Snapshot(counters);
        await CollectAsync(Assert.IsType<ResourceGovernor>(Field(alias, "Governor")));
        Assert.Equal(before, Snapshot(counters));
        Assert.True(before.Work <= (budget == "work" ? ceiling : QpdfLimits.Default.MaxWork));
        Assert.True(before.Peak <= (budget == "memory" ? ceiling : QpdfLimits.Default.MaxMemoryBytes));
    }

    private sealed record Detached(object Value, WeakReference[] Metadata, WeakReference Host);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Detached> Detach(string kind, bool compressed, string budget, long ceiling)
    {
        object alias = await Complete(kind, compressed, budget, ceiling);
        ResourceGovernor governor = Assert.IsType<ResourceGovernor>(Field(alias, "Governor"));
        List<WeakReference> metadata = [new(governor)];
        object? owners = typeof(ResourceGovernor).GetField("_valueOwners", Members)?.GetValue(governor);
        if (owners is not null)
        {
            metadata.Add(new(owners));
            foreach (string field in new[] { "_storage", "_heads", "_slots", "_segments" })
            {
                object? storage = owners.GetType().GetField(field, Members)?.GetValue(owners);
                if (storage is not null) { metadata.Add(new(storage)); }
            }
        }
        return new(Field(alias, "Value"), metadata.ToArray(), Assert.IsType<WeakReference>(Field(alias, "Host")));
    }

    [Theory]
    [MemberData(nameof(BudgetAliases))]
    [Trait("Layer", "Contract")]
    public async Task FailureAliasesDoNotRootCompletedJobMetadata(string kind, bool compressed, string budget, long ceiling)
    {
        Detached alias = await Detach(kind, compressed, budget, ceiling);
        await Task.Yield();
        if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
        await (_collection ?? throw new InvalidOperationException()).CollectAsync(null, 0);
        Assert.False(alias.Host.IsAlive);
        Assert.All(alias.Metadata, reference => Assert.False(reference.IsAlive));
        QpdfExecutionCounters counters = alias.Value is QpdfExecutionResult result ? result.Counters : Assert.IsType<QpdfExecutionCounters>(alias.Value);
        Assert.True(counters.LiveMemoryBytes >= 168L);
        GC.KeepAlive(alias);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<object> Copy(string kind, bool compressed, long work)
    {
        object alias = await Complete("result", compressed, "work", work);
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
                : new(c.LiveMemoryBytes, c.PeakMemoryBytes, c.TotalWork, c.TotalDecodedBytes, c.TotalOutputBytes,
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
    public async Task CallerRecordCopiesPreserveValuesWithoutOwningOriginalReceipts(string kind, bool compressed, long work)
    {
        object alias = await Copy(kind, compressed, work);
        await Task.Yield();
        ResourceGovernor governor = Assert.IsType<ResourceGovernor>(Field(alias, "Governor"));
        if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
        await CollectAsync(governor);
        Assert.False(Assert.IsType<WeakReference>(Field(alias, "Result")).IsAlive);
        Assert.Equal(kind == "result", Assert.IsType<WeakReference>(Field(alias, "Counters")).IsAlive);
        Assert.False(Assert.IsType<WeakReference>(Field(alias, "Host")).IsAlive);
        if (kind == "result") { Assert.True(governor.Snapshot().LiveMemoryBytes >= 88L); }
        else { Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); }
        SetValue(alias, null);
        await CollectAsync(governor);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(alias);
    }

    public static IEnumerable<object[]> WarningFailures()
    {
        foreach (string route in new[] { "argv", "job", "mixed", "response" })
        foreach (string kind in new[] { "result", "counters" })
        foreach (bool compressed in new[] { false, true })
        foreach (bool noWarn in new[] { false, true }) { yield return [route, kind, compressed, noWarn]; }
    }

    private sealed class WarningAlias(ResourceGovernor governor, object value, object host, QpdfExecutionResult result)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal object? Value = value;
        internal readonly WeakReference Host = new(host);
        internal readonly WeakReference Result = new(result);
        internal readonly WeakReference Counters = new(result.Counters);
    }

    private static object Property(object target, string name) => target.GetType().GetProperty(name, Members)?.GetValue(target)
        ?? throw new InvalidOperationException("Missing canonical fixture property: " + name);

    private static Task<QpdfExecutionResult> RunWarnings(object host, string route, bool noWarn, QpdfLimits limits)
        => (Task<QpdfExecutionResult>)(typeof(DiagnosticFailureExportTests).GetMethod("RunMode", Members)?.Invoke(null,
            [host, route, false, noWarn, "json-file", limits, CancellationToken.None])
            ?? throw new InvalidOperationException("Missing canonical warning driver."));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WarningAlias CaptureWarning(ResourceGovernor governor, object host, QpdfExecutionResult result, string kind)
    {
        Assert.Equal(governor.Snapshot(), result.Counters);
        return new(governor, kind == "result" ? result : result.Counters, host, result);
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
        Task<QpdfExecutionResult> running = RunWarnings(host, route, noWarn, QpdfLimits.Default with { MaxWork = 5000L });
        try
        {
            await Assert.IsAssignableFrom<Task>(Property(host, "Entered")).WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = Assert.IsType<ResourceGovernor>(typeof(ExecutionCounterTests).GetMethod("FindGovernor", Members)?.Invoke(null,
                [running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)]));
            Assert.True(Assert.IsType<long>(Field(host, "ReadBytes")) > 0L);
            long warnings = governor.Snapshot().DiagnosticCount;
            Assert.InRange(warnings, 1L, 50L);
            host.GetType().GetMethod("Resume", Members)?.Invoke(host, []);
            QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded
                && d.Message.Contains(nameof(QpdfLimits.MaxWork), StringComparison.Ordinal));
            Assert.Equal(accepted.Take(noWarn ? 0 : (int)warnings), result.Diagnostics
                .Where(d => d.Code == QpdfDiagnosticCode.DuplicateKey).Select(d => d.Message));
            Assert.Equal(warnings, result.Counters.DiagnosticCount); Assert.True(result.Counters.TotalWork <= 5000L);
            Assert.Null(result.OutputText); Assert.Null(result.OutputBytes);
            Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, inner.OpenLeaseCount);
            Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            Assert.True(inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] kept)); Assert.Equal(prior, kept);
            return CaptureWarning(governor, host, result, kind);
        }
        finally { host.GetType().GetMethod("Resume", Members)?.Invoke(host, []); await running; }
    }

    [Theory]
    [MemberData(nameof(WarningFailures))]
    [Trait("Layer", "Contract")]
    public async Task WorkFailuresKeepAcceptedWarningsAndIndependentOutcomeAliases(string route, string kind, bool compressed, bool noWarn)
    {
        WarningAlias alias = await CompleteWarnings(route, kind, compressed, noWarn);
        await Task.Yield();
        if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
        await CollectAsync(alias.Governor);
        Assert.False(alias.Host.IsAlive); Assert.Equal(kind == "result", alias.Result.IsAlive);
        Assert.True(alias.Counters.IsAlive);
        Assert.True(alias.Governor.Snapshot().LiveMemoryBytes >= (kind == "result" ? 168L : 88L));
        await VerifyCounterValuesAsync(alias, "work", 5000L);
        alias.Value = null;
        await CollectAsync(alias.Governor);
        Assert.False(alias.Result.IsAlive); Assert.False(alias.Counters.IsAlive);
        Assert.Equal(0L, alias.Governor.Snapshot().LiveMemoryBytes);
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
    public async Task UnadmittedJobsBorrowImmutableOutcomesWithoutHostCallsOrWarmAllocation(string route, string budget, long ceiling, bool canceled)
    {
        CountingHost host = await Host(route);
        QpdfLimits limits = budget == "memory" ? QpdfLimits.Default with { MaxMemoryBytes = ceiling }
            : QpdfLimits.Default with { MaxWork = ceiling };
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
            Assert.Contains(budget == "memory" ? "MaxMemoryBytes" : "MaxWork", error.Message, StringComparison.Ordinal);
            IList<QpdfDiagnostic> diagnostics = Assert.IsAssignableFrom<IList<QpdfDiagnostic>>(first.Diagnostics);
            Assert.Throws<NotSupportedException>(() => diagnostics[0] = error);
        }
        Prior(host);
    }

    [Theory]
    [MemberData(nameof(AdmissionEdges))]
    [Trait("Layer", "Contract")]
    public async Task ExactlyAdmittedFailuresKeepTheFixedCarrierReserve(string route, string bound)
    {
        CountingHost host = await Host(route);
        QpdfLimits limits = QpdfLimits.Default with
        { MaxMemoryBytes = bound is "memory" or "both" ? 256L : QpdfLimits.Default.MaxMemoryBytes,
            MaxWork = bound is "work" or "both" ? 4L : QpdfLimits.Default.MaxWork };
        QpdfExecutionResult result = await Request(host, route, limits, CancellationToken.None)();
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(result.Counters.LiveMemoryBytes >= 168L);
        Assert.True(result.Counters.PeakMemoryBytes <= limits.MaxMemoryBytes);
        Assert.True(result.Counters.TotalWork <= limits.MaxWork); Assert.Equal(0L, result.Counters.OpenHandles);
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
            "work" => QpdfLimits.Default with { MaxWork = 1L },
            "edge-memory" => QpdfLimits.Default with { MaxMemoryBytes = 256L },
            "edge-work" => QpdfLimits.Default with { MaxWork = 4L },
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

    private static IEnumerable<(string Method, IEnumerable<object[]> Rows)> OwnershipCases()
        => new (string, IEnumerable<object[]>)[]
        {
            (nameof(OriginalBudgetFailuresFundIndependentResultAndCounterAliases), BudgetAliases()),
            (nameof(FailureAliasesDoNotRootCompletedJobMetadata), BudgetAliases()),
            (nameof(CallerRecordCopiesPreserveValuesWithoutOwningOriginalReceipts), CopyCases()),
            (nameof(WorkFailuresKeepAcceptedWarningsAndIndependentOutcomeAliases), WarningFailures()),
        };

    public static IEnumerable<object[]> OwnershipBatches() => OwnershipMatrix.Batches(OwnershipCases());

    [Theory]
    [MemberData(nameof(OwnershipBatches))]
    [Trait("Layer", "Ownership")]
    public Task BatchedLifetimeObservationsPreserveEveryOriginalPhase(int batch)
        => OwnershipMatrix.RunAsync(_output, OwnershipCases(), batch,
            observation => new ExecutionResultFailureOwnershipTests(_output) { _collection = observation });
}
