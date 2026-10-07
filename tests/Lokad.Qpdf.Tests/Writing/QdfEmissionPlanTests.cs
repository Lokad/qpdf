using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(WriterOrderCollection))]
public sealed class QdfEmissionPlanTests
{
    internal static WriterOrder<NumberedObject> Source(ResourceGovernor governor, params NumberedObject[] values)
    {
        WriterOrder<NumberedObject> source = WriterOrder<NumberedObject>.Create(values.Length, 16L, governor, CancellationToken.None);
        foreach (NumberedObject value in values) { source.Add(value); }
        return source;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthoredRowsPreserveNumbersOriginalIdsLengthsAndBorrowedStreamBytes(bool hasLength)
    {
        Dictionary<PdfName,QpdfObject> entries = new()
        { [PdfName.FromAscii("Target")] = new PdfArray([new PdfReference(18,7)]) };
        PdfName? lengthKey = null;
        if (hasLength) { lengthKey = PdfName.FromAscii("Length"); entries.Add(lengthKey,new PdfReference(99999,0)); }
        PdfStream stream = new(new PdfDictionary(entries),new byte[] {0,255,10,40});
        ResourceGovernor governor = new(QpdfLimits.Default,CancellationToken.None);
        using WriterOrder<NumberedObject> source = Source(governor,new NumberedObject(8,stream),new NumberedObject(18,new PdfReference(8,9)));
        long prior = governor.Snapshot().LiveMemoryBytes;
        using QdfEmissionPlan plan = QdfEmissionPlan.Create(source,new Dictionary<int,int> {{8,44},{18,55}},governor,CancellationToken.None);
        Assert.Equal(3,plan.Count); Assert.Equal(2,plan.LastLengthNumber);
        Assert.Equal(1,plan[0].Number); Assert.Equal(44,plan[0].OriginalSource);
        Assert.Equal(2,plan[1].Number); Assert.Equal(8,plan[1].LengthFor); Assert.Null(plan[1].Value);
        Assert.Equal(3,plan[2].Number); Assert.Equal(55,plan[2].OriginalSource);
        PdfStream rebuilt = Assert.IsType<PdfStream>(plan[0].Value);
        PdfArray array = Assert.IsType<PdfArray>(Entry(rebuilt.Dictionary,"Target"));
        Assert.Equal(3,Assert.IsType<PdfReference>(array[0]).ObjectNumber);
        Assert.Equal(0,Assert.IsType<PdfReference>(array[0]).Generation);
        Assert.Equal(2,Assert.IsType<PdfReference>(Entry(rebuilt.Dictionary,"Length")).ObjectNumber);
        Assert.Equal(1,Assert.IsType<PdfReference>(plan[2].Value).ObjectNumber);
        Assert.Equal(4L,plan.StreamLength(plan[1]));
        Assert.True(MemoryMarshal.TryGetArray(stream.RawMemory,out ArraySegment<byte> original));
        Assert.True(MemoryMarshal.TryGetArray(rebuilt.RawMemory,out ArraySegment<byte> borrowed));
        Assert.Same(original.Array,borrowed.Array);
        if (lengthKey is not null) { Assert.Contains(rebuilt.Dictionary.Keys,key => ReferenceEquals(key,lengthKey)); }
        Assert.Equal(hasLength ? 2 : 1,stream.Dictionary.Count);
        Assert.Equal(18,Assert.IsType<PdfReference>(Assert.IsType<PdfArray>(Entry(stream.Dictionary,"Target"))[0]).ObjectNumber);
        plan.Dispose(); Assert.Equal(prior,governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(new byte[] {0,255,10,40},rebuilt.Data); GC.KeepAlive(rebuilt); GC.KeepAlive(source);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(513)]
    public void ExactNullRowsAdmitWrapperScopesArrayAndRenumbering(int count)
    {
        ResourceGovernor governor = new(QpdfLimits.Default,CancellationToken.None);
        NumberedObject[] values = Enumerable.Range(1,count).Select(number => new NumberedObject(number,PdfNull.Instance)).ToArray();
        using WriterOrder<NumberedObject> source = Source(governor,values);
        long before = governor.Snapshot().LiveMemoryBytes;
        using QdfEmissionPlan plan = QdfEmissionPlan.Create(source,null,governor,CancellationToken.None);
        long rowBytes = count == 0 ? 128L : 160L+32L*count;
        long indexBytes = WriterIndexBytes(count);
        Assert.Equal(before+128L+rowBytes+indexBytes,governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(count,plan.Count); Assert.Equal(0,plan.LastLengthNumber);
        for (int index=0;index<count;index++) { Assert.Equal(index+1,plan[index].Number); Assert.Same(PdfNull.Instance,plan[index].Value); }
        plan.Dispose(); Assert.Equal(before,governor.Snapshot().LiveMemoryBytes);
    }

    private static long WriterIndexBytes(int count)
    { int capacity=0; if (count>0) {capacity=4;while (capacity<count) {capacity*=2;}} return 272L+(capacity==0 ? 0L : 64L+20L*capacity); }

    private static QpdfObject Entry(PdfDictionary dictionary,string key)
    {Assert.True(dictionary.TryGet(PdfName.FromAscii(key),out QpdfObject? value));return Assert.IsAssignableFrom<QpdfObject>(value);}

    [Fact]
    public void OneByteShortRowsAdmissionPreservesSourceAndPriorOwnersAndRetry()
    {
        QpdfLimits limits = QpdfLimits.Default with {MaxMemoryBytes=4096L};
        ResourceGovernor governor = new(limits,CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using WriterOrder<NumberedObject> source = Source(governor,new NumberedObject(1,PdfNull.Instance),new NumberedObject(2,PdfNull.Instance),new NumberedObject(3,PdfNull.Instance));
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        const long planBytes = 800L;
        filler.Reserve(limits.MaxMemoryBytes-governor.Snapshot().LiveMemoryBytes-planBytes+1L);
        long before=governor.Snapshot().LiveMemoryBytes,allocated=GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure=Assert.Throws<QpdfLimitException>(()=>QdfEmissionPlan.Create(source,null,governor,CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes),failure.LimitName);
        Assert.True(GC.GetAllocatedBytesForCurrentThread()-allocated<32768L);
        Assert.Equal(before,governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L,prior.ReservedBytes); Assert.Equal(3,source.Count);
        filler.Dispose(); using (QdfEmissionPlan retry=QdfEmissionPlan.Create(source,null,governor,CancellationToken.None)) {Assert.Equal(3,retry.Count);}
        Assert.Equal(161L+208L,governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ExhaustedWorkRejectsBeforeLargeRowsAndPreservesBorrowedSource()
    {
        QpdfLimits limits=QpdfLimits.Default with {MaxWork=1000000L}; ResourceGovernor governor=new(limits,CancellationToken.None);
        NumberedObject[] values=Enumerable.Range(1,8192).Select(number=>new NumberedObject(number,PdfNull.Instance)).ToArray();
        using WriterOrder<NumberedObject> source=Source(governor,values);
        governor.ChargeWork(limits.MaxWork-governor.Snapshot().TotalWork);
        long before=governor.Snapshot().LiveMemoryBytes,allocated=GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure=Assert.Throws<QpdfLimitException>(()=>QdfEmissionPlan.Create(source,null,governor,CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxWork),failure.LimitName); Assert.True(GC.GetAllocatedBytesForCurrentThread()-allocated<32768L);
        Assert.Equal(before,governor.Snapshot().LiveMemoryBytes); Assert.Equal(8192,source.Count);
    }

    [Fact]
    public void FailedNestedRemappingReleasesOnlyNewRowsAndMetadata()
    {
        ResourceGovernor governor=new(QpdfLimits.Default,CancellationToken.None);
        using ParserMemoryScope prior=ParserMemoryScope.Create(governor);prior.Reserve(97L);
        PdfArray original=new([new PdfReference(1,0),new PdfReference(999,0)]);
        using WriterOrder<NumberedObject> source=Source(governor,new NumberedObject(1,original));
        long before=governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfException>(()=>QdfEmissionPlan.Create(source,null,governor,CancellationToken.None));
        Assert.Equal(before,governor.Snapshot().LiveMemoryBytes);Assert.Equal(97L,prior.ReservedBytes);
        Assert.Equal(999,Assert.IsType<PdfReference>(original[1]).ObjectNumber);Assert.Same(original,source[0].Value);
    }

    [Fact]
    public void ForeignSourceOwnerRejectsBeforeAdmission()
    {
        ResourceGovernor owner=new(QpdfLimits.Default,CancellationToken.None),other=new(QpdfLimits.Default,CancellationToken.None);
        using WriterOrder<NumberedObject> source=Source(owner,new NumberedObject(1,PdfNull.Instance));
        long live=owner.Snapshot().LiveMemoryBytes;
        Assert.Throws<InvalidOperationException>(()=>QdfEmissionPlan.Create(source,null,other,CancellationToken.None));
        Assert.Equal(live,owner.Snapshot().LiveMemoryBytes);Assert.Equal(0L,other.Snapshot().LiveMemoryBytes);Assert.Equal(0L,other.Snapshot().TotalWork);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherPreCanceledRouteRejectsBeforeAdmission(bool governorToken)
    {
        using CancellationTokenSource cancellation=new();
        ResourceGovernor governor=new(QpdfLimits.Default,governorToken ? cancellation.Token : CancellationToken.None);
        using WriterOrder<NumberedObject> source=Source(governor,new NumberedObject(1,PdfNull.Instance));
        long live=governor.Snapshot().LiveMemoryBytes,work=governor.Snapshot().TotalWork;cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(()=>QdfEmissionPlan.Create(source,null,governor,governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(live,governor.Snapshot().LiveMemoryBytes);Assert.Equal(work,governor.Snapshot().TotalWork);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterActualRowAndIndexWorkPreservesSource(bool governorToken)
    {
        using CancellationTokenSource cancellation=new();
        ResourceGovernor governor=new(QpdfLimits.Default,governorToken ? cancellation.Token : CancellationToken.None);
        NumberedObject[] values=Enumerable.Range(1,8192).Select(number=>new NumberedObject(number,PdfNull.Instance)).ToArray();
        using WriterOrder<NumberedObject> source=Source(governor,values);
        long before=governor.Snapshot().LiveMemoryBytes,threshold=governor.Snapshot().TotalWork+65536L;
        using ManualResetEventSlim reached=new(),resume=new(); GateIds ids=new(reached,resume);
        Task worker=Task.Factory.StartNew(()=>
        {using QdfEmissionPlan plan=QdfEmissionPlan.Create(source,ids,governor,governorToken ? CancellationToken.None : cancellation.Token);},
            CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10)));Assert.True(governor.Snapshot().TotalWork>=threshold);
            cancellation.Cancel();resume.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(before,governor.Snapshot().LiveMemoryBytes);Assert.Equal(8192,source.Count);GC.KeepAlive(worker);
        }
        finally {cancellation.Cancel();resume.Set();try {await worker.WaitAsync(TimeSpan.FromSeconds(10));}catch (OperationCanceledException) {}}
    }

    private sealed class GateIds(ManualResetEventSlim reached,ManualResetEventSlim resume):IReadOnlyDictionary<int,int>
    {
        public bool TryGetValue(int key,out int value)
        {reached.Set();if (!resume.Wait(TimeSpan.FromSeconds(10))) {throw new TimeoutException("QDF row observation did not resume.");}value=0;return false;}
        public int Count=>0;public IEnumerable<int> Keys=>Array.Empty<int>();public IEnumerable<int> Values=>Array.Empty<int>();
        public int this[int key]=>throw new KeyNotFoundException();public bool ContainsKey(int key)=>false;
        public IEnumerator<KeyValuePair<int,int>> GetEnumerator()=>Enumerable.Empty<KeyValuePair<int,int>>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }

    [Fact]
    public void LargeImmutablePayloadFitsFiniteMetadataBudgetWithoutCopies()
    {
        PdfStream original=new(new PdfDictionary(new Dictionary<PdfName,QpdfObject>()),new byte[2*1024*1024]);
        QpdfLimits limits=QpdfLimits.Default with {MaxMemoryBytes=4096L};ResourceGovernor governor=new(limits,CancellationToken.None);
        using WriterOrder<NumberedObject> source=Source(governor,new NumberedObject(1,original));
        long before=GC.GetAllocatedBytesForCurrentThread();using QdfEmissionPlan plan=QdfEmissionPlan.Create(source,null,governor,CancellationToken.None);
        Assert.True(GC.GetAllocatedBytesForCurrentThread()-before<32768L);
        Assert.True(MemoryMarshal.TryGetArray(original.RawMemory,out ArraySegment<byte> originalBytes));
        Assert.True(MemoryMarshal.TryGetArray(Assert.IsType<PdfStream>(plan[0].Value).RawMemory,out ArraySegment<byte> borrowed));
        Assert.Same(originalBytes.Array,borrowed.Array);Assert.Equal(2L*1024*1024,plan.StreamLength(plan[1]));
        Assert.True(governor.Snapshot().PeakMemoryBytes<=4096L);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (QdfEmissionPlan Owner,WeakReference Rows,WeakReference Graph,WeakReference Governor) Disposed()
    {
        ResourceGovernor governor=new(QpdfLimits.Default,CancellationToken.None);
        using WriterOrder<NumberedObject> source=Source(governor,new NumberedObject(1,new PdfArray([new PdfReference(1,0)])));
        QdfEmissionPlan plan=QdfEmissionPlan.Create(source,null,governor,CancellationToken.None);
        object sequence=Field(plan,"_rows"),rows=Field(sequence,"_items");
        var result=(plan,new WeakReference(rows),new WeakReference(plan[0].Value),new WeakReference(governor));
        plan.Dispose();source.Dispose();Assert.Equal(0L,governor.Snapshot().LiveMemoryBytes);return result;
    }

    [Fact]
    public void RootedDisposedPlanDetachesRowsGraphAndCompletedGovernor()
    {
        var state=Disposed();Collected();Assert.False(state.Rows.IsAlive);Assert.False(state.Graph.IsAlive);Assert.False(state.Governor.IsAlive);
        Assert.Throws<ObjectDisposedException>(()=>state.Owner.Count);Assert.Throws<ObjectDisposedException>(()=>state.Owner.Renumber);
        state.Owner.Dispose();GC.KeepAlive(state);
    }

    internal static object Field(object owner,string name)=>owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(owner)
        ?? throw new InvalidOperationException("Expected QDF plan field.");
    internal static void Collected(){for(int round=0;round<3;round++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}}
}
