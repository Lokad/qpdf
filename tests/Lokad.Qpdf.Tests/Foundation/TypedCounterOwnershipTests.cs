namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class TypedCounterOwnershipTests
{
    private static readonly QpdfPath Output = QpdfPath.From("output.pdf");

    public static IEnumerable<object[]> PublicJobs()
    {
        foreach (object[] row in TypedResultOwnershipTests.SuccessAliases())
        { yield return [row[0], row[1], "read", "success"]; }
        foreach ((string route, string boundary, string outcome) in new[] {
            ("typed-check", "input-close", "fault"), ("typed-check", "input-close", "cancel"),
            ("typed-json", "write", "fault"), ("typed-json", "write", "cancel"),
            ("typed-import", "commit", "fault"), ("typed-update", "input-close", "cancel"),
            ("typed-extract", "output-close", "fault"), ("typed-add", "write", "fault"),
            ("typed-remove", "commit", "fault"), ("typed-copy", "read", "cancel") })
        { yield return [route, false, boundary, outcome]; }
    }

    public static IEnumerable<object[]> MetadataJobs()
    { foreach (string route in ExecutionJobFixtures.TypedRoutes) { yield return [route]; } }

    [Theory]
    [MemberData(nameof(PublicJobs))]
    [Trait("Layer", "Contract")]
    public async Task PublicJobsExposeFinalCountersAndPreserveTargets(string route, bool compressed, string boundary, string outcome)
    {
        using RecordingJobHost host = await ExecutionJobFixtures.PrepareAsync(route, compressed, boundary,
            outcome == "fault" ? "fault" : "success");
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior));
        using CancellationTokenSource cancellation = new();
        Task<object> running = ExecutionJobFixtures.RunAsync(host, route, QpdfLimits.Default, cancellation.Token);
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L); Assert.False(running.IsCompleted);
            if (outcome == "cancel") { cancellation.Cancel(); } else { host.Resume(); }
            object? result = null;
            if (outcome == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
            else if (outcome == "fault") { await Assert.ThrowsAnyAsync<QpdfException>(() => running); }
            else { result = await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            Assert.True(host.ReadBytes > 0L); Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            Assert.True(host.Inner.TryGetOutput(Output, out byte[] current));
            if (host.Committed.Count == 0) { Assert.Equal(prior, current); }
            QpdfExecutionCounters? counters = result is null ? null : ExecutionJobFixtures.Counters(result);
            QpdfExecutionCounters? snapshot = counters is null ? null : counters with { };
            if (counters is not null)
            {
                Assert.Equal(host.HostCalls, counters.HostOperations); Assert.Equal(0L, counters.OpenHandles);
                Assert.InRange(counters.PeakMemoryBytes, 1L, QpdfLimits.Default.MaxMemoryBytes);
            }
            host.Dispose();
            Assert.Equal(0L, host.Budget.ReservedBytes); Assert.Equal(0, host.Budget.LiveReservations);
            Assert.Equal(snapshot, counters);
        }
        finally { host.Resume(); try { await running; } catch (QpdfException) { } catch (OperationCanceledException) { } }
    }

    [Theory]
    [MemberData(nameof(MetadataJobs))]
    [Trait("Layer", "Contract")]
    public async Task PrecanceledTypedJobsRejectBeforeHostAccess(string route)
    {
        using RecordingJobHost host = await ExecutionJobFixtures.PrepareAsync(route, false, "none", "success");
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecutionJobFixtures.RunAsync(host, route, QpdfLimits.Default, cancellation.Token));
        Assert.Equal(0L, host.HostCalls); Assert.Equal(0L, host.Budget.ReservedBytes);
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal(new byte[] { 17, 23, 41 }, prior);
    }

    [Theory]
    [MemberData(nameof(MetadataJobs))]
    [Trait("Layer", "Contract")]
    public async Task RealPublicBudgetFailuresPreservePriorOutputs(string route)
    {
        using RecordingJobHost host = await ExecutionJobFixtures.PrepareAsync(route, true, "none", "success");
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 2048L };
        await Assert.ThrowsAsync<QpdfLimitException>(() => ExecutionJobFixtures.RunAsync(host, route, limits, CancellationToken.None));
        Assert.Equal(0L, host.Inner.OpenLeaseCount); Assert.Empty(host.Committed);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal(new byte[] { 17, 23, 41 }, prior);
        host.Dispose();
        Assert.Equal(0L, host.Budget.ReservedBytes); Assert.Equal(0, host.Budget.LiveReservations);
    }
}
