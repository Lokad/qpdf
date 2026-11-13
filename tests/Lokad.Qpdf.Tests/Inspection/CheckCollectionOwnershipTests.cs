using System.Collections;
using System.Security.Cryptography;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Inspection;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class CheckCollectionOwnershipTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf");

    private static byte[] Fixture(bool compressed, bool failed)
    {
        byte[] bytes = ExecutionJobFixtures.DocumentBytes(compressed, "none");
        Assert.Equal(compressed ? "188B9906AC757763AD2F807238C8CC94216FD7E019F438F4DEE552113E6B4C0F"
            : "4A615306E948AA4AB466A6B75CDBA752468741AA6703DDAEC2712FB72D356849",
            Convert.ToHexString(SHA256.HashData(bytes)));
        if (failed)
        {
            int kids = bytes.AsSpan().IndexOf("[3 0 R]"u8), count = bytes.AsSpan().IndexOf("/Count 1"u8);
            Assert.True(kids >= 0 && count >= 0);
            "[     ]"u8.CopyTo(bytes.AsSpan(kids, 7)); bytes[count + 7] = (byte)'0';
        }
        return bytes;
    }

    private sealed class GateHost(string boundary, string outcome, CancellationTokenSource cancellation) : IQpdfHost, IQpdfMemoryHost, IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _paused;
        internal InMemoryQpdfHost Inner { get; } = new();
        internal RecordingMemoryBudget Budget { get; } = new(64L << 20);
        public IMemoryBudget MemoryBudget => Budget;
        internal long HostCalls;
        public void Dispose() { Budget.Dispose(); Inner.Dispose(); }
        internal Task Entered => _entered.Task;
        internal long ReadBytes;
        internal int Closes, Creates;
        internal void Resume() => _resume.TrySetResult();
        private async ValueTask Pause(string phase, CancellationToken token)
        {
            if (_paused || boundary != phase) { return; }
            _paused = true; _entered.TrySetResult();
            await _resume.Task.WaitAsync(token);
            if (outcome == "fault") { throw new QpdfHostException(Input, QpdfHostFailureReason.ReadFailed); }
            if (outcome == "cancel-return") { cancellation.Cancel(); }
        }
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        { HostCalls++; return new InputLease(this, await Inner.OpenInputAsync(path, token), token); }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { HostCalls++; Creates++; return Inner.CreateOutputAsync(path, token); }
        private sealed class InputLease(GateHost host, IQpdfInput inner, CancellationToken openingToken) : IQpdfInput
        {
            private bool _closed;
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            {
                host.HostCalls++;
                int data = await inner.ReadAsync(offset, destination, token);
                host.ReadBytes += data; await host.Pause("read", token); return data;
            }
            public async ValueTask DisposeAsync()
            {
                if (_closed) { return; }
                _closed = true; await inner.DisposeAsync(); host.Closes++;
                await host.Pause("close", openingToken);
            }
        }
    }

    private static async Task<GateHost> Host(bool compressed, bool failed, string boundary, string outcome,
        CancellationTokenSource cancellation)
    {
        GateHost host = new(boundary, outcome, cancellation);
        host.Inner.AddInput(Input, Fixture(compressed, failed), QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(Output, CancellationToken.None);
        await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
        return host;
    }

    private static void Closed(GateHost host, object? result)
    {
        Assert.True(host.ReadBytes > 0L); Assert.Equal(0, host.Creates); Assert.Equal(0L, host.Inner.OpenLeaseCount);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal(new byte[] { 17, 23, 41 }, prior);
        QpdfExecutionCounters? counters = result is null ? null : ExecutionJobFixtures.Counters(result);
        QpdfExecutionCounters? snapshot = counters is null ? null : counters with { };
        if (counters is not null)
        { Assert.Equal(host.HostCalls, counters.HostOperations); Assert.Equal(0L, counters.OpenHandles); }
        host.Dispose();
        Assert.Equal(0L, host.Budget.ReservedBytes); Assert.Equal(0, host.Budget.LiveReservations);
        Assert.Equal(snapshot, counters);
    }

    public static IEnumerable<object[]> FixtureCases()
    {
        foreach (bool compressed in new[] { false, true })
        foreach (bool failed in new[] { false, true }) { yield return [compressed, failed]; }
    }

    [Theory]
    [MemberData(nameof(FixtureCases))]
    public async Task PublicCheckViewsElementsAndCopiesSurviveHostCleanup(bool compressed, bool failed)
    {
        using CancellationTokenSource cancellation = new();
        using GateHost host = await Host(compressed, failed, "read", "success", cancellation);
        Task<QpdfCheckResult> running = Qpdf.CheckAsync(host, Input, cancellation.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L); host.Resume();
            QpdfCheckResult result = await running;
            Assert.Equal(!failed, result.Passed); Assert.Equal(failed ? 0 : 1, result.PageCount);
            Assert.Equal(9, result.Checks.Count); Assert.Equal(failed ? 1 : 0, result.Diagnostics.Count);
            IReadOnlyList<QpdfCheckOutcome> checks = result.Checks;
            IReadOnlyList<QpdfDiagnostic> diagnostics = result.Diagnostics;
            QpdfCheckOutcome[] checkCopy = checks.ToArray();
            QpdfDiagnostic[] diagnosticCopy = diagnostics.ToArray();
            QpdfCheckOutcome selected = checks.Single(x => x.Name == "trailer"), recordCopy = selected with { };
            Assert.Equal("header", checks[0].Name); Assert.Equal("linearization", checks[^1].Name);
            if (checks is IList mutableChecks)
            { Assert.True(mutableChecks.IsReadOnly); Assert.Throws<NotSupportedException>(() => mutableChecks.Clear()); }
            if (diagnostics.Count != 0 && diagnostics is IList mutableDiagnostics)
            { Assert.True(mutableDiagnostics.IsReadOnly); Assert.Throws<NotSupportedException>(() => mutableDiagnostics.Clear()); }
            Closed(host, result);
            Assert.Equal(checkCopy, checks); Assert.Equal(diagnosticCopy, diagnostics);
            Assert.Equal(selected, recordCopy); Assert.Equal(QpdfCheckStatus.Passed, selected.Status);
            for (int i = 0; i < checks.Count; i++) { Assert.Same(checkCopy[i], checks[i]); }
            if (failed)
            {
                QpdfDiagnostic element = Assert.Single(diagnostics), copy = element with { };
                Assert.Equal(QpdfDiagnosticCode.CheckFailed, element.Code); Assert.Equal(element, copy);
                Assert.Equal(9, checks.Count);
            }
        }
        finally { host.Resume(); await running; }
    }

    public static IEnumerable<object[]> Boundaries()
    {
        foreach (object[] fixture in FixtureCases())
        foreach (string outcome in new[] { "fault", "cancel-return" }) { yield return [.. fixture, outcome]; }
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public async Task ClosedInputFaultOrCancellationRejectsBeforePublicSnapshots(bool compressed, bool failed, string outcome)
    {
        using CancellationTokenSource cancellation = new();
        using GateHost host = await Host(compressed, failed, "close", outcome, cancellation);
        Task<QpdfCheckResult> running = Qpdf.CheckAsync(host, Input, cancellation.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            // This host has already closed the physical input. Returning from
            // close triggers cancellation without throwing at a suspended read.
            Assert.Equal(0L, host.Inner.OpenLeaseCount); Assert.True(host.Budget.ReservedBytes > 0L); host.Resume();
            if (outcome == "fault") { await Assert.ThrowsAnyAsync<QpdfException>(() => running); }
            else { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
            Assert.Equal(1, host.Closes); Closed(host, null);
        }
        finally { host.Resume(); try { await running; } catch (QpdfException) { } catch (OperationCanceledException) { } }
    }

    public static IEnumerable<object[]> Budgets()
    { foreach (object[] fixture in FixtureCases()) foreach (bool shortBudget in new[] { false, true }) { yield return [.. fixture, shortBudget]; } }

    [Theory]
    [MemberData(nameof(Budgets))]
    public async Task FixedPublicBudgetsPreserveMeaningfulLimitsAndCleanup(bool compressed, bool failed, bool shortBudget)
    {
        using CancellationTokenSource cancellation = new();
        using GateHost host = await Host(compressed, failed, "read", "success", cancellation);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = compressed ? shortBudget ? 131072L : 262144L : shortBudget ? 16384L : 32768L };
        Task<QpdfCheckResult> running = Qpdf.CheckAsync(host, Input, new QpdfCheckOptions(), limits, cancellation.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10)); host.Resume();
            QpdfCheckResult? result = null;
            if (compressed && shortBudget)
            {
                try
                {
                    result = await running; Assert.False(result.Passed);
                    Assert.Contains(result.Diagnostics, x => x.Code == QpdfDiagnosticCode.CheckFailed && x.Message.Contains(nameof(QpdfLimits.MaxMemoryBytes), StringComparison.Ordinal));
                }
                catch (QpdfLimitException failure) { Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); }
            }
            else
            {
                result = await running; Assert.Equal(!failed, result.Passed); Assert.Equal(9, result.Checks.Count);
                if (failed) { Assert.Contains(result.Diagnostics, x => x.Code == QpdfDiagnosticCode.CheckFailed); } else { Assert.Empty(result.Diagnostics); }
            }
            if (result is not null) { Assert.True(result.Counters.PeakMemoryBytes <= limits.MaxMemoryBytes); }
            Closed(host, result);
        }
        finally { host.Resume(); try { await running; } catch (QpdfException) { } }
    }

    public static IEnumerable<object[]> SharedRoutes()
    {
        foreach (string route in new[] { "argv", "job", "mixed" })
        foreach (bool failed in new[] { false, true }) { yield return [failed, failed, route]; }
    }

    [Theory]
    [MemberData(nameof(SharedRoutes))]
    public async Task CommandRoutesRetireSharedCheckerScratch(bool compressed, bool failed, string route)
    {
        using CancellationTokenSource cancellation = new();
        using GateHost host = await Host(compressed, failed, "read", "success", cancellation);
        host.Inner.AddInput(QpdfPath.From("settings.json"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
        Task<QpdfExecutionResult> running = route == "job"
            ? Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input.pdf\",\"check\":\"\"}", cancellation.Token).AsTask()
            : Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(route == "mixed" ? ["input.pdf", "--job-json-file=settings.json", "--check"] : ["input.pdf", "--check"]), cancellation.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10)); host.Resume();
            QpdfExecutionResult result = await running;
            Assert.Equal(failed ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
            if (failed) { Assert.Contains(result.Diagnostics, x => x.Code == QpdfDiagnosticCode.CheckFailed); }
            Assert.Equal(route == "mixed" ? 2 : 1, host.Closes); Closed(host, result);
        }
        finally { host.Resume(); await running; }
    }
}
