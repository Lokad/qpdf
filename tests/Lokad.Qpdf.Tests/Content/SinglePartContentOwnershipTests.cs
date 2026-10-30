using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Filters;

namespace Lokad.Qpdf.Tests.Content;

public sealed class SinglePartContentOwnershipTests
{
    private static PoolResolver Resolver() => new(new Dictionary<int, QpdfObject>());
    private static PdfStream Stream(string kind, byte[] payload)
    {
        bool decoded = kind.StartsWith("decoded", StringComparison.Ordinal);
        PdfDictionary header = decoded ? DecoderOwnershipFixtures.Dictionary(("Filter", PdfName.FromAscii("FlateDecode")))
            : DecoderOwnershipFixtures.Dictionary();
        byte[] bytes = decoded ? DecoderOwnershipFixtures.Encode("FlateDecode", payload) : payload;
        if (!kind.EndsWith("slice", StringComparison.Ordinal)) { return PdfStream.FromOwnedData(header, bytes); }
        byte[] backing = new byte[bytes.Length + 16]; bytes.CopyTo(backing, 8);
        return PdfStream.FromImmutableSlice(header, backing.AsMemory(8, bytes.Length));
    }

    [Theory]
    [InlineData("slice")]
    [InlineData("decoded")]
    [InlineData("decoded-slice")]
    public void OwnedPartMovesWithoutASecondPayloadAllocation(string kind)
    {
        byte[] payload = Enumerable.Repeat((byte)91, 65536).ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, CancellationToken.None);
        Assert.True(chunks.TryAdd(Stream(kind, payload), Resolver(), QpdfLimits.Default));
        byte[] part = Assert.Single(chunks.Parts);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        using JoinedContent joined = chunks.Join();
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < payload.Length / 2);
        Assert.Same(part, joined.RawBytes); Assert.Equal(payload, joined.RawBytes);
        Assert.Throws<ObjectDisposedException>(() => chunks.Parts);
        Assert.Equal(baseline + payload.Length + 128L, governor.Snapshot().LiveMemoryBytes);
        joined.Dispose(); chunks.Dispose(); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void BorrowedPartStillGetsIndependentOwnedStorage()
    {
        byte[] input = Enumerable.Repeat((byte)91, 1024).ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, CancellationToken.None);
        Assert.True(chunks.TryAdd(Stream("borrowed", input), Resolver(), QpdfLimits.Default));
        Assert.Same(input, Assert.Single(chunks.Parts));
        using JoinedContent joined = chunks.Join();
        Assert.NotSame(input, joined.RawBytes); input[0] = 0; Assert.Equal(91, joined.RawBytes[0]);
        Assert.Throws<ObjectDisposedException>(() => chunks.Parts);
        joined.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyJoinClosesTheChunkOwnerWithoutAllocatingPayloadStorage(bool encodedPart)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, CancellationToken.None);
        if (encodedPart) { Assert.True(chunks.TryAdd(Stream("decoded", []), Resolver(), QpdfLimits.Default)); }
        using JoinedContent joined = chunks.Join(); Assert.Empty(joined.RawBytes);
        Assert.Throws<ObjectDisposedException>(() => chunks.Join()); Assert.Equal(96L, governor.Snapshot().LiveMemoryBytes);
        joined.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("borrowed")]
    [InlineData("slice")]
    [InlineData("decoded")]
    public void EmptyAndMultiplePartsKeepTheirExactJoiningRules(string kind)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, CancellationToken.None);
        foreach (byte[] part in new[] { "a\n"u8.ToArray(), Array.Empty<byte>(), "b"u8.ToArray() })
        { Assert.True(chunks.TryAdd(Stream(kind, part), Resolver(), QpdfLimits.Default)); }
        using JoinedContent joined = chunks.Join();
        Assert.Equal("a\n\nb"u8.ToArray(), joined.RawBytes);
        Assert.Throws<ObjectDisposedException>(() => chunks.Parts);
        joined.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JoinAdmissionRefusalPreservesTheSourceAndCanRetry(bool shared)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = shared ? QpdfLimits.Default.MaxMemoryBytes : 16384L };
        RecordingMemoryBudget root = new(16384L);
        using IMemoryScope caller = root.CreateScope(); using InMemoryQpdfHost host = new(limits, null, caller);
        ResourceGovernor governor = shared ? new(limits, CancellationToken.None, host) : new(limits, CancellationToken.None);
        using (AnalysisChunks chunks = AnalysisChunks.Create(governor, CancellationToken.None))
        {
            Assert.True(chunks.TryAdd(Stream("slice", new byte[1024]), Resolver(), limits)); byte[] part = chunks.Parts[0];
            using (ParserMemoryScope pressure = ParserMemoryScope.Create(governor))
            {
                pressure.Reserve(16384L - governor.Snapshot().LiveMemoryBytes - 80L);
                long before = governor.Snapshot().LiveMemoryBytes;
                if (shared) { Assert.Throws<QpdfMemoryAdmissionException>(() => chunks.Join()); }
                else { Assert.Throws<QpdfLimitException>(() => chunks.Join()); }
                Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Same(part, Assert.Single(chunks.Parts));
            }
            using JoinedContent joined = chunks.Join(); Assert.Same(part, joined.RawBytes);
        }
        governor.CompletePrivateStorage(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }

    private sealed class ObservedBudget(IMemoryBudget inner, Action observe) : IMemoryBudget
    {
        internal bool Armed { get; set; }
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            if (!inner.TryReserve(bytes, out IMemoryReservation? admitted)) { reservation = null; return false; }
            reservation = new Reservation(admitted, this); return true;
        }
        private sealed class Reservation(IMemoryReservation inner, ObservedBudget owner) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes)
            {
                long before = Bytes; bool accepted = inner.TryResize(bytes);
                if (accepted && bytes > before && owner.Armed) { owner.Armed = false; owner.Observe(); }
                return accepted;
            }
            public void Dispose() => inner.Dispose();
        }
        private void Observe() => observe();
    }

    [Fact]
    public void FailedListGrowthRefundsANewRawSliceAndPreservesEarlierParts()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L };
        ResourceGovernor governor = new(limits, CancellationToken.None); PoolResolver resolver = Resolver();
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, CancellationToken.None);
        PdfStream borrowed = Stream("borrowed", "q"u8.ToArray());
        for (int index = 0; index < 512; index++) { Assert.True(chunks.TryAdd(borrowed, resolver, limits)); }
        PdfStream slice = Stream("slice", Enumerable.Repeat((byte)91, 2048).ToArray());
        using (ParserMemoryScope pressure = ParserMemoryScope.Create(governor))
        {
            pressure.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - 2080L - 4096L);
            long before = governor.Snapshot().LiveMemoryBytes;
            Assert.Throws<QpdfLimitException>(() => chunks.TryAdd(slice, resolver, limits));
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(512, chunks.Parts.Count);
            Assert.All(chunks.Parts, part => Assert.Same(borrowed.AllocationCarrier, part));
        }
        Assert.True(chunks.TryAdd(slice, resolver, limits)); Assert.Equal(513, chunks.Parts.Count);
        Assert.Equal(2048, chunks.Parts[^1].Length);
        chunks.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterJoinAdmissionLeavesThePartFunded(bool governorToken)
    {
        using CancellationTokenSource cancellation = new(); RecordingMemoryBudget root = new(32768L);
        using IMemoryScope caller = root.CreateScope(); ObservedBudget budget = new(caller, cancellation.Cancel);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None, host);
        using (AnalysisChunks chunks = AnalysisChunks.Create(governor, governorToken ? CancellationToken.None : cancellation.Token))
        {
            Assert.True(chunks.TryAdd(Stream("slice", new byte[1024]), Resolver(), QpdfLimits.Default)); byte[] part = chunks.Parts[0];
            using ParserMemoryScope pressure = ParserMemoryScope.Create(governor);
            pressure.Reserve(16384L - governor.Snapshot().LiveMemoryBytes - 32L);
            long before = governor.Snapshot().LiveMemoryBytes; budget.Armed = true;
            Assert.ThrowsAny<OperationCanceledException>(() => chunks.Join()); Assert.True(cancellation.IsCancellationRequested);
            Assert.Same(part, Assert.Single(chunks.Parts)); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        }
        governor.CompletePrivateStorage(); Assert.Equal(0L, root.ReservedBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (byte[] Bytes, WeakReference Source, WeakReference Governor, WeakReference Chunks) Escaped()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] backing = new byte[1048576]; backing[17] = 91;
        PdfStream stream = PdfStream.FromImmutableSlice(DecoderOwnershipFixtures.Dictionary(), backing.AsMemory(16, 32));
        using ParserMemoryScope result = ParserMemoryScope.Create(governor);
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, CancellationToken.None);
        Assert.True(chunks.TryAdd(stream, Resolver(), QpdfLimits.Default));
        using JoinedContent joined = chunks.Join(); byte[] part = joined.RawBytes;
        byte[] bytes = joined.TakeBytes(result); Assert.Same(part, bytes);
        joined.Dispose(); chunks.Dispose(); result.Dispose(); governor.CompletePrivateStorage();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        return (bytes, new(backing), new(governor), new(chunks));
    }

    [Fact]
    public void EscapedBytesDropTheLargerSourceAndCompletedExecution()
    {
        var escaped = Escaped(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.Equal(91, escaped.Bytes[1]); Assert.False(escaped.Source.IsAlive); Assert.False(escaped.Governor.IsAlive);
        Assert.False(escaped.Chunks.IsAlive); GC.KeepAlive(escaped.Bytes);
    }
}
