using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit.Abstractions;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class ExecutionResultOwnershipTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly record struct CounterState(long Live, long Peak, long Decoded,
        long Output, long HostCalls, long Handles, long Diagnostics);

    public static IEnumerable<object[]> FileJsonAliases()
    {
        foreach (string kind in new[] { "result", "counters" })
        foreach (string route in new[] { "argv", "job", "mixed", "response" })
        foreach (bool compressed in new[] { false, true }) { yield return [kind, route, compressed]; }
    }

    public static IEnumerable<object[]> CommandJobs()
    {
        foreach (object[] job in ExecutionCounterTests.Jobs())
        {
            if (((string)job[0]).StartsWith("typed-", StringComparison.Ordinal)) { continue; }
            foreach (string kind in new[] { "result", "counters" })
            { yield return [kind, .. job]; }
        }
    }

    public static IEnumerable<object[]> BudgetAliases()
    {
        foreach (string kind in new[] { "result", "counters" })
        {
            foreach (long memory in new[] { 2048L, 4096L, 8192L }) { yield return [kind, memory]; }
        }
    }

    private sealed class Alias(ResourceGovernor governor, object value, WeakReference host,
        WeakReference result, WeakReference counters, CounterState snapshot)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal object? Value = value;
        internal readonly WeakReference Host = host;
        internal readonly WeakReference Result = result;
        internal readonly WeakReference Counters = counters;
        internal readonly CounterState Snapshot = snapshot;
    }

    private static object Read(object target, string name)
        => target.GetType().GetProperty(name, Members)?.GetValue(target)
            ?? throw new InvalidOperationException("Missing existing fixture property: " + name);

    private static void Resume(object host)
        => host.GetType().GetMethod("Resume", Members)?.Invoke(host, []);

    private static async Task<object> Prepare(string route, bool compressed, string boundary, string outcome)
    {
        Task preparation = (Task)(typeof(ExecutionCounterTests).GetMethod("Prepare", Members)?.Invoke(null,
            [route, compressed, boundary, outcome]) ?? throw new InvalidOperationException("Missing complete-job fixture."));
        await preparation;
        return Read(preparation, "Result");
    }

    private static ResourceGovernor FindGovernor(Task running)
        => (ResourceGovernor)(typeof(ExecutionCounterTests).GetMethod("FindGovernor", Members)?.Invoke(null,
            [running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)])
            ?? throw new InvalidOperationException("Missing actual public governor."));

    private static CounterState Values(QpdfExecutionCounters counters)
        => new(counters.LiveMemoryBytes, counters.PeakMemoryBytes, counters.TotalDecodedBytes,
            counters.TotalOutputBytes, counters.HostOperations, counters.OpenHandles, counters.DiagnosticCount);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias Capture(ResourceGovernor governor, object host, QpdfExecutionResult result, string kind)
    {
        CounterState final = Values(governor.Snapshot());
        Assert.Equal(final with { Live = result.Counters.LiveMemoryBytes }, Values(result.Counters));
        return new(governor, kind == "result" ? result : result.Counters, new(host), new(result), new(result.Counters), Values(result.Counters));
    }

    private static async Task<Alias> CompleteFileJson(string kind, string route, bool compressed, QpdfLimits limits, bool failure)
    {
        object host = await Prepare("argv-json", compressed, "input-close", "success");
        host.GetType().GetField("PausePath", Members)?.SetValue(host, QpdfPath.From("input.pdf"));
        InMemoryQpdfHost inner = (InMemoryQpdfHost)Read(host, "Inner");
        await using (IQpdfOutputTransaction seed = await inner.CreateOutputAsync(QpdfPath.From("output.json"), CancellationToken.None))
        {
            await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }
        Assert.True(inner.TryGetOutput(QpdfPath.From("output.json"), out byte[] prior));
        List<string> argv = ["input.pdf", "--json=2", "--json-stream-data=inline", "--", "output.json"];
        Task<QpdfExecutionResult> running;
        if (route == "job")
        {
            running = Qpdf.ExecuteJobAsync((IQpdfHost)host,
                "{\"inputFile\":\"input.pdf\",\"outputFile\":\"output.json\",\"json\":\"2\",\"jsonStreamData\":\"inline\"}",
                new QpdfExecutionOptions(limits), CancellationToken.None).AsTask();
        }
        else
        {
            if (route == "mixed") { argv.Insert(1, "--job-json-file=settings.json"); }
            if (route == "response")
            {
                inner.AddInput(QpdfPath.From("args.txt"), Encoding.UTF8.GetBytes(string.Join('\n', argv) + "\n"), QpdfInputOwnership.Copy);
                argv = ["@args.txt"];
            }
            running = Qpdf.ExecuteAsync((IQpdfHost)host, new QpdfCommandInvocation(argv),
                new QpdfExecutionOptions(limits), CancellationToken.None).AsTask();
        }
        try
        {
            await ((Task)Read(host, "Entered")).WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = FindGovernor(running);
            Resume(host);
            QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(failure ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
            Assert.Null(result.OutputText);
            Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, inner.OpenLeaseCount);
            Assert.True(result.Counters.PeakMemoryBytes <= limits.MaxMemoryBytes);
            Assert.True((long)(host.GetType().GetField("ReadBytes", Members)?.GetValue(host) ?? 0L) > 0L);
            Assert.True(inner.TryGetOutput(QpdfPath.From("output.json"), out byte[] bytes));
            Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            if (failure)
            {
                Assert.Equal(prior, bytes);
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
                Assert.Null(result.OutputBytes);
            }
            else
            {
                Assert.Empty(result.Diagnostics);
                Assert.Equal(1378, bytes.Length);
                using JsonDocument parsed = JsonDocument.Parse(bytes);
                Assert.Equal(2, parsed.RootElement.GetProperty("version").GetInt32());
            }
            return Capture(governor, host, result, kind);
        }
        finally
        {
            Resume(host); await running;
        }
    }

    private static async Task<Alias> CompleteJob(string kind, string route, bool compressed, string boundary, string outcome)
    {
        using CancellationTokenSource cancellation = new();
        object host = await Prepare(route, compressed, boundary, outcome);
        InMemoryQpdfHost inner = (InMemoryQpdfHost)Read(host, "Inner");
        Assert.True(inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] prior));
        Task<object> running = (Task<object>)(typeof(ExecutionCounterTests).GetMethod("Start", Members)?.Invoke(null,
            [host, route, QpdfLimits.Default, cancellation.Token]) ?? throw new InvalidOperationException("Missing public job driver."));
        try
        {
            await ((Task)Read(host, "Entered")).WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = FindGovernor(running);
            Assert.True((long)(host.GetType().GetField("ReadBytes", Members)?.GetValue(host) ?? 0L) > 0L);
            if (outcome == "cancel") { cancellation.Cancel(); } else { Resume(host); }
            QpdfExecutionResult result = Assert.IsType<QpdfExecutionResult>(await running.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(outcome == "success" ? QpdfExecutionStatus.Completed : outcome == "cancel"
                ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, inner.OpenLeaseCount);
            Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            if ((int)(host.GetType().GetField("Commits", Members)?.GetValue(host) ?? 0) == 0)
            { Assert.True(inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] kept)); Assert.Equal(prior, kept); }
            if (outcome != "success") { Assert.Null(result.OutputText); Assert.Null(result.OutputBytes); }
            return Capture(governor, host, result, kind);
        }
        finally
        {
            Resume(host); await running;
        }
    }

    private static void VerifyAlias(Alias alias, string kind)
    {
        object value = alias.Value ?? throw new InvalidOperationException("Missing public alias.");
        QpdfExecutionCounters counters = value is QpdfExecutionResult result ? result.Counters : Assert.IsType<QpdfExecutionCounters>(value);
        Assert.Equal(alias.Snapshot, Values(counters));
        GC.KeepAlive(value);
    }

    [Theory]
    [MemberData(nameof(FileJsonAliases))]
    [Trait("Layer", "Contract")]
    public async Task FileJsonResultsPreserveImmutableCounters(string kind, string route, bool compressed)
    {
        Alias alias = await CompleteFileJson(kind, route, compressed, QpdfLimits.Default, false);
        VerifyAlias(alias, kind);
    }

    [Theory]
    [MemberData(nameof(BudgetAliases))]
    [Trait("Layer", "Contract")]
    public async Task AfterReadBudgetFailuresPreserveTargetsAndCounters(string kind, long ceiling)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = ceiling };
        Alias alias = await CompleteFileJson(kind, "argv", false, limits, true);
        VerifyAlias(alias, kind);
    }

    [Theory]
    [MemberData(nameof(CommandJobs))]
    [Trait("Layer", "Contract")]
    public async Task CommandOutcomesPreserveImmutableCounters(string kind, string route, bool compressed, string boundary, string outcome)
    {
        Alias alias = await CompleteJob(kind, route, compressed, boundary, outcome);
        VerifyAlias(alias, kind);
    }

}
