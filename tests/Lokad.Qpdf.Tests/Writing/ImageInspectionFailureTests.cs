using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ImageInspectionFailureTests
{
    [Theory]
    [InlineData("raw-value", "memory")]
    [InlineData("utf8-name", "memory")]
    [InlineData("binary-name", "memory")]
    [InlineData("nested-value", "depth")]
    [InlineData("raw-value", "caller")]
    [InlineData("utf8-name", "caller")]
    [InlineData("raw-value", "governor")]
    [InlineData("binary-name", "governor")]
    public async Task BoundedFormattingFailureRefundsItsScratch(string kind, string failure)
    {
        string name = kind == "utf8-name" ? string.Concat(Enumerable.Repeat("#c3#a9", 32768))
            : kind == "binary-name" ? string.Concat(Enumerable.Repeat("#ff", 32768)) : "Im0";
        string width = kind == "nested-value" ? new string('[', 24) + "2" + new string(']', 24) : "(" + new string('A', 262144) + ")";
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), ImageInspectionTests.Fixture("6 0 R", "/Image", "/" + name + " 5 0 R",
            "/Width " + width + " /Height 2 /BitsPerComponent 8"), QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None))
        {
            for (int number = 1; number <= 6; number++) { document.GetObject(number); }
            _ = document.PageReferences;
            QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L, MaxNestingDepth = 8L };
            using CancellationTokenSource cancellation = new();
            ResourceGovernor governor = new(limits, cancellation.Token);
            using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(71L);
            long baseline = governor.Snapshot().LiveMemoryBytes;
            if (failure == "governor") { cancellation.Cancel(); }
            void Inspect()
            {
                using PreparedJsonOutput output = PreparedJsonOutput.Create(governor);
                using DisplayBuffer text = DocumentJson.WriteDocument(document, new(2, ["pages"], "none", "none", null, [], false),
                    output, limits, governor, new(governor), failure == "caller" ? new CancellationToken(true) : CancellationToken.None);
            }
            if (failure is "caller" or "governor") { Assert.ThrowsAny<OperationCanceledException>(Inspect); }
            else if (failure == "depth") { Assert.Throws<QpdfParseException>(Inspect); }
            else { Assert.Throws<QpdfLimitException>(Inspect); }
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget, string fault, CancellationTokenSource cancellation) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        private void Check(string phase)
        {
            if (fault == "cancel" && phase == "write") { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            if (fault == phase) { throw new QpdfHostException(QpdfPath.From("output"), QpdfHostFailureReason.WriteFailed); }
        }
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Check("create"); return new Transaction(this, await storage.CreateOutputAsync(path, token)); }
        private sealed class Transaction(Host host, IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) { host.Check("write"); return inner.WriteAsync(bytes, token); }
            public ValueTask CommitAsync(CancellationToken token) { host.Check("commit"); return inner.CommitAsync(token); }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private static async Task<InMemoryQpdfHost> Storage()
    {
        InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), ImageInspectionTests.Fixture("6 0 R", "/Image", "/Im#c3#a9 5 0 R",
            "/Width (" + new string('A', 32768) + ") /Height 2 /BitsPerComponent 8"), QpdfInputOwnership.Transfer);
        await using IQpdfOutputTransaction prior = await storage.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await prior.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
        return storage;
    }

    private static async Task Run(IQpdfHost host, string route, CancellationToken token)
    {
        if (route == "typed")
        { await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new(2, ["pages"]), token); }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","json":"2","jsonKey":["pages"]}""", token)
                : await Qpdf.ExecuteAsync(host, new(["input", "--json=2", "--json-key=pages", "--", "output"]), token);
            if (result.Status == QpdfExecutionStatus.Canceled) { throw new OperationCanceledException(); }
            if (result.Status == QpdfExecutionStatus.Failed) { throw new QpdfException(string.Join(';', result.Diagnostics.Select(item => item.Message))); }
        }
    }

    public static IEnumerable<object[]> PublicationCases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (string phase in new[] { "create", "write", "commit", "cancel" }) { yield return [route, phase]; }
    }

    [Theory]
    [MemberData(nameof(PublicationCases))]
    public async Task PublicationFailuresPreserveExistingOutputAndRefundAdmission(string route, string phase)
    {
        using InMemoryQpdfHost storage = await Storage(); RecordingMemoryBudget root = new(16L << 20);
        using (IMemoryScope owner = root.CreateScope())
        using (CancellationTokenSource cancellation = new())
        {
            Host host = new(storage, owner, phase, cancellation);
            if (phase == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(host, route, cancellation.Token)); }
            else { await Assert.ThrowsAnyAsync<QpdfException>(() => Run(host, route, cancellation.Token)); }
            Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal(new byte[] { 17, 23, 41 }, output);
            Assert.Equal(0L, storage.OpenLeaseCount);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task EverySharedGrowthCanRefuseBeforePublishing(string route)
    {
        int growths;
        using (InMemoryQpdfHost storage = await Storage())
        {
            RecordingMemoryBudget root = new(16L << 20);
            using (IMemoryScope owner = root.CreateScope())
            using (CancellationTokenSource cancellation = new()) { await Run(new Host(storage, owner, "none", cancellation), route, cancellation.Token); }
            Assert.Equal(0L, root.ReservedBytes); growths = root.GrowthCalls;
        }
        Assert.InRange(growths, 1, 32);
        for (int growth = 1; growth <= growths; growth++)
        {
            using InMemoryQpdfHost storage = await Storage(); RecordingMemoryBudget root = new(16L << 20) { RefuseOnGrowth = growth };
            using (IMemoryScope owner = root.CreateScope())
            using (CancellationTokenSource cancellation = new())
            {
                await Assert.ThrowsAnyAsync<QpdfException>(() => Run(new Host(storage, owner, "none", cancellation), route, cancellation.Token));
                Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal(new byte[] { 17, 23, 41 }, output);
                Assert.Equal(0L, storage.OpenLeaseCount);
            }
            Assert.Equal(0L, root.ReservedBytes);
        }
    }
}
