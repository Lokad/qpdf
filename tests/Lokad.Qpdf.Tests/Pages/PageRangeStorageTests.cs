using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Pages;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Pages;

public sealed class PageRangeStorageTests
{
    [Theory]
    [InlineData("1,x2-z", 100000, 1)]
    [InlineData("1,xz-2", 100000, 1)]
    [InlineData("1,x1-z", 100000, 0)]
    [InlineData("1,x2-z", int.MaxValue, 1)]
    public void ExclusionsUseSelectedStorageRatherThanEnumeratingTheSpan(string range, int total, int count)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 65536L }, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            IReadOnlyList<int> selected = PageRanges.Expand(range, total, long.MaxValue, governor, memory);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 32768L);
            Assert.Equal(count, selected.Count); if (count != 0) { Assert.Equal(1, selected[0]); }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ExclusionWidthDoesNotConsumeTheSelectedEntryLimit()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        Assert.Equal(new[] { 1 }, PageRanges.Expand("1,x2-z", 100000, 1L, governor, memory));
        Assert.Equal(new[] { 10, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, PageRanges.Expand("1-10,x1-9,2-10", 10, 10L, governor, memory));
        Assert.Throws<QpdfLimitException>(() => PageRanges.Expand("1-10,x1-9,1-10", 10, 10L, governor, memory));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageRanges.Expand("1,x2-100001", 100000, 1L, governor, memory));
    }

    [Theory]
    [InlineData("1-z:odd", 1, 99999)]
    [InlineData("z-1:even", 99999, 1)]
    public void IncludedSpansAndPositionFiltersFitOneAdmittedList(string range, int first, int last)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 1048576L }, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            IReadOnlyList<int> selected = PageRanges.Expand(range, 100000, 100000L, governor, memory);
            Assert.Equal(50000, selected.Count); Assert.Equal(first, selected[0]); Assert.Equal(last, selected[^1]);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class Host(IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class CancelingBudget(IMemoryBudget budget, CancellationTokenSource cancellation) : IMemoryBudget
    {
        public IMemoryScope CreateScope() => budget.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            if (!budget.TryReserve(bytes, out IMemoryReservation? storage)) { reservation = null; return false; }
            reservation = new Receipt(storage, cancellation); return true;
        }
        private sealed class Receipt(IMemoryReservation storage, CancellationTokenSource cancellation) : IMemoryReservation
        {
            public long Bytes => storage.Bytes;
            public bool TryResize(long bytes)
            {
                long delta = bytes - storage.Bytes; bool accepted = storage.TryResize(bytes);
                if (accepted && delta >= 32768L) { cancellation.Cancel(); }
                return accepted;
            }
            public void Dispose() => storage.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedGrowthRefundsOnlyRangeStorage(bool cancel)
    {
        using CancellationTokenSource cancellation = new(); RecordingMemoryBudget root = new(4194304L) { RefuseOnGrowth = cancel ? 0 : 3 };
        using IMemoryScope caller = root.CreateScope();
        ResourceGovernor governor = new(QpdfLimits.Default with { CancellationCheckInterval = 1L }, cancellation.Token,
            new Host(new CancelingBudget(caller, cancellation)));
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            if (cancel) { Assert.ThrowsAny<OperationCanceledException>(() => PageRanges.Expand("1-z,x2-z:odd", 100000, 100000L, governor, memory)); }
            else { Assert.Throws<QpdfMemoryAdmissionException>(() => PageRanges.Expand("1-z,x2-z:odd", 100000, 100000L, governor, memory)); }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task EquivalentPublicRangesKeepTheSameOutputAndBoundedStorage()
    {
        const int count = 10000;
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, $"<< /Type /Pages /Count {count} /Kids [" + string.Join(' ', Enumerable.Range(3, count).Select(id => $"{id} 0 R")) + "] >>", null)];
        for (int index = 0; index < count; index++) { objects.Add((index + 3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null)); }
        byte[] input = CheckTests.Assemble(objects, 1); byte[]? expected = null; long peak = 0L;
        foreach (string range in new[] { "1", "1,x2-z" })
        {
            using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy);
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", range, "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            if (expected is null) { expected = output; peak = result.Counters.PeakMemoryBytes; }
            else { Assert.Equal(expected, output); Assert.InRange(result.Counters.PeakMemoryBytes, peak - 65536L, peak + 65536L); }
        }
    }
}
