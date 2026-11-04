using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Filters;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AnalysisChunksOwnershipTests
{
    [Fact]
    public void DecodedPayloadTransferRetiresDecoderScratchAndMovesASolePayload()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, CancellationToken.None);
        byte[] bytes = "decoded program"u8.ToArray();
        using (ParserMemoryScope decoder = ParserMemoryScope.Create(governor))
        {
            decoder.Reserve(checked(32L + bytes.Length + 17L));
            chunks.Add(bytes, decoder); Assert.Equal(17L, decoder.ReservedBytes);
        }
        using JoinedContent joined = chunks.Join(); Assert.Same(bytes, joined.RawBytes);
        Assert.Equal(bytes.Length + 128L, governor.Snapshot().LiveMemoryBytes);
        joined.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void RefusedDecodedListGrowthRollsPayloadBackToTheDecoderAndCanRetry()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 2048L }, CancellationToken.None);
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, CancellationToken.None);
        byte[] borrowed = "borrowed"u8.ToArray();
        for (int index = 0; index < 8; index++)
        { using ParserMemoryScope empty = ParserMemoryScope.Create(governor); chunks.Add(borrowed, empty); }
        using ParserMemoryScope decoder = ParserMemoryScope.Create(governor);
        byte[] owned = "owned"u8.ToArray(); long allocation = owned.Length + 32L; decoder.Reserve(allocation);
        using ParserMemoryScope pressure = ParserMemoryScope.Create(governor); pressure.Reserve(governor.RemainingMemoryBytes);
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => chunks.Add(owned, decoder));
        Assert.Equal(allocation, decoder.ReservedBytes); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(8, chunks.Parts.Count); Assert.All(chunks.Parts, part => Assert.True(part.Equals(borrowed.AsMemory())));
        pressure.Dispose(); chunks.Add(owned, decoder); Assert.Equal(0L, decoder.ReservedBytes);
        Assert.True(chunks.Parts[8].Equals(owned.AsMemory())); chunks.Dispose(); decoder.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanceledTransferKeepsTheDecoderOwnershipUntilCleanup(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, governorToken ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope decoder = ParserMemoryScope.Create(governor);
        byte[] bytes = "owned"u8.ToArray(); long allocation = bytes.Length + 32L; decoder.Reserve(allocation);
        long live = governor.Snapshot().LiveMemoryBytes; cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => chunks.Add(bytes, decoder));
        Assert.Equal(allocation, decoder.ReservedBytes); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Empty(chunks.Parts); chunks.Dispose(); decoder.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private static PdfStream Stream(string filter, byte[] raw)
        => new(filter == "" ? DecoderOwnershipFixtures.Dictionary() : DecoderOwnershipFixtures.Dictionary(("Filter", PdfName.FromAscii(filter))),
            DecoderOwnershipFixtures.Encode(filter, raw));
    private static PoolResolver Resolver() => new(new Dictionary<int, QpdfObject>());

    [Theory]
    [InlineData(63L)]
    [InlineData(111L)]
    [InlineData(143L)]
    public void PartialConstructionReleasesOnlyItsAdmission(long maximum)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = maximum }, CancellationToken.None);
        Assert.Throws<QpdfLimitException>(() => AnalysisChunks.Create(governor, CancellationToken.None));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void RawStorageIsBorrowedAndFilteredResultsStayOwnedThroughJoining()
    {
        byte[] raw = "/Used 12 Tf"u8.ToArray(); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, CancellationToken.None); PoolResolver resolver = Resolver();
        PdfStream unfiltered = Stream("", raw); Assert.True(chunks.TryAdd(unfiltered, resolver, QpdfLimits.Default));
        Assert.True(unfiltered.RawMemory.Equals(chunks.Parts[0]));
        foreach (string filter in new[] { "FlateDecode", "ASCIIHexDecode", "ASCII85Decode", "LZWDecode", "RunLengthDecode" })
        {
            PdfStream stream = Stream(filter, raw); Assert.True(chunks.TryAdd(stream, resolver, QpdfLimits.Default));
            Assert.False(stream.RawMemory.Equals(chunks.Parts[^1])); Assert.Equal(raw, chunks.Parts[^1].ToArray());
        }
        Assert.Equal(312L + 5L * (raw.Length + 32L), governor.Snapshot().LiveMemoryBytes);
        using JoinedContent joined = chunks.Join();
        chunks.Dispose(); Assert.Equal(joined.Bytes.Length + 128L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(string.Join('\n', Enumerable.Repeat("/Used 12 Tf", 6)), System.Text.Encoding.ASCII.GetString(joined.Bytes.Span));
        joined.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(raw, unfiltered.Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableNextChunkPreservesPreviouslyDecodedChunks(bool unsupported)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using AnalysisChunks chunks = AnalysisChunks.Create(governor, CancellationToken.None);
        PoolResolver resolver = Resolver(); Assert.True(chunks.TryAdd(Stream("FlateDecode", "first"u8.ToArray()), resolver, QpdfLimits.Default));
        long live = governor.Snapshot().LiveMemoryBytes;
        PdfStream bad = new(DecoderOwnershipFixtures.Dictionary(("Filter", PdfName.FromAscii(unsupported ? "DCTDecode" : "ASCIIHexDecode"))), "ABCG>"u8.ToArray());
        Assert.False(chunks.TryAdd(bad, resolver, QpdfLimits.Default));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Single(chunks.Parts); Assert.Equal("first"u8.ToArray(), chunks.Parts[0].ToArray());
        chunks.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void FailedListGrowthPreservesPriorChunksAndCanRetry()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 2048L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(17L);
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, CancellationToken.None); PoolResolver resolver = Resolver();
        PdfStream stream = Stream("", "borrowed"u8.ToArray());
        for (int index = 0; index < 8; index++) { Assert.True(chunks.TryAdd(stream, resolver, limits)); }
        using ParserMemoryScope pressure = ParserMemoryScope.Create(governor); pressure.Reserve(governor.RemainingMemoryBytes);
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => chunks.TryAdd(stream, resolver, limits));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(8, chunks.Parts.Count);
        pressure.Dispose(); prior.Release(17L); Assert.True(chunks.TryAdd(stream, resolver, limits)); Assert.Equal(9, chunks.Parts.Count);
        Assert.All(chunks.Parts, part => Assert.True(stream.RawMemory.Equals(part)));
        chunks.Dispose(); Assert.Equal(64L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationPreservesPreviouslyOwnedChunksUntilOwnerDisposal(bool governorToken)
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, governorToken ? CancellationToken.None : source.Token);
        PoolResolver resolver = Resolver(); PdfStream stream = Stream("FlateDecode", "first"u8.ToArray());
        Assert.True(chunks.TryAdd(stream, resolver, QpdfLimits.Default)); long live = governor.Snapshot().LiveMemoryBytes; source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => chunks.TryAdd(stream, resolver, QpdfLimits.Default));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal("first"u8.ToArray(), chunks.Parts[0].ToArray());
        chunks.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value) ?? throw new InvalidOperationException(name);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (AnalysisChunks Owner, PdfStream Borrowed, WeakReference[] Witnesses) Complete()
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, source.Token);
        AnalysisChunks chunks = AnalysisChunks.Create(governor, source.Token); PoolResolver resolver = Resolver();
        PdfStream borrowed = Stream("", new byte[32768]); Assert.True(chunks.TryAdd(borrowed, resolver, QpdfLimits.Default));
        Assert.True(chunks.TryAdd(Stream("FlateDecode", "first"u8.ToArray()), resolver, QpdfLimits.Default));
        Assert.True(MemoryMarshal.TryGetArray(chunks.Parts[1], out ArraySegment<byte> payload));
        WeakReference[] witnesses = [new(governor), new(source), new(Field(chunks, "_memory")), new(Field(chunks, "_parts")), new(payload.Array ?? throw new InvalidOperationException("Missing decoded array"))];
        chunks.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); return (chunks, borrowed, witnesses);
    }
    [Fact]
    public void RootedDisposedChunksDropExclusiveStorageAndExecutionState()
    {
        var completed = Complete();
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(completed.Witnesses, witness => Assert.False(witness.IsAlive)); Assert.Equal(32768, completed.Borrowed.Data.Length);
        Assert.Throws<ObjectDisposedException>(() => completed.Owner.Parts); GC.KeepAlive(completed);
    }
}
