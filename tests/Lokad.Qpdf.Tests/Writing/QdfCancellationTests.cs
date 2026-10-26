using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class QdfCancellationTests
{
    public static IEnumerable<object[]> Scans()
    {
        foreach (string method in new[] { "IndexOfToken", "TryParseObjectHeader", "MatchesMember", "MatchesSizeLine" })
        foreach (bool governorToken in new[] { false, true }) { yield return [method, governorToken]; }
    }

    [Theory]
    [MemberData(nameof(Scans))]
    public void ScanEntryChecksBothTokensBeforeReadingLongInput(string method, bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        byte[] input = new byte[65536]; memory.Reserve(input.Length + 32L);
        QdfRepair repair = QdfRepair.Create(governor, new CountingSink(), memory);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        (typeof(QdfRepair).GetField("_input", flags) ?? throw new InvalidOperationException()).SetValue(repair, input);
        cancellation.Cancel(); CancellationToken token = governorToken ? CancellationToken.None : cancellation.Token;
        object?[] arguments = method switch
        {
            "IndexOfToken" => [0, input.Length, "/Type /ObjStm"u8.ToArray(), token],
            "MatchesSizeLine" => [0, input.Length, token],
            _ => [0, input.Length, token, 0],
        };
        long before = governor.Snapshot().LiveMemoryBytes;
        MethodInfo target = typeof(QdfRepair).GetMethod(method, flags) ?? throw new InvalidOperationException();
        TargetInvocationException failure = Assert.Throws<TargetInvocationException>(() => target.Invoke(repair, arguments));
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class CancelingGrowth(IMemoryBudget inner, CancellationTokenSource cancellation) : IMemoryBudget
    {
        internal long AllocatedAtCancellation;
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            if (!inner.TryReserve(bytes, out IMemoryReservation? storage)) { reservation = null; return false; }
            reservation = new Receipt(this, storage); return true;
        }
        private void Observe(long increase)
        {
            if (increase >= 2500000L && !cancellation.IsCancellationRequested)
            { cancellation.Cancel(); AllocatedAtCancellation = GC.GetAllocatedBytesForCurrentThread(); }
        }
        private sealed class Receipt(CancelingGrowth owner, IMemoryReservation storage) : IMemoryReservation
        {
            public long Bytes => storage.Bytes;
            public bool TryResize(long bytes)
            {
                long increase = bytes - storage.Bytes; bool accepted = storage.TryResize(bytes);
                if (accepted) { owner.Observe(increase); } return accepted;
            }
            public void Dispose() => storage.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAtTableAdmissionPrecedesMaterializationAndRefunds(bool governorToken)
    {
        byte[] input = QdfRepairFixtures.ObjectStream(20000, 0);
        using CancellationTokenSource cancellation = new(); RecordingMemoryBudget root = new(33554432L);
        CancelingGrowth budget = new(root, cancellation);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None, host);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            memory.Reserve(input.Length + 32L); QdfRepair repair = QdfRepair.Create(governor, new CountingSink(), memory);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await repair.RepairAsync(input,
                governorToken ? CancellationToken.None : cancellation.Token));
            Assert.True(cancellation.IsCancellationRequested); Assert.True(budget.AllocatedAtCancellation > 0L);
            long after = GC.GetAllocatedBytesForCurrentThread() - budget.AllocatedAtCancellation;
            Assert.True(after < 100000L, $"Canceled preparation allocated {after} bytes after admission.");
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }
}
