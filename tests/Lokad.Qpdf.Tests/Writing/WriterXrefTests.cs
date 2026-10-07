using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(WriterOrderCollection))]
public sealed class WriterXrefTests
{
    public static IEnumerable<object[]> Widths()
    { for (int first=0;first<=8;first++) { for (int second=0;second<=8;second++) { yield return [first,second]; } } }

    [Theory]
    [MemberData(nameof(Widths))]
    public void ExactFieldsPreserveFreeDirectAndCompressedSpelling(int first, int second)
    {
        ResourceGovernor governor = new(QpdfLimits.Default,CancellationToken.None);
        using WriterXref body = WriterXref.Create(3,first,second,QpdfLimits.Default,governor,CancellationToken.None);
        body.Append(0,0,0); body.Append(1,0x0102030405060708L,0); body.Append(2,-1,0x0807060504030201L);
        body.Finish(false,null,QpdfLimits.Default);
        byte[] ascending = [1,2,3,4,5,6,7,8], descending = [8,7,6,5,4,3,2,1];
        int width=1+first+second; byte[] expected = new byte[3*width];
        expected[width]=1; ascending.AsSpan(8-first).CopyTo(expected.AsSpan(width+1,first));
        expected[2*width]=2; expected.AsSpan(2*width+1,first).Fill(255);
        descending.AsSpan(8-second).CopyTo(expected.AsSpan(2*width+1+first,second));
        Assert.Equal(expected,body.Data.ToArray()); Assert.Equal(192L+expected.Length,governor.Snapshot().LiveMemoryBytes);
        body.Dispose(); Assert.Equal(0L,governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneByteShortAdmissionPreservesPriorOwnersAndRetry(bool empty)
    {
        QpdfLimits limits=QpdfLimits.Default with {MaxMemoryBytes=4096L};
        ResourceGovernor governor=new(limits,CancellationToken.None);
        using ParserMemoryScope prior=ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using ParserMemoryScope filler=ParserMemoryScope.Create(governor);
        long admission=empty ? 160L : 206L;
        filler.Reserve(limits.MaxMemoryBytes-governor.Snapshot().LiveMemoryBytes-admission+1L);
        long before=governor.Snapshot().LiveMemoryBytes,allocated=GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(()=>WriterXref.Create(empty ? 0 : 1,5,8,limits,governor,CancellationToken.None));
        Assert.True(GC.GetAllocatedBytesForCurrentThread()-allocated<32768L);
        Assert.Equal(before,governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L,prior.ReservedBytes);
        filler.Dispose();
        using (WriterXref retry=WriterXref.Create(empty ? 0 : 1,5,8,limits,governor,CancellationToken.None))
        { if (!empty) { retry.Append(1,7,0); } retry.Finish(false,null,limits); Assert.Equal(empty ? 0 : 14,retry.Data.Length); }
        Assert.Equal(161L,governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void ExhaustedWorkRejectsBeforeLargeBackingAllocation()
    {
        QpdfLimits limits=QpdfLimits.Default with {MaxWork=1L};
        ResourceGovernor governor=new(limits,CancellationToken.None);
        using ParserMemoryScope prior=ParserMemoryScope.Create(governor); prior.Reserve(97L); governor.ChargeWork(1L);
        long before=GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure=Assert.Throws<QpdfLimitException>(()=>WriterXref.Create(1<<20,8,8,limits,governor,CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxWork),failure.LimitName); Assert.True(GC.GetAllocatedBytesForCurrentThread()-before<32768L);
        Assert.Equal(161L,governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L,prior.ReservedBytes);
    }

    [Fact]
    public void OutputPreflightUsesWideArithmeticWithoutAllocatingOrChargingWork()
    {
        QpdfLimits limits=QpdfLimits.Default with {MaxOutputBytes=9L};
        ResourceGovernor governor=new(limits,CancellationToken.None);
        QpdfLimitException failure=Assert.Throws<QpdfLimitException>(()=>WriterXref.Create(int.MaxValue,8,8,limits,governor,CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxOutputBytes),failure.LimitName);
        Assert.Equal(0L,governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L,governor.Snapshot().TotalWork);
    }

    [Fact]
    public void IncompleteAndCompletedBodiesHaveExplicitGuards()
    {
        ResourceGovernor governor=new(QpdfLimits.Default,CancellationToken.None);
        using WriterXref body=WriterXref.Create(2,1,0,QpdfLimits.Default,governor,CancellationToken.None);
        Assert.Throws<InvalidOperationException>(()=>body.Data);
        body.Append(1,7,0); Assert.Throws<QpdfException>(()=>body.Finish(false,null,QpdfLimits.Default));
        body.Append(2,9,0); Assert.Throws<QpdfException>(()=>body.Append(0,0,0));
        body.Finish(false,null,QpdfLimits.Default); Assert.Equal(new byte[]{1,7,2,9},body.Data.ToArray());
        Assert.Throws<InvalidOperationException>(()=>body.Append(0,0,0)); Assert.Throws<InvalidOperationException>(()=>body.Finish(false,null,QpdfLimits.Default));
        body.Dispose(); Assert.Throws<ObjectDisposedException>(()=>body.Data); Assert.Throws<ObjectDisposedException>(()=>body.Append(0,0,0));
        Assert.Throws<ObjectDisposedException>(()=>body.Finish(false,null,QpdfLimits.Default)); Assert.Equal(0L,governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherCanceledTokenRejectsConstructionBeforeAdmission(bool governorToken)
    {
        using CancellationTokenSource cancellation=new(); cancellation.Cancel();
        ResourceGovernor governor=new(QpdfLimits.Default,governorToken ? cancellation.Token : CancellationToken.None);
        Assert.ThrowsAny<OperationCanceledException>(()=>WriterXref.Create(1,8,8,QpdfLimits.Default,governor,
            governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(0L,governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L,governor.Snapshot().TotalWork);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterActualEntriesPreservesPriorOwnership(bool governorToken)
    {
        using CancellationTokenSource cancellation=new();
        ResourceGovernor governor=new(QpdfLimits.Default,governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope prior=ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using WriterXref body=WriterXref.Create(1<<20,1,1,QpdfLimits.Default,governor,
            governorToken ? CancellationToken.None : cancellation.Token);
        long threshold=governor.Snapshot().TotalWork+65536L;
        using ManualResetEventSlim reached=new(),resume=new();
        Task worker=Task.Factory.StartNew(()=>
        {
            for (int entry=0;;entry++)
            {
                body.Append(1,entry,0);
                if (governor.Snapshot().TotalWork>=threshold)
                { reached.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Xref observation did not resume."); } }
            }
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10))); Assert.True(governor.Snapshot().TotalWork>=threshold);
            cancellation.Cancel(); resume.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(161L+192L+3L*(1<<20),governor.Snapshot().LiveMemoryBytes);
            body.Dispose(); Assert.Equal(161L,governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L,prior.ReservedBytes);
            GC.KeepAlive(worker);
        }
        finally { cancellation.Cancel(); resume.Set(); try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) {} }
    }

    [Fact]
    public void CodecAdmissionFailurePreservesRawBackingAndCanFinishWithoutCompression()
    {
        QpdfLimits limits=QpdfLimits.Default with {MaxMemoryBytes=4096L};
        ResourceGovernor governor=new(limits,CancellationToken.None);
        using ParserMemoryScope prior=ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using WriterXref body=WriterXref.Create(32,1,1,limits,governor,CancellationToken.None);
        for (int entry=0;entry<32;entry++) { body.Append(1,entry,0); }
        object raw=Field(body,"_raw"); long before=governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(()=>body.Finish(true,null,limits));
        Assert.Same(raw,Field(body,"_raw")); Assert.Equal(before,governor.Snapshot().LiveMemoryBytes);
        body.Finish(false,null,limits); Assert.Equal(96,body.Data.Length); body.Dispose();
        Assert.Equal(161L,governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(raw);
    }

    [Fact]
    public void CompressionRetiresRawBackingAndRoundTripsExactFields()
    {
        ResourceGovernor governor=new(QpdfLimits.Default,CancellationToken.None);
        using WriterXref body=WriterXref.Create(2048,4,2,QpdfLimits.Default,governor,CancellationToken.None);
        byte[] expected=new byte[2048*7];
        for (int entry=0;entry<2048;entry++)
        {
            body.Append(1,entry,0); int offset=entry*7; expected[offset]=1;
            expected[offset+3]=(byte)(entry>>8); expected[offset+4]=(byte)entry;
        }
        WeakReference raw=WeakField(body,"_raw"); body.Finish(true,null,QpdfLimits.Default);
        Assert.Null(typeof(WriterXref).GetField("_raw",BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(body));
        Assert.Equal(192L+body.Data.Length,governor.Snapshot().LiveMemoryBytes);
        Collected(); Assert.False(raw.IsAlive);
        using MemoryStream source=new(body.Data.ToArray()); using ZLibStream inflater=new(source,CompressionMode.Decompress);
        using MemoryStream output=new(); inflater.CopyTo(output); Assert.Equal(expected,output.ToArray());
        body.Dispose(); Assert.Equal(0L,governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(body); GC.KeepAlive(raw);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WriterXref Owner,WeakReference Bytes,WeakReference Governor) Disposed(bool compress)
    {
        ResourceGovernor governor=new(QpdfLimits.Default,CancellationToken.None);
        WriterXref body=WriterXref.Create(512,4,2,QpdfLimits.Default,governor,CancellationToken.None);
        for (int entry=0;entry<512;entry++) { body.Append(1,entry,0); }
        body.Finish(compress,null,QpdfLimits.Default);
        Assert.True(MemoryMarshal.TryGetArray(body.Data,out ArraySegment<byte> segment));
        WeakReference bytes=new(segment.Array ?? throw new InvalidOperationException("Expected xref backing.")), budget=new(governor);
        body.Dispose(); Assert.Equal(0L,governor.Snapshot().LiveMemoryBytes); return (body,bytes,budget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootedDisposedOwnersReleaseBackingAndCompletedGovernor(bool compress)
    {
        var state=Disposed(compress); Collected();
        Assert.False(state.Bytes.IsAlive); Assert.False(state.Governor.IsAlive); GC.KeepAlive(state);
    }

    private static void Collected() { for (int round=0;round<3;round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); } }
    private static object Field(object value,string field)=>value.GetType().GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(value)
        ?? throw new InvalidOperationException("Expected live xref field.");
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference WeakField(object value,string field)=>new(Field(value,field));
}
