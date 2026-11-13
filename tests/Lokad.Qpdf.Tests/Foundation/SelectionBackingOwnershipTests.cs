using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class SelectionBackingOwnershipTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf");

    private static void ReadOnly<T>(IReadOnlyList<T> values)
    {
        if (values is IList<T> mutable)
        { Assert.True(mutable.IsReadOnly); Assert.Throws<NotSupportedException>(() => mutable.Clear()); }
    }

    [Theory]
    [InlineData("typed-page", false)]
    [InlineData("typed-pages", true)]
    [InlineData("typed-pages-rotation", false)]
    [InlineData("typed-pages-options", true)]
    [Trait("Layer", "Contract")]
    public async Task PublicMetadataAliasesRemainUsableAfterCleanup(string route, bool compressed)
    {
        using RecordingJobHost host = await ExecutionJobFixtures.PrepareAsync(route, compressed, "read", "success");
        Task<object> running = ExecutionJobFixtures.RunAsync(host, route, QpdfLimits.Default, CancellationToken.None);
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L); host.Resume();
            object result = await running;
            QpdfExecutionCounters counters = ExecutionJobFixtures.Counters(result);
            Assert.Equal(host.HostCalls, counters.HostOperations); Assert.Equal(0L, counters.OpenHandles);
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            QpdfExecutionCounters snapshot = counters with { };
            host.Dispose();
            Assert.Equal(0L, host.Budget.ReservedBytes); Assert.Equal(0, host.Budget.LiveReservations);
            Assert.Equal(snapshot, counters);
            if (result is PdfPageSelectionResult single)
            {
                IReadOnlyList<int> pages = single.SourcePages;
                Assert.Equal(new[] { 1 }, pages); ReadOnly(pages);
                return;
            }
            PdfMultiPageSelectionResult multiple = Assert.IsType<PdfMultiPageSelectionResult>(result);
            IReadOnlyList<PdfPageSelectionMapping> mapping = multiple.Mapping;
            IReadOnlyList<int> counts = multiple.SourcePageCounts;
            IReadOnlyList<QpdfPath> paths = multiple.OutputPaths;
            Assert.Equal(new[] { new PdfPageSelectionMapping(0, 1), new PdfPageSelectionMapping(1, 1) }, mapping);
            Assert.Equal(new[] { 1, 1 }, counts); Assert.Equal(new[] { Output }, paths);
            ReadOnly(mapping); ReadOnly(counts); ReadOnly(paths);
            PdfPageSelectionMapping first = mapping[0];
            PdfPageSelectionMapping[] copies = [first with { }, new(first.SourceIndex, first.SourcePage)];
            foreach (PdfPageSelectionMapping copy in copies)
            { Assert.Equal(first, copy); Assert.Equal(first.GetHashCode(), copy.GetHashCode()); Assert.NotSame(first, copy); }
            PdfMultiPageSelectionResult sibling = multiple with { };
            Assert.Equal(multiple, sibling); Assert.Same(mapping, sibling.Mapping);
            Assert.False(typeof(PdfPageSelectionMapping).GetProperty(nameof(PdfPageSelectionMapping.SourcePage))?.CanWrite);
        }
        finally { host.Resume(); await running; }
    }

    public static IEnumerable<object[]> SplitJobs()
    {
        yield return [false, "read", "success", false];
        yield return [true, "write", "success", true];
        foreach (string boundary in new[] { "read", "input-close" })
        foreach (string outcome in new[] { "fault", "cancel" })
        { yield return [false, boundary, outcome, false]; }
        foreach (string boundary in new[] { "write", "commit", "output-close" })
        foreach (string outcome in new[] { "fault", "cancel" })
        foreach (bool second in new[] { false, true })
        { yield return [true, boundary, outcome, second]; }
    }

    [Theory]
    [MemberData(nameof(SplitJobs))]
    [Trait("Layer", "Contract")]
    public async Task SplitJobsKeepPublishedAndPriorAliasesAcrossBothOutputs(bool compressed, string boundary, string outcome, bool second)
    {
        using RecordingJobHost host = await ExecutionJobFixtures.PrepareAsync("typed-pages", compressed, boundary,
            outcome == "fault" ? "fault" : "success");
        List<byte[]> prior = [];
        QpdfPath[] paths = [QpdfPath.From("output-1.pdf"), QpdfPath.From("output-2.pdf")];
        foreach (QpdfPath path in paths)
        {
            await using IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(path, CancellationToken.None);
            await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
            Assert.True(host.Inner.TryGetOutput(path, out byte[] bytes)); prior.Add(bytes);
        }
        if (second) { host.PausePath = paths[1]; }
        using CancellationTokenSource cancellation = new();
        Task<PdfMultiPageSelectionResult> running = Qpdf.SelectPagesAsync(host,
            [new PdfPageSource(Input, new PdfPageSelectionOptions(1)),
             new PdfPageSource(QpdfPath.From("second.pdf"), new PdfPageSelectionOptions(1))],
            Output, new PdfPageMergeOptions { SplitPages = 1 }, QpdfLimits.Default, cancellation.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L);
            if (outcome == "cancel") { cancellation.Cancel(); } else { host.Resume(); }
            PdfMultiPageSelectionResult? result = null;
            if (outcome == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
            else if (outcome == "fault") { await Assert.ThrowsAnyAsync<QpdfException>(() => running); }
            else { result = await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            int commits = outcome == "success" ? 2 : boundary == "output-close" ? second ? 2 : 1 : second ? 1 : 0;
            Assert.Equal(commits, host.Committed.Count);
            for (int index = 0; index < prior.Count; index++)
            {
                Assert.Equal(new byte[] { 17, 23, 41 }, prior[index]);
                Assert.True(host.Inner.TryGetOutput(paths[index], out byte[] bytes));
                if (index >= commits) { Assert.Equal(prior[index], bytes); continue; }
                Assert.NotEqual(prior[index], bytes);
                using InMemoryQpdfHost validation = new();
                validation.AddInput(Input, bytes, QpdfInputOwnership.Copy);
                QpdfCheckResult check = await Qpdf.CheckAsync(validation, Input, CancellationToken.None);
                Assert.True(check.Passed); Assert.Equal(1, check.PageCount); Assert.Equal(0L, validation.OpenLeaseCount);
            }
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            QpdfExecutionCounters? snapshot = result is null ? null : result.Counters with { };
            if (result is not null)
            {
                Assert.Equal(paths, result.OutputPaths); Assert.Equal(new[] { 1, 1 }, result.SourcePageCounts);
                Assert.Equal(host.HostCalls, result.Counters.HostOperations); Assert.Equal(0L, result.Counters.OpenHandles);
            }
            host.Dispose();
            Assert.Equal(0L, host.Budget.ReservedBytes); Assert.Equal(0, host.Budget.LiveReservations);
            if (result is not null) { Assert.Equal(snapshot, result.Counters); }
        }
        finally
        {
            host.Resume(); try { await running; } catch (QpdfException) { } catch (OperationCanceledException) { }
        }
    }
}
