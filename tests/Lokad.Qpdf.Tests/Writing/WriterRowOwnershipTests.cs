using System.Runtime.InteropServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[Collection(nameof(WriterOrderCollection))]
public sealed class WriterRowOwnershipTests
{
    public static IEnumerable<object[]> Outcomes()
    {
        foreach (string route in new[] {"ordinary-streamed","qdf","qdf-streamed"})
        {foreach (string outcome in new[] {"success","failure","cancellation"}) {yield return [route,outcome];}}
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task RetiredRowsReleaseStorageAcrossPublicationOutcomes(string route,string outcome)
    {
        List<NumberedObject> model=WriterIndexOwnershipTests.Model(513);QpdfObject borrowed=model[2].Value;
        ResourceGovernor governor=new(QpdfLimits.Default,CancellationToken.None);
        using ParserMemoryScope prior=ParserMemoryScope.Create(governor);prior.Reserve(97L);
        long baseline=governor.Snapshot().LiveMemoryBytes;
        using CancellationTokenSource cancellation=new();
        string pause=route.EndsWith("streamed",StringComparison.Ordinal) ? "streamed-retired" : "classic-retired";
        WriterIndexOwnershipTests.PausedSink sink=new(pause,outcome=="failure");
        Task writing=WriterIndexOwnershipTests.Start(route,model,QpdfLimits.Default,governor,sink,cancellation.Token);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(writing.IsCompleted);
            Assert.InRange(governor.Snapshot().LiveMemoryBytes,baseline,QpdfLimits.Default.MaxMemoryBytes);
            if (outcome=="cancellation") {cancellation.Cancel();}else {sink.Resume();}
            await WriterIndexOwnershipTests.Finish(writing,outcome);
            Assert.Equal(baseline,governor.Snapshot().LiveMemoryBytes);Assert.Equal(97L,prior.ReservedBytes);
            Assert.Equal(513,model.Count);Assert.Same(borrowed,model[2].Value);
            GC.KeepAlive(writing);GC.KeepAlive(sink);GC.KeepAlive(model);GC.KeepAlive(borrowed);
        }
        finally {await WriterIndexOwnershipTests.Drain(writing,sink);}
    }

    [Theory]
    [InlineData("ordinary-streamed")]
    [InlineData("qdf")]
    [InlineData("qdf-streamed")]
    public async Task RepeatedFiniteRowsReleaseTheirOwnAdmission(string route)
    {
        List<NumberedObject> model=WriterIndexOwnershipTests.Model(129);
        QpdfLimits limits=QpdfLimits.Default with {MaxMemoryBytes=32768L};ResourceGovernor governor=new(limits,CancellationToken.None);
        using ParserMemoryScope prior=ParserMemoryScope.Create(governor);prior.Reserve(97L);
        for(int cycle=0;cycle<32;cycle++)
        {
            CountingSink sink=new();await WriterIndexOwnershipTests.Start(route,model,limits,governor,sink,CancellationToken.None);
            Assert.True(sink.Length>0);Assert.Equal(161L,governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(97L,prior.ReservedBytes);Assert.Equal(129,model.Count);
        }
        Assert.True(governor.Snapshot().PeakMemoryBytes<=32768L);
    }

    [Fact]
    public async Task ClassicQdfBorrowsLargePayloadUnderFiniteMetadataCeiling()
    {
        List<NumberedObject> model=WriterIndexOwnershipTests.Model(4);
        PdfStream original=new(new PdfDictionary(new Dictionary<PdfName,QpdfObject>()),new byte[2*1024*1024]);
        model[3]=new NumberedObject(4,original);
        Assert.True(MemoryMarshal.TryGetArray(original.RawMemory,out ArraySegment<byte> segment));
        byte[] backing=segment.Array ?? throw new InvalidOperationException("Expected borrowed stream backing.");
        QpdfLimits limits=QpdfLimits.Default with {MaxMemoryBytes=32768L};ResourceGovernor governor=new(limits,CancellationToken.None);
        BorrowedSink sink=new(backing);
        long before=GC.GetAllocatedBytesForCurrentThread();
        await WriterIndexOwnershipTests.Start("qdf",model,limits,governor,sink,CancellationToken.None);
        Assert.True(GC.GetAllocatedBytesForCurrentThread()-before<131072L);
        Assert.Equal(backing.Length,sink.BorrowedBytes);Assert.Equal(0L,governor.Snapshot().LiveMemoryBytes);Assert.True(governor.Snapshot().PeakMemoryBytes<=32768L);
        Assert.Equal(2*1024*1024,original.RawMemory.Length);Assert.Same(original,model[3].Value);
        byte[] publicCopy=original.Data;Assert.NotSame(backing,publicCopy);publicCopy[0]=255;Assert.Equal(0,original.RawMemory.Span[0]);
    }

    private sealed class BorrowedSink(byte[] expected):IByteSink
    {
        internal int BorrowedBytes{get;private set;}public long Length{get;private set;}
        public ValueTask AppendAsync(ReadOnlyMemory<byte> data,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(MemoryMarshal.TryGetArray(data,out ArraySegment<byte> segment) && ReferenceEquals(expected,segment.Array))
            {Assert.Equal(BorrowedBytes,segment.Offset);BorrowedBytes+=data.Length;}
            Length+=data.Length;return ValueTask.CompletedTask;
        }
    }
}
