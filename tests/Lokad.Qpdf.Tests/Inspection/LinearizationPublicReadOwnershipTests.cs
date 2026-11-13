using Lokad.Qpdf.Tests.Foundation;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class LinearizationPublicReadOwnershipTests
{
    public static IEnumerable<object[]> Jobs()
    {
        foreach (string input in new[] { "classic-control", "part8-streamed-control", "overflow-36-valid", "overflow-61-valid", "compressed" })
        { yield return [input, "typed", "success"]; }
        foreach (string route in new[] { "argv-check", "job-check", "argv-show", "job-show" })
        { yield return ["classic-control", route, "success"]; }
        yield return ["compressed", "argv-show", "success"];
        foreach (string route in new[] { "typed", "job-show" })
        foreach (string outcome in new[] { "close-failure", "cancellation" })
        { yield return ["part8-streamed-control", route, outcome]; }
        yield return ["compressed", "argv-check", "close-failure"];
    }

    [Theory]
    [MemberData(nameof(Jobs))]
    public async Task CompleteReadJobsPreserveAliasesAndCloseOnEveryObservedOutcome(string input, string route, string outcome)
    {
        using CancellationTokenSource cancellation = new();
        using RecordingJobHost host = new("input-close", QpdfPath.From("input.pdf"),
            outcome == "close-failure" ? "fault" : "success", true);
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
            Assert.True(host.Budget.ReservedBytes > 0L);
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
            Assert.Empty(host.CreatedPaths);
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            if (keptResult is QpdfCheckResult typed)
            {
                Assert.Equal(0L, typed.Counters.OpenHandles);
                Assert.Equal(host.HostCalls, typed.Counters.HostOperations);
                if (input == "compressed") { Assert.True(typed.Counters.TotalDecodedBytes > 0L); }
            }
            else if (keptResult is QpdfExecutionResult command)
            {
                Assert.Equal(0L, command.Counters.OpenHandles);
                Assert.Equal(host.HostCalls, command.Counters.HostOperations);
                if (input == "compressed") { Assert.True(command.Counters.TotalDecodedBytes > 0L); }
            }
            Assert.True(host.Budget.ReservedBytes < host.Budget.PeakBytes);
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From("prior.pdf"), out byte[] prior));
            Assert.Equal(new byte[] { 17, 23, 41 }, priorAlias);
            Assert.Equal(priorAlias, prior);
            GC.KeepAlive(keptResult); GC.KeepAlive(executing); GC.KeepAlive(source); GC.KeepAlive(priorAlias);
        }
        finally
        {
            host.Resume();
            try { await executing; }
            catch (QpdfHostException) { }
            catch (OperationCanceledException) { }
        }
    }

}
