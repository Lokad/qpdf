using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class SplitInputCollisionTests
{
    private static readonly QpdfPath Primary = QpdfPath.From("primary.pdf"), Source = QpdfPath.From("other.pdf"),
        First = QpdfPath.From("result-1.pdf"), Last = QpdfPath.From("result-2.pdf"), Pattern = QpdfPath.From("result.pdf");
    private static readonly byte[] Prior = [17, 23, 41];

    public static IEnumerable<object[]> Collisions()
    {
        foreach (bool marker in new[] { false, true })
        foreach (int slot in new[] { 1, 2 })
        foreach (bool omitted in new[] { false, true })
        { yield return [false, marker, slot, omitted]; }
        yield return [true, false, 1, true];
        yield return [true, true, 2, true];
    }

    private sealed class ObservedHost(InMemoryQpdfHost storage) : IQpdfHost
    {
        internal int Creates;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Creates++; return storage.CreateOutputAsync(path, token); }
    }

    private static async Task<QpdfExecutionResult> Execute(ObservedHost host, bool job, bool empty,
        string primary, string source, string pattern)
    {
        if (job)
        {
            Dictionary<string, object> settings = new()
            { ["outputFile"] = pattern, ["splitPages"] = "1", ["pages"] = new[] { new { file = source, range = "1,1" } } };
            if (empty) { settings["empty"] = ""; } else { settings["inputFile"] = primary; }
            return await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None);
        }
        return await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation([
            empty ? "--empty" : primary, "--pages", source, "1,1", "--", "--split-pages=1", pattern]), CancellationToken.None);
    }

    [Theory]
    [MemberData(nameof(Collisions))]
    public async Task EveryExpandedNameProtectsThePrimaryEvenWhenItsPagesAreOmitted(bool job, bool marker, int slot, bool omitted)
    {
        using InMemoryQpdfHost storage = new();
        byte[] source = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf"));
        string primary = $"result-{slot}.pdf";
        storage.AddInput(QpdfPath.From(primary), source, QpdfInputOwnership.Copy);
        storage.AddInput(QpdfPath.From("other.pdf"), source, QpdfInputOwnership.Copy);
        byte[] prior = [17, 23, 41];
        for (int group = 1; group <= 2; group++)
        {
            await using IQpdfOutputTransaction seed = await storage.CreateOutputAsync(QpdfPath.From($"result-{group}.pdf"), CancellationToken.None);
            await seed.WriteAsync(prior, CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }
        ObservedHost host = new(storage);
        QpdfExecutionResult result = await Execute(host, job, false, primary, omitted ? "other.pdf" : ".",
            marker ? "result-%d.pdf" : "result.pdf");
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.InvalidArguments
            && item.Message.Contains(primary, StringComparison.Ordinal));
        Assert.Equal(0, host.Creates);
        Assert.Equal(0L, result.Counters.TotalOutputBytes);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, storage.OpenLeaseCount);
        for (int group = 1; group <= 2; group++)
        {
            Assert.True(storage.TryGetOutput(QpdfPath.From($"result-{group}.pdf"), out byte[] bytes));
            Assert.Equal(prior, bytes);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ExplicitEmptyPrimaryDoesNotProtectAnUnopenedHostInput(bool job, bool marker)
    {
        using InMemoryQpdfHost storage = new();
        byte[] source = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf"));
        storage.AddInput(QpdfPath.From("result-2.pdf"), source, QpdfInputOwnership.Copy);
        storage.AddInput(QpdfPath.From("other.pdf"), source, QpdfInputOwnership.Copy);
        ObservedHost host = new(storage);
        QpdfExecutionResult result = await Execute(host, job, true, "result-2.pdf", "other.pdf",
            marker ? "result-%d.pdf" : "result.pdf");
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(2, host.Creates);
        for (int group = 1; group <= 2; group++)
        {
            Assert.True(storage.TryGetOutput(QpdfPath.From($"result-{group}.pdf"), out byte[] bytes));
            using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(bytes);
            Assert.Equal(1, document.NumberOfPages);
        }
        Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Theory]
    [InlineData("catalog-primary")]
    [InlineData("placement")]
    [InlineData("earlier-output")]
    public async Task LatePartCannotCommitToAClaimedStorageIdentity(string role)
    {
        using RecordingJobHost recording = new(null, null, "success", true);
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf"));
        QpdfPath placement = QpdfPath.From("placement.pdf");
        recording.Inner.AddInput(Primary, input, QpdfInputOwnership.Copy);
        recording.Inner.AddInput(Source, input, QpdfInputOwnership.Copy);
        QpdfPath canonical = role switch
        {
            "catalog-primary" => Primary,
            "placement" => placement,
            _ => First,
        };
        if (canonical != Primary) { recording.Inner.AddInput(canonical, input, QpdfInputOwnership.Copy); }
        await SeedPrior(recording.Inner, First);
        await SeedPrior(recording.Inner, Last);
        if (canonical != First) { await SeedPrior(recording.Inner, canonical); }

        IQpdfHost host = new OutputAliasHost(recording, Last, canonical);
        string[] overlays = role == "placement" ? ["--overlay", placement.Value, "--"] : [];
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new([Primary.Value, "--pages", Source.Value, "1,1", "--", .. overlays, "--split-pages=1", Pattern.Value]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.InvalidArguments);
        Assert.Contains(Last.Value, diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(role == "earlier-output" ? "collide" : "aliases an input", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(recording.CreatedPaths);
        Assert.Empty(recording.Committed);
        AssertPrior(recording.Inner, First);
        AssertPrior(recording.Inner, Last);
        AssertPrior(recording.Inner, canonical);
        ExecutionJobFixtures.AssertClosed(recording, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedSplitUsesIdentityAndAllowsEqualBytesAtSeparateTargets(bool alias)
    {
        using RecordingJobHost recording = new(null, null, "success", true);
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf"));
        recording.Inner.AddInput(Primary, input, QpdfInputOwnership.Copy);
        recording.Inner.AddInput(Last, input, QpdfInputOwnership.Copy);
        await SeedPrior(recording.Inner, First);
        await SeedPrior(recording.Inner, Last);
        IQpdfHost host = alias ? new OutputAliasHost(recording, Last, Primary) : recording;
        PdfPageSource[] sources = [new(Primary, PdfPageSelectionOptions.FromPages([1, 2]))];
        PdfPageMergeOptions options = new() { SplitPages = 1 };
        if (alias)
        {
            QpdfParseException error = await Assert.ThrowsAsync<QpdfParseException>(() =>
                Qpdf.SelectPagesAsync(host, sources, Pattern, options, CancellationToken.None).AsTask());
            Assert.Contains(Last.Value, error.Message, StringComparison.Ordinal);
            Assert.Empty(recording.CreatedPaths);
            Assert.Empty(recording.Committed);
            AssertPrior(recording.Inner, First);
            AssertPrior(recording.Inner, Last);
            ExecutionJobFixtures.AssertClosed(recording, null);
        }
        else
        {
            PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host, sources, Pattern, options, CancellationToken.None);
            Assert.Equal(2, result.PageCount);
            Assert.Equal([First, Last], recording.CreatedPaths);
            Assert.Equal(2, recording.Committed.Count);
            foreach (QpdfPath path in new[] { First, Last })
            {
                Assert.True(recording.Inner.TryGetOutput(path, out byte[] bytes));
                using UglyToad.PdfPig.PdfDocument part = UglyToad.PdfPig.PdfDocument.Open(bytes);
                Assert.Equal(1, part.NumberOfPages);
            }
            ExecutionJobFixtures.AssertClosed(recording, result);
        }
    }

    private static async Task SeedPrior(InMemoryQpdfHost storage, QpdfPath path)
    {
        await using IQpdfOutputTransaction target = await storage.CreateOutputAsync(path, CancellationToken.None);
        await target.WriteAsync(Prior, CancellationToken.None);
        await target.CommitAsync(CancellationToken.None);
    }

    private static void AssertPrior(InMemoryQpdfHost storage, QpdfPath path)
    {
        Assert.True(storage.TryGetOutput(path, out byte[] bytes));
        Assert.Equal(Prior, bytes);
    }
}
