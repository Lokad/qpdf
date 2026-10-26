using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class QdfStorageTests
{
    [Fact]
    public async Task LargeMemberDoesNotDuplicateTheOwnedInput()
    {
        byte[] input = QdfRepairFixtures.ObjectStream(3, 1024 * 1024);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            memory.Reserve(input.Length + 32L); CountingSink sink = new();
            QdfRepair repair = QdfRepair.Create(governor, sink, memory);
            long allocated = GC.GetAllocatedBytesForCurrentThread(); await repair.RepairAsync(input, CancellationToken.None);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.True(allocated < 131072L, $"Repair allocated {allocated} bytes beyond its already owned input.");
            Assert.True(governor.Snapshot().PeakMemoryBytes < input.Length + 262144L);
            Assert.True(sink.Length > input.Length);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal((byte)'%', input[0]);
    }

    [Fact]
    public async Task MemberTableFitsSevenMiBWithoutUnusedStorage()
    {
        byte[] input = QdfRepairFixtures.ObjectStream(20000, 0);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 7L * 1024L * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            memory.Reserve(input.Length + 32L); CountingSink sink = new();
            QdfRepair repair = QdfRepair.Create(governor, sink, memory);
            await repair.RepairAsync(input, CancellationToken.None);
            Assert.True(sink.Length > input.Length); Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class MemoryHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }

    private static async Task Seed(InMemoryQpdfHost storage)
    {
        await using IQpdfOutputTransaction seed = await storage.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await seed.WriteAsync(new byte[] { 17, 23 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllocationRefusalRefundsWorkingStorageAndPreservesPriorOutput(bool shared)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), QdfRepairFixtures.ObjectStream(20000, 0), QpdfInputOwnership.Transfer); await Seed(storage);
        RecordingMemoryBudget root = new(33554432L) { RefuseOnGrowth = shared ? 8 : 0 };
        using IMemoryScope caller = root.CreateScope(); MemoryHost host = new(storage, caller);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = shared ? 33554432L : 2097152L };
        QpdfExecutionResult result = await Qpdf.FixQdfAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), limits, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.Equal(new byte[] { 17, 23 }, prior);
        Assert.Equal(0L, storage.OpenLeaseCount); caller.Dispose(); Assert.Equal(0L, root.ReservedBytes);
    }

    private sealed class RetainingHost(InMemoryQpdfHost storage, IMemoryBudget caller, IMemoryBudget durable,
        string fault, CancellationTokenSource cancellation) : IQpdfHost, IQpdfMemoryHost, IDisposable
    {
        private readonly Dictionary<byte[], IMemoryReservation> _owners = new();
        internal readonly List<ReadOnlyMemory<byte>> Chunks = [];
        internal readonly List<string> Hashes = [];
        public IMemoryBudget MemoryBudget => caller;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Output(this, await storage.CreateOutputAsync(path, token));
        private void Retain(ReadOnlyMemory<byte> data)
        {
            Assert.True(MemoryMarshal.TryGetArray(data, out ArraySegment<byte> backing)); Assert.NotNull(backing.Array);
            if (!_owners.ContainsKey(backing.Array))
            {
                if (!durable.TryReserve(backing.Array.LongLength + 32L, out IMemoryReservation? receipt))
                { throw new QpdfMemoryAdmissionException(backing.Array.LongLength + 32L); }
                _owners.Add(backing.Array, receipt);
            }
            Chunks.Add(data); Hashes.Add(Convert.ToHexString(SHA256.HashData(data.Span)));
        }
        private void BeforeWrite(ReadOnlyMemory<byte> data, QpdfPath path)
        {
            if (data.Length <= 1024) { return; }
            if (fault == "write") { throw new QpdfHostException(path, QpdfHostFailureReason.WriteFailed); }
            if (fault == "oom") { throw new OutOfMemoryException("injected QDF write"); }
            if (fault == "cancel") { cancellation.Cancel(); }
        }
        private void BeforeCommit(QpdfPath path)
        { if (fault == "commit") { throw new QpdfHostException(path, QpdfHostFailureReason.CommitFailed); } }
        private void AfterClose(QpdfPath path)
        { if (fault == "close") { throw new QpdfHostException(path, QpdfHostFailureReason.WriteFailed); } }
        public void Dispose()
        {
            foreach (IMemoryReservation receipt in _owners.Values) { receipt.Dispose(); }
            _owners.Clear(); Chunks.Clear(); Hashes.Clear();
        }
        private sealed class Output(RetainingHost owner, IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
            {
                owner.Retain(data); await Task.Yield(); owner.BeforeWrite(data, Path); await inner.WriteAsync(data, token);
            }
            public ValueTask CommitAsync(CancellationToken token) { owner.BeforeCommit(Path); return inner.CommitAsync(token); }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); owner.AfterClose(Path); }
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("write")]
    [InlineData("commit")]
    [InlineData("close")]
    [InlineData("cancel")]
    [InlineData("oom")]
    public async Task RetainedAsyncPacketsStayImmutableAndFundedThroughCleanup(string fault)
    {
        RecordingMemoryBudget root = new(33554432L); using IMemoryScope durable = root.CreateScope(); using IMemoryScope caller = root.CreateScope();
        using InMemoryQpdfHost storage = new(QpdfLimits.Default, null, durable);
        byte[] input = QdfRepairFixtures.ObjectStream(3, 131072);
        storage.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy); await Seed(storage);
        using CancellationTokenSource cancellation = new(); using RetainingHost host = new(storage, caller, durable, fault, cancellation);
        QpdfExecutionResult? result = null;
        if (fault == "oom")
        { await Assert.ThrowsAsync<OutOfMemoryException>(async () => await Qpdf.FixQdfAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), cancellation.Token)); }
        else
        {
            result = await Qpdf.FixQdfAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), cancellation.Token);
            Assert.Equal(fault == "none" ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
            if (fault == "cancel") { Assert.Equal(QpdfExecutionStatus.Canceled, result.Status); }
        }
        Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        if (fault is "none" or "close")
        {
            Assert.True(output.Length > 131072);
            if (fault == "none") { Assert.Equal(output, host.Chunks.SelectMany(c => c.ToArray()).ToArray()); }
        }
        else { Assert.Equal(new byte[] { 17, 23 }, output); }
        Array.Fill(input, (byte)0); caller.Dispose(); storage.Dispose();
        Assert.NotEmpty(host.Chunks); Assert.True(root.ReservedBytes > 0L);
        for (int i = 0; i < host.Chunks.Count; i++)
        { Assert.Equal(host.Hashes[i], Convert.ToHexString(SHA256.HashData(host.Chunks[i].Span))); }
        host.Dispose(); durable.Dispose(); Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0, root.LiveReservations);
    }
}
