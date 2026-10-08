using System.Reflection;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class LinearizationPublicReadOwnershipTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static IEnumerable<object[]> Jobs()
    {
        foreach (string input in new[] { "classic-control", "part8-streamed-control", "overflow-36-valid", "overflow-61-valid", "compressed" })
        foreach (string route in new[] { "typed", "argv-check", "job-check", "argv-show", "job-show" })
        foreach (string outcome in new[] { "success", "close-failure", "cancellation" })
        { yield return new object[] { input, route, outcome }; }
    }

    private static ResourceGovernor? FindGovernor(object value, int depth, HashSet<object> seen)
    {
        if (depth > 96 || !seen.Add(value)) { return null; }
        if (value is ResourceGovernor governor) { return governor; }
        if (value is Task task)
        {
            object? state = task.GetType().GetField("StateMachine", Fields)?.GetValue(task);
            return state is null ? null : FindGovernor(state, depth + 1, seen);
        }
        foreach (FieldInfo field in value.GetType().GetFields(Fields))
        { if (field.GetValue(value) is ResourceGovernor found) { return found; } }
        foreach (FieldInfo field in value.GetType().GetFields(Fields))
        {
            if (field.GetValue(value) is object nested &&
                (nested is Task || nested.GetType().IsValueType && !nested.GetType().IsPrimitive && !nested.GetType().IsEnum))
            {
                ResourceGovernor? found = FindGovernor(nested, depth + 1, seen);
                if (found is not null) { return found; }
            }
        }
        return null;
    }

    [Theory]
    [MemberData(nameof(Jobs))]
    public async Task CompleteReadJobsPreserveAliasesAndCloseOnEveryObservedOutcome(string input, string route, string outcome)
    {
        using CancellationTokenSource cancellation = new();
        CloseHost host = new(outcome);
        byte[] source = LinearizationDataOwnershipTests.Input(input);
        host.Inner.AddInput(QpdfPath.From("input.pdf"), source, QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(QpdfPath.From("prior.pdf"), CancellationToken.None))
        {
            await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }
        Assert.True(host.Inner.TryGetOutput(QpdfPath.From("prior.pdf"), out byte[] priorAlias));
        bool show = route.EndsWith("show", StringComparison.Ordinal);
        Task executing = route == "typed"
            ? Qpdf.CheckAsync(host, QpdfPath.From("input.pdf"), new QpdfCheckOptions(), QpdfLimits.Default, cancellation.Token).AsTask()
            : route.StartsWith("argv", StringComparison.Ordinal)
                ? Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(new[] { "input.pdf", show ? "--show-linearization" : "--check-linearization" }),
                    new QpdfExecutionOptions(QpdfLimits.Default), cancellation.Token).AsTask()
                : Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(show
                    ? (object)new { inputFile = "input.pdf", showLinearization = "" }
                    : new { inputFile = "input.pdf", checkLinearization = "" }), new QpdfExecutionOptions(QpdfLimits.Default), cancellation.Token).AsTask();
        await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            ResourceGovernor governor = FindGovernor(executing, 0, new HashSet<object>(ReferenceEqualityComparer.Instance))
                ?? throw new InvalidOperationException("Expected the complete public read-job governor.");
            Assert.True(governor.Snapshot().TotalWork > 0L);
            if (input == "compressed") { Assert.True(governor.Snapshot().TotalDecodedBytes > 0L); }
            Assert.False(executing.IsCompleted);
            if (outcome == "cancellation") { cancellation.Cancel(); }
            else { host.Resume(); }
            object? keptResult = null;
            if (route == "typed")
            {
                Task<QpdfCheckResult> checking = (Task<QpdfCheckResult>)executing;
                if (outcome == "close-failure")
                { await Assert.ThrowsAsync<QpdfHostException>(async () => await checking); }
                else if (outcome == "cancellation")
                { await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await checking); }
                else
                {
                    QpdfCheckResult result = await checking;
                    Assert.True(result.Passed);
                    Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(result.Checks, value => value.Name == "linearization").Status);
                    keptResult = result;
                }
            }
            else
            {
                QpdfExecutionResult result = await (Task<QpdfExecutionResult>)executing;
                Assert.Equal(outcome == "success" ? QpdfExecutionStatus.Completed :
                    outcome == "cancellation" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
                Assert.Equal(outcome == "success" ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
                if (show && outcome == "success")
                { Assert.Contains(result.Diagnostics, value => value.Message.Contains("Shared Objects Hint Table", StringComparison.Ordinal)); }
                keptResult = result;
            }
            Assert.Equal(0, host.OutputCreations);
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.Equal(0L, governor.Snapshot().OpenHandles);
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From("prior.pdf"), out byte[] prior));
            Assert.Equal(new byte[] { 17, 23, 41 }, priorAlias);
            Assert.Equal(priorAlias, prior);
            GC.KeepAlive(keptResult); GC.KeepAlive(executing); GC.KeepAlive(governor); GC.KeepAlive(source); GC.KeepAlive(priorAlias);
        }
        finally
        {
            host.Resume();
            try { await executing; }
            catch (QpdfHostException) { }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class CloseHost(string outcome) : IQpdfHost
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string Outcome { get; } = outcome;
        internal InMemoryQpdfHost Inner { get; } = new();
        internal Task Entered => _entered.Task;
        internal int OutputCreations { get; private set; }
        internal void Resume() => _resume.TrySetResult();
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => new Input(this, await Inner.OpenInputAsync(path, token), token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            OutputCreations++;
            return Inner.CreateOutputAsync(path, token);
        }
        private sealed class Input(CloseHost host, IQpdfInput inner, CancellationToken token) : IQpdfInput
        {
            private bool _closed;
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellationToken)
                => inner.ReadAsync(offset, count, cancellationToken);
            public async ValueTask DisposeAsync()
            {
                if (_closed) { return; }
                _closed = true;
                try
                {
                    host._entered.TrySetResult();
                    await host._resume.Task.WaitAsync(token);
                    if (host.Outcome == "close-failure") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); }
                }
                finally { await inner.DisposeAsync(); }
            }
        }
    }
}
