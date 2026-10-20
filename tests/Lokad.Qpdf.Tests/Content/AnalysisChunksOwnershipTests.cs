using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Filters;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AnalysisChunksOwnershipTests
{
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
        Assert.Same(unfiltered.AllocationCarrier, chunks.Parts[0]);
        foreach (string filter in new[] { "FlateDecode", "ASCIIHexDecode", "ASCII85Decode", "LZWDecode", "RunLengthDecode" })
        {
            PdfStream stream = Stream(filter, raw); Assert.True(chunks.TryAdd(stream, resolver, QpdfLimits.Default));
            Assert.NotSame(stream.AllocationCarrier, chunks.Parts[^1]); Assert.Equal(raw, chunks.Parts[^1]);
        }
        Assert.Equal(240L + 5L * (raw.Length + 32L), governor.Snapshot().LiveMemoryBytes);
        using JoinedContent joined = JoinedContent.Create(chunks.Parts, governor, CancellationToken.None);
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
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Single(chunks.Parts); Assert.Equal("first"u8.ToArray(), chunks.Parts[0]);
        chunks.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void FailedListGrowthPreservesPriorChunksAndCanRetry()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 500L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(17L);
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, CancellationToken.None); PoolResolver resolver = Resolver();
        PdfStream stream = Stream("", "borrowed"u8.ToArray());
        for (int index = 0; index < 4; index++) { Assert.True(chunks.TryAdd(stream, resolver, limits)); }
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => chunks.TryAdd(stream, resolver, limits));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(4, chunks.Parts.Count);
        prior.Release(17L); Assert.True(chunks.TryAdd(stream, resolver, limits)); Assert.Equal(5, chunks.Parts.Count);
        Assert.All(chunks.Parts, part => Assert.Same(stream.AllocationCarrier, part));
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
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal("first"u8.ToArray(), chunks.Parts[0]);
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
        WeakReference[] witnesses = [new(governor), new(source), new(Field(chunks, "_memory")), new(Field(chunks, "_parts")), new(chunks.Parts[1])];
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
