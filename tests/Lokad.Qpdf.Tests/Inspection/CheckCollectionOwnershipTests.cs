using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Inspection;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class CheckCollectionOwnershipTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf");
    private static object Field(object value, string name) => value.GetType().GetField(name, Members)?.GetValue(value)
        ?? throw new InvalidOperationException(name);

    private static byte[] Fixture(bool compressed, bool failed)
    {
        byte[] bytes = (byte[])(typeof(PdfVersionOwnershipTests).GetMethod("DocumentBytes", Members)?.Invoke(null,
            [compressed, "none"]) ?? throw new InvalidOperationException("Missing preserved checker fixture."));
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

    private sealed class GateHost(string boundary, string outcome, CancellationTokenSource cancellation) : IQpdfHost
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _paused;
        internal InMemoryQpdfHost Inner { get; } = new();
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
            => new InputLease(this, await Inner.OpenInputAsync(path, token), token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Creates++; return Inner.CreateOutputAsync(path, token); }
        private sealed class InputLease(GateHost host, IQpdfInput inner, CancellationToken openingToken) : IQpdfInput
        {
            private bool _closed;
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token)
            {
                ReadOnlyMemory<byte> data = await inner.ReadAsync(offset, count, token);
                host.ReadBytes += data.Length; await host.Pause("read", token); return data;
            }
            public async ValueTask DisposeAsync()
            {
                if (_closed) { return; }
                _closed = true; await inner.DisposeAsync(); host.Closes++;
                await host.Pause("close", openingToken);
            }
        }
    }

    private static ResourceGovernor Governor(Task running) => (ResourceGovernor)(typeof(ExecutionCounterTests)
        .GetMethod("FindGovernor", Members)?.Invoke(null, [running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)])
        ?? throw new InvalidOperationException("Missing actual public checker governor."));

    private static async Task<GateHost> Host(bool compressed, bool failed, string boundary, string outcome,
        CancellationTokenSource cancellation)
    {
        GateHost host = new(boundary, outcome, cancellation);
        host.Inner.AddInput(Input, Fixture(compressed, failed), QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(Output, CancellationToken.None);
        await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
        return host;
    }

    private static void Closed(GateHost host, ResourceGovernor governor)
    {
        Assert.True(host.ReadBytes > 0L); Assert.Equal(1, host.Closes); Assert.Equal(0, host.Creates);
        Assert.Equal(0L, host.Inner.OpenLeaseCount); Assert.Equal(0L, governor.Snapshot().OpenHandles);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] output)); Assert.Equal(new byte[] { 17, 23, 41 }, output);
    }

    private static Array Backing(object value) => value as Array
        ?? value.GetType().GetFields(Members).Select(f => f.GetValue(value)).OfType<Array>().Single();

    private sealed class Aliases(ResourceGovernor governor, object value, object? sibling, bool admitted,
        QpdfCheckOutcome[]? originalElements)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal object? Value = value, Sibling = sibling;
        internal readonly bool Admitted = admitted;
        internal readonly QpdfCheckOutcome[]? OriginalElements = originalElements;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Aliases Keep(QpdfCheckResult result, ResourceGovernor governor, string kind)
    {
        Assert.Equal(9, result.Checks.Count);
        Array checks = Backing(result.Checks), diagnostics = Backing(result.Diagnostics);
        QpdfCheckOutcome selected = result.Checks.Single(x => x.Name == "trailer");
        bool admitted = governor.CallerStorageBytes + governor.PrivateRetainedBytes >= 56L;
        object value = kind switch
        {
            "original" or "checks-backing" => checks,
            "checks-element" => selected,
            "checks-copy" => result.Checks.ToArray(),
            "checks-record-copy" => selected with { },
            "diagnostics-backing" => diagnostics,
            "diagnostics-element" => Assert.Single(result.Diagnostics),
            "diagnostics-copy" => result.Diagnostics.ToArray(),
            "diagnostics-record-copy" => Assert.Single(result.Diagnostics) with { },
            "diagnostics-view" => result.Diagnostics,
            _ => result.Checks,
        };
        object? sibling = kind == "original" ? null : kind.StartsWith("diagnostics-", StringComparison.Ordinal)
            ? result.Checks : result.Diagnostics;
        return new(governor, value, sibling, admitted, kind == "original" ? result.Checks.ToArray() : null);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Aliases> Complete(bool compressed, bool failed, string kind)
    {
        using CancellationTokenSource cancellation = new();
        GateHost host = await Host(compressed, failed, "read", "success", cancellation);
        Task<QpdfCheckResult> running = Qpdf.CheckAsync(host, Input, new QpdfCheckOptions(), QpdfLimits.Default, cancellation.Token).AsTask();
        await host.Entered.WaitAsync(TimeSpan.FromSeconds(10)); ResourceGovernor governor = Governor(running); host.Resume();
        QpdfCheckResult result = await running.WaitAsync(TimeSpan.FromSeconds(10)); Closed(host, governor);
        Assert.Equal(!failed, result.Passed); Assert.Equal(failed ? 0 : 1, result.PageCount);
        Assert.Equal(failed ? 1 : 0, result.Diagnostics.Count);
        QpdfExecutionCounters final = governor.Snapshot();
        Assert.Equal(final.PeakMemoryBytes, result.Counters.PeakMemoryBytes);
        Assert.InRange(final.LiveMemoryBytes, 0L, result.Counters.LiveMemoryBytes);
        return Keep(result, governor, kind);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Validate(Aliases aliases, string kind)
    {
        object value = aliases.Value ?? throw new InvalidOperationException();
        if (value is QpdfCheckOutcome check)
        { Assert.Equal("trailer", check.Name); Assert.Equal(QpdfCheckStatus.Passed, check.Status); }
        else if (value is QpdfDiagnostic diagnostic) { Assert.Equal(QpdfDiagnosticCode.CheckFailed, diagnostic.Code); }
        else if (kind.StartsWith("diagnostics-", StringComparison.Ordinal))
        { Assert.Equal(QpdfDiagnosticCode.CheckFailed, Assert.Single((IReadOnlyList<QpdfDiagnostic>)value).Code); }
        else
        {
            IReadOnlyList<QpdfCheckOutcome> entries = (IReadOnlyList<QpdfCheckOutcome>)value;
            Assert.Equal(9, entries.Count); Assert.Equal("header", entries[0].Name); Assert.Equal("linearization", entries[^1].Name);
            if (aliases.OriginalElements is not null)
            { for (int i = 0; i < entries.Count; i++) { Assert.Same(aliases.OriginalElements[i], entries[i]); } }
        }
        if (kind.EndsWith("-view", StringComparison.Ordinal))
        { IList list = (IList)value; Assert.True(list.IsReadOnly); Assert.Throws<NotSupportedException>(() => list.Clear()); }
        if (kind != "original")
        {
            Assert.True(aliases.Admitted, "Public check view/backing lacks a durable coarse owner.");
            bool diagnostic = kind.StartsWith("diagnostics-", StringComparison.Ordinal);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ValidateSibling(Aliases aliases, string kind)
    {
        if (kind.StartsWith("diagnostics-", StringComparison.Ordinal))
        { Assert.Equal(9, ((IReadOnlyList<QpdfCheckOutcome>)(aliases.Sibling ?? throw new InvalidOperationException())).Count); }
        else
        {
            Assert.Equal(QpdfDiagnosticCode.CheckFailed, Assert.Single((IReadOnlyList<QpdfDiagnostic>)
            (aliases.Sibling ?? throw new InvalidOperationException())).Code);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Layer", "Ownership")]
    public async Task CheckElementsSurviveTheirOriginalView(bool compressed)
    {
        Aliases aliases = await Complete(compressed, false, "original"); await Task.Yield();
        QpdfExecutionCounters held = aliases.Governor.Snapshot(); Validate(aliases, "original"); aliases.Value = null;
        QpdfExecutionCounters dropped = aliases.Governor.Snapshot();
        Assert.Equal(held.LiveMemoryBytes, dropped.LiveMemoryBytes);
        Assert.Equal(9, aliases.OriginalElements?.Length); GC.KeepAlive(aliases.OriginalElements);
    }

    public static IEnumerable<object[]> PublicAliases()
    {
        foreach (bool compressed in new[] { false, true })
        foreach (string kind in new[] { "checks-view", "checks-backing", "checks-element", "checks-copy", "checks-record-copy",
            "diagnostics-view", "diagnostics-backing", "diagnostics-element", "diagnostics-copy", "diagnostics-record-copy" })
        { yield return [compressed, kind]; }
    }
    [Theory]
    [MemberData(nameof(PublicAliases))]
    [Trait("Layer", "Contract")]
    public async Task CompleteFailedChecksOwnIndependentPublicAliasesAndSiblings(bool compressed, string kind)
    {
        Aliases aliases = await Complete(compressed, true, kind); await Task.Yield();
        QpdfExecutionCounters held = aliases.Governor.Snapshot(); Validate(aliases, kind); aliases.Value = null;
        QpdfExecutionCounters dropped = aliases.Governor.Snapshot();
        Assert.Equal(held.LiveMemoryBytes, dropped.LiveMemoryBytes);
        ValidateSibling(aliases, kind); aliases.Sibling = null; Assert.Equal(aliases.Governor.CallerMetadataBytes + aliases.Governor.CallerStorageBytes, aliases.Governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, aliases.Governor.Snapshot().OpenHandles);
    }

    public static IEnumerable<object[]> Boundaries()
    {
        foreach (bool compressed in new[] { false, true })
        foreach (bool failed in new[] { false, true })
        foreach (string outcome in new[] { "fault", "cancel-return" }) { yield return [compressed, failed, outcome]; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ResourceGovernor> RejectBoundary(bool compressed, bool failed, string outcome)
    {
        using CancellationTokenSource cancellation = new();
        GateHost host = await Host(compressed, failed, "close", outcome, cancellation);
        Task<QpdfCheckResult> running = Qpdf.CheckAsync(host, Input, new QpdfCheckOptions(), QpdfLimits.Default, cancellation.Token).AsTask();
        await host.Entered.WaitAsync(TimeSpan.FromSeconds(10)); ResourceGovernor governor = Governor(running);
        Assert.True(host.ReadBytes > 0L); Assert.Equal(0L, host.Inner.OpenLeaseCount); host.Resume();
        if (outcome == "fault") { await Assert.ThrowsAnyAsync<QpdfException>(() => running); }
        else { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
        Closed(host, governor); return governor;
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    [Trait("Layer", "Contract")]
    public async Task ClosedInputFaultOrCancellationRejectsBeforePublicSnapshots(bool compressed, bool failed, string outcome)
    {
        ResourceGovernor governor = await RejectBoundary(compressed, failed, outcome);
        await Task.Yield();
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
    }

    public static IEnumerable<object[]> Budgets()
    {
        foreach (bool compressed in new[] { false, true })
        foreach (bool failed in new[] { false, true })
        foreach (string budget in new[] { "memory-short", "memory-success"})
        { yield return [compressed, failed, budget]; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ResourceGovernor> CompleteBudget(bool compressed, bool failed, string budget)
    {
        using CancellationTokenSource cancellation = new();
        GateHost host = await Host(compressed, failed, "read", "success", cancellation);
        QpdfLimits limits = budget switch
        {
            "memory-short" => QpdfLimits.Default with { MaxMemoryBytes = compressed ? 131072L : 16384L },
            "memory-success" => QpdfLimits.Default with { MaxMemoryBytes = compressed ? 262144L : 32768L },
            _ => QpdfLimits.Default,
        };
        Task<QpdfCheckResult> running = Qpdf.CheckAsync(host, Input, new QpdfCheckOptions(), limits, cancellation.Token).AsTask();
        await host.Entered.WaitAsync(TimeSpan.FromSeconds(10)); ResourceGovernor governor = Governor(running); host.Resume();
        // The small plain fixture now fits these former accounting boundaries.
        // Compressed expansion must still reject at the unchanged lower ceilings.
        if (compressed && budget is "work-short" or "memory-short")
        {
            string limit = nameof(QpdfLimits.MaxMemoryBytes);
            try
            {
                QpdfCheckResult result = await running;
                Assert.False(result.Passed);
                Assert.Contains(result.Diagnostics, x => x.Code == QpdfDiagnosticCode.CheckFailed
                    && x.Message.Contains(limit, StringComparison.Ordinal));
            }
            catch (QpdfLimitException failure) { Assert.Equal(limit, failure.LimitName); }
        }
        else
        {
            QpdfCheckResult result = await running; Assert.Equal(!failed, result.Passed);
            Assert.Equal(9, result.Checks.Count);
            if (failed) { Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.CheckFailed); }
            else { Assert.Empty(result.Diagnostics); }
        }
        Closed(host, governor); Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
return governor;
    }
    [Theory]
    [MemberData(nameof(Budgets))]
    [Trait("Layer", "Contract")]
    public async Task FixedPublicBudgetsPreserveMeaningfulLimitsAndCleanup(bool compressed, bool failed, string budget)
    {
        ResourceGovernor governor;
        { governor = await CompleteBudget(compressed, failed, budget); }
        await Task.Yield();
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
    }
    public static IEnumerable<object[]> FixtureCases()
    {
        foreach (bool compressed in new[] { false, true })
        foreach (bool failed in new[] { false, true }) { yield return [compressed, failed]; }
    }

    private static async Task<object> Start(GateHost host, string route, CancellationToken token)
    {
        if (route == "typed") { return await Qpdf.CheckAsync(host, Input, token); }
        if (route == "job") { return await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input.pdf\",\"check\":\"\"}", token); }
        string[] arguments = route == "mixed" ? ["input.pdf", "--job-json-file=settings.json", "--check"] : ["input.pdf", "--check"];
        return await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(arguments), token);
    }
    public static IEnumerable<object[]> SharedRoutes()
    {
        foreach (object[] fixture in FixtureCases())
        foreach (string route in new[] { "typed", "argv", "job", "mixed" }) { yield return [.. fixture, route]; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ResourceGovernor> CompleteRoute(bool compressed, bool failed, string route)
    {
        using CancellationTokenSource cancellation = new();
        GateHost host = await Host(compressed, failed, "read", "success", cancellation);
        host.Inner.AddInput(QpdfPath.From("settings.json"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
        Task<object> running = Start(host, route, cancellation.Token);
        await host.Entered.WaitAsync(TimeSpan.FromSeconds(10)); ResourceGovernor governor = Governor(running); host.Resume();
        object result = await running;
        if (result is QpdfCheckResult typed) { Assert.Equal(!failed, typed.Passed); Assert.Equal(9, typed.Checks.Count); }
        else
        {
            QpdfExecutionResult command = Assert.IsType<QpdfExecutionResult>(result);
            Assert.Equal(failed ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, command.Status);
            if (failed) { Assert.Contains(command.Diagnostics, x => x.Code == QpdfDiagnosticCode.CheckFailed); }
        }
        Assert.True(host.ReadBytes > 0L); Assert.Equal(route == "mixed" ? 2 : 1, host.Closes); Assert.Equal(0, host.Creates);
        Assert.Equal(0L, host.Inner.OpenLeaseCount); Assert.Equal(0L, governor.Snapshot().OpenHandles);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] kept)); Assert.Equal(new byte[] { 17, 23, 41 }, kept);
        return governor;
    }
    [Theory]
    [MemberData(nameof(SharedRoutes))]
    [Trait("Layer", "Contract")]
    public async Task CompleteTypedArgvJobAndMixedChecksRetireSharedDecoderScratch(bool compressed, bool failed, string route)
    {
        ResourceGovernor governor = await CompleteRoute(compressed, failed, route); await Task.Yield();
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
    }

    internal static async Task<object> MeasurePublicAdmission(bool compressed, bool failed)
    {
        using CancellationTokenSource cancellation = new();
        GateHost host = await Host(compressed, failed, "close", "success", cancellation);
        Task<QpdfCheckResult> running = Qpdf.CheckAsync(host, Input, new QpdfCheckOptions(), QpdfLimits.Default, cancellation.Token).AsTask();
        await host.Entered.WaitAsync(TimeSpan.FromSeconds(10)); ResourceGovernor governor = Governor(running);
        QpdfExecutionCounters closed = governor.Snapshot(); host.Resume();
        QpdfCheckResult result = await running.WaitAsync(TimeSpan.FromSeconds(10)); Closed(host, governor);
        return new
        {
            compressed,
            failed,
            ClosedLive = closed.LiveMemoryBytes,
            FinalLive = result.Counters.LiveMemoryBytes,
            Peak = result.Counters.PeakMemoryBytes,
            Checks = result.Checks.Count,
            Diagnostics = result.Diagnostics.Count
        };
    }
}
