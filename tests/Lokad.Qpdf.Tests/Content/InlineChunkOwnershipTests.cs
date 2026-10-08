using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Filters;

namespace Lokad.Qpdf.Tests.Content;

public sealed class InlineChunkOwnershipTests
{
    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries) => DecoderOwnershipFixtures.Dictionary(entries);

    [Theory]
    [InlineData("missing-reference")]
    [InlineData("bad-array")]
    [InlineData("mixed-chain")]
    [InlineData("bad-params")]
    [InlineData("unknown-binary-name")]
    public void MalformedAndUnsupportedPlansKeepBorrowingTheExactRawPayload(string shape)
    {
        QpdfObject filter = shape switch
        { "missing-reference" => new PdfReference(7, 0), "bad-array" => new PdfArray([new PdfInteger("1", 1L)]),
            "mixed-chain" => new PdfArray([PdfName.FromAscii("ASCIIHexDecode"), PdfName.FromAscii("DCTDecode")]),
            "unknown-binary-name" => new PdfName([0, 255, (byte)'x']), _ => PdfName.FromAscii("ASCIIHexDecode") };
        PdfDictionary header = shape == "bad-params" ? Dictionary(("Filter", filter), ("DecodeParms", new PdfInteger("1", 1L))) : Dictionary(("Filter", filter));
        PdfStream stream = new(header, "ABCG>"u8.ToArray()); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, CancellationToken.None);
        Assert.True(chunks.TryAddForInlineImages(stream, new(new Dictionary<int, QpdfObject>()), QpdfLimits.Default));
        Assert.Same(stream.AllocationCarrier, Assert.Single(chunks.Parts)); Assert.Equal(208L, governor.Snapshot().LiveMemoryBytes);
        chunks.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal("ABCG>"u8.ToArray(), stream.Data);
    }

    [Fact]
    public void SupportedDecodeFailureKeepsEarlierChunksFundedUntilOwnerDisposal()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using AnalysisChunks chunks = AnalysisChunks.Create(governor, CancellationToken.None);
        PoolResolver resolver = new(new Dictionary<int, QpdfObject>());
        PdfStream first = new(Dictionary(("Filter", PdfName.FromAscii("ASCIIHexDecode"))), "6669727374>"u8.ToArray());
        Assert.True(chunks.TryAddForInlineImages(first, resolver, QpdfLimits.Default)); long live = governor.Snapshot().LiveMemoryBytes;
        PdfStream bad = new(Dictionary(("Filter", PdfName.FromAscii("ASCIIHexDecode"))), "ABCG>"u8.ToArray());
        Assert.False(chunks.TryAddForInlineImages(bad, resolver, QpdfLimits.Default));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal("first"u8.ToArray(), Assert.Single(chunks.Parts));
        chunks.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class CancellingPool : IReadOnlyDictionary<int, QpdfObject>
    {
        private readonly CancellationTokenSource _source;
        internal int Visits;
        internal CancellingPool(CancellationTokenSource source) { _source = source; }
        public bool TryGetValue(int key, out QpdfObject value) { Visits++; _source.Cancel(); value = PdfName.FromAscii("ASCIIHexDecode"); return true; }
        public int Count => 1;
        public QpdfObject this[int key] => throw new NotSupportedException();
        public IEnumerable<int> Keys => throw new NotSupportedException();
        public IEnumerable<QpdfObject> Values => throw new NotSupportedException();
        public bool ContainsKey(int key) => throw new NotSupportedException();
        public IEnumerator<KeyValuePair<int, QpdfObject>> GetEnumerator() => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringIndirectFilterResolutionPreservesPriorChunks(bool governorToken)
    {
        using CancellationTokenSource source = new(); CancellingPool pool = new(source);
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        using AnalysisChunks chunks = AnalysisChunks.Create(governor, governorToken ? CancellationToken.None : source.Token);
        PoolResolver resolver = new(pool); PdfStream first = new(Dictionary(), "first"u8.ToArray());
        Assert.True(chunks.TryAddForInlineImages(first, resolver, QpdfLimits.Default)); long live = governor.Snapshot().LiveMemoryBytes;
        PdfStream indirect = new(Dictionary(("Filter", new PdfReference(7, 0))), "AB>"u8.ToArray());
        Assert.Throws<OperationCanceledException>(() => chunks.TryAddForInlineImages(indirect, resolver, QpdfLimits.Default));
        Assert.Equal(1, pool.Visits); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Same(first.AllocationCarrier, Assert.Single(chunks.Parts)); chunks.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private static object Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(owner)
        ?? throw new InvalidOperationException(name);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (AnalysisChunks Owner, PdfStream Borrowed, WeakReference[] Witnesses) Complete()
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, source.Token);
        AnalysisChunks owner = AnalysisChunks.Create(governor, source.Token); PoolResolver resolver = new(new Dictionary<int, QpdfObject>());
        PdfStream borrowed = new(Dictionary(), new byte[32768]); Assert.True(owner.TryAddForInlineImages(borrowed, resolver, QpdfLimits.Default));
        PdfStream encoded = new(Dictionary(("Filter", PdfName.FromAscii("ASCIIHexDecode"))), "6669727374>"u8.ToArray());
        Assert.True(owner.TryAddForInlineImages(encoded, resolver, QpdfLimits.Default));
        WeakReference[] witnesses = [new(governor), new(source), new(Field(owner, "_memory")), new(Field(owner, "_parts")), new(owner.Parts[1])];
        owner.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); return (owner, borrowed, witnesses);
    }
    [Fact]
    public void RootedDisposedInlineChunksDropTheirExclusiveStorageAndContext()
    {
        var completed = Complete(); for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(completed.Witnesses, witness => Assert.False(witness.IsAlive)); Assert.Equal(32768, completed.Borrowed.Data.Length);
        Assert.Throws<ObjectDisposedException>(() => completed.Owner.Parts); GC.KeepAlive(completed);
    }
}
