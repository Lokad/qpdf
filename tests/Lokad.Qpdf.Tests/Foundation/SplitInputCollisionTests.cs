using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class SplitInputCollisionTests
{
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
}
