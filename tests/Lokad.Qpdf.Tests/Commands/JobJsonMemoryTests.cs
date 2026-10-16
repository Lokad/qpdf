using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Jobs;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class JobJsonMemoryTests
{
    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        internal int OutputOpens;
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { OutputOpens++; return storage.CreateOutputAsync(path, token); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DensityPreflightUsesAggregateCreditWithoutTokenAllocations(bool utf8)
    {
        string dense = "[" + string.Join(',', Enumerable.Repeat("\"a\"", 8192)) + "]";
        string scalar = "[\"" + new string('A', dense.Length - 4) + "\"]";
        byte[] denseBytes = Encoding.UTF8.GetBytes(dense), scalarBytes = Encoding.UTF8.GetBytes(scalar);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        (long Document, long Copies) Estimate(string text, byte[] bytes) => utf8
            ? JobJsonMemory.Estimate(bytes, governor) : JobJsonMemory.Estimate(text.AsSpan(), bytes.Length, governor);
        var single = Estimate(scalar, scalarBytes);
        _ = Estimate(dense, denseBytes);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var many = Estimate(dense, denseBytes);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0L, 8192L);
        Assert.Equal(denseBytes.Length, scalarBytes.Length);
        Assert.True(many.Document > single.Document * 3L);
        Assert.True(many.Copies > single.Copies * 2L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    public static IEnumerable<object[]> Descriptions()
    {
        foreach (string route in new[] { "text", "file", "mixed", "nested", "argv" })
        foreach (string encoding in new[] { "ascii", "unicode", "escaped", "quotes", "surrogate" })
        {
            if (route == "argv" && encoding is "unicode" or "surrogate") { continue; }
            yield return [route, encoding];
        }
    }

    [Theory]
    [MemberData(nameof(Descriptions))]
    public async Task LargeDescriptionPublishesUnderASmallerSharedAllowance(string route, string encoding)
    {
        string description = encoding switch
        {
            "unicode" or "escaped" => new string('\u00e9', 65536),
            "quotes" => string.Concat(Enumerable.Repeat("a\"\\", 16384)),
            "surrogate" => string.Concat(Enumerable.Repeat("\ud83d\ude80", 16384)),
            _ => new string('A', 131072),
        };
        string value = encoding is "unicode" or "surrogate" ? "\"" + description + "\"" : JsonSerializer.Serialize(description);
        string json = "{\"inputFile\":\"pdf\",\"outputFile\":\"output\",\"addAttachment\":[{\"file\":\"data\",\"description\":" + value + "}]}";
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("pdf"), CheckTests.Assemble(CheckTests.SinglePage("<< /Length 0 >>", []), 1), QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("data"), "payload"u8.ToArray(), QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("config"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("settings"), "{}"u8.ToArray(), QpdfInputOwnership.Transfer);
        long limit = encoding == "ascii" ? route == "argv" ? 3145728L : 2097152L : 8388608L;
        RecordingMemoryBudget root = new(limit);
        using IMemoryScope owner = root.CreateScope();
        Host host = new(storage, owner);
        QpdfExecutionResult result = route switch
        {
            "file" => await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["--job-json-file=config"]), CancellationToken.None),
            "mixed" => await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["--job-json-file=config", "--stream-data=preserve"]), CancellationToken.None),
            "nested" => await Qpdf.ExecuteJobAsync(host, """{"jobJsonFile":"config"}""", CancellationToken.None),
            "argv" => await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["pdf", "--job-json-file=settings",
                "--add-attachment", "data", "--description=" + description, "--", "--", "output"]), CancellationToken.None),
            _ => await Qpdf.ExecuteJobAsync(host, json, CancellationToken.None),
        };
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.True(root.PeakBytes < limit);
        Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] published));
        storage.AddInput(QpdfPath.From("verify"), published, QpdfInputOwnership.Transfer);
        // Listing includes the complete description in its diagnostic output.
        // Increase only that verifier's diagnostic allowance, not the job budget.
        QpdfAttachmentInfo attachment = Assert.Single(await Qpdf.ListAttachmentsAsync(storage, QpdfPath.From("verify"),
            QpdfLimits.Default with { MaxDiagnosticBytes = 1048576L }, CancellationToken.None));
        Assert.Equal(description, attachment.Description);
        Assert.InRange(root.ReservedBytes, 1L, 1024L);
        owner.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task DenseDocumentRefusalPreservesPriorOutputAndReusesTheSameOwner()
    {
        string json = "{\"empty\":\"\",\"outputFile\":\"output\",\"pages\":[" + string.Join(',', Enumerable.Repeat("{\"file\":\"data\",\"range\":\"1\"}", 1000)) + "]}";
        using InMemoryQpdfHost storage = new();
        ResourceGovernor control = new(QpdfLimits.Default, CancellationToken.None);
        using (JobJsonLoader.LoadedJob loaded = await JobJsonLoader.ParseAsync(storage, json, QpdfLimits.Default, control, CancellationToken.None))
        { Assert.NotNull(loaded.Plan); }
        Assert.Equal(0L, control.Snapshot().LiveMemoryBytes);
        await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        RecordingMemoryBudget root = new(262144L);
        using IMemoryScope owner = root.CreateScope();
        Assert.True(owner.TryReserve(4096L, out IMemoryReservation? held));
        using (held)
        {
            Host host = new(storage, owner);
            long before = GC.GetAllocatedBytesForCurrentThread();
            QpdfExecutionResult refused = await Qpdf.ExecuteJobAsync(host, json, CancellationToken.None);
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0L, 131072L);
            Assert.Equal(QpdfExecutionStatus.Failed, refused.Status);
            Assert.Contains(refused.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
            Assert.Equal(0, host.OutputOpens);
            Assert.Equal(4096L, held.Bytes);
            Assert.InRange(root.ReservedBytes, 4096L, 5120L);
            Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            Assert.Equal("prior"u8.ToArray(), output);
            QpdfExecutionResult next = await Qpdf.ExecuteJobAsync(host, """{"empty":"","showNpages":""}""", CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, next.Status);
            Assert.Equal(0L, storage.OpenLeaseCount);
        }
    }

    [Fact]
    public void PreflightHonorsWorkAndCancellationBeforeBuildingStorage()
    {
        string json = "[\"" + new string('A', 32768) + "\"]";
        ResourceGovernor work = new(QpdfLimits.Default with { MaxWork = 8L }, CancellationToken.None);
        Assert.Throws<QpdfLimitException>(() => JobJsonMemory.Estimate(json.AsSpan(), json.Length, work));
        Assert.Equal(0L, work.Snapshot().LiveMemoryBytes);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor canceled = new(QpdfLimits.Default with { CancellationCheckInterval = 1L }, cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => JobJsonMemory.Estimate(json.AsSpan(), json.Length, canceled));
        Assert.Equal(0L, canceled.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ArgumentScratchAccountsForEscapesWithoutChargingEveryAsciiByteAtTheWorstRate()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        foreach (string value in new[] { new string('A', 65536), string.Concat(Enumerable.Repeat("\u0001\\\"\u00e9\ud83d\ude80", 8192)) })
        {
            long bytes = Encoding.UTF8.GetByteCount(value);
            long scratch = JobJsonMemory.ArgumentScratch([value], bytes, governor);
            Assert.InRange(scratch, 2L * JsonSerializer.Serialize(value).Length, 32L * bytes);
            if (value[0] == 'A') { Assert.True(scratch < 16L * bytes); }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}
