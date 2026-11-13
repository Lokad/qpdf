using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Reading;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class TypedWarningOwnershipTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf");
    private static readonly string[] Routes = ["typed-page", "typed-page-rotation", "typed-pages", "typed-pages-rotation",
        "typed-pages-options", "typed-split", "typed-json", "typed-json-sidecars",
        "typed-update", "typed-extract", "typed-add", "typed-remove", "typed-copy"];

    private static IReadOnlyList<QpdfDiagnostic> Warnings(object result) => result switch
    {
        PdfPageSelectionResult r => r.Warnings,
        PdfMultiPageSelectionResult r => r.Warnings,
        QpdfJsonResult r => r.Warnings,
        QpdfJsonImportResult r => r.Warnings,
        QpdfAttachmentExtraction r => r.Warnings,
        QpdfAttachmentEditResult r => r.Warnings,
        _ => throw new InvalidOperationException("Unknown typed warning result."),
    };

    private static async Task<RecordingJobHost> Prepare(string route, bool compressed, string boundary, string outcome)
    {
        string originalRoute = route is "typed-split" ? "typed-pages-options"
            : route.StartsWith("typed-json", StringComparison.Ordinal) ? "typed-json" : route;
        RecordingJobHost host = await ExecutionJobFixtures.PrepareAsync(originalRoute, compressed, boundary, outcome);
        InMemoryQpdfHost inner = host.Inner;
        byte[] bytes = DocumentWarningSnapshotTests.OriginalFixture(compressed);
        if (route is "typed-extract" or "typed-remove" or "typed-copy")
        {
            List<(string Dict, byte[]? StreamData)> extras = [
                ("<< /Type /EmbeddedFile /Length 4 /Filter /Bogus >>", "q Q\n"u8.ToArray()),
                ("<< /Type /Filespec /F (payload.bin) /EF << /F 6 0 R >> >>", null),
                ("<< /Names [(payload) 7 0 R] >>", null)];
            bytes = AttachmentTests.BuildPdf(" /Names << /EmbeddedFiles 8 0 R >>", "", extras);
            int at = bytes.AsSpan().IndexOf("/Count 1"u8); Assert.True(at >= 0); bytes[at + 7] = (byte)'2';
        }
        inner.AddInput(Input, bytes, QpdfInputOwnership.Copy);
        inner.AddInput(QpdfPath.From("second.pdf"), bytes, QpdfInputOwnership.Copy);
        if (route == "typed-split")
        {
            await using IQpdfOutputTransaction seed = await inner.CreateOutputAsync(QpdfPath.From("output-1.pdf"), CancellationToken.None);
            await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
        }
        return host;
    }

    private static async Task<object> Start(IQpdfHost host, string route, QpdfLimits limits, CancellationToken token)
    {
        if (route == "typed-split")
        {
            return await Qpdf.SelectPagesAsync(host, [new PdfPageSource(Input, new PdfPageSelectionOptions(1))], Output,
            new PdfPageMergeOptions { SplitPages = 1 }, limits, token);
        }
        if (route == "typed-json-sidecars")
        { return await Qpdf.GetDocumentJsonAsync(host, Input, Output, new QpdfJsonOptions(2, [], "file", "stream", [], false), limits, token); }
        return await ExecutionJobFixtures.RunAsync(host, route, limits, token);
    }

    public static IEnumerable<object[]> PublicJobs()
    { for (int i = 0; i < Routes.Length; i++) { yield return [Routes[i], i % 2 == 0]; } }

    [Theory]
    [MemberData(nameof(PublicJobs))]
    [Trait("Layer", "Contract")]
    public async Task CompleteJobsKeepWarningAliasesAfterHostCleanup(string route, bool compressed)
    {
        using RecordingJobHost host = await Prepare(route, compressed, "read", "success");
        Task<object> task = Start(host, route, QpdfLimits.Default, CancellationToken.None);
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L); host.Resume();
            object result = await task.WaitAsync(TimeSpan.FromSeconds(10));
            IReadOnlyList<QpdfDiagnostic> warnings = Warnings(result);
            Assert.NotEmpty(warnings);
            QpdfDiagnostic[] copy = warnings.ToArray();
            QpdfDiagnostic element = warnings[0], recordCopy = element with { };
            QpdfPath path = result is PdfMultiPageSelectionResult multiple ? Assert.Single(multiple.OutputPaths) : Output;
            Assert.True(host.Inner.TryGetOutput(path, out byte[] output));
            ExecutionJobFixtures.AssertClosed(host, result);
            Assert.Equal(copy, warnings); Assert.Same(element, warnings[0]);
            Assert.Equal(element, recordCopy); Assert.Same(element.Message, recordCopy.Message);
            Assert.All(copy, x => Assert.False(string.IsNullOrEmpty(x.Message)));
            if (warnings is IList<QpdfDiagnostic> mutable)
            { Assert.True(mutable.IsReadOnly); Assert.Throws<NotSupportedException>(() => mutable.Clear()); }
            if (route is "typed-page" or "typed-page-rotation")
            {
                Assert.Equal(QpdfDiagnosticCode.PageCountMismatch, Assert.Single(warnings).Code);
                using InMemoryQpdfHost validation = new(); validation.AddInput(Input, output, QpdfInputOwnership.Copy);
                QpdfCheckResult check = await Qpdf.CheckAsync(validation, Input, CancellationToken.None);
                Assert.True(check.Passed); Assert.Equal(1, check.PageCount); Assert.Equal(0L, validation.OpenLeaseCount);
            }
        }
        finally { host.Resume(); await task; }
    }

    public static IEnumerable<object[]> FaultJobs()
    {
        foreach (string boundary in new[] { "write", "commit", "output-close" })
        foreach (string outcome in new[] { "fault", "cancel" }) { yield return ["typed-page", true, boundary, outcome]; }
        foreach ((string route, string boundary) in new[] { ("typed-pages", "write"), ("typed-split", "output-close"),
            ("typed-json", "output-close"), ("typed-update", "commit"), ("typed-extract", "output-close"), ("typed-add", "write") })
        foreach (string outcome in new[] { "fault", "cancel" }) { yield return [route, false, boundary, outcome]; }
    }

    [Theory]
    [MemberData(nameof(FaultJobs))]
    [Trait("Layer", "Contract")]
    public async Task WarningJobsPreserveOutputAliasesAndCloseOnFaultOrCancellation(string route, bool compressed,
        string boundary, string outcome)
    {
        using RecordingJobHost host = await Prepare(route, compressed, boundary, outcome == "fault" ? "fault" : "success");
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior));
        using CancellationTokenSource cancellation = new();
        Task<object> task = Start(host, route, QpdfLimits.Default, cancellation.Token);
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L);
            if (outcome == "cancel") { cancellation.Cancel(); } else { host.Resume(); }
            if (outcome == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); }
            else { await Assert.ThrowsAnyAsync<QpdfException>(() => task); }
            Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            Assert.True(host.Inner.TryGetOutput(route == "typed-split" ? QpdfPath.From("output-1.pdf") : Output, out byte[] output));
            if (boundary != "output-close") { Assert.Equal(prior, output); }
            else if (route == "typed-extract") { Assert.Equal("q Q\n"u8.ToArray(), output); }
            else if (route == "typed-json") { Assert.StartsWith("{", Encoding.UTF8.GetString(output).TrimStart()); }
            else { Assert.True(output.AsSpan().StartsWith("%PDF-"u8)); }
            ExecutionJobFixtures.AssertClosed(host, null);
        }
        finally { host.Resume(); try { await task; } catch (QpdfException) { } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonCreationBorrowsEmptyWarningStorage(bool compressed)
    {
        using RecordingJobHost host = await Prepare("typed-import", compressed, "read", "success");
        Task<object> task = Start(host, "typed-import", QpdfLimits.Default, CancellationToken.None);
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10)); host.Resume();
            object result = await task;
            Assert.Same(Array.Empty<QpdfDiagnostic>(), Warnings(result)); ExecutionJobFixtures.AssertClosed(host, result);
        }
        finally { host.Resume(); await task; }
    }

    public static IEnumerable<object[]> AdmissionJobs()
    { foreach (bool compressed in new[] { false, true }) foreach (bool shortBudget in new[] { false, true }) { yield return [compressed, shortBudget]; } }

    [Theory]
    [MemberData(nameof(AdmissionJobs))]
    [Trait("Layer", "Contract")]
    public async Task CompleteJobsHonorExistingAdmissionCeilings(bool compressed, bool shortBudget)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = shortBudget ? 16384L : 32768L };
        using RecordingJobHost host = await Prepare("typed-page", compressed, "read", "success");
        Task<object> task = Start(host, "typed-page", limits, CancellationToken.None);
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L); host.Resume();
            object? result = null;
            QpdfLimitException? failure = null;
            try { result = await task; }
            catch (QpdfLimitException error) { failure = error; }
            if (failure is not null)
            {
                Assert.True(shortBudget);
                Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
                Assert.Empty(host.Committed);
                Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior));
                Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            }
            else
            {
                IReadOnlyList<QpdfDiagnostic> warnings = Warnings(Assert.IsAssignableFrom<object>(result));
                Assert.Single(warnings);
                Assert.Single(host.Committed);
                Assert.True(host.Inner.TryGetOutput(Output, out byte[] published));
                using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(published);
                Assert.Equal(1, independent.NumberOfPages);
            }
            if (result is not null) { Assert.True(ExecutionJobFixtures.Counters(result).PeakMemoryBytes <= limits.MaxMemoryBytes); }
            ExecutionJobFixtures.AssertClosed(host, result);
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
        }
        finally
        {
            host.Resume(); try { await task; } catch (QpdfException) { }
        }
    }

    private sealed class ClosingCancellationHost(RecordingJobHost inner, CancellationTokenSource cancellation) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => inner.Budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new OutputLease(await inner.CreateOutputAsync(path, token), cancellation);
        private sealed class OutputLease(IQpdfOutputTransaction inner, CancellationTokenSource cancellation) : IQpdfOutputTransaction
        {
            private bool _committed;
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) => inner.WriteAsync(bytes, token);
            public async ValueTask CommitAsync(CancellationToken token) { await inner.CommitAsync(token); _committed = true; }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public async ValueTask DisposeAsync()
            { await inner.DisposeAsync(); if (_committed) { cancellation.Cancel(); } }
        }
    }

    public static IEnumerable<object[]> PostCommitJobs()
    {
        for (int i = 0; i < Routes.Length; i++)
        { if (Routes[i] != "typed-json-sidecars") { yield return [Routes[i], i % 2 == 0]; } }
    }

    [Theory]
    [MemberData(nameof(PostCommitJobs))]
    public async Task CompletedPublicationDoesNotAddANewCancellationPoll(string route, bool compressed)
    {
        using RecordingJobHost host = await Prepare(route, compressed, "read", "success");
        using CancellationTokenSource cancellation = new();
        Task<object> task = Start(new ClosingCancellationHost(host, cancellation), route, QpdfLimits.Default, cancellation.Token);
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L); host.Resume();
            object result = await task;
            Assert.True(cancellation.IsCancellationRequested); Assert.NotEmpty(Warnings(result)); ExecutionJobFixtures.AssertClosed(host, result);
        }
        finally { host.Resume(); try { await task; } catch (OperationCanceledException) { } }
    }

    [Fact]
    public void FinalPreparationPreservesEveryAcceptedWarningInOrder()
    {
        using RecordingJobHost host = new(null, null, "success", true);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 1L << 20 }, CancellationToken.None, host);
        governor.PrepareTypedResult(PdfPageSelectionResult.StorageBytes);
        DiagnosticBag bag = new(governor);
        bag.Add(QpdfDiagnosticCode.PageCountMismatch, QpdfDiagnosticSeverity.Warning, "First warning.");
        using ResultWarningSnapshot snapshot = Assert.IsType<ResultWarningSnapshot>(ResultWarningSnapshot.Create(governor, bag, CancellationToken.None));
        snapshot.Prepare();
        IReadOnlyList<QpdfDiagnostic> first = snapshot.Warnings;
        Assert.Same(bag.Entries[0], Assert.Single(first));
        bag.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Late preparation warning.");
        long pressure = governor.RemainingMemoryBytes;
        governor.ReserveMemory(pressure);
        try
        {
            Assert.Throws<QpdfLimitException>(() => snapshot.Prepare());
            Assert.Same(first, snapshot.Warnings); Assert.Single(first);
        }
        finally { governor.ReleaseMemory(pressure); }
        snapshot.Prepare();
        IReadOnlyList<QpdfDiagnostic> final = snapshot.Warnings;
        Assert.Equal(2, final.Count); Assert.Single(first);
        for (int i = 0; i < final.Count; i++) { Assert.Same(bag.Entries[i], final[i]); }
        Assert.Equal("Late preparation warning.", final[1].Message);
        snapshot.Dispose(); governor.AbandonTypedCounters(); host.Dispose();
        Assert.Equal(0L, host.Budget.ReservedBytes); Assert.Equal(0, host.Budget.LiveReservations);
        Assert.Equal("First warning.", first[0].Message); Assert.Equal("Late preparation warning.", final[1].Message);
    }
}
