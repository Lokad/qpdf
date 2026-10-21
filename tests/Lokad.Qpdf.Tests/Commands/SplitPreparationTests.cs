using System.Collections;
using System.Reflection;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class SplitPreparationTests
{
    [Fact]
    public async Task ManyPreparedSplitsFitTheBudgetAfterTemporaryModelsRefund()
    {
        string refs = string.Join(' ', Enumerable.Range(19, 512).Select(number => number + " 0 R"));
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [" + string.Join(' ', Enumerable.Range(3, 16).Select(number => number + " 0 R")) + "] /Count 16 >>", null) ];
        for (int page = 3; page < 19; page++)
        { objects.Add((page, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 20] /Resources << /Extra [" + refs + "] >> >>", null)); }
        for (int index = 0; index < 512; index++) { objects.Add((19 + index, "<< /V " + index + " >>", null)); }
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), CheckTests.Assemble(objects, 1), QpdfInputOwnership.Transfer);
        RecordingMemoryBudget root = new(1310720);
        using (IMemoryScope owner = root.CreateScope())
        using (CancellationTokenSource cancellation = new())
        {
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(new Host(storage, owner, "none", cancellation),
                new(["input", "--object-streams=disable", "--split-pages=1", "output"]), cancellation.Token);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            for (int index = 1; index <= 16; index++)
            {
                Assert.True(storage.TryGetOutput(QpdfPath.From("output-" + index.ToString("D2")), out byte[] bytes));
                using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(bytes);
                Assert.Equal(1, document.NumberOfPages);
            }
            Assert.Equal(0L, storage.OpenLeaseCount);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    private delegate PdfDictionary PagesCall(IReadOnlyList<PdfReference> kids, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token);
    private static readonly PagesCall Pages = (typeof(Qpdf).GetMethod("PagesNode", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException()).CreateDelegate<PagesCall>();

    [Fact]
    public void PageChildrenRefuseBeforeAllocatingLargeBacking()
    {
        PdfReference[] children = Enumerable.Repeat(new PdfReference(7, 0), 262144).ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(31L);
        long before = governor.Snapshot().LiveMemoryBytes, allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => Pages(children, memory, governor, CancellationToken.None));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0L, 65536L);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(31L, memory.ReservedBytes);
    }

    private sealed class CancelingChildren(Action cancel) : IReadOnlyList<PdfReference>
    {
        public int Count => 128;
        public PdfReference this[int index] { get { if (index == 32) { cancel(); } return new(7, 0); } }
        public IEnumerator<PdfReference> GetEnumerator() => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherTokenInterruptsChildConstructionAndTheGroupRefunds(bool caller)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(31L);
        long before = governor.Snapshot().LiveMemoryBytes;
        using (ParserMemoryScope attempt = ParserMemoryScope.Create(governor))
        {
            Assert.Throws<OperationCanceledException>(() => Pages(new CancelingChildren(cancellation.Cancel), attempt,
                governor, caller ? cancellation.Token : CancellationToken.None));
        }
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(31L, prior.ReservedBytes);
    }

    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget, string failure, CancellationTokenSource cancellation) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        private void Fail(QpdfPath path, string phase, CancellationToken token)
        {
            if (path.Value != "output-2") { return; }
            if (failure == "cancel" && phase == "write") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            if (failure == phase) { throw new QpdfHostException(path, QpdfHostFailureReason.WriteFailed); }
        }
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Fail(path, "create", token); return new Transaction(this, await storage.CreateOutputAsync(path, token)); }
        private sealed class Transaction(Host host, IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            { host.Fail(Path, "write", token); return inner.WriteAsync(bytes, token); }
            public ValueTask CommitAsync(CancellationToken token)
            { host.Fail(Path, "commit", token); return inner.CommitAsync(token); }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); host.Fail(Path, "close", CancellationToken.None); }
        }
    }

    [Theory]
    [InlineData("create", false)]
    [InlineData("write", false)]
    [InlineData("commit", false)]
    [InlineData("cancel", false)]
    [InlineData("close", false)]
    [InlineData("create", true)]
    [InlineData("write", true)]
    [InlineData("commit", true)]
    [InlineData("cancel", true)]
    [InlineData("close", true)]
    public async Task CapturedSplitsSurviveGroupCleanupAndKeepEarlierCommitsOnLaterFailure(string failure, bool streamed)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), ObjectStreamPlanningTests.Fixture(16), QpdfInputOwnership.Transfer);
        byte[] prior = [17, 23, 41];
        for (int index = 1; index <= 3; index++)
        {
            await using IQpdfOutputTransaction initial = await storage.CreateOutputAsync(QpdfPath.From("output-" + index), CancellationToken.None);
            await initial.WriteAsync(prior, CancellationToken.None); await initial.CommitAsync(CancellationToken.None);
        }
        RecordingMemoryBudget root = new(64L * 1024 * 1024);
        using (IMemoryScope owner = root.CreateScope())
        using (CancellationTokenSource cancellation = new())
        {
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(new Host(storage, owner, failure, cancellation),
                new(["input", "--qdf", "--object-streams=" + (streamed ? "generate" : "disable"), "--split-pages=1",
                    "--pages", ".", "1,1,1", "--", "output"]), cancellation.Token);
            Assert.Equal(failure == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            for (int index = 1; index <= 3; index++)
            {
                Assert.True(storage.TryGetOutput(QpdfPath.From("output-" + index), out byte[] bytes));
                if (index == 1 || index == 2 && failure == "close")
                {
                    using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(bytes);
                    Assert.Equal(1, document.NumberOfPages);
                }
                else { Assert.Equal(prior, bytes); }
            }
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}
