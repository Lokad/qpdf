using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class OutlineInspectionTests
{
    internal static byte[] Fixture(params string[] entries)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null),
            (4, "<< /Type /Outlines /First 5 0 R >>", null)];
        for (int i = 0; i < entries.Length; i++) { objects.Add((5 + i, entries[i], null)); }
        return CheckTests.Assemble(objects, 1);
    }

    private static ValueTask<QpdfExecutionResult> Run(InMemoryQpdfHost host, bool job, CancellationToken token)
        => job ? Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input","outputFile":"output","json":"2","jsonKey":["outlines"]}""", token)
            : Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--json=2", "--json-key=outlines", "--", "output"]), token);

    [Theory]
    [InlineData("root-self", false)]
    [InlineData("root-self", true)]
    [InlineData("root-pair", false)]
    [InlineData("root-pair", true)]
    [InlineData("child-self", false)]
    [InlineData("child-self", true)]
    [InlineData("child-pair", false)]
    [InlineData("child-pair", true)]
    [InlineData("ancestor", false)]
    [InlineData("ancestor", true)]
    public async Task CyclesFailBeforePublication(string shape, bool job)
    {
        string[] entries = shape switch {
            "root-self" => ["<< /Title (A) /Next 5 0 R >>"],
            "root-pair" => ["<< /Title (A) /Next 6 0 R >>", "<< /Title (B) /Next 5 0 R >>"],
            "child-self" => ["<< /Title (A) /First 6 0 R >>", "<< /Title (B) /Next 6 0 R >>"],
            "child-pair" => ["<< /Title (A) /First 6 0 R >>", "<< /Title (B) /Next 7 0 R >>", "<< /Title (C) /Next 6 0 R >>"],
            _ => ["<< /Title (A) /First 6 0 R >>", "<< /Title (B) /First 5 0 R >>"] };
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(entries), QpdfInputOwnership.Transfer);
        byte[] prior = [17, 23, 41];
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await seed.WriteAsync(prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        QpdfExecutionResult result = await Run(host, job, timeout.Token);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("cycle", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal(prior, output);
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("99 0 R")]
    [InlineData("[]")]
    public async Task MalformedSiblingsFailWithoutOutput(string next)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("<< /Title (A) /Next " + next + " >>"), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, false, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.NotEmpty(result.Diagnostics);
        Assert.False(host.TryGetOutput(QpdfPath.From("output"), out _)); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SiblingTrackingPreservesSeparateSharedBranchesAndDirectNodes()
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(
            "<< /Title (A) /First 6 0 R /Next << /Title (B) /First 6 0 R >> >>",
            "<< /Title (Child) /Dest [3 0 R /Fit] >>"), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, false, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        using JsonDocument json = JsonDocument.Parse(output); JsonElement roots = json.RootElement.GetProperty("outlines");
        Assert.Equal(2, roots.GetArrayLength());
        Assert.Equal("Child", roots[0].GetProperty("kids")[0].GetProperty("title").GetString());
        Assert.Equal("Child", roots[1].GetProperty("kids")[0].GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task PageSummariesIncludeNestedBookmarksInTreeOrder(string route)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(
            "<< /Title (A) /Dest [3 0 R /Fit] /First 6 0 R /Next 7 0 R >>",
            "<< /Title (Child) /Dest [3 0 R /Fit] /First << /Title (Direct) /Dest [3 0 R /Fit] >> >>",
            "<< /Title (B) /Dest [3 0 R /Fit] >>"), QpdfInputOwnership.Transfer);
        if (route == "typed") { await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["pages"]), CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","json":"2","jsonKey":["pages"]}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--json=2", "--json-key=pages", "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal(0L, host.OpenLeaseCount);
        using JsonDocument json = JsonDocument.Parse(output);
        JsonElement entries = json.RootElement.GetProperty("pages")[0].GetProperty("outlines");
        Assert.Equal(new[] { "A", "Child", "Direct", "B" }, entries.EnumerateArray().Select(entry => entry.GetProperty("title").GetString()));
        Assert.All(entries.EnumerateArray(), entry => Assert.False(entry.TryGetProperty("kids", out _)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusalAndCancellationRefundInspectionScratch(bool canceled)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("<< /Title (" + new string('A', 1048576) + ") >>"), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        document.GetObject(4); document.GetObject(5);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096 };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(71L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        JsonRequest request = new(2, ["outlines"], "none", "none", null, [], false);
        void Inspect()
        {
            using PreparedJsonOutput output = PreparedJsonOutput.Create(governor);
            using DisplayBuffer text = DocumentJson.WriteDocument(document, request, output, limits, governor, new(governor),
                canceled ? new CancellationToken(true) : CancellationToken.None);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        if (canceled) { Assert.ThrowsAny<OperationCanceledException>(Inspect); }
        else { Assert.Throws<QpdfLimitException>(Inspect); }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 131072L);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }
}
