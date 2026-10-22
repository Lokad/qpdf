using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class SelectionPlanningAdmissionTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output");
    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }

    private static Task Select(IQpdfHost host, PdfPageSelectionOptions selection, bool multi, QpdfLimits limits, CancellationToken token)
        => multi ? Qpdf.SelectPagesAsync(host, [new PdfPageSource(Input, selection)], Output, limits, token).AsTask()
            : Qpdf.SelectPageAsync(host, Input, Output, selection, limits, token).AsTask();

    private sealed class CancelingGrowth(IMemoryBudget inner, CancellationTokenSource cancellation) : IMemoryBudget
    {
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            bool accepted = inner.TryReserve(bytes, out IMemoryReservation? storage);
            reservation = storage is null ? null : new Reservation(storage, cancellation); return accepted;
        }
        private sealed class Reservation(IMemoryReservation inner, CancellationTokenSource cancellation) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes)
            { bool accepted = inner.TryResize(bytes); if (bytes >= 65536L) { cancellation.Cancel(); } return accepted; }
            public void Dispose() => inner.Dispose();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HugePreownedSelectionsRefuseBeforeAllocatingPageSizedWorkingLists(bool multi, bool shared)
    {
        QpdfLimits limits = shared ? QpdfLimits.Default : QpdfLimits.Default with { MaxMemoryBytes = 128 * 1024L };
        RecordingMemoryBudget root = new(shared ? 128 * 1024L : 4 * 1024 * 1024L);
        using InMemoryQpdfHost storage = new();
        storage.AddInput(Input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction seed = await storage.CreateOutputAsync(Output, CancellationToken.None))
        { await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        PdfPageSelectionOptions selection = PdfPageSelectionOptions.FromPages(Enumerable.Repeat(1, 262144));
        using (IMemoryScope owner = root.CreateScope())
        {
            Host host = new(storage, owner);
            long before = GC.GetAllocatedBytesForCurrentThread();
            if (shared) { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => Select(host, selection, multi, limits, CancellationToken.None)); }
            else { await Assert.ThrowsAsync<QpdfLimitException>(() => Select(host, selection, multi, limits, CancellationToken.None)); }
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0L, 256 * 1024L);
            Assert.Equal(0L, storage.OpenLeaseCount);
            Assert.True(storage.TryGetOutput(Output, out byte[] prior)); Assert.Equal(new byte[] { 17, 23, 41 }, prior);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData("range", false)]
    [InlineData("preview", false)]
    [InlineData("range", true)]
    [InlineData("preview", true)]
    public void ExpandedSelectionsRefuseOrCancelBeforeMaterialGrowth(string kind, bool canceled)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, cancellation.Token);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            memory.Reserve(31L); long prior = governor.Snapshot().LiveMemoryBytes;
            PdfPageSelectionOptions selection = kind == "range" ? PdfPageSelectionOptions.FromRange("1-z") : PdfPageSelectionOptions.FirstAndLast(500000);
            if (canceled) { cancellation.Cancel(); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            Action resolve = () => selection.Resolve(1000000, 1000000, governor, memory);
            if (canceled) { Assert.ThrowsAny<OperationCanceledException>(resolve); }
            else { Assert.Throws<QpdfLimitException>(resolve); }
            Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0L, 65536L);
            Assert.InRange(governor.Snapshot().LiveMemoryBytes, prior, 4096L);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(500000)]
    [InlineData(131072)]
    public void PreviewCardinalityChecksBothBranchesBeforeAllocating(int window)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
            PdfPageSelectionOptions.FirstAndLast(window).Resolve(1000000, 1, governor, memory));
        Assert.Equal(nameof(QpdfLimits.MaxContainerSize), failure.LimitName); Assert.Equal(0L, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringExpansionRetiresAllAdmittedScratch(bool range)
    {
        RecordingMemoryBudget root = new(16 * 1024 * 1024L);
        using IMemoryScope owner = root.CreateScope(); using InMemoryQpdfHost storage = new();
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default with { CancellationCheckInterval = 1L }, cancellation.Token,
            new Host(storage, new CancelingGrowth(owner, cancellation)));
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            PdfPageSelectionOptions selection = range ? PdfPageSelectionOptions.FromRange("1-z") : PdfPageSelectionOptions.FirstAndLast(500000);
            Assert.ThrowsAny<OperationCanceledException>(() => selection.Resolve(1000000, 1000000, governor, memory));
            Assert.True(cancellation.IsCancellationRequested); Assert.True(root.PeakBytes >= 65536L);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public void AdmittedRangeAndPreviewKeepTheirOrderAndDuplicates()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            Assert.Equal(new[] { 3, 2, 1, 2 }, PdfPageSelectionOptions.FromRange("3-1,2").Resolve(5, 4, governor, memory));
            Assert.Equal(new[] { 1, 2, 4, 5 }, PdfPageSelectionOptions.FirstAndLast(2).Resolve(5, 4, governor, memory));
            Assert.Equal(new[] { 1, 2, 3 }, PdfPageSelectionOptions.FirstAndLast(2).Resolve(3, 3, governor, memory));
            Assert.True(memory.ReservedBytes > 0L);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task PublicDuplicateSelectionMetadataSurvivesPrivateCleanup()
    {
        RecordingMemoryBudget root = new(1024 * 1024L); PdfPageSelectionResult result;
        using (IMemoryScope owner = root.CreateScope())
        {
            using (InMemoryQpdfHost storage = new())
            {
                storage.AddInput(Input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf")), QpdfInputOwnership.Transfer);
                result = await Qpdf.SelectPageAsync(new Host(storage, owner), Input, Output,
                    PdfPageSelectionOptions.FromPages([2, 1, 2]), QpdfLimits.Default, CancellationToken.None);
                Assert.Equal(0L, storage.OpenLeaseCount); Assert.True(root.ReservedBytes > 0L);
            }
            Assert.Equal(new[] { 2, 1, 2 }, result.SourcePages); Assert.Equal(3, result.PageCount);
            Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0L, root.ReservedBytes); Assert.Equal(new[] { 2, 1, 2 }, result.SourcePages);
    }
}
